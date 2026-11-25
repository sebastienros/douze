namespace Douze.DotNetApi;

/// <summary>
/// An assembly read by <see cref="AssemblyReader"/>: its public API, before it is documented and turned into nodes.
/// </summary>
internal sealed class ApiAssembly
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? InformationalVersion { get; init; }
    public string? TargetFramework { get; init; }

    /// <summary>
    /// The path of the file it was read from.
    /// </summary>
    public required string Path { get; init; }

    public List<ApiType> Types { get; } = [];

    /// <summary>
    /// The documentation comment IDs of every type the assembly defines, public or not: a reference to one of them that
    /// has no page is not to a type of another library.
    /// </summary>
    public HashSet<string> DefinedTypes { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A visible type of an assembly.
/// </summary>
internal sealed class ApiType
{
    /// <summary>
    /// The documentation comment ID: <c>T:Contoso.Outer`1.Inner</c>.
    /// </summary>
    public required string Id { get; init; }

    public required NamedTypeRef Ref { get; init; }
    public required string Namespace { get; init; }
    public required ApiAssembly Assembly { get; init; }

    /// <summary>
    /// <c>class</c>, <c>struct</c>, <c>interface</c>, <c>enum</c>, <c>delegate</c>, <c>record</c> or <c>record struct</c>.
    /// </summary>
    public required string Kind { get; init; }

    public ApiType? DeclaringType { get; init; }

    /// <summary>
    /// The name with the type's own type parameters: <c>Inner&lt;T&gt;</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The name with the declaring types: <c>Outer&lt;T&gt;.Inner</c>.
    /// </summary>
    public required string DisplayName { get; init; }

    public string FullName => Namespace.Length == 0 ? DisplayName : Namespace + "." + DisplayName;

    public required string Accessibility { get; init; }
    public required List<string> Modifiers { get; init; }
    public string Signature { get; set; } = "";
    public ApiTypeRef? BaseType { get; init; }
    public List<ApiTypeRef> Interfaces { get; } = [];

    /// <summary>
    /// The type parameters of the type itself (not those of its declaring types).
    /// </summary>
    public List<ApiGenericParameter> TypeParameters { get; } = [];

    public List<ApiMember> Members { get; } = [];
    public List<ApiType> NestedTypes { get; } = [];

    /// <summary>
    /// The message of <c>[Obsolete]</c> (empty without one), or null when the type is not obsolete.
    /// </summary>
    public string? Obsolete { get; init; }

    /// <summary>
    /// The parameters of a delegate.
    /// </summary>
    public List<ApiParameter> Parameters { get; } = [];

    /// <summary>
    /// The return type of a delegate.
    /// </summary>
    public ApiTypeRef? ReturnType { get; set; }

    public bool IsSealed { get; init; }

    /// <summary>
    /// The underlying type of an enum, when it is not <c>int</c>.
    /// </summary>
    public ApiTypeRef? UnderlyingType { get; set; }
}

/// <summary>
/// A visible member of a type: a constructor, a method, an operator, a property, an event or a field.
/// </summary>
internal sealed class ApiMember
{
    public required string Id { get; init; }

    /// <summary>
    /// <c>constructor</c>, <c>method</c>, <c>operator</c>, <c>property</c>, <c>event</c> or <c>field</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The metadata name: <c>Join</c>, <c>.ctor</c>, <c>op_Addition</c>, <c>System.IDisposable.Dispose</c>.
    /// </summary>
    public required string MetadataName { get; init; }

    /// <summary>
    /// The name as C# writes it: <c>Join</c>, the type's name for a constructor, <c>operator +</c>, <c>this[]</c>,
    /// <c>IDisposable.Dispose</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The name with the parameter types of a method or an indexer: <c>Join&lt;T&gt;(string, IEnumerable&lt;T&gt;)</c>.
    /// </summary>
    public required string DisplayName { get; init; }

    public required ApiType DeclaringType { get; init; }

    /// <summary>
    /// <c>public</c>, <c>protected</c> or <c>protected internal</c>; empty for an explicit interface implementation.
    /// </summary>
    public required string Accessibility { get; init; }

    public required List<string> Modifiers { get; init; }
    public required string Signature { get; init; }
    public List<ApiGenericParameter> TypeParameters { get; init; } = [];
    public List<ApiParameter> Parameters { get; init; } = [];

    /// <summary>
    /// The return type of a method or an operator, the type of a property, an event or a field. Null for a constructor
    /// and a method that returns <c>void</c>.
    /// </summary>
    public ApiTypeRef? Type { get; init; }

    /// <summary>
    /// <c>ref</c> or <c>ref readonly</c> for a method that returns a reference.
    /// </summary>
    public string? ReturnModifier { get; init; }

    /// <summary>
    /// The value of a constant or of an enum member, as C# writes it.
    /// </summary>
    public string? Value { get; init; }

    public string? Obsolete { get; init; }
    public bool IsExtension { get; init; }

    /// <summary>
    /// The interface of an explicit implementation.
    /// </summary>
    public NamedTypeRef? ExplicitInterface { get; init; }

    /// <summary>
    /// The documentation comment ID of the interface member an explicit implementation implements.
    /// </summary>
    public string? ImplementedId { get; init; }

    /// <summary>
    /// Whether the member overrides a member of a base type.
    /// </summary>
    public bool IsOverride { get; init; }

    /// <summary>
    /// The parameters of the documentation comment ID: <c>(System.Int32,System.String)</c>, or empty.
    /// </summary>
    public required string ParametersId { get; init; }

    /// <summary>
    /// The name the documentation comment ID of an inherited member would use: the metadata name, without the
    /// interface of an explicit implementation, with the arity of a generic method (<c>Join``1</c>).
    /// </summary>
    public required string InheritedName { get; init; }
}

internal sealed record ApiParameter(string Name, ApiTypeRef Type, string? Modifier, string? DefaultValue);

/// <summary>
/// A type parameter: <paramref name="Variance"/> is <c>in</c>, <c>out</c> or null, <paramref name="Constraints"/> the
/// constraints as C# writes them (<c>class</c>, <c>IComparable&lt;T&gt;</c>, <c>new()</c>).
/// </summary>
internal sealed record ApiGenericParameter(string Name, string? Variance, IReadOnlyList<string> Constraints);
