using System.Collections.Immutable;
using System.Text;

namespace Douze.DotNetApi;

/// <summary>
/// A type as a signature or a base list names it, decoded from metadata without loading the assembly that defines it.
/// It formats as a documentation comment ID (<see cref="AppendDocId"/>, the key of the XML documentation file) and as
/// C# (<see cref="AppendCSharp"/>).
/// </summary>
internal abstract record ApiTypeRef
{
    /// <summary>
    /// The ID of the type in a documentation comment ID of a member: <c>System.Collections.Generic.List{`0}</c>.
    /// </summary>
    public abstract void AppendDocId(StringBuilder builder);

    /// <summary>
    /// The type as C# writes it, through <paramref name="writer"/>, which decides how the named types are written.
    /// </summary>
    public abstract void AppendCSharp(CSharpWriter writer);

    /// <summary>
    /// The named type a link to this type goes to: the element of an array, of a pointer or of a reference.
    /// </summary>
    public virtual NamedTypeRef? LinkTarget => null;

    public string DocId()
    {
        var builder = new StringBuilder();
        AppendDocId(builder);
        return builder.ToString();
    }
}

/// <summary>
/// A type with a name: <paramref name="Names"/> are its metadata names from the outermost declaring type (with their
/// arity, <c>List`1</c>), <paramref name="TypeArguments"/> the arguments of a generic instantiation, every level's in
/// order (empty for the definition itself).
/// </summary>
internal sealed record NamedTypeRef(string Namespace, ImmutableArray<string> Names, ImmutableArray<ApiTypeRef> TypeArguments) : ApiTypeRef
{
    public static NamedTypeRef Create(string ns, string name) => new(ns, [name], []);

    /// <summary>
    /// Whether a signature names the type as a value type. A type named elsewhere (a base list) is not known to be one.
    /// </summary>
    public bool IsValueType { get; init; }

    public override NamedTypeRef LinkTarget => this;

    /// <summary>
    /// The definition of a generic instantiation, or this type.
    /// </summary>
    public NamedTypeRef Definition => TypeArguments.IsEmpty ? this : this with { TypeArguments = [] };

    /// <summary>
    /// The namespace and the name of the type, without its arguments: <c>System.Collections.Generic.List`1</c>.
    /// </summary>
    public string MetadataName => Namespace.Length == 0 ? string.Join('.', Names) : Namespace + "." + string.Join('.', Names);

    /// <summary>
    /// The documentation comment ID of the definition: <c>T:System.Collections.Generic.List`1</c>.
    /// </summary>
    public string DefinitionId => "T:" + MetadataName;

    public bool Is(string ns, string name) => Names.Length == 1 && Names[0] == name && Namespace == ns;

    public override void AppendDocId(StringBuilder builder)
    {
        if (Namespace.Length > 0)
        {
            builder.Append(Namespace).Append('.');
        }

        if (TypeArguments.IsEmpty)
        {
            builder.AppendJoin('.', Names);
            return;
        }

        var argument = 0;

        for (var i = 0; i < Names.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('.');
            }

            var (name, arity) = SplitArity(Names[i]);
            builder.Append(name);

            if (arity > 0)
            {
                builder.Append('{');

                for (var a = 0; a < arity && argument < TypeArguments.Length; a++, argument++)
                {
                    if (a > 0)
                    {
                        builder.Append(',');
                    }

                    TypeArguments[argument].AppendDocId(builder);
                }

                builder.Append('}');
            }
        }
    }

    public override void AppendCSharp(CSharpWriter writer) => writer.WriteNamed(this);

    /// <summary>
    /// A metadata name without its arity: <c>List`1</c> is <c>List</c> and 1.
    /// </summary>
    public static (string Name, int Arity) SplitArity(string name)
    {
        var tick = name.LastIndexOf('`');

        if (tick > 0 && int.TryParse(name.AsSpan(tick + 1), out var arity))
        {
            return (name[..tick], arity);
        }

        return (name, 0);
    }
}

/// <summary>
/// A type parameter of a type (<c>`0</c>) or of a method (<c>``0</c>).
/// </summary>
internal sealed record GenericParameterRef(bool IsMethod, int Index, string Name) : ApiTypeRef
{
    public override void AppendDocId(StringBuilder builder) => builder.Append(IsMethod ? "``" : "`").Append(Index);

    public override void AppendCSharp(CSharpWriter writer) => writer.WriteText(Name);
}

/// <summary>
/// An array: <paramref name="Rank"/> is 0 for a vector (<c>T[]</c>), its rank for a multi-dimensional array.
/// </summary>
internal sealed record ArrayTypeRef(ApiTypeRef Element, int Rank) : ApiTypeRef
{
    public override NamedTypeRef? LinkTarget => Element.LinkTarget;

    public override void AppendDocId(StringBuilder builder)
    {
        Element.AppendDocId(builder);

        if (Rank == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');

        for (var i = 0; i < Rank; i++)
        {
            builder.Append(i == 0 ? "0:" : ",0:");
        }

        builder.Append(']');
    }

    public override void AppendCSharp(CSharpWriter writer)
    {
        Element.AppendCSharp(writer);
        writer.WriteText(Rank <= 1 ? "[]" : "[" + new string(',', Rank - 1) + "]");
    }
}

internal sealed record PointerTypeRef(ApiTypeRef Element) : ApiTypeRef
{
    public override NamedTypeRef? LinkTarget => Element.LinkTarget;

    public override void AppendDocId(StringBuilder builder)
    {
        Element.AppendDocId(builder);
        builder.Append('*');
    }

    public override void AppendCSharp(CSharpWriter writer)
    {
        Element.AppendCSharp(writer);
        writer.WriteText("*");
    }
}

/// <summary>
/// A managed reference (<c>ref</c>, <c>out</c>, <c>in</c>): C# writes the keyword with the parameter or the return
/// type, not with the type.
/// </summary>
internal sealed record ByRefTypeRef(ApiTypeRef Element) : ApiTypeRef
{
    public override NamedTypeRef? LinkTarget => Element.LinkTarget;

    public override void AppendDocId(StringBuilder builder)
    {
        Element.AppendDocId(builder);
        builder.Append('@');
    }

    public override void AppendCSharp(CSharpWriter writer) => Element.AppendCSharp(writer);
}

/// <summary>
/// A nullable reference type or type parameter (<c>string?</c>, <c>T?</c>), from the nullable metadata of the compiler.
/// </summary>
internal sealed record NullableTypeRef(ApiTypeRef Element) : ApiTypeRef
{
    public override NamedTypeRef? LinkTarget => Element.LinkTarget;

    public override void AppendDocId(StringBuilder builder) => Element.AppendDocId(builder);

    public override void AppendCSharp(CSharpWriter writer)
    {
        Element.AppendCSharp(writer);
        writer.WriteText("?");
    }
}

/// <summary>
/// A type with a custom modifier (<c>modreq</c>, <c>modopt</c>): documentation comment IDs and C# leave it out.
/// </summary>
internal sealed record ModifiedTypeRef(ApiTypeRef Unmodified, ApiTypeRef Modifier, bool IsRequired) : ApiTypeRef
{
    public override NamedTypeRef? LinkTarget => Unmodified.LinkTarget;

    public override void AppendDocId(StringBuilder builder) => Unmodified.AppendDocId(builder);

    public override void AppendCSharp(CSharpWriter writer) => Unmodified.AppendCSharp(writer);

    /// <summary>
    /// Whether the modifier is the type <c>System.Runtime.CompilerServices.<paramref name="name"/></c>.
    /// </summary>
    public bool HasModifier(string name) => Modifier is NamedTypeRef named && named.Is("System.Runtime.CompilerServices", name);
}

/// <summary>
/// A function pointer: <c>delegate*&lt;int, void&gt;</c>.
/// </summary>
internal sealed record FunctionPointerTypeRef(ApiTypeRef ReturnType, ImmutableArray<ApiTypeRef> ParameterTypes) : ApiTypeRef
{
    public override void AppendDocId(StringBuilder builder)
    {
        builder.Append("=FUNC:");
        ReturnType.AppendDocId(builder);

        if (!ParameterTypes.IsEmpty)
        {
            builder.Append('(');

            for (var i = 0; i < ParameterTypes.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                ParameterTypes[i].AppendDocId(builder);
            }

            builder.Append(')');
        }
    }

    public override void AppendCSharp(CSharpWriter writer)
    {
        writer.WriteText("delegate*<");

        foreach (var parameter in ParameterTypes)
        {
            parameter.AppendCSharp(writer);
            writer.WriteText(", ");
        }

        ReturnType.AppendCSharp(writer);
        writer.WriteText(">");
    }
}
