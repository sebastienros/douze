using System.Collections.Frozen;
using System.Net;
using System.Text;

namespace Douze.DotNetApi;

/// <summary>
/// Writes types as C#, as plain text or as HTML whose named types link to their pages (<see cref="Links"/>).
/// </summary>
internal sealed class CSharpWriter
{
    private static readonly FrozenDictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Void"] = "void",
        ["Boolean"] = "bool",
        ["Char"] = "char",
        ["SByte"] = "sbyte",
        ["Byte"] = "byte",
        ["Int16"] = "short",
        ["UInt16"] = "ushort",
        ["Int32"] = "int",
        ["UInt32"] = "uint",
        ["Int64"] = "long",
        ["UInt64"] = "ulong",
        ["Single"] = "float",
        ["Double"] = "double",
        ["Decimal"] = "decimal",
        ["String"] = "string",
        ["Object"] = "object",
        ["IntPtr"] = "nint",
        ["UIntPtr"] = "nuint",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly StringBuilder _builder = new();

    /// <summary>
    /// The url of the page of a type definition, or null: the HTML of a type without one has no link. Null writes
    /// plain text.
    /// </summary>
    public Func<NamedTypeRef, string?>? Links { get; init; }

    /// <summary>
    /// Whether named types are written with their namespace.
    /// </summary>
    public bool Qualified { get; init; }

    private bool Html => Links != null;

    public static string Plain(ApiTypeRef type, bool qualified = false)
    {
        var writer = new CSharpWriter { Qualified = qualified };
        type.AppendCSharp(writer);
        return writer.ToString();
    }

    /// <summary>
    /// The C# keyword of a type of <c>System</c>, or null.
    /// </summary>
    public static string? Keyword(NamedTypeRef type)
    {
        return type.Namespace == "System" && type.Names.Length == 1 && type.TypeArguments.IsEmpty && Keywords.TryGetValue(type.Names[0], out var keyword) ? keyword : null;
    }

    public void WriteText(string text)
    {
        _builder.Append(Html ? WebUtility.HtmlEncode(text) : text);
    }

    public void Write(ApiTypeRef type) => type.AppendCSharp(this);

    public void WriteNamed(NamedTypeRef type)
    {
        if (Keyword(type) is { } keyword)
        {
            WriteLink(keyword, type);
            return;
        }

        // int? and (int, string).
        if (type.Namespace == "System" && type.Names.Length == 1)
        {
            if (type.Names[0] == "Nullable`1" && type.TypeArguments.Length == 1)
            {
                type.TypeArguments[0].AppendCSharp(this);
                WriteText("?");
                return;
            }

            if (type.Names[0].StartsWith("ValueTuple`", StringComparison.Ordinal) && type.TypeArguments.Length is >= 2 and <= 7)
            {
                WriteText("(");

                for (var i = 0; i < type.TypeArguments.Length; i++)
                {
                    if (i > 0)
                    {
                        WriteText(", ");
                    }

                    type.TypeArguments[i].AppendCSharp(this);
                }

                WriteText(")");
                return;
            }
        }

        if (Qualified && type.Namespace.Length > 0)
        {
            WriteText(type.Namespace + ".");
        }

        var argument = 0;

        for (var i = 0; i < type.Names.Length; i++)
        {
            if (i > 0)
            {
                WriteText(".");
            }

            var (name, arity) = NamedTypeRef.SplitArity(type.Names[i]);

            // Each level links to its own type: Outer to Outer, Outer.Inner to Inner.
            var level = i == type.Names.Length - 1 ? type.Definition : new NamedTypeRef(type.Namespace, type.Names[..(i + 1)], []);
            WriteLink(name, level);

            if (arity > 0 && argument < type.TypeArguments.Length)
            {
                WriteText("<");

                for (var a = 0; a < arity && argument < type.TypeArguments.Length; a++, argument++)
                {
                    if (a > 0)
                    {
                        WriteText(", ");
                    }

                    type.TypeArguments[argument].AppendCSharp(this);
                }

                WriteText(">");
            }
        }
    }

    private void WriteLink(string text, NamedTypeRef type)
    {
        if (Links?.Invoke(type.Definition) is { } url)
        {
            _builder.Append("<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">").Append(WebUtility.HtmlEncode(text)).Append("</a>");
        }
        else
        {
            WriteText(text);
        }
    }

    public override string ToString() => _builder.ToString();
}
