using System.Text;

namespace Douze.Highlighting;

/// <summary>
/// The stylesheet of the default markup of the <c>highlight</c> step: the colors of the first theme, and those of the
/// first theme of the other type (dark or light) when the reader prefers it or the root element's <c>data-theme</c>
/// names its type. A token's color in theme <c>n</c> is the CSS variable <c>--n</c> of its span.
/// </summary>
internal static class CodeStylesheet
{
    public static string Write(IReadOnlyList<CodeTheme> themes)
    {
        var css = new StringBuilder();
        var first = themes[0];

        css.Append("""
            .code { position: relative; margin: 1rem 0; border-radius: 0.5rem; overflow: hidden; }
            .code pre { margin: 0; padding: 0.75rem 1rem; overflow-x: auto; background: var(--code-background); color: var(--code-foreground); }
            .code pre.wrap { white-space: pre-wrap; }
            .code code { display: block; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
            .code .line { display: inline-block; min-width: 100%; }
            .code .line.mark { background: var(--code-mark); }
            .code .line.ins { background: var(--code-ins); }
            .code .line.del { background: var(--code-del); }
            .code .line-number { display: inline-block; min-width: 2ch; margin-right: 1.5ch; text-align: right; opacity: 0.5; user-select: none; }
            .code mark { background: var(--code-mark); color: inherit; }
            .code ins { background: var(--code-ins); text-decoration: none; }
            .code del { background: var(--code-del); text-decoration: none; }
            .code-title { padding: 0.4rem 1rem; font-size: 0.85em; background: var(--code-title-background); color: var(--code-foreground); }
            .code-copy { position: absolute; top: 0.4rem; right: 0.4rem; font-size: 0.75em; opacity: 0.6; }
            .code:not(:hover) .code-copy { opacity: 0; }
            .code-section summary { cursor: pointer; opacity: 0.7; }
            .twoslash-hover { position: relative; border-bottom: 1px dotted currentColor; }
            .twoslash-popup { display: none; position: absolute; left: 0; top: 100%; z-index: 10; min-width: max-content; max-width: 40rem; padding: 0.5rem; border-radius: 0.3rem; background: var(--code-background); box-shadow: 0 2px 8px rgb(0 0 0 / 30%); white-space: normal; }
            .twoslash-popup .code { margin: 0; }
            .twoslash-hover:hover > .twoslash-popup, .twoslash-hover:focus > .twoslash-popup { display: block; }
            .twoslash-error-underline { text-decoration: underline wavy red; }
            .twoslash-error, .twoslash-query { display: block; margin: 0.25rem 0; padding: 0.25rem 0.5rem; white-space: normal; }

            """);

        AppendTheme(css, ":root", first, 0);

        if (themes.Skip(1).FirstOrDefault(x => x.Type != first.Type) is { } other)
        {
            var index = themes.ToList().IndexOf(other);
            css.Append($"@media (prefers-color-scheme: {other.Type}) {{\n");
            AppendTheme(css, ":root:not([data-theme])", other, index);
            css.Append("}\n");
            AppendTheme(css, $":root[data-theme='{other.Type}']", other, index);
        }

        return css.ToString();
    }

    private static void AppendTheme(StringBuilder css, string root, CodeTheme theme, int index)
    {
        var title = theme.Colors.GetValueOrDefault("editorGroupHeader.tabsBackground") ?? theme.Background;
        css.Append($"{root} .code {{ --code-background: {theme.Background}; --code-foreground: {theme.Foreground}; --code-title-background: {title}; ");
        css.Append(theme.Type == "dark"
            ? "--code-mark: #174a9080; --code-ins: #1e571580; --code-del: #862d2780; }\n"
            : "--code-mark: #9ab6ff80; --code-ins: #8ec77d80; --code-del: #ff9c8e80; }\n");
        css.Append($"{root} .code span[style^='--'] {{ color: var(--{index}, inherit); font-style: var(--{index}fs, inherit); font-weight: var(--{index}fw, inherit); text-decoration: var(--{index}td, inherit); }}\n");
    }
}
