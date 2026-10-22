using System.Collections.Immutable;
using Douze.Images;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: images</c>. Emits the variants of each image file (<c>with.widths</c>, <c>with.formats</c>,
/// <c>with.quality</c>, under <c>with.output</c>; the <c>images</c> settings of the <c>markdown</c> section for those
/// missing) and its original with a fingerprinted name, instead of the file itself; an SVG or a GIF is only copied.
/// Every output carries <c>image</c>: <c>{ source, url, width, height, format, variants }</c> (<see cref="ImageSet"/>),
/// which <c>image-map</c> collects for <c>pictures</c> and templates.
/// </summary>
internal sealed class ImagesStep : Step, IMapStep
{
    private readonly ImmutableArray<int>? _widths;
    private readonly ImmutableArray<string>? _formats;
    private readonly int? _quality;
    private readonly string? _output;
    private ImageRequest? _request;

    public ImagesStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "widths", "formats", "quality", "output");

        _widths = ReadList(definition, "widths", static x => x switch
        {
            NodeValue<int> i => (true, i.Value),
            NodeValue<long> l => (true, (int)l.Value),
            NodeValue<decimal> d when d.Value == decimal.Truncate(d.Value) => (true, (int)d.Value),
            _ => (false, 0),
        }, "a list of widths");

        _formats = ReadList(definition, "formats", static x => x is NodeValue<string> s && ImageFormats.Encodable(s.Value) != null ? (true, s.Value) : (false, ""), $"a list of image formats ({ImageFormats.EncodableNames})");
        _quality = options.Has("quality") ? options.GetInt("quality") : null;
        _output = options.GetString("output");

        if (_widths?.Any(x => x <= 0) == true || _quality is < 1 or > 100)
        {
            throw new WorkflowValidationException($"step '{Name}' needs positive widths and a quality from 1 to 100", definition.Location);
        }
    }

    public override StepShape NativeShape => StepShape.Map;

    public override IEnumerable<Requirement> Requirements(BuildContext context) => [ImageProcessor.Requirement];

    public override GlobSet DefaultMatch => ImageFormats.Sources;

    /// <summary>
    /// The request, which includes the defaults of the <c>markdown</c> section: they are not in the step's definition.
    /// </summary>
    public override Hash128 CacheVersion => _request == null
        ? Hash128.Empty
        : Hash128.OfText($"{string.Join(',', _request.Widths)}|{string.Join(',', _request.Formats)}|{_request.Quality}|{_request.Output}");

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        var defaults = ImageRequest.FromDefaults(context.Site.Markdown.Images);

        _request = new ImageRequest(
            _widths ?? defaults.Widths,
            _formats ?? defaults.Formats,
            _quality ?? defaults.Quality,
            _output is { } output ? StepPaths.Directory(output) : defaults.Output).Validate();

        return default;
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var build = context.Build;
        var bytes = await file.Content.ReadBytesAsync(ct);
        var (set, outputs) = await ImageBuilder.BuildAsync(() => build.Images, ImageSources.Url(file.Path, build.Site.ContentFolder), bytes, _request!, original: true);

        // One node shared by every output: nodes are never changed once published.
        var image = set.ToNode();
        var metadata = file.Metadata.With(own => own["image"] = image);
        var files = new VirtualFile[outputs.Count];

        for (var i = 0; i < files.Length; i++)
        {
            var output = outputs[i];
            var content = output.Bytes.Equals(bytes) ? file.Content : FileContent.FromBytes(output.Bytes, isText: false);
            files[i] = file.With(Name, path: output.Path, content: content, metadata: metadata, contentType: output.ContentType);
        }

        return FileList.Of(files);
    }

    private static ImmutableArray<T>? ReadList<T>(StepDefinition definition, string key, Func<Node?, (bool Valid, T Value)> read, string expected)
    {
        if (definition.With[key] is not { } node)
        {
            return null;
        }

        Node?[] items = node is NodeArray array ? [.. array] : [node];
        var result = ImmutableArray.CreateBuilder<T>(items.Length);

        foreach (var item in items)
        {
            var (valid, value) = read(item);
            result.Add(valid ? value : throw new WorkflowValidationException($"option '{key}' of step '{definition.Name}' must be {expected}", definition.Location));
        }

        return result.ToImmutable();
    }
}
