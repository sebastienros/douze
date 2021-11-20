namespace Douze;

internal partial class Arguments
{
    public string SourcePath { get; set; } = ".";
    public string OutputPath { get; set; } = "_site";
    public List<string> DataFolders { get; set; } = new List<string>();
    public List<string> IncludesFolders { get; set; } = new List<string>();
    public List<string> ContentFolders { get; set; } = new List<string>();

    public static Arguments ParseArguments(string[] args)
    {
        var arguments = new Arguments();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-o":
                case "--output":
                    i++;
                    arguments.OutputPath = args[i];
                    break;

                case "-s":
                case "--source":
                    i++;
                    arguments.SourcePath = args[i];
                    break;

                case "-d":
                case "--data":
                    i++;
                    arguments.DataFolders.Add(args[i]);
                    break;

                case "-c":
                case "--content":
                    i++;
                    arguments.ContentFolders.Add(args[i]);
                    break;

                case "-i":
                case "--includes":
                    i++;
                    arguments.IncludesFolders.Add(args[i]);
                    break;
            }
        }

        return arguments;
    }
}
