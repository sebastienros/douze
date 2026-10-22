using System.Collections.Immutable;
using System.Globalization;
using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Images;
using Douze.Workflow;
using Fluid;
using Fluid.Ast;
using Fluid.Values;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

namespace Douze.Liquid.Tags;

/// <summary>
/// <c>{% image "images/a.png", alt: "...", widths: [640, 1280], formats: ["webp", "png"], sizes: "...", class: "...",
/// loading: "lazy" %}</c>: the variants of a source image, added to the outputs of the unit, and their markup
/// (<see cref="PictureMarkup"/>). <c>light: "a.png", dark: "b.png"</c> gives one <c>&lt;picture&gt;</c> with
/// <c>prefers-color-scheme</c> sources (two elements with <c>theme: "attribute"</c>). An SVG or a GIF is copied as it
/// is. Named arguments the tag does not know are attributes of the <c>&lt;img&gt;</c>. Defaults come from the
/// <c>images</c> settings of the <c>markdown</c> section.
/// </summary>
internal static class ImageTag
{
    private static readonly string[] Known = ["light", "dark", "alt", "widths", "formats", "quality", "output", "sizes", "class", "loading", "decoding", "theme"];

    /// <summary>
    /// The arguments: comma separated values, named (<c>name: value</c>) or not. A value is a string, a number,
    /// <c>true</c>, <c>false</c>, a list of those (<c>[640, 1280]</c>), or a variable (<c>page.image</c>).
    /// </summary>
    internal static readonly Parser<IReadOnlyList<ImageArgument>> Arguments = CreateArguments();

    internal sealed record ImageArgument(string? Name, Expression Value);

    public static void Register(FluidParser parser)
    {
        parser.RegisterParserTag("image", Arguments, RenderAsync);
    }

    private static Parser<IReadOnlyList<ImageArgument>> CreateArguments()
    {
        static bool IsNamePart(char c) => c is '-' or '_';

        var text = Terms.String().Then<Expression>(static x => new LiteralExpression(StringValue.Create(x.ToString())));
        var number = Terms.Decimal().Then<Expression>(static x => new LiteralExpression(NumberValue.Create(x)));

        var member = Terms.Identifier(extraPart: IsNamePart)
            .And(ZeroOrMany(Literals.Char('.').SkipAnd(Literals.Identifier(extraPart: IsNamePart))))
            .Then<Expression>(static x =>
            {
                var first = x.Item1.ToString();

                if (x.Item2.Count == 0)
                {
                    switch (first)
                    {
                        case "true": return new LiteralExpression(BooleanValue.True);
                        case "false": return new LiteralExpression(BooleanValue.False);
                        case "nil" or "null": return new LiteralExpression(NilValue.Instance);
                    }
                }

                return new MemberExpression([new IdentifierSegment(first), .. x.Item2.Select(static s => new IdentifierSegment(s.ToString()))]);
            });

        var scalar = text.Or(number).Or(member);

        var list = Between(Terms.Char('['), ZeroOrOne(Separated(Terms.Char(','), scalar)), Terms.Char(']'))
            .Then<Expression>(static x => new ListExpression(x.Count == 0 ? [] : x[0]));

        var value = list.Or(scalar);

        var named = Terms.Identifier(extraPart: IsNamePart).AndSkip(Terms.Char(':')).And(value)
            .Then(static x => new ImageArgument(x.Item1.ToString(), x.Item2));

        var positional = value.Then(static x => new ImageArgument(null, x));

        return Separated(Terms.Char(','), named.Or(positional));
    }

    private static async ValueTask<Completion> RenderAsync(IReadOnlyList<ImageArgument> arguments, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        string? source = null;
        var values = new Dictionary<string, FluidValue>(StringComparer.Ordinal);
        var attributes = new List<(string Name, FluidValue Value)>();

        foreach (var argument in arguments)
        {
            var value = await argument.Value.EvaluateAsync(context);

            if (argument.Name == null)
            {
                if (source != null)
                {
                    throw new DouzeException("the image tag takes one source; name the other arguments");
                }

                source = value.ToStringValue(context);
            }
            else if (Known.Contains(argument.Name, StringComparer.Ordinal))
            {
                values[argument.Name] = value;
            }
            else
            {
                attributes.Add((argument.Name, value));
            }
        }

        var light = source ?? String(values, "light", context);
        var dark = String(values, "dark", context);

        if (light == null)
        {
            throw new DouzeException("the image tag needs a source: {% image \"path\" %} or {% image light: \"path\", dark: \"path\" %}");
        }

        if (source != null && values.ContainsKey("light"))
        {
            throw new DouzeException("the image tag takes a source or 'light', not both");
        }

        var images = ImageContext.From(context);
        var request = await RequestAsync(images.Build, values, context);

        var lightSet = await images.CreateAsync(light, request);
        var darkSet = dark == null ? null : await images.CreateAsync(dark, request);

        var defaults = images.Build.Site.Markdown.Images;
        var theme = String(values, "theme", context) switch
        {
            null or "media" => ThemeMode.Media,
            "attribute" => ThemeMode.Attribute,
            var other => throw new DouzeException($"image theme must be 'media' or 'attribute', not '{other}'"),
        };

        var options = new PictureOptions(
            String(values, "alt", context),
            String(values, "sizes", context),
            String(values, "class", context),
            String(values, "loading", context) ?? defaults.Loading ?? "lazy",
            String(values, "decoding", context) ?? "async",
            Attributes(attributes, context),
            theme);

        output.Write(PictureMarkup.Write(lightSet, darkSet, options));
        return Completion.Normal;
    }

    /// <summary>
    /// The request of a tag: its <c>widths</c>, <c>formats</c>, <c>quality</c> and <c>output</c>, the defaults of the
    /// <c>markdown</c> section for those missing.
    /// </summary>
    internal static async ValueTask<ImageRequest> RequestAsync(BuildContext build, Dictionary<string, FluidValue> values, TemplateContext context)
    {
        var request = ImageRequest.FromDefaults(build.Site.Markdown.Images);

        if (values.TryGetValue("widths", out var widths) && !widths.IsNil())
        {
            request = request with { Widths = [.. (await ItemsAsync(widths, context)).Select(x => (int)x.ToNumberValue(context))] };
        }

        if (values.TryGetValue("formats", out var formats) && !formats.IsNil())
        {
            request = request with { Formats = [.. (await ItemsAsync(formats, context)).Select(x => x.ToStringValue(context))] };
        }

        if (values.TryGetValue("quality", out var quality) && !quality.IsNil())
        {
            request = request with { Quality = (int)quality.ToNumberValue(context) };
        }

        if (values.TryGetValue("output", out var folder) && !folder.IsNil())
        {
            request = request with { Output = Workflow.Steps.StepPaths.Directory(folder.ToStringValue(context)) };
        }

        return request.Validate();
    }

    private static async ValueTask<List<FluidValue>> ItemsAsync(FluidValue value, TemplateContext context)
    {
        var items = new List<FluidValue>();

        if (value.Type != FluidValues.Array)
        {
            items.Add(value);
            return items;
        }

        await foreach (var item in value.EnumerateAsync(context))
        {
            items.Add(item);
        }

        return items;
    }

    private static string? String(Dictionary<string, FluidValue> values, string name, TemplateContext context)
    {
        return values.TryGetValue(name, out var value) && !value.IsNil() ? value.ToStringValue(context) : null;
    }

    /// <summary>
    /// The other named arguments as attributes: <c>true</c> without a value, <c>false</c> and nil left out.
    /// </summary>
    private static ImmutableArray<(string, string?)> Attributes(List<(string Name, FluidValue Value)> attributes, TemplateContext context)
    {
        var result = ImmutableArray.CreateBuilder<(string, string?)>(attributes.Count);

        foreach (var (name, value) in attributes)
        {
            if (value.IsNil() || value is BooleanValue { } b && !b.ToBooleanValue(context))
            {
                continue;
            }

            result.Add((name, value is BooleanValue ? null : PictureMarkup.Encode(value.ToStringValue(context))));
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// A list literal: <c>[640, 1280]</c>.
    /// </summary>
    private sealed class ListExpression(IReadOnlyList<Expression> items) : Expression
    {
        public override async ValueTask<FluidValue> EvaluateAsync(TemplateContext context)
        {
            var values = new FluidValue[items.Count];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] = await items[i].EvaluateAsync(context);
            }

            return ArrayValue.Create(values);
        }
    }
}

/// <summary>
/// What an image tag or filter needs from the render: the build, the unit (to add outputs and record the sources it
/// reads) and the folder relative sources start from.
/// </summary>
internal sealed class ImageContext
{
    private ImageContext(StepContext step, FPath directory)
    {
        Step = step;
        Directory = directory;
    }

    public StepContext Step { get; }

    public BuildContext Build => Step.Build;

    /// <summary>
    /// The source folder of the page being rendered (relative sources start from it), or the content folder.
    /// </summary>
    public FPath Directory { get; }

    /// <exception cref="DouzeException">The render is not a step's (no <see cref="AmbientKeys.StepContext"/>).</exception>
    public static ImageContext From(TemplateContext context)
    {
        if (!context.AmbientValues.TryGetValue(AmbientKeys.StepContext, out var value) || value is not StepContext step)
        {
            throw new DouzeException("image tags and filters render in a build step only");
        }

        var directory = context.AmbientValues.TryGetValue(AmbientKeys.File, out var f) && f is VirtualFile file ? file.Metadata.Directory : step.Build.Site.ContentFolder;
        return new ImageContext(step, directory);
    }

    /// <summary>
    /// The source path of <paramref name="src"/>.
    /// </summary>
    /// <exception cref="DouzeException">The path is an external url or leaves the source folder.</exception>
    public FPath Resolve(string src)
    {
        return ImageSources.Resolve(src, Directory, Build.Site.ContentFolder)
            ?? throw new DouzeException($"image '{src}' is not a path of the source folder");
    }

    /// <summary>
    /// Reads a source image (recorded as a dependency of the unit), produces its variants, adds them to the outputs of
    /// the unit and returns their set.
    /// </summary>
    public async ValueTask<ImageSet> CreateAsync(string src, ImageRequest request)
    {
        var path = Resolve(src);
        var bytes = await ImageSources.ReadAsync(Build.Source, path, src, Step.Dependencies);
        var (set, files) = ImageBuilder.Build(() => Build.Images, ImageSources.Url(path, Build.Site.ContentFolder), bytes, request, original: false);

        foreach (var file in files)
        {
            Step.AddOutput(file.ToFile());
        }

        return set;
    }
}
