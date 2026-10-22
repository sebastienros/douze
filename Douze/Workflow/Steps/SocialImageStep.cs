using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Douze.FileSystems;
using Douze.Images;
using Douze.Liquid;
using Douze.Nodes;
using Fluid;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: social-image</c>. Renders a social (Open Graph) image per page: <c>with.template</c> (a Liquid template of the
/// includes folder producing SVG) rendered with the page's model and rasterized at <c>with.width</c> by
/// <c>with.height</c> (1200 by 630 by default), then each block of <c>with.text</c> rendered with Pango and composited
/// over it, encoded as PNG at <c>with.output</c> (a Liquid template over the page's model). Pages for which
/// <c>with.skip</c> (a Liquid expression) is true get none. The step is inline: it passes every page through as the same
/// instance and adds the image to the outputs of the unit. Images the SVG references by a relative path (to the
/// template's folder, or to the content folder with <c>/</c>) are inlined as data URIs, since the SVG is rasterized
/// from memory.
/// </summary>
internal sealed partial class SocialImageStep : Step, IMapStep
{
    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html", "**/*.md"]);

    private readonly FPath _template;
    private readonly int _width;
    private readonly int _height;
    private readonly string _output;
    private readonly string? _skip;
    private readonly ImmutableArray<TextOptions> _text;

    // Font files copied to the temporary folder, where Pango reads them, by content hash.
    private readonly ConcurrentDictionary<Hash128, Lazy<Task<string>>> _fonts = new();

    // The files the cards read (font files, images inlined in the SVG), read once per build: every card reads the same.
    private readonly ConcurrentDictionary<FPath, Lazy<Task<SourceFile>>> _sources = new();

    private sealed record SourceFile(byte[] Bytes, Hash128 Hash)
    {
        private string? _dataUri;

        public string DataUri(FPath path)
        {
            var type = (ImageFormats.Sniff(Bytes) ?? ImageFormats.FromExtension(path.GetExtensionWithDot()))?.ContentType ?? "application/octet-stream";
            return _dataUri ??= $"data:{type};base64,{Convert.ToBase64String(Bytes)}";
        }
    }

    private sealed record TextOptions(string Value, string Font, string? FontFile, int X, int Y, int? Width, int? Height, string Color, string Align);

    public SocialImageStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "template", "width", "height", "output", "skip", "text");

        _template = new FPath(options.GetRequiredString("template")).ToRelative();
        _width = options.GetInt("width", 1200);
        _height = options.GetInt("height", 630);
        _output = options.GetRequiredString("output");
        _skip = options.GetString("skip");
        _text = ReadText(definition);

        if (_width is < 1 or > 10_000 || _height is < 1 or > 10_000)
        {
            throw new WorkflowValidationException($"options 'width' and 'height' of step '{Name}' must be from 1 to 10000", definition.Location);
        }
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => Pages;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _fonts.Clear();
        _sources.Clear();
        return default;
    }

    private static ImmutableArray<TextOptions> ReadText(StepDefinition definition)
    {
        if (definition.With["text"] is not { } node)
        {
            return [];
        }

        if (node is not NodeArray blocks || blocks.Any(x => x is not NodeObject))
        {
            throw Error("must be a list of text blocks");
        }

        var result = ImmutableArray.CreateBuilder<TextOptions>(blocks.Count);
        string[] keys = ["value", "font", "fontfile", "x", "y", "width", "height", "color", "align"];

        foreach (NodeObject block in blocks.Cast<NodeObject>())
        {
            foreach (var key in block.Select(x => x.Key))
            {
                if (!keys.Contains(key, StringComparer.Ordinal))
                {
                    throw Error($"has an unknown key '{key}'{WorkflowValidationException.Suggest(key, keys)}");
                }
            }

            var align = Text(block, "align") ?? "left";

            if (align is not ("left" or "center" or "centre" or "right"))
            {
                throw Error($"has an invalid 'align' '{align}'; expected 'left', 'center' or 'right'");
            }

            var color = Text(block, "color") ?? "#000000";

            try
            {
                ImageProcessor.ParseColor(color);
            }
            catch (DouzeException e)
            {
                throw Error($"has an {e.Message}");
            }

            result.Add(new TextOptions(
                Text(block, "value") ?? throw Error("needs a 'value'"),
                Text(block, "font") ?? "sans 32",
                Text(block, "fontfile"),
                Int(block, "x") ?? 0,
                Int(block, "y") ?? 0,
                Int(block, "width"),
                Int(block, "height"),
                color,
                align));
        }

        return result.ToImmutable();

        WorkflowValidationException Error(string message) => new($"option 'text' of step '{definition.Name}' {message}", definition.Location);

        string? Text(NodeObject block, string key) => block[key] switch
        {
            null => null,
            NodeValue<string> s => s.Value,
            NodeValue value and IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw Error($"'{key}' must be a string"),
        };

        int? Int(NodeObject block, string key) => block[key] switch
        {
            null => null,
            NodeValue<int> i => i.Value,
            NodeValue<long> l => (int)l.Value,
            NodeValue<decimal> d when d.Value == decimal.Truncate(d.Value) => (int)d.Value,
            _ => throw Error($"'{key}' must be an integer"),
        };
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var model = new MetadataValue(file.Metadata);

        if (_skip != null && await RenderAsync($"{{% if {_skip} %}}true{{% endif %}}", $"skip of step '{Name}'", model, file, context, ct) == "true")
        {
            return FileList.Of(file);
        }

        var build = context.Build;
        var loaded = await build.Templates.LoadAsync(_template, context.Dependencies, ct)
            ?? throw new DouzeException($"template '{_template}' not found in '{build.Site.IncludesFolder}'");

        var svgContext = context.CreateTemplateContext(model, file, ct);
        var svg = await loaded.Template.Template.RenderAsync(svgContext, HtmlEncoders.Html);
        svg = await InlineImagesAsync(svg, loaded.Path.GetDirectory(), context);

        var blocks = new List<TextBlock>(_text.Length);

        foreach (var text in _text)
        {
            var value = await RenderAsync(text.Value, $"text of step '{Name}'", model, file, context, ct);
            var fontFile = text.FontFile == null ? null : await FontFileAsync(text.FontFile, context);

            blocks.Add(new TextBlock(
                value.Trim(),
                text.Font,
                fontFile,
                text.X,
                text.Y,
                text.Width ?? _width - text.X,
                text.Height ?? _height - text.Y,
                text.Color,
                text.Align));
        }

        var path = OutputPath(await RenderAsync(_output, $"output of step '{Name}'", model, file, context, ct));
        var images = build.Images;
        var png = images.Compose(images.Rasterize(svg, _width, _height), blocks);

        context.AddOutput(new VirtualFile(path, FileContent.FromBytes(png, isText: false), Metadata.Empty, ImageFormats.Png.ContentType, Provenance.Source));

        return FileList.Of(file);
    }

    /// <summary>
    /// The path of a social image from its rendered <c>output</c> template: <c>og/docs/x/index.png</c> is
    /// <c>/og/docs/x/index.png</c>; the <c>social_image_url</c> filter returns the same.
    /// </summary>
    /// <exception cref="DouzeException">The result is not a file path.</exception>
    public static FPath OutputPath(string rendered)
    {
        var text = rendered.Trim();

        if (text.Length == 0 || text.EndsWith('/') || text.Contains("..", StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal))
        {
            throw new DouzeException($"social image output '{text}' is not a file path");
        }

        return FPath.Combine(FPath.Root, new FPath(text.TrimStart('/')));
    }

    private static async ValueTask<string> RenderAsync(string text, string name, MetadataValue model, VirtualFile file, StepContext context, CancellationToken ct)
    {
        var template = context.Build.Templates.GetOrParse(Hash128.OfText(text), text, name);
        var templateContext = context.CreateTemplateContext(model, file, ct);
        return await template.Template.RenderAsync(templateContext, NullEncoder.Default);
    }

    /// <summary>
    /// Replaces the relative image references of the SVG (<c>href</c>, <c>xlink:href</c>) by data URIs, recording the
    /// files as dependencies of the unit.
    /// </summary>
    private async ValueTask<string> InlineImagesAsync(string svg, FPath directory, StepContext context)
    {
        var matches = Reference().Matches(svg);

        if (matches.Count == 0)
        {
            return svg;
        }

        var builder = new StringBuilder(svg.Length);
        var position = 0;

        foreach (Match match in matches)
        {
            var value = match.Groups["value"];

            if (value.Value.StartsWith('#') || ImageSources.Resolve(System.Net.WebUtility.HtmlDecode(value.Value), directory, context.Build.Site.ContentFolder) is not { } path)
            {
                continue;
            }

            var file = await ReadAsync(path, value.Value, context);

            builder.Append(svg, position, value.Index - position);
            builder.Append(file.DataUri(path));
            position = value.Index + value.Length;
        }

        builder.Append(svg, position, svg.Length - position);
        return builder.ToString();
    }

    [GeneratedRegex("""(?<![\w-])(?:xlink:)?href\s*=\s*(?<quote>["'])(?<value>[^"']*)\k<quote>""")]
    private static partial Regex Reference();

    /// <summary>
    /// A font file of the source folder, copied once per build to the temporary folder for Pango, which reads fonts
    /// from the disk; recorded as a dependency of the unit.
    /// </summary>
    private async ValueTask<string> FontFileAsync(string fontFile, StepContext context)
    {
        var path = FPath.Combine(FPath.Root, new FPath(fontFile.TrimStart('/')));
        var source = context.Build.Source;

        if (!(await source.GetFileEntryAsync(path)).Exists)
        {
            throw new DouzeException($"font file '{fontFile}' not found");
        }

        var (bytes, hash) = await ReadAsync(path, fontFile, context);

        return await _fonts.GetOrAdd(hash, static (hash, state) => new Lazy<Task<string>>(async () =>
        {
            var folder = Path.Combine(state.Context.Build.TempRoot, "fonts");
            Directory.CreateDirectory(folder);

            var file = Path.Combine(folder, hash + state.Path.GetExtensionWithDot().ToLowerInvariant());
            await File.WriteAllBytesAsync(file, state.Bytes);
            return file;
        }, LazyThreadSafetyMode.ExecutionAndPublication), (Context: context, Path: path, Bytes: bytes)).Value;
    }

    /// <summary>
    /// A file of the source folder, read once per build, recorded as a dependency of the unit.
    /// </summary>
    /// <exception cref="DouzeException">The file does not exist.</exception>
    private async ValueTask<SourceFile> ReadAsync(FPath path, string src, StepContext context)
    {
        var file = await _sources.GetOrAdd(path, static (path, state) => new Lazy<Task<SourceFile>>(async () =>
        {
            var bytes = (await ImageSources.ReadAsync(state.Source, path, state.Src, null)).ToArray();
            return new SourceFile(bytes, Hash128.Of(bytes));
        }, LazyThreadSafetyMode.ExecutionAndPublication), (Source: context.Build.Source, Src: src)).Value;

        context.Dependencies.RecordTemplate(path, file.Hash);
        return file;
    }
}
