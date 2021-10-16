namespace Douze.FileProcessors
{
    internal sealed class HtmlFileProcessor : TemplateFileProcessor
    {
        public override string Format => "HTML";

        public override ValueTask<string> ProcessContentAsync(string content)
        {
            return ValueTask.FromResult(content);
        }
    }
}
