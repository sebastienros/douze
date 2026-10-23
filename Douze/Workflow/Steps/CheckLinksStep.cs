using System.Net;
using System.Text;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: check-links</c>. Checks the links that <c>links</c> extracted against the files of the build: an internal url
/// must resolve to the path of a file it receives (<c>/a/b/</c> to <c>/a/b/index.html</c>; <c>/a/b</c> to
/// <c>/a/b/index.html</c> or <c>/a/b.html</c> too with <c>extensionless</c>), and its fragment to an anchor of the
/// target when the target is a page <c>links</c> scanned (<c>anchors</c>). A reduce over metadata only (no content):
/// a body edit that changes no link nor anchor replays it. It emits no file and contributes nothing, so it is a branch
/// of the workflow that <c>write</c> does not wait for; an error fails the build all the same.
/// </summary>
/// <remarks>
/// Options: <c>ignore</c> (globs of the internal targets left unchecked, or url prefixes, <c>https://...</c>, of the
/// external ones), <c>skip</c> (globs of the pages whose links are not checked), <c>anchors</c> (<c>true</c>),
/// <c>extensionless</c> (<c>false</c>), <c>external</c> (<c>false</c>: <c>http(s)</c> links are checked over the
/// network, and reported as warnings only, since the network is not deterministic) and <c>errors</c> (<c>fail</c>, or
/// <c>warn</c>). Warnings are kept in a memo and logged again when the check is replayed.
/// </remarks>
internal sealed class CheckLinksStep : Step, IReduceStep, IReplayObserver
{
    private const int ExternalConcurrency = 8;
    private static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(10);

    private readonly GlobSet? _ignore;
    private readonly string[] _ignorePrefixes;
    private readonly GlobSet? _skip;
    private readonly bool _anchors;
    private readonly bool _extensionless;
    private readonly bool _external;
    private readonly bool _warn;
    private BuildContext? _build;

    public CheckLinksStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "ignore", "skip", "anchors", "extensionless", "external", "errors");

        _warn = options.GetString("errors") switch
        {
            null or "fail" => false,
            "warn" => true,
            var other => throw new WorkflowValidationException($"option 'errors' of step '{Name}' must be 'fail' or 'warn', not '{other}'", definition.Location),
        };

        var ignore = options.GetStrings("ignore");
        _ignorePrefixes = [.. ignore.Where(IsExternal)];

        var globs = ignore.Where(x => !IsExternal(x)).ToArray();

        try
        {
            _ignore = globs.Length > 0 ? GlobSet.Parse(globs) : null;
        }
        catch (GlobFormatException e)
        {
            throw new WorkflowValidationException($"invalid pattern in option 'ignore' of step '{Name}': {e.Message}", definition.Location);
        }

        _skip = options.GetGlobs("skip");
        _anchors = options.GetBool("anchors", true);
        _extensionless = options.GetBool("extensionless");
        _external = options.GetBool("external");
    }

    public override StepShape NativeShape => StepShape.Reduce;

    // The links and anchors are metadata; the paths of the files are what links resolve to.
    public override bool ReadsContent => false;

    public override bool ReadsScopes => false;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _build = context;
        return default;
    }

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var targets = new Dictionary<string, VirtualFile>(files.Count, StringComparer.Ordinal);

        foreach (var file in files)
        {
            targets[file.Path.FullName] = file;
        }

        var errors = new List<string>();
        var external = new List<(string Page, string Url, int Line)>();

        // Files are sorted by path and links by line: the errors are in a stable order.
        foreach (var file in files)
        {
            if (_skip?.IsMatch(file.Path) == true || !file.Metadata.TryGetOwn(LinksStep.LinksKey, out var node) || node is not NodeArray links)
            {
                continue;
            }

            var page = file.SourcePath is { } source && source != file.Path ? $"{file.Path.FullName} ({source.FullName})" : file.Path.FullName;

            foreach (var item in links)
            {
                if (item is not NodeObject { } link || link["url"] is not NodeValue<string> { Value: var url })
                {
                    continue;
                }

                var line = link["line"] switch { NodeValue<int> { Value: var i } => i, NodeValue<long> { Value: var l } => (int)l, _ => 0 };

                if (Check(file, url, targets) is { } error)
                {
                    if (error.Length == 0)
                    {
                        external.Add((page, url, line));
                    }
                    else
                    {
                        errors.Add($"{page}: {error} (line {line})");
                    }
                }
            }
        }

        var warnings = new List<string>();

        if (_external && external.Count > 0)
        {
            var results = await CheckExternalAsync(external.Select(x => WithoutFragment(x.Url)).Distinct(StringComparer.Ordinal), ct);

            foreach (var (page, url, line) in external)
            {
                if (results[WithoutFragment(url)] is { } failure)
                {
                    warnings.Add($"{page}: external link '{url}' (line {line}): {failure}");
                }
            }
        }

        if (_warn)
        {
            warnings.InsertRange(0, errors);
            errors.Clear();
        }

        foreach (var warning in warnings)
        {
            context.Log(LogLevel.Warning, warning);
        }

        if (errors.Count > 0)
        {
            throw new DouzeException(string.Join(Environment.NewLine, errors));
        }

        if (warnings.Count > 0)
        {
            context.Build.Cache?.PutMemo(Name, WarningsKey(files), Encoding.UTF8.GetBytes(string.Join('\n', warnings)));
        }

        return new StepOutput(FileList.None);
    }

    /// <summary>
    /// Logs the warnings of a replayed check again.
    /// </summary>
    public void Replayed(IReadOnlyList<VirtualFile> inputs, IReadOnlyList<VirtualFile> outputs)
    {
        if (_build?.Cache is { } cache && cache.TryGetMemo(Name, WarningsKey(inputs), out var memo))
        {
            foreach (var warning in Encoding.UTF8.GetString(memo).Split('\n'))
            {
                _build.View.Message(LogLevel.Warning, Name, warning);
            }
        }
    }

    /// <summary>
    /// Checks one link of <paramref name="page"/>: returns <c>null</c> when it is valid or not checked, the empty string
    /// when it is an external link to check over the network, an error otherwise.
    /// </summary>
    private string? Check(VirtualFile page, string url, Dictionary<string, VirtualFile> targets)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        if (Scheme(url) is { } scheme)
        {
            return (scheme is "http" or "https") && _external && !_ignorePrefixes.Any(x => url.StartsWith(x, StringComparison.Ordinal)) ? "" : null;
        }

        var hash = url.IndexOf('#');
        var fragment = hash < 0 ? null : url[(hash + 1)..];
        var rest = hash < 0 ? url : url[..hash];
        var query = rest.IndexOf('?');
        var path = query < 0 ? rest : rest[..query];

        VirtualFile? target;
        string? display = null;

        if (path.Length == 0)
        {
            target = page;
        }
        else
        {
            var resolved = Resolve(page.Path, Uri.UnescapeDataString(path));

            if (resolved != null && _ignore?.IsMatch(new FPath(resolved)) == true)
            {
                return null;
            }

            target = resolved == null ? null : Find(resolved, targets);

            if (target == null)
            {
                return $"broken link '{url}'";
            }

            display = rest;
        }

        if (!_anchors || string.IsNullOrEmpty(fragment) || fragment == "top" || fragment.StartsWith(":~:", StringComparison.Ordinal)
            || !target.Metadata.TryGetOwn(LinksStep.AnchorsKey, out var node) || node is not NodeArray anchors)
        {
            return null;
        }

        var id = Uri.UnescapeDataString(fragment);

        if (anchors.Any(x => x is NodeValue<string> { Value: var anchor } && anchor == id))
        {
            return null;
        }

        return display == null ? $"missing anchor '#{fragment}'" : $"missing anchor '#{fragment}' in '{display}'";
    }

    /// <summary>
    /// The file a resolved path names, with the rules of a static file server.
    /// </summary>
    private VirtualFile? Find(string path, Dictionary<string, VirtualFile> targets)
    {
        if (path.EndsWith('/'))
        {
            return targets.GetValueOrDefault(path + "index.html");
        }

        if (targets.TryGetValue(path, out var file))
        {
            return file;
        }

        return _extensionless ? targets.GetValueOrDefault(path + "/index.html") ?? targets.GetValueOrDefault(path + ".html") : null;
    }

    /// <summary>
    /// The absolute path of <paramref name="path"/> from the folder of <paramref name="page"/>, its <c>.</c> and
    /// <c>..</c> segments applied, ending with <c>/</c> when it names a folder; <c>null</c> when it leaves the site.
    /// </summary>
    internal static string? Resolve(FPath page, string path)
    {
        if (!path.StartsWith('/'))
        {
            var folder = page.FullName[..(page.FullName.LastIndexOf('/') + 1)];
            path = folder + path;
        }

        var segments = new List<string>();
        var parts = path.Split('/');

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else if (part != "." && part.Length > 0)
            {
                segments.Add(part);
            }
        }

        var last = parts[^1];
        var folderPath = last.Length == 0 || last is "." or "..";
        var joined = "/" + string.Join('/', segments);

        return folderPath && segments.Count > 0 ? joined + "/" : joined;
    }

    private static string? Scheme(string url)
    {
        for (var i = 0; i < url.Length; i++)
        {
            var c = url[i];

            if (c == ':')
            {
                return i > 0 ? url[..i].ToLowerInvariant() : null;
            }

            if (!(char.IsAsciiLetter(c) || i > 0 && (char.IsAsciiDigit(c) || c is '+' or '-' or '.')))
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsExternal(string pattern) =>
        pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string WithoutFragment(string url)
    {
        var absolute = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
        var hash = absolute.IndexOf('#');
        return hash < 0 ? absolute : absolute[..hash];
    }

    /// <summary>
    /// Requests every url once (<c>HEAD</c>, then <c>GET</c> when the server refuses it), a few at a time: the failure of
    /// each url, or <c>null</c>.
    /// </summary>
    private static async Task<Dictionary<string, string?>> CheckExternalAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = ExternalTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("douze-check-links");

        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        var list = urls.ToList();
        var failures = new string?[list.Count];

        await Parallel.ForAsync(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = ExternalConcurrency, CancellationToken = ct }, async (i, ct) =>
        {
            failures[i] = await RequestAsync(http, list[i], ct);
        });

        for (var i = 0; i < list.Count; i++)
        {
            results[list[i]] = failures[i];
        }

        return results;
    }

    private static async Task<string?> RequestAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, ct);

            if (head.IsSuccessStatusCode)
            {
                return null;
            }

            if (head.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden or HttpStatusCode.NotImplemented or HttpStatusCode.NotFound))
            {
                return Status(head);
            }

            using var get = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct);
            return get.IsSuccessStatusCode ? null : Status(get);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"no response in {ExternalTimeout.TotalSeconds:0} seconds";
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            return e.Message;
        }

        static string Status(HttpResponseMessage response) => $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
    }

    private Hash128 WarningsKey(IReadOnlyList<VirtualFile> files)
    {
        var hashes = new Hash128[files.Count * 2 + 1];
        hashes[0] = Definition.ConfigHash;

        for (var i = 0; i < files.Count; i++)
        {
            hashes[i * 2 + 1] = Hash128.OfText(files[i].Path.FullName);
            hashes[i * 2 + 2] = files[i].MetadataHash;
        }

        return Hash128.Combine(hashes);
    }
}
