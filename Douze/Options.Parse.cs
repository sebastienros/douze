namespace Douze
{
    internal partial class Options
    {
        public static Options ParseOptions(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg == "-o" || arg == "--output")
                {
                    i++;
                    options.OutputPath = args[i];                    
                }

                if (arg == "-s" || arg == "--source")
                {
                    i++;
                    options.SourcePath = args[i];
                }
            }

            return options;
        }
    }
}
