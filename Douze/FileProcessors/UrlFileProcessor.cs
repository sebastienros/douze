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
internal class UrlFileProcessor : IFileProcessor
{
    public Predicate<FPath> Match { get; set; } = _ => true;

    public UrlFileProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {

            if (!Match(document.Path))
            {
                result.Add(new DocumentResult(document));
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
                if (String.IsNullOrEmpty(extension))
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

            document.Data.Add("url", localFilePath.FullName);

            result.Add(new DocumentResult($"Generating file {localFilePath}", new CopyDocument(document, options.OutputFileSystem, localFilePath)));
        });

        return result;
    }
}
