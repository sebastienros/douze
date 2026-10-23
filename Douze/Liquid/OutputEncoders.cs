using System.Text.Encodings.Web;
using System.Text.Unicode;
using Fluid;

namespace Douze.Liquid;

/// <summary>
/// The encoders of <c>{{ }}</c> outputs, one per kind of output: HTML, XML (feeds, sitemaps, SVG), JSON strings, and
/// none for text. <see cref="ForContentType"/> picks the one of an output file.
/// </summary>
internal static class OutputEncoders
{
    /// <summary>
    /// <see cref="HtmlEncoder.Default"/> writes every non-ASCII character as a numeric entity (<c>&amp;#xE9;</c> for
    /// <c>é</c>), which bloats pages written in any language; this one encodes the HTML characters only.
    /// </summary>
    public static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c> and <c>'</c> as entities, like <c>xml_escape</c>; every other
    /// character as it is (the <c>+</c> of a date).
    /// </summary>
    public static readonly TextEncoder Xml = new XmlEncoder();

    /// <summary>
    /// The content of a JSON string: quotes, backslashes and control characters escaped, every other character as
    /// it is. A JSON file is not embedded in HTML, where the relaxed escaping would not be safe.
    /// </summary>
    public static readonly TextEncoder Json = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// The encoder of an output of <paramref name="contentType"/>: HTML for HTML and Markdown, XML for XML types,
    /// JSON for JSON types, none for anything else (text, CSS, scripts).
    /// </summary>
    public static TextEncoder ForContentType(string contentType)
    {
        return contentType switch
        {
            "text/html" or "text/markdown" => Html,
            "application/xml" or "text/xml" => Xml,
            "application/json" => Json,
            _ when contentType.EndsWith("+xml", StringComparison.Ordinal) => Xml,
            _ when contentType.EndsWith("+json", StringComparison.Ordinal) => Json,
            _ => NullEncoder.Default,
        };
    }

    private sealed class XmlEncoder : TextEncoder
    {
        public override int MaxOutputCharactersPerInputCharacter => 6;

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            return new ReadOnlySpan<char>(text, textLength).IndexOfAny("&<>\"'");
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            var entity = unicodeScalar switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => null,
            };

            var destination = new Span<char>(buffer, bufferLength);

            if (entity != null)
            {
                numberOfCharactersWritten = entity.Length;
                return entity.TryCopyTo(destination);
            }

            if (new System.Text.Rune(unicodeScalar).TryEncodeToUtf16(destination, out numberOfCharactersWritten))
            {
                return true;
            }

            numberOfCharactersWritten = 0;
            return false;
        }

        public override bool WillEncode(int unicodeScalar)
        {
            return unicodeScalar is '&' or '<' or '>' or '"' or '\'';
        }
    }
}
