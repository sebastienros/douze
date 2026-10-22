using System.Collections.Frozen;
using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;

namespace Douze.Liquid.Tags;

internal enum ShortcodeDisplay { Block, Inline }

/// <summary>
/// A shortcode: a Liquid file of the shortcodes folder that is a tag of every template.
/// </summary>
/// <param name="Name">The tag name: the file name without its extension.</param>
/// <param name="Path">The shortcode file.</param>
/// <param name="Block">Whether the tag is paired (<c>{% tabs %}...{% endtabs %}</c>).</param>
/// <param name="Display">Whether the output is block HTML, written as a raw region in a Markdown page.</param>
/// <param name="ArgNames">The names of the positional arguments, from the front matter's <c>args</c>.</param>
/// <param name="Template">The parsed template, without the front matter.</param>
/// <param name="ContentHash">The hash of the file, recorded as a template dependency by every render.</param>
/// <param name="LineOffset">The lines of the front matter: added to the positions in the template.</param>
internal sealed record ShortcodeDefinition(string Name, FPath Path, bool Block, ShortcodeDisplay Display, ImmutableArray<string> ArgNames, ParsedTemplate Template, Hash128 ContentHash, int LineOffset);

/// <summary>
/// The shortcodes of a site (<c>site.shortcodes</c>, default <c>&lt;includes&gt;/shortcodes/</c>). <see cref="ScanAsync"/>
/// reads the front matter of every file, registers every name on the parser, then parses the templates (a shortcode
/// can use another one), so it runs before any other template of the build is parsed.
/// </summary>
internal sealed class ShortcodeRegistry
{
    public static readonly ImmutableArray<string> FrontMatterKeys = ["block", "display", "args"];

    // Names that are part of other tags' syntax.
    private static readonly FrozenSet<string> ReservedNames = FrozenSet.Create(StringComparer.Ordinal, "else", "elsif", "when", "empty", "blank", "nil", "true", "false");

    private FrozenDictionary<string, ShortcodeDefinition> _byName = FrozenDictionary<string, ShortcodeDefinition>.Empty;

    private ShortcodeRegistry(FPath folder)
    {
        Folder = folder;
    }

    public static ShortcodeRegistry Empty { get; } = new(FPath.Empty);

    /// <summary>
    /// The folder the shortcodes were read from.
    /// </summary>
    public FPath Folder { get; }

    /// <summary>
    /// Every shortcode, sorted by name.
    /// </summary>
    public IReadOnlyList<ShortcodeDefinition> Shortcodes { get; private set; } = [];

    /// <summary>
    /// The combined hash of the shortcode files (names and contents): what a rescan compares (watch mode). Renders do
    /// not depend on it: each records the file of the shortcode it used.
    /// </summary>
    public Hash128 Hash { get; private set; }

    public ShortcodeDefinition? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Reads the shortcodes of <paramref name="folder"/> (its <c>.liquid</c> and <c>.html</c> files, not its
    /// subfolders) and registers them on <paramref name="parser"/>.
    /// </summary>
    /// <exception cref="WorkflowValidationException">A file name, a front matter or a template is invalid, or a name
    /// hides a built-in tag; every error is reported with its file.</exception>
    public static async Task<ShortcodeRegistry> ScanAsync(IFileSystem source, FPath folder, DouzeParser parser, CancellationToken ct)
    {
        var registry = new ShortcodeRegistry(folder);

        if (folder.IsEmpty || !(await source.GetDirectoryEntryAsync(folder)).Exists)
        {
            return registry;
        }

        var files = (await source.EnumerateFileSystemEntriesAsync(folder, recursive: false, filter: null))
            .Where(x => !x.IsDirectory && x.Path.GetExtensionWithDot() is ".liquid" or ".html" && IsDirectChild(folder, x.Path))
            .Select(x => x.Path)
            .Order()
            .ToList();

        var errors = new List<WorkflowValidationError>();
        var builtIns = parser.RegisteredTags.Keys.ToFrozenSet(StringComparer.Ordinal);
        var pending = new List<(string Name, FPath Path, bool Block, ShortcodeDisplay Display, ImmutableArray<string> ArgNames, string Body, int LineOffset, Hash128 Hash)>();

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(Path.GetFileName(path.FullName));
            var location = new SourceLocation(path.FullName, 1, 1);

            if (!IsTagName(name))
            {
                errors.Add(new($"'{Path.GetFileName(path.FullName)}' is not a shortcode name: use lowercase letters, digits and underscores", location));
                continue;
            }

            if (builtIns.Contains(name) || ReservedNames.Contains(name) || (name.StartsWith("end", StringComparison.Ordinal) && builtIns.Contains(name[3..])))
            {
                errors.Add(new($"shortcode '{name}' hides a built-in tag; rename the file", location));
                continue;
            }

            if (pending.Any(x => x.Name == name))
            {
                errors.Add(new($"shortcode '{name}' is defined twice", location));
                continue;
            }

            byte[] bytes;

            await using (var stream = await source.OpenFileAsync(path))
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory, ct);
                bytes = memory.ToArray();
            }

            var text = await FileContent.FromBytes(bytes, isText: true).ReadTextAsync(ct);

            NodeObject values;
            string body;

            try
            {
                (values, body) = FrontMatter.Parse(text);
            }
            catch (Exception e) when (e is DouzeException or SharpYaml.YamlException)
            {
                errors.Add(new($"invalid front matter: {e.Message}", location));
                continue;
            }

            if (!TryReadFrontMatter(name, values, location, errors, out var block, out var display, out var argNames))
            {
                continue;
            }

            var lineOffset = text.AsSpan(0, text.Length - body.Length).Count('\n');
            pending.Add((name, path, block, display, argNames, body, lineOffset, Hash128.Of(bytes)));
        }

        // Every name is a tag before any template is parsed: a shortcode can use another one. Paired names first, so
        // that 'end<name>' of each is known when a check needs it.
        foreach (var shortcode in pending)
        {
            var name = shortcode.Name;

            if (shortcode.Block)
            {
                parser.RegisterParserBlock(name, parser.TagArguments, (call, body, output, encoder, context) =>
                    ShortcodeTag.RenderBlockAsync(registry.Find(name)!, call, body, output, encoder, context));
            }
            else
            {
                parser.RegisterParserTag(name, parser.TagArguments, (call, output, encoder, context) =>
                    ShortcodeTag.RenderTagAsync(registry.Find(name)!, call, output, encoder, context));
            }
        }

        var loader = new TemplateLoader(source, [], parser);
        var definitions = new List<ShortcodeDefinition>();

        foreach (var shortcode in pending)
        {
            try
            {
                var template = loader.GetOrParse(Hash128.OfText(shortcode.Body), shortcode.Body, shortcode.Path.FullName);
                definitions.Add(new ShortcodeDefinition(shortcode.Name, shortcode.Path, shortcode.Block, shortcode.Display, shortcode.ArgNames, template, shortcode.Hash, shortcode.LineOffset));
            }
            catch (TemplateParseException e)
            {
                var location = new SourceLocation(shortcode.Path.FullName, (e.Line ?? 1) + shortcode.LineOffset, e.Column ?? 1);
                errors.Add(new($"{e.Reason}{SuggestTag(e.Reason, pending.Select(x => x.Name))}", location));
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }

        registry.Shortcodes = definitions;
        registry._byName = definitions.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);
        registry.Hash = Hash128.OfStrings(definitions.Select(x => $"{x.Name}:{x.ContentHash}"));

        return registry;
    }

    /// <summary>
    /// <c>; did you mean 'tabs'?</c> when <paramref name="reason"/> is Fluid's unknown tag error and a name is close to
    /// the unknown one.
    /// </summary>
    public static string SuggestTag(string reason, IEnumerable<string> names)
    {
        const string prefix = "Unknown tag '";

        if (!reason.StartsWith(prefix, StringComparison.Ordinal) || reason.IndexOf('\'', prefix.Length) is var end && end < 0)
        {
            return "";
        }

        return WorkflowValidationException.Suggest(reason[prefix.Length..end], names);
    }

    private static bool TryReadFrontMatter(string name, NodeObject values, SourceLocation location, List<WorkflowValidationError> errors, out bool block, out ShortcodeDisplay display, out ImmutableArray<string> argNames)
    {
        var valid = true;
        block = false;
        display = ShortcodeDisplay.Inline;
        argNames = [];
        string? displayText = null;

        foreach (var property in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            switch (property.Key)
            {
                case "block" when property.Value is NodeValue<bool> b:
                    block = b.Value;
                    break;

                case "display" when property.Value is NodeValue<string> { Value: "block" or "inline" } s:
                    displayText = s.Value;
                    break;

                case "args" when property.Value is NodeArray array && array.All(x => x is NodeValue<string> { Value: var v } && IsTagName(v)):
                    argNames = [.. array.Select(x => ((NodeValue<string>)x!).Value)];
                    break;

                case "block":
                    errors.Add(new($"'block' of shortcode '{name}' must be true or false", location));
                    valid = false;
                    break;

                case "display":
                    errors.Add(new($"'display' of shortcode '{name}' must be 'block' or 'inline'", location));
                    valid = false;
                    break;

                case "args":
                    errors.Add(new($"'args' of shortcode '{name}' must be a list of names (lowercase letters, digits and underscores)", location));
                    valid = false;
                    break;

                default:
                    errors.Add(new($"unknown key '{property.Key}' in the front matter of shortcode '{name}'; expected {Workflow.Yaml.WorkflowSchema.Expected(FrontMatterKeys)}", location));
                    valid = false;
                    break;
            }
        }

        // Paired shortcodes are blocks unless they say otherwise; single tags are inline.
        display = displayText switch
        {
            "block" => ShortcodeDisplay.Block,
            "inline" => ShortcodeDisplay.Inline,
            _ => block ? ShortcodeDisplay.Block : ShortcodeDisplay.Inline,
        };

        return valid;
    }

    private static bool IsDirectChild(FPath folder, FPath path)
    {
        var name = path.FullName;
        return name.StartsWith(folder.FullName, StringComparison.Ordinal) && name.IndexOf('/', folder.FullName.Length) < 0;
    }

    private static bool IsTagName(string name)
    {
        return name.Length > 0 && name[0] is (>= 'a' and <= 'z') or '_' && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
    }
}
