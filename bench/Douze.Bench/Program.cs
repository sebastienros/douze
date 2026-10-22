using Douze.Bench;

const string Usage = """
    usage: dotnet run -c Release --project bench/Douze.Bench -- <command> [options]

    Commands:
      synthetic     --out <dir> [--pages 32000] [--nav limit|index|none]
                    generates a site of N Markdown pages with tags, layouts, includes, data, a paginated page and assets
      api-reference --data <dir> --out <dir> [--mode full|data]
                    generates a site that renders the pages of a real-world API reference from its JSON data
                    (bench/fetch-api-data.sh fetches the data); --mode data writes the data and a single page
      run           --site <dir> --out <dir> [--douze <path>] [--repeat 3] [--label <text>] [--cache clean|warm|edit|tag]
                    [-- <douze options>]
                    builds the site with the douze binary (default: tool-out/douze) and prints a Markdown table row;
                    --cache clean (default) deletes the output and the cache before each build, warm builds again without
                    changes, edit appends a paragraph to one page before each build, tag changes the tags of one page
    """;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Console.WriteLine(Usage);
        return args.Length == 0 ? 2 : 0;
    }

    var options = Arguments.Parse(args.AsSpan(1));

    switch (args[0])
    {
        case "synthetic":
            SyntheticSite.Generate(options.Required("out"), options.Int("pages", 32_000), options.Get("nav") ?? "limit");
            return 0;
        case "api-reference":
            ApiReferenceSite.Generate(options.Required("data"), options.Required("out"), (options.Get("mode") ?? "full") switch
            {
                "full" => false,
                "data" => true,
                var other => throw new BenchException($"--mode must be 'full' or 'data', not '{other}'"),
            });
            return 0;
        case "run":
            return await Runner.RunAsync(
                options.Required("site"),
                options.Required("out"),
                options.Get("douze"),
                options.Int("repeat", 3),
                options.Get("label"),
                options.Get("cache") ?? "clean",
                options.Rest);
        default:
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (BenchException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}
