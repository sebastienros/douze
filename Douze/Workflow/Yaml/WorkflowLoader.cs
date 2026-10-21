using System.Collections.Immutable;
using System.Globalization;
using Douze.Workflow.Steps;
using SharpYaml.Model;

namespace Douze.Workflow.Yaml;

/// <summary>
/// Reads a workflow file (<c>douze.yml</c>) and checks its shape (section 4.5 of the workflow engine plan). The
/// semantic rules, such as unknown needs or cycles, are checked by <see cref="WorkflowGraph.Build"/>.
/// </summary>
internal static class WorkflowLoader
{
    /// <param name="path">The path of the file on disk.</param>
    /// <param name="fileName">The name used in error locations, such as <c>douze.yml</c> or <c>site/douze.yml</c>.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <exception cref="WorkflowValidationException">Every error of the file.</exception>
    public static async Task<WorkflowFile> LoadAsync(string path, string fileName, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        return Load(text, fileName, System.IO.Path.GetFullPath(path));
    }

    /// <param name="text">The YAML text.</param>
    /// <param name="fileName">The name used in error locations.</param>
    /// <param name="sourceFile">The <see cref="WorkflowDefinition.SourceFile"/> (default: <paramref name="fileName"/>).</param>
    /// <exception cref="WorkflowValidationException">Every error of the file.</exception>
    public static WorkflowFile Load(string text, string fileName, string? sourceFile = null)
    {
        var yaml = YamlSource.Parse(text, fileName);
        var errors = new List<WorkflowValidationError>();
        var site = SiteSettings.None;
        WorkflowDefinition? workflow = null;

        if (YamlSource.IsNull(yaml.Root))
        {
            return new WorkflowFile(site, null);
        }

        if (yaml.Root is not YamlMapping root)
        {
            throw new WorkflowValidationException("a workflow file must be a mapping with 'site' and 'steps'", yaml.Location(yaml.Root!));
        }

        foreach (var (key, keyNode, value) in yaml.Entries(root, errors))
        {
            switch (key)
            {
                case "site":
                    site = ReadSite(yaml, value, errors);
                    break;

                case "steps":
                    workflow = ReadSteps(yaml, value, errors) is { } steps ? new WorkflowDefinition(steps, sourceFile ?? fileName) : null;
                    break;

                default:
                    errors.Add(new($"unknown key '{key}'; expected 'site' or 'steps'", yaml.Location(keyNode)));
                    break;
            }
        }

        if (errors.Count > 0)
        {
            // In the order of the file; the rules that involve several fields of a step run after reading them.
            throw new WorkflowValidationException([.. errors.OrderBy(x => x.Location?.Line).ThenBy(x => x.Location?.Column)]);
        }

        return new WorkflowFile(site, workflow);
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
                var expected = string.Join(", ", WorkflowSchema.SiteKeys[..^1].Select(x => $"'{x}'")) + $" or '{WorkflowSchema.SiteKeys[^1]}'";
                errors.Add(new($"unknown key '{key}' in 'site'; expected {expected}", yaml.Location(keyNode)));
                continue;
            }

            if (YamlSource.IsNull(value))
            {
                continue;
            }

            if (YamlSource.Scalar(value!) is not { } text)
            {
                errors.Add(new($"'{key}' must be {(key == "parallelism" ? "a positive integer" : "a string")}", yaml.Location(value!)));
                continue;
            }

            switch (key)
            {
                case "content" or "data" or "includes":
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

    private static ImmutableArray<StepDefinition>? ReadSteps(YamlSource yaml, YamlElement? node, List<WorkflowValidationError> errors)
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

            steps.Add(ReadStep(yaml, name, nameNode, value, errors));
        }

        return steps.ToImmutable();
    }

    private static StepDefinition ReadStep(YamlSource yaml, string name, YamlElement nameNode, YamlElement? node, List<WorkflowValidationError> errors)
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

            step = field.Read(yaml, value!, step, errors);
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
