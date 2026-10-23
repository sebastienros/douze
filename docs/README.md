# Douze documentation site

The site at <https://sebastienros.github.io/douze/>, built by Douze itself from this folder (`douze.yml`). Every pull
request builds it with its own Douze on Linux, macOS and Windows and checks it (`.github/workflows/pr.yml`), and
`.github/workflows/docs.yml` deploys it from `main`. The design documents in this folder (`*-plan.md`, `code-hover-steps.md`) are not part
of the site.

```bash
dotnet run --project Douze -- serve -s docs    # http://localhost:8080/douze/, from the repository root
dotnet run --project Douze -- -s docs          # docs/_site; the site is docs/_site/douze
```

With an installed `douze`, `douze serve -s docs` does the same. The checks that CI runs, after a build:

```bash
cd docs
npm ci
npx playwright install chromium
npm run lint          # markdownlint over the pages
npm run check-links   # every internal link and anchor of docs/_site/douze
npm run test:e2e      # Playwright, against docs/_site served by a small static server
```

## Folders

| Folder | Contains |
|---|---|
| `_content/douze/` | The pages. GitHub Pages serves the project at `/douze/`, so the pages live under `douze/` and their urls start with it. |
| `_content/douze/tutorial/` | The blog tutorial, one page per unit. Its files are checked in Docker (`tests/docker/`). |
| `_content/douze/guide/` | Concept and how-it-works pages |
| `_content/douze/recipes/` | Short, task-oriented pages |
| `_content/douze/migrate/` | Migration guides from other generators |
| `_content/douze/reference/` | Exhaustive reference: command line, `douze.yml`, template data, Liquid, every built-in step |
| `_content/douze/assets/` | The theme's stylesheet, script and icons; `code.css` is written by the `highlight` step |
| `_includes/` | Layouts (`base.html`, `doc.html`), partials, and shortcodes (`shortcodes/tabs.liquid`, `tab.liquid`) |
| `_data/` | `sidebar.yml` (the navigation), `meta.yml`, `layout.yml` (the default layout) |
| `tests/` | Playwright tests (`e2e/`) and the Docker validation of the install page and the tutorial (`docker/`) |

## Writing

- Match the voice of the repository README: plain, precise and short. Say what Douze does, not how great it is. Use
  sentence case headings and avoid em dashes.
- Document what a user sees and does, not how Douze is built: no garbage collector, compiler mode (native AOT) or .NET
  SDK and runtime versions outside the contributing page. Requirements a user must install are fine (the .NET SDK for
  the `dotnet tool` route or `hover-csharp`).
- Every page starts with front matter: `title` (required, the build fails without it) and `description`. The layout
  renders the title as the H1: the body starts with a paragraph, then `##` sections.
- Every option, default and message must come from the code (`Douze/`), not from memory. When the README and the code
  disagree, the code wins.
- Code samples that a reader can run must work with the current `main`: build them before publishing them.
- Pages are rendered as Liquid before Markdown. Fences are safe, but Liquid in prose, tables or inline code runs: set
  `liquid: false` in the front matter of a page that shows Liquid syntax outside fences, or wrap it in
  `{% raw %}...{% endraw %}` on a page that uses shortcodes.
- Link to other pages with relative links to their `.md` file (`../reference/cli.md#options`): Douze rewrites them to
  urls, and the link check finds the broken ones.
- Asides: `:::note`, `:::tip`, `:::caution`, `:::danger` (`:::tip[A title]`), closed by `:::`. Fence titles:
  ```` ```yaml title="douze.yml" ````. Tabs: `{% tabs %}{% tab "macOS and Linux" %}...{% endtab %}{% endtabs %}`.
- A new page gets an entry in `_data/sidebar.yml`.
