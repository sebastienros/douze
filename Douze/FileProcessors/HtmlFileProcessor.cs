namespace Douze.FileProcessors
{
    internal sealed class HtmlFileProcessor : TemplateFileProcessor
    {
        public override string Format => "HTML";

        public override Task<string> ProcessContent(string content)
        {
            return Task.FromResult(content);
        }
    }
}
