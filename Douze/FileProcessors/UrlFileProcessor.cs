using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Results;
using Fluid;

namespace Douze.FileProcessors;

/// <summary>
/// Computes the file name of the destination file.
/// If the permalink contains a filename with extension it is preserved.
/// If the permalink doesn't contain and extension it is assumed as the final endpoint and uses index.html.
/// </summary>
internal sealed class UrlFileProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public UrlFileProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        var hasCustomContentFolder = options.ContentFolders.Any(x => x != FPath.Root);

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {
            if (!Match(document.Path))
            {
                result.Add(new DocumentsResult(document));
                return;
            }

            // Compute destination file path, e.g. blog/post1/index.html

            var context = new TemplateContext(document.Data, options.TemplateOptions);

            var localFilePath = document.Path;

            if (document.Data.TryGetItem("permalink", out var permalink) && permalink != null && !string.IsNullOrWhiteSpace(permalink.GetValue<string>()))
            {
                var permalinkTemplate = options.FluidParser.Parse(permalink.GetValue<string>());
                localFilePath = FPath.Combine("/", await permalinkTemplate.RenderAsync(context));

                var extension = Path.GetExtension(localFilePath.FullName);

                // If the file has no extension, we use the filename as a new folder
                if (string.IsNullOrEmpty(extension))
                {
                    localFilePath = FPath.Combine("/", localFilePath, "index.html");
                }
            }
            else
            {
                // If the processed file is named "index.*" then we don't need to create a folder, otherwise the name
                // of the file is used as a folder such that the url contains it.
                // blog/post1.html -> blog/post1/index.html

                if (Path.GetFileNameWithoutExtension(localFilePath.FullName) != "index")
                {
                    localFilePath = FPath.Combine(localFilePath.GetDirectory().ToAbsolute(), Path.GetFileNameWithoutExtension(localFilePath.FullName) ?? "", "index.html");
                }
                else
                {
                    localFilePath = FPath.Combine(localFilePath.GetDirectory().ToAbsolute(), "index.html");
                }

            }

            // Remove the custom content folder from the destination, e.g. /_content/

            if (hasCustomContentFolder)
            {
                var contentFolders = options.ContentFolders;
                var length = contentFolders.Length;

                for (var i = 0; i < length; i++)
                {
                    var folder = contentFolders[i];

                    if (folder == FPath.Root)
                    {
                        continue;
                    }

                    if (localFilePath.FullName.StartsWith(folder.FullName))
                    {
                        localFilePath = new FPath(localFilePath.FullName[folder.FullName.Length..]);
                    }
                }
            }

            document.Data.Add("url", localFilePath.FullName);

            result.Add(new DocumentsResult($"Generating file {localFilePath}", new CopyDocument(document, options.OutputFileSystem, localFilePath)));
        });

        return result;
    }
}
