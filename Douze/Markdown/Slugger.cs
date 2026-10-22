using System.Globalization;
using System.Text;

namespace Douze.Markdown;

/// <summary>
/// Heading ids, with GitHub's rules: lowercase, every character that is not a letter, a mark, a digit, a space, a
/// hyphen or an underscore removed (letters of every script are kept), each space replaced by a hyphen. Duplicates on
/// a page get <c>-1</c>, <c>-2</c>... (<see cref="Scope"/>). For heading ids only: the <c>slug</c> filter has its own
/// rules, for urls.
/// </summary>
internal static class Slugger
{
    /// <summary>
    /// The id of a heading whose text gives an empty slug (only symbols or emoji).
    /// </summary>
    public const string Fallback = "heading";

    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ' ')
            {
                builder.Append('-');
                continue;
            }

            if (rune.Value is '-' or '_')
            {
                builder.Append((char)rune.Value);
                continue;
            }

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                    builder.Append(Rune.ToLowerInvariant(rune).ToString());
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The ids of one page: a slug already used gets the first free suffix, <c>-1</c>, <c>-2</c>...
    /// </summary>
    public sealed class Scope
    {
        private readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);

        /// <summary>
        /// Marks an id as used without changing it (an id the page sets itself).
        /// </summary>
        public void Reserve(string id) => _occurrences.TryAdd(id, 0);

        public string Unique(string slug)
        {
            if (slug.Length == 0)
            {
                slug = Fallback;
            }

            var result = slug;

            while (_occurrences.ContainsKey(result))
            {
                var count = _occurrences[slug] + 1;
                _occurrences[slug] = count;
                result = slug + "-" + count.ToString(CultureInfo.InvariantCulture);
            }

            _occurrences[result] = 0;
            return result;
        }
    }
}
