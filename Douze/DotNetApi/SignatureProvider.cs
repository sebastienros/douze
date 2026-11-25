using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Douze.DotNetApi;

/// <summary>
/// The names of the type parameters in scope of a signature: those of the type (with those of its declaring types,
/// as metadata numbers them) and those of the method.
/// </summary>
internal readonly record struct GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters);

/// <summary>
/// Decodes the signatures and the custom attributes of one assembly into <see cref="ApiTypeRef"/>s. A referenced type
/// is known by its name only: no other assembly is read.
/// </summary>
internal sealed class SignatureProvider(MetadataReader reader) : ISignatureTypeProvider<ApiTypeRef, GenericContext>, ICustomAttributeTypeProvider<ApiTypeRef>
{
    private static readonly NamedTypeRef SystemType = NamedTypeRef.Create("System", "Type");

    private readonly Dictionary<EntityHandle, NamedTypeRef> _named = [];

    public MetadataReader Reader => reader;

    public ApiTypeRef GetArrayType(ApiTypeRef elementType, ArrayShape shape) => new ArrayTypeRef(elementType, shape.Rank);

    public ApiTypeRef GetByReferenceType(ApiTypeRef elementType) => new ByRefTypeRef(elementType);

    public ApiTypeRef GetFunctionPointerType(MethodSignature<ApiTypeRef> signature) => new FunctionPointerTypeRef(signature.ReturnType, signature.ParameterTypes);

    public ApiTypeRef GetGenericInstantiation(ApiTypeRef genericType, ImmutableArray<ApiTypeRef> typeArguments)
    {
        return genericType is NamedTypeRef named ? named with { TypeArguments = typeArguments } : genericType;
    }

    public ApiTypeRef GetGenericMethodParameter(GenericContext genericContext, int index)
    {
        var names = genericContext.MethodParameters;
        return new GenericParameterRef(true, index, !names.IsDefault && index < names.Length ? names[index] : $"TMethod{index}");
    }

    public ApiTypeRef GetGenericTypeParameter(GenericContext genericContext, int index)
    {
        var names = genericContext.TypeParameters;
        return new GenericParameterRef(false, index, !names.IsDefault && index < names.Length ? names[index] : $"T{index}");
    }

    public ApiTypeRef GetModifiedType(ApiTypeRef modifier, ApiTypeRef unmodifiedType, bool isRequired) => new ModifiedTypeRef(unmodifiedType, modifier, isRequired);

    public ApiTypeRef GetPinnedType(ApiTypeRef elementType) => elementType;

    public ApiTypeRef GetPointerType(ApiTypeRef elementType) => new PointerTypeRef(elementType);

    public ApiTypeRef GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return NamedTypeRef.Create("System", PrimitiveName(typeCode)) with { IsValueType = typeCode is not (PrimitiveTypeCode.String or PrimitiveTypeCode.Object) };
    }

    public ApiTypeRef GetSZArrayType(ApiTypeRef elementType) => new ArrayTypeRef(elementType, 0);

    public ApiTypeRef GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => WithKind(Definition(handle), rawTypeKind);

    public ApiTypeRef GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => WithKind(Reference(handle), rawTypeKind);

    private static NamedTypeRef WithKind(NamedTypeRef type, byte rawTypeKind)
    {
        return rawTypeKind == (byte)SignatureTypeKind.ValueType ? type with { IsValueType = true } : type;
    }

    public ApiTypeRef GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    /// <summary>
    /// The type a handle of a base list, a constraint or a member reference names.
    /// </summary>
    public ApiTypeRef? Decode(EntityHandle handle, GenericContext context)
    {
        if (handle.IsNil)
        {
            return null;
        }

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => Reference((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context),
            _ => null,
        };
    }

    public NamedTypeRef Definition(TypeDefinitionHandle handle)
    {
        if (_named.TryGetValue(handle, out var named))
        {
            return named;
        }

        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();

        if (declaring.IsNil)
        {
            named = new NamedTypeRef(reader.GetString(definition.Namespace), [name], []);
        }
        else
        {
            var outer = Definition(declaring);
            named = outer with { Names = outer.Names.Add(name) };
        }

        _named[handle] = named;
        return named;
    }

    public NamedTypeRef Reference(TypeReferenceHandle handle)
    {
        if (_named.TryGetValue(handle, out var named))
        {
            return named;
        }

        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);

        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outer = Reference((TypeReferenceHandle)reference.ResolutionScope);
            named = outer with { Names = outer.Names.Add(name) };
        }
        else
        {
            named = new NamedTypeRef(reader.GetString(reference.Namespace), [name], []);
        }

        _named[handle] = named;
        return named;
    }

    /// <summary>
    /// The type that declares a method or a field a custom attribute or a method implementation names.
    /// </summary>
    public NamedTypeRef? DeclaringType(EntityHandle member, GenericContext context = default)
    {
        switch (member.Kind)
        {
            case HandleKind.MethodDefinition:
                return Definition(reader.GetMethodDefinition((MethodDefinitionHandle)member).GetDeclaringType());

            case HandleKind.MemberReference:
                var parent = reader.GetMemberReference((MemberReferenceHandle)member).Parent;
                return parent.Kind switch
                {
                    HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)parent),
                    HandleKind.TypeReference => Reference((TypeReferenceHandle)parent),
                    HandleKind.TypeSpecification => Decode(parent, context) as NamedTypeRef,
                    _ => null,
                };

            default:
                return null;
        }
    }

    // Custom attributes: only the attributes Douze reads are decoded (strings and booleans), so an enum argument is
    // read as an int.
    public ApiTypeRef GetSystemType() => SystemType;

    public ApiTypeRef GetTypeFromSerializedName(string name)
    {
        var comma = name.IndexOf(',');
        var full = comma < 0 ? name : name[..comma];
        var dot = full.LastIndexOf('.');
        return dot < 0 ? NamedTypeRef.Create("", full) : NamedTypeRef.Create(full[..dot], full[(dot + 1)..]);
    }

    public PrimitiveTypeCode GetUnderlyingEnumType(ApiTypeRef type) => PrimitiveTypeCode.Int32;

    public bool IsSystemType(ApiTypeRef type) => type is NamedTypeRef named && named.Is("System", "Type");

    private static string PrimitiveName(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Boolean => "Boolean",
        PrimitiveTypeCode.Byte => "Byte",
        PrimitiveTypeCode.SByte => "SByte",
        PrimitiveTypeCode.Char => "Char",
        PrimitiveTypeCode.Int16 => "Int16",
        PrimitiveTypeCode.UInt16 => "UInt16",
        PrimitiveTypeCode.Int32 => "Int32",
        PrimitiveTypeCode.UInt32 => "UInt32",
        PrimitiveTypeCode.Int64 => "Int64",
        PrimitiveTypeCode.UInt64 => "UInt64",
        PrimitiveTypeCode.Single => "Single",
        PrimitiveTypeCode.Double => "Double",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.Object => "Object",
        PrimitiveTypeCode.String => "String",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        PrimitiveTypeCode.Void => "Void",
        _ => code.ToString(),
    };
}
