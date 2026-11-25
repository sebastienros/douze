using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Douze.DotNetApi;

/// <summary>
/// Reads the public API of an assembly from its metadata (<see cref="MetadataReader"/>): the visible types and their
/// visible members, with what C# declares that the XML documentation file does not have (kinds, modifiers, base types,
/// signatures, constraints, default values, constants). Nothing is loaded or run, so a reference assembly works as well
/// as an implementation, and native AOT is fine.
/// </summary>
internal sealed class AssemblyReader
{
    private const string CompilerServices = "System.Runtime.CompilerServices";

    private static readonly FrozenDictionary<string, string> Operators = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["op_Addition"] = "+",
        ["op_Subtraction"] = "-",
        ["op_Multiply"] = "*",
        ["op_Division"] = "/",
        ["op_Modulus"] = "%",
        ["op_BitwiseAnd"] = "&",
        ["op_BitwiseOr"] = "|",
        ["op_ExclusiveOr"] = "^",
        ["op_LeftShift"] = "<<",
        ["op_RightShift"] = ">>",
        ["op_UnsignedRightShift"] = ">>>",
        ["op_Equality"] = "==",
        ["op_Inequality"] = "!=",
        ["op_LessThan"] = "<",
        ["op_GreaterThan"] = ">",
        ["op_LessThanOrEqual"] = "<=",
        ["op_GreaterThanOrEqual"] = ">=",
        ["op_UnaryPlus"] = "+",
        ["op_UnaryNegation"] = "-",
        ["op_LogicalNot"] = "!",
        ["op_OnesComplement"] = "~",
        ["op_Increment"] = "++",
        ["op_Decrement"] = "--",
        ["op_True"] = "true",
        ["op_False"] = "false",
        ["op_CheckedAddition"] = "checked +",
        ["op_CheckedSubtraction"] = "checked -",
        ["op_CheckedMultiply"] = "checked *",
        ["op_CheckedDivision"] = "checked /",
        ["op_CheckedUnaryNegation"] = "checked -",
        ["op_CheckedIncrement"] = "checked ++",
        ["op_CheckedDecrement"] = "checked --",
        ["op_AdditionAssignment"] = "+=",
        ["op_SubtractionAssignment"] = "-=",
        ["op_MultiplicationAssignment"] = "*=",
        ["op_DivisionAssignment"] = "/=",
        ["op_ModulusAssignment"] = "%=",
        ["op_BitwiseAndAssignment"] = "&=",
        ["op_BitwiseOrAssignment"] = "|=",
        ["op_ExclusiveOrAssignment"] = "^=",
        ["op_LeftShiftAssignment"] = "<<=",
        ["op_RightShiftAssignment"] = ">>=",
        ["op_UnsignedRightShiftAssignment"] = ">>>=",
        ["op_IncrementAssignment"] = "++",
        ["op_DecrementAssignment"] = "--",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly MetadataReader _reader;
    private readonly SignatureProvider _provider;
    private readonly bool _includeProtected;
    private readonly ApiAssembly _assembly;

    // The nullable context of the type whose members are read (NullableContextAttribute): the nullability of the types
    // its members name without a NullableAttribute of their own.
    private byte _nullableContext;

    // The members of the enums of the assembly by value, to write an enum default value with its name.
    private Dictionary<string, Dictionary<string, string>>? _enumMembers;

    private AssemblyReader(MetadataReader reader, bool includeProtected, ApiAssembly assembly)
    {
        _reader = reader;
        _provider = new SignatureProvider(reader);
        _includeProtected = includeProtected;
        _assembly = assembly;
    }

    /// <summary>
    /// Reads the assembly in <paramref name="bytes"/>; <paramref name="path"/> names it in errors.
    /// </summary>
    /// <exception cref="DouzeException">The file is not a .NET assembly.</exception>
    public static ApiAssembly Read(byte[] bytes, string path, bool includeProtected)
    {
        try
        {
            using var pe = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(bytes));

            if (!pe.HasMetadata)
            {
                throw new DouzeException($"'{path}' is not a .NET assembly");
            }

            var reader = pe.GetMetadataReader();

            if (!reader.IsAssembly)
            {
                throw new DouzeException($"'{path}' is a .NET module, not an assembly");
            }

            var definition = reader.GetAssemblyDefinition();
            var provider = new SignatureProvider(reader);
            string? informational = null;
            string? framework = null;

            foreach (var handle in definition.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);

                switch (provider.DeclaringType(attribute.Constructor)?.MetadataName)
                {
                    case "System.Reflection.AssemblyInformationalVersionAttribute":
                        informational = FirstString(attribute, provider);
                        break;
                    case "System.Runtime.Versioning.TargetFrameworkAttribute":
                        framework = FirstString(attribute, provider);
                        break;
                }
            }

            var assembly = new ApiAssembly
            {
                Name = reader.GetString(definition.Name),
                Version = definition.Version.ToString(),
                InformationalVersion = informational,
                TargetFramework = framework,
                Path = path,
            };

            var assemblyReader = new AssemblyReader(reader, includeProtected, assembly);

            foreach (var handle in reader.TypeDefinitions)
            {
                if (reader.GetTypeDefinition(handle).GetDeclaringType().IsNil)
                {
                    assemblyReader.ReadType(handle, null);
                }
            }

            return assembly;
        }
        catch (BadImageFormatException e)
        {
            throw new DouzeException($"'{path}' is not a .NET assembly: {e.Message}");
        }
    }

    private void ReadType(TypeDefinitionHandle handle, ApiType? declaring)
    {
        var definition = _reader.GetTypeDefinition(handle);
        var metadataName = _reader.GetString(definition.Name);
        var attributes = definition.Attributes;

        if (metadataName == "<Module>" || metadataName.Contains('<') || Accessibility(attributes) is not { } accessibility)
        {
            return;
        }

        var custom = definition.GetCustomAttributes();

        if (Has(custom, CompilerServices, "CompilerGeneratedAttribute"))
        {
            return;
        }

        // Type parameters are numbered across the declaring types: a nested type has those of its declaring types first.
        var typeParameterNames = GenericNames(definition.GetGenericParameters());
        var inherited = definition.GetDeclaringType() is { IsNil: false } declaringHandle ? _reader.GetTypeDefinition(declaringHandle).GetGenericParameters().Count : 0;
        var context = new GenericContext(typeParameterNames, []);
        var reference = _provider.Definition(handle);
        var baseType = _provider.Decode(definition.BaseType, context);
        var baseNamed = baseType as NamedTypeRef;
        var methods = definition.GetMethods();

        var kind = (attributes & TypeAttributes.Interface) != 0 ? "interface"
            : baseNamed?.Is("System", "Enum") == true ? "enum"
            : baseNamed?.Is("System", "ValueType") == true && !reference.Is("System", "Enum") ? "struct"
            : baseNamed?.Is("System", "MulticastDelegate") == true ? "delegate"
            : "class";

        if (kind == "class" && methods.Any(x => _reader.GetString(_reader.GetMethodDefinition(x).Name) == "<Clone>$"))
        {
            kind = "record";
        }
        else if (kind == "struct" && methods.Any(x => IsRecordStructMember(_reader.GetMethodDefinition(x))))
        {
            kind = "record struct";
        }

        var modifiers = new List<string>();
        var isAbstract = (attributes & TypeAttributes.Abstract) != 0;
        var isSealed = (attributes & TypeAttributes.Sealed) != 0;

        if (kind is "class" or "record")
        {
            if (isAbstract && isSealed)
            {
                modifiers.Add("static");
            }
            else if (isAbstract)
            {
                modifiers.Add("abstract");
            }
            else if (isSealed)
            {
                modifiers.Add("sealed");
            }
        }
        else if (kind is "struct" or "record struct")
        {
            if (Has(custom, CompilerServices, "IsReadOnlyAttribute"))
            {
                modifiers.Add("readonly");
            }

            if (Has(custom, CompilerServices, "IsByRefLikeAttribute"))
            {
                modifiers.Add("ref");
            }
        }

        var ownTypeParameters = typeParameterNames.Length > inherited ? typeParameterNames[inherited..] : [];
        var name = NamedTypeRef.SplitArity(metadataName).Name + TypeParameterList(ownTypeParameters);

        var type = new ApiType
        {
            Id = reference.DefinitionId,
            Ref = reference,
            Namespace = declaring?.Namespace ?? _reader.GetString(definition.Namespace),
            Assembly = _assembly,
            Kind = kind,
            DeclaringType = declaring,
            Name = name,
            DisplayName = declaring == null ? name : declaring.DisplayName + "." + name,
            Accessibility = accessibility,
            Modifiers = modifiers,
            BaseType = kind is "class" or "record" && baseNamed?.Is("System", "Object") != true ? baseType : null,
            Obsolete = Obsolete(custom),
            IsSealed = isSealed,
        };

        var genericParameters = definition.GetGenericParameters();

        for (var i = inherited; i < genericParameters.Count; i++)
        {
            type.TypeParameters.Add(ReadGenericParameter(genericParameters[i], context));
        }

        foreach (var implementation in definition.GetInterfaceImplementations())
        {
            var interfaceHandle = _reader.GetInterfaceImplementation(implementation).Interface;

            // An interface of the assembly that is not visible is not part of the API.
            if (interfaceHandle.Kind == HandleKind.TypeDefinition && !IsVisible((TypeDefinitionHandle)interfaceHandle))
            {
                continue;
            }

            if (_provider.Decode(interfaceHandle, context) is { } decoded)
            {
                type.Interfaces.Add(decoded);
            }
        }

        if (kind == "enum")
        {
            var underlying = definition.GetFields().Select(_reader.GetFieldDefinition).FirstOrDefault(x => (x.Attributes & FieldAttributes.Static) == 0);
            var underlyingType = underlying.Name.IsNil ? null : underlying.DecodeSignature(_provider, context);

            if (underlyingType is NamedTypeRef named && !named.Is("System", "Int32"))
            {
                type.UnderlyingType = named;
            }
        }

        if (declaring == null)
        {
            _assembly.Types.Add(type);
        }
        else
        {
            declaring.NestedTypes.Add(type);
            _assembly.Types.Add(type);
        }

        ReadMembers(definition, type, context);
        type.Signature = TypeSignature(type);

        foreach (var nested in definition.GetNestedTypes())
        {
            ReadType(nested, type);
        }
    }

    private bool IsRecordStructMember(MethodDefinition method)
    {
        return _reader.GetString(method.Name) == "PrintMembers" && Has(method.GetCustomAttributes(), CompilerServices, "CompilerGeneratedAttribute");
    }

    private bool IsVisible(TypeDefinitionHandle handle)
    {
        var definition = _reader.GetTypeDefinition(handle);

        if (Accessibility(definition.Attributes) == null)
        {
            return false;
        }

        return definition.GetDeclaringType() is not { IsNil: false } declaring || IsVisible(declaring);
    }

    private string? Accessibility(TypeAttributes attributes)
    {
        return (attributes & TypeAttributes.VisibilityMask) switch
        {
            TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
            TypeAttributes.NestedFamily when _includeProtected => "protected",
            TypeAttributes.NestedFamORAssem when _includeProtected => "protected internal",
            _ => null,
        };
    }

    private string? Accessibility(MethodAttributes attributes, bool sealedType)
    {
        return (attributes & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Public => "public",
            // A protected member of a sealed type or of a struct cannot be reached from outside.
            MethodAttributes.Family when _includeProtected && !sealedType => "protected",
            MethodAttributes.FamORAssem when _includeProtected && !sealedType => "protected internal",
            _ => null,
        };
    }

    private static int Rank(string? accessibility) => accessibility switch
    {
        "public" => 3,
        "protected internal" => 2,
        "protected" => 1,
        _ => 0,
    };

    private ImmutableArray<string> GenericNames(GenericParameterHandleCollection parameters)
    {
        if (parameters.Count == 0)
        {
            return [];
        }

        var names = ImmutableArray.CreateBuilder<string>(parameters.Count);

        foreach (var handle in parameters)
        {
            names.Add(_reader.GetString(_reader.GetGenericParameter(handle).Name));
        }

        return names.MoveToImmutable();
    }

    private ApiGenericParameter ReadGenericParameter(GenericParameterHandle handle, GenericContext context)
    {
        var parameter = _reader.GetGenericParameter(handle);
        var attributes = parameter.Attributes;
        var constraints = new List<string>();
        var unmanaged = Has(parameter.GetCustomAttributes(), CompilerServices, "IsUnmanagedAttribute");
        var isStruct = (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;

        if (unmanaged)
        {
            constraints.Add("unmanaged");
        }
        else if (isStruct)
        {
            constraints.Add("struct");
        }
        else if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
        {
            constraints.Add("class");
        }

        foreach (var constraintHandle in parameter.GetConstraints())
        {
            var constraint = _reader.GetGenericParameterConstraint(constraintHandle);

            // struct is a constraint to ValueType, unmanaged one to a modified ValueType.
            if (_provider.Decode(constraint.Type, context) is not { } type || (isStruct || unmanaged) && Unwrap(type).Type is NamedTypeRef named && named.Is("System", "ValueType"))
            {
                continue;
            }

            constraints.Add(CSharpWriter.Plain(type));
        }

        if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !isStruct)
        {
            constraints.Add("new()");
        }

        if (((int)attributes & 0x20) != 0)
        {
            // GenericParameterAttributes.AllowByRefLike.
            constraints.Add("allows ref struct");
        }

        var variance = (attributes & GenericParameterAttributes.VarianceMask) switch
        {
            GenericParameterAttributes.Covariant => "out",
            GenericParameterAttributes.Contravariant => "in",
            _ => null,
        };

        return new ApiGenericParameter(_reader.GetString(parameter.Name), variance, constraints);
    }

    private void ReadMembers(TypeDefinition definition, ApiType type, GenericContext context)
    {
        // The interface member each explicit implementation implements.
        var implementations = new Dictionary<MethodDefinitionHandle, EntityHandle>();

        foreach (var handle in definition.GetMethodImplementations())
        {
            var implementation = _reader.GetMethodImplementation(handle);

            if (implementation.MethodBody.Kind == HandleKind.MethodDefinition)
            {
                implementations.TryAdd((MethodDefinitionHandle)implementation.MethodBody, implementation.MethodDeclaration);
            }
        }

        _nullableContext = TypeNullableContext(definition);
        var accessors = new HashSet<MethodDefinitionHandle>();
        var isInterface = type.Kind == "interface";
        var sealedType = type.IsSealed && !isInterface;

        foreach (var handle in definition.GetProperties())
        {
            var property = _reader.GetPropertyDefinition(handle);
            var methods = property.GetAccessors();
            AddAccessors(accessors, methods.Getter, methods.Setter, methods.Others);

            if (ReadProperty(property, methods, type, context, implementations, sealedType, isInterface) is { } member)
            {
                type.Members.Add(member);
            }
        }

        foreach (var handle in definition.GetEvents())
        {
            var @event = _reader.GetEventDefinition(handle);
            var methods = @event.GetAccessors();
            AddAccessors(accessors, methods.Adder, methods.Remover, methods.Others);
            AddAccessors(accessors, methods.Raiser, default, []);

            if (ReadEvent(@event, methods, type, context, implementations, sealedType, isInterface) is { } member)
            {
                type.Members.Add(member);
            }
        }

        foreach (var handle in definition.GetMethods())
        {
            if (accessors.Contains(handle))
            {
                continue;
            }

            var method = _reader.GetMethodDefinition(handle);

            if (type.Kind == "delegate")
            {
                if (_reader.GetString(method.Name) == "Invoke")
                {
                    var signature = method.DecodeSignature(_provider, context);
                    type.ReturnType = ReturnType(method, signature.ReturnType).Type;
                    type.Parameters.AddRange(ReadParameters(method, signature.ParameterTypes, isExtension: false));
                }

                continue;
            }

            if (ReadMethod(handle, method, type, context, implementations, sealedType, isInterface) is { } member)
            {
                type.Members.Add(member);
            }
        }

        foreach (var handle in definition.GetFields())
        {
            if (ReadField(_reader.GetFieldDefinition(handle), type, context, sealedType) is { } member)
            {
                type.Members.Add(member);
            }
        }
    }

    private static void AddAccessors(HashSet<MethodDefinitionHandle> accessors, MethodDefinitionHandle first, MethodDefinitionHandle second, ImmutableArray<MethodDefinitionHandle> others)
    {
        if (!first.IsNil)
        {
            accessors.Add(first);
        }

        if (!second.IsNil)
        {
            accessors.Add(second);
        }

        foreach (var other in others)
        {
            accessors.Add(other);
        }
    }

    /// <summary>
    /// The interface and the documentation comment ID of the member an explicit implementation implements.
    /// </summary>
    private (NamedTypeRef Interface, string Name, string Id)? Implemented(MethodDefinitionHandle handle, Dictionary<MethodDefinitionHandle, EntityHandle> implementations, GenericContext typeContext)
    {
        if (!implementations.TryGetValue(handle, out var declaration) || _provider.DeclaringType(declaration, typeContext) is not { } owner)
        {
            return null;
        }

        string name;
        MethodSignature<ApiTypeRef> signature;
        var context = new GenericContext([], []);

        if (declaration.Kind == HandleKind.MemberReference)
        {
            var reference = _reader.GetMemberReference((MemberReferenceHandle)declaration);
            name = _reader.GetString(reference.Name);
            signature = reference.DecodeMethodSignature(_provider, context);
        }
        else if (declaration.Kind == HandleKind.MethodDefinition)
        {
            var method = _reader.GetMethodDefinition((MethodDefinitionHandle)declaration);
            name = _reader.GetString(method.Name);
            signature = method.DecodeSignature(_provider, context);
        }
        else
        {
            return null;
        }

        var id = new StringBuilder("M:").Append(owner.Definition.MetadataName).Append('.').Append(EncodeName(name));

        if (signature.GenericParameterCount > 0)
        {
            id.Append("``").Append(signature.GenericParameterCount);
        }

        AppendParameters(id, signature.ParameterTypes);
        return (owner, name, id.ToString());
    }

    private ApiMember? ReadMethod(MethodDefinitionHandle handle, MethodDefinition method, ApiType type, GenericContext typeContext, Dictionary<MethodDefinitionHandle, EntityHandle> implementations, bool sealedType, bool isInterface)
    {
        var metadataName = _reader.GetString(method.Name);
        var attributes = method.Attributes;
        var accessibility = Accessibility(attributes, sealedType);
        var implemented = accessibility == null ? Implemented(handle, implementations, typeContext) : null;
        var custom = method.GetCustomAttributes();

        if (accessibility == null && implemented == null || metadataName == ".cctor")
        {
            return null;
        }

        if (implemented == null && metadataName.Contains('<') || Has(custom, CompilerServices, "CompilerGeneratedAttribute") && accessibility != "public")
        {
            return null;
        }

        var special = (attributes & MethodAttributes.SpecialName) != 0;
        // The name of the member an explicit implementation implements: op_Addition for an operator.
        var operatorName = implemented?.Name ?? metadataName;
        var isOperator = (special || implemented != null) && operatorName.StartsWith("op_", StringComparison.Ordinal);
        var kind = metadataName == ".ctor" ? "constructor" : isOperator ? "operator" : "method";

        // Other special methods are accessors of members C# does not declare in metadata (extension properties).
        if (special && kind == "method")
        {
            return null;
        }

        var methodParameters = GenericNames(method.GetGenericParameters());
        var context = typeContext with { MethodParameters = methodParameters };
        var signature = method.DecodeSignature(_provider, context);
        var isExtension = Has(custom, CompilerServices, "ExtensionAttribute");
        var parameters = ReadParameters(method, signature.ParameterTypes, isExtension);
        var (returnType, returnModifier) = ReturnType(method, signature.ReturnType);
        var typeParameters = method.GetGenericParameters().Select(x => ReadGenericParameter(x, context)).ToList();
        var modifiers = MethodModifiers(attributes, isInterface, implemented != null);
        var isOverride = (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0;

        var typeName = NamedTypeRef.SplitArity(type.Ref.Names[^1]).Name;
        var name = kind switch
        {
            "constructor" => typeName,
            "operator" when IsConversion(operatorName) => (operatorName == "op_Implicit" ? "implicit operator " : operatorName == "op_Explicit" ? "explicit operator " : "explicit operator checked ") + (returnType == null ? "void" : CSharpWriter.Plain(returnType)),
            "operator" => "operator " + (Operators.TryGetValue(operatorName, out var symbol) ? symbol : operatorName),
            _ => implemented?.Name ?? metadataName,
        };

        if (implemented is { } explicitImpl)
        {
            name = CSharpWriter.Plain(explicitImpl.Interface) + "." + name;
        }

        var id = new StringBuilder("M:").Append(type.Ref.MetadataName).Append('.').Append(EncodeName(metadataName));

        if (methodParameters.Length > 0)
        {
            id.Append("``").Append(methodParameters.Length);
        }

        var parametersId = ParametersId(signature.ParameterTypes);
        id.Append(parametersId);

        if (IsConversion(metadataName))
        {
            id.Append('~');
            signature.ReturnType.AppendDocId(id);
        }

        var display = name + TypeParameterList(methodParameters) + "(" + string.Join(", ", parameters.Select(ParameterTypeText)) + ")";

        var declaration = new StringBuilder();
        AppendPrefix(declaration, accessibility, modifiers);

        if (kind == "constructor")
        {
            declaration.Append(typeName);
        }
        else if (IsConversion(operatorName))
        {
            // implicit operator long(...), explicit operator checked long(...), or explicit IConvertible<int>.operator
            // long(...) for an explicit implementation.
            var conversion = name[(name.LastIndexOf("operator ", StringComparison.Ordinal) + 9)..];
            declaration.Append(operatorName == "op_Implicit" ? "implicit " : "explicit ");

            if (implemented is { } impl)
            {
                declaration.Append(CSharpWriter.Plain(impl.Interface)).Append('.');
            }

            declaration.Append("operator ").Append(conversion);
        }
        else
        {
            if (returnModifier != null)
            {
                declaration.Append(returnModifier).Append(' ');
            }

            declaration.Append(returnType == null ? "void" : CSharpWriter.Plain(returnType)).Append(' ').Append(name).Append(TypeParameterList(methodParameters, typeParameters));
        }

        AppendParameterList(declaration, parameters, '(', ')');
        AppendConstraints(declaration, typeParameters);

        return new ApiMember
        {
            Id = id.ToString(),
            Kind = kind,
            MetadataName = metadataName,
            Name = name,
            DisplayName = display,
            DeclaringType = type,
            Accessibility = accessibility ?? "",
            Modifiers = modifiers,
            Signature = declaration.ToString(),
            TypeParameters = typeParameters,
            Parameters = parameters,
            Type = returnType,
            ReturnModifier = returnModifier,
            Obsolete = Obsolete(custom),
            IsExtension = isExtension,
            ExplicitInterface = implemented?.Interface,
            ImplementedId = implemented?.Id,
            IsOverride = isOverride,
            ParametersId = parametersId,
            InheritedName = EncodeName(implemented?.Name ?? metadataName) + (methodParameters.Length > 0 ? "``" + methodParameters.Length : ""),
        };
    }

    private static bool IsConversion(string name) => name is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit";

    private ApiMember? ReadProperty(PropertyDefinition property, PropertyAccessors methods, ApiType type, GenericContext context, Dictionary<MethodDefinitionHandle, EntityHandle> implementations, bool sealedType, bool isInterface)
    {
        var getter = methods.Getter.IsNil ? (MethodDefinition?)null : _reader.GetMethodDefinition(methods.Getter);
        var setter = methods.Setter.IsNil ? (MethodDefinition?)null : _reader.GetMethodDefinition(methods.Setter);
        var getterAccess = getter is { } g ? Accessibility(g.Attributes, sealedType) : null;
        var setterAccess = setter is { } s ? Accessibility(s.Attributes, sealedType) : null;
        var accessibility = Rank(getterAccess) >= Rank(setterAccess) ? getterAccess : setterAccess;
        var main = getter ?? setter;
        var mainHandle = getter != null ? methods.Getter : methods.Setter;

        if (main is not { } accessor)
        {
            return null;
        }

        var implemented = accessibility == null ? Implemented(mainHandle, implementations, context) : null;

        if (accessibility == null && implemented == null)
        {
            return null;
        }

        var custom = property.GetCustomAttributes();
        var metadataName = _reader.GetString(property.Name);

        // The equality contract of a record is the compiler's.
        if (metadataName == "EqualityContract" && type.Kind is "record" or "record struct")
        {
            return null;
        }
        var signature = property.DecodeSignature(_provider, context);
        var propertyType = Unwrap(Annotate(signature.ReturnType, NullableFlags(custom), MethodNullableContext(accessor)));
        var parameters = getter is { } get
            ? ReadParameters(get, signature.ParameterTypes, isExtension: false)
            : ReadParameters(setter!.Value, signature.ParameterTypes, isExtension: false);
        var modifiers = MethodModifiers(accessor.Attributes, isInterface, implemented != null);

        if (Has(custom, CompilerServices, "RequiredMemberAttribute"))
        {
            modifiers.Insert(0, "required");
        }

        // The interface property an explicit implementation implements: its accessor's name without get_ or set_.
        var explicitName = implemented is { } impl && impl.Name.Length > 4 ? impl.Name[4..] : null;
        var indexer = parameters.Count > 0;
        var name = implemented is { } explicitImpl
            ? CSharpWriter.Plain(explicitImpl.Interface) + "." + (indexer ? "this[]" : explicitName)
            : indexer ? "this[]" : metadataName;

        var parametersId = ParametersId(signature.ParameterTypes);
        var display = indexer ? name[..^1] + string.Join(", ", parameters.Select(ParameterTypeText)) + "]" : name;

        var declaration = new StringBuilder();
        AppendPrefix(declaration, accessibility, modifiers);

        if (propertyType.Modifier != null)
        {
            declaration.Append(propertyType.Modifier).Append(' ');
        }

        declaration.Append(CSharpWriter.Plain(propertyType.Type)).Append(' ').Append(indexer ? name[..^2] : name);

        if (indexer)
        {
            AppendParameterList(declaration, parameters, '[', ']');
        }

        declaration.Append(" { ");

        if (getter != null && (getterAccess != null || implemented != null))
        {
            AppendAccessor(declaration, getterAccess, accessibility, "get");
        }

        if (setter is { } set && (setterAccess != null || implemented != null))
        {
            var init = set.DecodeSignature(_provider, context).ReturnType is ModifiedTypeRef modified && modified.HasModifier("IsExternalInit");
            AppendAccessor(declaration, setterAccess, accessibility, init ? "init" : "set");
        }

        declaration.Append('}');

        return new ApiMember
        {
            Id = "P:" + type.Ref.MetadataName + "." + EncodeName(metadataName) + parametersId,
            Kind = "property",
            MetadataName = metadataName,
            Name = name,
            DisplayName = display,
            DeclaringType = type,
            Accessibility = accessibility ?? "",
            Modifiers = modifiers,
            Signature = declaration.ToString(),
            Parameters = parameters,
            Type = propertyType.Type,
            ReturnModifier = propertyType.Modifier,
            Obsolete = Obsolete(custom),
            ExplicitInterface = implemented?.Interface,
            ImplementedId = implemented is { } i && explicitName != null ? "P:" + i.Interface.Definition.MetadataName + "." + EncodeName(explicitName) + parametersId : null,
            IsOverride = (accessor.Attributes & MethodAttributes.Virtual) != 0 && (accessor.Attributes & MethodAttributes.NewSlot) == 0,
            ParametersId = parametersId,
            InheritedName = EncodeName(explicitName ?? metadataName),
        };
    }

    private static void AppendAccessor(StringBuilder builder, string? access, string? propertyAccess, string keyword)
    {
        if (access != null && access != propertyAccess)
        {
            builder.Append(access).Append(' ');
        }

        builder.Append(keyword).Append("; ");
    }

    private ApiMember? ReadEvent(EventDefinition @event, EventAccessors methods, ApiType type, GenericContext context, Dictionary<MethodDefinitionHandle, EntityHandle> implementations, bool sealedType, bool isInterface)
    {
        if (methods.Adder.IsNil)
        {
            return null;
        }

        var adder = _reader.GetMethodDefinition(methods.Adder);
        var accessibility = Accessibility(adder.Attributes, sealedType);
        var implemented = accessibility == null ? Implemented(methods.Adder, implementations, context) : null;

        if (accessibility == null && implemented == null)
        {
            return null;
        }

        var metadataName = _reader.GetString(@event.Name);
        var eventType = Annotate(_provider.Decode(@event.Type, context) ?? NamedTypeRef.Create("System", "Delegate"), NullableFlags(@event.GetCustomAttributes()), _nullableContext);
        var modifiers = MethodModifiers(adder.Attributes, isInterface, implemented != null);
        var explicitName = implemented is { } impl && impl.Name.StartsWith("add_", StringComparison.Ordinal) ? impl.Name[4..] : null;
        var name = implemented is { } explicitImpl ? CSharpWriter.Plain(explicitImpl.Interface) + "." + explicitName : metadataName;

        var declaration = new StringBuilder();
        AppendPrefix(declaration, accessibility, modifiers);
        declaration.Append("event ").Append(CSharpWriter.Plain(eventType)).Append(' ').Append(name);

        return new ApiMember
        {
            Id = "E:" + type.Ref.MetadataName + "." + EncodeName(metadataName),
            Kind = "event",
            MetadataName = metadataName,
            Name = name,
            DisplayName = name,
            DeclaringType = type,
            Accessibility = accessibility ?? "",
            Modifiers = modifiers,
            Signature = declaration.ToString(),
            Type = eventType,
            Obsolete = Obsolete(@event.GetCustomAttributes()),
            ExplicitInterface = implemented?.Interface,
            ImplementedId = implemented is { } i && explicitName != null ? "E:" + i.Interface.Definition.MetadataName + "." + EncodeName(explicitName) : null,
            IsOverride = (adder.Attributes & MethodAttributes.Virtual) != 0 && (adder.Attributes & MethodAttributes.NewSlot) == 0,
            ParametersId = "",
            InheritedName = EncodeName(explicitName ?? metadataName),
        };
    }

    private ApiMember? ReadField(FieldDefinition field, ApiType type, GenericContext context, bool sealedType)
    {
        var attributes = field.Attributes;

        if ((attributes & FieldAttributes.SpecialName) != 0)
        {
            return null;
        }

        var accessibility = (attributes & FieldAttributes.FieldAccessMask) switch
        {
            FieldAttributes.Public => "public",
            FieldAttributes.Family when _includeProtected && !sealedType => "protected",
            FieldAttributes.FamORAssem when _includeProtected && !sealedType => "protected internal",
            _ => null,
        };

        var metadataName = _reader.GetString(field.Name);
        var custom = field.GetCustomAttributes();

        if (accessibility == null || metadataName.Contains('<') || Has(custom, CompilerServices, "CompilerGeneratedAttribute"))
        {
            return null;
        }

        var fieldType = Unwrap(Annotate(field.DecodeSignature(_provider, context), NullableFlags(custom), _nullableContext));
        var literal = (attributes & FieldAttributes.Literal) != 0;
        // The constant of an enum member is written as a number, not as a value of the enum.
        var value = literal ? Constant(field.GetDefaultValue(), type.Kind == "enum" ? NamedTypeRef.Create("System", "Int32") : fieldType.Type) : null;
        var modifiers = new List<string>();
        var declaration = new StringBuilder();

        if (type.Kind == "enum")
        {
            declaration.Append(metadataName);

            if (value != null)
            {
                declaration.Append(" = ").Append(value);
            }
        }
        else
        {
            if (literal)
            {
                modifiers.Add("const");
            }
            else
            {
                if ((attributes & FieldAttributes.Static) != 0)
                {
                    modifiers.Add("static");
                }

                if ((attributes & FieldAttributes.InitOnly) != 0)
                {
                    modifiers.Add("readonly");
                }
            }

            if (Has(custom, CompilerServices, "RequiredMemberAttribute"))
            {
                modifiers.Insert(0, "required");
            }

            AppendPrefix(declaration, accessibility, modifiers);

            if (fieldType.Modifier != null)
            {
                declaration.Append(fieldType.Modifier).Append(' ');
            }

            declaration.Append(CSharpWriter.Plain(fieldType.Type)).Append(' ').Append(metadataName);

            if (value != null)
            {
                declaration.Append(" = ").Append(value);
            }
        }

        return new ApiMember
        {
            Id = "F:" + type.Ref.MetadataName + "." + EncodeName(metadataName),
            Kind = "field",
            MetadataName = metadataName,
            Name = metadataName,
            DisplayName = metadataName,
            DeclaringType = type,
            Accessibility = accessibility,
            Modifiers = modifiers,
            Signature = declaration.ToString(),
            Type = fieldType.Type,
            ReturnModifier = fieldType.Modifier,
            Value = value,
            Obsolete = Obsolete(custom),
            ParametersId = "",
            InheritedName = EncodeName(metadataName),
        };
    }

    private List<string> MethodModifiers(MethodAttributes attributes, bool isInterface, bool isExplicit)
    {
        var modifiers = new List<string>();

        // An explicit implementation has no modifier, but static for a static member of an interface.
        if (isExplicit)
        {
            if ((attributes & MethodAttributes.Static) != 0)
            {
                modifiers.Add("static");
            }

            return modifiers;
        }

        var isStatic = (attributes & MethodAttributes.Static) != 0;
        var isAbstract = (attributes & MethodAttributes.Abstract) != 0;
        var isVirtual = (attributes & MethodAttributes.Virtual) != 0;
        var isFinal = (attributes & MethodAttributes.Final) != 0;
        var newSlot = (attributes & MethodAttributes.NewSlot) != 0;

        if (isStatic)
        {
            modifiers.Add("static");
        }

        if (isInterface)
        {
            // Instance members of an interface are abstract or virtual without saying so.
            if (isStatic && isAbstract)
            {
                modifiers.Add("abstract");
            }
            else if (isStatic && isVirtual)
            {
                modifiers.Add("virtual");
            }

            return modifiers;
        }

        if (isAbstract)
        {
            modifiers.Add(newSlot ? "abstract" : "abstract override");
        }
        else if (isVirtual && !newSlot)
        {
            modifiers.Add(isFinal ? "sealed override" : "override");
        }
        else if (isVirtual && !isFinal)
        {
            modifiers.Add("virtual");
        }

        return modifiers;
    }

    private List<ApiParameter> ReadParameters(MethodDefinition method, ImmutableArray<ApiTypeRef> types, bool isExtension)
    {
        var handles = new ParameterHandle[types.Length];

        foreach (var handle in method.GetParameters())
        {
            var sequence = _reader.GetParameter(handle).SequenceNumber;

            if (sequence > 0 && sequence <= types.Length)
            {
                handles[sequence - 1] = handle;
            }
        }

        var parameters = new List<ApiParameter>(types.Length);
        var nullableContext = MethodNullableContext(method);

        for (var i = 0; i < types.Length; i++)
        {
            var flags = handles[i].IsNil ? null : NullableFlags(_reader.GetParameter(handles[i]).GetCustomAttributes());
            var (type, byRef, modifiers) = UnwrapAll(Annotate(types[i], flags, nullableContext));
            string name;
            string? modifier = null;
            string? defaultValue = null;

            if (handles[i].IsNil)
            {
                name = $"arg{i}";
            }
            else
            {
                var parameter = _reader.GetParameter(handles[i]);
                var custom = parameter.GetCustomAttributes();
                name = _reader.GetString(parameter.Name);

                if (byRef)
                {
                    modifier = (parameter.Attributes & (ParameterAttributes.Out | ParameterAttributes.In)) == ParameterAttributes.Out ? "out"
                        : modifiers.Any(x => x.HasModifier("InAttribute")) || modifiers.Count == 0 && Has(custom, CompilerServices, "IsReadOnlyAttribute") && !Has(custom, CompilerServices, "RequiresLocationAttribute") ? "in"
                        : Has(custom, CompilerServices, "RequiresLocationAttribute") ? "ref readonly"
                        : "ref";
                }
                else if (Has(custom, "System", "ParamArrayAttribute") || Has(custom, CompilerServices, "ParamCollectionAttribute"))
                {
                    modifier = "params";
                }

                if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0)
                {
                    defaultValue = Constant(parameter.GetDefaultValue(), type) ?? "default";
                }
                else if ((parameter.Attributes & ParameterAttributes.Optional) != 0)
                {
                    defaultValue = "default";
                }
            }

            if (i == 0 && isExtension)
            {
                modifier = modifier == null ? "this" : "this " + modifier;
            }

            parameters.Add(new ApiParameter(name, type, modifier, defaultValue));
        }

        return parameters;
    }

    private (ApiTypeRef? Type, string? Modifier) ReturnType(MethodDefinition method, ApiTypeRef returnType)
    {
        if (IsVoid(returnType))
        {
            return (null, null);
        }

        var readOnly = false;
        byte[]? flags = null;

        foreach (var handle in method.GetParameters())
        {
            var parameter = _reader.GetParameter(handle);

            if (parameter.SequenceNumber == 0)
            {
                readOnly = Has(parameter.GetCustomAttributes(), CompilerServices, "IsReadOnlyAttribute");
                flags = NullableFlags(parameter.GetCustomAttributes());
            }
        }

        var (type, byRef, modifiers) = UnwrapAll(Annotate(returnType, flags, MethodNullableContext(method)));

        if (!byRef)
        {
            return (type, null);
        }

        readOnly |= modifiers.Any(x => x.HasModifier("InAttribute"));

        return (type, readOnly ? "ref readonly" : "ref");
    }

    /// <summary>
    /// The nullable context of a type: its NullableContextAttribute, or its declaring type's.
    /// </summary>
    private byte TypeNullableContext(TypeDefinition definition)
    {
        while (true)
        {
            if (NullableContext(definition.GetCustomAttributes()) is { } context)
            {
                return context;
            }

            if (definition.GetDeclaringType() is not { IsNil: false } declaring)
            {
                return 0;
            }

            definition = _reader.GetTypeDefinition(declaring);
        }
    }

    private byte MethodNullableContext(MethodDefinition method) => NullableContext(method.GetCustomAttributes()) ?? _nullableContext;

    private byte? NullableContext(CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = _reader.GetCustomAttribute(handle);

            if (_provider.DeclaringType(attribute.Constructor) is { } type && type.Is(CompilerServices, "NullableContextAttribute"))
            {
                return DecodeFirst(attribute) is byte value ? value : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The flags of a NullableAttribute: one for every type of the signature, or one for all of them.
    /// </summary>
    private byte[]? NullableFlags(CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = _reader.GetCustomAttribute(handle);

            if (_provider.DeclaringType(attribute.Constructor) is { } type && type.Is(CompilerServices, "NullableAttribute"))
            {
                return DecodeFirst(attribute) switch
                {
                    byte value => [value],
                    ImmutableArray<CustomAttributeTypedArgument<ApiTypeRef>> values => [.. values.Select(x => x.Value is byte b ? b : (byte)0)],
                    _ => null,
                };
            }
        }

        return null;
    }

    private object? DecodeFirst(CustomAttribute attribute)
    {
        try
        {
            var value = attribute.DecodeValue(_provider);
            return value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value : null;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks the nullable reference types and type parameters of a type (<see cref="NullableTypeRef"/>) from the flags
    /// of the compiler: one byte per type in a pre-order walk, 2 for nullable, where a value type without type
    /// arguments has none, a generic value type a 0 before its arguments, and <c>Nullable&lt;T&gt;</c> only those of
    /// its argument.
    /// </summary>
    internal static ApiTypeRef Annotate(ApiTypeRef type, byte[]? flags, byte context)
    {
        if (flags == null && context != 2)
        {
            return type;
        }

        var index = 0;

        byte Next()
        {
            if (flags == null)
            {
                return context;
            }

            if (flags.Length == 1)
            {
                return flags[0];
            }

            return index < flags.Length ? flags[index++] : (byte)0;
        }

        ApiTypeRef Walk(ApiTypeRef current)
        {
            switch (current)
            {
                case ModifiedTypeRef modified:
                    return modified with { Unmodified = Walk(modified.Unmodified) };

                case ByRefTypeRef reference:
                    return reference with { Element = Walk(reference.Element) };

                case ArrayTypeRef array:
                    {
                        var flag = Next();
                        var annotated = array with { Element = Walk(array.Element) };
                        return flag == 2 ? new NullableTypeRef(annotated) : annotated;
                    }

                case GenericParameterRef parameter:
                    return Next() == 2 ? new NullableTypeRef(parameter) : parameter;

                case NamedTypeRef named when named.IsValueType:
                    {
                        if (named.TypeArguments.IsEmpty)
                        {
                            return named;
                        }

                        if (!named.Is("System", "Nullable`1"))
                        {
                            Next();
                        }

                        return named with { TypeArguments = [.. named.TypeArguments.Select(Walk)] };
                    }

                case NamedTypeRef named:
                    {
                        var flag = Next();
                        var annotated = named.TypeArguments.IsEmpty ? named : named with { TypeArguments = [.. named.TypeArguments.Select(Walk)] };
                        return flag == 2 ? new NullableTypeRef(annotated) : annotated;
                    }

                default:
                    return current;
            }
        }

        return Walk(type);
    }

    private static bool IsVoid(ApiTypeRef type) => Unwrap(type).Type is NamedTypeRef named && named.Is("System", "Void");

    /// <summary>
    /// A type without its custom modifiers and its reference: <c>ref</c> or <c>ref readonly</c> for a field or a
    /// property that is a reference.
    /// </summary>
    private static (ApiTypeRef Type, string? Modifier) Unwrap(ApiTypeRef type)
    {
        var (unwrapped, byRef, modifiers) = UnwrapAll(type);
        return (unwrapped, byRef ? modifiers.Any(x => x.HasModifier("InAttribute")) ? "ref readonly" : "ref" : null);
    }

    private static (ApiTypeRef Type, bool ByRef, List<ModifiedTypeRef> Modifiers) UnwrapAll(ApiTypeRef type)
    {
        var modifiers = new List<ModifiedTypeRef>();
        var byRef = false;

        while (true)
        {
            switch (type)
            {
                case ModifiedTypeRef modified:
                    modifiers.Add(modified);
                    type = modified.Unmodified;
                    continue;
                case ByRefTypeRef reference when !byRef:
                    byRef = true;
                    type = reference.Element;
                    continue;
            }

            return (type, byRef, modifiers);
        }
    }

    private string ParametersId(ImmutableArray<ApiTypeRef> types)
    {
        var builder = new StringBuilder();
        AppendParameters(builder, types);
        return builder.ToString();
    }

    private static void AppendParameters(StringBuilder builder, ImmutableArray<ApiTypeRef> types)
    {
        if (types.IsEmpty)
        {
            return;
        }

        builder.Append('(');

        for (var i = 0; i < types.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            types[i].AppendDocId(builder);
        }

        builder.Append(')');
    }

    /// <summary>
    /// A member name in a documentation comment ID, as the C# compiler writes it: the dots of <c>.ctor</c> and of an
    /// explicit implementation are <c>#</c>, and its type arguments use braces
    /// (<c>Contoso#IConverter{nint,System#String}#Convert</c>).
    /// </summary>
    public static string EncodeName(string name)
    {
        return name.Replace('.', '#').Replace('<', '{').Replace('>', '}');
    }

    private static string ParameterTypeText(ApiParameter parameter)
    {
        var modifier = parameter.Modifier switch
        {
            null or "this" or "params" => null,
            var other => other.StartsWith("this ", StringComparison.Ordinal) ? other[5..] : other,
        };

        return modifier == null ? CSharpWriter.Plain(parameter.Type) : modifier + " " + CSharpWriter.Plain(parameter.Type);
    }

    private static string TypeParameterList(ImmutableArray<string> names)
    {
        return names.IsEmpty ? "" : "<" + string.Join(", ", names) + ">";
    }

    private static string TypeParameterList(ImmutableArray<string> names, List<ApiGenericParameter> parameters)
    {
        if (names.IsEmpty)
        {
            return "";
        }

        return "<" + string.Join(", ", parameters.Select(x => x.Variance == null ? x.Name : x.Variance + " " + x.Name)) + ">";
    }

    private static void AppendPrefix(StringBuilder builder, string? accessibility, List<string> modifiers)
    {
        if (!string.IsNullOrEmpty(accessibility))
        {
            builder.Append(accessibility).Append(' ');
        }

        foreach (var modifier in modifiers)
        {
            builder.Append(modifier).Append(' ');
        }
    }

    private static void AppendParameterList(StringBuilder builder, List<ApiParameter> parameters, char open, char close)
    {
        builder.Append(open);

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];

            if (i > 0)
            {
                builder.Append(", ");
            }

            if (parameter.Modifier != null)
            {
                builder.Append(parameter.Modifier).Append(' ');
            }

            builder.Append(CSharpWriter.Plain(parameter.Type)).Append(' ').Append(parameter.Name);

            if (parameter.DefaultValue != null)
            {
                builder.Append(" = ").Append(parameter.DefaultValue);
            }
        }

        builder.Append(close);
    }

    private static void AppendConstraints(StringBuilder builder, IEnumerable<ApiGenericParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Constraints.Count > 0)
            {
                builder.Append(" where ").Append(parameter.Name).Append(" : ").AppendJoin(", ", parameter.Constraints);
            }
        }
    }

    private static string TypeSignature(ApiType type)
    {
        var builder = new StringBuilder();
        AppendPrefix(builder, type.Accessibility, type.Modifiers);
        var name = type.Name;
        var parameters = type.TypeParameters;

        if (parameters.Count > 0)
        {
            // The name with the variance of the type parameters: interface IEnumerable<out T>.
            name = name[..name.IndexOf('<')] + "<" + string.Join(", ", parameters.Select(x => x.Variance == null ? x.Name : x.Variance + " " + x.Name)) + ">";
        }

        if (type.Kind == "delegate")
        {
            builder.Append("delegate ").Append(type.ReturnType == null ? "void" : CSharpWriter.Plain(type.ReturnType)).Append(' ').Append(name);
            AppendParameterList(builder, type.Parameters, '(', ')');
            AppendConstraints(builder, parameters);
            return builder.ToString();
        }

        builder.Append(type.Kind).Append(' ').Append(name);

        var bases = new List<string>();

        if (type.BaseType != null)
        {
            bases.Add(CSharpWriter.Plain(type.BaseType));
        }

        if (type.UnderlyingType != null)
        {
            bases.Add(CSharpWriter.Plain(type.UnderlyingType));
        }

        bases.AddRange(type.Interfaces.Select(x => CSharpWriter.Plain(x)));

        if (bases.Count > 0)
        {
            builder.Append(" : ").AppendJoin(", ", bases);
        }

        AppendConstraints(builder, parameters);
        return builder.ToString();
    }

    private bool Has(CustomAttributeHandleCollection attributes, string ns, string name)
    {
        foreach (var handle in attributes)
        {
            if (_provider.DeclaringType(_reader.GetCustomAttribute(handle).Constructor) is { } type && type.Is(ns, name))
            {
                return true;
            }
        }

        return false;
    }

    private string? Obsolete(CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = _reader.GetCustomAttribute(handle);

            if (_provider.DeclaringType(attribute.Constructor) is { } type && type.Is("System", "ObsoleteAttribute"))
            {
                return FirstString(attribute, _provider) ?? "";
            }
        }

        return null;
    }

    private static string? FirstString(CustomAttribute attribute, SignatureProvider provider)
    {
        try
        {
            var value = attribute.DecodeValue(provider);
            return value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A constant as C# writes it; an enum value is written with its type: <c>(Color)1</c>.
    /// </summary>
    private string? Constant(ConstantHandle handle, ApiTypeRef type)
    {
        if (handle.IsNil)
        {
            return null;
        }

        var constant = _reader.GetConstant(handle);
        var blob = _reader.GetBlobReader(constant.Value);
        var text = constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => blob.ReadBoolean() ? "true" : "false",
            ConstantTypeCode.Char => CharLiteral(blob.ReadChar()),
            ConstantTypeCode.SByte => blob.ReadSByte().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Byte => blob.ReadByte().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int16 => blob.ReadInt16().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt16 => blob.ReadUInt16().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int32 => blob.ReadInt32().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt32 => blob.ReadUInt32().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int64 => blob.ReadInt64().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt64 => blob.ReadUInt64().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Single => FloatLiteral(blob.ReadSingle()),
            ConstantTypeCode.Double => DoubleLiteral(blob.ReadDouble()),
            ConstantTypeCode.String => StringLiteral(blob.ReadUTF16(blob.Length)),
            ConstantTypeCode.NullReference => "null",
            _ => null,
        };

        // An enum value: the constant is the underlying integer.
        if (text != null && constant.TypeCode is not (ConstantTypeCode.String or ConstantTypeCode.NullReference or ConstantTypeCode.Boolean or ConstantTypeCode.Char)
            && type is NamedTypeRef named && CSharpWriter.Keyword(named) == null && !named.Is("System", "Nullable`1"))
        {
            return EnumMember(named, text) is { } member ? CSharpWriter.Plain(type) + "." + member : $"({CSharpWriter.Plain(type)}){text}";
        }

        return text;
    }

    /// <summary>
    /// The member of an enum of the assembly with a value, written as <see cref="Constant"/> writes it.
    /// </summary>
    private string? EnumMember(NamedTypeRef type, string value)
    {
        if (_enumMembers == null)
        {
            _enumMembers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            foreach (var handle in _reader.TypeDefinitions)
            {
                var definition = _reader.GetTypeDefinition(handle);

                if (_provider.Decode(definition.BaseType, default) is not NamedTypeRef baseType || !baseType.Is("System", "Enum"))
                {
                    continue;
                }

                var members = new Dictionary<string, string>(StringComparer.Ordinal);
                var int32 = NamedTypeRef.Create("System", "Int32");

                foreach (var fieldHandle in definition.GetFields())
                {
                    var field = _reader.GetFieldDefinition(fieldHandle);

                    if ((field.Attributes & FieldAttributes.Literal) != 0 && Constant(field.GetDefaultValue(), int32) is { } constant)
                    {
                        members.TryAdd(constant, _reader.GetString(field.Name));
                    }
                }

                _enumMembers[_provider.Definition(handle).MetadataName] = members;
            }
        }

        return _enumMembers.TryGetValue(type.Definition.MetadataName, out var byValue) && byValue.TryGetValue(value, out var name) ? name : null;
    }

    private static string FloatLiteral(float value) => float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) + "F"
        : float.IsNaN(value) ? "float.NaN" : value > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity";

    private static string DoubleLiteral(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture)
        : double.IsNaN(value) ? "double.NaN" : value > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity";

    private static string CharLiteral(char c) => c switch
    {
        '\'' => @"'\''",
        _ => "'" + Escape(c) + "'",
    };

    private static string StringLiteral(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');

        foreach (var c in text)
        {
            builder.Append(c == '"' ? "\\\"" : Escape(c));
        }

        return builder.Append('"').ToString();
    }

    private static string Escape(char c) => c switch
    {
        '\\' => @"\\",
        '\0' => @"\0",
        '\n' => @"\n",
        '\r' => @"\r",
        '\t' => @"\t",
        _ when char.IsControl(c) || char.IsSurrogate(c) => $"\\u{(int)c:X4}",
        _ => c.ToString(),
    };
}
