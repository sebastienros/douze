using System.Globalization;
using System.Text.RegularExpressions;

namespace Douze.Highlighting;

/// <summary>
/// An RGBA color, with the arithmetic that adjusts the colors of themes for contrast: luminance, WCAG contrast ratios,
/// mixing, compositing on a background, searches for a luminance or an alpha that reaches a contrast. The rules (and
/// their rounding) are those of the tinycolor library, as code highlighters on the web apply them, so that a theme
/// gets the same colors. Channels are 0 to 255 (fractions kept until written), alpha 0 to 1.
/// </summary>
internal readonly partial record struct Color(double R, double G, double B, double A)
{
    public static readonly Color Black = new(0, 0, 0, 1);

    public static readonly Color White = new(255, 255, 255, 1);

    /// <summary>
    /// A color from channels, as tinycolor builds one from an object: clamped, and channels below 1 rounded.
    /// </summary>
    public static Color FromChannels(double r, double g, double b, double a)
    {
        static double Channel(double value)
        {
            value = Math.Min(255, Math.Max(0, value));
            value = Math.Abs(value - 255) < 0.000001 ? 255 : value % 255;
            return value < 1 ? Math.Round(value, MidpointRounding.AwayFromZero) : value;
        }

        return new Color(Channel(r), Channel(g), Channel(b), double.IsNaN(a) || a < 0 || a > 1 ? 1 : a);
    }

    /// <summary>
    /// Parses a CSS color: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>, <c>rgba()</c>,
    /// <c>lch()</c>, <c>white</c>, <c>black</c> or <c>transparent</c>.
    /// </summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().ToLowerInvariant();

        switch (value)
        {
            case "transparent":
                color = new Color(0, 0, 0, 0);
                return true;
            case "white":
                color = White;
                return true;
            case "black":
                color = Black;
                return true;
        }

        if (Lch().Match(value) is { Success: true } lch)
        {
            color = FromLch(
                CssValue(lch.Groups[1].Value, 0, 100),
                CssValue(lch.Groups[2].Value, 0, 150),
                NormalizeAngle(double.Parse(lch.Groups[3].Value.Replace("deg", "", StringComparison.Ordinal), CultureInfo.InvariantCulture)),
                lch.Groups[4].Success ? CssValue(lch.Groups[4].Value, 0, 1) : 1);
            return true;
        }

        if (Rgb().Match(value) is { Success: true } rgb)
        {
            var a = rgb.Groups[4].Success ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            color = FromChannels(RgbChannel(rgb.Groups[1].Value), RgbChannel(rgb.Groups[2].Value), RgbChannel(rgb.Groups[3].Value), a);
            return true;
        }

        var hex = value.StartsWith('#') ? value[1..] : value;

        if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        int Hex(int start, int length) => int.Parse(length == 1 ? new string(hex[start], 2) : hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        color = hex.Length <= 4
            ? new Color(Hex(0, 1), Hex(1, 1), Hex(2, 1), hex.Length == 4 ? Hex(3, 1) / 255.0 : 1)
            : new Color(Hex(0, 2), Hex(2, 2), Hex(4, 2), hex.Length == 8 ? Hex(6, 2) / 255.0 : 1);
        return true;
    }

    public static Color Parse(string text) => TryParse(text, out var color) ? color : throw new FormatException($"'{text}' is not a color");

    private static double RgbChannel(string value) => value.EndsWith('%')
        ? Math.Floor(double.Parse(value[..^1], CultureInfo.InvariantCulture) * 255) / 100
        : double.Parse(value, CultureInfo.InvariantCulture);

    private static double CssValue(string value, double min, double max)
    {
        var number = double.Parse(value.TrimEnd('%'), CultureInfo.InvariantCulture);
        return Math.Max(min, Math.Min(max, value.EndsWith('%') ? number * max / 100 : number));
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }

    /// <summary>
    /// A CIE LCH color (D65), converted to sRGB without gamut mapping, as a channel can be out of range.
    /// </summary>
    private static Color FromLch(double l, double c, double h, double alpha)
    {
        var a = c * Math.Cos(h * Math.PI / 180);
        var b = c * Math.Sin(h * Math.PI / 180);

        // Lab to XYZ (D65).
        double[] whitePoint = [0.3127 / 0.329, 1, (1 - 0.3127 - 0.329) / 0.329];
        var y = (l / 100 + 0.16) / 1.16;
        var x = a / 100 / 5 + y;
        var z = y - b / 100 / 2;

        static double Transform(double v, double white)
        {
            var cube = Math.Pow(v, 3);
            return (cube > 0.008856 ? cube : (v - 16.0 / 116) / 7.787037) * white;
        }

        x = Transform(x, whitePoint[0]);
        y = Transform(y, whitePoint[1]);
        z = Transform(z, whitePoint[2]);

        // XYZ to sRGB.
        static double ToSrgb(double v) => v > 0.0031308 ? 1.055 * Math.Pow(v, 1 / 2.4) - 0.055 : 12.92 * v;

        var r = ToSrgb(x * 3.240969941904521 + y * -1.537383177570093 + z * -0.498610760293) * 255;
        var g = ToSrgb(x * -0.96924363628087 + y * 1.87596750150772 + z * 0.041555057407175) * 255;
        var bl = ToSrgb(x * 0.055630079696993 + y * -0.20397695888897 + z * 1.056971514242878) * 255;
        return FromChannels(r, g, bl, alpha);
    }

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The relative luminance (WCAG) of the rounded channels.
    /// </summary>
    public double Luminance
    {
        get
        {
            static double Linear(double channel)
            {
                var s = Round(channel) / 255;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
        }
    }

    /// <summary>
    /// The WCAG contrast ratio of two colors, alpha ignored.
    /// </summary>
    public static double Contrast(Color a, Color b)
    {
        var la = a.Luminance;
        var lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public Color WithAlpha(double alpha) => this with { A = double.IsNaN(alpha) || alpha < 0 || alpha > 1 ? 1 : alpha };

    /// <summary>
    /// This color mixed with another, <paramref name="amount"/> from 0 (this) to 100 (the other), on rounded channels.
    /// </summary>
    public Color Mix(Color other, double amount)
    {
        var p = amount / 100;
        return FromChannels(
            (Round(other.R) - Round(R)) * p + Round(R),
            (Round(other.G) - Round(G)) * p + Round(G),
            (Round(other.B) - Round(B)) * p + Round(B),
            (other.A - A) * p + A);
    }

    /// <summary>
    /// This color composited over <paramref name="background"/>.
    /// </summary>
    public Color OnBackground(Color background)
    {
        var alpha = A + background.A * (1 - A);
        return FromChannels(
            (Round(R) * A + Round(background.R) * background.A * (1 - A)) / alpha,
            (Round(G) * A + Round(background.G) * background.A * (1 - A)) / alpha,
            (Round(B) * A + Round(background.B) * background.A * (1 - A)) / alpha,
            alpha);
    }

    /// <summary>
    /// <c>#rrggbb</c>, or <c>#rrggbbaa</c> when not opaque, in lower case.
    /// </summary>
    public string ToHex()
    {
        var hex = $"#{(int)Round(R):x2}{(int)Round(G):x2}{(int)Round(B):x2}";
        return A == 1 ? hex : hex + ((int)Round(A * 255)).ToString("x2", CultureInfo.InvariantCulture);
    }

    public override string ToString() => ToHex();

    /// <summary>
    /// The color, made readable on <paramref name="background"/>: its luminance changed, then its alpha, until the
    /// contrast of the color composited on the background is between <paramref name="minContrast"/> and
    /// <paramref name="maxContrast"/>. Returns the color as hex.
    /// </summary>
    public static string EnsureContrast(Color color, Color background, double minContrast, double maxContrast = 22)
    {
        var current = Contrast(color.OnBackground(background), background);

        if (current < minContrast && Contrast(color, background) < minContrast)
        {
            color = ChangeLuminanceToReachContrast(color, background, minContrast);
            current = Contrast(color.OnBackground(background), background);
        }

        if (current < minContrast || current > maxContrast)
        {
            color = ChangeAlphaToReachContrast(color, background, minContrast, maxContrast);
        }

        return color.ToHex();
    }

    private static Color ChangeLuminanceToReachContrast(Color color, Color background, double minContrast)
    {
        var oldContrast = Contrast(color, background);

        if (oldContrast >= minContrast)
        {
            return Parse(color.ToHex());
        }

        var colorLuminance = color.Luminance;
        var backgroundLuminance = background.Luminance;
        var lightened = SetLuminance(color, (backgroundLuminance + 0.05) * minContrast - 0.05);
        var darkened = SetLuminance(color, (backgroundLuminance + 0.05) / minContrast - 0.05);
        var lightenedContrast = Contrast(lightened, background);
        var darkenedContrast = Contrast(darkened, background);

        if (lightenedContrast <= oldContrast && darkenedContrast <= oldContrast)
        {
            return Parse(color.ToHex());
        }

        if (colorLuminance >= backgroundLuminance && lightenedContrast >= minContrast)
        {
            return lightened;
        }

        if (colorLuminance < backgroundLuminance && darkenedContrast >= minContrast)
        {
            return darkened;
        }

        return lightenedContrast > darkenedContrast ? lightened : darkened;
    }

    private static Color SetLuminance(Color color, double target)
    {
        color = Parse(color.ToHex());
        target = MinMaxRounded(target);
        var increasing = target > color.Luminance;
        var mixWith = increasing ? White : Black;
        var amount = BinarySearch(
            amount => color.Mix(mixWith, amount * 100).Luminance,
            target,
            target > 0 && target < 1 ? increasing : null,
            1.0 / 256,
            increasing ? 0 : 1,
            increasing ? 1 : 0);
        return Parse(color.Mix(mixWith, amount * 100).ToHex());
    }

    private static Color ChangeAlphaToReachContrast(Color color, Color background, double minContrast, double maxContrast)
    {
        color = Parse(color.ToHex());
        var current = Contrast(color.OnBackground(background), background);

        if (current >= minContrast && current <= maxContrast)
        {
            return color;
        }

        var alpha = BinarySearch(
            alpha => Contrast(color.WithAlpha(alpha).OnBackground(background), background),
            current < minContrast ? minContrast : maxContrast,
            current < minContrast,
            1.0 / 256,
            0.15,
            1);
        return Parse(color.WithAlpha(alpha).ToHex());
    }

    private static double MinMaxRounded(double value) => Math.Max(0, Math.Min(1, Math.Round(value * 1000, MidpointRounding.AwayFromZero) / 1000));

    private static double BinarySearch(Func<double, double> valueOf, double target, bool? preferHigher, double tolerance, double low, double high)
    {
        const int MaxIterations = 25;
        var epsilon = 0.001 * Math.Abs(high - low);
        double? lastMid = null;
        var mid = (low + high) / 2;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            mid = (low + high) / 2;
            var value = valueOf(mid);
            var withinTolerance = Math.Abs(value - target) <= tolerance;
            var preferred = preferHigher is not { } higher || (higher ? value > target : value < target);
            var unchanged = lastMid is { } last && Math.Abs(last - mid) < epsilon;

            if (preferred && (withinTolerance || unchanged))
            {
                return mid;
            }

            if (value < target)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }

            lastMid = mid;
        }

        return (low + high) / 2;
    }

    [GeneratedRegex(@"^lch\(\s*([\d.]+%?)\s+([\d.]+%?)\s+([\d.]+(?:deg)?)(?:\s*\/\s*([\d.]+%?))?\s*\)$")]
    private static partial Regex Lch();

    [GeneratedRegex(@"^rgba?\(\s*([\d.]+%?)[,\s]+([\d.]+%?)[,\s]+([\d.]+%?)(?:[,\s/]+([\d.]+))?\s*\)$")]
    private static partial Regex Rgb();
}
