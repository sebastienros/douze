using System.Text;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;

namespace Douze.Cli;

/// <summary>
/// <c>douze doctor</c>: checks the requirements of every step of the workflow (<see cref="Step.Requirements"/>) without
/// building, and prints them as a table with the hint of each missing one. Exits with 1 when one is missing.
/// </summary>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync(CommandLine commandLine, TextWriter output)
    {
        if (await BuildCommand.LoadAsync(commandLine) is not { } loaded)
        {
            return ExitCodes.Usage;
        }

        var (_, graph, site) = loaded;

        using var cancellation = new CancellationTokenSource();
        using var _ = BuildCommand.HandleCancelKey(cancellation);

        await using var context = await CreateContextAsync(commandLine, site, SilentProgressView.Discard, cancellation.Token);

        if (context == null)
        {
            return ExitCodes.Usage;
        }

        var results = await Requirements.CheckAsync(graph, context, cancellation.Token);
        output.Write(Format(results));

        return results.Any(x => x.IsMissing) ? ExitCodes.BuildErrors : ExitCodes.Success;
    }

    /// <summary>
    /// The context the requirements of a workflow are checked with: the one of a build that does not open the cache, so
    /// that the check does not wait for a build that holds it. Null when the site is invalid (the errors are printed).
    /// </summary>
    internal static async Task<BuildContext?> CreateContextAsync(CommandLine commandLine, SiteOptions site, IProgressView view, CancellationToken ct)
    {
        var options = new BuildOptions
        {
            CacheDirectory = commandLine.CacheDirectory,
            UseCache = false,
            Verbose = commandLine.Verbose,
            View = view,
            Cancellation = ct,
        };

        try
        {
            return await BuildContext.CreateAsync(site, options, view, NullTraceRecorder.Instance, ct);
        }
        catch (WorkflowValidationException e)
        {
            foreach (var error in e.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return null;
        }
    }

    /// <summary>
    /// The table of <paramref name="results"/> (step, requirement, status, details), then a summary line. The details
    /// of a requirement found are its version and path; those of a missing one, why and its hint.
    /// </summary>
    internal static string Format(IReadOnlyList<RequirementResult> results)
    {
        var builder = new StringBuilder();

        if (results.Count == 0)
        {
            builder.AppendLine("No step of the workflow has requirements.");
            return builder.ToString();
        }

        string[] header = ["STEP", "REQUIREMENT", "STATUS", "DETAILS"];
        var rows = results.Select(x => new[] { x.Step, x.Requirement.Name, Status(x.Status.State), Details(x) }).ToList();
        var widths = Enumerable.Range(0, 3).Select(i => rows.Append(header).Max(x => x[i].Length)).ToArray();

        foreach (var row in rows.Prepend(header))
        {
            for (var i = 0; i < 3; i++)
            {
                builder.Append(row[i].PadRight(widths[i] + 2));
            }

            builder.Append(row[3]).Append('\n');
        }

        builder.Append('\n');

        var missing = results.Count(x => x.IsMissing);
        var installable = results.Count(x => x.Status.State == RequirementState.Installable);

        if (missing > 0)
        {
            builder.Append(StepSummary.Plural(missing, "requirement")).Append(" missing: builds fail until ").Append(missing == 1 ? "it is" : "they are").Append(" fixed\n");
        }
        else
        {
            builder.Append("Every requirement is met");

            if (installable > 0)
            {
                builder.Append(": ").Append(StepSummary.Plural(installable, "tool")).Append(" will be downloaded on first use ('douze tools install' downloads ").Append(installable == 1 ? "it" : "them").Append(" now)");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Status(RequirementState state) => state switch
    {
        RequirementState.Found => "ok",
        RequirementState.Installable => "download",
        RequirementState.Missing => "missing",
        _ => "unchecked",
    };

    private static string Details(RequirementResult result)
    {
        var status = result.Status;

        if (status.State == RequirementState.Missing)
        {
            return $"{status.Detail ?? "is missing"}: {result.Requirement.Hint}";
        }

        var path = status.Path == null ? null : status.Version == null ? status.Path : $"at {status.Path}";
        return string.Join(' ', new[] { status.Version, path, status.Detail }.OfType<string>());
    }
}
