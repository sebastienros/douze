using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Douze.DotNetApi;

/// <summary>
/// The documentation comments of XML documentation files, by documentation comment ID, with
/// <c>&lt;inheritdoc/&gt;</c> resolved: from its <c>cref</c>, or from the member it overrides or implements.
/// </summary>
internal sealed class DocSet
{
    private const int MaxInheritance = 16;

    private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);

    // The IDs by their part before the parameters (M:N.T.Join``1), to find the member a member inherits from when
    // their parameters are written differently (a generic base type).
    private readonly Dictionary<string, List<string>> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads an XML documentation file; a member documented by an earlier file keeps that file's comment.
    /// </summary>
    /// <exception cref="DouzeException">The file is not an XML documentation file.</exception>
    public void Add(Stream stream, string path)
    {
        XDocument document;

        try
        {
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new DouzeException($"'{path}' is not valid XML: {e.Message}");
        }

        if (document.Root is not { Name.LocalName: "doc" } root)
        {
            throw new DouzeException($"'{path}' is not an XML documentation file: its root element is not <doc>");
        }

        foreach (var member in root.Element("members")?.Elements("member") ?? [])
        {
            if (member.Attribute("name")?.Value is { Length: > 0 } name && Normalize(name) is var id && _members.TryAdd(id, member))
            {
                var key = NameOf(id);

                if (!_byName.TryGetValue(key, out var ids))
                {
                    _byName[key] = ids = [];
                }

                ids.Add(id);
            }
        }
    }

    public int Count => _members.Count;

    public bool Contains(string id) => _members.ContainsKey(Normalize(id));

    /// <summary>
    /// The comment of a member as written, without resolving <c>&lt;inheritdoc/&gt;</c>.
    /// </summary>
    public XElement? Find(string id) => _members.GetValueOrDefault(Normalize(id));

    /// <summary>
    /// An ID with the type arguments of an explicit implementation's name in <c>{}</c> and separated by <c>,</c>, as
    /// the C# compiler writes them, and <c>nint</c> and <c>nuint</c> as their types: the documentation of .NET also
    /// writes <c>@</c> (<c>IEquatable{System#Int32@System#Int32}</c>), <c>&lt;&gt;</c>
    /// (<c>IBinaryInteger&lt;System#Byte&gt;</c>) and <c>System#IntPtr</c> where the compiler keeps <c>nint</c>.
    /// </summary>
    internal static string Normalize(string id)
    {
        var end = id.IndexOfAny(['(', '~']);
        var name = end < 0 ? id : id[..end];

        if (name.AsSpan().IndexOfAny('@', '<', '>') < 0 && !name.Contains("nint", StringComparison.Ordinal) && !name.Contains("nuint", StringComparison.Ordinal))
        {
            return id;
        }

        name = name.Replace('@', ',').Replace('<', '{').Replace('>', '}');
        name = ReplaceTypeArgument(ReplaceTypeArgument(name, "nint", "System#IntPtr"), "nuint", "System#UIntPtr");
        return name + (end < 0 ? "" : id[end..]);
    }

    /// <summary>
    /// Replaces a type argument of a name (between <c>{</c> or <c>,</c> and <c>,</c> or <c>}</c>).
    /// </summary>
    private static string ReplaceTypeArgument(string name, string argument, string replacement)
    {
        var builder = new StringBuilder(name.Length + 16);
        var i = 0;

        while (i < name.Length)
        {
            if ((name[i] is '{' or ',') && string.CompareOrdinal(name, i + 1, argument, 0, argument.Length) == 0
                && i + 1 + argument.Length < name.Length && name[i + 1 + argument.Length] is ',' or '}')
            {
                builder.Append(name[i]).Append(replacement);
                i += 1 + argument.Length;
            }
            else
            {
                builder.Append(name[i]);
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The comment of <paramref name="id"/> with <c>&lt;inheritdoc/&gt;</c> resolved. <paramref name="inherited"/> gives
    /// the members a member without a <c>cref</c> inherits from, in order: each is a documentation comment ID before
    /// its parameters, and the parameters to prefer.
    /// </summary>
    public XElement? Resolve(string id, Func<string, IEnumerable<(string Name, string Parameters)>> inherited)
    {
        return Resolve(id, inherited, 0);
    }

    private XElement? Resolve(string id, Func<string, IEnumerable<(string Name, string Parameters)>> inherited, int depth)
    {
        if (Find(id) is not { } element)
        {
            return null;
        }

        if (element.Element("inheritdoc") is not { } inheritdoc || depth >= MaxInheritance)
        {
            return element;
        }

        XElement? source = null;

        id = Normalize(id);

        if (inheritdoc.Attribute("cref")?.Value is { Length: > 0 } cref)
        {
            source = Resolve(cref, inherited, depth + 1);
        }
        else
        {
            // A member with the same parameters first, in any of the candidates, then one with as many.
            var candidates = inherited(id).ToList();

            foreach (var exact in (bool[])[true, false])
            {
                foreach (var (name, parameters) in candidates)
                {
                    if (Match(name, parameters, exact) is { } match && match != id && Resolve(match, inherited, depth + 1) is { } found)
                    {
                        source = found;
                        break;
                    }
                }

                if (source != null)
                {
                    break;
                }
            }
        }

        return source == null ? element : Merge(element, source);
    }

    /// <summary>
    /// The ID with this name and these parameters, or else (<paramref name="exact"/> false) the one with this name and
    /// as many parameters, when there is only one: the parameters of a generic base type's member are written with its
    /// type parameters.
    /// </summary>
    private string? Match(string name, string parameters, bool exact)
    {
        if (!_byName.TryGetValue(name, out var ids))
        {
            return null;
        }

        if (exact)
        {
            return ids.Contains(name + parameters) ? name + parameters : null;
        }

        var count = CountParameters(parameters);
        var candidates = ids.Where(x => CountParameters(x[name.Length..]) == count).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// The comment's own elements, plus the inherited ones it does not have: a <c>param</c>, <c>typeparam</c> or
    /// <c>exception</c> by its name or <c>cref</c>, any other element by its name.
    /// </summary>
    private static XElement Merge(XElement local, XElement inherited)
    {
        var merged = new XElement(local.Name, local.Attributes());
        var own = local.Elements().Where(x => x.Name.LocalName != "inheritdoc").ToList();
        merged.Add(own);

        foreach (var element in inherited.Elements())
        {
            var tag = element.Name.LocalName;

            var present = tag switch
            {
                "inheritdoc" => true,
                "param" or "typeparam" => own.Any(x => x.Name.LocalName == tag && (string?)x.Attribute("name") == (string?)element.Attribute("name")),
                "exception" or "seealso" or "permission" => own.Any(x => x.Name.LocalName == tag && (string?)x.Attribute("cref") == (string?)element.Attribute("cref")),
                _ => own.Any(x => x.Name.LocalName == tag),
            };

            if (!present)
            {
                merged.Add(element);
            }
        }

        return merged;
    }

    /// <summary>
    /// An ID before its parameters and the return type of a conversion operator.
    /// </summary>
    internal static string NameOf(string id)
    {
        var end = id.IndexOfAny(['(', '~']);
        return end < 0 ? id : id[..end];
    }

    private static int CountParameters(string parameters)
    {
        if (parameters.Length < 2 || parameters[0] != '(')
        {
            return 0;
        }

        var count = 1;
        var depth = 0;

        foreach (var c in parameters)
        {
            switch (c)
            {
                case '{' or '[' or '(':
                    depth++;
                    break;
                case '}' or ']' or ')':
                    depth--;
                    break;
                case ',' when depth == 1:
                    count++;
                    break;
            }
        }

        return count;
    }
}
