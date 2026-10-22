using System.Text;

namespace Douze.Liquid;

/// <summary>
/// The fenced code blocks of a Markdown text, replaced by placeholders before the text is parsed as a template, so
/// that <c>{{</c> and <c>{%</c> in code samples are not Liquid. A placeholder is <c>\u001F&lt;n&gt;\u001F</c>
/// followed by as many newlines as the block has, so that positions reported by the template parser stay on the
/// original lines. <see cref="Restore"/> puts the blocks back in what the template produced.
/// </summary>
/// <remarks>
/// A fence is a line of three or more backticks or tildes (a backtick fence's info string has no backtick), after any
/// indentation and <c>&gt;</c> prefixes, so that fences in list items and block quotes are found too; it ends at the
/// next line of at least as many of the same character followed by nothing but spaces, or at the end of the text.
/// </remarks>
internal sealed class FenceTable
{
    public const char Marker = '\u001F';

    private readonly List<(string Text, int NewLines)> _blocks = [];

    public int Count => _blocks.Count;

    /// <summary>
    /// Returns <paramref name="text"/> with its fenced code blocks replaced by placeholders, and the table of the
    /// blocks, or the text itself and <c>null</c> when it has none.
    /// </summary>
    public static string Protect(string text, out FenceTable? table)
    {
        table = null;

        if (text.AsSpan().IndexOfAny('`', '~') < 0)
        {
            return text;
        }

        StringBuilder? builder = null;
        var copied = 0;
        var position = 0;

        while (position < text.Length)
        {
            var lineEnd = LineEnd(text, position);

            if (!TryOpen(text.AsSpan(position, lineEnd - position), out var fence, out var length))
            {
                position = lineEnd + 1;
                continue;
            }

            // Without a closing fence, the block runs to the end of the text.
            var blockEnd = text.Length;

            for (var scan = lineEnd + 1; scan < text.Length;)
            {
                var end = LineEnd(text, scan);

                if (IsClose(text.AsSpan(scan, end - scan), fence, length))
                {
                    blockEnd = end;
                    break;
                }

                scan = end + 1;
            }

            if (blockEnd == text.Length && text.EndsWith('\n'))
            {
                blockEnd--;
            }

            var block = text[position..blockEnd];
            var newLines = block.AsSpan().Count('\n');

            table ??= new FenceTable();
            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, position - copied);
            builder.Append(Marker).Append(table._blocks.Count).Append(Marker).Append('\n', newLines);
            table._blocks.Add((block, newLines));

            copied = blockEnd;
            position = blockEnd + 1;
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// Puts the blocks back in place of their placeholders, each with the newlines that follow it.
    /// </summary>
    public string Restore(string text)
    {
        if (text.IndexOf(Marker) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (true)
        {
            var start = text.IndexOf(Marker, index);

            if (start < 0)
            {
                break;
            }

            var digits = start + 1;

            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            {
                digits++;
            }

            if (digits == start + 1 || digits >= text.Length || text[digits] != Marker
                || !int.TryParse(text.AsSpan(start + 1, digits - start - 1), out var number) || number >= _blocks.Count)
            {
                builder.Append(text, index, start + 1 - index);
                index = start + 1;
                continue;
            }

            var (block, newLines) = _blocks[number];
            builder.Append(text, index, start - index).Append(block);

            index = digits + 1;

            while (newLines > 0 && index < text.Length && text[index] == '\n')
            {
                index++;
                newLines--;
            }
        }

        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    private static int LineEnd(string text, int start)
    {
        var end = text.IndexOf('\n', start);
        return end < 0 ? text.Length : end;
    }

    private static ReadOnlySpan<char> SkipPrefix(ReadOnlySpan<char> line) => line.TrimStart(" \t>");

    private static bool TryOpen(ReadOnlySpan<char> line, out char fence, out int length)
    {
        line = SkipPrefix(line);
        fence = line.IsEmpty ? '\0' : line[0];
        length = 0;

        if (fence is not ('`' or '~'))
        {
            return false;
        }

        while (length < line.Length && line[length] == fence)
        {
            length++;
        }

        return length >= 3 && (fence == '~' || !line[length..].Contains('`'));
    }

    private static bool IsClose(ReadOnlySpan<char> line, char fence, int length)
    {
        line = SkipPrefix(line);
        var count = 0;

        while (count < line.Length && line[count] == fence)
        {
            count++;
        }

        return count >= length && line[count..].Trim(" \t\r").IsEmpty;
    }
}
