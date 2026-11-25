using System.Text;
using System.Xml.Linq;
using Douze.Nodes;

namespace Douze.DotNetApi;

/// <summary>
/// Turns the assemblies read by <see cref="AssemblyReader"/> and their documentation into the data templates read:
/// namespaces, types and members with their urls, signatures, type references (with HTML that links each type to its
/// page) and documentation as HTML. The urls are made from the documentation comment IDs: <c>{url}{slug}/</c>, slugs in
/// lowercase and unique, <c>`</c> written <c>-</c> (<c>contoso.widgets.list-1</c>).
/// </summary>
internal sealed class ApiNodeBuilder
{
    /// <summary>
    /// The documentation of another library: the types whose namespace is <paramref name="Prefix"/> or under it link
    /// to <paramref name="Pattern"/>, where <c>{slug}</c> is their slug (<c>system.string.join</c>) and <c>{name}</c>
    /// their ID without its kind and its parameters (<c>System.String.Join</c>).
    /// </summary>
    public sealed record ExternalDocs(string Prefix, string Pattern);

    public static readonly IReadOnlyList<ExternalDocs> DefaultExternal =
    [
        new("System", "https://learn.microsoft.com/dotnet/api/{slug}"),
        new("Microsoft", "https://learn.microsoft.com/dotnet/api/{slug}"),
    ];

    private static readonly string[] KindOrder = ["constructor", "property", "method", "event", "field", "operator"];

    private readonly string _url;
    private readonly bool _memberPages;
    private readonly IReadOnlyList<ExternalDocs> _external;
    private readonly DocSet _docs;
    private readonly DocHtml _html;

    private readonly Dictionary<string, ApiType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _urls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _anchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _slugs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node> _typeRefs = new(StringComparer.Ordinal);

    public ApiNodeBuilder(string url, bool memberPages, IReadOnlyList<ExternalDocs> external, DocSet docs)
    {
        _url = url;
        _memberPages = memberPages;
        _external = external;
        _docs = docs;
        _html = new DocHtml(ResolveCref);
    }

    /// <summary>
    /// What happened that the build should say: a type defined twice.
    /// </summary>
    public List<string> Warnings { get; } = [];

    public NodeObject Build(IReadOnlyList<ApiAssembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.Types)
            {
                if (!_types.TryAdd(type.Id, type))
                {
                    Warnings.Add($"the type '{type.FullName}' is defined by both '{_types[type.Id].Assembly.Path}' and '{assembly.Path}': the first one is documented");
                }
            }
        }

        var types = _types.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        var namespaces = types.GroupBy(x => x.Namespace, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

        // Urls first: the documentation of any item links to any other.
        foreach (var ns in namespaces)
        {
            _urls["N:" + ns.Key] = _url + Unique(ns.Key.Length == 0 ? "global" : Slug(ns.Key)) + "/";
        }

        var groupsByType = new Dictionary<ApiType, List<List<ApiMember>>>();

        foreach (var type in types)
        {
            var typeSlug = Unique(Slug(type.Ref.MetadataName));
            var typeUrl = _url + typeSlug + "/";
            _urls[type.Id] = typeUrl;

            var groups = Sort(type.Members).GroupBy(x => x.MetadataName, StringComparer.Ordinal).Select(x => x.ToList()).ToList();
            groupsByType[type] = groups;
            var pageAnchors = new HashSet<string>(StringComparer.Ordinal);

            // The values of an enum are anchors of its page.
            var memberPages = _memberPages && type.Kind != "enum";

            foreach (var group in groups)
            {
                var nameSlug = MemberSlug(group[0].MetadataName);
                string? groupUrl = null;

                if (memberPages)
                {
                    groupUrl = _url + Unique(typeSlug + "." + nameSlug) + "/";
                    pageAnchors = new HashSet<string>(StringComparer.Ordinal);
                }

                foreach (var member in group)
                {
                    _members.TryAdd(member.Id, member);
                    var anchorBase = nameSlug.TrimStart('-');

                    if (group.Count > 1 && member.ParametersId.Length > 0)
                    {
                        anchorBase += "-" + Slug(member.ParametersId.Trim('(', ')').Replace(',', '-'));
                    }

                    var anchor = UniqueIn(pageAnchors, anchorBase);
                    _anchors[member.Id] = anchor;
                    _urls[member.Id] = (groupUrl ?? typeUrl) + "#" + anchor;
                }

                _urls[GroupKey(group[0])] = groupUrl ?? _urls[group[0].Id];
            }
        }

        // The types derived from each type, by its ID.
        var derived = types.Where(x => x.BaseType?.LinkTarget != null).ToLookup(x => x.BaseType!.LinkTarget!.DefinitionId, StringComparer.Ordinal);
        var typeNodes = new Dictionary<ApiType, NodeObject>();
        var allTypes = new NodeArray();
        var allGroups = new NodeArray();
        var ids = new NodeObject();

        foreach (var type in types)
        {
            var node = TypeNode(type, derived[type.Id], groupsByType[type], allGroups, ids);
            typeNodes[type] = node;
            allTypes.Add(node);
            ids[type.Id] = node;
        }

        var namespaceNodes = new NodeArray();

        foreach (var ns in namespaces)
        {
            var nsTypes = new NodeArray();

            foreach (var type in ns.OrderBy(x => x.DisplayName, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                nsTypes.Add(typeNodes[type]);
            }

            var name = ns.Key;
            var node = new NodeObject
            {
                ["kind"] = "namespace",
                ["id"] = "N:" + name,
                ["name"] = name,
                ["title"] = (name.Length == 0 ? "Global" : name) + " namespace",
                ["url"] = _urls["N:" + name],
                ["assemblies"] = Strings(ns.Select(x => x.Assembly.Name).Distinct().Order(StringComparer.Ordinal)),
                ["types"] = nsTypes,
            };

            // A namespace is documented by a comment of its own, or by the class NamespaceDoc in it.
            var doc = _docs.Find("N:" + name) ?? _docs.Find("T:" + (name.Length == 0 ? "" : name + ".") + "NamespaceDoc");
            Set(node, "summary", _html.Render(doc?.Element("summary")));
            Set(node, "remarks", _html.Render(doc?.Element("remarks")));
            namespaceNodes.Add(node);
            ids["N:" + name] = node;
        }

        var assemblyNodes = new NodeArray();

        foreach (var assembly in assemblies)
        {
            var node = new NodeObject
            {
                ["name"] = assembly.Name,
                ["version"] = assembly.Version,
                ["namespaces"] = Strings(assembly.Types.Select(x => x.Namespace).Distinct().Order(StringComparer.Ordinal)),
            };

            Set(node, "informationalVersion", assembly.InformationalVersion);
            Set(node, "targetFramework", assembly.TargetFramework);
            assemblyNodes.Add(node);
        }

        return new NodeObject
        {
            ["url"] = _url,
            ["assemblies"] = assemblyNodes,
            ["namespaces"] = namespaceNodes,
            ["types"] = allTypes,
            ["members"] = allGroups,
            ["ids"] = ids,
        };
    }

    private static IEnumerable<ApiMember> Sort(IEnumerable<ApiMember> members)
    {
        return members
            .OrderBy(x => Array.IndexOf(KindOrder, x.Kind))
            .ThenBy(x => x.ExplicitInterface != null)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Parameters.Count)
            .ThenBy(x => x.Id, StringComparer.Ordinal);
    }

    private static string GroupKey(ApiMember member) => "O:" + member.DeclaringType.Id[2..] + "." + member.MetadataName;

    private NodeObject TypeNode(ApiType type, IEnumerable<ApiType> derivedTypes, List<List<ApiMember>> groups, NodeArray allGroups, NodeObject ids)
    {
        var doc = _docs.Resolve(type.Id, Inherited);

        var node = new NodeObject
        {
            ["kind"] = type.Kind,
            ["id"] = type.Id,
            ["name"] = type.Name,
            ["displayName"] = type.DisplayName,
            ["fullName"] = type.FullName,
            ["title"] = type.DisplayName + " " + type.Kind,
            ["namespace"] = type.Namespace,
            ["namespaceUrl"] = _urls["N:" + type.Namespace],
            ["assembly"] = type.Assembly.Name,
            ["url"] = _urls[type.Id],
            ["accessibility"] = type.Accessibility,
            ["modifiers"] = Strings(type.Modifiers),
            ["signature"] = type.Signature,
            ["interfaces"] = TypeRefs(type.Interfaces),
            ["inheritance"] = TypeRefs(Inheritance(type)),
            ["derivedTypes"] = TypeRefs(derivedTypes.Select(x => (ApiTypeRef)x.Ref)),
            ["nestedTypes"] = TypeRefs(type.NestedTypes.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => (ApiTypeRef)x.Ref)),
            ["typeParameters"] = TypeParameters(type.TypeParameters, doc),
        };

        if (type.DeclaringType != null)
        {
            node["declaringType"] = TypeRef(type.DeclaringType.Ref);
        }

        if (type.BaseType != null)
        {
            node["baseType"] = TypeRef(type.BaseType);
        }

        if (type.UnderlyingType != null)
        {
            node["underlyingType"] = TypeRef(type.UnderlyingType);
        }

        if (type.Kind == "delegate")
        {
            node["parameters"] = Parameters(type.Parameters, doc);

            if (type.ReturnType != null)
            {
                node["returns"] = Returns(type.ReturnType, null, doc);
            }
        }

        Obsolete(node, type.Obsolete);
        Documentation(node, doc);

        var lists = KindOrder.ToDictionary(x => x, _ => new NodeArray(), StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var overloads = new NodeArray();
            var first = group[0];
            var title = GroupTitle(first);

            foreach (var member in group)
            {
                var memberNode = MemberNode(member, title);
                lists[member.Kind].Add(memberNode);
                overloads.Add(memberNode);
                ids[member.Id] = memberNode;
            }

            if (type.Kind == "enum")
            {
                continue;
            }

            allGroups.Add(new NodeObject
            {
                ["kind"] = first.Kind,
                ["name"] = first.Name,
                ["title"] = title,
                ["url"] = _urls[GroupKey(first)],
                ["type"] = TypeRef(type.Ref),
                ["overloads"] = overloads,
            });
        }

        node["constructors"] = lists["constructor"];
        node["properties"] = lists["property"];
        node["methods"] = lists["method"];
        node["events"] = lists["event"];
        node["fields"] = lists["field"];
        node["operators"] = lists["operator"];

        return node;
    }

    private NodeObject MemberNode(ApiMember member, string title)
    {
        var doc = _docs.Resolve(member.Id, Inherited);

        var node = new NodeObject
        {
            ["kind"] = member.Kind,
            ["id"] = member.Id,
            ["name"] = member.Name,
            ["displayName"] = member.DisplayName,
            ["title"] = title,
            ["url"] = _urls[member.Id],
            ["anchor"] = _anchors[member.Id],
            ["accessibility"] = member.Accessibility,
            ["modifiers"] = Strings(member.Modifiers),
            ["signature"] = member.Signature,
            ["declaringType"] = TypeRef(member.DeclaringType.Ref),
            ["typeParameters"] = TypeParameters(member.TypeParameters, doc),
            ["parameters"] = Parameters(member.Parameters, doc),
        };

        if (member.Kind is "method" or "operator")
        {
            if (member.Type != null)
            {
                node["returns"] = Returns(member.Type, member.ReturnModifier, doc);
            }
        }
        else if (member.Type != null)
        {
            node["type"] = TypeRef(member.Type);
        }

        Set(node, "value", member.Value);

        if (member.IsExtension)
        {
            node["isExtension"] = NodeValue.Create(true);
        }

        if (member.IsOverride)
        {
            node["isOverride"] = NodeValue.Create(true);
        }

        if (member.ExplicitInterface != null)
        {
            node["isExplicit"] = NodeValue.Create(true);
            node["explicitInterface"] = TypeRef(member.ExplicitInterface);
        }

        Obsolete(node, member.Obsolete);
        Documentation(node, doc);

        // The <value> of a property.
        Set(node, "valueDescription", _html.Render(doc?.Element("value")));

        return node;
    }

    private void Documentation(NodeObject node, XElement? doc)
    {
        Set(node, "summary", _html.Render(doc?.Element("summary")));
        Set(node, "remarks", _html.Render(doc?.Element("remarks")));

        if (doc?.Elements("example").Select(_html.Render).Where(x => x != null).ToList() is { Count: > 0 } examples)
        {
            node["example"] = string.Join("\n", examples);
        }

        var exceptions = new NodeArray();

        foreach (var exception in doc?.Elements("exception") ?? [])
        {
            if ((string?)exception.Attribute("cref") is not { Length: > 0 } cref)
            {
                continue;
            }

            var item = new NodeObject { ["type"] = Link(cref) };
            Set(item, "description", _html.Render(exception));
            exceptions.Add(item);
        }

        node["exceptions"] = exceptions;

        var seeAlso = new NodeArray();

        foreach (var element in doc?.Elements("seealso") ?? [])
        {
            if ((string?)element.Attribute("cref") is { Length: > 0 } cref)
            {
                seeAlso.Add(Link(cref));
            }
            else if ((string?)element.Attribute("href") is { Length: > 0 } href)
            {
                var text = _html.Render(element) ?? System.Net.WebUtility.HtmlEncode(href);
                seeAlso.Add(new NodeObject
                {
                    ["name"] = string.Concat(element.Nodes().OfType<XText>().Select(x => x.Value)).Trim() is { Length: > 0 } plain ? plain : href,
                    ["url"] = href,
                    ["html"] = $"<a href=\"{System.Net.WebUtility.HtmlEncode(href)}\">{text}</a>",
                });
            }
        }

        node["seeAlso"] = seeAlso;
    }

    private NodeObject Link(string cref)
    {
        var (text, url) = ResolveCref(cref);
        var encoded = System.Net.WebUtility.HtmlEncode(text);
        var node = new NodeObject
        {
            ["name"] = text,
            ["id"] = cref,
            ["html"] = url == null ? encoded : $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\">{encoded}</a>",
        };

        Set(node, "url", url);
        return node;
    }

    private NodeArray Parameters(List<ApiParameter> parameters, XElement? doc)
    {
        var array = new NodeArray();

        foreach (var parameter in parameters)
        {
            var node = new NodeObject
            {
                ["name"] = parameter.Name,
                ["type"] = TypeRef(parameter.Type),
            };

            Set(node, "modifier", parameter.Modifier);
            Set(node, "defaultValue", parameter.DefaultValue);
            Set(node, "description", _html.Render(doc?.Elements("param").FirstOrDefault(x => (string?)x.Attribute("name") == parameter.Name)));
            array.Add(node);
        }

        return array;
    }

    private NodeObject Returns(ApiTypeRef type, string? modifier, XElement? doc)
    {
        var node = new NodeObject { ["type"] = TypeRef(type) };
        Set(node, "modifier", modifier);
        Set(node, "description", _html.Render(doc?.Element("returns")));
        return node;
    }

    private NodeArray TypeParameters(List<ApiGenericParameter> parameters, XElement? doc)
    {
        var array = new NodeArray();

        foreach (var parameter in parameters)
        {
            var node = new NodeObject { ["name"] = parameter.Name };
            Set(node, "variance", parameter.Variance);

            if (parameter.Constraints.Count > 0)
            {
                node["constraints"] = string.Join(", ", parameter.Constraints);
            }

            Set(node, "description", _html.Render(doc?.Elements("typeparam").FirstOrDefault(x => (string?)x.Attribute("name") == parameter.Name)));
            array.Add(node);
        }

        return array;
    }

    private static void Obsolete(NodeObject node, string? obsolete)
    {
        if (obsolete != null)
        {
            node["obsolete"] = new NodeObject { ["message"] = obsolete };
        }
    }

    /// <summary>
    /// The base types of a class from <c>object</c> to its direct base, as far as the assemblies tell; the fixed chain
    /// of a struct, an enum or a delegate.
    /// </summary>
    private List<ApiTypeRef> Inheritance(ApiType type)
    {
        var chain = new List<ApiTypeRef>();
        var objectType = NamedTypeRef.Create("System", "Object");

        switch (type.Kind)
        {
            case "interface":
                return chain;
            case "struct" or "record struct":
                return [objectType, NamedTypeRef.Create("System", "ValueType")];
            case "enum":
                return [objectType, NamedTypeRef.Create("System", "ValueType"), NamedTypeRef.Create("System", "Enum")];
            case "delegate":
                return [objectType, NamedTypeRef.Create("System", "Delegate"), NamedTypeRef.Create("System", "MulticastDelegate")];
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type.BaseType; current != null;)
        {
            chain.Insert(0, current);

            if (current.LinkTarget is not { } target || !visited.Add(target.DefinitionId) || !_types.TryGetValue(target.DefinitionId, out var local))
            {
                break;
            }

            current = local.BaseType;
        }

        if (!type.Ref.Is("System", "Object") && (chain.Count == 0 || chain[0] is not NamedTypeRef first || !first.Is("System", "Object")))
        {
            chain.Insert(0, objectType);
        }

        return chain;
    }

    /// <summary>
    /// The members a member or a type without a <c>cref</c> in its <c>&lt;inheritdoc/&gt;</c> inherits from: the
    /// interface member of an explicit implementation, then the same member in the base types and the interfaces.
    /// </summary>
    private IEnumerable<(string Name, string Parameters)> Inherited(string id)
    {
        if (_types.TryGetValue(id, out var type))
        {
            var (bases, interfaces) = Ancestors(type);

            foreach (var ancestor in bases.Concat(interfaces))
            {
                yield return (ancestor.DefinitionId, "");
            }

            yield break;
        }

        if (!_members.TryGetValue(id, out var member))
        {
            yield break;
        }

        if (member.ImplementedId is { } implemented)
        {
            var name = DocSet.NameOf(implemented);
            yield return (name, implemented[name.Length..]);
        }

        // An override inherits from its base types, object last; another member from the interfaces it implements.
        var kind = member.Id[..2];
        var ancestors = Ancestors(member.DeclaringType);

        foreach (var ancestor in member.IsOverride ? ancestors.Bases : ancestors.Interfaces)
        {
            yield return ($"{kind}{ancestor.MetadataName}.{member.InheritedName}", member.ParametersId);
        }

        if (member.IsOverride && kind == "M:")
        {
            yield return ($"M:System.Object.{member.InheritedName}", member.ParametersId);
        }
    }

    /// <summary>
    /// The base types of a type, and its interfaces and theirs, as definitions.
    /// </summary>
    private (List<NamedTypeRef> Bases, List<NamedTypeRef> Interfaces) Ancestors(ApiType type)
    {
        var bases = new List<NamedTypeRef>();
        var interfaces = new List<NamedTypeRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<ApiType>();
        pending.Enqueue(type);

        while (pending.TryDequeue(out var current))
        {
            if (current.BaseType?.LinkTarget is { } baseType && seen.Add(baseType.DefinitionId))
            {
                bases.Add(baseType.Definition);

                if (_types.TryGetValue(baseType.DefinitionId, out var local))
                {
                    pending.Enqueue(local);
                }
            }

            foreach (var iface in current.Interfaces)
            {
                if (iface.LinkTarget is { } target && seen.Add(target.DefinitionId))
                {
                    interfaces.Add(target.Definition);

                    if (_types.TryGetValue(target.DefinitionId, out var local))
                    {
                        pending.Enqueue(local);
                    }
                }
            }
        }

        return (bases, interfaces);
    }

    private NodeArray TypeRefs(IEnumerable<ApiTypeRef> types)
    {
        var array = new NodeArray();

        foreach (var type in types)
        {
            array.Add(TypeRef(type));
        }

        return array;
    }

    /// <summary>
    /// A type as a signature names it: its <c>name</c> (C#), <c>fullName</c>, <c>html</c> (its named types link to their
    /// pages), and the <c>id</c> and <c>url</c> of the type a link goes to.
    /// </summary>
    private Node TypeRef(ApiTypeRef type)
    {
        var name = CSharpWriter.Plain(type);
        var key = type.DocId() + "|" + name;

        if (_typeRefs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var html = new CSharpWriter { Links = x => Url(x.DefinitionId) };
        html.Write(type);

        var node = new NodeObject
        {
            ["name"] = name,
            ["fullName"] = CSharpWriter.Plain(type, qualified: true),
            ["html"] = html.ToString(),
        };

        if (type.LinkTarget is { } target)
        {
            node["id"] = target.DefinitionId;
            Set(node, "url", Url(target.DefinitionId));
        }

        _typeRefs[key] = node;
        return node;
    }

    /// <summary>
    /// The url of a documentation comment ID: its page here, or in the documentation of another library.
    /// </summary>
    private string? Url(string id)
    {
        if (_urls.TryGetValue(id, out var url))
        {
            return url;
        }

        if (id.Length < 3 || id[1] != ':')
        {
            return null;
        }

        var name = DocSet.NameOf(id[2..]);
        var typeName = id[0] is 'T' or 'N' ? name : name[..Math.Max(0, name.LastIndexOf('.'))];

        foreach (var external in _external)
        {
            if (typeName == external.Prefix || typeName.StartsWith(external.Prefix + ".", StringComparison.Ordinal))
            {
                var slug = id[0] is 'T' or 'N' ? Slug(name) : Slug(typeName) + "." + MemberSlug(StripArity(name[(name.LastIndexOf('.') + 1)..]).Replace('#', '.'));
                return external.Pattern.Replace("{slug}", slug, StringComparison.Ordinal).Replace("{name}", name, StringComparison.Ordinal);
            }
        }

        return null;
    }

    private (string Text, string? Url) ResolveCref(string cref)
    {
        if (cref.StartsWith("!:", StringComparison.Ordinal))
        {
            return (cref[2..], null);
        }

        if (_types.TryGetValue(cref, out var type))
        {
            return (type.DisplayName, _urls[cref]);
        }

        if (_members.TryGetValue(cref, out var member))
        {
            return (member.Kind == "constructor" ? member.DisplayName : member.DeclaringType.Name + "." + member.Name, _urls[cref]);
        }

        if (cref.StartsWith("N:", StringComparison.Ordinal))
        {
            return (cref[2..], Url(cref));
        }

        return (CrefText(cref), Url(cref));
    }

    /// <summary>
    /// The text of a link to a type or a member of another library: <c>List&lt;T&gt;</c>, <c>String.Join</c>.
    /// </summary>
    private string CrefText(string cref)
    {
        if (cref.Length < 3 || cref[1] != ':')
        {
            return cref;
        }

        var name = DocSet.NameOf(cref[2..]);

        if (cref[0] == 'T')
        {
            return TypeText(name, cref);
        }

        var dot = name.LastIndexOf('.');

        if (dot < 0)
        {
            return name;
        }

        var typeName = name[..dot];
        var typeText = TypeText(typeName, "T:" + typeName);
        var member = StripArity(name[(dot + 1)..]);
        return member == "#ctor" ? typeText : typeText + "." + member.Replace('#', '.');
    }

    private string TypeText(string name, string id)
    {
        var dot = name.LastIndexOf('.');
        var ns = dot < 0 ? "" : name[..dot];
        var (simple, arity) = NamedTypeRef.SplitArity(name[(dot + 1)..]);

        if (arity == 0)
        {
            return CSharpWriter.Keyword(NamedTypeRef.Create(ns, simple)) ?? simple;
        }

        // The names of the type parameters when the documentation of the type has them.
        var names = _docs.Find(id)?.Elements("typeparam").Select(x => (string?)x.Attribute("name")).OfType<string>().ToList();

        if (names == null || names.Count != arity)
        {
            names = arity == 1 ? ["T"] : [.. Enumerable.Range(1, arity).Select(x => "T" + x)];
        }

        return simple + "<" + string.Join(", ", names) + ">";
    }

    private static string StripArity(string member)
    {
        var tick = member.IndexOf("``", StringComparison.Ordinal);
        return tick < 0 ? member : member[..tick];
    }

    private static string GroupTitle(ApiMember member)
    {
        var typeName = member.DeclaringType.Name;

        return member.Kind switch
        {
            "constructor" => typeName + " constructor",
            "operator" => typeName + "." + member.Name.Replace("operator ", "", StringComparison.Ordinal) + " operator",
            var kind => typeName + "." + member.Name + " " + kind,
        };
    }

    /// <summary>
    /// A name as a url segment: lowercase, <c>`</c> and characters other than letters, digits, <c>.</c>, <c>_</c> and
    /// <c>-</c> written <c>-</c>.
    /// </summary>
    internal static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            var lower = char.ToLowerInvariant(c);
            builder.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? lower : '-');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The slug of a member name: <c>.ctor</c> is <c>-ctor</c>, the dots of an explicit implementation are dashes.
    /// </summary>
    private static string MemberSlug(string metadataName)
    {
        return Slug(metadataName.Replace('.', '-'));
    }

    private string Unique(string slug) => UniqueIn(_slugs, slug);

    private static string UniqueIn(HashSet<string> used, string slug)
    {
        if (used.Add(slug))
        {
            return slug;
        }

        for (var i = 2; ; i++)
        {
            var candidate = slug + "-" + i;

            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static NodeArray Strings(IEnumerable<string> values)
    {
        var array = new NodeArray();

        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static void Set(NodeObject node, string key, string? value)
    {
        if (value != null)
        {
            node[key] = value;
        }
    }
}
