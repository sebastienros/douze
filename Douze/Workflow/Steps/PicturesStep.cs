using System.Net;
using System.Text;
using Douze.Images;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: pictures</c>. Rewrites the <c>&lt;img src&gt;</c> of HTML files whose source is in <c>images</c> (from
/// <c>image-map</c>) into the markup of the <c>image</c> tag: a <c>&lt;picture&gt;</c> of the variants with
/// <c>width</c> and <c>height</c>. A relative source starts from the page's source folder, one starting with <c>/</c>
/// from the content folder. The other attributes are kept (<c>alt</c>, <c>title</c>, <c>class</c>, <c>loading</c>...);
/// images that are not in <c>images</c>, and those already in a <c>&lt;picture&gt;</c>, are left as they are. A page
/// depends on the entries of <c>images</c> it used only.
/// </summary>
internal sealed class PicturesStep : Step, IMapStep
{
    private static readonly GlobSet HtmlFiles = GlobSet.Parse(["**/*.html"]);

    // Replaced by the markup, or written by it.
    private static readonly string[] Replaced = ["src", "srcset", "width", "height", "alt", "class", "loading", "decoding", "sizes"];

    public PicturesStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => HtmlFiles;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var html = await file.Content.ReadTextAsync(ct);

        if (!html.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            return FileList.Of(file);
        }

        var build = context.Build;
        var lookedUp = false;
        Nodes.NodeObject? images = null;
        AccessRecorder? recorder = null;

        var builder = new StringBuilder(html.Length);
        var position = 0;
        var changed = false;

        foreach (var tag in ImgScanner.Scan(html))
        {
            if (tag.Attribute("src") is not { } src
                || ImageSources.Resolve(WebUtility.HtmlDecode(src), file.Metadata.Directory, build.Site.ContentFolder) is not { } path
                || InPicture(html, tag.Start))
            {
                continue;
            }

            if (!lookedUp)
            {
                lookedUp = true;

                if (context.Scopes.TryResolveTracked(file.Metadata, ImageMapStep.Key, null, context.Dependencies, out var node, out recorder))
                {
                    images = node as Nodes.NodeObject;
                }
            }

            if (images == null)
            {
                break;
            }

            var url = ImageSources.Url(path, build.Site.ContentFolder);
            recorder?.Member(url).ReadWhole();

            if (ImageSet.FromNode(images[url]) is not { } set)
            {
                continue;
            }

            var options = new PictureOptions(
                tag.Attribute("alt") is { } alt ? WebUtility.HtmlDecode(alt) : null,
                tag.Attribute("sizes") is { } sizes ? WebUtility.HtmlDecode(sizes) : null,
                tag.Attribute("class") is { } cssClass ? WebUtility.HtmlDecode(cssClass) : null,
                tag.Attribute("loading") is { } loading ? WebUtility.HtmlDecode(loading) : build.Site.Markdown.Images.Loading ?? "lazy",
                tag.Attribute("decoding") is { } decoding ? WebUtility.HtmlDecode(decoding) : "async",
                [.. tag.Attributes.Where(x => !Replaced.Contains(x.Name, StringComparer.OrdinalIgnoreCase))]);

            builder.Append(html, position, tag.Start - position);
            builder.Append(PictureMarkup.Write(set, null, options));
            position = tag.End;
            changed = true;
        }

        if (!changed)
        {
            return FileList.Of(file);
        }

        builder.Append(html, position, html.Length - position);
        return FileList.Of(file.With(Name, content: FileContent.FromText(builder.ToString())));
    }

    private static bool InPicture(string html, int position)
    {
        var open = html.LastIndexOf("<picture", position, StringComparison.OrdinalIgnoreCase);
        return open >= 0 && html.LastIndexOf("</picture>", position, StringComparison.OrdinalIgnoreCase) < open;
    }
}

/// <summary>
/// The <c>&lt;img&gt;</c> tags of HTML, with their attributes as written (values still encoded), found with a forward scan
/// that skips comments, <c>&lt;pre&gt;</c>, <c>&lt;code&gt;</c>, <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c>.
/// </summary>
internal static class ImgScanner
{
    internal sealed record ImgTag(int Start, int End, IReadOnlyList<(string Name, string? EncodedValue)> Attributes)
    {
        public string? Attribute(string name) => Attributes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).EncodedValue;
    }

    private static readonly string[] Skipped = ["pre", "code", "script", "style", "textarea"];

    public static List<ImgTag> Scan(string html)
    {
        var tags = new List<ImgTag>();
        var index = 0;

        while ((index = html.IndexOf('<', index)) >= 0)
        {
            if (string.CompareOrdinal(html, index, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = close < 0 ? html.Length : close + 3;
                continue;
            }

            var skipped = Skipped.FirstOrDefault(x => IsTag(html, index, x));

            if (skipped != null)
            {
                var close = html.IndexOf($"</{skipped}", index + 1, StringComparison.OrdinalIgnoreCase);
                index = close < 0 ? html.Length : close + 2;
                continue;
            }

            if (IsTag(html, index, "img") && ReadTag(html, index) is { } tag)
            {
                tags.Add(tag);
                index = tag.End;
                continue;
            }

            index++;
        }

        return tags;
    }

    private static bool IsTag(string html, int index, string name)
    {
        var end = index + 1 + name.Length;
        return end < html.Length
            && string.Compare(html, index + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (char.IsWhiteSpace(html[end]) || html[end] is '>' or '/');
    }

    private static ImgTag? ReadTag(string html, int start)
    {
        var attributes = new List<(string, string?)>();
        var i = start + 4;

        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= html.Length)
            {
                return null;
            }

            if (html[i] == '>')
            {
                return new ImgTag(start, i + 1, attributes);
            }

            if (html[i] == '/' && i + 1 < html.Length && html[i + 1] == '>')
            {
                return new ImgTag(start, i + 2, attributes);
            }

            var nameStart = i;

            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
            {
                i++;
            }

            if (i == nameStart)
            {
                // A stray '/'.
                i++;
                continue;
            }

            var name = html[nameStart..i];

            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= html.Length || html[i] != '=')
            {
                attributes.Add((name, null));
                continue;
            }

            i++;

            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i < html.Length && html[i] is '"' or '\'')
            {
                var quote = html[i];
                var close = html.IndexOf(quote, i + 1);

                if (close < 0)
                {
                    return null;
                }

                attributes.Add((name, html[(i + 1)..close]));
                i = close + 1;
            }
            else
            {
                var valueStart = i;

                while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                {
                    i++;
                }

                attributes.Add((name, html[valueStart..i]));
            }
        }

        return null;
    }
}
