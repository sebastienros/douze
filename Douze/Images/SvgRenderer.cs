using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Path = SixLabors.ImageSharp.Drawing.Path;

namespace Douze.Images;

/// <summary>
/// Rasterizes the SVG of social images with ImageSharp.Drawing. It renders the static vector subset that cards use:
/// <c>svg</c> (nested too), <c>g</c>, <c>a</c>, <c>switch</c>, <c>use</c> (of elements and symbols), <c>rect</c>,
/// <c>circle</c>, <c>ellipse</c>, <c>line</c>, <c>polyline</c>, <c>polygon</c>, <c>path</c> and <c>image</c> (PNG, JPEG,
/// WebP, GIF or SVG data URIs); <c>transform</c>, <c>viewBox</c> and <c>preserveAspectRatio</c>; fills (with
/// <c>fill-rule</c>) and strokes (width, caps, joins, miter limit, dashes); solid colors (hex, <c>rgb()</c>,
/// <c>hsl()</c>, names, <c>currentColor</c>); linear and radial gradients (units, transforms, spread methods, focal
/// points, <c>href</c> inheritance); <c>opacity</c>, <c>fill-opacity</c> and <c>stroke-opacity</c>; presentation
/// attributes, <c>style</c> attributes and <c>&lt;style&gt;</c> sheets of simple selectors (<c>rect</c>,
/// <c>.class</c>, <c>#id</c>, <c>rect.class</c>). Text, clipping paths, masks, filters, patterns and markers are reported
/// as errors, with the line of the element: the step's <c>text</c> blocks lay out text. Animation elements are ignored.
/// </summary>
internal static partial class SvgRenderer
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

    // Elements that are never rendered where they stand (definitions, metadata), and animation elements, ignored.
    private static readonly HashSet<string> NotRendered = new(StringComparer.Ordinal)
    {
        "defs", "title", "desc", "metadata", "style", "linearGradient", "radialGradient", "stop", "symbol", "clipPath",
        "mask", "filter", "pattern", "marker", "script", "animate", "animateMotion", "animateTransform", "set", "mpath",
        "view", "cursor",
    };

    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "color", "opacity", "display", "visibility",
        "stop-color", "stop-opacity", "clip-path", "mask", "filter", "marker", "marker-start", "marker-mid", "marker-end",
    };

    // Properties whose value other than none would change the image, and that the renderer does not support.
    private static readonly string[] Unsupported = ["clip-path", "mask", "filter", "marker", "marker-start", "marker-mid", "marker-end"];

    /// <summary>
    /// Renders an SVG at <paramref name="width"/> by <paramref name="height"/>: its view box fitted in its intrinsic size
    /// (<c>preserveAspectRatio</c>), which is stretched to the size.
    /// </summary>
    /// <exception cref="DouzeException">The SVG is invalid or uses what the renderer does not support.</exception>
    public static Image<Rgba32> Render(string svg, int width, int height) => Render(svg, width, height, depth: 0);

    private static Image<Rgba32> Render(string svg, int width, int height, int depth)
    {
        XDocument document;

        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new DouzeException($"invalid SVG: {e.Message.TrimEnd('.')}");
        }

        var root = document.Root;

        if (root == null || root.Name.LocalName != "svg")
        {
            throw new DouzeException("invalid SVG: the root element is not <svg>");
        }

        using var renderer = new Renderer(document, depth);
        var viewBox = renderer.ViewBox(root);
        var intrinsic = IntrinsicSize(root, viewBox);
        var viewport = viewBox is { } box ? new SizeF(box.Width, box.Height) : intrinsic;
        var transform = (viewBox is { } vb ? ViewBoxTransform(vb, intrinsic, root.Attribute("preserveAspectRatio")?.Value) : Matrix3x2.Identity)
            * Matrix3x2.CreateScale(width / intrinsic.Width, height / intrinsic.Height);

        var image = new Image<Rgba32>(ImageProcessor.Configuration, width, height);

        try
        {
            image.Mutate(x => x.Paint(canvas => renderer.RenderElement(root, canvas, new Context(transform, viewport, Style.Initial), isRoot: true)));
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    private static SizeF IntrinsicSize(XElement root, RectangleF? viewBox)
    {
        var width = AbsoluteLength(root.Attribute("width")?.Value);
        var height = AbsoluteLength(root.Attribute("height")?.Value);

        if (width is { } w && height is { } h)
        {
            return new SizeF(w, h);
        }

        if (viewBox is not { Width: > 0, Height: > 0 } box)
        {
            throw new DouzeException("invalid SVG: the <svg> element has neither a width and a height nor a viewBox");
        }

        return (width, height) switch
        {
            ({ } only, null) => new SizeF(only, only * box.Height / box.Width),
            (null, { } only) => new SizeF(only * box.Width / box.Height, only),
            _ => new SizeF(box.Width, box.Height),
        };
    }

    private static float? AbsoluteLength(string? value)
    {
        return value == null || value.TrimEnd().EndsWith('%') || !TryLength(value, out var length, out _) || length <= 0 ? null : length;
    }

    /// <summary>
    /// The transform from a view box to a viewport of <paramref name="size"/>, by <c>preserveAspectRatio</c>
    /// (<c>xMidYMid meet</c> by default).
    /// </summary>
    private static Matrix3x2 ViewBoxTransform(RectangleF box, SizeF size, string? preserveAspectRatio)
    {
        var sx = size.Width / box.Width;
        var sy = size.Height / box.Height;
        var words = (preserveAspectRatio ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var align = words.FirstOrDefault(x => x != "defer") ?? "xMidYMid";

        if (align == "none")
        {
            return Matrix3x2.CreateTranslation(-box.X, -box.Y) * Matrix3x2.CreateScale(sx, sy);
        }

        var scale = words.Contains("slice") ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var ax = align.Length >= 4 ? Fraction(align.AsSpan(1, 3)) : 0.5f;
        var ay = align.Length >= 8 ? Fraction(align.AsSpan(5, 3)) : 0.5f;

        return Matrix3x2.CreateTranslation(-box.X, -box.Y)
            * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation((size.Width - box.Width * scale) * ax, (size.Height - box.Height * scale) * ay);

        static float Fraction(ReadOnlySpan<char> position) => position switch
        {
            "Min" => 0f,
            "Max" => 1f,
            _ => 0.5f,
        };
    }

    /// <summary>
    /// The user space of an element: its transform to the canvas, the size its percentages refer to, and the inherited
    /// style.
    /// </summary>
    private sealed record Context(Matrix3x2 Transform, SizeF Viewport, Style Style);

    /// <summary>
    /// The computed style of an element.
    /// </summary>
    private sealed record Style(
        Paint Fill,
        float FillOpacity,
        IntersectionRule FillRule,
        Paint Stroke,
        string StrokeWidth,
        float StrokeOpacity,
        LineCap LineCap,
        LineJoin LineJoin,
        float MiterLimit,
        string? DashArray,
        string DashOffset,
        Color Color,
        bool Visible)
    {
        public static readonly Style Initial = new(
            new SolidPaint(Color.Black),
            1,
            IntersectionRule.NonZero,
            NoPaint.Instance,
            "1",
            1,
            LineCap.Butt,
            LineJoin.Miter,
            4,
            null,
            "0",
            Color.Black,
            true);
    }

    private abstract record Paint;

    private sealed record NoPaint : Paint
    {
        public static readonly NoPaint Instance = new();
    }

    private sealed record SolidPaint(Color Color) : Paint;

    private sealed record CurrentColorPaint : Paint
    {
        public static readonly CurrentColorPaint Instance = new();
    }

    private sealed record UrlPaint(string Id, Paint Fallback) : Paint;

    private sealed record CssRule(string? Tag, string? Id, string[] Classes, int Order, Dictionary<string, string> Declarations)
    {
        public (int, int, int, int) Specificity => (Id == null ? 0 : 1, Classes.Length, Tag == null ? 0 : 1, Order);

        public bool Matches(XElement element)
        {
            return (Tag == null || Tag == element.Name.LocalName)
                && (Id == null || Id == element.Attribute("id")?.Value)
                && (Classes.Length == 0 || element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { } classes && Classes.All(classes.Contains));
        }
    }

    private sealed class Renderer : IDisposable
    {
        // The images drawn: the canvas draws when it is flushed, after the elements are rendered.
        private readonly List<IDisposable> _images = [];

        private readonly Dictionary<string, XElement> _ids = new(StringComparer.Ordinal);
        private readonly List<CssRule> _rules = [];
        private readonly int _depth;
        private int _uses;

        public Renderer(XDocument document, int depth)
        {
            _depth = depth;

            foreach (var element in document.Descendants())
            {
                if (element.Attribute("id")?.Value is { Length: > 0 } id)
                {
                    _ids.TryAdd(id, element);
                }

                if (IsSvg(element) && element.Name.LocalName == "style")
                {
                    ParseStyleSheet(element);
                }
            }
        }

        public void Dispose()
        {
            foreach (var image in _images)
            {
                image.Dispose();
            }
        }

        public RectangleF? ViewBox(XElement element)
        {
            if (element.Attribute("viewBox")?.Value is not { } value)
            {
                return null;
            }

            var numbers = Numbers(value);

            if (numbers.Count != 4)
            {
                throw Error(element, $"invalid viewBox '{value}'");
            }

            return numbers[2] > 0 && numbers[3] > 0 ? new RectangleF(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
        }

        public void RenderElement(XElement element, DrawingCanvas canvas, Context parent, bool isRoot = false)
        {
            if (!IsSvg(element))
            {
                // Foreign elements (editor metadata) render nothing.
                return;
            }

            var name = element.Name.LocalName;

            if (NotRendered.Contains(name))
            {
                return;
            }

            var declared = Declared(element);

            if (declared.TryGetValue("display", out var display) && display == "none")
            {
                return;
            }

            foreach (var property in Unsupported)
            {
                if (declared.TryGetValue(property, out var value) && value != "none")
                {
                    throw Error(element, $"the property '{property}' is not supported");
                }
            }

            var style = Cascade(element, parent.Style, declared);
            var transform = element.Attribute("transform")?.Value is { } text ? ParseTransform(element, text) * parent.Transform : parent.Transform;
            var context = parent with { Transform = transform, Style = style };
            var opacity = declared.TryGetValue("opacity", out var opacityValue) ? Opacity(opacityValue) : 1;

            if (opacity <= 0)
            {
                return;
            }

            if (opacity < 1)
            {
                canvas.SaveLayer(new GraphicsOptions { BlendPercentage = opacity });
            }

            try
            {
                switch (name)
                {
                    case "svg":
                        RenderViewport(element, canvas, context, isRoot);
                        break;
                    case "g" or "a":
                        RenderChildren(element, canvas, context);
                        break;
                    case "switch":
                        if (element.Elements().FirstOrDefault(IsSvg) is { } first)
                        {
                            RenderElement(first, canvas, context);
                        }

                        break;
                    case "use":
                        RenderUse(element, canvas, context);
                        break;
                    case "image":
                        RenderImage(element, canvas, context);
                        break;
                    case "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path":
                        if (Shape(element, context) is { } path)
                        {
                            Paint(element, path, canvas, context);
                        }

                        break;
                    case "text" or "tspan" or "textPath":
                        throw Error(element, $"<{name}> is not supported; lay out text with the 'text' blocks of the step");
                    default:
                        throw Error(element, $"<{name}> is not supported");
                }
            }
            finally
            {
                if (opacity < 1)
                {
                    canvas.Restore();
                }
            }
        }

        private void RenderChildren(XElement element, DrawingCanvas canvas, Context context)
        {
            foreach (var child in element.Elements())
            {
                RenderElement(child, canvas, context);
            }
        }

        /// <summary>
        /// A nested <c>svg</c> (the root's view box is applied by <see cref="Render(string, int, int)"/>): a new viewport at x, y.
        /// </summary>
        private void RenderViewport(XElement element, DrawingCanvas canvas, Context context, bool isRoot)
        {
            if (isRoot)
            {
                RenderChildren(element, canvas, context);
                return;
            }

            var x = Length(element, "x", context, Axis.X);
            var y = Length(element, "y", context, Axis.Y);
            var width = element.Attribute("width") == null ? context.Viewport.Width : Length(element, "width", context, Axis.X);
            var height = element.Attribute("height") == null ? context.Viewport.Height : Length(element, "height", context, Axis.Y);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            RenderInViewport(element, element, canvas, context, x, y, new SizeF(width, height));
        }

        private void RenderInViewport(XElement viewBoxSource, XElement content, DrawingCanvas canvas, Context context, float x, float y, SizeF size)
        {
            var viewBox = ViewBox(viewBoxSource);
            var transform = (viewBox is { } box ? ViewBoxTransform(box, size, viewBoxSource.Attribute("preserveAspectRatio")?.Value) : Matrix3x2.Identity)
                * Matrix3x2.CreateTranslation(x, y)
                * context.Transform;

            RenderChildren(content, canvas, context with { Transform = transform, Viewport = viewBox is { } vb ? new SizeF(vb.Width, vb.Height) : size });
        }

        private void RenderUse(XElement element, DrawingCanvas canvas, Context context)
        {
            if (Href(element) is not { } href || !href.StartsWith('#'))
            {
                throw Error(element, "<use> must reference an element of the document (href=\"#id\")");
            }

            if (!_ids.TryGetValue(href[1..], out var target))
            {
                return;
            }

            if (++_uses > 1_000 || target.AncestorsAndSelf().Contains(element))
            {
                throw Error(element, "<use> references form a cycle");
            }

            var x = Length(element, "x", context, Axis.X);
            var y = Length(element, "y", context, Axis.Y);

            if (IsSvg(target) && target.Name.LocalName == "symbol")
            {
                var width = element.Attribute("width") == null ? context.Viewport.Width : Length(element, "width", context, Axis.X);
                var height = element.Attribute("height") == null ? context.Viewport.Height : Length(element, "height", context, Axis.Y);

                if (width > 0 && height > 0)
                {
                    RenderInViewport(target, target, canvas, context, x, y, new SizeF(width, height));
                }

                return;
            }

            RenderElement(target, canvas, context with { Transform = Matrix3x2.CreateTranslation(x, y) * context.Transform });
        }

        private void RenderImage(XElement element, DrawingCanvas canvas, Context context)
        {
            if (!context.Style.Visible)
            {
                return;
            }

            var href = Href(element) ?? throw Error(element, "<image> needs an href");
            var bytes = DataUri(element, href);
            var format = ImageFormats.Sniff(bytes) ?? throw Error(element, $"the image is not a format douze reads ({ImageFormats.ReadNames})");

            var x = Length(element, "x", context, Axis.X);
            var y = Length(element, "y", context, Axis.Y);
            var width = element.Attribute("width") is { } w && w.Value != "auto" ? Length(element, "width", context, Axis.X) : (float?)null;
            var height = element.Attribute("height") is { } h && h.Value != "auto" ? Length(element, "height", context, Axis.Y) : (float?)null;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            var (sourceWidth, sourceHeight) = format == ImageFormats.Svg || format == ImageFormats.Gif
                ? ImageFormats.ReadSize(format, bytes)
                : Identify(element, bytes);

            var viewport = new RectangleF(x, y, width ?? (height is { } only ? only * sourceWidth / sourceHeight : sourceWidth), height ?? (width is { } other ? other * sourceHeight / sourceWidth : sourceHeight));
            var fit = ViewBoxTransform(new RectangleF(0, 0, sourceWidth, sourceHeight), viewport.Size, element.Attribute("preserveAspectRatio")?.Value);
            var destination = new RectangleF(viewport.X + fit.M31, viewport.Y + fit.M32, sourceWidth * fit.M11, sourceHeight * fit.M22);

            // A slice overflows the viewport: the part of the source inside it is drawn.
            var visible = RectangleF.Intersect(destination, viewport);

            if (visible.Width <= 0 || visible.Height <= 0)
            {
                return;
            }

            var scale = (X: Math.Sqrt(context.Transform.M11 * context.Transform.M11 + context.Transform.M12 * context.Transform.M12), Y: Math.Sqrt(context.Transform.M21 * context.Transform.M21 + context.Transform.M22 * context.Transform.M22));
            var image = format == ImageFormats.Svg ? RenderNested(element, bytes, destination, scale) : Decode(element, bytes);
            _images.Add(image);

            var ratioX = image.Width / destination.Width;
            var ratioY = image.Height / destination.Height;
            var source = new Rectangle(
                (int)Math.Round((visible.X - destination.X) * ratioX),
                (int)Math.Round((visible.Y - destination.Y) * ratioY),
                Math.Max(1, (int)Math.Round(visible.Width * ratioX)),
                Math.Max(1, (int)Math.Round(visible.Height * ratioY)));

            canvas.Save(new DrawingOptions { Transform = new Matrix4x4(context.Transform) });
            canvas.DrawImage(image, Rectangle.Intersect(source, image.Bounds), visible, KnownResamplers.Bicubic);
            canvas.Restore();
        }

        private Image<Rgba32> RenderNested(XElement element, byte[] bytes, RectangleF destination, (double X, double Y) scale)
        {
            if (_depth >= 4)
            {
                throw Error(element, "SVG images are nested too deep");
            }

            var width = Math.Clamp((int)Math.Ceiling(destination.Width * scale.X), 1, 10_000);
            var height = Math.Clamp((int)Math.Ceiling(destination.Height * scale.Y), 1, 10_000);
            return Render(Encoding.UTF8.GetString(bytes), width, height, _depth + 1);
        }

        private static (int Width, int Height) Identify(XElement element, byte[] bytes)
        {
            try
            {
                var info = Image.Identify(new SixLabors.ImageSharp.Formats.DecoderOptions { Configuration = ImageProcessor.Configuration }, bytes);
                return (info.Width, info.Height);
            }
            catch (Exception e) when (e is ImageFormatException or NotSupportedException)
            {
                throw Error(element, $"cannot read the image ({e.Message.TrimEnd('.')})");
            }
        }

        private static Image<Rgba32> Decode(XElement element, byte[] bytes)
        {
            try
            {
                return Image.Load<Rgba32>(new SixLabors.ImageSharp.Formats.DecoderOptions { Configuration = ImageProcessor.Configuration, MaxFrames = 1 }, bytes);
            }
            catch (Exception e) when (e is ImageFormatException or NotSupportedException)
            {
                throw Error(element, $"cannot read the image ({e.Message.TrimEnd('.')})");
            }
        }

        private static byte[] DataUri(XElement element, string href)
        {
            if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || href.IndexOf(',') is not (> 0 and var comma))
            {
                throw Error(element, $"the image '{(href.Length > 80 ? href[..80] + "..." : href)}' is not inlined; reference images of the site by a relative path");
            }

            var header = href.AsSpan(5, comma - 5);
            var data = href.AsSpan(comma + 1);

            try
            {
                return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(data.ToString())
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data.ToString()));
            }
            catch (FormatException)
            {
                throw Error(element, "the image's data URI is not valid base64");
            }
        }

        /// <summary>
        /// The outline of a basic shape or path in user space, or null when it renders nothing.
        /// </summary>
        private IPath? Shape(XElement element, Context context)
        {
            string? data;

            switch (element.Name.LocalName)
            {
                case "rect":
                    var x = Length(element, "x", context, Axis.X);
                    var y = Length(element, "y", context, Axis.Y);
                    var width = Length(element, "width", context, Axis.X);
                    var height = Length(element, "height", context, Axis.Y);

                    if (width <= 0 || height <= 0)
                    {
                        return null;
                    }

                    var rx = element.Attribute("rx") is { Value: not "auto" } ? Length(element, "rx", context, Axis.X) : (float?)null;
                    var ry = element.Attribute("ry") is { Value: not "auto" } ? Length(element, "ry", context, Axis.Y) : (float?)null;
                    var radiusX = Math.Clamp(rx ?? ry ?? 0, 0, width / 2);
                    var radiusY = Math.Clamp(ry ?? rx ?? 0, 0, height / 2);

                    if (radiusX <= 0 || radiusY <= 0)
                    {
                        return new RectanglePolygon(x, y, width, height);
                    }

                    data = Invariant($"M{x + radiusX},{y} H{x + width - radiusX} A{radiusX},{radiusY} 0 0 1 {x + width},{y + radiusY} V{y + height - radiusY} A{radiusX},{radiusY} 0 0 1 {x + width - radiusX},{y + height} H{x + radiusX} A{radiusX},{radiusY} 0 0 1 {x},{y + height - radiusY} V{y + radiusY} A{radiusX},{radiusY} 0 0 1 {x + radiusX},{y} Z");
                    break;

                case "circle":
                    var r = Length(element, "r", context, Axis.Other);
                    data = r <= 0 ? null : Ellipse(Length(element, "cx", context, Axis.X), Length(element, "cy", context, Axis.Y), r, r);
                    break;

                case "ellipse":
                    var erx = Length(element, "rx", context, Axis.X);
                    var ery = Length(element, "ry", context, Axis.Y);
                    data = erx <= 0 || ery <= 0 ? null : Ellipse(Length(element, "cx", context, Axis.X), Length(element, "cy", context, Axis.Y), erx, ery);
                    break;

                case "line":
                    data = Invariant($"M{Length(element, "x1", context, Axis.X)},{Length(element, "y1", context, Axis.Y)} L{Length(element, "x2", context, Axis.X)},{Length(element, "y2", context, Axis.Y)}");
                    break;

                case "polyline" or "polygon":
                    var points = Numbers(element.Attribute("points")?.Value ?? "");

                    if (points.Count < 4)
                    {
                        return null;
                    }

                    var builder = new StringBuilder();

                    for (var i = 0; i + 1 < points.Count; i += 2)
                    {
                        builder.Append(i == 0 ? 'M' : 'L').Append(Invariant($"{points[i]},{points[i + 1]} "));
                    }

                    data = element.Name.LocalName == "polygon" ? builder.Append('Z').ToString() : builder.ToString();
                    break;

                default:
                    data = element.Attribute("d")?.Value;

                    if (string.IsNullOrWhiteSpace(data) || data.Trim() == "none")
                    {
                        return null;
                    }

                    break;
            }

            if (data == null)
            {
                return null;
            }

            if (!Path.TryParseSvgPath(data, out var path))
            {
                throw Error(element, "invalid path data");
            }

            return path;

            static string Ellipse(float cx, float cy, float rx, float ry) => Invariant($"M{cx - rx},{cy} A{rx},{ry} 0 1 0 {cx + rx},{cy} A{rx},{ry} 0 1 0 {cx - rx},{cy} Z");
        }

        /// <summary>
        /// Fills then strokes a shape, in the user space of its element.
        /// </summary>
        private void Paint(XElement element, IPath path, DrawingCanvas canvas, Context context)
        {
            var style = context.Style;

            if (!style.Visible)
            {
                return;
            }

            var transform = new Matrix4x4(context.Transform);
            var bounds = path.Bounds;

            if (Brush(element, style.Fill, style.FillOpacity, bounds, context) is { } fill)
            {
                canvas.Save(new DrawingOptions { Transform = transform, IntersectionRule = style.FillRule });
                canvas.Fill(fill, path);
                canvas.Restore();
            }

            var width = Length(element, style.StrokeWidth, context, Axis.Other, "stroke-width");

            if (width > 0 && Brush(element, style.Stroke, style.StrokeOpacity, bounds, context) is { } stroke)
            {
                var dashes = style.DashArray is { } array and not "none" ? Numbers(array).Select(x => x / width).ToArray() : [];

                // An odd list repeats; a list of zeros draws a solid stroke.
                if (dashes.Length % 2 == 1)
                {
                    dashes = [.. dashes, .. dashes];
                }

                var options = dashes.Length > 0 && dashes.Any(x => x > 0) && dashes.All(x => x >= 0)
                    ? new PenOptions(stroke, width, dashes) { StrokePatternOffset = Length(element, style.DashOffset, context, Axis.Other, "stroke-dashoffset") / width }
                    : new PenOptions(stroke, width, []);

                options.StrokeOptions = new StrokeOptions { LineCap = style.LineCap, LineJoin = style.LineJoin, MiterLimit = style.MiterLimit };

                canvas.Save(new DrawingOptions { Transform = transform });
                canvas.Draw(dashes.Length > 0 ? new PatternPen(options) : new SolidPen(options), path);
                canvas.Restore();
            }
        }

        /// <summary>
        /// The brush of a paint, or null for none. A gradient is defined in the shape's user space (or its bounding box).
        /// </summary>
        private Brush? Brush(XElement element, Paint paint, float opacity, RectangleF bounds, Context context)
        {
            switch (paint)
            {
                case SolidPaint solid:
                    return Brushes.Solid(WithOpacity(solid.Color, opacity));
                case CurrentColorPaint:
                    return Brushes.Solid(WithOpacity(context.Style.Color, opacity));
                case UrlPaint url:
                    if (!_ids.TryGetValue(url.Id, out var target))
                    {
                        return Brush(element, url.Fallback, opacity, bounds, context);
                    }

                    return target.Name.LocalName switch
                    {
                        "linearGradient" or "radialGradient" => Gradient(target, opacity, bounds, context),
                        var other => throw Error(element, $"a paint of <{other}> is not supported"),
                    };
                default:
                    return null;
            }
        }

        private Brush? Gradient(XElement gradient, float opacity, RectangleF bounds, Context context)
        {
            // The attributes and stops of a gradient are inherited from the gradients its href references.
            var chain = new List<XElement> { gradient };

            while (Href(chain[^1]) is { } href && href.StartsWith('#') && _ids.TryGetValue(href[1..], out var parent)
                && parent.Name.LocalName is "linearGradient" or "radialGradient" && !chain.Contains(parent))
            {
                chain.Add(parent);
            }

            string? Attribute(string name) => chain.Select(x => x.Attribute(name)?.Value).FirstOrDefault(x => x != null);

            var stops = Stops(chain.FirstOrDefault(x => x.Elements().Any(IsStop)) ?? gradient, opacity);

            if (stops.Length == 0)
            {
                return null;
            }

            if (stops.Length == 1)
            {
                return Brushes.Solid(stops[0].Color);
            }

            var boundingBox = Attribute("gradientUnits") != "userSpaceOnUse";

            if (boundingBox && (bounds.Width <= 0 || bounds.Height <= 0))
            {
                return null;
            }

            var space = Attribute("gradientTransform") is { } text ? ParseTransform(gradient, text) : Matrix3x2.Identity;

            if (boundingBox)
            {
                space *= new Matrix3x2(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y);
            }

            var transform = new Matrix4x4(space);
            var mode = Attribute("spreadMethod") switch
            {
                "reflect" => GradientRepetitionMode.Reflect,
                "repeat" => GradientRepetitionMode.Repeat,
                _ => GradientRepetitionMode.None,
            };

            float Coordinate(string name, string fallback, Axis axis)
            {
                var value = Attribute(name) ?? fallback;

                if (boundingBox)
                {
                    return value.TrimEnd().EndsWith('%') ? Number(gradient, value.TrimEnd()[..^1], name) / 100 : Number(gradient, value, name);
                }

                return Length(gradient, value, context, axis, name);
            }

            if (gradient.Name.LocalName == "linearGradient")
            {
                var start = new PointF(Coordinate("x1", "0%", Axis.X), Coordinate("y1", "0%", Axis.Y));
                var end = new PointF(Coordinate("x2", "100%", Axis.X), Coordinate("y2", "0%", Axis.Y));
                return start == end ? Brushes.Solid(stops[^1].Color) : new LinearGradientBrush(start, end, mode, transform, stops);
            }

            var center = new PointF(Coordinate("cx", "50%", Axis.X), Coordinate("cy", "50%", Axis.Y));
            var radius = Coordinate("r", "50%", Axis.Other);
            var focal = new PointF(Attribute("fx") == null ? center.X : Coordinate("fx", "50%", Axis.X), Attribute("fy") == null ? center.Y : Coordinate("fy", "50%", Axis.Y));
            var focalRadius = Coordinate("fr", "0%", Axis.Other);

            return radius <= 0 ? Brushes.Solid(stops[^1].Color) : new RadialGradientBrush(focal, focalRadius, center, radius, mode, transform, stops);
        }

        private ColorStop[] Stops(XElement gradient, float opacity)
        {
            var stops = new List<ColorStop>();
            var previous = 0f;

            foreach (var stop in gradient.Elements().Where(IsStop))
            {
                var declared = Declared(stop);
                var offsetText = stop.Attribute("offset")?.Value.Trim() ?? "0";
                var offset = offsetText.EndsWith('%') ? Number(stop, offsetText[..^1], "offset") / 100 : Number(stop, offsetText, "offset");
                offset = Math.Max(previous, Math.Clamp(offset, 0, 1));
                previous = offset;

                var color = declared.TryGetValue("stop-color", out var value) ? ParseColor(stop, value) switch
                {
                    SolidPaint solid => solid.Color,
                    CurrentColorPaint => declared.TryGetValue("color", out var current) && ParseColor(stop, current) is SolidPaint own ? own.Color : Color.Black,
                    _ => Color.Black,
                } : Color.Black;

                var stopOpacity = declared.TryGetValue("stop-opacity", out var alpha) ? Opacity(alpha) : 1;
                stops.Add(new ColorStop(offset, WithOpacity(color, stopOpacity * opacity)));
            }

            return [.. stops];
        }

        private static bool IsStop(XElement element) => IsSvg(element) && element.Name.LocalName == "stop";

        /// <summary>
        /// The properties an element declares: presentation attributes, then the rules of the style sheets that match
        /// it (by specificity), then its <c>style</c> attribute.
        /// </summary>
        private Dictionary<string, string> Declared(XElement element)
        {
            var declared = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.Namespace == XNamespace.None && Properties.Contains(attribute.Name.LocalName))
                {
                    declared[attribute.Name.LocalName] = attribute.Value.Trim();
                }
            }

            if (_rules.Count > 0)
            {
                foreach (var rule in _rules.Where(x => x.Matches(element)).OrderBy(x => x.Specificity))
                {
                    foreach (var (name, value) in rule.Declarations)
                    {
                        declared[name] = value;
                    }
                }
            }

            if (element.Attribute("style")?.Value is { } style)
            {
                foreach (var (name, value) in Declarations(style))
                {
                    declared[name] = value;
                }
            }

            foreach (var (name, value) in declared.Where(x => x.Value == "inherit").ToArray())
            {
                declared.Remove(name);
            }

            return declared;
        }

        private Style Cascade(XElement element, Style parent, Dictionary<string, string> declared)
        {
            var style = parent;

            foreach (var (name, value) in declared)
            {
                style = name switch
                {
                    "fill" => style with { Fill = ParseColor(element, value) },
                    "fill-opacity" => style with { FillOpacity = Opacity(value) },
                    "fill-rule" => style with { FillRule = value == "evenodd" ? IntersectionRule.EvenOdd : IntersectionRule.NonZero },
                    "stroke" => style with { Stroke = ParseColor(element, value) },
                    "stroke-width" => style with { StrokeWidth = value },
                    "stroke-opacity" => style with { StrokeOpacity = Opacity(value) },
                    "stroke-linecap" => style with
                    {
                        LineCap = value switch
                        {
                            "round" => LineCap.Round,
                            "square" => LineCap.Square,
                            _ => LineCap.Butt,
                        },
                    },
                    "stroke-linejoin" => style with
                    {
                        LineJoin = value switch
                        {
                            "round" => LineJoin.Round,
                            "bevel" => LineJoin.Bevel,
                            _ => LineJoin.Miter,
                        },
                    },
                    "stroke-miterlimit" => style with { MiterLimit = Math.Max(1, Number(element, value, name)) },
                    "stroke-dasharray" => style with { DashArray = value },
                    "stroke-dashoffset" => style with { DashOffset = value },
                    "color" => ParseColor(element, value) is SolidPaint color ? style with { Color = color.Color } : style,
                    "visibility" => style with { Visible = value == "visible" },
                    _ => style,
                };
            }

            return style;
        }

        private void ParseStyleSheet(XElement element)
        {
            var text = CssComment().Replace(element.Value, "");
            var position = 0;

            while (position < text.Length)
            {
                var open = text.IndexOf('{', position);

                if (open < 0)
                {
                    break;
                }

                var prelude = text[position..open].Trim();
                var close = MatchingBrace(text, open);
                var body = text[(open + 1)..close];
                position = close + 1;

                // At-rules (@font-face, @media, @import) style no shape of a static card.
                if (prelude.StartsWith('@'))
                {
                    continue;
                }

                var declarations = Declarations(body).Where(x => Properties.Contains(x.Name)).ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

                foreach (var selector in prelude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var match = SimpleSelector().Match(selector);

                    if (!match.Success)
                    {
                        throw Error(element, $"the CSS selector '{selector}' is not supported; use a type, class or id selector");
                    }

                    var tag = match.Groups["tag"].Value is { Length: > 0 } t && t != "*" ? t : null;
                    var id = match.Groups["id"].Success ? match.Groups["id"].Value : null;
                    var classes = match.Groups["class"].Captures.Select(x => x.Value).ToArray();
                    _rules.Add(new CssRule(tag, id, classes, _rules.Count, declarations));
                }
            }

            static int MatchingBrace(string text, int open)
            {
                var depth = 0;

                for (var i = open; i < text.Length; i++)
                {
                    depth += text[i] switch
                    {
                        '{' => 1,
                        '}' => -1,
                        _ => 0,
                    };

                    if (depth == 0)
                    {
                        return i;
                    }
                }

                return text.Length - 1;
            }
        }

        private static IEnumerable<(string Name, string Value)> Declarations(string text)
        {
            foreach (var declaration in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':');

                if (colon > 0)
                {
                    var value = declaration[(colon + 1)..].Trim();
                    var important = value.EndsWith("!important", StringComparison.Ordinal);
                    yield return (declaration[..colon].Trim(), important ? value[..^"!important".Length].Trim() : value);
                }
            }
        }
    }

    private enum Axis
    {
        X,
        Y,
        Other,
    }

    private static bool IsSvg(XElement element) => element.Name.Namespace == SvgNamespace || element.Name.Namespace == XNamespace.None;

    private static string? Href(XElement element) => (element.Attribute(XName.Get("href", XlinkNamespace)) ?? element.Attribute("href"))?.Value.Trim();

    private static float Length(XElement element, string attribute, Context context, Axis axis)
    {
        return element.Attribute(attribute)?.Value is { } value ? Length(element, value, context, axis, attribute) : 0;
    }

    private static float Length(XElement element, string value, Context context, Axis axis, string name)
    {
        if (!TryLength(value, out var length, out var percent))
        {
            throw Error(element, $"invalid length '{value}' of '{name}'");
        }

        if (!percent)
        {
            return length;
        }

        var size = context.Viewport;
        var reference = axis switch
        {
            Axis.X => size.Width,
            Axis.Y => size.Height,
            _ => (float)Math.Sqrt((size.Width * size.Width + size.Height * size.Height) / 2),
        };

        return length / 100 * reference;
    }

    /// <summary>
    /// A length in user units (px): a number with a unit (<c>px</c>, <c>pt</c>, <c>pc</c>, <c>mm</c>, <c>cm</c>,
    /// <c>in</c>, <c>em</c> of 16px, <c>ex</c> of 8px) or a percentage, returned as is.
    /// </summary>
    private static bool TryLength(string value, out float length, out bool percent)
    {
        var text = value.Trim();
        var end = text.Length;

        while (end > 0 && (char.IsAsciiLetter(text[end - 1]) || text[end - 1] == '%'))
        {
            end--;
        }

        var unit = text[end..];
        percent = unit == "%";
        length = 0;

        if (!float.TryParse(text.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number))
        {
            return false;
        }

        float? factor = unit switch
        {
            "" or "px" or "%" => 1f,
            "pt" => 4f / 3,
            "pc" => 16f,
            "mm" => 96f / 25.4f,
            "cm" => 96f / 2.54f,
            "in" => 96f,
            "em" => 16f,
            "ex" => 8f,
            _ => null,
        };

        length = number * (factor ?? 0);
        return factor != null;
    }

    private static float Number(XElement element, string value, string name)
    {
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
            ? number
            : throw Error(element, $"invalid number '{value}' of '{name}'");
    }

    private static float Opacity(string value)
    {
        var text = value.Trim();
        var percent = text.EndsWith('%');

        return float.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(percent ? number / 100 : number, 0, 1)
            : 1;
    }

    /// <summary>
    /// The numbers of a list (<c>points</c>, <c>viewBox</c>, a transform's arguments, a dash array), separated by
    /// white space or commas, or by a sign or a second decimal point (<c>1-2</c>, <c>.5.5</c>).
    /// </summary>
    private static List<float> Numbers(string text)
    {
        var numbers = new List<float>();

        foreach (Match match in NumberToken().Matches(text))
        {
            if (float.TryParse(match.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    private static Matrix3x2 ParseTransform(XElement element, string text)
    {
        var result = Matrix3x2.Identity;
        var matches = TransformFunction().Matches(text);

        if (matches.Count == 0 && !string.IsNullOrWhiteSpace(text) && text.Trim() != "none")
        {
            throw Error(element, $"invalid transform '{text}'");
        }

        foreach (Match match in matches)
        {
            var arguments = Numbers(match.Groups["arguments"].Value);
            var name = match.Groups["name"].Value;

            float Argument(int index, float fallback = 0) => index < arguments.Count ? arguments[index] : fallback;

            var transform = name switch
            {
                "matrix" when arguments.Count == 6 => new Matrix3x2(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5]),
                "translate" when arguments.Count is 1 or 2 => Matrix3x2.CreateTranslation(Argument(0), Argument(1)),
                "scale" when arguments.Count is 1 or 2 => Matrix3x2.CreateScale(Argument(0), Argument(1, Argument(0))),
                "rotate" when arguments.Count is 1 or 3 => Matrix3x2.CreateRotation(Argument(0) * MathF.PI / 180, new Vector2(Argument(1), Argument(2))),
                "skewX" when arguments.Count == 1 => new Matrix3x2(1, 0, MathF.Tan(Argument(0) * MathF.PI / 180), 1, 0, 0),
                "skewY" when arguments.Count == 1 => new Matrix3x2(1, MathF.Tan(Argument(0) * MathF.PI / 180), 0, 1, 0, 0),
                _ => throw Error(element, $"invalid transform '{match.Value}'"),
            };

            // A list applies its last transform first: translate(10) scale(2) scales, then translates.
            result = transform * result;
        }

        return result;
    }

    /// <summary>
    /// A paint: <c>none</c>, a color, <c>currentColor</c>, or <c>url(#id)</c> with an optional fallback color.
    /// </summary>
    private static Paint ParseColor(XElement element, string value)
    {
        var text = value.Trim();

        if (text.StartsWith("url(", StringComparison.Ordinal))
        {
            var close = text.IndexOf(')');

            if (close < 0)
            {
                throw Error(element, $"invalid paint '{value}'");
            }

            var reference = text[4..close].Trim().Trim('"', '\'');
            var fallback = text[(close + 1)..].Trim();

            if (!reference.StartsWith('#'))
            {
                throw Error(element, $"the paint '{value}' must reference an element of the document");
            }

            return new UrlPaint(reference[1..], fallback.Length == 0 ? NoPaint.Instance : ParseColor(element, fallback));
        }

        if (text is "none" or "transparent")
        {
            return text == "none" ? NoPaint.Instance : new SolidPaint(Color.Transparent);
        }

        if (text.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return CurrentColorPaint.Instance;
        }

        return TryParseColor(text, out var color) ? new SolidPaint(color) : throw Error(element, $"invalid color '{value}'");
    }

    internal static bool TryParseColor(string text, out Color color)
    {
        color = default;

        if (text.StartsWith('#'))
        {
            try
            {
                var (r, g, b, a) = ImageProcessor.ParseColor(text);
                color = Color.FromPixel(new Rgba32((byte)r, (byte)g, (byte)b, (byte)a));
                return true;
            }
            catch (DouzeException)
            {
                return false;
            }
        }

        var function = ColorFunction().Match(text);

        if (function.Success)
        {
            var name = function.Groups["name"].Value.ToLowerInvariant();
            var parts = function.Groups["arguments"].Value.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length is not (3 or 4))
            {
                return false;
            }

            float Channel(string part, float scale)
            {
                return part.EndsWith('%')
                    ? float.Parse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture) / 100 * scale
                    : float.Parse(part.TrimEnd('d', 'e', 'g'), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            try
            {
                var alpha = parts.Length == 4 ? Math.Clamp(Channel(parts[3], 1), 0, 1) : 1;

                if (name.StartsWith("rgb", StringComparison.Ordinal))
                {
                    color = Color.FromPixel(new Rgba32(Math.Clamp(Channel(parts[0], 255), 0, 255) / 255, Math.Clamp(Channel(parts[1], 255), 0, 255) / 255, Math.Clamp(Channel(parts[2], 255), 0, 255) / 255, alpha));
                    return true;
                }

                var (red, green, blue) = Hsl(Channel(parts[0], 360), Math.Clamp(Channel(parts[1], 1), 0, 1), Math.Clamp(Channel(parts[2], 1), 0, 1));
                color = Color.FromPixel(new Rgba32(red, green, blue, alpha));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return Color.TryParse(text, out color);
    }

    private static (float R, float G, float B) Hsl(float hue, float saturation, float lightness)
    {
        var h = ((hue % 360) + 360) % 360 / 360;
        var q = lightness < 0.5f ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;

        return (Channel(h + 1f / 3), Channel(h), Channel(h - 1f / 3));

        float Channel(float t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
        }
    }

    private static Color WithOpacity(Color color, float opacity)
    {
        if (opacity >= 1)
        {
            return color;
        }

        var pixel = color.ToPixel<Rgba32>();
        return Color.FromPixel(new Rgba32(pixel.R, pixel.G, pixel.B, (byte)Math.Round(pixel.A * opacity)));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static DouzeException Error(XElement element, string message)
    {
        var line = ((IXmlLineInfo)element).HasLineInfo() ? $" (line {((IXmlLineInfo)element).LineNumber})" : "";
        return new DouzeException($"SVG <{element.Name.LocalName}>{line}: {message}");
    }

    [GeneratedRegex(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"(?<name>[a-zA-Z]+)\s*\((?<arguments>[^)]*)\)")]
    private static partial Regex TransformFunction();

    [GeneratedRegex(@"^(?<name>rgba?|hsla?)\(\s*(?<arguments>[^)]*)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColorFunction();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"^(?<tag>[a-zA-Z][\w-]*|\*)?(?:#(?<id>[\w-]+)|\.(?<class>[\w-]+))*$")]
    private static partial Regex SimpleSelector();
}
