using Fluid;

namespace Douze.Liquid;

internal sealed class LiquidParser : FluidParser
{
    public static string SectionsIndex = "$$sections";

    public LiquidParser()
    {
        //RegisterIdentifierBlock("section", static (identifier, statements, writer, encoder, context) =>
        //{
        //    if (context.AmbientValues.TryGetValue(SectionsIndex, out var sections))
        //    {
        //        var dictionary = sections as Dictionary<string, IReadOnlyList<Statement>>;

        //        if (dictionary == null)
        //        {
        //            // Lazily initialize the sections dictionary

        //            dictionary = new Dictionary<string, IReadOnlyList<Statement>>();
        //            context.AmbientValues[SectionsIndex] = dictionary;
        //        }

        //        dictionary[identifier] = statements;
        //    }

        //    return new ValueTask<Completion>(Completion.Normal);
        //});
    }
}