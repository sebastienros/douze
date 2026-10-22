using Fluid;
using Fluid.Ast;

namespace Douze.Liquid;

/// <summary>
/// Prepares the text statements of a template once, on the thread that parsed it, before other threads render it.
/// </summary>
/// <remarks>
/// A Fluid <see cref="TextSpanStatement"/> prepares its text on its first render and publishes it without a memory
/// barrier: on ARM64 a thread rendering a fresh template concurrently can see it prepared without its text, and write
/// nothing (a page whose layout lost a part). Fixed in Fluid by sebastienros/fluid#1059; remove this
/// once Douze uses a Fluid preview with the fix. Douze does not change the trimming options, so the defaults prepare
/// the same text as the options templates are rendered with.
/// </remarks>
internal sealed class TextSpanPreparer : AstVisitor
{
    private static readonly TemplateOptions DefaultOptions = new TemplateOptionsBuilder().Build();
    private static readonly TextSpanPreparer Instance = new();

    public static IFluidTemplate Prepare(IFluidTemplate template)
    {
        return template is IStatementList ? Instance.VisitTemplate(template) : template;
    }

    protected override Statement VisitTextSpanStatement(TextSpanStatement textSpanStatement)
    {
        textSpanStatement.PrepareBuffer(DefaultOptions);
        return textSpanStatement;
    }
}
