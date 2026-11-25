---
title: Write an API reference for a .NET library
description: Generate a page per namespace and per type of a .NET library from its assembly and XML documentation, with templates of your own.
liquid: false
---
The [dotnet-api](../reference/steps/dotnet-api.md) step reads a .NET assembly and its XML documentation file and
publishes the library's API as site data, `api`. Pages paginate over it: one page per namespace and one per type, laid
out by templates of the site that you change like any other page.

## Get the assembly and its documentation

The compiler writes the XML documentation file when the project of the library asks for it:

```xml title="Contoso.Widgets.csproj"
<PropertyGroup>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
</PropertyGroup>
```

Douze reads the files of the site's folder: copy the assembly and its documentation into it after the build, here to
`_api/`, which is not part of the content and is not written to the output.

```bash
dotnet build src/Contoso.Widgets -c Release
mkdir -p _api && cp src/Contoso.Widgets/bin/Release/net10.0/Contoso.Widgets.{dll,xml} _api/
```

A reference assembly works too (`obj/Release/net10.0/ref/Contoso.Widgets.dll`): the step only reads metadata. Give the
step one target framework of a library that has several: two assemblies with the same name are an error.

## Add the step to the workflow

Start from the default workflow (`douze --print-workflow`), add the `api` step, and have `pages` need it so that pages
see `api`. The `highlight` step colors the signatures and the code examples:

```yaml title="douze.yml"
steps:
  site-data:   { inputs: "_data/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: site } }
  folder-data: { inputs: "_content/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: folder, root: _content } }
  api:         { inputs: "_api/*.{dll,xml}", uses: dotnet-api }
  pages:       { inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"], needs: [site-data, folder-data, api], uses: frontmatter }
  permalink:   { needs: pages, uses: permalink, with: { root: _content } }
  collections: { needs: permalink, uses: collect }
  paginate:    { needs: [permalink, collections], uses: paginate }
  render:      { needs: paginate, uses: liquid }
  markdown:    { needs: render, uses: markdown }
  highlight:   { needs: markdown, uses: highlight, with: { stylesheet: assets/code.css } }
  layout:      { needs: highlight, uses: layout }
  write:       { needs: layout, uses: write }
```

Every namespace, type and member has a `url` under `/api/` (the `url` option changes it): pages use it as their
`permalink`, and the links of the documentation point to it.

## Write the pages

The index lists the namespaces, `_content/api/index.html`:

```liquid
---
layout: api/layout
title: API reference
---
<h1>API reference</h1>
{% for assembly in api.assemblies %}
<p>{{ assembly.name }} {{ assembly.informationalVersion | default: assembly.version }}</p>
{% endfor %}
<ul>
{% for ns in api.namespaces %}
  <li><a href="{{ ns.url }}">{{ ns.name }}</a>{% if ns.summary %}: {{ ns.summary | raw }}{% endif %}</li>
{% endfor %}
</ul>
```

A page per namespace lists its types by kind, `_content/api/namespace.html`:

```liquid
---
layout: api/layout
pagination:
  data: api.namespaces
  size: 1
  alias: ns
permalink: "{{ ns.url }}"
---
<h1>{{ ns.title }}</h1>
{{ ns.summary | raw }}
{% assign kinds = "class,record,struct,record struct,interface,enum,delegate" | split: "," %}
{% for kind in kinds %}
  {% assign types = ns.types | where: "kind", kind %}
  {% if types.size > 0 %}
  <h2>{{ kind | capitalize }}</h2>
  <table>
    {% for t in types %}<tr><td><a href="{{ t.url }}">{{ t.displayName }}</a></td><td>{{ t.summary | raw }}</td></tr>{% endfor %}
  </table>
  {% endif %}
{% endfor %}
```

A page per type, `_content/api/type.html`, renders an include:

```liquid
---
layout: api/layout
pagination:
  data: api.types
  size: 1
  alias: type
permalink: "{{ type.url }}"
---
{% include 'api/type' %}
```

## Write the templates

The type, `_includes/api/type.liquid`: its declaration, base types, documentation, then its members by kind.

```liquid
<p>Namespace: <a href="{{ type.namespaceUrl }}">{{ type.namespace }}</a> · Assembly: {{ type.assembly }}</p>
<h1>{{ type.title }}</h1>
{% if type.obsolete %}<p class="obsolete">Obsolete. {{ type.obsolete.message }}</p>{% endif %}
{{ type.summary | raw }}
<pre data-lang="csharp"><code>{{ type.signature }}</code></pre>
{% if type.inheritance.size > 0 %}
<p>Inheritance: {% for t in type.inheritance %}{{ t.html | raw }} → {% endfor %}{{ type.name }}</p>
{% endif %}
{% if type.interfaces.size > 0 %}
<p>Implements: {% for t in type.interfaces %}{{ t.html | raw }}{% unless forloop.last %}, {% endunless %}{% endfor %}</p>
{% endif %}
{% if type.typeParameters.size > 0 %}
<h2>Type parameters</h2>
<dl>{% for p in type.typeParameters %}<dt><code>{{ p.name }}</code></dt><dd>{{ p.description | raw }}</dd>{% endfor %}</dl>
{% endif %}
{% if type.remarks %}<h2>Remarks</h2>{{ type.remarks | raw }}{% endif %}
{% if type.example %}<h2>Examples</h2>{{ type.example | raw }}{% endif %}
{% assign sections = "constructors:Constructors,properties:Properties,methods:Methods,events:Events,fields:Fields,operators:Operators" | split: "," %}
{% for section in sections %}
  {% assign parts = section | split: ":" %}
  {% assign members = type[parts[0]] %}
  {% if members.size > 0 %}
  <h2>{{ parts[1] }}</h2>
  {% for member in members %}{% include 'api/member', member: member %}{% endfor %}
  {% endif %}
{% endfor %}
{% if type.seeAlso.size > 0 %}
<h2>See also</h2>
<ul>{% for link in type.seeAlso %}<li>{{ link.html | raw }}</li>{% endfor %}</ul>
{% endif %}
```

A member, `_includes/api/member.liquid`. Its `anchor` is the id that its `url` points to:

```liquid
<section id="{{ member.anchor }}">
  <h3>{{ member.displayName }}</h3>
  {% if member.obsolete %}<p class="obsolete">Obsolete. {{ member.obsolete.message }}</p>{% endif %}
  {{ member.summary | raw }}
  <pre data-lang="csharp"><code>{{ member.signature }}</code></pre>
  {% if member.parameters.size > 0 %}
  <h4>Parameters</h4>
  <dl>{% for p in member.parameters %}<dt><code>{{ p.name }}</code> {{ p.type.html | raw }}</dt><dd>{{ p.description | raw }}</dd>{% endfor %}</dl>
  {% endif %}
  {% if member.returns %}<h4>Returns</h4><p>{{ member.returns.type.html | raw }}</p>{{ member.returns.description | raw }}{% endif %}
  {% if member.valueDescription %}<h4>Property value</h4>{{ member.valueDescription | raw }}{% endif %}
  {% if member.exceptions.size > 0 %}
  <h4>Exceptions</h4>
  <dl>{% for e in member.exceptions %}<dt>{{ e.type.html | raw }}</dt><dd>{{ e.description | raw }}</dd>{% endfor %}</dl>
  {% endif %}
  {% if member.remarks %}<h4>Remarks</h4>{{ member.remarks | raw }}{% endif %}
</section>
```

The layout, `_includes/api/layout.liquid`, has a navigation of every type. A paginated page has its item in its data,
so the title is the type's or the namespace's:

```liquid
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>{{ type.title | default: ns.title | default: title }}</title>
  <link rel="stylesheet" href="/assets/code.css">
</head>
<body>
  <nav>
    {% for n in api.namespaces %}
    <h3><a href="{{ n.url }}">{{ n.name }}</a></h3>
    <ul>{% for t in n.types %}<li><a href="{{ t.url }}">{{ t.displayName }}</a></li>{% endfor %}</ul>
    {% endfor %}
  </nav>
  <main>{{ content }}</main>
</body>
</html>
```

The build writes `_site/api/index.html`, `_site/api/contoso.widgets/index.html` for the namespace and
`_site/api/contoso.widgets.widget/index.html` for the type `Contoso.Widgets.Widget`.

- The summaries, remarks and descriptions are HTML: write them with `raw`. A `<see cref="..."/>` is a link to the
  page of its type or member, or to learn.microsoft.com for the types of .NET (`external` sets other libraries).
- `html` of a type reference links each type of a signature: `Func<Widget, T>` links `Func` and `Widget`.
- A name the documentation does not have is not set: `{% if member.remarks %}` leaves the heading out.
- [The step's reference](../reference/steps/dotnet-api.md#data) lists every value.

## Link to the API from other pages

`api.ids` has every namespace, type and member by documentation comment ID, the ID that `cref` uses:

```liquid
Start with the [Widget]({{ api.ids["T:Contoso.Widgets.Widget"].url }}) class.
```

## Give each member a page

With `member-pages: true`, a member's url is on the page of its name (`/api/contoso.widgets.widget.resize/`), and
`api.members` has each name of each type with its overloads. A page paginates over it, and renders the member include
for each overload:

```liquid
---
layout: api/layout
pagination:
  data: api.members
  size: 1
  alias: group
permalink: "{{ group.url }}"
---
<h1>{{ group.title }}</h1>
<p>Declared by {{ group.type.html | raw }}</p>
{% for member in group.overloads %}{% include 'api/member', member: member %}{% endfor %}
```

The type page then lists its members with a link to their page: `<a href="{{ member.url }}">{{ member.displayName }}</a>`.

## Document what the library inherits

`<inheritdoc/>` on an override of a .NET member (`ToString`, `Equals`) needs the documentation of .NET. Copy the XML
documentation file of the assembly that declares it from the reference pack of the SDK, for instance
`packs/Microsoft.NETCore.App.Ref/<version>/ref/net10.0/System.Runtime.xml`, to `_api/`: the step reads every `.xml` file
it consumes as documentation, and only the `.dll` files as API.

## See also

- [dotnet-api](../reference/steps/dotnet-api.md)
- [paginate](../reference/steps/paginate.md)
- [highlight](../reference/steps/highlight.md)
