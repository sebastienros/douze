# Douze site features: implementation plan

This plan adds to the workflow engine of `docs/workflow-engine-plan.md` everything a large documentation site needs
beyond the engine: component syntax in Markdown, a configurable Markdown pipeline, headings and tables of contents,
Markdown includes, navigation data, locales, code highlighting, images, asset fingerprinting, git dates, link
validation, feeds and text outputs, Markdown twins of pages, redirects, search, and the watch mode and dev server
that the engine plan left as its phase 6. It is written like the engine plan: for implementers who execute one phase
at a time without making architecture decisions. Every type, option, file and message below is a decision; what is
left open on purpose is in section 7 with a recommended answer, which is what an implementer uses.

The target is the documentation site of the engine plan: about 32k pages, of which about 24k are an API reference
rendered from a real-world data set, and about 8k are articles in 15 locales (most of them English fallbacks of
untranslated pages), written in a component Markdown dialect (MDX) today, with build-time syntax highlighting of
about 6,300 code fences (400 of them with type hover information), about 540 generated social images, a search index,
feeds, text exports for language models, a Markdown twin of every page and about 180 redirects. The engine renders its
API pages in seconds (`bench/README.md`); what remains is the article pipeline and the site-wide outputs.

Inputs read while planning: `AGENTS.md`, `README.md`, `docs/workflow-engine-plan.md`, `bench/README.md`, every file
under `Douze/` and `Douze.Tests/`, the Fluid 3.0.0-preview-781 API surface (`RegisterParserBlock`,
`RegisterEmptyBlock`, `RenderStatementsAsync`, `IFluidArray`, `TemplateOptionsBuilder`), the Markdig 1.4 API, the
sources of the target site (its components, their usage counts, its remark and rehype plugins, its build and hosting),
and an empirical check of NetVips under the native AOT publish (section 3.8).

How to read this document:

- Section 1 is the scope and the rules every phase follows, and the rule that decides where a feature lives.
- Section 2 is the architecture: new folders, the engine additions every feature builds on, with C# signatures.
- Section 3 is one subsection per feature: design, signatures, files, tests, acceptance criteria.
- Section 4 is the `douze.yml` additions. Section 5 is the phases, their ownership, model and effort, and which
  phases run in parallel. Section 6 is the execution roadmap across this repository and a site that adopts Douze.
  Section 7 lists the risks and open questions.

Conventions: everything is `internal`; `ct` is a `CancellationToken`; namespaces follow folders. "Own" means
`file.Metadata.Own`. A feature "resolves" a key when it reads it through `ScopeSet.TryResolve` (or a template reads it
through `MetadataValue`), which records the dependency for the cache.

---

## 1. Scope and rules

### 1.1 Goals

1. Markdown pages can use components: paired block tags with a Markdown body (`{% tabs %}...{% endtabs %}`) and
   inline tags (`{% badge "New" %}`), each backed by a Liquid file, with nesting, so that a site written in MDX can be
   converted by a script to Markdown plus tags with no loss of structure.
2. A configurable Markdown pipeline with the extensions documentation needs (tables, footnotes, attributes, task
   lists, alerts and admonition containers rendered as one markup, fenced code with its meta string, link rewriting),
   headings with stable ids and a table of contents per page, and Markdown includes.
3. Navigation and locales as data: a sidebar tree with the active trail, previous and next links, breadcrumbs; locale
   folders with a prefix-less root locale, UI strings, fallback pages for untranslated paths, alternates for `hreflang`.
4. Build-time syntax highlighting with the features of the target site's highlighter (frames, titles, line markers,
   collapsible sections, type hovers), through a command step contract, since the grammars, themes and the TypeScript
   compiler those features need exist in JavaScript only.
5. Images resized and converted at build time, static assets fingerprinted, dates from git, broken links reported as
   build errors, feeds, sitemaps and text exports written from templates, a Markdown twin per page, redirect stubs,
   and the search index of an external indexer run after `write`.
6. `douze watch` and `douze serve` (engine plan phase 6), with command steps and workers that survive rebuilds.
7. Every feature is incremental (its units replay from the cache when nothing they read changed), deterministic, and
   works under the native AOT publish, with and without the Satori GC.

Non-goals: a CSS engine (atomic CSS, bundling, minification: command steps run the site's tools), a JavaScript
bundler, a client-side framework, a CMS, server-side rendering, a plugin API loaded at run time (a native AOT binary
cannot load code; extension is by command steps). Nothing in this plan names the target site: it is "the target site"
or "a documentation site".

### 1.2 Where a feature lives

Each feature is one of:

- **Built-in step** (`uses:`): work with the shape of a step (one unit per file, per group or per build) whose
  inputs and outputs are files and scopes. The step records what it reads, so it replays from the cache.
- **Liquid tag or filter**: work that happens inside a render and whose result is text in the page. A tag or filter
  reads data through the template's model (recorded) and can add outputs to its unit (section 2.2).
- **Markdig extension**: parsing or rendering of Markdown syntax.
- **Fluid change**: when the cleanest solution is in the template engine (`AGENTS.md`, "Fluid changes").
- **Command step contract**: Douze defines the markup or the protocol, the site runs a tool (Node or anything else)
  through `run:` in `filter`, `batch` or `worker` mode. Chosen when the feature needs an ecosystem that only exists
  in JavaScript (TextMate grammars and themes, the TypeScript compiler, a layout engine for social images, a search
  indexer, a CSS engine), when parity with a JavaScript tool's output is the requirement, or when the C# port would
  be a large one-off with no other user.

The decision for each feature, with the reason, is in its subsection and summarized in section 5.4.

### 1.3 Rules for every phase

The rules of the engine plan (section 1 and 8.0) apply: `dotnet test` passes, the Sample output is unchanged or every
difference is intentional and documented, the native AOT publish and smoke test pass with no new IL warnings beyond
those suppressed in `Douze.csproj`, `AGENTS.md` and `README.md` are updated with every convention or option, and the
engine's invariants hold: `VirtualFile` is immutable, nodes are never changed once published, scopes are read through
`TryResolve` only, everything collected across files is sorted before it is exposed, nothing is reflection based that
the AOT compiler cannot see. In addition:

- A feature that renders HTML from Markdown uses the site's Markdown pipeline (`BuildContext.Markdown`, 2.3), never a
  second configuration, so a body rendered inside a tag and a page render the same.
- A feature that reads a file of the source tree outside its unit's inputs (an image referenced by a tag, a git
  index, a data file read by a filter) records the dependency (`DependencyTracker`), or the step declares a state hash
  (2.1) that changes when the file does.
- A feature that runs once per build and whose result every page reads (navigation index, locale tables, asset map)
  computes it once and shares it by reference; templates read it through values that record what they read
  (`ValueAccess`), so that a page depends on the part it used.
- New dependencies are justified in 2.6 and verified under native AOT on `osx-arm64` and `linux-x64` before a phase
  is merged; a native dependency is verified with the Satori GC publish too.
- Messages follow the engine's format: `error: <step>: <file>(<line>,<col>): <message>`; a tag or filter error names
  the template file and position when Fluid gives one.

---

## 2. Architecture

### 2.1 Folder layout additions

```
Douze/
  Markdown/
    MarkdownOptions.cs           the markdown: section of douze.yml (4.2) and the defaults
    MarkdownPipelineFactory.cs   builds the Markdig pipeline from MarkdownOptions
    MarkdownRenderer.cs          renders Markdown to HTML with a pipeline; owns the renderer pool
    RawRegionExtension.cs        sentinel delimited HTML regions that Markdig emits as they are (3.1)
    AsideExtension.cs            ::: containers and > [!NOTE] alerts rendered as one markup (3.2)
    CodeFenceRenderer.cs         <pre> markup with the fence language and meta (3.2, 3.7)
    LinkRewriteExtension.cs      .md links to urls, relative links resolved (3.2)
    HeadingScanner.cs            headings in rendered HTML: ids, text, depth (3.3)
    Slugger.cs                   GitHub style heading ids, shared by the slug filter (3.3)
  Liquid/
    LiquidOptions.cs             (changed) registers the tags and filters of Tags/ and Filters/
    Tags/ShortcodeRegistry.cs    scans the shortcodes folder, registers one block or tag per file (3.1)
    Tags/ShortcodeTag.cs         the render of a shortcode (body, args, raw regions)
    Tags/MarkdownTags.cs         {% markdown %}, {% include_markdown %} (3.4)
    Tags/DiagnosticTags.cs       {% error %}, {% warn %} (3.14)
    Tags/CacheTag.cs             {% cache %} (3.15)
    Tags/ImageTag.cs             {% image %} (3.8)
    Filters/MarkdownFilters.cs   markdownify, strip_markdown, excerpt (3.4)
    Filters/HtmlFilters.cs       strip_html, xml_escape, absolute_url, relative_url, toc (3.3, 3.12)
    Filters/NavigationFilters.cs nav_active, nav_prev_next, nav_breadcrumbs, nav_flatten (3.5)
    Filters/LocaleFilters.cs     t, locale_url, locale_of, format_date, format_number (3.6)
    Filters/AssetFilters.cs      asset_url, asset_inline (3.9)
    Filters/DataFilters.cs       where_exp, group_by_exp, sort_by, index_by (3.5, 3.12)
    Filters/ImageFilters.cs      image_url, image_size (3.8)
    ShortcodeModel.cs            the FluidValue a shortcode renders with (args, body, source)
  Images/
    ImageProcessor.cs            NetVips bridge: resize, convert, read size, strip metadata (3.8)
    ImageVariant.cs              a requested output (width, format, quality) and its file name
    ImageFormats.cs              extension, content type and NetVips saver per format
  Workflow/
    Steps/HeadingsStep.cs        uses: headings (3.3)
    Steps/LocalizeStep.cs        uses: localize (3.6)
    Steps/AssetsStep.cs          uses: assets (3.9)
    Steps/ImagesStep.cs          uses: images (3.8)
    Steps/GitStep.cs             uses: git (3.10)
    Steps/LinksStep.cs           uses: links (3.11)
    Steps/CheckLinksStep.cs      uses: check-links (3.11)
    Steps/ForkStep.cs            uses: fork (3.12)
    Steps/RedirectsStep.cs       uses: redirects (3.13)
    Steps/ReplaceStep.cs         uses: replace (3.18)
    Steps/SocialImageStep.cs     uses: social-image (3.8)
    LocaleSet.cs                 site.locales (3.6)
    Steps/CollectStep.cs         (changed) with.content, collections.urls (3.12, 3.6)
    Steps/LiquidStep.cs          (changed) fence protection, markdown: false (3.1)
    Steps/MarkdownStep.cs        (changed) uses BuildContext.Markdown, raw regions, markdown: false (3.2)
    Steps/FrontMatterStep.cs     (changed) with.require (3.14)
    StepContext.cs               (changed) AddOutput (2.2)
    IStep.cs                     (changed) Step.StateHash (2.2)
    Cache/ResultKey.cs           (changed) state hash in keys (2.2)
    Steps/WriteStep.cs           (changed) identical duplicates accepted (2.2); watch mode (3.16)
    Watch/*                      phase 6 of the engine plan (3.16)
  Cli/WatchCommand.cs            douze watch, douze serve (3.16)
Douze.Tests/
  Markdown/*, Liquid/Tags/*, Liquid/Filters/*, Images/*, Workflow/Steps/* (one file per step), Watch/*
Sample/                          gains a docs section that exercises the features (5.3)
docs/
  site-features-plan.md          this plan
  code-worker.mjs                reference highlighter worker (3.7), not shipped in the tool
```

### 2.2 Engine additions

Three small additions to the engine, all in the first phase (section 5), because several features need them.

**State hash.** A step whose units depend on something it computed once per build (the git index of 3.10, the
locale tables of 3.6, the asset map of 3.9 when read by a step rather than a template) exposes it in its result keys:

`Douze/Workflow/IStep.cs`

```csharp
internal abstract class Step
{
    // ... existing members ...

    /// <summary>
    /// A hash of the state the step computed in <see cref="InitializeAsync"/> that its units read without resolving
    /// it through the scopes (a git index, a table read from disk). Part of every unit's result key, so a unit replays
    /// only while that state is the same. <see cref="Hash128.Empty"/> when the step has no such state.
    /// </summary>
    public virtual Hash128 StateHash => Hash128.Empty;
}
```

`ResultKey.Compute` takes it as a parameter (`Hash128 stateHash`, combined right after `definition.ConfigHash`);
the scheduler passes `Node.Step.StateHash` (read once per step after `InitializeAsync`, so it is stable for the
build). `ResultKeyTests.ChangesWithStateHash` covers it.

**Unit outputs from tags.** A tag or filter that produces a file (an image variant of 3.8, a cached fragment is not a
file) adds it to the unit it renders in:

`Douze/Workflow/StepContext.cs`

```csharp
internal sealed class StepContext
{
    // ... existing members ...

    /// <summary>
    /// Adds a file to the outputs of the current unit, besides what the step returns: a tag that produces a file
    /// (an image variant) calls it through the ambient values. Outputs are stored with the unit's result and
    /// replayed with it. Two units may add the same file (same path, content and metadata): a consumer sees it
    /// once per producer, and <c>write</c> accepts identical files for one path.
    /// </summary>
    public void AddOutput(VirtualFile file);

    /// <summary>
    /// The files added so far, sorted by path; read by the scheduler when the unit ends.
    /// </summary>
    public IReadOnlyList<VirtualFile> AddedOutputs { get; }
}
```

`AmbientKeys.StepContext` carries the context into renders (`StepContext.CreateTemplateContext` sets it), so a tag
reaches `AddOutput`. The scheduler appends `AddedOutputs` to the unit's outputs (provenance: the unit's step and
inputs) before writing them to the channel and storing the result.

**Identical duplicates.** `WriteStep.Register` and the scheduler's duplicate path tracking (`EnterOnceAsync`) accept a
second file for a path when its `FileRef` equals the first (same content and metadata hashes): a content addressed
image variant referenced by two pages is produced twice and written once. A second file with another content is still
the error of today. `WriteStepTests.IdenticalDuplicateIsWrittenOnce`, `SchedulerTests.IdenticalFileFromTwoUnitsEntersOnce`.

### 2.3 Shared services on the build context

`Douze/Workflow/BuildContext.cs` gains:

```csharp
public MarkdownRenderer Markdown { get; }      // the site's pipeline (markdown: section), built once (3.2)
public ShortcodeRegistry Shortcodes { get; }   // the shortcodes found in the includes folder (3.1)
public ImageProcessor? Images { get; }         // null until an image feature initializes it (3.8); lazy, thread safe
public LocaleSet Locales { get; }              // site.locales (3.6); LocaleSet.None when absent
```

`BuildContext.LiquidOptions` is built after `Shortcodes` scanned the includes folder, since shortcodes are parser
registrations (`FluidParser`) and `TemplateLoader` holds the parser: `BuildContext`'s constructor creates the parser
(`LiquidOptions.CreateParser()`), registers the shortcodes on it, then creates the loader and the options. Every
template of the build is parsed with that one parser.

### 2.4 Markdown rendering from tags

Tags and filters render Markdown during a Liquid render (shortcode bodies, `markdownify`, `{% markdown %}`). They
call `BuildContext.Markdown.Render(string markdown)`, which parses with the pipeline and renders with a pooled
`HtmlRenderer` (Markdig's renderer is not thread safe; `MarkdownRenderer` keeps a `ConcurrentBag` of renderers over
`ZStringWriter`s). The result is HTML with raw regions already resolved (3.1): a Markdown render always produces
final HTML.

### 2.5 The model of a shortcode render

`Douze/Liquid/ShortcodeModel.cs`: a `FluidValue` (`Type = Dictionary`) that resolves, in order: the shortcode's
own names (`body`, `source`, `args`, each named argument), then the page's model (the `MetadataValue` of the unit,
read from the ambient `AmbientKeys.Model`), so that a shortcode reads `page.url`, `site.url`, data and collections
like an include does, with the same dependency recording.

### 2.6 Dependencies

| Package | Why | AOT |
|---|---|---|
| `NetVips` (latest stable) and `NetVips.Native.<rid>` per tool package RID | Image resizing and format conversion (3.8), text rendering for social images (3.8) | Verified empirically in 3.8; the native library is loaded at first use, so a tool package without it still builds sites that use no image feature |

No other package. HTML scanning (headings, links, code fences) is done with small hand written scanners over the
regular markup Markdig emits, not an HTML parser: the input is Markdig's output and the layouts of a site, never
arbitrary HTML, and `AngleSharp` or `HtmlAgilityPack` would add megabytes to the binary for nothing. The dev server
uses `System.Net.HttpListener` from the shared framework (engine plan, risk 8), with the `TcpListener` fallback.

---

## 3. Features

Each subsection has the same shape: what the site needs, the design, the signatures and files, the tests, the
acceptance criteria, and where the feature lives (1.2) with the reason.

### 3.1 Components in Markdown: shortcodes

**What the site needs.** The target site's articles use about 30 components for more than 95% of their component
usages: tabs synchronized by a key (about 1,200 usages, 2,500 tab items), asides (about 840, plus 300 admonition
containers), numbered steps (260), badges, link buttons, cards and card grids, file trees, an include of another
content file (420), a theme aware image (160), install instructions for a package, OS aware tabs, a pivot selector
whose sections show for the selected language, a keyboard key, a "learn more" box (480), an API reference link
(1,200), plus a tail of about 45 rarer ones. Most take a few scalar attributes, many have a Markdown body, some nest
(tabs inside a pivot, steps inside a tab). A scripted conversion needs a one to one mapping from a component with
attributes and a body to a tag with arguments and a body.

**Design.** A shortcode is a Liquid file in the shortcodes folder (`<includes>/shortcodes/` by default,
`site.shortcodes` in `douze.yml`): `shortcodes/tabs.liquid` registers the tag `tabs`. Its front matter says what kind
of tag it is:

```yaml
---
block: true        # paired: {% tabs %}...{% endtabs %}; default false: {% badge "New" %}
display: block     # block | inline: whether the output is wrapped as a raw region (below); default block when
                   # paired, inline otherwise
args: [label]      # optional: names of the positional arguments, so {% tab "C#" %} exposes label
---
<div class="tabs" data-sync="{{ sync }}">{{ body }}</div>
```

Syntax, in a page:

```liquid
{% tabs sync: "lang" %}
{% tab "C#" icon: "seti:c-sharp" %}
Some **Markdown** with a fence:

```csharp
var x = 1;
```
{% endtab %}
{% tab "TypeScript" %}...{% endtab %}
{% endtabs %}

Install it with {% badge "v2" variant: "note" %} or see {% learnmore "/docs/x/" %}.
```

Arguments follow Fluid's argument list (`FluidParser.ArgumentsListParser`): positional expressions and `name: value`
pairs, with any Liquid expression as a value (`{% tab page.title %}` works). In the shortcode template, a named
argument is a variable of that name, a positional one is `args[i]` and, when the front matter names it, a variable
too; `args` is the array of positional values. `body` is the rendered body, `source` its Liquid output before Markdown
rendering (below). The page's names (`page`, `url`, data, `collections`, `site`) resolve as in an include (2.5).

**Rendering a block shortcode.** The tag's render (Fluid `RegisterParserBlock`) does, in order:

1. Renders the body statements to a string with the page's context (`RenderStatementsAsync`): nested shortcodes and
   every other tag in the body run now, with the page's variables, and nested block shortcodes produce raw regions
   (step 4) in that string.
2. When the file being rendered is Markdown (`AmbientKeys.File`'s content type is `text/markdown`), renders the string
   to HTML with the site's pipeline (2.4): `body` is HTML, `source` the string. For an HTML page or a template,
   `body` is the string as it is.
3. Renders the shortcode template with the `ShortcodeModel`.
4. When `display` is `block` and the file is Markdown, writes the result wrapped as a **raw region**; otherwise writes
   it as it is.

A raw region is the HTML of a block shortcode, delimited for Markdig so that the page's Markdown pass keeps it as it is
(Markdig would otherwise treat it as an HTML block that ends at the first blank line, and render the rest of the
shortcode's output as Markdown). `RawRegionExtension` (`Douze/Markdown/RawRegionExtension.cs`) adds a block parser
whose opening character is `U+001E` (the record separator, which no text contains): a line that is exactly `\u001E`
opens a region, the lines up to the next line that is exactly `\u001E` are its content, and the renderer writes the
content without escaping or processing, followed by a newline. The tag writes `\n\u001E\n<html>\n\u001E\n`. Regions
nest trivially: an inner shortcode's region was already resolved by the inner Markdown pass of step 2, so the outer
region's content is final HTML. `MarkdownRenderer.Render` strips any `\u001E` left in its input before parsing (a
region opened and not closed is a bug of a tag, reported as an error naming the tag).

An inline shortcode's output goes in the text as it is: CommonMark passes inline HTML through, and a tag whose output
starts a line without ending it (`<span class="badge">New</span> text`) is not an HTML block. A tag whose output is a
block (`<div ...>` on its own line) and may contain blank lines sets `display: block` in its front matter and is
wrapped like a paired one.

**Positions and errors.** A parse error in a shortcode template is reported once, with the shortcode file and the
position, when the registry scans the folder (before any step runs, as a `WorkflowValidationException`: the build does
not start). A render error inside a shortcode names the shortcode file and the page: `error: render:
/docs/a.md: shortcode 'tabs' (shortcodes/tabs.liquid(3,12)): <message>`. An unknown tag in a page is Fluid's parse
error, with the page's position, as today; the message adds `did you mean '<name>'?` when a shortcode name is within a
Levenshtein distance of 2 (the registry gives `TemplateParseException` the list of names).

**Tag names.** A shortcode file name is the tag name: lowercase letters, digits and underscores (`link_card.liquid` is
`{% link_card %}`), since Liquid tag names cannot contain dashes. A name that is a Fluid tag (`if`, `for`, `include`,
`render`, `capture`, `raw`, ...) or one of Douze's (3.4, 3.8, 3.14, 3.15) is an error: `shortcode 'include' hides a
built-in tag; rename the file`.

**Fence protection.** Liquid runs before Markdown, so `{{` and `{%` inside a fenced code block are Liquid to the
`liquid` step today (`liquid: false` is the opt-out). Documentation pages quote templating syntax in fences all the
time (GitHub Actions `${{ }}`, Helm, Go templates). The `liquid` step, for a Markdown file, replaces every fenced
code block (lines from an opening fence of 3 or more backticks or tildes to the matching closing fence, CommonMark
rules, indentation up to 3 spaces) with a placeholder `\u001F<n>\u001F` before parsing the content as a template, and
restores the blocks in the output. `with.protect: fences | none` (default `fences`) turns it off. Inline code spans
are not protected (they are short and rarely hold Liquid; a page uses `{% raw %}` for them). The replacement is on
the content given to `GetOrParse`, so the template cache keys on the protected text; positions reported by Fluid are
mapped back to the original lines (the placeholder keeps the line count: it is followed by as many newlines as the
block had, minus one).

**Markdown opt-out.** `markdown: false` (resolved through the scopes, like `liquid: false`) makes the `markdown` step
pass a `.md` file through unchanged: used by Markdown twins (3.12) and by Markdown files written as is.

`Douze/Liquid/Tags/ShortcodeRegistry.cs`

```csharp
internal sealed record ShortcodeDefinition(string Name, FPath Path, bool Block, ShortcodeDisplay Display, ImmutableArray<string> ArgNames, ParsedTemplate Template, Hash128 ContentHash);
internal enum ShortcodeDisplay { Block, Inline }

internal sealed class ShortcodeRegistry
{
    public static async Task<ShortcodeRegistry> ScanAsync(IFileSystem source, FPath folder, FluidParser parser, CancellationToken ct);
    public IReadOnlyList<ShortcodeDefinition> Shortcodes { get; }     // sorted by name
    public ShortcodeDefinition? Find(string name);
    public void Register(FluidParser parser);     // RegisterParserBlock for Block, RegisterParserTag otherwise, with FluidParser.ArgumentsListParser
    public Hash128 Hash { get; }                  // combined hash of every shortcode file: part of every render's dependencies (below)
}
```

`Douze/Liquid/Tags/ShortcodeTag.cs`

```csharp
internal static class ShortcodeTag
{
    public static ValueTask<Completion> RenderBlockAsync(ShortcodeDefinition shortcode, IReadOnlyList<FilterArgument> args, IReadOnlyList<Statement> body, TextWriter writer, TextEncoder encoder, TemplateContext context);
    public static ValueTask<Completion> RenderTagAsync(ShortcodeDefinition shortcode, IReadOnlyList<FilterArgument> args, TextWriter writer, TextEncoder encoder, TemplateContext context);
    public const char RawRegionMarker = '\u001E';
    public const char FencePlaceholderMarker = '\u001F';
}
```

A render records the shortcode file as a template dependency (`DependencyTracker.RecordTemplate(path, hash)`), so
editing `tabs.liquid` re-renders the pages that use `{% tabs %}` and no other.

**Tests** (`Douze.Tests/Liquid/Tags/ShortcodeTests.cs`): block shortcode with a Markdown body renders the body to HTML;
inline shortcode in a paragraph stays inline; nested blocks (a tab in tabs in a pivot) produce the inner HTML inside
the outer; positional and named arguments, expressions as arguments, `args` names from front matter; a shortcode
reads `page.title` and site data; a fence inside a body is rendered by the inner pass, with its meta (3.2); a block
whose output has blank lines survives the page's Markdown pass (raw region); `display: block` on an unpaired
shortcode; a shortcode in an HTML page gets the body as text; unknown shortcode error with suggestion; shortcode parse
error reported at scan with file and position; render error names the shortcode and the page; a shortcode named like a
built-in tag is rejected; editing a shortcode file re-runs only its users (an `EarlyCutoffTests` case). Fence
protection (`LiquidStepTests`): `{{ x }}` inside a fence is kept verbatim, outside it is rendered, positions of an
error after a fence are the original lines, `with.protect: none`. `MarkdownStepTests.MarkdownFalsePassesThrough`.

**Acceptance.** The Sample's new docs section (5.3) has tabs, asides, steps and a badge, converted from a component
Markdown page kept next to it as documentation; the engine plan's shared criteria.

**Where it lives and why.** Liquid tags backed by include files (the extension point a site author already knows),
plus a Markdig extension for raw regions. Not a Fluid change: `RegisterParserBlock` and `RenderStatementsAsync` are
enough. Not a Markdig extension recognizing `{% %}`: Liquid runs before Markdown by design (engine plan 4.6), and tags
need the page's variables and data.

### 3.2 The Markdown pipeline

**What the site needs.** Pipe tables, footnotes, task lists, definition lists, generic attributes (`{#id .class}` on
headings and blocks), auto links, smart punctuation off (code samples), emphasis extras off, admonitions as
`:::note[Title]` containers (about 300 in the target site) and GitHub alerts (`> [!NOTE]`, about 20) rendered as the
same markup as the aside component, fenced code with its language and meta string kept for the highlighter (3.7),
Mermaid fences left for a client script, links to `.md` files rewritten to the page urls, heading ids (3.3), and the
same pipeline everywhere Markdown is rendered (pages, shortcode bodies, `markdownify`).

**Design.** A `markdown:` section in `douze.yml` (4.2) configures one pipeline for the build, exposed as
`BuildContext.Markdown`. The `markdown` step uses it; `with.extensions` on the step remains as an override for a step
that renders another dialect (the twins of 3.12 are not rendered at all).

`Douze/Markdown/MarkdownOptions.cs`

```csharp
internal sealed record MarkdownOptions
{
    public ImmutableArray<string> Extensions { get; init; } = DefaultExtensions;   // Markdig names, see below
    public AsideOptions Aside { get; init; } = AsideOptions.Default;
    public bool RewriteLinks { get; init; } = true;        // .md -> url, index.md -> folder
    public string CodeClassPrefix { get; init; } = "language-";
    public ImmutableArray<string> PassthroughLanguages { get; init; } = ["mermaid"];   // fences left as <pre class="mermaid">
    public static readonly ImmutableArray<string> DefaultExtensions = ["pipetables", "footnotes", "tasklists", "definitionlists", "attributes", "autolinks", "alerts", "customcontainers"];
}

internal sealed record AsideOptions(string Tag, string ClassPrefix, ImmutableArray<string> Kinds, IReadOnlyDictionary<string, string> Titles, string? TitlesFrom)
{
    // Default: <aside class="aside aside-note" aria-label="Note"><p class="aside-title">Note</p>...</aside>; kinds note, tip, caution, danger.
    // TitlesFrom: a data key ("i18n") under which <locale>.aside.<kind> gives the default title of each kind in the page's
    // locale (3.6); the markdown step resolves them per page through the scopes (recorded) and passes them to the render.
}
```

`MarkdownPipelineFactory.Create(MarkdownOptions)` builds a `MarkdownPipelineBuilder`, calls `Configure` with the
extension names (Markdig's registry: `advanced` expands as today), then adds Douze's extensions in this order:
`RawRegionExtension` (always), `AsideExtension` (when `alerts` or `customcontainers` is in the list: it replaces the
HTML renderers of Markdig's `CustomContainer` and alert blocks by one that writes the aside markup with the kind and
the optional title, `:::note[Title]` syntax parsed from the container's info string), `CodeFenceRenderer` (always),
`LinkRewriteExtension` (when `RewriteLinks`), and `HeadingAttributes` (ids from `{#id}` only; automatic ids are the
job of 3.3, so `autoidentifiers` is rejected in the list with the message `use the headings step for heading ids`).
A name Markdig does not know is `douze.yml(12,5): error: unknown Markdown extension 'tabels'; did you mean 'pipetables'?`.

**Fenced code.** `CodeFenceRenderer` replaces Markdig's `CodeBlockRenderer`: a fence renders as

```html
<pre data-lang="csharp" data-meta="title=&quot;Program.cs&quot; {3-4}"><code class="language-csharp">...escaped...</code></pre>
```

`data-lang` is the first word of the info string, `data-meta` the rest (attribute encoded), both omitted when empty;
an indented code block renders as `<pre><code>`. A language in `PassthroughLanguages` renders as
`<pre class="mermaid">...escaped...</pre>` with no `<code>` element (the client script reads `.mermaid`). The
highlighter contract of 3.7 is this markup.

**Links.** `LinkRewriteExtension` runs on `LinkInline` and `LinkReferenceDefinition` urls: a relative url ending in
`.md` (with optional `#fragment`) becomes the url of that page (`other.md` -> `other/`, `index.md` -> `./`,
`../a/b.md` -> `../a/b/`), resolved against the page's directory when the url is relative, and left as is when the
target does not exist in the source tree (checked through the source file system; the lookup records a dependency on
the path, `DependencyTracker.RecordAbsent`, so creating the file later re-renders the page). Absolute urls, mailto and
anchors are untouched. The extension reads the page's directory from `MarkdownRenderer.Render(markdown, FPath? directory)`.

`Douze/Markdown/MarkdownRenderer.cs`

```csharp
internal sealed class MarkdownRenderer
{
    public MarkdownRenderer(MarkdownOptions options, IFileSystem source, FPath contentFolder);
    public MarkdownPipeline Pipeline { get; }
    public string Render(string markdown, FPath? directory, DependencyTracker? deps, IReadOnlyDictionary<string, string>? asideTitles = null);   // pooled HtmlRenderer; raw regions resolved; U+001E stripped
}
```

**Tests** (`Douze.Tests/Markdown/`): each default extension renders its syntax; `:::note[Title]` and `> [!NOTE]`
render the same aside markup, every kind, unknown kind is `note` with a warning; fence markup with lang and meta,
attribute encoding of `"` and `<` in the meta, passthrough language, indented block; link rewriting of the five forms
and the untouched ones, missing target left as is with an absent dependency; unknown extension error with suggestion;
`autoidentifiers` rejected; `MarkdownStep` uses the site pipeline and `with.extensions` overrides it; two renders in
parallel on the pool give the same output.

**Acceptance.** Shared criteria; the Sample's docs section has a table, a footnote, a `:::tip`, an alert, a fence
with meta, a Mermaid fence and a `.md` link, and its output is checked in `CommonTests`.

**Where it lives and why.** Markdig extensions and a site-wide configuration in C#: Markdig has every extension; the
aside markup, fence markup and link rewriting are small renderers. No command step.

### 3.3 Headings and tables of contents

**What the site needs.** A right-hand table of contents from the `h2` and `h3` of each page, heading ids that match
the ids the target site has today (its deep links, redirects and tests depend on them: GitHub style slugs, duplicates
suffixed `-1`, `-2`), anchor links on headings, and headings contributed by shortcode bodies and includes counted
with the page's own.

**Design.** Ids are assigned and the table of contents is collected on the **rendered HTML**, by a step after
`markdown`, not in the Markdig AST: the HTML is where headings from every source meet (the page, inner Markdown
passes of shortcodes, Markdown includes, HTML pages), and one pass over it gives unique ids for the whole page.

`uses: headings` (map, default match `**/*.html`, after `markdown`, before `layout`):

- Scans the content for `<h1>` to `<h6>` elements (`HeadingScanner`: a forward scan for `<h` followed by a digit, the
  attributes up to `>`, the inner HTML up to the matching `</hN>`; headings inside `<pre>`, `<code>`, `<script>`,
  `<style>` and `<template>` are skipped by tracking those elements). The text of a heading is its inner HTML with
  tags removed and entities decoded (`WebUtility.HtmlDecode`).
- Keeps an existing `id` attribute; otherwise computes `Slugger.Slug(text)`: lowercase (invariant), remove every
  character that is not a letter, digit, space, hyphen or underscore (Unicode letters kept, so non-Latin headings
  keep their text), spaces to hyphens, which is the GitHub algorithm the target site's `rehype-slug` uses. A
  duplicate id on the page gets `-1`, `-2`, ... (GitHub's rule). The step writes the id into the tag.
- `with.anchors: true` (default false) appends `<a class="heading-anchor" href="#<id>" aria-label="...">#</a>`
  inside the heading (the class and the label text are `with.anchor-class`, `with.anchor-label`).
- Sets Own `headings` to an array of `{ depth, text, id }` in document order, and `toc` to the nested tree of the
  levels in `with.levels` (default `[2, 3]`): each entry `{ depth, text, id, children }`.
- The first `h1`'s text is stored as `page.heading` when the page has one (a layout can use it when `title` is absent).

A layout renders the tree with a recursive include, or with the `toc` filter (`Filters/HtmlFilters.cs`), which
renders `toc` as nested `<ul>` with `with.levels` applied again: `{{ toc | toc: class: "toc", max-depth: 3 }}`.

`Douze/Markdown/HeadingScanner.cs`

```csharp
internal readonly record struct Heading(int Depth, string Text, string? Id, int Start, int End, int OpenTagEnd);   // offsets in the HTML
internal static class HeadingScanner
{
    public static List<Heading> Scan(string html);
    public static string Rewrite(string html, IReadOnlyList<Heading> headings, Func<Heading, string> id, Func<Heading, string?> append);   // one pass, ZString builder
}

internal static class Slugger
{
    public static string Slug(string text);                      // GitHub rules above
    public sealed class Scope { public string Unique(string slug); }   // dedupe with -1, -2
}
```

`SlugFilter` (`slug`) keeps its rules (urls); `Slugger` is for heading ids, documented as such.

**Tests** (`Douze.Tests/Workflow/Steps/HeadingsStepTests.cs`, `Douze.Tests/Markdown/HeadingScannerTests.cs`, `SluggerTests.cs`):
ids match a table of GitHub examples (punctuation, accents, CJK, emoji, leading digits); duplicates suffixed; existing
id kept; headings in `<pre>` and `<code>` ignored; inner tags stripped from the text and entities decoded; anchors
appended with class and label; `headings` and `toc` metadata shapes, `with.levels`; a heading inside a shortcode's
raw region is counted; `page.heading`; `toc` filter output.

**Acceptance.** Shared criteria; the Sample's docs layout shows a table of contents built from `toc`.

**Where it lives and why.** A built-in step over HTML: one pass that sees every heading of the page whatever
produced it. Not Markdig's `autoidentifiers`: it would run once per Markdown pass (page, each shortcode body, each
include) with separate counters, and misses HTML pages.

### 3.4 Markdown includes and rendering helpers

**What the site needs.** An include of another content file rendered inline (420 usages, 14 distinct targets, all
command line reference partials), with the include's own Liquid evaluated in the page's context and its Markdown
rendered as part of the page (its headings in the page's table of contents, its fences highlighted like the page's),
and rendering Markdown from data (descriptions of a catalog, glossary terms) in templates.

**Design.** Three tags and three filters in `Douze/Liquid/Tags/MarkdownTags.cs` and `Filters/MarkdownFilters.cs`:

- `{% include_markdown "reference/includes/options.md" %}`: loads the file from the content folder (a path relative
  to the content folder, or relative to the current page when it starts with `./` or `../`), strips its front matter,
  renders it as a Liquid template with the page's context (the include's front matter values are variables of the
  render, below the page's), and writes the Liquid output **as Markdown source** into the page: the page's own
  Markdown pass renders it, so its headings join the page's and nothing is rendered twice. Fences inside the include
  are protected like the page's (3.1). In an HTML page or a layout, the output is rendered to HTML first (the page has
  no Markdown pass). The tag records a template dependency on the file (`RecordTemplate`), and the page re-renders when
  it changes. Named arguments become variables: `{% include_markdown "x.md", version: "2.0" %}`. A missing file is an
  error: `include_markdown: 'x.md' not found in '/docs/'`. Recursion deeper than 16 is an error naming the chain.
- `{% markdown %}...{% endmarkdown %}`: renders its body as Markdown to HTML, for Markdown written inside HTML pages,
  layouts and shortcodes. In a Markdown page it is the same as writing the Markdown (and emits a raw region).
- `{% raw_markdown %}` is not needed: `{% raw %}` is Fluid's.
- `markdownify`: `{{ item.description | markdownify }}` renders a string to HTML (`inline: true` renders a single
  paragraph without the `<p>` wrapper: `{{ text | markdownify: inline: true }}`).
- `strip_markdown`: plain text from Markdown (renders, then strips tags and decodes entities), for descriptions and
  search metadata.
- `excerpt`: the first paragraph of a Markdown string, as HTML (`{{ content | excerpt }}`), or the text before
  `<!-- more -->` when present.

The content folder for `include_markdown` is `site.content`; `with.root`-like overrides are not needed (a path can
climb with `../`). All three read the source through `BuildContext.Source` and the page's `MarkdownRenderer` (2.4).

**Tests** (`Douze.Tests/Liquid/Tags/MarkdownTagsTests.cs`, `Filters/MarkdownFiltersTests.cs`): include rendered inline
with its headings in the page's `toc` (through the `headings` step); include front matter visible as variables, page
variables win; relative and content-folder paths; arguments; missing file error; recursion limit; include in an HTML
page renders to HTML; include edit re-renders the page only (`EarlyCutoffTests`); `markdown` block in a layout;
`markdownify` with and without `inline`; `strip_markdown`; `excerpt` with and without the marker.

**Acceptance.** Shared criteria; the Sample's docs section includes a shared Markdown partial.

**Where it lives and why.** Liquid tags and filters: the page's context is needed, and Fluid's `include` cannot
return Markdown to the page's pass without this. Not a Markdig extension (`{% %}` is Liquid's).

### 3.5 Navigation data: sidebar, active trail, previous and next, breadcrumbs

**What the site needs.** A sidebar of 6 topics and about 530 links hand written as data (today 5,200 lines of
TypeScript), with group labels in 15 locales, badges on items, collapsed groups, an active item and open ancestors,
previous and next links computed from the sidebar order (adjacent links of the same group only, no link across groups,
none on splash pages, front matter `prev`/`next` overrides), breadcrumbs, and an API reference section whose sidebar is
generated per package from data (3.12, `cache`). Rendering the sidebar on 8k pages must not cost 8k tree walks of
530 nodes with per-page dependency on the whole tree.

**Design.** The sidebar is site data: `_data/sidebar.yml` (or several files, one per topic, `_data/sidebar/*.yml`
keyed by file name), with this shape, which the filters understand:

```yaml
- label: Foundations                      # a topic: a top-level group with an icon and a root link
  label_translations: { fr: Fondations }  # or label: { en: ..., fr: ... }; a string is the default locale's
  icon: open-book
  link: /docs/
  items:
    - label: Get started
      collapsed: true
      items:
        - { label: Install, link: /get-started/install/, badge: { text: New, variant: tip } }
        - { label: First app, link: /get-started/first-app/, translations: { fr: Première application } }
        - /get-started/deploy/              # a bare url: label from the page's title (needs collections)
    - { label: GitHub, link: https://github.com/x/y, external: true }
```

Filters (`Douze/Liquid/Filters/NavigationFilters.cs`), all pure functions of a sidebar value and a url:

| Filter | Result |
|---|---|
| `nav_active: page.url` | The same tree with `active: true` on the matching item and `open: true` on its ancestors (new nodes, the input is never changed); a locale prefix on the url is removed before matching when the tree has none (3.6) |
| `nav_prev_next: page.url` | `{ prev: item?, next: item? }`: the adjacent leaf links of the same group as the active item (`with` the rule of the target site); `null` when the url is not in the tree |
| `nav_breadcrumbs: page.url` | The items from the root to the active item (labels and links) |
| `nav_flatten` | The leaf links in order (for sitemaps, text exports, search indexes) |
| `nav_section: page.url` | The topic (top-level group) that contains the url, so a layout can show one topic's items |
| `nav_label: lang` | The label of an item in a locale, falling back to the default locale |

Each filter resolves bare urls through `collections.urls` (3.6) when the tree has them (the page's `title` becomes
the label), which records a dependency on those entries only.

Performance and dependencies: a filter indexes a sidebar node once per build (`ConditionalWeakTable<NodeArray,
SidebarIndex>`: the flattened leaf list, a `Dictionary<string url, int index>` and parent links), since published
nodes never change. What a page depends on is what the template read: the filters work on the `NodeArrayValue`
of the sidebar through `IFluidArray` and member reads, so `ValueAccess` records the members and items read; a
whole-tree read (`nav_active` returns a new tree built from the whole input) records the whole value, which is
correct: a sidebar edit re-renders every page that shows it. Cost: about 530 nodes cloned per page, under a
millisecond.

Front matter `prev: false`, `next: false`, or `prev: { label, link }` are read by the layout, not by the filter
(`{% assign nav = sidebar | nav_prev_next: page.url %}{% if prev == false %}...`), as today's site does.

**Tests** (`Douze.Tests/Liquid/Filters/NavigationFiltersTests.cs`): active and open flags; prev and next within a
group, none across groups, none at the ends; breadcrumbs; flatten order; bare url resolution through collections
with a recorded dependency on that url only; locale prefix stripping; labels by locale with fallback; input tree
unchanged (identity of nodes); a page that reads only `nav_section` of its topic re-renders when another topic
changes (whole value read, documented) and `nav_prev_next` likewise; index built once for two renders.

**Acceptance.** Shared criteria; the Sample's docs layout has a sidebar with the active trail and previous/next.

**Where it lives and why.** Liquid filters over site data: no step, no new file type, and the data is plain YAML a
site can generate from anything (the target site's TypeScript sidebar converts with a script). Not a built-in step:
nothing is emitted, and the only state is a per-node index.

### 3.6 Locales

**What the site needs.** 15 locales with a prefix-less root locale (`/docs/x/` is English, `/ja/docs/x/` is
Japanese), translated files next to the root ones under `<locale>/`, UI strings per locale (380 nested keys in the
root locale, 37 missing in the others: fall back to the root), labels of the sidebar per locale, `lang` and `dir`,
`hreflang` alternates listing the locales where the page exists, and a page for every locale even when the file is
not translated (today 7,500 of the 8,000 article routes are such fallbacks, rendered with the root locale's content,
the locale's chrome and a notice), dates and numbers in the locale's format.

**Design.** `site.locales` in `douze.yml` (4.1):

```yaml
site:
  locales:
    default: en                 # the root locale: no prefix
    root: docs                  # the content subfolder whose children are locale folders; default: the content folder
    fallback: true              # emit a copy of every untranslated root page under each locale (default false)
    languages:                  # folder -> language
      ja: { label: 日本語, lang: ja, dir: ltr }
      zh-cn: { label: 简体中文, lang: zh-CN }
      ar: { label: العربية, lang: ar, dir: rtl }
    strings: i18n               # data key of the UI strings: _data/i18n/<locale>.json; default i18n
```

`LocaleSet` (`Douze/Workflow/LocaleSet.cs`) holds it; `BuildContext.Locales` exposes it; every setting is part of
`StepDefinition.ConfigHash` of the steps that read it (through `with`, resolved by the step from the settings, or
through `StateHash`).

`uses: localize` (map, default match `**/*.html`, `**/*.md`; runs after `collect` and before `render`):

- For every page under `root`: sets Own `locale` (the folder's key, or `default`), `lang`, `dir`, `locale_url`
  (the url without the locale prefix: what the page is called in every locale), and `alternates`: the list of
  `{ locale, lang, url }` of every locale where a page with the same `locale_url` exists, found through
  `collections.urls` (a lookup per locale: `urls["/ja" + locale_url]`, which records a dependency on the presence of
  that one url only; `collect` contributes `collections.urls`, 3.12).
- With `fallback: true`, for a page of the default locale and for every locale `L` with no page at `/L<locale_url>`:
  emits `file.With(path: /L/<path>, metadata: Own + { locale: L, lang, dir, url: /L<url>, fallback: true,
  original_url: url })`, the content shared by reference (no copy). The copies flow through `render`, `markdown`
  and `layout` like any page, so each locale renders its own chrome around the root content; `fallback` lets the
  layout show the notice. Copies are not in `collections.all` (built before): the sitemap and feeds expand them from
  `alternates` and the `fallback` rule when they want them, with the `locale_pages` filter below.
- A page outside `root` passes through unchanged.

Filters (`Douze/Liquid/Filters/LocaleFilters.cs`):

| Filter | Result |
|---|---|
| `t: "search.label"` | The string at that path in `i18n[page.locale]`, else in `i18n[default]`, else the key itself with a warning once per key; `{{ "pages.count" | t: count: 3 }}` replaces `{count}` placeholders (the arguments are named) |
| `locale_url: "ja"` | The url of the page in another locale (`/ja` + `locale_url`), whether or not it exists; `locale_url: "en"` is the root url |
| `locale_of: url` | The locale key of a url (`default` when it has no prefix) |
| `locale_pages: collections.all` | Every (locale, page) pair the site serves, including fallbacks when `fallback: true`: for sitemaps and search configuration |
| `format_date: "long"`, `format_number` | `DateTimeOffset`/number formatting with the page's `lang` culture (`CultureInfo` with `InvariantGlobalization` is on: the AOT publish of the tool keeps ICU off, so formats come from .NET's invariant data plus a small built-in table of month and day names per `lang`, documented as a limitation; see 7.3) |

`t` reads `i18n` through the scopes (`_data/i18n/ja.json` is `i18n.ja`), so a string edit re-renders only the pages
that read that key (member level dependency).

Interplay with other features: the `permalink` step keeps the locale folder in the url (`/ja/docs/x/`), nothing to
do; `nav_active` strips the prefix (3.5); `headings` ids are computed from the translated text; the Markdown twins of
3.12 are emitted for every locale's pages, fallback copies included, when the `fork` step runs after `localize`.

**Tests** (`Douze.Tests/Workflow/Steps/LocalizeStepTests.cs`, `Filters/LocaleFiltersTests.cs`): `locale`, `lang`,
`dir`, `locale_url` on root and translated pages; alternates list only existing locales, dependency recorded on
those urls only (adding a translation re-runs the root page's `localize` unit and the pages that showed its alternates,
nothing else: an `EarlyCutoffTests` case); fallback copies emitted for missing locales only, with shared content
(same `FileContent` instance), `fallback` and `original_url` set, rendered by a layout per locale; no copies without
`fallback: true`; pages outside `root` untouched; `t` fallback chain and placeholders, missing key warning once;
`locale_url`, `locale_of`, `locale_pages`; date and number formatting for three cultures.

**Acceptance.** Shared criteria; the Sample's docs section has a second locale folder with one translated page and
`fallback: true`, and the output has the fallback copies with the locale's strings.

**Where it lives and why.** A built-in step (the copies are files the engine must know about, and `collections.urls`
makes the lookups precise) and filters. Not a server-side fallback (a hosting concern that would change the site's
urls and `hreflang` set); the engine renders 8k extra pages in a few seconds.

### 3.7 Code highlighting

**What the site needs.** About 5,600 fences in the root locale (plus 800 in a translated one): C#, shell,
TypeScript, JSON, XML, Python, Go, PowerShell, Bicep, YAML and 20 others, rendered today by Expressive Code with
two themes, titles (4,900 fences), frames (terminal), line markers (`{3-4}`, `ins`, `del`, `mark`), line numbers,
collapsible sections, a copy button and a `disable-copy` meta, and 320 TypeScript fences with twoslash type hovers
computed from a 6.5 MB generated declaration file by the TypeScript compiler. Mermaid fences are rendered by a client
script. The API reference's 50k signatures are highlighted too, but those come pre-highlighted from the site's
generators (engine plan, gap B11), not from this feature.

**Design: a command step over the fence markup.** Douze's part is the markup of 3.2 (`<pre data-lang data-meta>`),
stable and documented as the contract, and the pattern:

```yaml
code: { needs: markdown, run: "node scripts/code-worker.mjs", mode: worker, match: "docs/**/*.html",
        sources: ["scripts/code-worker.mjs", "data/twoslash/types.d.ts"], with: { themes: [laserwave, slack-ochin] } }
```

The worker receives each HTML page, replaces every `<pre data-lang=...>` by the highlighter's HTML, and answers.
`docs/code-worker.mjs` in this repository is the reference worker (not shipped in the tool, no dependency on it):
it uses the Expressive Code engine as a library (`ExpressiveCodeEngine.render({ code, language, meta })`), with its
plugins (frames, text markers, line numbers, collapsible sections, twoslash), so a site keeps its highlighter's
exact output and configuration; it emits the engine's base styles and scripts once as files (`/assets/code.css`,
`/assets/code.js`), and the layouts reference them. Fences are found with a scanner over the regular markup
(`<pre data-lang="..." data-meta="..."><code class="...">escaped</code></pre>`), decoded, rendered, and replaced; a
fence whose language the engine does not know is left as it is with a `warn` log. The worker answers with
`concurrency` equal to the cores, twoslash renders serialized inside it as the site does today.

Caching: a worker unit is a page, keyed by the page's content and the `sources`: a page re-highlights when it
changes, and every page when the worker script or the types file changes. Per-fence caching inside the worker (a
content addressed cache folder under `.douze/`, passed as `with.cache`) is the site's choice; the reference worker
has it, keyed by the engine's version, the configuration hash, language, meta and code, because twoslash costs
hundreds of milliseconds per fence.

Page count: only pages with fences need the step; `match` restricts it (`docs/**/*.html`); a page without `<pre
data-lang` is answered in microseconds.

**Tests.** Douze's side is tested in 3.2 (the markup). `Douze.Tests/Commands/CodeWorkerTests.cs` runs the reference
worker when `node` and its modules are available (`Assert.Skip` otherwise, like `NodeHelperTests`): a page with two
fences is highlighted, the meta reaches the engine (a title appears), an unknown language is kept with a warning,
the styles file is emitted once. CI installs the worker's modules in a `docs/` package for that test.

**Acceptance.** The Sample's docs section is highlighted when the worker runs (`Sample/douze.yml` has the step with
`match` on its docs folder, behind a `scripts/` folder with the worker; CI runs it on Linux with Node 24). The
worker's time for the target site's fences is measured in `bench/README.md` (expected: a few seconds for 6k fences
without twoslash, plus the twoslash cost the site pays today, cached per fence after the first build).

**Where it lives and why.** Command step contract: the grammars (TextMate), themes and the twoslash plugin exist in
JavaScript, the TypeScript compiler is the only thing that can compute the hovers, and the site's e2e tests assert the
exact markup of its highlighter. A C# highlighter (TextMateSharp needs a native Oniguruma, ColorCode has no modern
grammars) would be weaker and would still not do twoslash. The worker mode makes the cost one process for the build,
started while the earlier steps run.

### 3.8 Images and social images

**What the site needs.** About 680 images imported by the articles (550 PNG, 110 SVG, a few GIF and WebP),
rendered with width and height attributes (no layout shift), hashed file names, WebP or AVIF variants and lazy
loading; a light and a dark variant of the same figure (160 usages); and about 540 social (Open Graph) images, a
title and a description laid out over a branded background, one per page of the root locale.

**NetVips under native AOT (verified 2026-10-21, `osx-arm64`, .NET 11 RC 2 SDK).** `NetVips` 3.2.0 declares
`IsAotCompatible`; it binds libvips with classic `DllImport`s (no reflection), publishes with zero IL warnings with
the standard GC and with the Satori GC, and its results are identical to the JIT run. `NetVips.Native` 8.18.7 ships
one statically linked library per RID (`libvips.42.dylib`, `libvips.so.42`, `libvips-42.dll`; packages exist for
`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `win-x64`, `win-arm64`,
`win-x86`), 7 to 8.3 MB per package, 16 to 20 MB on disk, depending only on OS libraries; `dotnet pack -r <rid>
-p:PackTool=true` packs it next to the binary (a tool package grows by 7 to 8 MB; the AOT binary by 0.7 MB).
With the library missing, the process still starts (the module initializer records the `DllNotFoundException`) and
the first image call throws, so a build without image features never needs it. Loaders: JPEG, PNG, WebP, GIF, TIFF,
HEIF/AVIF, SVG (librsvg), no JPEG XL or PDF; savers: JPEG, PNG, WebP, AVIF/HEIF, GIF, TIFF. Pango text rendering
(`Image.Text` with `width` for wrapping, markup, `fontfile:` for a bundled `.ttf`) and `Composite2` work, which is
what social images need. Throughput: about 400 thumbnails per second (1600x1200 JPEG to 400 px WebP) on 14 cores with
`Concurrency = 1` per operation and `Parallel.For`; 216 MB peak RSS for the test. Details and commands: the check's
report, summarized in `bench/README.md` by the phase that adopts it.

Rules the bridge follows (from the check): `NetVips.Cache.Max = 0` and `NetVips.Concurrency = 1` (the engine
parallelizes per unit); `Image.ThumbnailBuffer` (shrink on load, auto rotation, premultiplied alpha) rather than
`Resize`; a thumbnail result is a sequential pipeline written once (one `Thumbnail` per output); `keep: none` on
every save (strip EXIF, XMP and ICC: 446 bytes against 194 for a small WebP); a synthesized background is copied with
`interpretation: Srgb` before compositing; VIPS warnings are captured with `Log.SetLogHandler` and forwarded as
`warn` messages rather than left on stderr.

**Design: the `image` tag.** In a page or a layout:

```liquid
{% image "images/dashboard.png", alt: "The dashboard", widths: [640, 1280], formats: ["webp", "png"], sizes: "(min-width: 60em) 50vw, 100vw", class: "figure", loading: "lazy" %}
{% image "images/arch.svg", alt: "Architecture" %}                 # SVG: copied, width and height read from the file
{% image light: "images/a-light.png", dark: "images/a-dark.png", alt: "..." %}   # two sources, one <picture> with media queries
```

The source path is relative to the page's directory, to the content folder when it starts with `/`, or an `assets`
path (3.9). The tag:

1. Reads the source through `BuildContext.Source` and records it as a template dependency (its content hash), so
   the page re-renders when the image changes.
2. Computes the variants (`ImageVariant`: width, format, quality from `with` or the `markdown.images` defaults of 4.2),
   processes them with `ImageProcessor` (3.8 rules), names each `<name>-<width>.<hash8>.<ext>` under
   `with.output` (`/assets/images/` by default; the hash is the content hash of the variant), and adds them to the
   unit with `StepContext.AddOutput` (2.2). Identical variants produced by two pages are identical files (2.2).
3. Writes a `<picture>` with one `<source>` per format (`srcset` of every width) and an `<img>` of the fallback
   format with `width`, `height`, `alt`, `loading` (`lazy` default), `decoding="async"`, the class and any other
   named argument as an attribute. A single width and format gives a plain `<img>`. `light`/`dark` gives one
   `<picture>` with `media="(prefers-color-scheme: dark)"` sources, or `data-theme` variants when
   `theme: attribute` is set (a site that toggles `data-theme` renders both and hides one in CSS).
4. An SVG or GIF is copied as is (fingerprinted name, size read from the file; `svg` dimensions from the
   `viewBox` or `width`/`height` attributes, without rasterizing).

The `image_url` filter (`{{ "images/a.png" | image_url: width: 400, format: "webp" }}`) produces one variant and
returns its url, for `og:image` meta or CSS; `image_size` returns `{ width, height }` of a source.

Work per variant is cached with the page's unit (the outputs are blobs in the pack files): a second build with no
change processes nothing; a page edit replays the variants from the cache (their refs are in the unit's outputs).
The hash in the file name means a changed source gets a new name, so `write` never overwrites an image that a cached
page still references with its old name until that page re-renders.

**Design: the `images` step.** For sites that keep plain `<img src>` in Markdown (`![alt](images/x.png)`), a map
step over image files produces the variants and a map step over HTML rewrites the tags:

```yaml
images:  { inputs: "docs/**/*.{png,jpg,jpeg,webp,avif,gif,svg}", uses: images, with: { widths: [640, 1280], formats: [webp], output: assets/images } }
pictures: { needs: [markdown, images], uses: pictures }    # rewrites <img src> to <picture> with width and height, using the images scope
```

`uses: images` (map): emits the variants of each image (and the original, fingerprinted) and contributes nothing;
the variants' metadata (`image: { source, width, height, variants: [...] }`) is on each output. A reduce step
`image-map` (`uses: images, with: { scope: true }`; reduce, `ReadsContent: false` over the variants' metadata)
contributes `images[source url]` for `pictures` and templates. `uses: pictures` (map over HTML) rewrites `<img
src="...">` whose source is in `images` into the markup of the tag above and leaves the others. The `image` tag is the
primary design (explicit, per use); the steps exist for Markdown image syntax, which the target site's articles use
little (their images are components).

**Design: the `social-image` step.** `uses: social-image` (map over pages, after `localize`):

```yaml
og: { needs: localize, uses: social-image, match: "docs/**/*.md",
      with: { template: og/card.svg.liquid, width: 1200, height: 630, output: "og{{ locale_url }}index.png",
              skip: "og == false or locale != 'default'",
              text: [ { value: "{{ title }}", font: "Outfit Bold 56", fontfile: assets/fonts/Outfit-Bold.ttf, x: 80, y: 160, width: 1040, height: 220, color: "#ffffff", align: left },
                      { value: "{{ description | strip_markdown | truncate: 160 }}", font: "Outfit 30", fontfile: assets/fonts/Outfit-Regular.ttf, x: 80, y: 400, width: 1040, height: 150, color: "#c8c8d0" } ] } }
```

The step consumes pages and emits **images only** (it sits on its own branch of the graph: `needs: localize`, and
`write` needs it; the pages reach `write` through the rendering chain). A layout computes a page's image url with the
same `output` template through the `social_image_url` filter (`{{ page | social_image_url: "og{{ locale_url }}index.png" }}`,
`Filters/ImageFilters.cs`), so the step keeps no per-page metadata. For each page not skipped (`skip` is a Liquid expression rendered with the page's model): renders `template` (a
Liquid template producing SVG: logos, gradients, badges, anything static or data driven) with the page's model,
rasterizes it with libvips at `width` x `height` (the SVG is the background; it may reference images of the source
tree by relative path, which the step inlines as data URIs before rasterizing, recording them as dependencies),
renders each `text` block with Pango (`Image.Text(value, font, fontfile, width, height clipped, align, rgba)`,
colored, composited at `x`, `y`), encodes PNG, and emits it at `output` (a Liquid template over the page's model). Cost: about 20 to 40 ms per image; 540 images
in a few seconds across cores. `fontfile` is required on Linux CI (no system fonts in containers): the step warns
when a block has a `font` family that fontconfig cannot resolve and no `fontfile`.

`Douze/Images/ImageProcessor.cs`

```csharp
internal sealed class ImageProcessor
{
    public static ImageProcessor Create();   // throws DouzeException with the install hint when libvips is missing (ModuleInitializer.Exception)
    public ImageInfo Probe(ReadOnlyMemory<byte> source);                                   // width, height, format, hasAlpha, pages
    public ReadOnlyMemory<byte> Variant(ReadOnlyMemory<byte> source, ImageVariant variant);   // ThumbnailBuffer + save (keep: none)
    public ReadOnlyMemory<byte> Rasterize(string svg, int width, int height);              // svgload at scale
    public ReadOnlyMemory<byte> Compose(ReadOnlyMemory<byte> background, IReadOnlyList<TextBlock> blocks);   // Pango text layers, PNG
}

internal readonly record struct ImageVariant(int? Width, string Format, int Quality);
internal sealed record TextBlock(string Text, string Font, string? FontFile, int X, int Y, int Width, int Height, string Color, string Align);
internal readonly record struct ImageInfo(int Width, int Height, string Format, bool HasAlpha);
```

**Tests** (`Douze.Tests/Images/`, `Liquid/Tags/ImageTagTests.cs`, `Steps/SocialImageStepTests.cs`; skipped with
`Assert.Skip` when libvips is not available, and CI runs them on `linux-x64` and `osx-arm64`): variants have the
requested widths and formats, names carry the hash, metadata stripped (output smaller than a `keep: all` save);
`<picture>` markup with `srcset`, `sizes`, width and height of the fallback; single `<img>`; SVG copied with size
from `viewBox`; light and dark sources; two pages using one image produce one output file; source edit re-renders
the page and renames the variant (`EarlyCutoffTests`); second build processes nothing; `image_url` and `image_size`;
`images` and `pictures` steps on a page with Markdown images; social image rendered from an SVG template with two
text blocks (checked by size and by sampling a few pixels), `skip`, `output` template, `social_image_url` filter,
missing font file error; missing libvips gives the install hint and fails only the units that need it.

**Acceptance.** Shared criteria plus: the tool packages for every RID include `NetVips.Native.<rid>`; the `any`
(CoreCLR) package has no native library and reports the hint; the AOT publish with the Satori GC passes the image
tests; `bench/README.md` records the variants per second on the benchmark machine. The Sample's docs section uses the
`image` tag and the `social-image` step.

**Where it lives and why.** Built-in (C# with NetVips): the check shows libvips works under AOT with both GCs, is
fast, and covers resizing, formats, SVG and text layout; a Node step with `sharp` would need the same native library
plus Node, and `satori` only adds flex layout over Pango's wrapping, which the SVG template plus text blocks cover.
The decision for a site that must keep its exact `satori` output is a `batch` command step over the pages (engine plan
5.2), which stays available; the migration plan of a site decides.

### 3.9 Asset fingerprinting

**What the site needs.** Stylesheets, scripts and fonts served with a content hash in their name so that the host
can cache them as immutable, references in layouts that follow the names, and a manifest for scripts that load other
assets at run time.

**Design.** `uses: assets` (reduce, over the files `inputs` select; `ReadsContent: true`, the files are small):

```yaml
assets: { inputs: "assets/**", uses: assets, with: { root: assets, output: assets, manifest: assets/manifest.json } }
pages:  { inputs: "docs/**", needs: [site-data, assets], uses: frontmatter }
```

For each file, computes the name `<name>.<hash8>.<ext>` (first 8 hex characters of the content hash) under
`output` (with `root` stripped), emits the renamed file, and contributes the site scope `assets`: an object
`{ "/assets/site.css": { url: "/assets/site.a1b2c3d4.css", hash: "...", length: 1234 } }` keyed by the original
url. `with.rewrite: ["**/*.css"]` (default: CSS files) rewrites `url(...)` references between the fingerprinted
files in dependency order (a CSS file referencing a font takes the font's final name before its own hash is computed;
cycles are an error). With `manifest`, the object is also written as a JSON file. The step is a reduce because the
map must be complete before any page renders: a page that references an asset waits for it, by the engine's barrier,
and no page renders twice.

Filters (`Filters/AssetFilters.cs`): `asset_url` (`{{ "/assets/site.css" | asset_url }}`: the fingerprinted url, an
error naming the template when the asset is unknown, or the input unchanged with `with.strict: false`),
`asset_inline` (the content of a small asset, for critical CSS and inline SVG icons; records the asset's hash). A
lookup reads one member of `assets`, so a page depends on the assets it references, not on the map.

**Tests** (`Steps/AssetsStepTests.cs`, `Filters/AssetFiltersTests.cs`): names and the scope; CSS `url()` rewriting
in dependency order and the cycle error; manifest; `asset_url` hit, miss error and non strict; `asset_inline`; a
changed stylesheet re-renders only the pages that referenced it; an unchanged one replays.

**Acceptance.** Shared criteria; the Sample references its stylesheet through `asset_url`.

**Where it lives and why.** Built-in reduce step and filters: a few hundred lines, no tool needed, and the map
must exist before pages render, which the engine's scopes express.

### 3.10 Dates from git

**What the site needs.** "Last updated" on every page and the edit link, from the last commit that touched the file,
not the checkout's modification time, which is meaningless on CI; and the first commit date as a publication date
when the front matter has none.

**Design.** `uses: git` (map, default match `**/*.md`, `**/*.html`):

```yaml
dates: { needs: pages, uses: git, with: { root: ".", updated: page.updated, created: page.created, ignore: ["**/CHANGELOG.md"] } }
```

`InitializeAsync` runs `git -C <root> log --format=%H%x00%ct%x00%an%x00%ae --name-status -z` once (through
`ProcessRunner`, 5 to 15 seconds on a repository with tens of thousands of commits; `--since` is not used because
first commit dates need the whole history, and CI checkouts need `fetch-depth: 0`, which the README says), parses it
into a `Dictionary<string path, (DateTimeOffset Updated, DateTimeOffset Created, string Author)>` following renames
(`R` entries map the old name to the new), and sets `StateHash` to the hash of the current `HEAD` and of the
index's dirty state for the files of the content folder (`git status --porcelain -z`, so an uncommitted edit uses the
file's modification time with `dirty: true`). `ProcessAsync` sets Own `page.updated`, `page.created`,
`page.author` (keys configurable in `with`), as `DateTimeOffset` values; a file git does not know (untracked) gets its
modification time and `dirty: true`. The git root is `with.root` relative to the source folder (default: found by
`git rev-parse --show-toplevel` from the source folder); without git, or outside a repository, the step warns once
and passes files through unchanged (`with.required: true` makes it an error).

A `StateHash` keyed on `HEAD` means every page's `git` unit re-runs after a commit (a few microseconds each: a
dictionary lookup), and the pages whose dates did not change replay downstream by early cutoff (same output refs).

**Tests** (`Steps/GitStepTests.cs`, which create a repository with `git init` in a temp folder, `Assert.Skip` without
git): updated and created dates, author, rename followed, untracked file dirty with modification time, ignore
globs, outside a repository, `StateHash` changes with a commit and with a dirty file, downstream replay when dates
are unchanged.

**Acceptance.** Shared criteria; the Sample's docs layout prints `page.updated`.

**Where it lives and why.** Built-in step calling `git` once: the only alternative is a git library (libgit2 is
native; a managed reader of pack files is a project of its own), and one process per build is nothing.

### 3.11 Link validation

**What the site needs.** Internal links (`href`, `src`, `srcset`) that point to a page, an asset or an anchor that
exists, reported as build errors with the page and the link, exclusions by glob and by prefix (generated sections,
a translation dashboard), fallback pages not reported, external links optional.

**Design.** Two steps, so that the per-page work streams and caches and only the check is a barrier:

```yaml
links:       { needs: layout, uses: links }                       # map: extracts links and anchors into metadata
check-links: { needs: [links, redirects, og], uses: check-links, with: { ignore: ["/reference/api/**", "/i18n/**"], external: false, anchors: true, warn: false } }
write:       { needs: [links, redirects, og], uses: write }       # write does not wait for the check: an error fails the build anyway
```

`uses: links` (map over HTML; `ReadsContent: true`): scans the content for `href`, `src`, `srcset`, `poster`,
`data-src` attributes and `id` attributes (plus `name` on `<a>`), and sets Own `links` (sorted, distinct: `{ url,
line }` with the url as written) and `anchors` (sorted ids). The scanner is the attribute scanner of 3.3 extended
(quoted attribute values, entity decoding, `<pre>`/`<code>`/`<script>` skipped).

`uses: check-links` (reduce over every page and asset, `ReadsContent: false`: it reads the `links` and `anchors`
metadata and the paths of every input, so a body edit that changes no link replays it; like `collect`, it emits no
files and contributes nothing, which is what the engine requires of a step that does not read contents, so it is a
terminal node of the graph: `write` needs the same steps it needs, and an error reported by the check fails the build
with exit code 1 whether or not the files were written): resolves each internal
url (relative to the page's url, query and fragment split, trailing slash and `index.html` normalized) against the
set of output paths of its inputs (`/a/b/` matches `/a/b/index.html`; `/a/b` matches `/a/b/index.html` and `/a/b.html`
when `with.extensionless: true`), then the fragment against the target's `anchors` when `anchors: true`. Reports
`error: check-links: /docs/a/index.html: broken link '/docs/b/' (line 42)` and `... missing anchor '#setup' in
'/docs/b/'`; `warn: true` reports warnings instead. `ignore` globs match the link target; `with.skip` globs match
the pages not scanned. External links (`http(s)://`) are checked only with `external: true`, with a 10 second
timeout and a concurrency of 8 through `HttpClient`, cached per url for the build, and never fail the build (warnings),
since the network is not deterministic. Pages with `fallback: true` are checked like the others (their links are the
root page's and resolve in the fallback locale through the same alternates logic: a link to `/docs/x/` from
`/ja/docs/y/` is valid when `/docs/x/` exists).

**Tests** (`Steps/LinksStepTests.cs`, `CheckLinksStepTests.cs`): extraction of the attributes with line numbers,
`srcset` candidates, ids, skipped elements; resolution rules (relative, absolute, `..`, query, fragment, trailing
slash, `index.html`, extensionless); missing page and missing anchor errors with line; ignore and skip globs; `warn`;
external off by default; a body edit without link changes replays the check (`EarlyCutoffTests`); determinism of
the error order.

**Acceptance.** Shared criteria; `douze build` on the Sample with a broken link added exits 1 with the message.

**Where it lives and why.** Built-in: all output paths and anchors are known in the build, no tool has that
knowledge without crawling. The split in two steps keeps the barrier metadata-only.

### 3.12 Outputs from collections: feeds, sitemaps, text exports, Markdown twins

**What the site needs.** A sitemap with every locale and fallback page, an RSS feed of the articles with their
rendered descriptions, text exports for language models (an index of pages with descriptions, a full export with
every page's content, a small one), a Markdown twin of every page served to agents on content negotiation (the
page's Markdown after Liquid, so that components are resolved and placeholders replaced), and JSON indexes of pages.

**Design.**

**Page content in collections.** `collect` gains `with.content: true`: the step then has `ReadsContent: true` and
each item exposes `content` (the file's content at that point of the workflow, as a string) besides `data`. The
option is for a collect step whose `match` selects the pages that need it (`docs/**`): holding 800 article bodies
is a few tens of megabytes; holding 32k API pages would be a gigabyte, and the plan says so in the README. A
template reads `item.content`, which records a dependency on that item's content hash (`ValueAccess` records the
member; the item's hash covers it).

**`collections.urls`.** `collect` always contributes `collections.urls`: an object keyed by `url` whose values are
the items (the same nodes as in `all`), for `localize` (3.6), the navigation filters (3.5) and templates that look
up a page by url (`collections.urls["/docs/x/"].data.title`). Reading one member records that member only.

**Twins: the `fork` step.** `uses: fork` (map) emits a copy of each file with another path and data, and passes the
original through:

```yaml
twins: { needs: render, uses: fork, match: "docs/**/*.md", with: { permalink: "{{ url | replace: 'index.html', 'index.md' }}", data: { markdown: false, liquid: false, layout: false, twin: true } } }
```

After `render` (Liquid done, Markdown source with shortcodes resolved to HTML raw regions: the twin strips the
region markers with `with.strip-regions: true`, default, so the twin is Markdown with inline HTML where a component
was) and before `markdown`; the copy's `markdown: false` keeps it Markdown, `layout: false` keeps it bare, and its
path is the Markdown twin next to the HTML (`/docs/x/index.md`). `data` is merged over Own; `permalink` is a Liquid
template over the file's model; `with.content: "{{ ... }}"` renders a template as the copy's content instead of
keeping the original's (a front matter header for agents: `---\ntitle: ...\n---\n{{ content }}`). A twin of an HTML
page is possible the same way (`match: "**/*.html"`, after `layout`, with a template that strips the chrome: not
recommended, the site keeps Markdown sources for that).

**Templates for the rest.** Feeds, sitemaps and text exports are pages (`sitemap.xml`, `rss.xml`, `llms.txt`,
`llms-full.txt`) written as Liquid templates with `permalink`, `liquid: true`, `layout: false`, rendered after
`collect` (they are in the default workflow's `render` already). Filters they need (`Filters/HtmlFilters.cs`,
`DataFilters.cs`): `xml_escape`, `absolute_url` (`site.url` + path), `relative_url`, `strip_html`, `date_to_xmlschema`
(a `date` alias with the RFC 3339 format), `date_to_rfc822`, `where_exp: "item", "item.data.locale == 'default'"`,
`group_by_exp`, `sort_by: "data.title"`, `index_by: "url"`, `jsonify` (alias of `json`). `locale_pages` (3.6)
expands fallbacks for the sitemap. A text export of every page reads `item.content` of a `collect` step with
`content: true` (the content after `markdown`, before `layout`, when the collect step runs there: `collect-docs:
{ needs: markdown, uses: collect, match: "docs/**/*.html", with: { content: true, name: docs } }`; `with.name`
(default `collections`) is the scope key, so a second collect step does not shadow the first).

**Tests** (`Steps/CollectStepTests.cs`, `ForkStepTests.cs`, `Filters/HtmlFiltersTests.cs`, `DataFiltersTests.cs`): `content:
true` exposes content and makes the step content dependent; `collections.urls` members; a second collect step with
`name`; fork path template, data merge, region markers stripped, `content` template, original passed through,
`markdown: false` honored downstream; each filter; a fixture site writes `sitemap.xml`, `rss.xml` and `llms.txt`
from templates and the test checks them (`CommonTests`).

**Acceptance.** Shared criteria; the Sample writes a sitemap, a feed and Markdown twins.

**Where it lives and why.** Small built-in changes and filters; the outputs themselves are templates, which is
what a static site generator is for. Not a `sitemap` step: a template is 20 lines and the site controls the format.

### 3.13 Redirects

**What the site needs.** About 180 old urls redirected to new ones, 14 of them in a locale, one of them to a target
computed from data (the latest release notes page), served as real redirects by the host and as stub pages for
static hosting and for crawlers that read the stubs.

**Design.** `uses: redirects` (reduce over nothing: `inputs: "_data/redirects.yml"` or the key `with.data: redirects`
resolved through the scopes, after `site-data`):

```yaml
redirects: { needs: site-data, uses: redirects, with: { data: redirects, template: redirect.liquid } }
```

The data is a mapping `from: to` (`to` may be a Liquid template over the site data: `"/whats-new/{{ releases |
last | map: 'slug' | first }}/"`) or a list of `{ from, to, status }`. The step emits one page per entry at the
`from` path (`/old/` -> `/old/index.html`), rendered from `template` (default: an embedded `<meta http-equiv="refresh"
content="0; url=...">` page with a link and `<link rel="canonical">`) with `from`, `to` and `status` in the model,
plus `redirects.json` (`[{ from, to, status }]`, sorted) at `with.manifest` for the host to import at startup.
A `from` that collides with a page is reported by `write`'s duplicate rule. An entry whose `to` is not a page of the
site is reported by `check-links` when it runs after (`redirects` outputs have `links` metadata set by the step).

**Tests** (`Steps/RedirectsStepTests.cs`): stub pages at `from`, template model, computed `to`, manifest, list and
mapping forms, status in the manifest, collision with a page reported by `write`.

**Acceptance.** Shared criteria; the Sample has two redirects.

**Where it lives and why.** Built-in, trivial; the manifest is what a host needs to serve 301s, which a stub cannot.

### 3.14 Diagnostics from templates and front matter

**What the site needs.** A template that detects a bad page (an unresolved API reference, a missing required field,
a deprecated component) must fail the build with the page and a message, not render silently; required front matter
fields per folder; warnings that do not fail.

**Design.** Tags (`Tags/DiagnosticTags.cs`): `{% error "unknown API reference " | append: name %}` throws a
`DouzeException` with the message, which the step reports with the page and the tag's position; `{% warn %}` logs a
warning with the page. `frontmatter` gains `with.require: [title, description]` (keys that must resolve for every
consumed file, through the scopes, so a folder data file can provide them) and `with.schema` is not added (open
question 7.5). `liquid` gains `with.strict: true` (Fluid's `StrictVariables`: an undefined variable is an error,
rather than nil; off by default, as Liquid sites expect nil).

**Tests**: error tag fails the unit with the page and position, warn tag logs, `require` missing key message
with the file, satisfied by folder data, `strict` on an undefined variable.

**Where it lives and why.** Tags and options: a few lines each.

### 3.15 Fragment cache

**What the site needs.** The API reference renders a sidebar of the whole package (namespaces, types, member kinds:
up to 1,800 entries) on every one of a package's pages (24k pages, about 10 per package on average, 2,500 for the
largest), and the articles render the same 530-link sidebar on 8k pages: rendering such fragments once per key
instead of once per page.

**Design.** `{% cache key %}...{% endcache %}` (`Tags/CacheTag.cs`): renders its body once per `(template content
hash, key)` for the build and writes the same string on every other render with that key. The key is any expression
(`{% cache package.name %}`, `{% cache "sidebar" | append: page.locale %}`). The body's dependencies (what it resolved
through the scopes and the templates it included) are recorded by the first render into a `DependencySet` stored with
the fragment, and added to every later unit that uses it (`DependencyTracker.Add`), so that a page that reuses the
fragment still depends on what the fragment read. A fragment that read the page's own metadata (`page.url`, `title`)
is an error in the first render when `with.strict` is on, and otherwise a documented foot-gun: the body must depend on
the key only (the README says: pass what varies through the key, mark the active item outside the cached block, or
with a cheap `replace` on the fragment: `{{ fragment | replace: 'data-url="' | append: page.url ... }}`). Memory: one
string per key (the largest sidebar is about 200 KB, 180 packages: 36 MB).

**Tests** (`Tags/CacheTagTests.cs`): rendered once for two pages with the same key (a counter in a fake filter),
separate keys, dependencies of the fragment propagate to the second page (a data edit re-renders both), key from an
expression, nested cache blocks, no sharing across templates with different content.

**Where it lives and why.** A Liquid tag: it is the Hugo and Jekyll pattern, and the engine's dependency sets make
it correct for incremental builds.

### 3.16 Watch mode and dev server

Phase 6 of the engine plan (`docs/workflow-engine-plan.md`, 7.5 and phase 6): `douze watch` and `douze serve`, the
`FileWatcher` with debounce, `IncrementalBuilder.RebuildAsync` on the in-memory results plus the disk cache, deletions
and renames through provenance, `write` skipping unchanged files and deleting removed outputs, the `DevServer` on
`HttpListener` (`TcpListener` fallback) with live reload over SSE, the key test `IncrementalEqualsCleanTests`
extended to adds, deletes and renames, and the items the phase 5 review left to it. This plan adds what the features
above need from it:

- **Workers survive rebuilds.** A `worker` command step keeps its process between rebuilds (the highlighter of 3.7
  loads a 6.5 MB declaration file at startup); `InitializeAsync` is called once per watch session and
  `DisposeAsync` at exit, with a `ReinitializeAsync` when the step's `sources` changed (the script edited: restart the
  worker). Batch and filter steps are unchanged.
- **Build-scoped state is rebuilt per rebuild.** `ShortcodeRegistry` rescans when a file of the shortcodes folder
  changed (the parser is recreated, every template re-parsed: a shortcode edit is a full re-render, acceptable);
  `GitStep` recomputes its `StateHash` (dirty files) per rebuild; the `cache` tag's fragments are per build;
  `ImageProcessor` stays.
- **Serve semantics a documentation site needs.** The dev server serves `/a/b/` from `/a/b/index.html`, `/a/b` with a
  redirect to `/a/b/` when the folder exists (the host's behavior), `404.html` for unknown paths when it exists,
  `Content-Type` by extension (the `ContentTypes` table), `Accept: text/markdown` negotiation to the Markdown twin
  when `with` `serve.markdown: true` in `douze.yml` (4.3), redirects from `redirects.json` when it exists (3.13),
  and `--open` to open the browser. Live reload: the SSE event lists changed urls; the client reloads when its url
  or a stylesheet it uses changed.
- **Errors in watch mode** are printed and the previous output stays; the next change retries. The live view shows
  `watching N files` between builds.

**Tests.** The engine plan's (`ChangeSetTests`, `IncrementalBuilderTests`, `DevServerTests`, `IncrementalEqualsCleanTests`)
plus: worker kept across two rebuilds (one process start), restarted when its script changes; shortcode edit
rescans; `GitStep` state per rebuild; dev server folder redirect, 404 page, Markdown negotiation, redirect manifest.

**Acceptance.** The engine plan's, plus: `douze serve` on the Sample with the code worker configured highlights a
new fence within a second of saving (manual check noted in the PR).

### 3.17 Search

Nothing to build (see also 3.18 for token substitution): the pattern is a `batch` command step after `write` (`search: { needs: write, run: "npx pagefind
--site {{ site }}" }`, engine plan 4.7), with `data-pagefind-body`, `data-pagefind-ignore` and `data-pagefind-meta`
attributes written by the layouts, and `pagefind: false` pages excluded by the layout omitting the attribute. A site
that wants its own index writes it from a template over `collections` (3.12, `jsonify`). The README documents both.

**Where it lives and why.** Command step: the indexer and its client library are one product; a C# indexer would
not produce the same client bundle.

### 3.18 Token substitution

**What the site needs.** Version placeholders (`%VERSION%`, about 110 usages in 65 files) replaced by values from
data everywhere in a page, including inside code fences (`dotnet add package X --version %VERSION%`), which fence
protection (3.1) hides from Liquid, and in the Markdown twins (3.12).

**Design.** `uses: replace` (map, default match `**/*.md`, `**/*.html`):

```yaml
versions: { needs: pages, uses: replace, with: { tokens: { "%VERSION%": "{{ versions.current }}", "%VERSION_PREVIEW%": "{{ versions.preview }}" } } }
```

Each value is a Liquid template rendered once per unit with the file's model (so a value can depend on the page or
on data; the lookups are recorded), and every token is replaced in the content with an ordinal search (no regex;
tokens are literal). A token with no occurrence costs one scan. The step runs before `render` so the twins and the
Markdown pass see the values; a page that must show the literal token writes it with a zero-width join in the source
(documented), or the step's `with.skip` glob excludes it.

**Tests** (`Steps/ReplaceStepTests.cs`): replacement inside a fence and in prose, value from data with a recorded
dependency (a version bump re-runs the pages that use the token: `EarlyCutoffTests`), no occurrence passes the
content instance through, `skip`.

**Where it lives and why.** Built-in: ten lines of logic, and it must see the content before Liquid protects fences.
Owned by phase S2.

---

## 4. `douze.yml` additions

### 4.1 `site`

```yaml
site:
  shortcodes: _includes/shortcodes      # folder of 3.1; default <includes>/shortcodes
  locales:                              # 3.6; absent: a single locale, no prefixes
    default: en
    root: docs
    fallback: true
    languages: { ja: { label: 日本語, lang: ja }, ... }
    strings: i18n
  serve:                                # 3.16
    port: 8080
    markdown: true                      # Accept: text/markdown negotiation to twins
    redirects: redirects.json           # manifest of 3.13, relative to the output folder
```

`WorkflowSchema.SiteKeys` grows with `shortcodes`, `locales` and `serve`; the loader validates each field with the
messages pattern of the engine plan 4.5 (`unknown key 'x' in 'site.locales'; expected 'default', 'root', 'fallback',
'languages' or 'strings'`, `'languages' must be a mapping of locale folders to settings`, `'default' must be a locale
folder name`...). `site.locales` values reach steps through `BuildContext.Locales` and are part of the config hash of
the `localize` step (the step copies the settings it uses into its `ConfigHash` through `StepDefinition.With`
completion in `WorkflowSource`, so a locale change re-runs it).

### 4.2 `markdown`

A new top-level section (next to `site` and `steps`), since it configures rendering and is read by tags as much as
by the `markdown` step:

```yaml
markdown:
  extensions: [pipetables, footnotes, tasklists, definitionlists, attributes, autolinks, alerts, customcontainers]
  aside: { tag: aside, class: aside, kinds: [note, tip, caution, danger], titles: { note: Note, tip: Tip, caution: Caution, danger: Danger }, titles-from: i18n }   # titles-from: localized defaults from <key>.<locale>.aside.<kind>
  links: true                           # .md link rewriting
  code: { class-prefix: language-, passthrough: [mermaid] }
  images: { widths: [640, 1280], formats: [webp], quality: 80, output: assets/images, loading: lazy }   # defaults of the image tag
```

`WorkflowSchema.TopLevelKeys` becomes `site`, `markdown`, `steps`; `WorkflowLoader` reads the section into
`MarkdownOptions` with positions for errors. The section's canonical JSON is part of `BuildContext`'s
"render configuration hash", which `liquid`, `markdown`, `layout` and every step that renders templates fold into
their `StateHash` (2.2): a Markdown option change re-renders everything that rendered Markdown, which is right.

### 4.3 New built-in steps

`WorkflowSchema` and `BuiltInSteps.Registry` list: `headings`, `localize`, `assets`, `images`, `image-map`,
`pictures`, `social-image`, `git`, `links`, `check-links`, `fork`, `redirects`, `replace`. `--print-workflow` prints them like
the others. The default workflow (engine plan 4.6) does not change: a site without `douze.yml` gets no new step
(headings and twins are opt-in), except that `collect` contributes `collections.urls` and the `markdown` step honors
`markdown: false`.

### 4.4 A documentation site's workflow

The shape a documentation site ends up with (the migration plan of a site has the real one):

```yaml
site: { content: content, data: data, includes: templates, url: https://example.com, shortcodes: templates/shortcodes,
        locales: { default: en, root: docs, fallback: true, languages: { ja: { label: 日本語, lang: ja } } } }
markdown: { extensions: [pipetables, footnotes, tasklists, attributes, autolinks, alerts, customcontainers], code: { passthrough: [mermaid] } }

steps:
  site-data:   { inputs: "data/**/*.{yml,json}", uses: data, with: { scope: site } }
  folder-data: { inputs: "content/**/*.{yml,json}", uses: data, with: { scope: folder, root: content } }
  assets:      { inputs: "assets/**", uses: assets, with: { root: assets, output: assets } }
  pages:       { inputs: ["content/**", "!content/**/*.{yml,json}"], needs: [site-data, folder-data, assets], uses: frontmatter, with: { require: [title] } }
  dates:       { needs: pages, uses: git }
  versions:    { needs: dates, uses: replace, with: { tokens: { "%VERSION%": "{{ versions.current }}" } } }
  permalink:   { needs: versions, uses: permalink, with: { root: content } }
  collections: { needs: permalink, uses: collect, match: "content/docs/**" }
  localize:    { needs: [permalink, collections], uses: localize }
  paginate:    { needs: localize, uses: paginate }
  render:      { needs: paginate, uses: liquid }
  twins:       { needs: render, uses: fork, match: "docs/**/*.md", with: { permalink: "{{ url | replace: 'index.html', 'index.md' }}", data: { markdown: false, layout: false } } }
  markdown:    { needs: twins, uses: markdown }
  headings:    { needs: markdown, uses: headings, with: { anchors: true } }
  code:        { needs: headings, run: "node scripts/code-worker.mjs", mode: worker, match: "docs/**/*.html", sources: ["scripts/code-worker.mjs"] }
  og:          { needs: localize, uses: social-image, match: "docs/**/*.md", with: { template: og/card.svg.liquid, output: "og{{ locale_url }}index.png", ... } }
  packages:    { inputs: "data/api/pkgs/*.json", uses: parse, with: { as: package } }
  api:         { needs: [packages, site-data], uses: liquid, group-by: file, with: { template: api/package.liquid, permalink: "reference/api/{{ group.files[0].data.package.slug }}/" } }
  layout:      { needs: [code, api], uses: layout }
  links:       { needs: layout, uses: links }
  redirects:   { needs: site-data, uses: redirects, with: { data: redirects } }
  check-links: { needs: [links, og, redirects], uses: check-links, with: { ignore: ["/reference/api/**"] } }
  write:       { needs: [links, og, redirects], uses: write }
  search:      { needs: write, run: "npx pagefind --site {{ site }}" }
```

---

## 5. Phases

### 5.0 Rules shared by every phase

The engine plan's 8.0 applies (tests, Sample comparison, AOT publish and smoke test, docs, PR description). Model and
effort: the default is Opus at Medium; phases marked **High** run at High effort; phases marked **review** end with a
review pass by the planning model before the phases that depend on them start. The review checks this plan's rules
(1.3) and the engine's invariants. Phases that touch disjoint files run in separate worktrees at the same time; each
phase lists what it owns, and a phase does not edit a file owned by a phase that can run in parallel with it: the
first phase creates the registration points (one file per feature area) that make this possible, as phase 1b of the
engine plan did.

```
S1 -> { S2, S3, S4, S5 } -> S6 -> S7
```

S6 can start once S4 is merged (they share `WriteStep.cs` and the scheduler); S2, S3 and S5 need only S1.

### Phase S1: Markdown pipeline, shortcodes, engine additions (High, review)

**Goal.** Sections 3.1, 3.2, 3.4, 3.14 and the engine additions of 2.2 and 2.3; the registration points of every
later phase.

**Owns.** `Douze/Markdown/*` (all of 2.1 except `HeadingScanner.cs` and `Slugger.cs`), `Douze/Liquid/LiquidOptions.cs`,
`Douze/Liquid/Tags/ShortcodeRegistry.cs`, `ShortcodeTag.cs`, `MarkdownTags.cs`, `DiagnosticTags.cs`,
`Douze/Liquid/ShortcodeModel.cs`, `Douze/Liquid/Filters/MarkdownFilters.cs`, `Douze/Liquid/AmbientKeys.cs`,
`Douze/Workflow/BuildContext.cs`, `StepContext.cs`, `IStep.cs`, `Cache/ResultKey.cs`, `Scheduling/Scheduler.cs`
(state hash, added outputs, identical duplicates), `Steps/WriteStep.cs` (identical duplicates only),
`Steps/LiquidStep.cs`, `Steps/MarkdownStep.cs`, `Steps/FrontMatterStep.cs`, `Yaml/WorkflowSchema.cs`,
`Yaml/WorkflowLoader.cs`, `Yaml/WorkflowFile.cs` (`markdown:` section, `site.shortcodes`), `Cli/WorkflowSource.cs`,
`README.md` (sections "shortcodes", "markdown", "diagnostics"; placeholders for "headings", "navigation", "locales",
"code", "images", "assets", "git", "links", "outputs", "redirects", "cache", "search", "watch and serve"), the
Sample's docs section skeleton (`Sample/_content/docs/`, `Sample/_includes/shortcodes/`, `Sample/douze.yml` with the
explicit workflow that later phases extend: the Sample keeps building identically through it, which the comparison
script checks since `--print-workflow` of the file equals the default workflow plus the new steps).

**Registration points created for the other phases** (empty files or stubs): `Douze/Liquid/Filters/HtmlFilters.cs`,
`NavigationFilters.cs`, `LocaleFilters.cs`, `AssetFilters.cs`, `DataFilters.cs`, `ImageFilters.cs`,
`Douze/Liquid/Tags/CacheTag.cs`, `ImageTag.cs`, each with a static `Register(TemplateOptionsBuilder)` or
`Register(FluidParser)` that `LiquidOptions` calls; `Douze/Workflow/BuiltInSteps.cs` lists the new step ids mapped to
factories in files the later phases own (`Steps/HeadingsStep.cs` etc. as stubs throwing "not available yet", like
`CommandStepFactory` in the engine's phase 1); `Douze/Workflow/LocaleSet.cs` (settings only); `Douze/Images/` folder
with `ImageProcessor.cs` stub and the `NetVips` package references in `Directory.Packages.props` and `Douze.csproj`
(so S4 does not edit the project files: the native package per RID is added here, verified by the AOT publish in CI).

**Tests.** 3.1, 3.2, 3.4, 3.14 tests; `ResultKeyTests.ChangesWithStateHash`, `SchedulerTests.AddedOutputsAreUnitOutputs`,
`SchedulerTests.IdenticalFileFromTwoUnitsEntersOnce`, `WriteStepTests.IdenticalDuplicateIsWrittenOnce`,
`WorkflowLoaderTests` for the `markdown:` section and `site.shortcodes` (messages with positions).

**Acceptance.** Shared criteria; the Sample's docs section renders shortcodes (tabs, aside, steps, badge), an
include, a table, an alert, a fence with meta; `scripts/compare-sample.sh` shows only the documented differences
(the new section's files; the `index.html` of the Sample is unchanged); the AOT publish for `osx-arm64` and
`linux-x64` includes the native library and has no new IL warnings.

### Phase S2: headings, code contract, outputs and redirects (Medium)

**Goal.** Sections 3.3, 3.7, 3.12, 3.13, 3.17, 3.18.

**Owns.** `Douze/Markdown/HeadingScanner.cs`, `Slugger.cs`, `Douze/Workflow/Steps/HeadingsStep.cs`,
`ForkStep.cs`, `RedirectsStep.cs`, `ReplaceStep.cs`, `CollectStep.cs`, `Douze/Liquid/Filters/HtmlFilters.cs`, `DataFilters.cs`,
`docs/code-worker.mjs` and `docs/package.json` (the reference worker and its modules, used by the tests),
`Douze.Tests/Commands/CodeWorkerTests.cs`, the README sections "headings", "code", "outputs", "redirects", "search",
the Sample's docs layout (`Sample/_includes/docs.html`: table of contents, and `Sample/_content/sitemap.xml`,
`rss.xml`, `llms.txt` templates; the Sample's `douze.yml` gains `headings`, `twins`, `redirects` and the `code` step
guarded by a `scripts/code-worker.mjs` copy; CI installs its modules).

**Tests.** 3.3, 3.7, 3.12, 3.13 tests.

**Acceptance.** Shared criteria; the Sample output gains the table of contents, twins, feed, sitemap, text export,
redirect stubs, documented in the PR; the code worker test runs in CI on Linux.

### Phase S3: navigation and locales (Medium)

**Goal.** Sections 3.5 and 3.6.

**Owns.** `Douze/Liquid/Filters/NavigationFilters.cs`, `LocaleFilters.cs`, `Douze/Workflow/Steps/LocalizeStep.cs`,
`Douze/Workflow/LocaleSet.cs`, the README sections "navigation" and "locales", the Sample's `_data/sidebar.yml`,
`_data/i18n/*.json`, a `docs/ja/` folder with one translated page, and the sidebar and language switcher of the docs
layout (a separate include file, `Sample/_includes/docs-nav.html`, so S2's layout edits do not conflict).

**Tests.** 3.5 and 3.6 tests.

**Acceptance.** Shared criteria; the Sample has fallback pages for its second locale and a sidebar with the active
trail and previous and next links.

### Phase S4: assets, images, social images, git dates (High, review)

**Goal.** Sections 3.8, 3.9, 3.10 and the NetVips bridge.

**Owns.** `Douze/Images/*`, `Douze/Liquid/Tags/ImageTag.cs`, `Douze/Liquid/Filters/ImageFilters.cs`,
`AssetFilters.cs`, `Douze/Workflow/Steps/AssetsStep.cs`, `ImagesStep.cs` (`images`, `image-map`, `pictures`),
`SocialImageStep.cs`, `GitStep.cs`, `Douze/Workflow/Commands/ProcessRunner.cs` (a `RunAsync` overload for the git
command's large stdout, streamed), the README sections "images", "assets", "git", `bench/README.md` (the NetVips
numbers and the tool package sizes), `.github/workflows/pr.yml` and `publish.yml` (the image tests on both OSes, the
per-RID packages checked for the native library), the Sample's images and `og/card.svg.liquid`.

**Tests.** 3.8, 3.9, 3.10 tests.

**Acceptance.** Shared criteria; the tool packages of every RID carry the native library and the `any` package
reports the hint; `osx-arm64` and `linux-x64` image tests pass in CI with the Satori publish; `bench/README.md` has
the image numbers; the Sample's docs section has a `<picture>` and a social image.

### Phase S5: links, fragment cache (Medium)

**Goal.** Sections 3.11 and 3.15.

**Owns.** `Douze/Workflow/Steps/LinksStep.cs`, `CheckLinksStep.cs`, `Douze/Liquid/Tags/CacheTag.cs`, the README
sections "links" and "cache", the Sample's `douze.yml` lines for `links` and `check-links` (a one-line addition at a
marked place, to avoid conflicts with S2 and S3, which add their own lines at their own marks).

**Tests.** 3.11 and 3.15 tests.

**Acceptance.** Shared criteria; a broken link in the Sample fails the build (a test, not a committed broken link).

### Phase S6: watch mode and dev server (High, review)

**Goal.** Section 3.16: the engine plan's phase 6 plus the additions.

**Owns.** The engine plan's phase 6 ownership (`Douze/Workflow/Watch/*`, `Douze/Cli/WatchCommand.cs`,
`Douze/Cli/CommandLine.cs`, `Douze/Workflow/Steps/WriteStep.cs`, `Douze/Liquid/TemplateLoader.cs`) plus
`Douze/Workflow/Commands/WorkerCommandStep.cs` (process kept across rebuilds), `Douze/Liquid/Tags/ShortcodeRegistry.cs`
(rescan), `Douze/Workflow/Scheduling/Scheduler.cs` (rebuild entry points), the README section "watch and serve",
`Yaml/WorkflowSchema.cs` (`site.serve`).

**Tests.** 3.16 tests and the engine plan's phase 6 tests.

**Acceptance.** The engine plan's phase 6 acceptance plus 3.16's.

### Phase S7: documentation site sample, benchmarks, release (Medium)

**Goal.** A second sample (`Sample.Docs/`, a documentation site of about 100 pages with two locales, the API data of
the benchmark harness as its reference section, every feature of this plan in use, and the code worker), used as the
end-to-end test of the features together (`CommonTests`-style assertions on its output, and the comparison script
extended to it); the benchmark harness grows a `docs` scenario (the synthetic site with shortcodes, headings, twins,
locales with fallback, links checked), its numbers recorded; `AGENTS.md` and `README.md` complete; the first tagged
release (`v*`) with the per-RID packages that include the native library.

**Owns.** `Sample.Docs/*`, `bench/*`, `scripts/compare-sample.sh` (second sample), `AGENTS.md`, `README.md` (final pass),
`.github/workflows/*`.

**Acceptance.** `bench/README.md` has the `docs` scenario: the synthetic 32k site with the features on builds in
under 60 s clean and under 3 s with no change, the 8k-article shape with fallbacks, twins, headings and links in
under 20 s; the native AOT packages of every RID are published to the test feed by a dry run of `publish.yml`.

### 5.1 Summary table

| Phase | Depends on | Parallel with | Model and effort | Review pass |
|---|---|---|---|---|
| S1 Markdown pipeline, shortcodes, engine additions | engine phase 5 and the bounded channels (merged) | | Opus, High | yes |
| S2 headings, code contract, outputs, redirects | S1 | S3, S4, S5 | Opus, Medium | no |
| S3 navigation, locales | S1 | S2, S4, S5 | Opus, Medium | no |
| S4 assets, images, social images, git | S1 | S2, S3, S5 | Opus, High | yes |
| S5 links, fragment cache | S1 | S2, S3, S4 | Opus, Medium | no |
| S6 watch and serve | S4 (and S1) | S2, S3, S5 while they are open | Opus, High | yes |
| S7 docs sample, benchmarks, release | S2 to S6 | | Opus, Medium | final review |

### 5.2 Review passes

The planning model reviews S1, S4 and S6 before dependents start, and the whole at S7. Each review checks: no scope
read outside `TryResolve`; every tag and filter that reads a file records it; `StateHash` set wherever a step reads
build-scoped state; `AddOutput` files have provenance and content addressed names; nothing mutates a published node;
every reduce input sorted; messages in the engine's format; no reflection the AOT compiler cannot see (the publish
log); the native library optional at startup; `IncrementalEqualsCleanTests` extended with the features (shortcode
edit, include edit, image edit, data edit, locale added) and green on 25 seeds.

### 5.3 The Sample

The Sample stays a no-configuration site for the comparison script's base build, and gains a `douze.yml` in S1 whose
`steps` reproduce the default workflow plus the new steps on a new `docs` section; `scripts/compare-sample.sh`
compares the `docs` output between phases too (the base build of a phase's base commit builds the docs section as
that commit did). The docs section is the living documentation of the features: each page says which feature it
exercises.

### 5.4 Where each feature lives

| Feature | Lives in | Reason in short |
|---|---|---|
| Shortcodes, raw regions, fence protection | Liquid tags + Markdig extension + `liquid` step | needs the page's variables; Fluid's block API is enough |
| Markdown pipeline, asides, fence markup, link rewriting | Markdig extensions, `markdown:` section | Markdig has it all |
| Headings and TOC | built-in step over HTML | one pass sees every heading |
| Markdown includes, `markdownify` | Liquid tags and filters | the page's context |
| Navigation | Liquid filters over data | pure functions, data stays YAML |
| Locales | built-in step + filters | copies are files; `collections.urls` makes it precise |
| Code highlighting, twoslash | command step contract (worker) | grammars, themes, TypeScript compiler, exact output parity |
| Images, social images | built-in (NetVips) | verified under AOT, fast, covers resize, formats, SVG, text |
| Asset fingerprinting | built-in reduce step + filters | the map must exist before pages render |
| Git dates | built-in step running `git` once | a git library is native or a project |
| Link validation | two built-in steps | all paths and anchors are known in the build |
| Feeds, sitemap, text exports | templates + filters; `collect` content; `fork` twins | that is what templates are for |
| Redirects | built-in step | trivial; the manifest is for the host |
| Token substitution | built-in step | must run before fence protection |
| Diagnostics | tags and options | a few lines |
| Fragment cache | Liquid tag | dependency sets make it correct |
| Search | command step (batch) | indexer and client are one product |
| CSS pipeline, bundling | command step (batch) | not a site generator's job |

---

## 6. Execution roadmap across both repositories

The roadmap of a site adopting Douze and of Douze itself, ordered, with dependencies. "Site" phases (M0 to M8) are
defined in the site's own migration plan; what each needs from Douze is listed here so that both plans agree.

| Order | Work | Repository | Needs | Can run in parallel with |
|---|---|---|---|---|
| 1 | S1: Markdown pipeline, shortcodes, engine additions | Douze | engine phase 5 + bounded channels (done) | M0 (site: generator changes: page index, cross-reference map, slugs, pre-highlighted signatures, Markdown twins data), M1 (site: Playwright gate against the current build, structural diff harness) |
| 2 | S2, S3, S4, S5 in parallel worktrees | Douze | S1 | M2 (site: API reference templates on the `parse` + `liquid group-by` workflow, with the `cache` tag arriving with S5) |
| 3 | S6: watch and serve | Douze | S4 | M3 (site: chrome port: layouts, includes, styles, client scripts, using S1 to S5 from the preview feed), M4 (site: converter script MDX to Markdown + shortcodes, dry runs) |
| 4 | S7: docs sample, benchmarks, first release | Douze | S2 to S6 | M5 (site: articles converted, locales, outputs: feeds, twins, social images, search), M6 (site: hosting: redirects manifest, immutable assets, Markdown negotiation unchanged) |
| 5 | Fixes found by the site's parity runs | Douze | S7 | M7 (site: parity: Playwright suite green on Douze output, diff harness, performance in CI) |
| 6 | | Site | Douze release | M8 (site: cutover: CI builds with Douze, old generator removed) |

Douze consumes nothing from the site; the site consumes Douze from the preview feed (every push to `main`) until the
first release. The Douze features the site needs first are S1 (the whole article conversion depends on shortcodes)
and S5's `cache` tag (the API sidebar); S4's images are needed by M3; S6 is needed for authors, not for the cutover
(CI builds once). Recommended model and effort for the site's phases are in its plan; they follow the same rule
(Opus Medium by default, High for the chrome port and the converter, review by the planning model after M2, M4 and M7).

---

## 7. Risks and open questions

Each item has a recommended answer; implementers use it unless the user decides otherwise.

1. **Raw regions and Markdig's HTML block rules.** A block shortcode's HTML is protected, but an inline shortcode
   whose output starts at the beginning of a line with a block-level tag (`<div>`) still starts an HTML block that ends
   at a blank line. Recommended: `display: block` on such shortcodes (3.1), and the README says which to choose; the
   converter of a site sets it per component.

2. **Fence protection changes line numbers in error messages** when the placeholder does not keep the line count.
   Recommended: the placeholder keeps the line count (3.1), tested.

3. **Headings with Markdown inside shortcode bodies get ids from the inner pass's text**, identical to the outer pass
   since ids are assigned on the final HTML (3.3). A heading inside a `cache` fragment (3.15) is rendered once and
   its id would be deduplicated against the first page only. Recommended: document that cached fragments must not
   contain headings; `headings` runs before `layout`, where fragments live, so page bodies are unaffected.

4. **Native library in the tool packages** adds 7 to 8 MB per RID package and 16 to 20 MB on disk; Linux containers
   without fonts need `fontfile` for social images; `linux-musl` and `win-x86` have no tool package today (the
   `ToolPackageRuntimeIdentifiers` list stays as it is). Recommended: ship the library in every RID package (a tool
   that cannot process images is a surprise), keep `any` without it with the hint; verify Linux text rendering in S4
   on CI (the check ran on macOS only).

5. **Front matter schemas.** The target site validates front matter with a schema library today. `with.require`
   covers presence; types and enums are not checked. Recommended: not in this plan; a `schema:` section mapping folders
   to JSON Schema files is a follow-up if the site's converter does not keep the front matter valid by construction.

6. **Locale-aware formatting without ICU.** The tool is published with `InvariantGlobalization`; `format_date` with
   month names per language needs data. Recommended: a built-in table for the languages .NET's invariant data lacks is
   not worth it; `format_date` takes a format string and the page's `lang` for the few things the invariant culture
   supports (numeric formats), and month and day names come from the site's `i18n` strings (`{{ date | format_date:
   "%d {month} %Y" | t_month }}` style helpers are left to the site). Revisit if a site needs more: enabling ICU adds
   about 1.5 MB per package.

7. **`collect` with `content: true` on large selections** holds every selected page's content at the barrier.
   Recommended: document the cost, and let `match` do the selecting; a streaming text export (a `reduce` step writing
   while it reads) is a follow-up.

8. **The code worker contract is HTML in, HTML out.** A worker that fails on one fence fails the page (the reference
   worker keeps the fence and warns instead). Recommended: keep the contract; it is the engine's worker protocol with
   no new message types.

9. **Social images without `satori`.** Pango wraps text; it does not do flex layout, emoji fonts or gradients
   (the SVG template does gradients). Recommended: the built-in step, with a `batch` command step on `satori` as the
   documented alternative for a site that must keep pixel parity with a previous renderer.

10. **`git log` on a large repository** with `--name-status` for every commit is seconds; a shallow CI clone gives
    wrong dates. Recommended: the README documents `fetch-depth: 0`; the step warns when the repository is shallow
    (`git rev-parse --is-shallow-repository`).

11. **Rebuild of every template when a shortcode changes** (3.16): acceptable for a first version; a parser that
    re-registers one tag is a Fluid feature (`FluidParser` registration is append-only today). Recommended: accept;
    propose the Fluid change (removing or replacing a registered tag) if authors edit shortcodes more than pages.

12. **A possible Fluid change: block tags whose body is captured raw.** Not needed by this plan (fence protection
    does it in Douze). Recommended: none.
