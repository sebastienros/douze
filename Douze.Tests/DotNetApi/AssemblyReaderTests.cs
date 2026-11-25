using Douze.DotNetApi;
using Xunit;

namespace Douze.Tests.DotNetApi;

public sealed class AssemblyReaderTests : DotNetApiTestBase
{
    /// <summary>
    /// Corrupts bytes of the sample assembly at random (with a fixed seed): every assembly that cannot be read fails
    /// with an error that names it, whatever System.Reflection.Metadata throws (an <see cref="OverflowException"/>, a
    /// <see cref="NullReferenceException"/>).
    /// </summary>
    [Fact]
    public void ACorruptAssemblyIsAnErrorThatNamesIt()
    {
        var original = File.ReadAllBytes(SampleDll);
        var random = new Random(42);
        var failures = 0;

        for (var i = 0; i < 20_000; i++)
        {
            var bytes = (byte[])original.Clone();

            for (var j = random.Next(1, 8); j > 0; j--)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            try
            {
                AssemblyReader.Read(bytes, "/lib/corrupt.dll", includeProtected: true);
            }
            catch (DouzeException e)
            {
                Assert.StartsWith("'/lib/corrupt.dll' ", e.Message, StringComparison.Ordinal);
                failures++;
            }
        }

        Assert.True(failures > 0);
    }
}
