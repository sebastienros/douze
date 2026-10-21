using Douze.FileSystems;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

public class DefaultWorkflowTests
{
    [Fact]
    public async Task ToYamlWithContentFolder()
    {
        var source = new MemoryFileSystem();
        await source.CreateDirectoryAsync("/_content/");
        await source.CreateDirectoryAsync("/_data/");
        await source.CreateDirectoryAsync("/_includes/");

        var site = await SiteOptions.DetectAsync(source, new MemoryFileSystem(), null, null);

        // Section 4.6 of the workflow engine plan.
        Assert.Equal("""
            steps:
              site-data:   { inputs: "_data/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: site } }
              folder-data: { inputs: "_content/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: folder, root: _content } }
              pages:       { inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"], needs: [site-data, folder-data], uses: frontmatter }
              markdown:    { needs: pages, uses: markdown }
              paginate:    { needs: markdown, uses: paginate }
              permalink:   { needs: paginate, uses: permalink, with: { root: _content } }
              collections: { needs: permalink, uses: collect }
              render:      { needs: [permalink, collections], uses: liquid }
              layout:      { needs: render, uses: layout }
              write:       { needs: layout, uses: write }

            """, WorkflowGraph.Build(DefaultWorkflow.Create(site)).ToYaml());
    }

    [Fact]
    public async Task ToYamlWithoutContentFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "douze-site");
        var source = new MemoryFileSystem();
        await source.CreateDirectoryAsync("/_data/");
        await source.CreateDirectoryAsync("/_includes/");

        // The output folder is inside the source folder.
        var site = await SiteOptions.DetectAsync(source, new MemoryFileSystem(), root, Path.Combine(root, "_site"));

        Assert.Equal("""
            steps:
              site-data:   { inputs: "_data/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: site } }
              folder-data: { inputs: ["**/*.{yml,yaml,json,xml}", "!_data/**", "!_includes/**", "!douze.yml", "!_site/**"], uses: data, with: { scope: folder } }
              pages:       { inputs: ["**", "!**/*.{yml,yaml,json,xml}", "!_data/**", "!_includes/**", "!douze.yml", "!_site/**"], needs: [site-data, folder-data], uses: frontmatter }
              markdown:    { needs: pages, uses: markdown }
              paginate:    { needs: markdown, uses: paginate }
              permalink:   { needs: paginate, uses: permalink }
              collections: { needs: permalink, uses: collect }
              render:      { needs: [permalink, collections], uses: liquid }
              layout:      { needs: render, uses: layout }
              write:       { needs: layout, uses: write }

            """, WorkflowGraph.Build(DefaultWorkflow.Create(site)).ToYaml());
    }
}
