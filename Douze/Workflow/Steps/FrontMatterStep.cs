using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: frontmatter</c>. Moves the YAML front matter of a file into its metadata, removes it from the content, and
/// sets <c>page.date</c> to the modification time of the source file.
/// </summary>
internal sealed class FrontMatterStep : Step, IMapStep
{
    private static readonly GlobSet Templates = GlobSet.Parse(["**/*.{md,html,liquid}"]);

    public FrontMatterStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => Templates;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!file.Content.IsText)
        {
            return FileList.Of(file);
        }

        var text = await file.Content.ReadTextAsync(ct);
        var (values, content) = FrontMatter.Parse(text);

        var metadata = file.Metadata.With(own =>
        {
            if (file.LastWriteTimeUtc is { } date)
            {
                var page = own["page"] is NodeObject existing ? existing.ShallowClone() : new NodeObject();
                page["date"] = NodeValue.Create(date);
                own["page"] = page;
            }

            MergeInto(own, values);
        });

        // Without front matter the content is kept as is, with its hash.
        var newContent = ReferenceEquals(content, text) ? file.Content : FileContent.FromText(content);

        return FileList.Of(file.With(Name, content: newContent, metadata: metadata));
    }

    /// <summary>
    /// Merges <paramref name="source"/> into <paramref name="target"/> without changing any node <paramref name="target"/>
    /// shares with other files: nested objects are merged into shallow clones, arrays and values are replaced.
    /// </summary>
    internal static void MergeInto(NodeObject target, NodeObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is NodeObject sourceObject && target[property.Key] is NodeObject targetObject)
            {
                var merged = targetObject.ShallowClone();
                MergeInto(merged, sourceObject);
                target[property.Key] = merged;
            }
            else
            {
                target[property.Key] = property.Value;
            }
        }
    }
}
