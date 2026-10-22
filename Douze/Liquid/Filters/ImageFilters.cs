using System.Text.Encodings.Web;
using Douze.Images;
using Douze.Liquid.Tags;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Steps;
using Fluid;
using Fluid.Values;

namespace Douze.Liquid.Filters;

/// <summary>
/// Filters of the image features: <c>image_url</c>, <c>image_size</c>, <c>social_image_url</c>.
/// </summary>
internal static class ImageFilters
{
    public static void Register(TemplateOptionsBuilder builder)
    {
        builder.AddFilter("image_url", ImageUrl);
        builder.AddFilter("image_size", ImageSize);
        builder.AddFilter("social_image_url", SocialImageUrl);
    }

    /// <summary>
    /// <c>{{ "images/a.png" | image_url: width: 400, format: "webp" }}</c>: one variant of a source image (an SVG or a
    /// GIF is copied), added to the outputs of the unit; returns its url, for <c>og:image</c> meta or CSS. Without
    /// <c>width</c> or <c>format</c>, the first of the <c>markdown.images</c> defaults, or the source's own.
    /// </summary>
    public static async ValueTask<FluidValue> ImageUrl(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var images = ImageContext.From(context);
        var values = new Dictionary<string, FluidValue>(StringComparer.Ordinal);

        if (arguments["width"] is { Type: not FluidValues.Nil } width)
        {
            values["widths"] = width;
        }

        if (arguments["format"] is { Type: not FluidValues.Nil } format)
        {
            values["formats"] = format;
        }

        foreach (var name in new[] { "quality", "output" })
        {
            if (arguments[name] is { Type: not FluidValues.Nil } value)
            {
                values[name] = value;
            }
        }

        var request = await ImageTag.RequestAsync(images.Build, values, context);

        // One variant: the first width and format requested.
        request = request with { Widths = request.Widths.IsEmpty ? [] : [request.Widths[0]], Formats = request.Formats.IsEmpty ? [] : [request.Formats[0]] };

        var set = await images.CreateAsync(input.ToStringValue(context), request);
        return StringValue.Create(set.Variants[0].Url);
    }

    /// <summary>
    /// <c>{{ "images/a.png" | image_size }}</c>: <c>{ width, height }</c> of a source image, as displayed. The source is a
    /// dependency of the unit; nothing is written.
    /// </summary>
    public static async ValueTask<FluidValue> ImageSize(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        var images = ImageContext.From(context);
        var src = input.ToStringValue(context);
        var path = await images.ResolveAsync(src);
        var bytes = await ImageSources.ReadAsync(images.Build.Source, path, src, images.Step.Dependencies);

        var format = ImageFormats.Sniff(bytes.Span) ?? throw new DouzeException($"'{src}' is not an image format douze reads (png, jpeg, webp, avif, gif, svg)");
        var (width, height) = format.CopiedAsIs ? ImageFormats.ReadSize(format, bytes.Span) : Size(images.Build.Images.Probe(bytes));

        return new NodeObjectValue(new NodeObject { ["width"] = NodeValue.Create(width), ["height"] = NodeValue.Create(height) });

        static (int, int) Size(ImageInfo info) => (info.Width, info.Height);
    }

    /// <summary>
    /// <c>{{ page | social_image_url: "og{{ page.url }}index.png" }}</c>: the url of the social image of the page being
    /// rendered, from the <c>output</c> template of its <c>social-image</c> step, rendered with the same model (the page's
    /// metadata); the input is not read.
    /// </summary>
    public static async ValueTask<FluidValue> SocialImageUrl(FluidValue input, FilterArguments arguments, TemplateContext context)
    {
        if (arguments.Count == 0)
        {
            throw new DouzeException("social_image_url needs the output template of the social-image step");
        }

        var images = ImageContext.From(context);
        var text = arguments.At(0).ToStringValue(context);
        var template = images.Build.Templates.GetOrParse(Hash128.OfText(text), text, "social_image_url");
        var file = context.AmbientValues.TryGetValue(AmbientKeys.File, out var f) ? f as VirtualFile : null;

        var templateContext = images.Step.CreateTemplateContext(context.Model, file, context.CancellationToken);
        var path = SocialImageStep.OutputPath(await template.Template.RenderAsync(templateContext, NullEncoder.Default));

        return StringValue.Create(path.FullName);
    }
}
