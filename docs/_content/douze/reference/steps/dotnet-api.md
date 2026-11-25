---
title: dotnet-api
description: "Reads .NET assemblies and their XML documentation into site data for API reference pages."
liquid: false
---
The `dotnet-api` step reads .NET assemblies (`.dll`) and their XML documentation files, and publishes their public API
as site data: assemblies, namespaces, types and members, each with its url, its C# signature and its documentation as
HTML. It emits no files. Pages [paginate](paginate.md) over `api.types` or `api.namespaces`, so the site's own
templates decide what an API page shows. [Write an API reference for a .NET library](../../recipes/dotnet-api-reference.md)
has a complete site.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | `**/*.{dll,xml}` |
| Default workflow | no |

## What it does

The step reads the metadata of each assembly, without loading or running it: a reference assembly works as well as
the assembly of a build. The API is what another assembly can use: the public types and their public and protected
members (`protected: false` leaves the protected ones out). Internal and private members, compiler-generated members
and the protected members of a sealed class or a struct are left out.

From the metadata, the step reads what the XML documentation file does not have: the kind of each type, its
modifiers, base type and interfaces, type parameters and their constraints, parameters with their modifiers and default
values, constants and enum values, `[Obsolete]`, extension methods, explicit interface implementations and nullable
reference types. It joins them to the comments of the documentation by their documentation comment ID
(`M:Contoso.Widgets.Widget.Resize(System.Int32,System.Int32)`).

### Documentation files

The XML documentation file of `Contoso.Widgets.dll` is `Contoso.Widgets.xml` in the same folder, as the compiler writes
it with `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. An assembly without one is listed without
documentation, with a warning. Other `.xml` files the step consumes are documentation too: they document what the
assemblies inherit, for instance the `System.Runtime.xml` of the .NET reference pack for the members that override
`object.Equals`.

A comment's elements are turned into HTML:

| Element | HTML |
|---|---|
| `<para>` | `<p>` |
| `<c>` | `<code>` |
| `<code>` | `<pre data-lang="csharp"><code class="language-csharp">`, without the common indentation; `language="..."` sets the language. The [highlight](highlight.md) step colors it like a Markdown code block |
| `<see cref="..."/>`, `<seealso cref="..."/>` | A link to the page of the type or member, with its name in `<code>`; `<see cref="...">text</see>` links the text |
| `<see href="...">`, `<seealso href="...">` | A link to the url |
| `<see langword="null"/>` | `<code>null</code>` |
| `<paramref>`, `<typeparamref>` | `<code>` with the name |
| `<list type="bullet">`, `<list type="number">` | `<ul>`, `<ol>`; an item with a `term` and a `description` is `<strong>term</strong> – description` |
| `<list type="table">` | `<table>`, `<listheader>` as `<thead>` |
| `<b>`, `<i>`, `<a>`, `<br>`, `<ul>`, `<table>`... | Kept as they are |

`<inheritdoc/>` takes the comment of the member that a member overrides (from the base types, `object` last) or
implements (from the interfaces), or of its `cref`. The elements of the member's own comment win: `<inheritdoc/>` with
a `<remarks>` keeps the remarks.

A namespace is documented by a `NamespaceDoc` class in it, as with Sandcastle and DocFX: the class is internal, and its
`<summary>` is the namespace's.

### Urls

Every item has a url under `url` (`/api/` by default), made from its name in lowercase, a type's arity written `-1`:

| Item | Url |
|---|---|
| Namespace `Contoso.Widgets` | `/api/contoso.widgets/` |
| Type `Contoso.Widgets.Widget` | `/api/contoso.widgets.widget/` |
| Type `Contoso.Widgets.Bag<T>.Entry<TKey>` | `/api/contoso.widgets.bag-1.entry-1/` |
| Member `Widget.Tag` | `/api/contoso.widgets.widget/#tag` |
| Overload `Widget.Resize(int, int)` | `/api/contoso.widgets.widget/#resize-system.int32-system.int32` |

A member is an anchor of its type's page; with `member-pages: true` it is an anchor of the page of its name instead
(`/api/contoso.widgets.widget.resize/#resize-system.int32-system.int32`, `/api/contoso.widgets.widget.-ctor/` for the
constructors), so that each name can have its own page; the values of an enum stay anchors of its page. Two items whose
slugs are the same get `-2`, `-3`...

A type of another library links to its documentation when its namespace is a key of `external`. By default, `System`
and `Microsoft` link to `https://learn.microsoft.com/dotnet/api/{slug}`, the url scheme of the .NET documentation:
`string` links to `.../system.string`, `List<T>` to `.../system.collections.generic.list-1`.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `as` | string | `api` | The name of the site value |
| `url` | string | `/api/` | The url the urls of the items start with |
| `member-pages` | boolean | `false` | Members are anchors of the page of their name (`api.members`) instead of the page of their type |
| `protected` | boolean | `true` | Whether protected members are part of the API |
| `external` | mapping | `System` and `Microsoft` to learn.microsoft.com | Namespaces of other libraries and the url of their items: `{slug}` is the item's slug (`system.string.join`), `{name}` its name (`System.String.Join`). A namespace is the key or under it; the longest key wins. `{}` links nothing |

## Data

`api` (or the name of `as`):

| Name | Value |
|---|---|
| `url` | The `url` option |
| `assemblies` | Each assembly: `name`, `version`, `informationalVersion`, `targetFramework` (`.NETCoreApp,Version=v10.0`) and `namespaces`, their names |
| `namespaces` | Each namespace, sorted by name |
| `types` | Each type, sorted by documentation comment ID: what a page paginates over |
| `members` | Each member name of each type but the values of enums, with its overloads: what a page per member paginates over |
| `ids` | Namespaces, types and members by documentation comment ID: `api.ids["T:Contoso.Widgets.Widget"].url` |

A namespace:

| Name | Value |
|---|---|
| `kind` | `namespace` |
| `id`, `name`, `url`, `title` | `N:Contoso.Widgets`, `Contoso.Widgets`, its url, `Contoso.Widgets namespace` |
| `summary`, `remarks` | HTML, from its `NamespaceDoc` class |
| `assemblies` | The names of the assemblies with types in it |
| `types` | Its types, nested types included, sorted by name |

A type:

| Name | Value |
|---|---|
| `kind` | `class`, `record`, `struct`, `record struct`, `interface`, `enum` or `delegate` |
| `id` | ``T:Contoso.Widgets.Bag`1`` |
| `name`, `displayName`, `fullName` | `Entry<TKey>`, `Bag<T>.Entry<TKey>` (with its declaring types), `Contoso.Widgets.Bag<T>.Entry<TKey>` |
| `title` | `Bag<T>.Entry<TKey> class` |
| `namespace`, `namespaceUrl`, `assembly` | The name and url of its namespace, the name of its assembly |
| `url` | Its url |
| `accessibility`, `modifiers` | `public`, `protected` or `protected internal`; `static`, `abstract`, `sealed`, `readonly`, `ref` |
| `signature` | Its C# declaration: `public class Widget : WidgetBase, IShape` |
| `baseType`, `interfaces` | Type references |
| `inheritance` | The base types from `object` to the direct base, type references |
| `derivedTypes`, `nestedTypes`, `declaringType` | Type references |
| `underlyingType` | The type of an enum, when it is not `int` |
| `typeParameters` | Type parameters |
| `parameters`, `returns` | Of a delegate |
| `constructors`, `properties`, `methods`, `events`, `fields`, `operators` | Its members, sorted by name then overload; explicit interface implementations last; an enum's values are its `fields` |
| `obsolete` | `{ message }` when it is `[Obsolete]` |
| documentation | `summary`, `remarks`, `example`, `exceptions`, `seeAlso` |

A member:

| Name | Value |
|---|---|
| `kind` | `constructor`, `property`, `method`, `event`, `field` or `operator` |
| `id` | `M:Contoso.Widgets.Widget.Resize(System.Int32,System.Int32)` |
| `name` | `Resize`, the type's name for a constructor, `this[]` for an indexer, `operator +`, `IDisposable.Dispose` |
| `displayName` | With the parameter types: `Resize(int, int)`, `this[int]` |
| `title` | `Widget.Resize method`, `Widget constructor` |
| `url`, `anchor` | Its url, and the id of its element on the page |
| `accessibility`, `modifiers` | As for a type (empty for an explicit implementation); `static`, `virtual`, `abstract`, `override`, `sealed override`, `const`, `readonly`, `required` |
| `signature` | `public bool Resize(int width, int height)`, `public int Width { get; protected set; }` |
| `declaringType` | A type reference |
| `typeParameters`, `parameters` | Of a method, or an indexer |
| `returns` | Of a method or an operator that returns a value: `type`, `modifier` (`ref`, `ref readonly`), `description` |
| `type` | The type of a property, an event or a field |
| `value` | The value of a constant or of an enum value: `"widget"`, `1` |
| `valueDescription` | The `<value>` of a property, HTML |
| `isExtension`, `isOverride`, `isExplicit` | `true` for an extension method, an override, an explicit interface implementation |
| `explicitInterface` | The interface of an explicit implementation, a type reference |
| `obsolete` | `{ message }` |
| documentation | `summary`, `remarks`, `example`, `exceptions`, `seeAlso` |

An item of `members`: `kind`, `name`, `title`, `url` (the page of the name, or the first overload without
`member-pages`), `type` (the declaring type, a type reference) and `overloads`, its members.

Parameters: `name`, `type` (a type reference), `modifier` (`ref`, `out`, `in`, `ref readonly`, `params`, `this`),
`defaultValue` (`"widget"`, `Color.Red`, `null`, `default`) and `description`. Type parameters: `name`, `variance`
(`in`, `out`), `constraints` (`class, new()`) and `description`.

A type reference is a type as a signature writes it:

| Name | Value |
|---|---|
| `name` | `IEnumerable<KeyValuePair<string, int>>`, `int[]`, `T?` |
| `fullName` | With namespaces: `System.Collections.Generic.IEnumerable<...>` |
| `html` | `name`, each type linking to its page: `<a href="...">IEnumerable</a>&lt;...&gt;` |
| `id`, `url` | The documentation comment ID and url of the type a link goes to (``T:System.Collections.Generic.IEnumerable`1``); no `url` without a page |

The documentation of a type or a member: `summary`, `remarks` and `example` (HTML), `exceptions` (each a `type`, a link,
and a `description`) and `seeAlso` (links). A link is `name`, `url`, `id` (for a `cref`) and `html`, the link as HTML.
A name the comment does not have is not set: `{% if member.remarks %}`.

The documentation is HTML that Liquid would encode: write it with `raw`, `{{ type.summary | raw }}`.

## Errors

Validation errors stop the build before it starts (exit code 2):

```text
douze.yml(2,3): error: option 'external' of step 'api' must be a mapping of namespaces to url patterns
douze.yml(2,3): error: option 'as' of step 'api' must not be empty
```

Build errors (exit code 1):

```text
error: api: '/_api/Bad.dll' is not a .NET assembly: Image is too small.
error: api: '/_api/other.xml' is not an XML documentation file: its root element is not <doc>
error: api: the assembly 'Contoso.Widgets' is read from both '/_api/net8.0/Contoso.Widgets.dll' and '/_api/net10.0/Contoso.Widgets.dll': give the step one of them (a target framework)
```

Warnings:

```text
warning: api: '/_api/Contoso.Widgets.dll' has no XML documentation file next to it ('/_api/Contoso.Widgets.xml'): its API is listed without documentation
```

## Example

```yaml title="douze.yml"
steps:
  api:
    inputs: "_api/*.{dll,xml}"
    uses: dotnet-api
  pages:
    inputs: "_content/**"
    needs: api
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  paginate:
    needs: permalink
    uses: paginate
    with:
      root: _content
  render:
    needs: paginate
    uses: liquid
  write:
    needs: render
    uses: write
```

`_content/api/type.html` writes a page per type:

```liquid
---
pagination:
  data: api.types
  size: 1
  alias: type
permalink: "{{ type.url }}"
---
<h1>{{ type.title }}</h1>
{{ type.summary | raw }}
<pre data-lang="csharp"><code>{{ type.signature }}</code></pre>
{% for method in type.methods %}
<h2 id="{{ method.anchor }}">{{ method.displayName }}</h2>
{{ method.summary | raw }}
{% endfor %}
```

## Related

- [Write an API reference for a .NET library](../../recipes/dotnet-api-reference.md)
- [paginate](paginate.md)
- [highlight](highlight.md): colors the signatures and the examples
- [Template data](../template-data.md)
