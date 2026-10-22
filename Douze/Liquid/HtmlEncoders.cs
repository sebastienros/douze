using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Douze.Liquid;

/// <summary>
/// The HTML encoder of every render: <see cref="HtmlEncoder.Default"/> writes every non-ASCII character as a numeric
/// entity (<c>&amp;#xE9;</c> for <c>é</c>), which bloats pages written in any language; this one encodes the HTML
/// characters only.
/// </summary>
internal static class HtmlEncoders
{
    public static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);
}
