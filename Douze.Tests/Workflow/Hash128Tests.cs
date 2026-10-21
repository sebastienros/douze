using System.Text;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow;

public class Hash128Tests
{
    [Fact]
    public void OfMatchesKnownVector()
    {
        // XXH3 128 bits, seed 0, canonical (big endian) representation, as printed by `xxhsum -H2`
        // and by Python's `xxhash.xxh3_128_hexdigest(b"abc")`.
        Assert.Equal("06b05ab6733a618578af5f94892f3950", Hash128.Of("abc"u8).ToString());
        Assert.Equal("99aa06d3014798d86001c324468d497f", Hash128.Empty.ToString());
    }

    [Fact]
    public void CombineIsOrderSensitive()
    {
        var a = Hash128.OfText("a");
        var b = Hash128.OfText("b");

        Assert.NotEqual(Hash128.Combine(a, b), Hash128.Combine(b, a));
        Assert.Equal(Hash128.Combine(a, b), Hash128.Combine(a, b));
    }

    [Fact]
    public void OfTextEqualsOfUtf8Bytes()
    {
        var text = "Hello, 世界 " + new string('x', 40_000) + " 👋";

        Assert.Equal(Hash128.Of(Encoding.UTF8.GetBytes(text)), Hash128.OfText(text));
        Assert.Equal(Hash128.Empty, Hash128.OfText(""));
    }

    [Fact]
    public void ToStringAndParseRoundTrip()
    {
        var hash = Hash128.OfText("round trip");
        var hex = hash.ToString();

        Assert.Equal(32, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
        Assert.Equal(hash, Hash128.Parse(hex));
    }
}
