namespace Douze.CodeHover;

/// <summary>
/// Converts between offsets and (line, character) positions as twoslash's <c>createPositionConverter</c> does: a line
/// includes its line break, and an offset at or past the end of the last line is on the line after it.
/// </summary>
internal sealed class PositionConverter
{
    private readonly int[] _lineLengths;

    public PositionConverter(string code)
    {
        var lengths = new List<int>();
        var start = 0;

        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '\n')
            {
                lengths.Add(i + 1 - start);
                start = i + 1;
            }
        }

        lengths.Add(code.Length - start);
        _lineLengths = [.. lengths];
    }

    public (int Line, int Character) ToPosition(int index)
    {
        var character = index;
        var line = 0;

        foreach (var length in _lineLengths)
        {
            if (character < length)
            {
                break;
            }

            character -= length;
            line++;
        }

        return (line, character);
    }

    public int ToIndex(int line, int character)
    {
        var index = 0;

        for (var i = 0; i < line && i < _lineLengths.Length; i++)
        {
            index += _lineLengths[i];
        }

        return index + character;
    }
}
