using System.Text.Json;
using Douze.Highlighting;
using Xunit;

namespace Douze.Tests.Highlighting;

/// <summary>
/// The tokens of the fixture snippets (Fixtures/highlight/snippets.json), in four themes, against those of Shiki 4.5.0
/// (<c>codeToTokensBase</c>) with the grammars and themes Douze bundles (shiki-tokens.json): the same color and font
/// style for every character.
/// </summary>
public class CodeTokenizerTests
{
    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "highlight");

    public static TheoryData<string> Languages()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "snippets.json")));
        return [.. document.RootElement.EnumerateArray().Select(x => x.GetProperty("lang").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TokensAreShikis(string language)
    {
        using var snippets = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "snippets.json")));
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "shiki-tokens.json")));

        var index = snippets.RootElement.EnumerateArray().ToList().FindIndex(x => x.GetProperty("lang").GetString() == language);
        var code = snippets.RootElement[index].GetProperty("code").GetString()!;
        var themeNames = expected.RootElement.GetProperty("themes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var themes = themeNames.Select(x => CodeTheme.Load(x, TextMateBundle.Default.GetTheme(x)!, 0)).ToArray();
        var tokenizer = new CodeTokenizer(TextMateBundle.Default.Grammars, themes);
        var lines = code.Split('\n');
        var actual = tokenizer.Tokenize(code, language);

        for (var t = 0; t < themes.Length; t++)
        {
            var expectedLines = expected.RootElement.GetProperty("snippets")[index].GetProperty("tokens").GetProperty(themeNames[t]);

            for (var l = 0; l < lines.Length; l++)
            {
                var expectedStyles = new List<string>();

                foreach (var token in expectedLines[l].EnumerateArray())
                {
                    var color = token[1].GetString() is { Length: > 0 } c ? c : themes[t].Foreground;
                    expectedStyles.AddRange(Enumerable.Repeat($"{color} {token[2].GetInt32()}", token[0].GetString()!.Length));
                }

                var actualStyles = new string[lines[l].Length];

                foreach (var token in actual[l])
                {
                    for (var c = token.Start; c < token.End; c++)
                    {
                        actualStyles[c] = $"{token.Styles[t].Color} {token.Styles[t].FontStyle}";
                    }
                }

                for (var c = 0; c < lines[l].Length; c++)
                {
                    Assert.True(expectedStyles[c] == actualStyles[c], $"{themeNames[t]}, line {l + 1}, column {c + 1} ('{lines[l][c]}' in '{lines[l]}'): expected {expectedStyles[c]}, got {actualStyles[c]}");
                }
            }
        }
    }

    [Fact]
    public void PlainTextIsTheForeground()
    {
        var theme = CodeTheme.Load("github-dark", TextMateBundle.Default.GetTheme("github-dark")!, 0);
        var tokens = new CodeTokenizer(TextMateBundle.Default.Grammars, [theme]).Tokenize("a\n\nb c", "text");

        Assert.Equal([1, 0, 1], tokens.Select(x => x.Count));
        Assert.Equal(new Token(0, 3, [new TokenStyle(theme.Foreground, 0)]), tokens[2][0], TokenComparer.Instance);
    }

    [Fact]
    public void LanguagesByNameAndAlias()
    {
        var tokenizer = new CodeTokenizer(TextMateBundle.Default.Grammars, []);

        Assert.Equal("csharp", tokenizer.Find("cs")?.Name);
        Assert.Equal("shellscript", tokenizer.Find("bash")?.Name);
        Assert.Null(tokenizer.Find("not-a-language"));
        Assert.True(CodeTokenizer.IsPlain("plaintext"));
        Assert.True(CodeTokenizer.IsPlain(""));
    }

    [Fact]
    public void GrammarsTextMateSharpCannotReadAsTheyAre()
    {
        // The XML grammar has captures that are not rules, and a rule with a begin and no end.
        var theme = CodeTheme.Load("github-light", TextMateBundle.Default.GetTheme("github-light")!, 0);
        var tokens = new CodeTokenizer(TextMateBundle.Default.Grammars, [theme]).Tokenize("<a b=\"c\"><!-- d --></a>", "xml");

        Assert.True(tokens[0].Count > 3);
    }

    private sealed class TokenComparer : IEqualityComparer<Token>
    {
        public static readonly TokenComparer Instance = new();

        public bool Equals(Token x, Token y) => x.Start == y.Start && x.End == y.End && x.Styles.SequenceEqual(y.Styles);

        public int GetHashCode(Token obj) => obj.Start;
    }
}
