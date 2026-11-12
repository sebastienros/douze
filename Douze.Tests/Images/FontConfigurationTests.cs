using System.Runtime.InteropServices;
using System.Xml.Linq;
using Douze.Images;
using Xunit;

namespace Douze.Tests.Images;

public class FontConfigurationTests
{
    [Fact]
    public void ConfigurationListsTheMacOSFontFoldersAndItsCache()
    {
        var document = XDocument.Parse(FontConfiguration.Configuration("/tmp/a & b/cache"));

        Assert.Equal("fontconfig", document.Root!.Name.LocalName);
        Assert.Equal(["/System/Library/Fonts", "/Library/Fonts", "~/Library/Fonts"], document.Root.Elements("dir").Select(x => x.Value));
        Assert.Equal("/tmp/a & b/cache", document.Root.Element("cachedir")!.Value);
    }

    [Fact]
    public void FontconfigIsConfiguredOnMacOSWithoutAConfiguration()
    {
        FontConfiguration.EnsureConfigured();

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Fontconfig reads the native environment: on macOS without a configuration of the user's, FONTCONFIG_FILE is
        // set there (the managed copy of the environment keeps its startup values).
        var file = Marshal.PtrToStringUTF8(GetEnvironmentVariable("FONTCONFIG_FILE"));

        if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("FONTCONFIG_FILE") == null
            && Environment.GetEnvironmentVariable("FONTCONFIG_PATH") == null && !File.Exists(FontConfiguration.DefaultFile))
        {
            Assert.NotNull(file);
            Assert.Contains("<dir>/System/Library/Fonts</dir>", File.ReadAllText(file));
        }
        else
        {
            Assert.Equal(Environment.GetEnvironmentVariable("FONTCONFIG_FILE"), file);
        }
    }

    [DllImport("libc", EntryPoint = "getenv")]
    private static extern IntPtr GetEnvironmentVariable(string name);
}
