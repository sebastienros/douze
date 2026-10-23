using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Douze.Markdown;
using Douze.FileSystems;
using Douze.Workflow.Steps;
using SharpYaml.Model;

namespace Douze.Workflow.Yaml;

/// <summary>
/// Reads a workflow file (<c>douze.yml</c>) and checks its shape (section 4.5 of the workflow engine plan). The
/// semantic rules, such as unknown needs or cycles, are checked by <see cref="WorkflowGraph.Build"/>.
/// </summary>
internal static partial class WorkflowLoader
{
    /// <param name="path">The path of the file on disk.</param>
    /// <param name="fileName">The name used in error locations, such as <c>douze.yml</c> or <c>site/douze.yml</c>.</param>
    /// <param name="environment">What the <c>enabled</c> expressions see (default: this process, not watching).</param>
    /// <param name="ct">Cancels the read.</param>
    /// <exception cref="WorkflowValidationException">Every error of the file.</exception>
    public static async Task<WorkflowFile> LoadAsync(string path, string fileName, WorkflowEnvironment? environment = null, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        return Load(text, fileName, System.IO.Path.GetFullPath(path), environment);
    }

    /// <param name="text">The YAML text.</param>
    /// <param name="fileName">The name used in error locations.</param>
    /// <param name="sourceFile">The <see cref="WorkflowDefinition.SourceFile"/> (default: <paramref name="fileName"/>).</param>
    /// <param name="environment">What the <c>enabled</c> expressions see (default: this process, not watching).</param>
    /// <exception cref="WorkflowValidationException">Every error of the file.</exception>
    public static WorkflowFile Load(string text, string fileName, string? sourceFile = null, WorkflowEnvironment? environment = null)
    {
        environment ??= WorkflowEnvironment.Current(watch: false);
        var yaml = YamlSource.Parse(text, fileName);
        var errors = new List<WorkflowValidationError>();
        var site = SiteSettings.None;
        MarkdownOptions? markdown = null;
        WorkflowDefinition? workflow = null;

        if (YamlSource.IsNull(yaml.Root))
        {
            return new WorkflowFile(site, null, null);
        }

        if (yaml.Root is not YamlMapping root)
        {
            throw new WorkflowValidationException("a workflow file must be a mapping with 'site', 'markdown' and 'steps'", yaml.Location(yaml.Root!));
        }

        foreach (var (key, keyNode, value) in yaml.Entries(root, errors))
        {
            switch (key)
            {
                case "site":
                    site = ReadSite(yaml, value, errors);
                    break;

                case "markdown":
                    markdown = ReadMarkdown(yaml, value, errors);
                    break;

                case "steps":
                    workflow = ReadSteps(yaml, value, environment, errors) is { } steps ? new WorkflowDefinition(steps, sourceFile ?? fileName) : null;
                    break;

                default:
                    errors.Add(new($"unknown key '{key}'; expected {WorkflowSchema.Expected(WorkflowSchema.TopLevelKeys)}", yaml.Location(keyNode)));
                    break;
            }
        }

        if (errors.Count > 0)
        {
            // In the order of the file; the rules that involve several fields of a step run after reading them.
            throw new WorkflowValidationException([.. errors.OrderBy(x => x.Location?.Line).ThenBy(x => x.Location?.Column)]);
        }

        return new WorkflowFile(site, markdown, workflow);
    }

    private static SiteSettings ReadSite(YamlSource yaml, YamlElement? node, List<WorkflowValidationError> errors)
    {
        var site = SiteSettings.None;

        if (YamlSource.IsNull(node))
        {
            return site;
        }

        if (node is not YamlMapping mapping)
        {
            errors.Add(new("'site' must be a mapping", yaml.Location(node!)));
            return site;
        }

        foreach (var (key, keyNode, value) in yaml.Entries(mapping, errors))
        {
            if (!WorkflowSchema.SiteKeys.Contains(key))
            {
                errors.Add(new($"unknown key '{key}' in 'site'; expected {WorkflowSchema.Expected(WorkflowSchema.SiteKeys)}", yaml.Location(keyNode)));
                continue;
            }

            if (YamlSource.IsNull(value))
            {
                continue;
            }

            if (key == "locales")
            {
                site = site with { Locales = ReadLocales(yaml, value!, errors) };
                continue;
            }

            if (YamlSource.Scalar(value!) is not { } text)
            {
                errors.Add(new($"'{key}' must be {(key == "parallelism" ? "a positive integer" : "a string")}", yaml.Location(value!)));
                continue;
            }

            switch (key)
            {
                case "content" or "data" or "includes" or "shortcodes":
                    if (text.Split('/', '\\').Contains(".."))
                    {
                        errors.Add(new($"'{key}' must be a folder inside the source folder", yaml.Location(value!)));
                        break;
                    }

                    var folder = StepPaths.Directory(text);
                    site = key switch
                    {
                        "content" => site with { ContentFolder = folder },
                        "data" => site with { DataFolder = folder },
                        "shortcodes" => site with { ShortcodesFolder = folder },
                        _ => site with { IncludesFolder = folder },
                    };
                    break;

                case "output":
                    site = site with { Output = text };
                    break;

                case "url":
                    site = site with { Url = text };
                    break;

                case "parallelism":
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parallelism) || parallelism < 1)
                    {
                        errors.Add(new("'parallelism' must be a positive integer", yaml.Location(value!)));
                        break;
                    }

                    site = site with { Parallelism = parallelism };
                    break;
            }
        }

        return site;
    }

    /// <summary>
    /// Reads the <c>markdown</c> section. <c>markdown:</c> without a value is the same as no section; a section without
    /// <c>extensions</c> has <see cref="MarkdownOptions.DefaultExtensions"/>.
    /// </summary>
    private static MarkdownOptions? ReadMarkdown(YamlSource yaml, YamlElement? node, List<WorkflowValidationError> errors)
    {
        if (YamlSource.IsNull(node))
        {
            return null;
        }

        var options = new MarkdownOptions();

        foreach (var (key, _, value) in Section(yaml, node!, "markdown", WorkflowSchema.MarkdownKeys, errors))
        {
            switch (key)
            {
                case "extensions":
                    if (ReadNames(yaml, value, "extensions", errors) is not { } extensions)
                    {
                        break;
                    }

                    var valid = true;

                    foreach (var (name, nameNode) in extensions)
                    {
                        if (MarkdownPipelineFactory.Validate(name) is { } error)
                        {
                            errors.Add(new(error, yaml.Location(nameNode)));
                            valid = false;
                        }
                    }

                    if (valid)
                    {
                        options = options with { Extensions = [.. extensions.Select(x => x.Name)] };
                    }

                    break;

                case "aside":
                    options = options with { Aside = ReadAside(yaml, value, errors) };
                    break;

                case "links":
                    if (ReadBool(yaml, value, "links", errors) is { } links)
                    {
                        options = options with { RewriteLinks = links };
                    }

                    break;

                case "code":
                    foreach (var (codeKey, _, codeValue) in Section(yaml, value, "markdown.code", WorkflowSchema.CodeKeys, errors))
                    {
                        if (codeKey == "class-prefix")
                        {
                            if (ReadString(yaml, codeValue, "class-prefix", errors) is { } prefix)
                            {
                                options = options with { CodeClassPrefix = prefix };
                            }
                        }
                        else if (ReadNames(yaml, codeValue, "passthrough", errors) is { } passthrough)
                        {
                            options = options with { PassthroughLanguages = [.. passthrough.Select(x => x.Name)] };
                        }
                    }

                    break;

                case "images":
                    options = options with { Images = ReadImages(yaml, value, errors) };
                    break;
            }
        }

        return options;
    }

    private static AsideOptions ReadAside(YamlSource yaml, YamlElement node, List<WorkflowValidationError> errors)
    {
        var aside = AsideOptions.Default;
        var named = new List<(string Kind, YamlElement KeyNode)>();

        foreach (var (key, _, value) in Section(yaml, node, "markdown.aside", WorkflowSchema.AsideKeys, errors))
        {
            switch (key)
            {
                case "tag" or "class":
                    if (ReadString(yaml, value, key, errors) is not { } text)
                    {
                        break;
                    }

                    if (!ElementName().IsMatch(text))
                    {
                        errors.Add(new($"'{key}' must be {(key == "tag" ? "an HTML element name" : "a class name")}", yaml.Location(value)));
                        break;
                    }

                    aside = key == "tag" ? aside with { Tag = text } : aside with { Class = text };
                    break;

                case "kinds":
                    if (ReadNames(yaml, value, "kinds", errors) is { } kinds)
                    {
                        aside = aside with { Kinds = [.. kinds.Select(x => x.Name.ToLowerInvariant()).Distinct()] };
                    }

                    break;

                case "titles" or "icons":
                    if (ReadStringMap(yaml, value, key, errors) is not { } map)
                    {
                        break;
                    }

                    var dictionary = map.ToImmutableDictionary(x => x.Key.ToLowerInvariant(), x => x.Value, StringComparer.Ordinal);
                    named.AddRange(map.Select(x => (x.Key.ToLowerInvariant(), x.KeyNode)));
                    aside = key == "titles" ? aside with { Titles = aside.Titles.SetItems(dictionary) } : aside with { Icons = dictionary };
                    break;

                case "titles-from":
                    aside = aside with { TitlesFrom = ReadString(yaml, value, key, errors) };
                    break;
            }
        }

        if (aside.Kinds.IsEmpty)
        {
            errors.Add(new("'kinds' must list at least one kind", yaml.Location(node)));
            return aside;
        }

        foreach (var (kind, keyNode) in named)
        {
            if (!aside.Kinds.Contains(kind))
            {
                errors.Add(new($"unknown aside kind '{kind}'; expected {WorkflowSchema.Expected(aside.Kinds)}", yaml.Location(keyNode)));
            }
        }

        return aside;
    }

    private static ImageOptions ReadImages(YamlSource yaml, YamlElement node, List<WorkflowValidationError> errors)
    {
        var images = ImageOptions.Default;

        foreach (var (key, _, value) in Section(yaml, node, "markdown.images", WorkflowSchema.ImageKeys, errors))
        {
            switch (key)
            {
                case "widths":
                    if (ReadNames(yaml, value, "widths", errors) is not { } widths)
                    {
                        break;
                    }

                    var parsed = new List<int>();

                    foreach (var (text, item) in widths)
                    {
                        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width < 1)
                        {
                            errors.Add(new("'widths' must be a list of positive integers", yaml.Location(item)));
                            break;
                        }

                        parsed.Add(width);
                    }

                    images = images with { Widths = [.. parsed.Distinct().Order()] };
                    break;

                case "formats":
                    if (ReadNames(yaml, value, "formats", errors) is { } formats)
                    {
                        images = images with { Formats = [.. formats.Select(x => x.Name.ToLowerInvariant())] };
                    }

                    break;

                case "quality":
                    if (ReadString(yaml, value, key, errors) is { } quality)
                    {
                        if (!int.TryParse(quality, NumberStyles.None, CultureInfo.InvariantCulture, out var q) || q is < 1 or > 100)
                        {
                            errors.Add(new("'quality' must be an integer from 1 to 100", yaml.Location(value)));
                            break;
                        }

                        images = images with { Quality = q };
                    }

                    break;

                case "output":
                    images = images with { Output = ReadString(yaml, value, key, errors) };
                    break;

                case "loading":
                    if (ReadString(yaml, value, key, errors) is { } loading)
                    {
                        if (loading is not ("lazy" or "eager"))
                        {
                            errors.Add(new("'loading' must be 'lazy' or 'eager'", yaml.Location(value)));
                            break;
                        }

                        images = images with { Loading = loading };
                    }

                    break;
            }
        }

        return images;
    }

    /// <summary>
    /// The entries of a mapping section whose keys are in <paramref name="keys"/>, with an error for the others and for
    /// a section that is not a mapping. Entries without a value are skipped.
    /// </summary>
    private static IEnumerable<(string Key, YamlElement KeyNode, YamlElement Value)> Section(YamlSource yaml, YamlElement node, string name, ImmutableArray<string> keys, List<WorkflowValidationError> errors)
    {
        if (node is not YamlMapping mapping)
        {
            errors.Add(new($"'{name}' must be a mapping", yaml.Location(node)));
            yield break;
        }

        foreach (var (key, keyNode, value) in yaml.Entries(mapping, errors))
        {
            if (!keys.Contains(key))
            {
                errors.Add(new($"unknown key '{key}' in '{name}'; expected {WorkflowSchema.Expected(keys)}", yaml.Location(keyNode)));
                continue;
            }

            if (!YamlSource.IsNull(value))
            {
                yield return (key, keyNode, value!);
            }
        }
    }

    private static string? ReadString(YamlSource yaml, YamlElement node, string name, List<WorkflowValidationError> errors)
    {
        if (YamlSource.Scalar(node) is { } text)
        {
            return text;
        }

        errors.Add(new($"'{name}' must be a string", yaml.Location(node)));
        return null;
    }

    private static bool? ReadBool(YamlSource yaml, YamlElement node, string name, List<WorkflowValidationError> errors)
    {
        if (YamlSource.Scalar(node) is { } text && bool.TryParse(text, out var value))
        {
            return value;
        }

        errors.Add(new($"'{name}' must be true or false", yaml.Location(node)));
        return null;
    }

    /// <summary>
    /// A string or a list of strings, each with its node.
    /// </summary>
    private static List<(string Name, YamlElement Node)>? ReadNames(YamlSource yaml, YamlElement node, string name, List<WorkflowValidationError> errors)
    {
        if (YamlSource.Scalar(node) is { } single)
        {
            return [(single, node)];
        }

        if (node is YamlSequence sequence)
        {
            var names = new List<(string, YamlElement)>(sequence.Count);

            foreach (var item in sequence)
            {
                if (item is null || YamlSource.Scalar(item) is not { } text)
                {
                    errors.Add(new($"'{name}' must be a string or a list of strings", yaml.Location(item ?? node)));
                    return null;
                }

                names.Add((text, item));
            }

            return names;
        }

        errors.Add(new($"'{name}' must be a string or a list of strings", yaml.Location(node)));
        return null;
    }

    private static List<(string Key, YamlElement KeyNode, string Value)>? ReadStringMap(YamlSource yaml, YamlElement node, string name, List<WorkflowValidationError> errors)
    {
        if (node is not YamlMapping mapping)
        {
            errors.Add(new($"'{name}' must be a mapping of kinds to strings", yaml.Location(node)));
            return null;
        }

        var entries = new List<(string, YamlElement, string)>();

        foreach (var (key, keyNode, value) in yaml.Entries(mapping, errors))
        {
            if (value is null || YamlSource.Scalar(value) is not { } text)
            {
                errors.Add(new($"'{name}' must be a mapping of kinds to strings", yaml.Location(value ?? keyNode)));
                return null;
            }

            entries.Add((key, keyNode, text));
        }

        return entries;
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ElementName();
    private static readonly ImmutableArray<string> LocaleKeys = ["default", "root", "fallback", "languages", "strings"];

    private static readonly ImmutableArray<string> LanguageKeys = ["label", "lang", "dir"];

    /// <summary>
    /// Reads <c>site.locales</c>, or returns null after adding its errors.
    /// </summary>
    private static LocaleSet? ReadLocales(YamlSource yaml, YamlElement node, List<WorkflowValidationError> errors)
    {
        if (node is not YamlMapping mapping)
        {
            errors.Add(new("'locales' must be a mapping", yaml.Location(node)));
            return null;
        }

        var errorCount = errors.Count;
        string? defaultKey = null;
        string? root = null;
        var fallback = false;
        var strings = LocaleSet.DefaultStrings;
        var languages = new List<LanguageSettings>();

        foreach (var (key, keyNode, value) in yaml.Entries(mapping, errors))
        {
            if (!LocaleKeys.Contains(key))
            {
                errors.Add(new($"unknown key '{key}' in 'site.locales'; expected {Expected(LocaleKeys)}", yaml.Location(keyNode)));
                continue;
            }

            if (YamlSource.IsNull(value))
            {
                continue;
            }

            if (key == "languages")
            {
                ReadLanguages(yaml, value!, languages, errors);
                continue;
            }

            if (YamlSource.Scalar(value!) is not { } text)
            {
                errors.Add(new($"'{key}' must be {(key == "fallback" ? "true or false" : "a string")}", yaml.Location(value!)));
                continue;
            }

            switch (key)
            {
                case "default":
                    if (!IsLocaleFolder(text))
                    {
                        errors.Add(new("'default' must be a locale folder name", yaml.Location(value!)));
                        break;
                    }

                    defaultKey = text;
                    break;

                case "root":
                    if (text.Split('/', '\\').Contains(".."))
                    {
                        errors.Add(new("'root' must be a folder inside the content folder", yaml.Location(value!)));
                        break;
                    }

                    root = StepPaths.Directory(text) == FPath.Root ? null : text;
                    break;

                case "fallback":
                    if (!bool.TryParse(text, out fallback))
                    {
                        errors.Add(new("'fallback' must be true or false", yaml.Location(value!)));
                    }

                    break;

                case "strings":
                    strings = text;
                    break;
            }
        }

        if (defaultKey == null && errors.Count == errorCount)
        {
            errors.Add(new("'site.locales' needs 'default', the locale whose pages have no url prefix", yaml.Location(node)));
        }

        if (errors.Count > errorCount)
        {
            return null;
        }

        // The default locale may be listed with the others, for its label, lang and dir; it has no folder.
        var defaultLocale = (languages.FirstOrDefault(x => x.Key == defaultKey) ?? new LanguageSettings(defaultKey!, null, null, null)).ToLocale("");

        return new LocaleSet(
            defaultLocale,
            [.. languages.Where(x => x.Key != defaultKey).Select(x => x.ToLocale("/" + x.Key))],
            root,
            fallback,
            strings);
    }

    private static void ReadLanguages(YamlSource yaml, YamlElement node, List<LanguageSettings> languages, List<WorkflowValidationError> errors)
    {
        if (node is not YamlMapping mapping)
        {
            errors.Add(new("'languages' must be a mapping of locale folders to settings", yaml.Location(node)));
            return;
        }

        foreach (var (folder, folderNode, value) in yaml.Entries(mapping, errors))
        {
            if (!IsLocaleFolder(folder))
            {
                errors.Add(new($"'{folder}' is not a locale folder name", yaml.Location(folderNode)));
                continue;
            }

            string? label = null, lang = null, dir = null;

            if (!YamlSource.IsNull(value))
            {
                if (value is not YamlMapping settings)
                {
                    errors.Add(new($"the settings of locale '{folder}' must be a mapping of 'label', 'lang' and 'dir'", yaml.Location(value!)));
                    continue;
                }

                foreach (var (key, keyNode, setting) in yaml.Entries(settings, errors))
                {
                    if (!LanguageKeys.Contains(key))
                    {
                        errors.Add(new($"unknown key '{key}' in 'site.locales.languages.{folder}'; expected {Expected(LanguageKeys)}", yaml.Location(keyNode)));
                        continue;
                    }

                    if (YamlSource.IsNull(setting))
                    {
                        continue;
                    }

                    if (YamlSource.Scalar(setting!) is not { } text)
                    {
                        errors.Add(new($"'{key}' must be a string", yaml.Location(setting!)));
                        continue;
                    }

                    switch (key)
                    {
                        case "label":
                            label = text;
                            break;
                        case "lang":
                            lang = text;
                            break;
                        default:
                            if (text is not ("ltr" or "rtl"))
                            {
                                errors.Add(new("'dir' must be 'ltr' or 'rtl'", yaml.Location(setting!)));
                                break;
                            }

                            dir = text;
                            break;
                    }
                }
            }

            languages.Add(new LanguageSettings(folder, label, lang, dir));
        }
    }

    // A single folder name: no separators or spaces, not '.' or '..'.
    private static bool IsLocaleFolder(string name)
    {
        return name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ' ']) < 0;
    }

    private static string Expected(ImmutableArray<string> keys)
    {
        return string.Join(", ", keys[..^1].Select(x => $"'{x}'")) + $" or '{keys[^1]}'";
    }

    private sealed record LanguageSettings(string Key, string? Label, string? Lang, string? Dir)
    {
        public Locale ToLocale(string prefix) => new(Key, Label ?? Key, Lang ?? Key, Dir ?? "ltr", prefix);
    }

    private static ImmutableArray<StepDefinition>? ReadSteps(YamlSource yaml, YamlElement? node, WorkflowEnvironment environment, List<WorkflowValidationError> errors)
    {
        // 'steps:' with no value is the same as no 'steps': the default workflow.
        if (YamlSource.IsNull(node))
        {
            return null;
        }

        if (node is not YamlMapping mapping)
        {
            errors.Add(new("'steps' must be a mapping of step names to steps", yaml.Location(node!)));
            return null;
        }

        var steps = ImmutableArray.CreateBuilder<StepDefinition>();

        foreach (var (name, nameNode, value) in yaml.Entries(mapping, errors))
        {
            if (!WorkflowSchema.IsValidStepName(name))
            {
                errors.Add(new($"invalid step name '{name}': use lowercase letters, digits and dashes", yaml.Location(nameNode)));
            }
            else if (name.Length > WorkflowSchema.MaxStepNameLength)
            {
                errors.Add(new($"step name '{name}' is longer than {WorkflowSchema.MaxStepNameLength} characters", yaml.Location(nameNode)));
            }
            else if (name == WorkflowGraph.ReservedName)
            {
                errors.Add(new($"'{name}' is a reserved step name", yaml.Location(nameNode)));
            }

            steps.Add(ReadStep(yaml, name, nameNode, value, environment, errors));
        }

        return WithoutDisabledSteps(steps.ToImmutable());
    }

    /// <summary>
    /// The steps without those whose <c>enabled</c> is false: a step that needs a disabled step needs what that step needs
    /// instead, so that the files pass it by.
    /// </summary>
    internal static ImmutableArray<StepDefinition> WithoutDisabledSteps(ImmutableArray<StepDefinition> steps)
    {
        var disabled = new Dictionary<string, StepDefinition>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (!step.Enabled)
            {
                disabled.TryAdd(step.Name, step);
            }
        }

        if (disabled.Count == 0)
        {
            return steps;
        }

        IEnumerable<string> Resolve(string name, HashSet<string> visited) =>
            !disabled.TryGetValue(name, out var step) ? [name]
            : visited.Add(name) ? step.Needs.SelectMany(x => Resolve(x, visited))
            : [];

        return [.. steps.Where(x => x.Enabled).Select(x => x with { Needs = [.. x.Needs.SelectMany(n => Resolve(n, [])).Distinct(StringComparer.Ordinal)] })];
    }

    private static StepDefinition ReadStep(YamlSource yaml, string name, YamlElement nameNode, YamlElement? node, WorkflowEnvironment environment, List<WorkflowValidationError> errors)
    {
        var location = yaml.Location(nameNode);
        var step = new StepDefinition { Name = name, Location = location };

        if (YamlSource.IsNull(node))
        {
            errors.Add(new($"step '{name}' needs 'uses' or 'run'", location));
            return step;
        }

        if (node is not YamlMapping mapping)
        {
            errors.Add(new($"step '{name}' must be a mapping of fields", yaml.Location(node!)));
            return step;
        }

        // The key of every field that is set, for the rules that involve several fields.
        var present = new List<(WorkflowField Field, YamlElement Key)>();

        foreach (var (key, keyNode, value) in yaml.Entries(mapping, errors))
        {
            if (!WorkflowSchema.StepFieldsByName.TryGetValue(key, out var field))
            {
                errors.Add(new($"unknown field '{key}' on step '{name}'{WorkflowValidationException.Suggest(key, WorkflowSchema.StepFieldsByName.Keys)}", yaml.Location(keyNode)));
                continue;
            }

            if (YamlSource.IsNull(value))
            {
                continue;
            }

            step = field.Read(yaml, value!, step, environment, errors);
            present.Add((field, keyNode));
        }

        var hasUses = present.Any(x => x.Field.Name == "uses");
        var hasRun = present.Any(x => x.Field.Name == "run");

        if (hasUses && hasRun)
        {
            errors.Add(new($"step '{name}' cannot have both 'uses' and 'run'", location));
        }
        else if (!hasUses && !hasRun)
        {
            errors.Add(new($"step '{name}' needs 'uses' or 'run'", location));
        }

        foreach (var (field, key) in present)
        {
            if (field.CommandOnly && !hasRun)
            {
                errors.Add(new($"'{field.Name}' is only valid with 'run'", yaml.Location(key)));
            }
            else if (field.Name == "with" && !hasUses && !(hasRun && step.Mode == CommandMode.Worker))
            {
                errors.Add(new("'with' is only valid with 'uses' or with 'mode: worker'", yaml.Location(key)));
            }
        }

        return step;
    }
}
