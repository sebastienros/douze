using System.Runtime.InteropServices;
using System.Text;

namespace Douze.Images;

/// <summary>
/// The Fontconfig configuration of the text that libvips lays out (Pango). The libvips that douze ships on macOS reads
/// its configuration from <c>/usr/local/etc/fonts</c>, which only an Intel Homebrew install creates: without it, Pango
/// prints "Fontconfig error: Cannot load default config file" on the first text and finds no system font by family
/// name. When neither <c>FONTCONFIG_FILE</c> nor <c>FONTCONFIG_PATH</c> is set and that file does not exist, douze
/// writes a configuration of the macOS font folders and points Fontconfig to it, before libvips lays out any text.
/// </summary>
internal static partial class FontConfiguration
{
    /// <summary>
    /// The configuration the shipped libvips reads on macOS (its Fontconfig's sysconfdir).
    /// </summary>
    internal const string DefaultFile = "/usr/local/etc/fonts/fonts.conf";

    /// <summary>
    /// Points Fontconfig to a configuration of the system's fonts when it would find none. Does nothing on other
    /// platforms, or when the user configured Fontconfig.
    /// </summary>
    public static void EnsureConfigured()
    {
        if (!OperatingSystem.IsMacOS()
            || Environment.GetEnvironmentVariable("FONTCONFIG_FILE") != null
            || Environment.GetEnvironmentVariable("FONTCONFIG_PATH") != null
            || File.Exists(DefaultFile))
        {
            return;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "douze-fontconfig");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "fonts.conf");
            var content = Configuration(Path.Combine(folder, "cache"));

            if (!File.Exists(file) || File.ReadAllText(file) != content)
            {
                // Written next to it then moved, so that a concurrent douze never reads half a file.
                var temporary = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temporary, content);
                File.Move(temporary, file, overwrite: true);
            }

            // Fontconfig reads the native environment, which Environment.SetEnvironmentVariable does not change on Unix.
            SetEnvironmentVariable("FONTCONFIG_FILE", file, 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pango then prints its error and uses its built-in configuration, as without this.
        }
    }

    /// <summary>
    /// A Fontconfig configuration of the macOS font folders, with its cache in <paramref name="cacheFolder"/>.
    /// </summary>
    internal static string Configuration(string cacheFolder)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\"?>\n<!DOCTYPE fontconfig SYSTEM \"urn:fontconfig:fonts.dtd\">\n<fontconfig>\n");
        builder.Append("  <dir>/System/Library/Fonts</dir>\n");
        builder.Append("  <dir>/Library/Fonts</dir>\n");
        builder.Append("  <dir>~/Library/Fonts</dir>\n");
        builder.Append("  <cachedir>").Append(System.Security.SecurityElement.Escape(cacheFolder)).Append("</cachedir>\n");
        builder.Append("</fontconfig>\n");
        return builder.ToString();
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnvironmentVariable(string name, string value, int overwrite);
}
