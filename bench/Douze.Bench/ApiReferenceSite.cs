using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Douze.Bench;

/// <summary>
/// A site that renders a real-world API reference from its JSON data (<c>pkgs/*.json</c>, <c>ts-modules/*.json</c>)
/// with the routes of its documentation site: C# package, type and member kind pages, TypeScript module, item and method pages.
/// </summary>
/// <remarks>
/// The site builds with the default workflow: the JSON files are site data (<c>pkgs.&lt;slug&gt;</c>,
/// <c>tsmodules.&lt;slug&gt;</c>), each package folder has a page index as folder data (<c>pages.json</c>), and one
/// template per route paginates over it with a size of 1. Section 4.7 of the plan describes the workflow file version
/// (<c>parse</c> plus <c>liquid</c> with <c>group-by</c>), which needs phase 2.
/// </remarks>
internal static class ApiReferenceSite
{
    /// <summary>
    /// Writes the site to <paramref name="outDir"/>. With <paramref name="dataOnly"/>, the site has the data and a
    /// single page, which measures loading the data.
    /// </summary>
    public static void Generate(string dataDir, string outDir, bool dataOnly)
    {
        var data = Path.GetFullPath(dataDir);
        var root = Path.GetFullPath(outDir);
        var csharp = Files(Path.Combine(data, "pkgs"));
        var typescript = Files(Path.Combine(data, "ts-modules"));

        if (csharp.Length == 0 && typescript.Length == 0)
        {
            throw new BenchException($"no JSON files in '{data}/pkgs' or '{data}/ts-modules'; run bench/fetch-api-data.sh first");
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var nav = new JsonArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var packageSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in csharp)
        {
            var json = File.ReadAllBytes(file);
            var package = JsonNode.Parse(json)!.AsObject();
            var slug = Unique(packageSlugs, Slug((string)package["package"]!["name"]!));
            File.WriteAllBytes(Directory.CreateDirectory(Path.Combine(root, "_data/pkgs")).FullName + $"/{slug}.json", json);
            if (!dataOnly)
            {
                CSharpPackage(root, slug, package, counts);
            }

            nav.Add(new JsonObject { ["title"] = (string)package["package"]!["name"]!, ["url"] = $"/reference/api/csharp/{slug}/" });
        }

        var moduleSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in typescript)
        {
            var json = File.ReadAllBytes(file);
            var module = JsonNode.Parse(json)!.AsObject();
            var slug = Unique(moduleSlugs, Slug((string)module["package"]!["name"]!));
            File.WriteAllBytes(Directory.CreateDirectory(Path.Combine(root, "_data/tsmodules")).FullName + $"/{slug}.json", json);
            if (!dataOnly)
            {
                TypeScriptModule(root, slug, module, counts);
            }

            nav.Add(new JsonObject { ["title"] = $"{(string)module["package"]!["name"]!} (TypeScript)", ["url"] = $"/reference/api/typescript/{slug}/" });
        }

        SyntheticSite.Write(root, "_data/apinav.json", nav.ToJsonString());
        SyntheticSite.Write(root, "_data/layout.json", "\"api.html\"");
        WriteTemplates(root);

        if (dataOnly)
        {
            SyntheticSite.Write(root, "_content/index.html", "<p>{{ pkgs.size }} packages, {{ tsmodules.size }} modules</p>\n");
            Count(counts, "index", 1);
        }

        var total = counts.Values.Sum();
        Console.WriteLine($"API reference site with {total} pages written to {root}: {string.Join(", ", counts.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value}"))}");
    }

    private static string[] Files(string directory)
    {
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)]
            : [];
    }

    private static void CSharpPackage(string root, string slug, JsonObject package, Dictionary<string, int> counts)
    {
        var typePages = new JsonArray();
        var kindPages = new JsonArray();
        var typeSlugs = new HashSet<string>(StringComparer.Ordinal);
        var types = package["types"]?.AsArray() ?? [];

        for (var t = 0; t < types.Count; t++)
        {
            var type = types[t]!;
            var typeSlug = Unique(typeSlugs, Slug((string)type["name"]!));
            typePages.Add(new JsonObject { ["t"] = t, ["slug"] = typeSlug });

            var kinds = (type["members"]?.AsArray() ?? []).Select(x => (string)x!["kind"]!).Distinct().Order(StringComparer.Ordinal);

            foreach (var kind in kinds)
            {
                kindPages.Add(new JsonObject { ["t"] = t, ["slug"] = typeSlug, ["kind"] = kind });
            }
        }

        var folder = $"_content/reference/api/csharp/{slug}";
        var index = new JsonObject { ["pkgKey"] = slug, ["typePages"] = typePages, ["kindPages"] = kindPages };
        SyntheticSite.Write(root, $"{folder}/pages.json", index.ToJsonString());
        SyntheticSite.Write(root, $"{folder}/index.html", "{% include 'api/csharp-package' %}\n");
        SyntheticSite.Write(root, $"{folder}/type.html", Paginated("typePages", $"reference/api/csharp/{slug}/{{{{ p.slug }}}}/", "api/csharp-type"));
        SyntheticSite.Write(root, $"{folder}/kind.html", Paginated("kindPages", $"reference/api/csharp/{slug}/{{{{ p.slug }}}}/{{{{ p.kind }}}}/", "api/csharp-kind"));

        Count(counts, "csharp-package", 1);
        Count(counts, "csharp-type", typePages.Count);
        Count(counts, "csharp-kind", kindPages.Count);
    }

    private static void TypeScriptModule(string root, string slug, JsonObject module, Dictionary<string, int> counts)
    {
        var itemPages = new JsonArray();
        var memberPages = new JsonArray();
        var itemSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var list in new[] { "handleTypes", "dtoTypes", "enumTypes" })
        {
            var items = module[list]?.AsArray() ?? [];

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i]!;
                var itemSlug = Unique(itemSlugs, Slug((string)item["name"]!));
                itemPages.Add(new JsonObject { ["l"] = list, ["i"] = i, ["slug"] = itemSlug });

                if (list != "handleTypes")
                {
                    continue;
                }

                // Methods get their own page, like on the original site; properties are listed on the item page.
                var memberSlugs = new HashSet<string>(StringComparer.Ordinal);
                var capabilities = item["capabilities"]?.AsArray() ?? [];

                for (var c = 0; c < capabilities.Count; c++)
                {
                    if ((string?)capabilities[c]!["kind"] is "InstanceMethod" or "Method")
                    {
                        var memberSlug = Unique(memberSlugs, Slug((string)capabilities[c]!["name"]!));
                        memberPages.Add(new JsonObject { ["i"] = i, ["c"] = c, ["slug"] = $"{itemSlug}/{memberSlug}" });
                    }
                }
            }
        }

        var folder = $"_content/reference/api/typescript/{slug}";
        var index = new JsonObject { ["modKey"] = slug, ["itemPages"] = itemPages, ["memberPages"] = memberPages };
        SyntheticSite.Write(root, $"{folder}/pages.json", index.ToJsonString());
        SyntheticSite.Write(root, $"{folder}/index.html", "{% include 'api/ts-module' %}\n");
        SyntheticSite.Write(root, $"{folder}/item.html", Paginated("itemPages", $"reference/api/typescript/{slug}/{{{{ p.slug }}}}/", "api/ts-item"));
        SyntheticSite.Write(root, $"{folder}/member.html", Paginated("memberPages", $"reference/api/typescript/{slug}/{{{{ p.slug }}}}/", "api/ts-member"));

        Count(counts, "ts-module", 1);
        Count(counts, "ts-item", itemPages.Count);
        Count(counts, "ts-member", memberPages.Count);
    }

    private static string Paginated(string data, string permalink, string include)
    {
        return $"""
            ---
            pagination:
              data: {data}
              size: 1
              alias: p
            permalink: "{permalink}"
            ---
            {"{%"} include '{include}' {"%}"}

            """;
    }

    private static void WriteTemplates(string root)
    {
        SyntheticSite.Write(root, "_includes/api.html", """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>{{ title }} - API reference</title>
              </head>
              <body>
                <nav class="sidebar">
                  <ul>{% for item in apinav %}<li><a href="{{ item.url }}">{{ item.title }}</a></li>{% endfor %}</ul>
                </nav>
                <main>{{ content | raw }}</main>
              </body>
            </html>
            """);

        // Doc comments are lists of nodes: text, paramref, see, code, para (with children).
        SyntheticSite.Write(root, "_includes/api/doc.liquid", """
            {%- for n in doc -%}
              {%- case n.kind -%}
                {%- when 'para' -%}<p>{% for c in n.children %}{% if c.text %}{{ c.text }}{% else %}<code>{{ c.value }}</code>{% endif %}{% endfor %}</p>
                {%- when 'text' -%}{{ n.text }}
                {%- else -%}<code>{{ n.value }}{{ n.text }}</code>
              {%- endcase -%}
            {%- endfor -%}
            """);

        SyntheticSite.Write(root, "_includes/api/csharp-package.liquid", """
            {%- assign pkg = pkgs[pkgKey] -%}
            {%- capture title %}{{ pkg.package.name }}{% endcapture -%}
            <h1>{{ pkg.package.name }} <small>{{ pkg.package.version }}</small></h1>
            <p>Target framework: {{ pkg.package.targetFramework }}</p>
            <table>
            {%- for page in typePages -%}
              {%- assign type = pkg.types[page.t] %}
              <tr><td><a href="{{ page.slug }}/">{{ type.name }}</a></td><td>{{ type.kind }}</td><td>{% assign doc = type.docs.summary %}{% include 'api/doc' %}</td></tr>
            {%- endfor %}
            </table>
            """);

        SyntheticSite.Write(root, "_includes/api/csharp-type.liquid", """
            {%- assign pkg = pkgs[pkgKey] -%}
            {%- assign type = pkg.types[p.t] -%}
            {%- capture title %}{{ type.name }}{% endcapture -%}
            <h1>{{ type.name }} <small>{{ type.kind }}</small></h1>
            <p>Namespace {{ type.namespace }}, package {{ pkg.package.name }} {{ pkg.package.version }}</p>
            <p><a href="{{ pkg.package.sourceRepository }}/blob/{{ pkg.package.sourceCommit }}/{{ type.sourceFile }}">{{ type.sourceFile }}</a> ({{ type.sourceLines }})</p>
            <div class="summary">{% assign doc = type.docs.summary %}{% include 'api/doc' %}</div>
            <div class="remarks">{% assign doc = type.docs.remarks %}{% include 'api/doc' %}</div>
            <h2>Members</h2>
            <table>
            {%- for m in type.members %}
              <tr><td><a href="{{ m.kind }}/">{{ m.name }}</a></td><td><code>{{ m.signature }}</code></td><td>{% assign doc = m.docs.summary %}{% include 'api/doc' %}</td></tr>
            {%- endfor %}
            </table>
            """);

        SyntheticSite.Write(root, "_includes/api/csharp-kind.liquid", """
            {%- assign pkg = pkgs[pkgKey] -%}
            {%- assign type = pkg.types[p.t] -%}
            {%- capture title %}{{ type.name }} {{ p.kind }}{% endcapture -%}
            <h1>{{ type.name }} {{ p.kind }}</h1>
            {%- for m in type.members %}
              {%- if m.kind == p.kind %}
              <section>
                <h2>{{ m.name }}</h2>
                <pre><code>{{ m.signature }}</code></pre>
                <div class="summary">{% assign doc = m.docs.summary %}{% include 'api/doc' %}</div>
                <div class="remarks">{% assign doc = m.docs.remarks %}{% include 'api/doc' %}</div>
                {%- if m.parameters.size > 0 %}
                <h3>Parameters</h3>
                <dl>{% for param in m.parameters %}<dt>{{ param.name }} <code>{{ param.type }}</code></dt><dd>{% assign doc = m.docs.parameters[param.name] %}{% include 'api/doc' %}</dd>{% endfor %}</dl>
                {%- endif %}
                <p>Returns <code>{{ m.returnType }}</code></p>
              </section>
              {%- endif %}
            {%- endfor %}
            """);

        SyntheticSite.Write(root, "_includes/api/ts-module.liquid", """
            {%- assign mod = tsmodules[modKey] -%}
            {%- capture title %}{{ mod.package.name }}{% endcapture -%}
            <h1>{{ mod.package.name }} <small>{{ mod.package.version }}</small></h1>
            <h2>Types</h2>
            <ul>
            {%- for page in itemPages -%}
              {%- assign list = mod[page.l] -%}
              {%- assign item = list[page.i] %}
              <li><a href="{{ page.slug }}/">{{ item.name }}</a> {{ item.kind }}: {{ item.description }}</li>
            {%- endfor %}
            </ul>
            <h2>Functions</h2>
            <ul>{% for f in mod.functions %}<li><code>{{ f.signature }}</code></li>{% endfor %}</ul>
            """);

        SyntheticSite.Write(root, "_includes/api/ts-item.liquid", """
            {%- assign mod = tsmodules[modKey] -%}
            {%- assign list = mod[p.l] -%}
            {%- assign item = list[p.i] -%}
            {%- capture title %}{{ item.name }}{% endcapture -%}
            <h1>{{ item.name }} <small>{{ item.kind }}</small></h1>
            <p>{{ item.fullName }}</p>
            <p>{{ item.description }}</p>
            <p>{{ item.remarks }}</p>
            {%- if item.capabilities %}
            <table>
            {%- for c in item.capabilities %}
              <tr><td>{{ c.name }}</td><td>{{ c.kind }}</td><td><code>{{ c.signature }}</code></td><td>{{ c.description }}</td></tr>
            {%- endfor %}
            </table>
            {%- endif %}
            {%- if item.fields %}
            <dl>{% for f in item.fields %}<dt>{{ f.name }}</dt><dd><code>{{ f.type }}</code> {{ f.description }}</dd>{% endfor %}</dl>
            {%- endif %}
            {%- if item.members %}
            <ul>{% for m in item.members %}<li>{{ m }}</li>{% endfor %}</ul>
            {%- endif %}
            """);

        SyntheticSite.Write(root, "_includes/api/ts-member.liquid", """
            {%- assign mod = tsmodules[modKey] -%}
            {%- assign item = mod.handleTypes[p.i] -%}
            {%- assign c = item.capabilities[p.c] -%}
            {%- capture title %}{{ item.name }}.{{ c.name }}{% endcapture -%}
            <h1>{{ item.name }}.{{ c.name }}</h1>
            <pre><code>{{ c.signature }}</code></pre>
            <p>{{ c.description }}</p>
            {%- if c.parameters.size > 0 %}
            <dl>{% for param in c.parameters %}<dt>{{ param.name }}</dt><dd><code>{{ param.type }}</code> {{ param.description }}</dd>{% endfor %}</dl>
            {%- endif %}
            <p>Returns <code>{{ c.returnType }}</code>{% if c.returnsBuilder %} (builder){% endif %}</p>
            """);
    }

    private static void Count(Dictionary<string, int> counts, string key, int value) => counts[key] = counts.GetValueOrDefault(key) + value;

    /// <summary>
    /// Lower case, letters, digits, dots and dashes: <c>Contoso.Hosting</c> is <c>contoso.hosting</c>,
    /// <c>Foo&lt;T&gt;</c> is <c>foo-t</c>.
    /// </summary>
    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    private static string Unique(HashSet<string> used, string slug)
    {
        var candidate = slug;

        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{slug}-{i}");
        }

        return candidate;
    }
}
