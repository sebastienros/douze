using Douze.DotNetApi;
using Xunit;

namespace Douze.Tests.DotNetApi;

public sealed class DocSetTests
{
    [Theory]
    // The documentation of .NET separates the type arguments of an explicit implementation's name with @.
    [InlineData("M:System.Int32.System#IEquatable{System#Int32@System#Int32}#Equals(System.Int32)", "M:System.Int32.System#IEquatable{System#Int32,System#Int32}#Equals(System.Int32)")]
    // It writes some in <>.
    [InlineData("M:System.Byte.System#Numerics#IBinaryInteger<System#Byte>#GetShortestBitLength", "M:System.Byte.System#Numerics#IBinaryInteger{System#Byte}#GetShortestBitLength")]
    // The compiler keeps nint and nuint, the documentation of .NET writes their types.
    [InlineData("P:System.IntPtr.System#Numerics#INumberBase{nint}#One", "P:System.IntPtr.System#Numerics#INumberBase{System#IntPtr}#One")]
    [InlineData("M:N.C.N#IConverter{nuint,nint}#Convert(System.UIntPtr)", "M:N.C.N#IConverter{System#UIntPtr,System#IntPtr}#Convert(System.UIntPtr)")]
    [InlineData("P:System.IntPtr.System#Numerics#INumberBase{System#IntPtr}#One", "P:System.IntPtr.System#Numerics#INumberBase{System#IntPtr}#One")]
    // Only type arguments of the name, never the parameters: a by-reference parameter keeps its @.
    [InlineData("M:N.C.Mint{nintx}(System.Int32@)", "M:N.C.Mint{nintx}(System.Int32@)")]
    [InlineData("M:N.C.TryRead(System.Int32@)", "M:N.C.TryRead(System.Int32@)")]
    [InlineData("T:N.C`1", "T:N.C`1")]
    public void Normalize(string id, string normalized)
    {
        Assert.Equal(normalized, DocSet.Normalize(id));
    }
}
