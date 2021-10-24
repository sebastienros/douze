using Douze.FileSystems;

namespace Douze.FileProcessors;

internal sealed class HtmlFileProcessor : TemplateFileProcessor
{
    public HtmlFileProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public override ValueTask<string> ProcessContentAsync(string content)
    {
        return ValueTask.FromResult(content);
    }
}
