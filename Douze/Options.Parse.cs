namespace Douze;

internal partial class Options
{
    public static Options ParseOptions(string[] args)
    {
        var options = new Options();

        foreach (var arg in args)
        {
            if (arg is "-o" or "--output")
            {
                options.OutputPath = arg;
                continue;
            }

            if (arg is "-s" or "--source")
            {
                options.SourcePath = arg;
                continue;
            }
        }

        return options;
    }
}