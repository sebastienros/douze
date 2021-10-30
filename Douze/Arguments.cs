namespace Douze;

internal partial class Arguments
{
    public string SourcePath { get; set; } = ".";
    public string OutputPath { get; set; } = "_site";

    public static Arguments ParseArguments(string[] args)
    {
        var arguments = new Arguments();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "-o" || arg == "--output")
            {
                i++;
                arguments.OutputPath = args[i];
            }

            if (arg == "-s" || arg == "--source")
            {
                i++;
                arguments.SourcePath = args[i];
            }
        }

        return arguments;
    }
}
