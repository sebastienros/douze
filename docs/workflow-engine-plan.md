# Douze workflow engine: implementation plan

This plan turns the fixed processor pipeline into a configurable workflow engine, as described in
`docs/workflow-engine-brief.md`. It is written for implementers who will execute one phase at a time
without having to make architecture decisions. Every type, file, field, rule and message below is a
decision, not a suggestion. When something is left open on purpose it is listed in section 11 with a
recommended answer, and the recommended answer is what an implementer should use.

Inputs read while planning: `AGENTS.md`, `README.md`, `Douze/todo.txt`, `aspire-dev-gap.md`, every
file under `Douze/` and `Douze.Tests/`, the Sample site, the CI workflows, the Fluid 3 preview API
surface (`Fluid.Core 3.0.0-preview-776`), the SharpYaml 3.16 API surface, and an empirical native AOT
check of Spectre.Console (section 6.1).

How to read this document:

- Section 2 is the architecture: core types with C# signatures and file locations.
- Section 3 maps today's code to the new code, including what is deleted and how the 36 tests survive.
- Sections 4 to 7 specify the YAML format, the command modes and worker protocol, the console and
  trace output, and the incremental design.
- Section 8 lists the phases, their files, tests, acceptance criteria, model and effort, and which
  phases can run in parallel.
- Sections 9 to 11 cover benchmarks, the aspire.dev gaps, and risks with open questions.

Conventions used in signatures: everything is `internal` unless the type is already public today
(`FPath`, `IFileSystem` and the file system types stay public). Nullable reference types are enabled.
`ct` is always a `CancellationToken`. Namespaces follow folders: `Douze/Workflow/Steps/MarkdownStep.cs`
is `Douze.Workflow.Steps.MarkdownStep`.

---

## 1. Goals, non-goals and guiding rules

Goals, in priority order:

1. Replace `Options.BuildDefaultPipeline()` and `SiteHelper.BuildSiteAsync` with a graph of steps
   scheduled by shape (`map`, `group`, `reduce`), where virtual files (path, content, metadata) flow
   between steps.
2. Keep "stupidly simple": a site with no `douze.yml` builds with a built-in default workflow that
   reproduces today's output byte for byte (except the `Generated on` timestamp).
3. Let a site describe its own workflow in `douze.yml`, GitHub Actions style, including command
   steps that run Node (filter, batch and worker modes).
4. Show live progress in the console, write a Chrome trace file and a self-contained `timeline.html`.
5. Make data shared and read-only through layered scopes, parse templates once, keep builds
   deterministic.
6. Design provenance, content hashing, early cutoff and dependency recording into the model from
   phase 1 so that a disk cache (phase 5) and watch mode with partial replays (phase 6) are additive.

Non-goals (scope guard from the brief): no conditions, matrices, secrets, reusable workflows, or any
other general task runner feature. A step takes files and metadata in and gives files and metadata
out. Anything else belongs in CI or in a script that a step calls.

Rules that apply to every phase:

- Follow `AGENTS.md`: .NET 10, central package versions, latest stable dependencies (Fluid 3 previews
  from Feedz), native AOT, deterministic output, no backward compatibility shims.
- Every phase ends in a mergeable state: `dotnet test` passes, the Sample output is unchanged
  (section 8.0 defines the comparison script), the native AOT publish and smoke test pass.
- A phase that changes a convention adapts the Sample sources in the same change so that the Sample
  output stays identical, and the phase lists those adaptations.
- Nothing collected across files in parallel reaches a template, a `reduce` step or the output without
  being sorted first. Sorting is by `FPath` ordinal unless stated otherwise.
- New dependencies must be justified in this plan (section 2.12) and verified under native AOT.

---

## 2. Architecture

### 2.1 Overview and folder layout

```
Douze/
  Program.cs                     CLI entry (rewritten)
  Cli/CommandLine.cs             argument parsing (replaces Arguments.cs)
  Cli/BuildCommand.cs            douze build
  Cli/WorkflowSource.cs          site options and workflow of a build (2.10)
  Cli/ViewSelector.cs            progress view selection (6.2)
  Cli/WorkerHelperCommand.cs     douze worker-helper (phase 3)
  Cli/WatchCommand.cs            douze watch / serve (phase 6)
  SiteOptions.cs                 folders and file systems (replaces Options.cs)
  DouzeException.cs              kept
  FrontMatter.cs                 FrontMatterHelper renamed, same parsing
  JsonHelper.cs / YamlHelper.cs / XmlHelper.cs   kept
  FileSystems/                   kept as is
  Nodes/                         kept; one addition (NodeObject.ShallowClone)
  Liquid/
    LiquidOptions.cs             TemplateOptions factory (replaces Options.Liquid.cs)
    SlugFilter.cs / DouzeJsonContext.cs   kept
    NodeValues.cs                NodeObjectValue, NodeArrayValue, NodeValue to FluidValue
    MetadataValue.cs             scope-resolving FluidValue used as the template model
    TemplateLoader.cs            ITemplateFileProvider over the includes folders + parsed template cache
    AmbientKeys.cs               keys used in TemplateContext.AmbientValues
  Workflow/
    VirtualFile.cs  FileContent.cs  Hash128.cs  Provenance.cs
    Metadata.cs  ScopeSet.cs  DependencyTracker.cs
    StepShape.cs  StepDefinition.cs  IStep.cs  StepContext.cs  StepOutput.cs  FileGroup.cs
    StepFactory.cs  BuiltInSteps.cs  CommandStepFactory.cs
    WorkflowDefinition.cs  WorkflowGraph.cs  WorkflowValidationException.cs
    Glob.cs  GlobSet.cs
    DefaultWorkflow.cs
    BuildOptions.cs  BuildContext.cs  BuildResult.cs  BuildError.cs  FailurePolicy.cs
    SiteBuilder.cs
    Scheduling/Scheduler.cs  StepRun.cs  StepState.cs  FileChannel.cs  UnitKey.cs
    Steps/DataStep.cs  FrontMatterStep.cs  ParseStep.cs  MarkdownStep.cs  PaginateStep.cs
          PermalinkStep.cs  PermalinkResolver.cs  CollectStep.cs  LiquidStep.cs  LayoutStep.cs
          WriteStep.cs
    Commands/CommandStep.cs  FilterCommandStep.cs  BatchCommandStep.cs  WorkerCommandStep.cs
             WorkerProtocol.cs  WorkerProcess.cs  ProcessRunner.cs  CommandTemplate.cs
             douze-worker.mjs (embedded resource)
    Yaml/WorkflowLoader.cs  YamlSource.cs  WorkflowSchema.cs  WorkflowFile.cs
    Cache/BuildCache.cs  CacheIndex.cs  ResultKey.cs  NodeSerializer.cs  PackFile.cs  (phase 5)
    Watch/FileWatcher.cs  IncrementalBuilder.cs  DevServer.cs  LiveReload.cs  (phase 6)
  Progress/IProgressView.cs  PlainProgressView.cs  SilentProgressView.cs  SpectreProgressView.cs
  Tracing/ITraceRecorder.cs  TraceRecorder.cs  TraceSession.cs  ChromeTraceWriter.cs
          TimelineHtmlWriter.cs  timeline.html (embedded resource)
```

The flow of a build:

1. `Program` parses the command line, builds `SiteOptions`, loads `douze.yml` into a
   `WorkflowDefinition` (or calls `DefaultWorkflow.Create(siteOptions)`).
2. `WorkflowGraph.Build(definition, StepFactory)` validates the definition and instantiates one step
   object per definition.
3. `SiteBuilder.BuildAsync` creates a `BuildContext` (file systems, template loader, trace recorder,
   progress view, parallelism semaphore) and runs the `Scheduler`.
4. The scheduler streams source files into root steps, runs each step according to its shape,
   merges scope contributions, and stops on failure according to the `FailurePolicy`.
5. `SiteBuilder` returns a `BuildResult`; `Program` prints the summary, writes trace files, and
   returns the exit code (0 success, 1 build errors, 2 usage or configuration error, 130 cancelled).

### 2.2 Virtual files

`Douze/Workflow/VirtualFile.cs`

```csharp
internal sealed class VirtualFile
{
    public VirtualFile(FPath path, FileContent content, Metadata metadata, string? contentType, Provenance provenance);

    public FPath Path { get; }                 // absolute virtual path, file (never a directory)
    public FileContent Content { get; }
    public Metadata Metadata { get; }
    public string? ContentType { get; }        // null means "derive from the extension" (ContentTypes.FromPath)
    public Provenance Provenance { get; }
    public DateTime? LastWriteTimeUtc { get; } // modification time of the source file this one comes from, kept through With; used for page.date
    public FPath? SourcePath { get; }          // path of the source file this one comes from, kept through With, null for Derived; for messages only, not in Ref
    public string EffectiveContentType { get; } // ContentType ?? ContentTypes.FromPath(Path)

    public Hash128 ContentHash => Content.Hash;
    public Hash128 MetadataHash => Metadata.Hash;
    public FileRef Ref => new(Path, ContentHash, MetadataHash);

    // Derives a new file from this one. Provenance becomes (step, [this.Ref]).
    public VirtualFile With(string step, FPath? path = null, FileContent? content = null, Metadata? metadata = null, string? contentType = null);

    // Creates a file derived from several inputs (group and batch steps).
    public static VirtualFile Derived(string step, IReadOnlyList<VirtualFile> inputs, FPath path, FileContent content, Metadata metadata, string? contentType);

    // Creates a source file. Provenance is (Provenance.SourceStep, []) and Metadata.Directory is path.GetDirectory().
    public static VirtualFile FromSource(IFileSystem fs, FPath path, FileMetadata info);
}

internal readonly record struct FileRef(FPath Path, Hash128 ContentHash, Hash128 MetadataHash);
```

Virtual files are immutable. A step never mutates its input; it returns new files built with `With`
or `Derived`, or returns the input instance unchanged (pass-through). Pass-through is cheap and
keeps hashes and provenance intact, which is what makes early cutoff trivial for untouched files.

`Douze/Workflow/FileContent.cs`

```csharp
internal abstract class FileContent
{
    public static FileContent FromText(string text);
    public static FileContent FromBytes(ReadOnlyMemory<byte> bytes, bool isText);
    public static FileContent FromFile(IFileSystem fs, FPath path, long length);   // lazy: read once on first access, then retained
    public static readonly FileContent Empty;

    public abstract bool IsText { get; }
    public abstract long? Length { get; }              // null until a lazy file is read
    public Hash128 Hash { get; }                       // lazy, thread safe (Lazy<Hash128> with ExecutionAndPublication); blocks on the read of a file that was not loaded
    public virtual ValueTask LoadAsync(CancellationToken ct);                        // reads and retains a lazy file; the scheduler awaits it for every consumed file, so steps never block on Hash

    public abstract ValueTask<string> ReadTextAsync(CancellationToken ct);           // UTF-8, BOM stripped
    public abstract ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(CancellationToken ct);
    public abstract ValueTask<Stream> OpenReadAsync(CancellationToken ct);           // streaming for large data files
    public async ValueTask CopyToAsync(Stream destination, CancellationToken ct);
}
```

Three implementations live in the same file: `TextContent` (string, hashed by streaming UTF-8 bytes
through `XxHash128.Append` with a pooled 16 KB buffer, never allocating a second copy),
`BytesContent`, and `LazyFileContent` (reads the file into a byte array on first access; `OpenReadAsync`
opens the file system stream directly when the bytes are not loaded yet). Text decoding of
`BytesContent`/`LazyFileContent` is cached after the first `ReadTextAsync`.

`Douze/Workflow/Hash128.cs`

```csharp
internal readonly record struct Hash128(ulong Low, ulong High) : IComparable<Hash128>
{
    public static Hash128 Of(ReadOnlySpan<byte> data);
    public static Hash128 OfText(string text);                      // UTF-8, streamed
    public static Hash128 Combine(params ReadOnlySpan<Hash128> hashes);   // XxHash128 over the concatenated 16-byte values, order sensitive
    public static Hash128 OfStrings(IEnumerable<string> values);    // hashes each string's UTF-8 bytes separated by a 0 byte
    public static Hash128 Parse(string hex);
    public override string ToString();                              // 32 lowercase hex characters
    public static readonly Hash128 Empty;                           // Of(ReadOnlySpan<byte>.Empty)
}
```

Backed by `System.IO.Hashing.XxHash128` (section 2.12). Hashes are stable across runs and
platforms, which is what the disk cache needs.

`Douze/Workflow/Provenance.cs`

```csharp
internal sealed record Provenance(string Step, ImmutableArray<FileRef> Inputs)
{
    public const string SourceStep = "source";
    public static readonly Provenance Source = new(SourceStep, ImmutableArray<FileRef>.Empty);
    public bool IsSource => Step == SourceStep;
}
```

Provenance records the step that produced the file and the refs (path and hashes) of the files it was
derived from. It is one level deep: the full chain is reconstructed by following refs through the
step results kept by the scheduler (section 2.6) and later by the cache index (section 7).

### 2.3 Metadata and scopes

Data is never deep-copied per file. A file carries only its own metadata; folder data and site data
live in scopes that are shared by reference and resolved at lookup time.

`Douze/Workflow/Metadata.cs`

```csharp
internal sealed class Metadata
{
    public static readonly Metadata Empty;
    public Metadata(NodeObject own, FPath directory);

    public NodeObject Own { get; }        // front matter, page.*, url, pagination, captured variables. Treated as immutable.
    public FPath Directory { get; }       // source directory used to find folder scopes; a directory path (trailing slash)
    public Hash128 Hash { get; }          // lazy: canonical JSON of Own (sorted keys, invariant formatting) + Directory

    public Metadata With(Action<NodeObject> mutate);   // NodeObject.ShallowClone(), apply, new Metadata
    public Metadata WithDirectory(FPath directory);
    public bool TryGetOwn(string key, out Node? value);
}
```

`NodeObject.ShallowClone()` is added to `Douze/Nodes/NodeObject.cs`: it copies the top-level
dictionary and shares nested nodes. Nested nodes are shared between files and must not be mutated
after a step publishes them. Steps that need to change a nested object replace it with a new one.

`Douze/Workflow/ScopeSet.cs`

```csharp
internal sealed record SiteScope(string Source, NodeObject Values, IReadOnlyDictionary<string, string>? KeySources = null);
// Source: "step:<name>" of the contributing step (data steps included).
// KeySources: top-level key -> data file path, used for dependency tracking at whole-file granularity. A key built
// from a folder of files (_data/pkgs/*.json -> pkgs) maps to the folder path ("/_data/pkgs/"): a replay checks every
// data file under it.

internal sealed record FolderScope(FPath Directory, NodeObject Values, ImmutableArray<FPath> Sources);

internal enum ScopeKind { Own, Folder, Site, Fallback, None }
internal readonly record struct ScopeHit(ScopeKind Kind, string Source);   // Source: directory, scope source or data file

internal sealed class ScopeSet
{
    public static readonly ScopeSet Empty;
    public IReadOnlyDictionary<FPath, FolderScope> Folders { get; }
    public IReadOnlyList<SiteScope> Site { get; }        // highest precedence first

    public ScopeSet WithSite(SiteScope scope);           // prepends: later contributions win
    public ScopeSet WithFolders(IEnumerable<FolderScope> folders);
    public static ScopeSet Merge(IReadOnlyList<ScopeSet> contributions, ScopeSet @base);  // used by the scheduler, see 2.6

    // Resolution order: own -> folder scopes from meta.Directory up to "/" -> site scopes in order -> fallback
    public bool TryResolve(Metadata meta, string key, NodeObject? fallback, DependencyTracker? deps, out Node? value, out ScopeHit hit);
    public Hash128 Hash { get; }   // combined hash of all scopes, used in result keys of reduce steps and groups
}
```

Lookup semantics are "first scope that contains the top-level key wins". There is no deep merge
between scopes (the settings scope of `site:`, added at the integration of phases 2 to B, completes a
`site` object one level deep; see 4.2). This matches today's behavior for every Sample and test case (folder data only sets
top-level keys), and it is what makes sharing safe.

Which scopes a step sees: the scheduler gives each step a `ScopeSet` that is the merge of the
contributions of all its transitive predecessors over the base set (section 2.6). A step that is not
downstream of `collections` does not see `collections`, which is correct: the YAML author writes the
dependency explicitly, as in the brief's example (`render: needs: [markdown, collections]`).

`Douze/Workflow/DependencyTracker.cs`

```csharp
internal sealed class DependencyTracker
{
    public static readonly DependencyTracker Null;             // records nothing
    public void RecordTemplate(FPath path, Hash128 contentHash);   // layouts, includes, with.template files
    public void RecordDataFile(FPath path);                     // whole-file granularity for _data keys
    public void RecordScope(string source);                     // "step:collections" and other step contributions
    public DependencySet ToSet();                               // sorted, deduplicated, immutable
}

internal sealed record DependencySet(ImmutableArray<(FPath Path, Hash128 Hash)> Templates, ImmutableArray<FPath> DataFiles, ImmutableArray<string> Scopes);
```

The tracker is created per unit of work (one file for `map`, one group for `group`, the whole input
for `reduce`) and passed through `StepContext.Dependencies`. The Liquid bridge records template reads
and scope lookups into it (sections 2.8 and 7.3). `TryResolve` records every scope it consults, hit
or miss: a scope that does not define the key could define it later and shadow the hit. The set is
not hashed and is not part of a result key (section 2.7): it is the record a replay validates.

### 2.4 Steps

`Douze/Workflow/StepShape.cs`

```csharp
internal enum StepShape { Map, Group, Reduce }
```

`Douze/Workflow/StepDefinition.cs` is the configuration of one step, whether it comes from YAML or
from `DefaultWorkflow`:

```csharp
internal sealed record StepDefinition
{
    public required string Name { get; init; }
    public ImmutableArray<string> Needs { get; init; } = [];
    public ImmutableArray<string> Inputs { get; init; } = [];     // globs over the source tree
    public ImmutableArray<string> Match { get; init; } = [];      // globs over entering files; empty = the step's default
    public string? Uses { get; init; }                             // built-in id
    public NodeObject With { get; init; } = new();                 // options for `uses`
    public string? Run { get; init; }                              // command line
    public CommandMode Mode { get; init; } = CommandMode.Batch;    // filter | batch | worker
    public string? GroupBy { get; init; }                          // "file" | "dir" | Liquid template
    public ImmutableArray<string> Outputs { get; init; } = [];    // globs read back after a batch command
    public bool Incremental { get; init; } = true;
    public string Version { get; init; } = "";
    public ImmutableArray<string> Sources { get; init; } = [];    // globs of files that participate in the command's cache key
    public ImmutableDictionary<string, string> Env { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string? WorkingDirectory { get; init; }
    public TimeSpan? Timeout { get; init; }
    public StderrPolicy Stderr { get; init; } = StderrPolicy.Fail;
    public bool Shell { get; init; } = true;
    public SourceLocation? Location { get; init; }                 // YAML file, line, column; null for the default workflow

    public bool IsCommand => Run is not null;
    public Hash128 ConfigHash { get; }   // lazy: canonical JSON of every field except Name, Needs, Location, plus Douze's informational version
}

internal enum CommandMode { Filter, Batch, Worker }
internal enum StderrPolicy { Fail, Warn, Ignore }
internal readonly record struct SourceLocation(string File, int Line, int Column);
```

`Douze/Workflow/IStep.cs`: a step implementation declares which shapes it supports and implements
the matching interfaces. The scheduler picks the interface from the definition's effective shape.

```csharp
internal abstract class Step
{
    protected Step(StepDefinition definition);
    public StepDefinition Definition { get; }
    public string Name => Definition.Name;
    public abstract StepShape NativeShape { get; }               // shape when group-by is absent
    public virtual bool SupportsGrouping => false;               // whether group-by is allowed
    public virtual GlobSet DefaultMatch => GlobSet.All;          // which entering files the step consumes
    public StepShape EffectiveShape => Definition.GroupBy is null ? NativeShape : StepShape.Group;
    public virtual ValueTask InitializeAsync(BuildContext context, CancellationToken ct) => default;  // parse options, start workers
    public virtual ValueTask DisposeAsync() => default;          // stop workers, delete temp folders
}

internal interface IMapStep     { ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct); }
internal interface IGroupStep   { ValueTask<StepOutput> ProcessGroupAsync(FileGroup group, StepContext context, CancellationToken ct); }
internal interface IReduceStep  { ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct); }
```

`Douze/Workflow/StepOutput.cs`

```csharp
// One, many or zero files without allocating a list for the common single case.
internal readonly struct FileList
{
    public static FileList None;
    public static FileList Of(VirtualFile file);
    public static FileList Of(IReadOnlyList<VirtualFile> files);
    public int Count { get; }
    public VirtualFile this[int index] { get; }
    public IEnumerable<VirtualFile> AsEnumerable();
}

internal sealed record StepOutput(FileList Files, SiteScope? Scope = null, IReadOnlyList<FolderScope>? Folders = null);
```

Only `reduce` steps may contribute scopes (`Scope`, `Folders`). The scheduler rejects a non-null
scope from a group step with an internal error; map steps cannot return one by construction.

`Douze/Workflow/FileGroup.cs`

```csharp
internal sealed record FileGroup(string Key, IReadOnlyList<VirtualFile> Files);   // Files sorted by Path
```

`Douze/Workflow/StepContext.cs`

```csharp
internal sealed class StepContext
{
    public StepDefinition Definition { get; }
    public string StepName => Definition.Name;
    public ScopeSet Scopes { get; }                 // merged ancestor contributions over the base
    public BuildContext Build { get; }
    public DependencyTracker Dependencies { get; }  // per unit of work
    public TraceSpan Span { get; }                  // the unit's span, so steps can add args
    public void Log(LogLevel level, string message);   // forwarded to the progress view with the step name
}
```

`Douze/Workflow/BuildContext.cs`

```csharp
internal sealed class BuildContext : IAsyncDisposable
{
    public SiteOptions Site { get; }
    public BuildOptions Options { get; }
    public IFileSystem Source => Site.Source;
    public IFileSystem Output => Site.Output;
    public TemplateLoader Templates { get; }        // shared parsed template cache + include file provider
    public TemplateOptions LiquidOptions { get; }   // built once by LiquidOptions.Create(this)
    public ITraceRecorder Trace { get; }
    public IProgressView View { get; }
    public SemaphoreSlim Parallelism { get; }       // CPU work slots for built-in steps, see 2.6
    public SemaphoreSlim ProcessSlots { get; }      // slots for external processes (command steps), same size, see 2.6
    public CancellationToken Cancellation { get; }  // user cancellation (Ctrl+C)
    public string TempRoot { get; }                 // <cache dir>/tmp/<build id>/ for batch commands, deleted by DisposeAsync (kept with --verbose)
    public BuildCache? Cache { get; }               // the disk cache; null with --no-cache, for a site not on disk, or when another build holds it (phase 5)
}
```

`Douze/Workflow/StepFactory.cs` and `BuiltInSteps.cs`

```csharp
internal static class BuiltInSteps
{
    // id -> factory. Registered ids: data, frontmatter, parse, markdown, paginate, permalink, collect, liquid, layout, write
    public static IReadOnlyDictionary<string, Func<StepDefinition, Step>> Registry { get; }
}

internal static class StepFactory
{
    // Throws WorkflowValidationException with the definition's Location for unknown `uses`, bad `with` options, or run+uses together.
    public static Step Create(StepDefinition definition);   // Run != null -> CommandStepFactory.Create(definition)
}

// Douze/Workflow/CommandStepFactory.cs: phase 1 throws WorkflowValidationException("command steps are not available yet");
// phase 3 replaces the body. Keeping it in its own file is what lets phases 2 and 3 run in parallel.
internal static class CommandStepFactory { public static Step Create(StepDefinition definition); }
```

Each built-in validates its `with` options in its constructor and throws
`WorkflowValidationException` with the message format of section 4.5. Options are read from the
`NodeObject` with small helpers in `Douze/Workflow/WithOptions.cs` (`GetString`, `GetBool`,
`GetGlobs`, `GetInt`, each raising "unknown option" for keys the step does not declare).

### 2.5 Workflow definition and graph

`Douze/Workflow/WorkflowDefinition.cs`

```csharp
internal sealed record WorkflowDefinition(ImmutableArray<StepDefinition> Steps, string? SourceFile);
```

`Douze/Workflow/WorkflowGraph.cs`

```csharp
internal sealed class WorkflowGraph
{
    public static WorkflowGraph Build(WorkflowDefinition definition);   // validates (4.5) and instantiates steps through StepFactory

    public IReadOnlyList<GraphNode> Nodes { get; }           // topological order, ties broken by name (ordinal)
    public GraphNode this[string name] { get; }
    public IReadOnlyList<GraphNode> Roots { get; }           // nodes with Inputs and no Needs
    public IReadOnlyList<GraphNode> Terminals { get; }       // nodes nobody needs
    public IReadOnlySet<string> Ancestors(string name);
    public string ToYaml();                                   // effective workflow, used by `douze build --print-workflow`
}

internal sealed class GraphNode
{
    public StepDefinition Definition { get; }
    public Step Step { get; }
    public int Index { get; }                                 // topological index
    public IReadOnlyList<GraphNode> Needs { get; }
    public IReadOnlyList<GraphNode> Dependents { get; }
    public GlobSet? Inputs { get; }                           // null when Inputs is empty
    public GlobSet Match { get; }                             // Definition.Match or Step.DefaultMatch
}
```

Topological order is computed with Kahn's algorithm, always picking the smallest name (ordinal)
among ready nodes, so the order is deterministic regardless of the YAML order. Cycle detection
reports the cycle path (`a -> b -> c -> a`).

### 2.6 Scheduler

`Douze/Workflow/Scheduling/Scheduler.cs`

```csharp
internal sealed class Scheduler
{
    public Scheduler(WorkflowGraph graph, BuildContext context);
    public Task<SchedulerResult> RunAsync(CancellationToken ct);
}

internal sealed record SchedulerResult(IReadOnlyList<StepRun> Runs, IReadOnlyList<BuildError> Errors, ScopeSet FinalScopes, bool Cancelled);
```

`Douze/Workflow/Scheduling/StepRun.cs`

```csharp
internal enum StepState { Pending, Running, Succeeded, Failed, Skipped, Cancelled }

internal sealed class StepRun
{
    public GraphNode Node { get; }
    public StepState State { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? EndedAt { get; }
    public int FilesIn { get; }            // files that entered (after Inputs filtering)
    public int FilesConsumed { get; }      // files that matched and were processed
    public int FilesOut { get; }
    public int Units { get; }              // map: files consumed; group: groups; reduce: 1
    public int UnitsDone { get; }
    public IReadOnlyList<BuildError> Errors { get; }
    public ScopeSet Contributions { get; }                 // what this step added (empty for map and group)
    public IReadOnlyList<UnitResult> Results { get; }      // one per unit: key, outputs; kept in memory for incremental builds
}

internal sealed record UnitResult(UnitKey Key, ImmutableArray<FileRef> Inputs, ImmutableArray<VirtualFile> Outputs, DependencySet Dependencies);
```

`Douze/Workflow/Scheduling/UnitKey.cs`

```csharp
// Identity of a unit of work inside a step: the file path for map, the group key for group, "*" for reduce.
internal readonly record struct UnitKey(string Step, string Unit);
```

How the scheduler runs:

**Channels.** Every node owns a `FileChannel` (`Douze/Workflow/Scheduling/FileChannel.cs`): an
unbounded `System.Threading.Channels.Channel<VirtualFile>` per consumer. When a node has several
dependents, the producer writes each output to every dependent's channel (fan-out). A `FileChannel`
exposes `ChannelReader<VirtualFile> Subscribe(string consumer)`, `ValueTask WriteAsync(VirtualFile)`
and `void Complete(Exception? error)`. Unbounded is fine: the number of in-flight files is bounded by
the number of files, and content is shared, not copied.

**Source.** Before any step starts, the scheduler enumerates the source file system once
(`EnumerateFileSystemEntriesAsync(FPath.Root, recursive: true)`), keeps files only, sorts them by
path, and excludes the output folder when it is inside the source. Each root node receives the
source files that match its `Inputs` glob set, in sorted order. A node with both `Inputs` and
`Needs` receives the matching source files plus every output of its `Needs`. Source files are
created with `VirtualFile.FromSource` and read lazily.

**Entering files and matching.** For every node, the scheduler merges the channels of its `Needs`
with `MergeAsync(readers)`. While merging it tracks paths in a `Dictionary<FPath, string>`
(path to producing step) under a lock; a path arriving from two different producers raises
`BuildError` "step 'write' received '/a/index.html' from both 'render' and 'api'" and fails the
node, unless it is the same file: the same `VirtualFile` instance (a file passed through by two needs,
a diamond such as `render` needing `permalink` and `collections` in the default workflow, where
`collections` passes the non-HTML files through), or another instance with the same `FileRef` (a
replayed unit re-emitting an unchanged file next to a re-executed one in phases 5 and 6, or a step
returning an equal copy of its input). Such a file enters once, through whichever need delivered it
first. Identity is checked under the lock; refs are compared outside it, after loading both contents,
because they are only needed on this rare path. Entering files are then split by `Match`: files that match are **consumed** by the step;
files that do not match are **passed through** to the node's output unchanged, as soon as they
arrive, without waiting for the step.

**Map.** `Parallel.ForEachAsync(reader.ReadAllAsync(ct), options, body)` where
`MaxDegreeOfParallelism = BuildOptions.MaxDegreeOfParallelism`. The body acquires one slot of
`BuildContext.Parallelism` (a global `SemaphoreSlim` sized to `MaxDegreeOfParallelism`, so that
several map steps running at once do not oversubscribe the machine; command steps take a
`ProcessSlots` slot instead while they wait on an external process), creates a `DependencyTracker`
and a trace span, calls `ProcessAsync`, writes each output file to the node's channel immediately,
records a `UnitResult`, releases the slot, and reports progress. A file therefore moves to the next
step as soon as it is done. The map step's channel is completed when the input channel is complete
and all bodies have returned.

**Reduce.** The node reads its whole input into a list, waits for the input channel to complete,
sorts the list by path (ordinal), acquires one slot, calls `ProcessAllAsync`, writes the outputs,
records the contribution (`StepOutput.Scope`, `StepOutput.Folders`), and completes its channel. The
`UnitResult.Inputs` of a reduce step is the sorted list of all input refs, which is what early cutoff
compares later.

**Group.** Same barrier as reduce (groups cannot be known complete before the input ends), then
files are partitioned by the group key, groups are ordered by key (ordinal), and each group runs as
its own task under `Parallel.ForEachAsync` with one slot each. Group keys come from
`GroupKeyResolver` (`Douze/Workflow/Scheduling/GroupKeyResolver.cs`): `file` is the file path,
`dir` is the file's directory, and any other value is parsed once as a Liquid template rendered
with `file` (`path`, `dir`, `name`, `ext`) and `data` (the file's `MetadataValue`) in scope.
Outputs of all groups go to the channel as they finish.

**Scopes.** Before a node starts, its `StepContext.Scopes` is
`ScopeSet.Merge(contributions of every ancestor in descending topological index, BuildContext.BaseScopes)`;
the base is `ScopeSet.FromSettings` of the values of `site:` that templates see (`site.url`, 4.2), or
`ScopeSet.Empty` when there are none; the `data` step's contribution is one of them when it is an ancestor. Descending index means the
nearest contributions take precedence. A node starts once every ancestor has published its
contribution: only reduce nodes delay that (they publish after their unit ran, before completing
their channel), map and group nodes publish an empty contribution before they run. So a map node
downstream of a reduce node starts only after the reduce ran, by construction of the barrier, and a
chain of map nodes starts immediately and streams, whatever the states of the nodes in between.

**Completion order.** A node makes its run final (state, end time, counters, sorted results and
errors), waits for the nodes it needs to be complete, reports `IProgressView.StepCompleted`, and only
then completes its channel. For every edge A -> B, A is reported before B, whatever the states. A
node that stopped early is `Skipped` when a need failed or was skipped, `Cancelled` otherwise, so the
final states do not depend on which of a failure and a cancellation was observed first.
Publishing the outcome cannot be skipped: `NodeRunner.RunAsync` completes the contribution, the
channel and `Completed` in a `finally`, because dependents wait for them. An exception outside the
units and the step's own code (in the view, in the reporting of a failure, an out-of-memory
exception anywhere) fails the node with that error and cancels the build, which then ends with the
other nodes skipped or cancelled; before the integration of phases 2 to B it skipped the completion
and the build hung at 0% CPU (benchmark finding 5, `SchedulerTests.ExceptionOutsideAUnitFailsTheBuild`).
A map step whose processing loop fails outside a unit also stops reading its inputs at once.

**Skipping.** When a node fails or is skipped, its channel is completed with a
`StepUnavailableException`; every dependent stops when it reaches it, is marked `Skipped`, completes
its own channel the same way, and the rest of the graph continues. Independent nodes finish
normally. A streaming dependent may have processed files before the failure and emitted outputs
downstream: it is `Skipped` all the same, and its `Results` are not reused (section 7.5 replays
succeeded steps only).

**Failure policy** (`Douze/Workflow/FailurePolicy.cs`, `enum FailurePolicy { Default, FailFast, KeepGoing }`):

| Policy | A unit of step S fails | Dependents of S | Independent steps |
|---|---|---|---|
| `Default` | S stops at once: its remaining units are cancelled through a per-step linked `CancellationTokenSource`; the first error is reported with the file | Skipped | Finish |
| `FailFast` (`--fail-fast`) | The build's `CancellationTokenSource` is cancelled; every running step stops | Skipped | Cancelled |
| `KeepGoing` (`--keep-going`) | S keeps processing its other units and reports every error | Skipped | Finish |

A unit failure is any exception thrown by the step for that unit, converted to a `BuildError`
(`Douze/Workflow/BuildError.cs`):

```csharp
internal sealed record BuildError(string Step, FPath? File, string Message, SourceLocation? Location, Exception? Exception)
{
    public override string ToString();   // "error: <step>: <file>(<line>,<col>): <message>" without the parts that are null
}
```

`DouzeException`, Fluid `ParseException` (with `Position` mapped to line and column) and YAML
exceptions are reported with their message only; any other exception type is reported with its
message and, with `--verbose`, its stack trace.

**Cancellation.** `Program` wires `Console.CancelKeyPress` to the build's token; the scheduler
observes it in every loop, completes channels with `OperationCanceledException`, marks running
steps `Cancelled`, and `RunAsync` returns `Cancelled = true`. Exit code 130. Partially written
output files are not cleaned up (the next build overwrites them).

**Determinism.** Map outputs reach the channel in completion order, which is not deterministic.
Every consumer that can observe order sorts first: reduce and group inputs are sorted by path,
group keys are sorted, `BuildResult.Outputs` is sorted, `collections` are sorted by date then path,
and the `write` step is order independent. Trace lanes (section 6.3) are assigned by span start
time, which is the only nondeterministic output, and it is a diagnostic.

**Progress reporting.** The scheduler calls `IProgressView.StepStarted` when a node starts working:
when its first file enters, or when its first unit runs (a reduce step with no input), or when it
completes without either. A node waiting for its needs is not started: `StepRun.StartedAt`, the
step's trace span, the summary's time column and its timeline all measure the time a step had work,
so a streaming step's bar starts after its first need produced a file. (Until the integration of
phases 2 to B, a node started as soon as its ancestors' scopes were ready, which is the start of the
build for most steps, and every bar of the timeline started at 0.) Then `StepProgress(step, done, total)` after each unit (the view throttles redraws), and
`StepCompleted(run)` at the end. `total` is null for a streaming map step until its input channel is
complete, which the views show as `done/?`.

### 2.7 Provenance, hashes and result keys

Every file carries `Provenance` (2.2). Every unit of work records a `UnitResult` with its `UnitKey`,
its input refs, its outputs and its dependency set. The result key of a unit, used by the in-memory
replay (phase 6) and the disk cache (phase 5) to find a previous result, is what is known before the
unit runs:

`Douze/Workflow/Cache/ResultKey.cs` (created in phase 1, in the `Workflow` folder; the `Cache`
folder is created in phase 5 and the file moves then)

```csharp
internal static class ResultKey
{
    // Hash128.Combine(stepName, definition.ConfigHash, inputs (sorted by path: path, contentHash, metadataHash), commandVersion)
    public static Hash128 Compute(StepDefinition definition, ImmutableArray<FileRef> inputs, Hash128 commandVersion);
}
```

- `commandVersion` is `Hash128.Empty` for built-ins and `Hash128.Combine(hash of definition.Version, hashes of files matching definition.Sources)` for command steps.
- What the unit read besides its inputs (templates, data files, scopes) is not in the key: it is only
  known once the unit ran. It is kept with the result as its `DependencySet`, and a previous result is
  reused only when its key matches **and** every recorded dependency is unchanged: each template has
  the recorded content hash, each data file has the content hash it had in the run that recorded it
  (known from the `data` step's input refs), each scope source has the hash it had (known from the
  contributing step's `Contributions`). A unit that reads scopes without recording (a command step)
  records every scope of its `ScopeSet` instead, so any data change re-runs it: correctness over
  minimal rebuilds.

Early cutoff (section 7.2) compares the **output** refs of a unit with its previous result: when
all outputs are identical, dependents are not re-run for those files.

*As implemented (phase 5):* `ResultKey` is in `Douze/Workflow/Cache/`. The dependency set records keys rather than
data files and scope sources (7.3), and a result is reused when every recorded lookup still resolves to the same
value. Command steps do not record every scope: they cannot read scopes (their model has the file, the folders and
`with`; workers receive the own metadata only), so `ScopeSet.Hash` in their keys would only re-run them for nothing;
what a command reads from disk is declared with `sources`. A step that reads only metadata (`Step.ReadsContent`, false
for `collect`) leaves the content hashes of its inputs out of its keys. The key also has the date of the source file
each input comes from (`VirtualFile.LastWriteTimeUtc`): `frontmatter` reads it for `page.date`, and a checkout or a copy
changes it without changing the content (found by `scripts/compare-sample.sh`, which touches the Sample). Early cutoff needs no comparison: a unit that
runs again and produces the same outputs gives its dependents the same input refs, hence the same keys.

### 2.8 Templates and the Liquid bridge

`Douze/Liquid/LiquidOptions.cs`

```csharp
internal static class LiquidOptions
{
    public static TemplateOptions Create(TemplateLoader loader);
    // new TemplateOptionsBuilder()
    //   .AddFilter("slug", SlugFilter.Slugify)
    //   .WithJsonSerializerOptions(DouzeJsonContext.Default.Options)
    //   .WithFileProvider(loader)                 // {% include %} / {% render %} from the includes folders (gap A2)
    //   .WithTemplateCache(loader)                // Fluid caches parsed includes through TemplateLoader
    //   .AddValueConverter(NodeValues.Convert)    // Node -> FluidValue, Metadata -> MetadataValue
    //   .WithDefaultFileExtension(".liquid")
    //   .Build();
    public static FluidParser CreateParser();      // new FluidParser(new FluidParserOptions { TrackStatementLocations = true }) : errors carry line/column
}
```

`Douze/Liquid/NodeValues.cs`: explicit, reflection-free conversions, so templates no longer rely on
Fluid's reflection based `ObjectValue` for `NodeValue<T>` (which only works today through
`ToString()`).

```csharp
internal static class NodeValues
{
    public static object? Convert(object value);   // NodeObject -> NodeObjectValue, NodeArray -> NodeArrayValue, NodeValue<T> -> StringValue/NumberValue/BooleanValue/DateTimeValue, NodeValue<Metadata> -> MetadataValue, Metadata -> MetadataValue, else null
}
internal sealed class NodeObjectValue : FluidValue   // Type = Dictionary; GetValueAsync by key; size; enumerates key/value pairs like Fluid's DictionaryValue
internal sealed class NodeArrayValue  : FluidValue   // Type = Array; GetIndex; size, first, last; enumerates
```

`Douze/Liquid/MetadataValue.cs`

```csharp
internal sealed class MetadataValue : FluidValue
{
    public MetadataValue(Metadata metadata, NodeObject? fallback = null);   // fallback: layout front matter (lowest precedence)
    public Metadata Metadata { get; }
    // GetValueAsync(name, context): reads ScopeSet and DependencyTracker from context.AmbientValues (AmbientKeys.Scopes, AmbientKeys.Dependencies),
    // calls ScopeSet.TryResolve(Metadata, name, fallback, deps, ...), converts the Node through NodeValues.Convert.
    // Misses return NilValue.Instance. Type = FluidValues.Dictionary. Enumeration is not supported (returns empty).
}
```

The model of every page render is a `MetadataValue`; Fluid's `TemplateContext.GetValue(name)`
falls back to `Model.GetValueAsync(name)` after local scope, which is exactly the lookup chain we
want. `TemplateContext.AmbientValues` carries: `AmbientKeys.Scopes` (the `ScopeSet`),
`AmbientKeys.Dependencies` (the `DependencyTracker`), `AmbientKeys.File` (the `VirtualFile`).

`Douze/Liquid/TemplateLoader.cs`

```csharp
internal sealed class TemplateLoader : ITemplateFileProvider, ITemplateCache
{
    public TemplateLoader(IFileSystem source, IReadOnlyList<FPath> includesFolders, FluidParser parser);

    // Parses once per distinct content hash. Used by liquid (page bodies), layout, grouped liquid (with.template) and permalink templates.
    public ParsedTemplate GetOrParse(Hash128 contentHash, string text, string displayPath);    // throws TemplateParseException(displayPath, line, column, reason): "<displayPath>(<line>,<col>): <reason>"; BuildError maps it to a location
    public ValueTask<LoadedTemplate?> LoadAsync(FPath relativePath, DependencyTracker deps, CancellationToken ct);   // searches the includes folders in order; reads the file, strips front matter, caches by path+hash; records the dependency
    public void Invalidate(FPath sourcePath);   // watch mode

    // ITemplateFileProvider: resolves {% include %} / {% render %} names (adds ".liquid" when there is no extension, then tries ".html") and records the dependency through the context's tracker.
    // ITemplateCache: TryGetTemplate/SetTemplate keyed by path; the DateTimeOffset argument is ignored because the loader keys on content hash.
}

internal sealed record ParsedTemplate(IFluidTemplate Template, Hash128 Hash);
internal sealed record LoadedTemplate(FPath Path, ParsedTemplate Template, NodeObject FrontMatter, Hash128 ContentHash);
```

Templates are parsed once per content hash, which also deduplicates identical page templates used
by thousands of paginated pages (gap B5). The cache is a `ConcurrentDictionary<Hash128, Lazy<...>>`.

### 2.9 Progress view and trace recorder interfaces

Both interfaces exist from phase 1 with simple implementations so the scheduler is instrumented
from the start. Phase 4 adds the Spectre view and the file writers.

`Douze/Progress/IProgressView.cs` (namespace `Douze.Progress`: a `Douze.Console` namespace would hide `System.Console` everywhere under `Douze`)

```csharp
internal enum LogLevel { Debug, Info, Warning, Error }

internal interface IProgressView : IDisposable
{
    void BuildStarted(WorkflowGraph graph);
    void StepStarted(StepRun run);
    void StepProgress(StepRun run);          // UnitsDone/Units changed; views throttle to 20 redraws per second
    void StepCompleted(StepRun run);
    void Message(LogLevel level, string? step, string message);
    void BuildCompleted(BuildResult result); // summary table
}
```

`PlainProgressView` (phase 1): prints one line per step completion
(`✓ markdown        312 files   0.41 s`, `✗ render   error ...`, `- og-images  skipped`), messages
at Info and above, and a summary with totals, errors and elapsed time. `--verbose` adds one line per
unit. `SilentProgressView` prints errors only. Unicode markers are replaced by `ok`, `FAIL`, `skip`
when `Console.OutputEncoding` is not UTF-8.

`Douze/Tracing/ITraceRecorder.cs`

```csharp
internal interface ITraceRecorder
{
    TraceSpan Begin(string name, string category, string? unit = null);   // category: "build" | "step" | "unit" | "command" | "io"
    void Instant(string name, string category, string? args = null);
    TraceSession Session { get; }
}

internal readonly struct TraceSpan : IDisposable   // Dispose() ends the span; AddArg(string key, string value)
internal sealed class TraceSession                 // thread safe append-only list of TraceEvent(name, category, unit, start ticks, duration ticks, thread id, args)
```

`TraceRecorder` records step and command spans always, and unit spans only when
`BuildOptions.TraceUnits` is true (`--trace-units`), because 32k spans of a few microseconds each
are noise in the timeline. `NullTraceRecorder` is used by tests.

### 2.10 Entry points, options and results

`Douze/SiteOptions.cs`

```csharp
internal sealed class SiteOptions
{
    public required IFileSystem Source { get; init; }
    public required IFileSystem Output { get; init; }
    public string? OutputDirectory { get; init; }       // absolute disk path when Output is physical; expands {{ site }} in commands
    public string? SourceDirectory { get; init; }       // absolute disk path when Source is physical; default cwd for commands
    public FPath ContentFolder { get; init; } = "/_content/";   // FPath.Root when the folder does not exist
    public FPath DataFolder { get; init; } = "/_data/";
    public FPath IncludesFolder { get; init; } = "/_includes/";
    public string? Url { get; init; }                   // site.url from douze.yml, exposed to templates as site.url (phase 2)

    public static Task<SiteOptions> DetectAsync(IFileSystem source, IFileSystem output, string? sourceDir, string? outputDir);   // today's "_content exists else /" rule, applied to all three folders (fixes gap A30)
}
```

`Douze/Workflow/BuildOptions.cs`

```csharp
internal sealed class BuildOptions
{
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public FailurePolicy Failure { get; init; } = FailurePolicy.Default;
    public bool Verbose { get; init; }
    public bool TraceUnits { get; init; }
    public string? TracePath { get; init; }          // --trace <file.json>
    public string? TimelinePath { get; init; }       // --timeline <file.html>
    public IProgressView? View { get; init; }        // null: chosen by Program (section 6.2)
    public ITraceRecorder? Trace { get; init; }
    public string? CacheDirectory { get; init; }     // default: <source>/.douze/
    public bool UseCache { get; init; } = true;      // --no-cache (phase 5)
    public CancellationToken Cancellation { get; init; }
}
```

`Douze/Workflow/BuildResult.cs`

```csharp
internal sealed class BuildResult
{
    public bool Succeeded { get; }                        // no errors and not cancelled
    public bool Cancelled { get; }
    public IReadOnlyList<BuildError> Errors { get; }
    public IReadOnlyList<StepRun> Steps { get; }          // topological order
    public IReadOnlyList<VirtualFile> Outputs { get; }    // files that reached `write` steps, sorted by path
    public ScopeSet Scopes { get; }                       // final merged scopes (all contributions)
    public TraceSession Trace { get; }
    public TimeSpan Elapsed { get; }

    // Test helpers
    public IReadOnlyList<Metadata> Collection(string name);      // from the "step:<collect step>" contribution; empty when absent
    public Node? Resolve(Metadata metadata, string key);          // Scopes.TryResolve without tracking
}
```

`Douze/Workflow/SiteBuilder.cs`

```csharp
internal static class SiteBuilder
{
    public static Task<BuildResult> BuildAsync(WorkflowDefinition workflow, SiteOptions site, BuildOptions options);
    public static Task<BuildResult> BuildAsync(WorkflowGraph graph, SiteOptions site, BuildOptions options);
}
```

`Douze/Cli/CommandLine.cs` keeps a hand-written parser (no new dependency):

```
douze [build] [options]          build once (default command)
douze watch [options]            rebuild on changes (phase 6)
douze serve [options]            watch + dev server with live reload (phase 6)
douze worker-helper              print douze-worker.mjs to stdout (phase 3)

Options:
  -s, --source <dir>             source folder (default: .)
  -o, --output <dir>             output folder (default: _site)
  -w, --workflow <file>          workflow file (default: <source>/douze.yml when it exists)
  -p, --parallelism <n>          max parallel units (default: processor count)
      --keep-going               report every error instead of stopping the failing step
      --fail-fast                stop everything on the first error
      --trace <file>             write a Chrome trace event file
      --timeline <file>          write a self-contained timeline.html
      --trace-units              include one span per file or group in the trace
      --plain                    one line per step, no live view
  -q, --quiet                    errors only
  -v, --verbose                  one line per file, stack traces
      --print-workflow           print the effective workflow as YAML and exit (phase 2)
      --no-cache                 ignore and do not write the disk cache (phase 5)
      --cache-dir <dir>          cache location (default: <source>/.douze)
      --port <n>                 dev server port (serve, default 8080)
  -h, --help                     show the help
      --version                  show the version
```

`-c`, `-d` and `-i` are removed: the folders come from `douze.yml` (`site:` section, phase 2) or
from the detection rule. `README.md` is updated in phase 1 and again in phase 2.

`Douze/Cli/WorkflowSource.cs` turns the command line into the site and the workflow of a build:
`LoadAsync(CommandLine, sourceDirectory)` loads `--workflow` (relative to the current folder; a missing file is an
error) or `douze.yml` at the source root when it exists, resolves the output folder (`CommandLine.Output` relative
to the current folder, else `site.output` relative to the source folder, else `_site`), builds the `SiteOptions`
with `SiteOptions.DetectAsync(..., SiteSettings)` (folders from `site:`, detection for the others), and returns the
file's `steps`, or `DefaultWorkflow.Create(site)` when the file has no `steps`. Error locations name the file
relative to the current folder (`Sample/douze.yml(3,5)`), or by its full path when it is outside. `SiteOptions.Parallelism` (from `site.parallelism`) applies when `-p` is absent.
`BuildCommand` only builds the graph and runs it.

### 2.11 The built-in steps

All steps are in `Douze/Workflow/Steps/`. Each lists its native shape, default match, `with`
options, and exact behavior. "Own" means `file.Metadata.Own`.

**`data`** (reduce, default match: all). Parses every consumed file by extension (`.yml`, `.yaml`,
`.json`, `.xml`) with the existing helpers (streaming from `OpenReadAsync`), emits no files, and
contributes scopes.
- `with.scope: site | folder` (required).
- `with.root: <dir>` (default: the site's data folder for `site`, the content folder for `folder`).
- `site`: the key of a file is its path relative to `root`, without extension, with each directory
  level becoming a nested object: `_data/colors.json` -> `colors`, `_data/pkgs/Aspire.Hosting.json`
  -> `pkgs["Aspire.Hosting"]`. Two files mapping to the same key is an error
  ("data key 'colors' is defined by both '/_data/colors.json' and '/_data/colors.yml'"). A file
  whose root is a scalar or an array is stored as is. Keys are recorded in `SiteScope.KeySources`.
  Contribution: `SiteScope("step:<name>", values, keySources)`.
- `folder`: files are sorted by path and each one's top-level keys are merged into the
  `FolderScope` of its directory (later files override earlier keys, which is today's behavior
  made deterministic). Contribution: `Folders`.
- Both modes parse in parallel (`Parallel.ForEachAsync` over the sorted list, results stored by
  index) and assemble sequentially.

**`frontmatter`** (map, default match: `**/*.md`, `**/*.html`, `**/*.liquid`). Parses the YAML front
matter with `FrontMatter.Parse` (today's `FrontMatterHelper`), merges it into Own (arrays replaced,
as today), strips the header from the content, sets `page.date` to `file.LastWriteTimeUtc` when it
is set (the date of the source file, kept through `With`), before merging the front matter (so a
front matter `page` object keeps `date`). `ContentType` stays null (derived from the extension).
The merge never changes a node the file shares: nested objects are merged into shallow clones. A
file without front matter keeps its content instance. Non-text files are not matched by default and
pass through when matched. Options: none.

**`parse`** (map, default match: `**/*.json`, `**/*.yml`, `**/*.yaml`, `**/*.xml`). Parses the whole
content into Own under `with.as` (default `data`), keeps the content. Used by data-driven workflows
such as the aspire.dev API pages (one JSON file becomes the model of a template).

**`markdown`** (map, default match: `**/*.md`). Renders with a `MarkdownPipeline` built once in the
constructor from `with.extensions` (`none` default, which is today's bare pipeline; `advanced` maps
to `UseAdvancedExtensions()`; a list maps to the Markdig extension names through
`MarkdownPipelineBuilder.Configure`). Output: path extension changed to `.html` (`post1.md` ->
`post1.html`), so the derived content type is `text/html`. Changing the extension
here, rather than in `permalink` as today, is what lets later steps match on `**/*.html`.

**`paginate`** (map 1 to n, default match: `**/*.html`, `**/*.md`). Reproduces `PaginationProcessor` on top of
scopes: `pagination.data` names a key resolved through `Scopes.TryResolve`; `size`, `alias`,
`resolve: values` behave as today. For each page it creates `file.With(...)` with Own =
shallow clone + `pagination` (`items`, `pageNumber`, `pages`, `page.next/previous/first/last`) +
alias. New in this step (gap B2/A13): each page's `permalink` is resolved **now**, with the page's
`pagination` and alias in scope, through `PermalinkResolver.ResolveAsync`, and stored as a concrete
`permalink` string in Own. When the file has no `permalink`, the resolved value is today's
`GeneratePagedPath` (`/products/index.html`, `/products/1/index.html`); more generally, when every
page resolves to the same path (no `permalink`, or one that does not depend on the page), pages get
paged paths of that path. Each page file gets its resolved path right away. All page hrefs are stored
in `pagination.hrefs` (array) so that templates can link pages. `pagination.pages` and
`pagination.page.next/previous/first/last` are page summaries (`items`, `pageNumber`, `href`) shared
by every page: linking the full pagination objects as before would form cycles that `Metadata.Hash`
cannot follow. The hrefs are only known once every page's `permalink` is resolved, so a `permalink`
that reads `pagination.hrefs` or a page's `href` sees them empty. `pagination.data` may be a dotted
path (`collections.post`, when the step runs after `collect`, as in the default workflow since the
integration of phases 2 to B, open question 19): the first segment resolves through the scopes. Each
page gets its `url` (the HTML path, `PermalinkResolver.Url`), so the step can run after `permalink`. A `pagination.data` that resolves to
nothing passes the file through unchanged, as before. `with.root` (default: the content folder) as
for `permalink`. Files without `pagination` pass through.

**`permalink`** (map, default match: all files). Computes the output path with
`PermalinkResolver.ResolveAsync(file, metadata, context, root)`: renders the `permalink` template
(resolved through the scopes, parsed once per distinct template string through
`TemplateLoader.GetOrParse`), applies today's rules (no extension means a folder with `index.html`;
without `permalink`, `index.html` stays in place and other `.html` files become `name/index.html`,
and files that are not HTML keep their path), strips `with.root` (default: the content folder) from
the front, sets Own `url` to the result (an absolute path, `/blog/post/index.html`), and returns
`file.With(path: result)`. `permalink: false` drops the file (returns `FileList.None`, gap A17).
Matching every file is what removes the content folder from the paths of images and other
passthrough files. A Markdown file is a page whose content is not HTML yet: its HTML output path
keeps the `.md` extension (`post.md` -> `/post/index.md`) and `url` is the `.html` path, so that
`markdown`, which changes the extension, can run after `permalink` (4.6); a `permalink` that is not
HTML (`feed.xml`) is kept as is.

`PermalinkResolver` (`Douze/Workflow/Steps/PermalinkResolver.cs`) is shared by `paginate` and
`permalink` so both produce identical paths.

**`collect`** (reduce, default match: `**/*.html`, `**/*.md`). Builds `collections.all` and `collections.<tag>`
from the sorted input: each item is `NodeObject { ["data"] = new NodeValue<Metadata>(file.Metadata) }`,
items are sorted by date (front matter `date`, else `page.date`, resolved through scopes), then by
path (ordinal; the path entering the step, the output path in the default workflow), exactly as today.
`tags` resolve through the scopes too (a folder can tag its pages), and a tag listed twice adds the
file once. Emits **no files** and contributes
`SiteScope("step:<name>", { ["collections"] = collectionsObject })`. The `collections` hash is the
hash of the sorted item refs.

**`liquid`** (map; group when `with.template` is set and `group-by` is present; default match:
`**/*.html`, `**/*.md`, `**/*.json`). A file whose `liquid` resolves (through the scopes) to `false`
passes through unchanged, without `with.template` (the per-page opt-out of open question 3).
- Without `with.template`: parses the file content once per content hash, renders with a
  `TemplateContext(new MetadataValue(file.Metadata), options)` and the ambient values of 2.8,
  `HtmlEncoder.Default`, collects captured variables (`context.Captured`) into a new Own, returns
  `file.With(content: html, metadata: newMeta)`. Parse and render errors become `BuildError`s with
  the file path and line/column.
- With `with.template: <includes-relative path>`: the template is loaded through
  `TemplateLoader.LoadAsync` and rendered once per unit. In map mode the unit is a file, exposed as
  `file` (`path`, `name`, `dir`, `ext`, `content`, `data`); in group mode the unit is a group,
  exposed as `group` (`key`, `files`). The model of the render resolves other names through the
  scopes (site data), with the directory of the (first) file. The output path is `with.permalink`
  (required, a Liquid template rendered with the same model; no extension means a folder with
  `index.html`), the content type comes from the resulting extension. Output Own = `{ url }` plus
  `with.data` entries if any (a mapping). `group-by` is only supported with `with.template`.
- `with.encode: html | none` (default `html`) selects the encoder.

**`layout`** (map, default match: `**/*.html`). Resolves `layout` through scopes (so a global default
in the data folder works, as today). Loads the layout through `TemplateLoader.LoadAsync` from
`with.includes` (default: the includes folder), renders it with
`new MetadataValue(file.Metadata, fallback: layout.FrontMatter)` and `content` set in the context,
chains to the layout's own `layout` front matter key, and returns the final HTML. Precedence is
own, folder, site, then layout front matter (the innermost layout's first), which is today's
effective order. `content` is set unencoded. Missing layout: `BuildError` "layout 'x.html' not found
in '/_includes/' (used by '/index.md')", where the source path comes from `VirtualFile.SourcePath`. A
layout chain longer than 32 is an error ("layout chain is longer than 32: a.html -> b.html -> ...").

**`write`** (map, default match: all). Creates the directory, writes the content to the output file
system, and passes the file through so that later steps (`needs: write`) can run. Duplicate output
paths are detected with a `ConcurrentDictionary<FPath, VirtualFile>` on the step instance (cleared in
`InitializeAsync`); the error names both source paths in ordinal order ("output '/a/index.html' is
produced by both '/_content/a.html' and '/_content/a.md'"), or the producing step for a derived file. In watch mode (phase 6) it skips files whose content hash equals the last
written hash and deletes files that disappeared.

Files that no step claims (css, js, images) flow from `frontmatter` (not matched, passed through)
down to `write` untouched. This is the passthrough copy of gap A1.

### 2.12 Dependencies

| Package | Why | AOT |
|---|---|---|
| `System.IO.Hashing` (Microsoft, latest stable) | `XxHash128` for content, metadata and key hashes; about 10 times faster than SHA-256, non cryptographic is fine for cache keys | Pure managed, AOT compatible, no warnings |
| `Spectre.Console` 0.57.2 (phase 4), plus `Spectre.Console.Testing` in the test project only | Live progress view and summary table | Verified empirically on 2026-10-21: `dotnet publish -r osx-arm64` with `PublishAot=true` produced zero IL warnings, a 3.4 MB binary, and the `Progress` and `Table` renderers worked both in a terminal and with redirected output (where Spectre skips the live rendering on its own) |

No other package is added. Globbing is implemented in `Glob.cs` (section 4.4) instead of
`Microsoft.Extensions.FileSystemGlobbing` because its semantics must apply to virtual paths and
include negation, and it is about 150 lines. The command line stays hand parsed. The dev server
(phase 6) uses `System.Net.HttpListener` from the shared framework; its AOT compatibility is the
first task of phase 6, with a raw `TcpListener` server as the fallback (section 11).

---

## 3. Migration map

### 3.1 Processors to steps

| Today | Becomes | Notes |
|---|---|---|
| `SiteHelper.BuildSiteAsync` enumeration + data pass + folder merge | `Scheduler` source enumeration + `data` step (`scope: site` and `scope: folder`) | Folder data is a scope, not merged into each file |
| `XmlDataFileProcessor`, `YamlDataFileProcessor`, `JsonDataFileProcessor` | `DataStep` | Same parsers (`XmlHelper`, `YamlHelper`, `JsonHelper`) |
| `PageMetadataProcessor` | `FrontMatterStep` sets `page.date` | `VirtualFile.LastWriteTimeUtc` captured at enumeration |
| `FrontMatterProcessor` + `FrontMatterHelper` | `FrontMatterStep` + `FrontMatter.Parse` | Same parsing code |
| `MarkdownFileProcessor` | `MarkdownStep` | Renames `.md` to `.html` |
| `UrlFileProcessor` | `PermalinkStep` + `PermalinkResolver` | Runs after pagination |
| `PaginationProcessor` | `PaginateStep` | No deep clone; per-page permalinks |
| `CollectionsProcessor` | `CollectStep` | Items wrap `Metadata`; scope contribution |
| `LiquidTemplateFileProcessor` | `LiquidStep` | Parse cache, scope model, explicit value conversion |
| `LayoutFileProcessor` + `LayoutHelper` | `LayoutStep` + `TemplateLoader` | Front matter of layouts becomes the fallback scope |
| `CopyDocumentProcessor` + `IDocument.SaveFileAsync` | `WriteStep` | Every unclaimed file is written (gap A1) |
| `Options`, `Options.Liquid` | `SiteOptions`, `BuildOptions`, `LiquidOptions` | `PropertyBag`, `SiteData`, `MaxDegreeOfParallelism` DEBUG default removed |
| `Arguments` | `Cli/CommandLine` | `-c/-d/-i` removed |
| `IncludeRule` | `Glob`, `GlobSet` | Standard glob semantics (4.4) |
| `Results/*` (`IDocument`, `IResult`, `TextDocument`, ...) | `VirtualFile`, `FileContent`, `FileList` | |
| `Liquid/LiquidParser.cs` | deleted | Dead commented code |

### 3.2 Deleted in phase 1b

`Douze/FileProcessors/` (all 12 files), `Douze/Results/` (all 8 files), `Douze/SiteHelper.cs`,
`Douze/LayoutHelper.cs`, `Douze/Options.cs`, `Douze/Options.Liquid.cs`, `Douze/Arguments.cs`,
`Douze/Configuration/IncludeRule.cs`, `Douze/Liquid/LiquidParser.cs`, `Douze/FrontMatterHelper.cs`
(renamed to `FrontMatter.cs`). `Douze/todo.txt` is rewritten to list what remains after this plan.

### 3.3 Tests

The 36 existing tests: 15 in `CommonTests`, 8 file system tests (4 in `FilesystemBaseTest` run by
`MemoryFileSystemTest` and `PhysicalFileSystemTest`), 5 in `IncludeRuleTests`, 8 in `NodeTests`.

- `NodeTests` (8) and the file system tests (8): unchanged.
- `IncludeRuleTests` (5): become `GlobTests` with the same intent but standard semantics: `*.json`
  matches at the root only, `**/*.json` at any depth, `config.json` at the root only, `/config.json`
  equals `config.json`. The five tests are kept with adjusted expectations and five more are added
  (`{a,b}`, `?`, negation order, `**` in the middle, directory prefix `content/**`).
- `CommonTests` (15): rewritten against the new API through a small helper in
  `Douze.Tests/SiteTestBase.cs`:

```csharp
protected MemoryFileSystem Source, Output;
protected SiteOptions Site;
protected Task<BuildResult> BuildAsync(WorkflowDefinition? workflow = null, BuildOptions? options = null);
// workflow defaults to DefaultWorkflow.Create(Site); options default to a NullTraceRecorder, SilentProgressView and MaxDegreeOfParallelism 4
```

  Every assertion on output files stays identical. The three collection tests replace
  `_options.SiteData["collections"]?["article"]?.AsArray()` with `result.Collection("article")` and
  read titles with `item.Own["title"]`. `ShouldLoadDataFiles` and `ShouldProcessFilesInCustomFolders`
  write `/_data/layout.yml` with content `layout.html` instead of `theme.yml` with `layout: layout.html`,
  because data is keyed by file name (section 2.11, `data`). `ShouldProcessFilesInCustomFolders`
  builds `Site` with the custom folders instead of passing them to `Options`.

New tests per phase are listed in section 8.

---

## 4. The YAML workflow format

### 4.1 File and discovery

`douze.yml` at the source root, or the file given with `--workflow`. The file is optional. It has
two top-level keys, both optional: `site` and `steps`. When `steps` is absent, the built-in default
workflow is used with the `site` settings.

### 4.2 `site`

```yaml
site:
  content: _content      # folder whose files become pages; default: _content if it exists, else /
  data: _data            # global data folder; default: _data if it exists, else /
  includes: _includes    # layouts, includes, templates; default: _includes if it exists, else /
  output: _site          # overridden by -o
  url: https://example.com   # exposed as site.url in templates
  parallelism: 8         # overridden by -p
```

Every path is relative to the source root. Paths are normalized to `FPath` directories (`docs`, `/docs/` and
`docs/` are `/docs/`; `.` and `/` are the root); a folder containing `..` is an error. A folder set here is used
whether or not it exists. `site.output` may be absolute. The loader returns the section as `SiteSettings`
(`Douze/Workflow/Yaml/WorkflowFile.cs`), which `SiteOptions.DetectAsync` applies.

Templates see `site.url` (the only setting they see: the others configure the build) through the base
scope of every step (`BuildContext.BaseScopes`, `ScopeSet.FromSettings`), below every contribution.
Unlike other scopes, it does not hide behind a hit of the same key: when a site data file
`_data/site.json` (or any scope) defines `site` as an object, its properties are layered over the
settings' `site` object (a shallow clone, built once per object and scope set), so `site.title` comes
from the data file and `site.url` from `douze.yml` unless the data file defines `url` too. A `site`
that is not an object (a string in front matter) hides the setting, as usual. A resolution of an
object records the settings scope (`settings`) as a dependency.

### 4.3 `steps`

`steps` is a mapping from step name to step. Names match `^[a-z][a-z0-9-]*$` and are at most 64
characters. Each step has these fields:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `needs` | string or list | `[]` | Steps whose outputs (files and scopes) enter this step |
| `inputs` | glob or list | `[]` | Source files that enter this step (section 4.4) |
| `match` | glob or list | step default | Entering files consumed by the step; the rest pass through unchanged |
| `uses` | string | | Built-in step id. Exactly one of `uses` and `run` is required |
| `with` | mapping | `{}` | Options of the built-in step |
| `run` | string | | Command line (section 5). Liquid expressions `{{ inputs }}`, `{{ outputs }}`, `{{ site }}`, `{{ step }}`, `{{ file.path }}` (filter mode) |
| `mode` | `filter`, `batch`, `worker` | `batch` | Command mode (section 5); only with `run` |
| `group-by` | `file`, `dir`, or a Liquid template | | Partitions consumed files into parallel groups |
| `outputs` | glob or list | `[]` | Batch mode: files read back from the outputs folder; empty means every file |
| `incremental` | bool | `true` | `false` forces the step to re-run on every build (phase 5); valid on any step |
| `version` | string | `""` | Free text that participates in the command's cache key |
| `sources` | glob or list | `[]` | Source files whose hashes participate in the command's cache key (the script the command runs) |
| `env` | mapping | `{}` | Environment variables for the command; values accept the same expressions as `run` |
| `cwd` | string | source root | Working directory of the command, relative to the source root |
| `timeout` | duration | none | `500ms`, `30s`, `5m`, `1h` (a positive integer and a unit); the command is killed and the step fails |
| `stderr` | `fail`, `warn`, `ignore` | `fail` | What stderr output from the command means |
| `shell` | bool | `true` | Run through `/bin/sh -c` (or `cmd.exe /d /s /c` on Windows); `false` splits the line with quote rules and executes directly |

### 4.4 Globs

`Douze/Workflow/Glob.cs` and `GlobSet.cs`.

```csharp
internal sealed class Glob
{
    public static Glob Parse(string pattern);          // throws FormatException with the offending position
    public bool Negated { get; }
    public string Pattern { get; }
    public bool IsMatch(FPath path);                   // path is absolute; the leading '/' is ignored
}

internal sealed class GlobSet
{
    public static readonly GlobSet All;                // matches everything
    public static GlobSet Parse(IEnumerable<string> patterns);
    public bool IsMatch(FPath path);
    public string? FixedPrefix { get; }                // longest directory prefix shared by all positive patterns, used to narrow enumeration
}
```

Syntax: `*` matches anything except `/`; `**` matches anything including `/` (as a whole segment or
in `prefix/**/suffix`); `?` one character except `/`; `{a,b}` alternatives (nested allowed);
`[abc]`, `[a-z]`, `[!abc]` character classes; `\` escapes the next character; a leading `/` is
ignored; a leading `!` negates. Matching is case sensitive and ordinal. A `GlobSet` evaluates
patterns in order and the **last** matching pattern decides; a set whose first pattern is negated
starts from "everything". `["content/**", "!**/*.yml"]` matches everything under `content/` except
YAML files. Patterns compile to `Regex` with `RegexOptions.CultureInvariant` (interpreted; AOT safe).

### 4.5 Validation rules and messages

Validation happens in `WorkflowLoader` (YAML shape) and `WorkflowGraph.Build` (semantics). Every
error is a `WorkflowValidationException` with `Location` (file, line, column of the offending node,
from SharpYaml's parsing event marks) and is printed as `douze.yml(12,5): error: <message>`. All
errors are collected and printed together before the build stops with exit code 2. Loader errors point at the
offending node (the key for an unknown key or a field that is not allowed, the value for a wrong type, the list
item for a bad glob) and are sorted by position. Graph errors, and the option errors raised by the steps, point at
the step's name (`StepDefinition.Location`), since the definition keeps one location per step.

Loader rules:
- Unknown top-level key: `unknown key 'jobs'; expected 'site' or 'steps'`.
- `steps` not a mapping: `'steps' must be a mapping of step names to steps`.
- Invalid step name: `invalid step name 'My Step': use lowercase letters, digits and dashes`.
- Unknown step field: `unknown field 'if' on step 'render'` (the scope guard: no conditions).
- Wrong type: `'needs' must be a string or a list of strings`.
- `uses` and `run` both present: `step 'x' cannot have both 'uses' and 'run'`.
- Neither present: `step 'x' needs 'uses' or 'run'`.
- `mode`, `outputs`, `env`, `cwd`, `timeout`, `stderr`, `shell`, `version`, `sources` without `run`:
  `'mode' is only valid with 'run'`.
- `with` without `uses`: `'with' is only valid with 'uses' or with 'mode: worker'` (worker steps receive it in the handshake, section 5.3).
- Invalid glob: `invalid pattern 'content/[': unterminated character class`.
- Invalid duration: `invalid timeout '5 minutes'; use 30s, 5m or 1h`.
- Reserved name: `'source' is a reserved step name`.
- Also: `a workflow file must be a mapping with 'site' and 'steps'`, `duplicate key 'x'`, `keys must be strings`,
  `step 'x' must be a mapping of fields`, `step name '...' is longer than 64 characters`, `'mode' must be 'filter',
  'batch' or 'worker'` (and the same for `stderr`), `'uses' must be a string`, `'with' must be a mapping`,
  `'incremental' must be true or false`, `'env' must be a mapping of names to strings`, `unknown key 'x' in 'site';
  expected 'content', 'data', 'includes', 'output', 'url' or 'parallelism'`, `'parallelism' must be a positive
  integer`, `'content' must be a folder inside the source folder`, `a workflow file has a single YAML document`,
  and SharpYaml's syntax errors with their position (`while parsing a flow sequence, did not find expected ',' or ']'`).
- A field set to `null` (or left empty) is absent; `steps:` with no value is the same as no `steps`.

Graph rules:
- Unknown `needs`: `step 'render' needs unknown step 'markdwn'` (plus `did you mean 'markdown'?` when
  the Levenshtein distance is 2 or less).
- Cycle: `steps form a cycle: a -> b -> c -> a`.
- No `needs` and no `inputs`: `step 'x' has no 'needs' and no 'inputs', so nothing enters it`.
- Unknown `uses`: `unknown built-in step 'frontmater'` with the suggestion rule above.
- `group-by` on a step that does not support it: `step 'markdown' does not support 'group-by'`.
- Unknown `with` option: `unknown option 'templte' for step 'liquid'`, missing required option:
  `step 'api' uses 'liquid' with 'template' but 'permalink' is missing`.
- `outputs` on a non-batch command: `'outputs' is only valid with 'mode: batch'`.
- Graph with no `write` step: warning `no 'write' step: nothing will be written to the output folder`.

### 4.6 The default workflow

`DefaultWorkflow.Create(SiteOptions)` produces the equivalent of the following, with `_content`,
`_data` and `_includes` replaced by the detected folders. When the content folder is `/`, the
`inputs` of `pages` and `folder-data` also exclude the data and includes folders (when they are not
`/`), `douze.yml`, and the output folder (when it is inside the source), and `folder-data` and
`permalink` have no `root`. When there is no data folder (`/`), `site-data` is omitted: data files at
the root are the folder data of `/`, which applies to every file. This is what `douze build
--print-workflow` prints for a site with no `douze.yml` (`DefaultWorkflowTests` checks both cases).

```yaml
steps:
  site-data:   { inputs: "_data/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: site } }
  folder-data: { inputs: "_content/**/*.{yml,yaml,json,xml}", uses: data, with: { scope: folder, root: _content } }
  pages:       { inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"], needs: [site-data, folder-data], uses: frontmatter }
  permalink:   { needs: pages, uses: permalink, with: { root: _content } }
  collections: { needs: permalink, uses: collect }
  paginate:    { needs: [permalink, collections], uses: paginate }
  render:      { needs: paginate, uses: liquid }
  markdown:    { needs: render, uses: markdown }
  layout:      { needs: markdown, uses: layout }
  write:       { needs: layout, uses: write }
```

*Changed at the integration of phases 2 to B* (open questions 3 and 19, approved): Liquid runs before
Markdown (Eleventy's model), and collections are built before pagination, so a page can paginate
`collections.<tag>`. Before, the order was `pages -> markdown -> paginate -> permalink -> collections ->
render (needs permalink and collections) -> layout`. What makes the new order work:

- `permalink` runs on Markdown files before they are HTML: a `.md` page gets the HTML output path with
  the `.md` extension kept (`post.md` -> `/post/index.md`, `index.md` stays), and `url` is the HTML
  path (`PermalinkResolver.Url`). `markdown` still matches `**/*.md` and changes the extension to
  `.html`. A `.md` page whose `permalink` is not HTML (`feed.xml`) keeps that extension and is not
  rendered to HTML.
- `paginate`, `collect` and `liquid` match `**/*.md` by default, besides their HTML (and JSON) globs.
- `paginate` runs after `permalink`, so it sets each page's `url` itself (and its `href`s are URLs);
  the paginated file is in `collections.all` once, unpaginated, with the `url` of its first page
  (Eleventy's default). The barrier before `paginate` (it needs `collections`) replaces the one that
  was before `render`, so nothing streams later than before.
- Per-page opt-out of Liquid (gap A4's `templateEngine`, Eleventy's `templateEngineOverride: md`):
  `liquid: false`, resolved through the scopes, so front matter or a folder's data file sets it; the
  `liquid` step passes such a file through unchanged.

Data files are keyed by file name, which requires the Sample adaptations listed in phase 1b; the
reorder requires one more (the `footer` capture of `index.md` is written in HTML, since a capture now
holds Markdown source), and changes the Sample output as listed in "Integration of phases 2, 3, 4
and B" (section 8).

### 4.7 Example: the aspire.dev API pages

```yaml
site:
  content: content
  includes: templates
  data: data

steps:
  site-data:  { inputs: "data/*.{yml,json}", uses: data, with: { scope: site } }   # xref.json, api-pages.json
  packages:   { inputs: "data/pkgs/*.json", uses: parse, with: { as: package } }
  csharp:     { needs: [packages, site-data], uses: liquid, group-by: file,
                with: { template: api/csharp-package.liquid, permalink: "reference/api/csharp/{{ group.files[0].data.package.slug }}/" } }
  csharp-md:  { needs: [packages, site-data], uses: liquid, group-by: file,
                with: { template: api/csharp-package.md.liquid, permalink: "reference/api/csharp/{{ group.files[0].data.package.slug }}/index.md", encode: none } }
  pages:      { inputs: ["content/**", "!content/**/*.{yml,json}"], needs: site-data, uses: frontmatter }
  render:     { needs: pages, uses: liquid, match: "**/*.md" }   # Liquid before Markdown for this site
  markdown:   { needs: render, uses: markdown, with: { extensions: advanced } }
  permalink:  { needs: markdown, uses: permalink, with: { root: content } }
  collections:{ needs: permalink, uses: collect }
  layout:     { needs: [permalink, collections], uses: layout }
  og-images:  { needs: pages, run: "node scripts/og.mjs --in {{ inputs }} --out {{ outputs }}", mode: batch, outputs: "og/*.png", sources: scripts/og.mjs }
  write:      { needs: [layout, csharp, csharp-md, og-images], uses: write }
  search:     { needs: write, run: "npx pagefind --site {{ site }}" }
```

One page per type rather than per package is a `group-by` Liquid template over a per-type page
index (`data/api-pages.json`) with `paginate`; the gap analysis recommends that the generators emit
page-shaped data, so the plan does not add nested pagination.

---

## 5. Command steps (`run:`)

All command steps live in `Douze/Workflow/Commands/`. `CommandStepFactory.Create` returns
`FilterCommandStep`, `BatchCommandStep` or `WorkerCommandStep` from `Definition.Mode`. They share:

- `CommandTemplate` (`CommandTemplate.cs`): parses the `run` string and every `env` value once with
  Fluid and renders them per unit with a model of `inputs`, `outputs`, `site`, `step`, `cwd` and,
  in filter mode, `file` (`path`, `name`, `dir`, `ext`). *As implemented (phase 3):* a value written
  directly in `run` quotes itself when it contains anything else than letters, digits and `_-./:@+=,`:
  single quotes for `/bin/sh` (`'\''` for a quote), double quotes for `cmd.exe`, and the quote rules of
  `ProcessRunner.Split` for `shell: false`; the result of a filter is a plain string, written as it is;
  `env` values are never quoted. `{{ inputs }}` and `{{ outputs }}` are only valid in batch mode and
  `{{ file }}` only in filter mode (a worker's `run` is rendered once): validation errors otherwise,
  found by walking the template with Fluid's `AstVisitor`. Folder paths have no trailing separator, so
  that a quoted Windows path never ends with `\"`.
- `ProcessRunner` (`ProcessRunner.cs`): starts the process with `System.Diagnostics.Process`
  (`UseShellExecute = false`, redirected streams, UTF-8), applies `cwd`, `env`, `timeout`, and
  returns `CommandResult(int ExitCode, ReadOnlyMemory<byte> Stdout, string Stderr, TimeSpan Elapsed)`.
  Each process is a `command` trace span with `command`, `elapsed` and `exitCode` arguments.
  `DOUZE_STEP`, `DOUZE_SITE` and `DOUZE_SOURCE` are always set in the environment (empty when the
  site has no folder on disk), after the step's `env`. The shell is `/bin/sh -c <line>` or
  `%ComSpec% /d /s /c "<line>"` (the line goes in `Arguments` as typed, not `ArgumentList`, which
  would escape its quotes for the C runtime); `shell: false` splits the line (whitespace, `'...'`
  literal, `\"` inside `"..."`, backslashes literal elsewhere for Windows paths). A `cwd` that does
  not exist fails the step before any process starts.
- Failure rules: a non-zero exit code fails the unit with
  "command exited with code 2 for '/blog/a.md': <last 20 lines of stderr>"; stderr output with
  `stderr: fail` (the default) fails the unit the same way even when the exit code is 0;
  `stderr: warn` forwards it as a warning message; `stderr: ignore` drops it. A timeout kills the
  process tree (`Process.Kill(entireProcessTree: true)`) and fails the unit.
- Caching (phase 5): the command version is `Hash128.Combine(hash of version, hashes of files matching sources)`.
  `batch` units are keyed by all their inputs, so any input change re-runs the whole batch; `filter`
  and `worker` units are keyed per file; `incremental: false` disables reuse.

### 5.1 `filter` (map)

One process per consumed file, up to `MaxDegreeOfParallelism` at a time (bounded by
`BuildContext.ProcessSlots`, see 2.6). The file content goes to stdin as bytes; stdout is read
fully as bytes and becomes the new content; the path is unchanged; the content type is derived from
the path. Environment:
`DOUZE_PATH` (virtual path), `DOUZE_CONTENT_TYPE`, `DOUZE_METADATA` (the file's Own metadata as
JSON, omitted when longer than 32,767 characters, the limit of one variable on Windows, in which case
`DOUZE_METADATA_FILE` points to a temp file deleted after the run).
Example:

```yaml
minify: { needs: layout, run: "npx html-minifier-terser --collapse-whitespace", mode: filter, match: "**/*.html" }
```

### 5.2 `batch` (reduce, or group with `group-by`)

One process per unit (the whole input, or one group). Before the run, when the command references
`{{ inputs }}`, every consumed file is written under `<TempRoot>/<step>/<unit>/in/<virtual path>`
and a manifest `<...>/in/douze-inputs.json` is written:

```json
{ "step": "og-images", "unit": "*", "files": [
  { "path": "/blog/a.html", "contentType": "text/html", "metadata": { "title": "A", "url": "/blog/a/" } } ] }
```

`{{ outputs }}` expands to `<...>/out`, created empty. *As implemented (phase 3):* `<unit>` is `all`
for the reduce unit and the first 16 hex digits of the group key's hash for a group (keys are paths
or rendered templates, not file names); the manifest's `unit` is `*` or the group key. stdout is
forwarded as debug messages (shown with `--verbose`). A `douze-outputs.json` entry for a file that
is not in `out/` is an error. After a successful run, files under `out/`
matching `outputs` (all files when `outputs` is empty) are read back as `VirtualFile.Derived(step,
inputs, "/" + relative path, ...)`. An optional `out/douze-outputs.json` with the same shape as the
inputs manifest attaches metadata to outputs. When the command uses neither `{{ inputs }}` nor
`{{ outputs }}` (the Pagefind case), nothing is materialized and the step emits no files; the
consumed inputs are not passed through (they were consumed). The temp folder is deleted after the
run unless `--verbose`. Temp paths are inside `BuildOptions.CacheDirectory/tmp/<build id>/`, so
they are on the same volume as the site and gitignored.

### 5.3 `worker` (map)

A long-running process handles files over JSON lines on stdin and stdout. `WorkerProcess`
(`WorkerProcess.cs`) owns one process per worker step; the step's `InitializeAsync` starts it and
`DisposeAsync` shuts it down. Concurrency: the worker announces how many requests it accepts in
flight; the step holds that many `ProcessSlots` at most.

*As implemented (phase 3):* `InitializeAsync` starts the process and sends `hello` without waiting
for `ready`, so the startup (seconds for twoslash) overlaps with the steps before; the first unit
waits for the handshake and reports its failure, and `DisposeAsync` reports it when no unit did.
The scheduler takes one `ProcessSlots` slot per unit, and `WorkerProcess` bounds the requests in
flight with its own semaphore of `min(ready.concurrency, hello.concurrency)`; a unit waiting for that
semaphore holds its process slot. `timeout` applies to each request, and a request that times out
kills the worker. Every failure of the worker (exit or stdout closed before `shutdown`, a line that is
not JSON, stderr with `stderr: fail`, a timeout) fails every request in flight and every later one
with the same message, and kills the process tree; one that no request reported is reported by
`DisposeAsync` (as an error without a file). stderr with `stderr: fail` waits 100 ms for more lines,
so the message has all of them; when the worker exits, the exit message carries stderr instead
(`worker exited with code 3: <last 20 lines>`). A worker that does not exit within the grace delay is
killed with a warning; a non-zero exit code after `shutdown` is an error. A `log` goes to the progress
view with the step name; a worker's `error` with `line`/`column` is reported as
`<message> (line 12, column 3)` (and ` in '<file>'` when `file` is another file), because
`BuildError.FromException` gives a location only to Liquid errors. The messages are written with
`Utf8JsonWriter` (relaxed escaping) and read with `JsonNode`: no serializer, nothing to source
generate.

Protocol (`WorkerProtocol.cs`, version 1). Every message is one JSON object per line, UTF-8, `\n`
terminated, with a `type` field. Douze writes to the worker's stdin, the worker writes to stdout.
Anything the worker writes to stderr follows the `stderr` policy. Message types:

1. Handshake. Douze sends `hello` first; the worker must answer `ready` within 30 seconds.

```json
{"type":"hello","protocol":1,"step":"twoslash","concurrency":8,"options":{},"site":"/abs/_site","source":"/abs/src"}
{"type":"ready","protocol":1,"concurrency":4,"name":"twoslash-worker"}
```

`options` is the step's `with` mapping (allowed on `run` steps with `mode: worker` only, section
4.5). `concurrency` in `ready` is the number of requests the worker accepts at once (default 1 when
omitted; Douze never sends more than `min(ready.concurrency, hello.concurrency)`).

2. Requests and responses. `id` is an opaque string; responses may arrive in any order.

```json
{"type":"file","id":"42","path":"/docs/a.md","contentType":"text/markdown","encoding":"utf8","content":"# Title\n...","metadata":{"title":"A"}}
{"type":"result","id":"42","files":[{"path":"/docs/a.md","encoding":"utf8","content":"# Title\n...","contentType":"text/markdown","metadata":{"twoslash":true}}]}
{"type":"error","id":"42","message":"TS2304: Cannot find name 'foo'","file":"/docs/a.md","line":12,"column":3}
```

`encoding` is `utf8` for text and `base64` for binary. A `result` may contain zero files (the input
is dropped), one, or many. Omitted `path` means the input path; omitted `metadata` means the input
metadata; `metadata` given is merged over Own (top-level keys). An `error` fails that unit only.
*As implemented (phase 3):* omitted `content` keeps the input content (a worker that only adds
metadata); omitted `contentType` keeps the input's when the path is unchanged and derives it from
the new path otherwise.

3. Progress and logs, optional, from the worker at any time:

```json
{"type":"log","level":"info","message":"loaded aspire.d.ts in 1.2 s"}
```

4. Shutdown. Douze sends `shutdown` after the last response; the worker exits 0 within 10 seconds
or is killed. A worker that exits or closes stdout before `shutdown` fails every in-flight unit with
"worker exited with code N" and the step fails.

```json
{"type":"shutdown"}
```

5. Unknown message types are ignored by both sides; a line that is not valid JSON fails the step
("worker wrote a line that is not JSON: <first 200 chars>").

Node helper `douze-worker.mjs`, embedded as a resource and printed by `douze worker-helper`. The
sketch below is what the plan started from; the real file (`Douze/Workflow/Commands/douze-worker.mjs`)
differs: after `shutdown` (or when stdin closes) it waits for the requests in flight and for stdout
to flush before exiting, instead of `process.exit(0)` at once, which could lose answers written to an
asynchronous pipe; it sends `contentType` only when the handler gives one (the request's type would
be wrong for a renamed file); a handler may return a string or a Buffer, an object whose members are
all optional, an array, or null; a thrown error is reported with its message (its `line`, `column` and
`file` properties when present), its stack going to a `debug` log.

```js
// douze-worker.mjs: minimal helper for Douze worker steps (protocol 1).
// usage: import { serve } from './douze-worker.mjs'; serve(async (file, ctx) => ({ content: transform(file.content) }), { concurrency: 4 });
import { createInterface } from 'node:readline';

export function serve(handler, { concurrency = 1, name = 'worker' } = {}) {
  const out = (msg) => process.stdout.write(JSON.stringify(msg) + '\n');
  const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });
  let ctx = {};
  rl.on('line', async (line) => {
    let msg;
    try { msg = JSON.parse(line); } catch { return; }
    switch (msg.type) {
      case 'hello':
        ctx = { step: msg.step, options: msg.options ?? {}, site: msg.site, source: msg.source, log: (message, level = 'info') => out({ type: 'log', level, message }) };
        out({ type: 'ready', protocol: 1, concurrency, name });
        break;
      case 'file': {
        const content = msg.encoding === 'base64' ? Buffer.from(msg.content, 'base64') : msg.content;
        try {
          const r = await handler({ path: msg.path, contentType: msg.contentType, metadata: msg.metadata ?? {}, content }, ctx);
          const files = r == null ? [] : Array.isArray(r) ? r : [r];
          out({ type: 'result', id: msg.id, files: files.map((f) => encode(f, msg)) });
        } catch (e) {
          out({ type: 'error', id: msg.id, message: String(e?.stack ?? e), file: msg.path });
        }
        break;
      }
      case 'shutdown':
        rl.close();
        process.exit(0);
    }
  });
}

function encode(f, msg) {
  const c = typeof f === 'string' ? f : f.content;
  const binary = Buffer.isBuffer(c);
  return { path: f.path ?? msg.path, contentType: f.contentType ?? msg.contentType, metadata: f.metadata,
           encoding: binary ? 'base64' : 'utf8', content: binary ? c.toString('base64') : c };
}
```

Example step:

```yaml
twoslash: { needs: render, run: "node scripts/twoslash-worker.mjs", mode: worker, match: "**/*.md", sources: ["scripts/twoslash-worker.mjs", "data/twoslash/aspire.d.ts"] }
```

### 5.4 Node startup cost

Starting Node costs about 50 ms. The plan's guidance, written into the README: `filter` for a
handful of files or non-Node tools, `batch` when the tool wants a folder (OG images, Pagefind),
`worker` for per-file work at scale (twoslash on thousands of fences).

---

## 6. Progress view, trace file and timeline

### 6.1 Spectre.Console under native AOT

Verified on 2026-10-21 with a scratch project on .NET 10 (`PublishAot=true`, `osx-arm64`):
Spectre.Console 0.57.2 publishes with zero IL warnings, the binary is 3.4 MB, and `Progress`,
`Table` and markup render correctly in an interactive terminal; with redirected output Spectre
detects the non-interactive profile and renders the final table only. The package is adopted in
phase 4. Phase 4 re-runs the check on the Douze project itself and on `linux-x64` in CI (the
existing `Publish douze (native AOT)` job).

Phase 4 result (2026-10-21, `osx-arm64`): the publish of Douze has no warnings; with every IL suppression lifted
(`-p:NoWarn=NU1507 -p:TrimmerSingleWarn=false`) it reports 31 IL warnings, all from Fluid (the ones `Douze.csproj`
suppresses) and none from Spectre.Console. The binary grows from 14.6 MB to 15.5 MB.

### 6.2 View selection

`ViewSelector` picks the view: `--quiet` gives `SilentProgressView`; `--plain`, a redirected stdout
(`Console.IsOutputRedirected`), `CI` set to anything but `false` or `0`, or `TERM=dumb` gives
`PlainProgressView`; otherwise `SpectreProgressView` on `AnsiConsole.Console`. `NO_COLOR` keeps the live view
(Spectre removes the colors on its own): it is about colors, not about cursor movement. Tests always pass a view
explicitly.

### 6.3 `SpectreProgressView` (phase 4)

A `Live` display of a `Table` with one row per running step, in graph order: spinner, step name, shape,
`done/total` (or `done/?` while streaming), a progress bar when total is known, elapsed time, and a caption
with the number of steps done and the build time. Completed steps leave the table and print one line
above it (same text as the plain view, `StepSummary.Line`), so a scrollback shows the history. Messages print
above the table too.

The scheduler calls the view from many threads, which only update its state under a lock. One loop owns
the console: every 50 ms (20 redraws per second) it prints the queued lines and redraws the table. Spectre
writes lines over the live area without clearing it, so a line shorter than a row would keep the end of
the row: the loop sets an empty target first when it has lines to print (the live shape only grows, so it
draws blank lines). A console that is not interactive (or has no ANSI support) gets the lines and the
summary without the live display.

On `BuildCompleted` the view stops the loop (the last pass prints the last lines and clears the table) and
prints the summary table: step, state, files in, files out, units, time, and a timeline column that draws
each step as a bar on the time scale of the build (the axis header goes from 0 to the build time; bars end
with an eighth of a character of precision), then the headline (`StepSummary.Headline`) and the error
list. The paths of the trace files are printed by `BuildCommand` after the summary, as Info messages,
because the files are written once the build result exists. Colors: green for succeeded, red for failed,
yellow for skipped, grey for cancelled.

### 6.4 Chrome trace events (phase 4)

`ChromeTraceWriter.Write(TraceSession, Stream)` emits the JSON Object Format:

```json
{"traceEvents":[
  {"name":"build","cat":"build","ph":"X","ts":0,"dur":2143000,"pid":1,"tid":0},
  {"name":"markdown","cat":"step","ph":"X","ts":12000,"dur":410000,"pid":1,"tid":1,"args":{"shape":"map","files":"312","state":"succeeded"}},
  {"name":"og-images","cat":"command","ph":"X","ts":430000,"dur":900000,"pid":1,"tid":3,"args":{"step":"og-images","command":"node scripts/og.mjs ..."}}
],"displayTimeUnit":"ms","otherData":{"douze":"1.0.0-beta","site":"/abs/src"}}
```

`ts` and `dur` are microseconds relative to the first event (the build span), rounded to the
nanosecond. `tid` is a lane: lanes are assigned greedily so that overlapping spans get distinct lanes
(sort by start, reuse the lowest lane whose last span ended before this start), which gives a compact
swimlane view in Perfetto and `chrome://tracing`. The build is lane 0, step spans share lanes 1 to n, and
every other span (units, commands, I/O) the lanes after those, so that units never push a step into a
far lane; `thread_name` and `thread_sort_index` metadata events name the lanes (`build`, `steps`,
`units`). `pid` is always 1. A step span runs from the step's start (its first file or unit, 2.6) to
its final state, and carries `shape`, `files` (consumed) and `state` (`succeeded`, `failed`, `skipped`
or `cancelled`); the scheduler sets `state` before the span ends. Command spans (a process of a
`filter` or `batch` step, the start of a worker) carry `step`, like unit spans, so the timeline packs
them under their step. Unit spans (with `--trace-units`) use `cat: "unit"`, the file or group as
`name` and the step as `args.step`. The writer uses `Utf8JsonWriter` directly (no serializer, nothing for
AOT to see), whose default encoder escapes `<` and `>`, so the JSON can sit in a `<script>` element.
`otherData` has `douze` (the version) and `site` (the source folder). The trace is written even when the
build fails or is cancelled.

### 6.5 `timeline.html` (phase 4)

`TimelineHtmlWriter.Write(TraceSession, Stream)` takes the embedded `Douze/Tracing/timeline.html`
and replaces the marker `/*__DOUZE_TRACE__*/null` with the same JSON as 6.4 (streamed, not built as a
string). The page opened as is shows an empty timeline. The page is self-contained: no external
scripts, fonts or styles. It draws one row per step (and per other top level span) on a `<canvas>` with
a time axis, rather than one lane per `tid`: a labeled row per step reads better than packed lanes, and
the spans of a step's units (`args.step`) are packed in thin sub-lanes under its bar, which shows its
parallelism. It zooms with the mouse wheel around the cursor, pans with drag, resets on double-click,
shows a tooltip with name, category, duration, start and args on hover, lists the top level spans in a
table under the chart (sortable by any column, by duration by default, with the number of unit spans of
each step), colors a span red when its `args.state` is `failed`, and follows `prefers-color-scheme`.
The page is about 10 KB and has no build step; unit tests check the marker replacement, that the data
cannot close the `<script>` element, that the output has no `http://` or `https://` references, that
the page is under 20 KB, and that each span adds less than 150 bytes (the 64 KB budget for 1,000 spans
was not reachable with the Chrome JSON, and one format for both files is worth more).

---

## 7. Incremental rebuilds: design present from phase 1

### 7.1 What phase 1 puts in place

- `Provenance` on every file, `FileRef` with content and metadata hashes.
- `UnitResult` per unit with inputs, outputs and the `DependencySet` recorded during the unit.
- `ResultKey.Compute` and `StepDefinition.ConfigHash`.
- `DependencyTracker` wired through `StepContext`, the Liquid ambient values, `TemplateLoader`
  (templates and includes) and `MetadataValue` (scope lookups: data file keys through
  `SiteScope.KeySources`, step contributions such as `collections` through `SiteScope.Source`, and
  every scope consulted on the way).
- `StepRun.Results` kept in memory after the build.
- `FileContent.LoadAsync`: the scheduler loads every consumed file before its unit runs, so hashing
  input refs never blocks a thread on I/O.

Nothing in phase 1 consumes these. Their cost is one hash per file content (lazy) and per Own
metadata, and a few allocations per unit.

### 7.2 Early cutoff

When a unit re-runs and produces outputs whose refs (path, content hash, metadata hash) equal the
previous `UnitResult.Outputs`, the scheduler marks those outputs "unchanged" and dependents reuse
their previous results for them instead of re-running. For a `reduce` step this means: it re-runs
only if the set of input refs differs from the previous run (a changed page that produced identical
HTML and metadata does not re-run `collect`), and after it re-runs, its dependents re-run only if
its outputs or its scope contribution hash changed. This is what keeps `collect` affordable in watch
mode: editing a page body never touches collections unless the page's metadata changed.

### 7.3 Dynamic dependencies

Recorded at render time, per unit:
- Layouts and includes: exact file and content hash, through `TemplateLoader` (precise).
- Data files: whole-file granularity, through `SiteScope.KeySources` when a lookup hits the data
  scope, and through `FolderScope.Sources` for every folder scope consulted on the file's directory
  path.
- Step contributions (`collections`): the contribution's source, through `SiteScope.Source`, when a
  lookup hits it or consults it before hitting a later scope.
- `with.template` and `permalink` templates: exact file or string hash.

A lookup records every scope it consults, hit or miss, because a scope that does not define the key
could define it later and shadow the hit. This is complete and coarse: a miss, or a hit in the data
scope, makes the unit depend on every site contribution consulted before it (typically
`step:collect`), so a metadata change anywhere re-renders the pages that looked past that scope.
Phase 5 may refine the record to key level (a scope *not defining* a key, checked against the
scope's current key set) if the benchmark shows the coarse record re-runs too much.

Folder scopes are recorded by the data files of every folder scope found on the directory path. A
folder scope that does not exist yet is not recorded: a data file added later to a directory on the
path would shadow a hit in a parent folder or in a site scope without any recorded dependency
noticing. Phase 5 closes this: every unit whose lookups went past Own also depends on the set of
folder scope directories it walked (recorded as a scope dependency on the folder data step, for
instance by giving `FolderScope` a `Source` like `SiteScope`), so a new folder data file re-runs the
units of the files under it. `ScopeSet.cs` is in phase 5's ownership for this.

When tracking is incomplete (a step implementation that reads scopes without going through
`TryResolve`, which is forbidden by code review, or a command step), the unit records every scope
of its `ScopeSet` (every site source and every folder data file), so any data change re-runs it:
correctness over minimal rebuilds.

*As implemented (phase 5):* the record is per key, and per part of a value for templates:

- `TryResolve` records nothing for a key of the file's own metadata (it is in the input ref). Any other key is a
  `Lookup(Directory, Key, Hash, Access)`: the directory of the metadata it was resolved for, the key, and
  `ScopeSet.ResolveHash(directory, key)`, the hash of what the key resolves to beyond own metadata (folder scopes
  from the directory up, site scopes, settings; `DependencySet.Absent` when nothing defines it; combined with the
  settings' value when the settings define the key, since they complete objects). A replay recomputes the hash in the
  step's current scopes. This replaces `DataFiles`, `Scopes`, `SiteScope.KeySources` and `FolderScope.Sources`, and
  it is both finer and complete: a data file edit re-runs only the units that resolved a key whose value changed; a
  folder data file added on the lookup path shadows the value only when it defines the key, and the hash says so
  (the gap of the phase 1b review); `settings` (`site.url`) is part of the hash of the keys it defines.
- A template does not depend on a whole value: `MetadataValue` resolves through `ScopeSet.TryResolveTracked`, and the
  `NodeObjectValue` and `NodeArrayValue` of a value from the scopes record what the template reads (`ValueAccess`,
  `Douze/Workflow/ValueAccess.cs`): members read (present or not), items read, whether the count was read, and the
  whole value when it is enumerated, compared, written or converted. The lookup's hash is then the hash of that part
  (`ScopeSet.ProjectHash`). A nav that lists `collections.all limit: 50` depends on the number of pages and on the
  metadata of those 50, so editing another page does not run every layout again. Steps written in C# depend on the
  whole value (`TryResolve`).
- Templates: `TemplateLoader` records the paths it looked for and did not find with `DependencySet.Absent`
  (`nav.liquid` before `nav.html`, earlier includes folders), so a file added in front of the one used re-runs its
  users.
- `write` records the files it wrote with their length and modification time (`DependencySet.Written`): a replay
  writes nothing, so it checks that the output file is still there, unchanged; otherwise it runs again.

### 7.4 Disk cache (phase 5)

`<source>/.douze/cache/` (gitignored by phase 5's change to `.gitignore` in the Sample and in the
README guidance):

```
index.json          { "version": 1, "douze": "<informational version>", "units": { "<result key hex>": { "step": "...", "unit": "...", "outputs": [ { "path": "...", "content": "<hash>", "metadata": "<hash>", "contentType": "..." } ], "scope": "<hash or null>", "deps": {...} } } }
objects/<hash>      content blobs, content addressed, written atomically (temp file + rename)
meta/<hash>.json    Own metadata blobs, canonical JSON
scopes/<hash>.json  serialized reduce contributions (collections are re-materialized from refs)
```

`BuildCache` (`Douze/Workflow/Cache/BuildCache.cs`): `TryGet(Hash128 key, out CachedUnit)`,
`Put(UnitResult)`, `LoadAsync`, `SaveAsync` (index rewritten at the end of a build, blobs written
as units complete), `Prune(TimeSpan olderThan)` run at build end for blobs not referenced by the
index. Cold starts and CI read it; watch mode keeps everything in memory and writes it on exit.
A version mismatch in `index.json` discards the cache.

*As implemented (phase 5):* `<cache dir>/cache/` holds `index.bin`, `packs/*.pack` and `lock`:

- **Packs, not one file per blob.** A build appends the blobs of the units it runs (contents, metadata, nodes, by
  hash) to a new pack file (`PackFile`), and the index makes them visible when it is saved (written to a temporary file
  and renamed): a build that stops before saving leaves a pack that nothing lists, deleted by the next build. The 32k
  synthetic site has about 130,000 blobs per clean build; creating that many files would cost what writing the site
  costs (benchmark finding 4).
- **A binary index, not `index.json`.** The 32k synthetic site has about 225,000 units; its index is 33 MB with every
  string stored once (`CacheIndex`), and JSON would be several times larger and slower to read at the start of every
  build. There is no `CacheJsonContext`.
- **Binary metadata and nodes.** `NodeSerializer` keeps the type of every value (a date stays a date, `1` an integer),
  so that a value read back hashes and renders like the original. An object or array over 1 KB is stored apart, by
  hash, and read back as one shared node: the pages of a pagination, shared by every page, are stored once (stored
  inline they would be O(pages²) bytes, like finding 2 for hashing).
- **What is stored.** For each unit that runs: its outputs (path, content hash and length, metadata hash, content
  type, source path and date) whose contents and metadata are stored unless they are those of an input (a replay takes
  them from the input), its dependency set (shared by every unit with the same set), and for a reduce unit its
  contribution: each site scope's values as a node blob with the hashes of its keys, each folder scope's values. A
  replayed output reads its content and its metadata from the packs only if a step reads them; a replayed site scope
  reads its values only when a unit resolves one of its keys and runs (checking lookups needs the key hashes only).
- **Source files** are not read when their length and modification time match the previous build's (`SourceStat`);
  a file whose time is within 2 s of the build that recorded it is read again (a change in the same second).
- **Retention.** A successful build keeps the units it used, replayed or ran: the results of an edited page's previous
  version, or of a deleted page, are dropped, so the cache does not grow with edits (switching between two branches
  rebuilds what differs). A failed or cancelled build keeps the previous units too. When less than half the bytes of
  the packs are in use (and they hold more than 4 MB), the used blobs are copied to a new pack and the others deleted.
- An index of another format or Douze version is discarded (info message); a damaged one is discarded with a
  warning; a cache that another build holds (`lock`) is not used, with a warning.

### 7.5 Watch mode (phase 6)

- `FileWatcher`: `FileSystemWatcher` on the source directory, events debounced for 150 ms, mapped
  to virtual paths, with the output folder and `.douze/` ignored.
- `IncrementalBuilder.RebuildAsync(changes)`: finds the root nodes whose `Inputs` match a changed
  path (added, modified, deleted, renamed as delete plus add), re-enumerates only those files, and
  runs the scheduler in replay mode: every unit whose key is found in the previous results of a
  succeeded step (or in the disk cache) and whose recorded dependencies are unchanged (section 2.7)
  is replayed from the stored outputs instead of executed; the other units are executed; early
  cutoff stops propagation. Template, include and data changes are mapped through the recorded
  dependency sets to the units that used them.
- Deletions and renames: provenance tells which outputs came from a removed source; `write` deletes
  them and removes now empty directories.
- `write` writes only files whose hash differs from what it wrote last.
- `DevServer`: serves the output folder, injects a live reload script before `</body>` in HTML
  responses, exposes `/__douze/events` as Server-Sent Events. After each rebuild it sends
  `{"changed":["/blog/a/","/styles.css"]}`; the client reloads when its own URL is in the list or
  when any changed path ends with `.css` or `.js` (stylesheets are swapped in place without a
  reload when only CSS changed).
- The key test (section 8, phase 6): for random edit sequences on a fixture site, the incremental
  output equals a clean build.

---

## 8. Phases

### 8.0 Rules shared by every phase

Acceptance criteria that apply to every phase, in addition to the phase's own:

1. `dotnet test` passes (xUnit v3 on Microsoft.Testing.Platform).
2. The Sample output is unchanged: `scripts/compare-sample.sh` (created in phase 1a) reports
   `Sample output identical`, or every difference is intentional, listed in the PR with its reason, and
   the Sample sources are adapted when that makes sense. An unexplained difference is a bug.
3. The native AOT publish and smoke test from `AGENTS.md` pass:
   `dotnet publish Douze -c Release -o tool-out && ./tool-out/douze -s Sample -o site-out`, and
   the publish log has no new IL warnings beyond the ones already suppressed in `Douze.csproj`.
4. `AGENTS.md` and `README.md` are updated when the phase changes a convention, an option or a
   command.
5. The PR description names the phase and lists the open questions of section 11 it resolved.

`scripts/compare-sample.sh` builds the Sample with the `main` branch's Douze and with the working
tree's Douze and diffs the outputs. Both copies of the Sample get the same fixed modification time
first, because `page.date` drives a permalink in `posts/post1.md` and the `Content modified on`
comment. Only lines containing `Generated on` are ignored:

```bash
#!/usr/bin/env bash
# Usage: scripts/compare-sample.sh [base-ref]   (default: main)
set -euo pipefail
base=${1:-main}
tmp=$(mktemp -d)
# --detach: the base branch may be checked out in another worktree.
git worktree add -q --detach "$tmp/base" "$base"
trap 'git worktree remove --force "$tmp/base" 2>/dev/null || true' EXIT
cp -R "$tmp/base/Sample" "$tmp/base-sample"
cp -R Sample "$tmp/head-sample"
find "$tmp/base-sample" "$tmp/head-sample" -exec touch -t 202001010000 {} +
dotnet run -c Release --project "$tmp/base/Douze" -- -s "$tmp/base-sample" -o "$tmp/base-out"
dotnet run -c Release --project Douze -- -s "$tmp/head-sample" -o "$tmp/head-out" --plain
git worktree remove --force "$tmp/base"
diff -r -I 'Generated on' "$tmp/base-out" "$tmp/head-out" && echo "Sample output identical"
```

The script must keep working from any later phase, so `-s`, `-o` and `--plain` never change
meaning. When a phase adapts the Sample sources (phase 1b does), the base build uses the base
branch's Sample and the head build uses the adapted one, which is what the script does.

Model and effort: the default is Opus at Medium. Phases marked **High** run at High effort, and
phases marked **review** end with a review pass by the planning model (this session's model) before
the next dependent phase starts. The review checks the plan's invariants: immutability of
`VirtualFile`, no scope reads outside `TryResolve`, sorting before every reduce, no deep copies of
data, no reflection that would break AOT, determinism of outputs.

Parallelism between phases: phases that touch disjoint files run in separate worktrees at the same
time. Each phase below lists the files it owns; a phase must not edit a file owned by a phase that
can run in parallel with it. Stubs created in phase 1b make this possible.

```
1a -> 1b -> { 2, 3, 4, B } -> 5 -> 6
```

### Phase 1a: engine core (High, review)

**Goal.** The workflow model, scheduler, globs, hashing, provenance, trace and progress interfaces,
fully unit tested with fake steps. The existing pipeline is untouched and still builds the site,
so the phase is mergeable on its own.

**Files to create.**
- `Douze/Workflow/`: `VirtualFile.cs`, `FileContent.cs`, `Hash128.cs`, `Provenance.cs`,
  `Metadata.cs`, `ScopeSet.cs`, `DependencyTracker.cs`, `StepShape.cs`, `StepDefinition.cs`,
  `IStep.cs`, `StepContext.cs`, `StepOutput.cs`, `FileGroup.cs`, `StepFactory.cs`, `BuiltInSteps.cs`
  (empty registry), `CommandStepFactory.cs` (throws), `WithOptions.cs`, `WorkflowDefinition.cs`,
  `WorkflowGraph.cs`, `WorkflowValidationException.cs`, `Glob.cs`, `GlobSet.cs`, `BuildOptions.cs`,
  `BuildContext.cs`, `BuildResult.cs`, `BuildError.cs`, `FailurePolicy.cs`, `SiteBuilder.cs`,
  `ResultKey.cs`.
- `Douze/Workflow/Scheduling/`: `Scheduler.cs`, `StepRun.cs`, `StepState.cs`, `FileChannel.cs`,
  `UnitKey.cs`, `GroupKeyResolver.cs`.
- `Douze/SiteOptions.cs` (coexists with `Options.cs` until 1b).
- `Douze/Liquid/TemplateLoader.cs`, `MetadataValue.cs`, `NodeValues.cs`, `AmbientKeys.cs`,
  `LiquidOptions.cs` (the group key resolver and `MetadataValue` need them; `Options.Liquid.cs`
  stays until 1b).
- `Douze/Progress/`: `IProgressView.cs`, `PlainProgressView.cs`, `SilentProgressView.cs`.
- `Douze/Tracing/`: `ITraceRecorder.cs`, `TraceRecorder.cs`, `NullTraceRecorder.cs`,
  `TraceSession.cs`.
- `Douze/Nodes/NodeObject.cs`: add `ShallowClone()`.
- `scripts/compare-sample.sh`.
- `Directory.Packages.props`, `Douze/Douze.csproj`: add `System.IO.Hashing`.
- Tests in `Douze.Tests/Workflow/`: `FakeSteps.cs` (configurable map, group and reduce steps with
  gates, delays and failures), `GlobTests.cs`, `Hash128Tests.cs`, `MetadataTests.cs`,
  `ScopeSetTests.cs`, `WorkflowGraphTests.cs`, `SchedulerTests.cs`, `FileContentTests.cs`;
  `Douze.Tests/Progress/PlainProgressViewTests.cs`; `Douze.Tests/Tracing/TraceRecorderTests.cs`;
  `Douze.Tests/Liquid/MetadataValueTests.cs`, `NodeValuesTests.cs`.

**Tests to add** (names are the test methods; each is one `[Fact]` unless noted):
- Glob (10): root only `*.json`; any depth `**/*.json`; `config.json` at root only; leading slash
  ignored; `?`; `{md,html}`; `[!_]*`; negation last wins; `content/**/x.md` middle `**`;
  `FixedPrefix`.
- Hash128 (4): known vector for `Of("abc")` (documented in the test), `Combine` is order
  sensitive, `OfText` equals `Of(UTF8)`, `ToString`/`Parse` round trip.
- FileContent (4): lazy file reads once (counting file system), text hash of lazy equals text hash
  of `FromText`, BOM stripped, `OpenReadAsync` before load streams.
- Metadata (3): `With` shares nested nodes, hash ignores key insertion order, `Directory` in hash.
- ScopeSet (6): own wins over folder; nearest folder wins; folder wins over site; site order
  (nearest contribution first); fallback last; dependency recording for data keys, step scopes and
  misses.
- WorkflowGraph (7): topological order is name-stable; cycle message; unknown needs with
  suggestion; no inputs no needs; group-by unsupported; reserved name `source`; `Roots` and
  `Terminals`.
- Scheduler (14): map streams (downstream sees file 1 before upstream finishes file 2, using gates);
  reduce sees all inputs sorted; group partitions and orders by key; fan-out delivers every file
  to both consumers; duplicate path from two producers fails with both names; unmatched files pass
  through immediately; `Default` policy stops the step and skips dependents while an independent
  step finishes; `FailFast` cancels the independent step; `KeepGoing` reports every error; skip
  propagates two levels; user cancellation returns `Cancelled`; scope contributions merge with the
  right precedence; never more than N units run at once (counter with `Interlocked`); the same
  graph run twice yields identical sorted outputs and result keys.
- MetadataValue and NodeValues (6): resolution through ambient scopes; `NodeValue<DateTime>`
  renders with the `date` filter like today (see risk 4 in section 11); arrays and objects
  enumerate; `size`; nil on miss; `NodeValue<Metadata>` converts.
- PlainProgressView (3): step line format, skipped line, summary with errors.
- TraceRecorder (3): nested spans, unit spans only when enabled, args.

**Acceptance.** The shared criteria (the Sample still builds through the old pipeline), all new
tests pass, `SiteBuilder.BuildAsync` runs a graph of fake steps end to end from a
`MemoryFileSystem` source to a `MemoryFileSystem` output through a fake `write` step.

**As implemented** (later phases build on these; they refine the signatures above):
- `WorkflowGraph.Build(WorkflowDefinition definition, Func<StepDefinition, Step>? factory = null)`: the factory
  defaults to `StepFactory.Create`; tests inject fake steps. `WorkflowGraph.Warnings` holds the "no 'write' step"
  warning, which `SiteBuilder` forwards to the view. `ToYaml` is a first version (flow mappings in definition
  order); phase 2 may rework it with `WorkflowSchema`.
- `StepRun` also exposes `Scopes` (what the step saw), `FilesPassedThrough` and `UnitsFinal` (the `done/?` case).
  `BuildResult` has a `this[string step]` indexer. `DependencyTracker.Add(DependencySet)` replays the reads of
  group keys into each group's tracker.
- Completion order and the wait on ancestors' contributions are as described in 2.6.
- `ScopeSet.TryResolve` records every scope it consults, hit or miss (7.3).
- `ResultKey.Compute(definition, inputs, commandVersion)`: the dependency set is not part of the key (2.7).
- `FileContent.LoadAsync`; the scheduler loads consumed files before their unit runs (2.2).
- `VirtualFile.With` keeps `LastWriteTimeUtc` (2.2).
- `TemplateLoader.GetOrParse` caches a failed parse too and reports it under each caller's path.
- `Douze/Workflow/CanonicalJson.cs` writes nodes as canonical JSON (`Utf8JsonWriter`, no reflection) for
  `Metadata.Hash`, scope hashes and `StepDefinition.ConfigHash`; `NodeValue<Metadata>` is written as its hash.
- `BuildContext.Cache` is added in phase 5 with `BuildCache`. `BuildContext.MaxDegreeOfParallelism` is the
  normalized value.
- `Glob.Parse` throws `GlobFormatException : FormatException` with a `Position`; its message is the reason only
  ("unterminated character class"), as the 4.5 messages expect.
- `StepDefinition.DouzeVersion` reads `AssemblyInformationalVersionAttribute` through reflection (AOT compatible, but
  the engine is not reachable from `Program` in 1a): phase 1b checks on the AOT publish that it is not empty.
- `FileContent.IsText` for lazy source files is decided by extension (`FileContent.IsTextPath`).
- The duplicate path error names both producers in ordinal order, so the message is deterministic.
- Open question 4 is resolved: `NodeValuesTests.DateTimeRendersLikeTheProcessorPipeline` renders the same with
  both paths, no adjustment needed.

### Phase 1b: built-in steps, default workflow, CLI, deletion of the old pipeline (Medium, review)

**Goal.** Douze builds sites with the new engine only. Today's processors are gone.

**Files to create.** `Douze/Workflow/Steps/*` (all eleven files of 2.11 plus
`PermalinkResolver.cs`), `Douze/Workflow/DefaultWorkflow.cs`, `Douze/FrontMatter.cs` (moved
from `FrontMatterHelper.cs`), `Douze/Cli/CommandLine.cs`, `Douze/Cli/BuildCommand.cs`,
`Douze/Cli/WorkflowSource.cs` (stub: returns `DefaultWorkflow.Create`; errors with
"workflow files are not available yet" when `--workflow` is given or `douze.yml` exists),
`Douze/Cli/ViewSelector.cs` (returns Plain or Silent; the Spectre branch is a `TODO(phase 4)`),
`Douze/Cli/WorkerHelperCommand.cs` (stub), `Douze/Workflow/Commands/douze-worker.mjs` and
`Douze/Tracing/timeline.html` as empty placeholder files registered as `EmbeddedResource` in
`Douze.csproj` (so phases 3 and 4 do not edit the project file).

**Files to change.** `Douze/Program.cs` (rewritten), `Douze/Douze.csproj` (embedded resources),
`README.md` (options, data keyed by file name, passthrough), `AGENTS.md` (workflow engine summary,
`scripts/compare-sample.sh`, folder layout), `Douze/todo.txt`, Sample adaptations:
- `Sample/_data/characters.yml` becomes the list only (`- name: Luke` ...).
- `Sample/_data/colors.json` becomes the array only.
- `Sample/_data/theming.json` is renamed to `Sample/_data/layout.json` with content `"layout.html"`.

**Files to delete.** Section 3.2.

**Tests.** `CommonTests` rewritten as described in 3.3 (15 tests, same names), plus
`Douze.Tests/Workflow/Steps/`: `DataStepTests` (5: keyed by file name, nested folder key, scalar
file, duplicate key error, folder merge order), `FrontMatterStepTests` (3), `MarkdownStepTests` (2:
extension renamed, content type), `PaginateStepTests` (4: hrefs, per-page permalink with alias,
default paged paths, shared pagination objects), `PermalinkStepTests` (4: root stripped, custom
extension kept, `permalink: false` drops, templated), `CollectStepTests` (3: no files emitted,
contribution hash changes with metadata only, items wrap metadata), `LiquidStepTests` (5: include
through the file provider records a dependency, parse error has line and column, capture lands in
Own, template parsed once for two files with the same content, grouped mode with `with.template`
and `with.permalink`), `LayoutStepTests` (4: fallback precedence, chaining, missing layout error
text, chain limit), `WriteStepTests` (2: duplicate output error text, directories created),
`DefaultWorkflowTests` (2: `ToYaml` equals the YAML of section 4.6 for a site with `_content`,
and for a site without it).

**Acceptance.** Shared criteria (this is the first phase where the comparison script exercises the
new engine; the three Sample data adaptations are part of the change), `dotnet run --project Douze -- -s Sample -o out`
prints one line per step and a summary, exit code 1 with a `BuildError` line when a layout is
missing (manual check documented in the PR), and `git grep -l "IFileProcessor\|IDocument\|IncludeRule"` returns nothing.

**As implemented** (later phases build on these):
- Sample output differences, intentional (everything else is identical, and the three data adaptations change
  nothing on their own):
  - Folder data wins over site data (section 2.3; the processor pipeline merged the site data first and never
    replaced a key, so the outermost data won). The articles now use `articles.html`, chained to `layout.html`, as
    `articles/articles.yml` says (Appendix A).
  - `url` is an absolute path: `/articles/article1/index.html` instead of `articles/article1/index.html` (a relative
    link that only worked from the root, and was absolute already when the content folder was `/`).
  - Passthrough files lose the content folder: the images are written to `/posts/images/`, not
    `/_content/posts/images/`. This needs `permalink` to match every file (2.11).
- The scheduler lets the same file instance reach a node through two needs (2.6): the default workflow's
  `render` needs `permalink` and `collections`, and `collections` passes the files it does not match through, so
  without this every image arrived twice.
- `VirtualFile.SourcePath` (messages), `VirtualFile.EffectiveContentType` and `ContentTypes.FromPath` are added;
  no step sets `ContentType` in the default workflow.
- `TemplateLoader.GetOrParse` throws `TemplateParseException` (path, line, column, reason; the reason is the first
  line of Fluid's message without the position it repeats). `BuildError.FromException` turns it into a location
  when the template is the file itself. `TemplateLoader.ParsedCount` exists for tests.
- `StepContext.CreateTemplateContext(model, file, ct)` sets the ambient values of 2.8 for every step render.
- `Douze/Workflow/Steps/StepPaths.cs` normalizes folder options (`_content` -> `/_content/`) and strips roots.
- Data steps contribute `SiteScope("step:<name>")`, like every other step.
- `paginate` page summaries, paged paths, `with.root` and dotted `pagination.data`; `permalink` matching every
  file; `liquid` `with.data` and `with.encode`; layout front matter layering: see 2.11.
- CLI: `-h`/`--help` and `--version` are added; `--print-workflow` works already (it prints
  `WorkflowGraph.ToYaml()`, whose padding now matches 4.6), so phase 2 only replaces `WorkflowSource`. `--trace`
  and `--timeline` are accepted and print a warning until phase 4; `--no-cache` and `--cache-dir` are accepted.
  Unknown options are an error (exit code 2). `--keep-going` with `--fail-fast`, or `--quiet` with `--verbose`,
  is an error.
- `FrontMatter.Parse(string)` returns the values and the content; `FrontMatter.Split` keeps today's algorithm,
  except that a file ending right after the closing `---` no longer throws.
- `YamlHelper.Parse` returns null for an empty document (an empty data file).
- `DouzeVersion` is not empty on the native AOT publish (`douze --version` prints `1.0.0-beta-0`).
- `NodeValuesTests.DateTimeRendersLikeTheProcessorPipeline` keeps the processor pipeline's renderings as literals.
- `IncludeRuleTests` is deleted: `GlobTests` (phase 1a) covers its cases with standard semantics.

**Review pass** (what it changed, so phases 2, 3, 4 and B start from it):
- The diamond rule of 2.6 also accepts another instance with the same ref (`SchedulerTests.EqualFileThroughTwoNeedsEntersOnce`).
- `WorkflowSource.LoadAsync(CommandLine, sourceDirectory)` builds the `SiteOptions` (detection, output folder) and
  returns them with the workflow; `CommandLine.Output` is null when `-o` is absent; `SiteOptions.Parallelism` is used
  when `-p` is absent. Phase 2 applies `site:` inside `WorkflowSource` only (`WorkflowSourceTests`).
- `Douze.Tests.FakeTool/` exists as a stub, referenced from `Douze.Tests.csproj`, which also copies `Fixtures/**`.
  The README has placeholder sections "workflow", "commands" and "progress and tracing". With these, the files each
  of phases 2, 3, 4 and B edits are disjoint (see their "Owns" lists).
- Left as follow-ups: error positions point into processed content under the output path (`todo.txt`); `pagination.hrefs`
  and the pages' `href` are empty while a page's `permalink` renders, since they come from it (2.11); a `pagination.data`
  that resolves to nothing passes the file through unchanged, as the processor pipeline did; the default workflow cannot
  paginate `collections.<tag>` (open question 19); `PlainProgressView` prints `1 files` (phase 4 owns the views).

### Phase 2: YAML workflow loading (Medium)

**Goal.** `douze.yml` with `site` and `steps`, validation with file and line, `--print-workflow`.

**Owns.** `Douze/Workflow/Yaml/WorkflowLoader.cs`, `YamlSource.cs` (reads SharpYaml model nodes
with their parsing event marks and exposes `Location(node)`), `WorkflowSchema.cs` (field table of
4.3 as data, used by the loader and by `WorkflowGraph.ToYaml`), `Douze/Workflow/WorkflowGraph.cs`
(`ToYaml` over the schema), `Douze/Cli/WorkflowSource.cs` (real implementation: it already builds the
`SiteOptions`, so `site:` is applied there and `BuildCommand.cs` is not touched; `site.output` applies
when `CommandLine.Output` is null, `site.parallelism` goes to `SiteOptions.Parallelism`),
`Douze/SiteOptions.cs` (apply `site:`), the README section "workflow" (a placeholder exists),
`Sample/douze.yml` is **not** added (the Sample stays a no-configuration site), a new
`Douze.Tests/Fixtures/workflows/*.yml` set (`Douze.Tests.csproj` already copies `Fixtures/**` to the
output, so the project file is not touched).

**Tests.** `WorkflowLoaderTests`: one test per message of 4.5 asserting the message and the line
and column (about 20 tests), the aspire example of 4.7 loads, `site` values apply, `steps` absent
uses the default, `--print-workflow` output round trips (load, `ToYaml`, load again, same
definitions), a duration table test.

**Acceptance.** Shared criteria; `douze build --print-workflow` on the Sample prints the YAML of
4.6; a fixture site with a two-pipeline workflow (pages and a `group-by: file` data pipeline) builds
in a test.

**As implemented:**
- `Douze/Workflow/Yaml/WorkflowFile.cs` is added: `WorkflowFile(SiteSettings Site, WorkflowDefinition? Workflow)`
  (null without `steps`) and `SiteSettings` (folders as `FPath`, output, url, parallelism).
  `WorkflowLoader.Load(text, fileName, sourceFile?)` and `LoadAsync(path, fileName)`; `SourceFile` is the full path.
- `WorkflowSchema.StepFields` is the field table of 4.3 as data (name, whether it needs `run`, the expected type,
  a reader and a writer). `WorkflowSchema.ToYaml(definition)` writes it and `WorkflowGraph.ToYaml()` delegates to it.
  Fields with their default value are left out (so `mode: batch` is no longer printed), scalars are quoted when they
  would not read back as the same string (numbers, booleans, null, indicators, `\n`), and an empty workflow is
  `steps: {}`. Load, print and load again gives the same names, needs and `ConfigHash` (`PrintedWorkflowLoadsBackTheSame`).
- `with` values are typed like data files (integers, decimals, booleans, null), with the invariant culture.
- `site.url` reaches `SiteOptions.Url` but is not exposed to templates yet: that needs a base scope in the
  scheduler (`ScopeSet.Empty` today in `Scheduler.cs` and `SiteBuilder.cs`, which phase 2 does not own). To do when
  the branches are merged: a `SiteScope("site", { site: { url } })` as the base of every unit's scopes, and decide
  how it combines with a `_data/site.json` (a contribution replaces the whole `site` key).
- `--print-workflow` builds the graph first, so a workflow with `run:` steps prints only once phase 3 lands.
  *Integration:* it does; `CommandWorkflowTests.PrintedWorkflowLoadsBackTheSame` prints the `douze.yml` of the
  command fixture site and loads it back.
- Tests: `WorkflowLoaderTests` (91 cases: every message of 4.5 with its line and column, the 4.7 example, typed
  `with`, round trips, scalars, durations), `WorkflowSourceTests` (site section, `-o` over `site.output`, `--workflow`,
  missing file, error location, the two-pipeline fixture site). Fixtures: `Douze.Tests/Fixtures/workflows/`
  (`aspire.yml`, `all-fields.yml`, `two-pipelines/`).

### Phase 3: command steps (Medium)

**Goal.** `run:` steps in the three modes with the worker protocol and the Node helper.

**Owns.** `Douze/Workflow/Commands/*` (all files, including the real `douze-worker.mjs`),
`Douze/Workflow/CommandStepFactory.cs`, `Douze/Cli/WorkerHelperCommand.cs`, the README section
"commands" (a placeholder exists), the test helper project `Douze.Tests.FakeTool/` (a stub console
app exists, already referenced from `Douze.Tests.csproj` with `ReferenceOutputAssembly=false`, so
neither the test project file nor `Douze.slnx` is touched; phase 3 fills in the modes `filter-upper`,
`batch-copy`, `batch-manifest`, `worker-echo`, `worker-slow`, `fail`, `stderr`, `hang`). The tests
launch it with `dotnet <path to Douze.Tests.FakeTool.dll>`, found from the test assembly's folder
(`../../../../Douze.Tests.FakeTool/bin/<Configuration>/net10.0/`), which works on every CI runner
without Node.

Sub-steps, each a commit: 3a `CommandTemplate`, `ProcessRunner`, `FilterCommandStep`,
`BatchCommandStep`; 3b `WorkerProtocol`, `WorkerProcess`, `WorkerCommandStep`, the helper.

**Tests.** `CommandTemplateTests` (5: expansion, quoting, `inputs` invalid outside batch, env
expansion, `file` model), `FilterCommandStepTests` (5: content transformed, non-zero exit message
includes the file, stderr policies, timeout kills, parallel bound), `BatchCommandStepTests` (6:
inputs materialized with manifest, outputs read back with globs, outputs manifest metadata, no
materialization without `{{ inputs }}`, group-by runs one process per group, temp folder removed),
`WorkerCommandStepTests` (8: handshake, concurrency honored, out of order responses, error per
unit, log messages forwarded, early exit fails in-flight units, shutdown within grace, stdin
closed after shutdown), `NodeHelperTests` (3, skipped with `Assert.Skip` when `node` is not on
the PATH: echo worker through the real helper, binary round trip, error path).

**Acceptance.** Shared criteria; a fixture workflow with all three modes runs in a test; the README
documents the protocol with the examples of 5.3.

**As implemented.** Section 5 has the details (marked *As implemented (phase 3)*); in short:

- Files: `CommandStep.cs` is the base class (template, working directory, environment, exit code and
  stderr rules); the others are as listed in 2.1. `CommandStepFactory` maps the three modes.
- The fake tool also has `filter-count` (records how many processes run at once), `args`, `env` and
  `sleep`; `hang` starts a child process and writes both process ids, so the timeout test checks that
  the whole tree is gone. `worker-echo` handles requests concurrently and reacts to their content
  (`ERROR`, `LOG`, `EXIT`, `GARBAGE`, `STDERR`, `MANY`, `DROP`, `RENAME`, `META`, `HANG`) and their
  `delay` metadata, and reports `sequence` and `maxInflight`; `worker-slow` never exits after
  `shutdown`.
- Tests: the planned ones plus `FilterCommandStepTests` environment, large metadata, paths with spaces
  and quotes through every shell, working directory, `with` outside worker mode;
  `BatchCommandStepTests` failure messages; `WorkerCommandStepTests` not JSON, stderr policies, result
  shapes (many, none, renamed, metadata only, binary), timeout, a worker that does not start;
  `WorkerDelayTests` (shutdown grace, handshake timeout; they change static delays, so their collection
  runs alone); `NodeHelperTests` also checks what `douze worker-helper` prints;
  `CommandWorkflowTests` builds `Douze.Tests/Fixtures/commands` from and to disk through a filter, a
  worker and a batch step. The workflow is built in code; phase 2 can give the fixture a `douze.yml`.
  *Integration:* the fixture has a `douze.yml` (`dotnet tools/Douze.Tests.FakeTool.dll <mode>`; the test copies
  the fake tool into `tools/`), loaded through `WorkflowSource`.
- `BuildContext.TempRoot` is removed by the command steps when they leave it empty; `BuildContext`
  owning it (deleting it on dispose unless `--verbose`) would be cleaner, left for the integration.
  *Integration:* `BuildContext.DisposeAsync` deletes the temp root with everything in it, or only when
  empty with `--verbose`; the command steps no longer clean it.

### Phase 4: progress view, trace file and timeline (Medium)

**Goal.** The live console view, the Chrome trace file, `timeline.html`.

**Owns.** `Douze/Progress/SpectreProgressView.cs`, `Douze/Cli/ViewSelector.cs` (real selection),
`Douze/Tracing/ChromeTraceWriter.cs`, `TimelineHtmlWriter.cs`, `timeline.html`,
`Douze/Cli/BuildCommand.cs` (write the trace files when requested), `Directory.Packages.props`,
`Douze/Douze.csproj` and `Douze.Tests/Douze.Tests.csproj` (`Spectre.Console` and
`Spectre.Console.Testing`; phase 4 is the only phase of the four that edits these three files), the
README section "progress and tracing" (a placeholder exists).

**Tests.** `ChromeTraceWriterTests` (4: valid JSON through `System.Text.Json` with the source
generated context, lanes do not overlap, microsecond units, args), `TimelineHtmlWriterTests` (3:
marker replaced, no external references, page under 20 KB and under 150 bytes per span),
`SpectreProgressViewTests` with `TestConsole` (4: running rows, completion lines, summary,
throttling does not drop the final state), `ViewSelectorTests` (4), `BuildCommandTests` (1: `--trace`
and `--timeline` write both files), `PlainProgressViewTests.OneFile` (`1 file`, not `1 files`).

**Done (2026-10-21).** Shared formatting moved to `Douze/Progress/StepSummary.cs` (step line, headline,
name width), used by both views. Verification: the live view was checked by running the AOT and Debug
builds under a pseudo-terminal (`script`) with `TERM=xterm-256color`, on the Sample and on a generated
site of 4,000 pages, and replaying the captured byte stream through a small terminal emulator to see
the frames and the final screen (that is how the residue of short lines over live rows was found and
fixed); the timeline page was checked in a browser in both themes (tooltip, zoom, sorting, no console
errors).

**Acceptance.** Shared criteria; the AOT publish log has zero IL warnings from Spectre; a manual run
in a terminal shows the live table (screenshot in the PR); `--trace out.json` opens in Perfetto
(checked manually once, noted in the PR); `--timeline out.html` renders with no network access.

### Phase B: benchmark harness (Medium, parallel with 2, 3 and 4)

**Goal.** Section 9's harness and the first numbers for the engine of phase 1b.

**Owns.** `bench/` (everything under it), `Douze.slnx` (adds `bench/Douze.Bench`; the only phase of
the four that edits it), `.gitignore` (`bench/**/data/`, `bench/**/out/`).

**Acceptance.** `bench/README.md` has the commands, the first results table is filled in for the
synthetic site on the implementer's machine, and the aspire data harness runs when the data is
present.

### Integration of phases 2, 3, 4 and B

The four phases merged without textual conflicts; this step made the result green and applied two
approved product changes. Each item is its own commit.

- **Tests in Release.** `Douze.Tests.FakeTool` was not in `Douze.slnx`, and MSBuild builds a project
  reference that is outside the solution without the solution's configuration: `dotnet test -c
  Release` built the fake tool in Debug, and the 35 command tests could not find it under
  `bin/Release/`. It is in the solution now.
- **`TwoPipelineFixtureBuilds`.** The `**/[Pp]ackages/*` rule of `.gitignore` (NuGet's legacy
  packages folder) kept phase 2's `Fixtures/workflows/two-pipelines/data/packages/*.json` out of its
  commits, so the merged fixture had no packages. The rule is gone and the files are added.
- **`SpectreProgressViewTests.Summary`.** A step's `StartedAt` was set when its ancestors' scopes
  were ready, which is the start of the build for every streaming step, so every bar of the summary
  timeline started at 0 and the test passed only when JIT delays separated the starts (it ran alone in
  phase 4, among more tests after the merge). A step now starts with its first file or unit (2.6).
- **Integration items:** `site.url` reaches templates (4.2, base settings scope); step spans carry
  `shape` and their final `state`, command spans `step` (6.4); `BuildContext` deletes its temp root;
  `--help` and the README no longer list worker-helper, `--trace`, `--timeline` and workflow files as
  unavailable; the command fixture has a `douze.yml` and `--print-workflow` prints `run:` steps;
  benchmark finding 5 (hang after an exception outside a unit) is fixed with a test (2.6); the write
  step no longer creates each directory twice (finding 4, partly). Findings 1 to 3 are left for
  phase 5 and are not made worse: the new `ScopeSet` settings scope adds one hash per scope set and a
  shallow clone per `site` object and scope set.
- **Default workflow** (open questions 3 and 19): Liquid before Markdown with `liquid: false` as the
  opt-out, collections before pagination (4.6).

Sample differences against `update-dependencies`, besides the three of phase 1b (folder data over
site data with the `articles.html` chain, absolute `url`, images under `/posts/images/`), all in
`index.html`, all from Liquid running before Markdown:

1. The lines produced by the two `{% for %}` loops lose their leading and trailing space
   (` Red: 1  &lt;Blue&gt;: 2 ` is `Red: 1  &lt;Blue&gt;: 2`): Markdig now renders Liquid's output,
   and trims the lines of a paragraph.
2. The empty `<p></p>`, the stray `</p>` after `</body>` and the unclosed `<p>` before the footer are
   gone: Markdig used to wrap the raw `{%- capture %}` and `{%- endcapture %}` tags in paragraphs.
3. The footer is `<strong>hello</strong>` instead of `<p><strong>hello</strong>`: a capture now holds
   Markdown source, which nothing renders, so the Sample writes it in HTML (`index.md` adapted).

### Phase 5: disk cache, early cutoff and replay (High, review)

**Goal.** A second `douze build` with no changes executes zero units; a build after one change
executes only the affected units; results survive across processes through the disk cache.

**Owns.** `Douze/Workflow/Cache/*` (`BuildCache.cs`, `CacheIndex.cs`, `ResultKey.cs` moved here,
`CacheJsonContext.cs` source generated), `Douze/Workflow/Scheduling/Scheduler.cs` (replay mode:
before executing a unit, compute its key; on a hit, emit the stored outputs with content loaded
lazily from the cache blobs; after executing, compare outputs with the previous result for early
cutoff), `Douze/Workflow/Scheduling/ReplayState.cs` (previous results by `UnitKey`), command step
cache keys in `Douze/Workflow/Commands/CommandStep.cs`, `Douze/Cli/BuildCommand.cs` (`--no-cache`,
`--cache-dir`), `Douze/Workflow/ScopeSet.cs` (folder scope dependencies, 7.3), `.gitignore` of the
Sample and the README (`.douze/`).

Refinement of keys (7.3): a unit whose recorded `DependencySet` is complete drops `ScopeSet.Hash`
from its key and uses the hashes of the recorded data files, templates and step scopes instead.

**Tests.** `BuildCacheTests` (5: round trip, atomic blob writes, version mismatch discards, prune,
corrupt index ignored with a warning), `ResultKeyTests` (4: stable, changes with config, with a
dependency, with command version), `EarlyCutoffTests` (6: no-change build executes zero units; body
edit re-runs markdown, render, layout and write for one file and not collect; tag edit re-runs
collect and every render that read `collections`; include edit re-runs only its users; data file
edit re-runs only units that resolved one of its keys; `incremental: false` always runs), a cold
start test (build, new process simulated by a new `BuildContext`, build again from the disk cache
with zero executed units), and the first version of the incremental-equals-clean test for sequences
of modifications only (no adds or deletes yet).

**Acceptance.** Shared criteria; `douze build` twice on the Sample reports `0 units executed` the
second time; the synthetic 32k site of section 9 rebuilds after one page edit in under 2 seconds
from the disk cache.

**As implemented** (phase 6 builds on these; sections 2.7, 7.3, 7.4 and risk 18 have the details):

- **Fluid first.** Fluid 3.0.0-preview-780 adds `IFluidArray`, which `NodeArrayValue` implements (open question 20,
  benchmark finding 1); preview-781 fixes a race in `TextSpanStatement` found by this phase's tests.
- **Hashes** (finding 2). `NodeHash` hashes nodes as a Merkle tree and caches the hash of every object and array on the
  node (`NodeHashCache`, reset by every change; nodes are not changed once published): the pagination shared by the
  pages of a file is hashed once instead of once per page. Values are hashed with their type. `CanonicalJson` remains
  for configuration hashes and the metadata given to commands. The refs of a reduce or group input are computed in
  parallel.
- **Memory** (finding 3, risk 18): unit results keep refs (`UnitResult.Outputs` are `FileRef`s, the files only with
  `BuildOptions.KeepFiles`, which the tests set), `BuildResult.Outputs` is refs, the source files are released once
  every root has read them, `write` keeps the paths it wrote, and the template cache holds page templates weakly.
- **Dependencies** (7.3): lookups by key with the hash of what they resolve to, or of the part a template read
  (`ValueAccess`, bounded to 64 members or items per value and 1,024 parts per lookup, beyond which the value counts as
  read whole); template paths looked for and missing; files written. `DataFiles`, `Scopes`, `SiteScope.KeySources` and
  `FolderScope.Sources` are gone; `SiteScope` is a class whose values can be read lazily from the cache.
- **Cache** (7.4): `BuildCache`, `CacheIndex` (binary), `PackFile`, `NodeSerializer`, `ResultKey` in
  `Douze/Workflow/Cache/`. Deviations: packs instead of one file per blob, a binary index instead of `index.json`
  (no `CacheJsonContext`), binary metadata and nodes instead of JSON, nodes over 1 KB stored apart; `Prune` is the
  retention and compaction done by `Save` (7.4). `BuildContext.Cache` is opened when the site is on disk or a cache
  folder is given, and `UseCache` is set; `BuildContext.CacheRoot` and `ExcludedSourceFolders` (the output and cache
  folders inside the source folder, never sources). Unchanged source files are not read (`SourceStat`).
- **Replay** (`Scheduler.RunUnitAsync`, `ReplayState`). With the cache, every unit of an incremental step computes its
  key; a cached result whose dependency set is unchanged (checked once per step and set, except written files) is
  replayed: its outputs are the inputs when equal, or files whose content and metadata are read from the cache on
  demand; a reduce unit's contribution is restored. A replayed unit is a unit like any other for counts, progress and
  results (`UnitResult.Replayed`, `StepRun.UnitsReplayed`, `BuildResult.UnitsExecuted`); exceptions of a replay are the
  unit's failure, so `NodeRunner.RunAsync` still completes the contribution, the channel and `Completed` in its
  `finally`. A step that keeps state across units is told about replayed units (`IReplayObserver`): `write` registers
  their paths, so a duplicate output is still an error. Early cutoff comes from the keys (2.7). `incremental: false`
  never replays nor stores. Command steps add `CacheVersion` (`version` and the files `sources` match, outside the
  output and cache folders) and do not depend on scopes (2.7). Steps declare `ReadsContent` (false for `collect`).
- **CLI and views.** `--no-cache` and `--cache-dir` work; `.douze/` is skipped as a source and ignored by the Sample's
  `.gitignore`; the headline ends with `N units executed, M cached`, a step line with `M cached`, and the live summary
  has a `Cached` column. `MemoryFileSystem` truncates rewritten files and gives each reader its own stream.
- **Tests.** `BuildCacheTests` (round trip, blobs visible once saved, version mismatch, prune and compaction, damaged
  index, a cache in use), `ResultKeyTests` (4), `EarlyCutoffTests` (no change, body edit with and without a date change,
  tag edit, include edit and a shadowing include, data file edit and a shadowing folder data file, `incremental:
  false`, a deleted output folder), `ColdStartTests` (two processes on disk, `.douze` and the output folder inside a
  source without `_content`), `IncrementalEqualsCleanTests` (25 seeds of 12 random modifications of a 40-page site with
  layouts, includes, site and folder data, tags and two paginated pages, each incremental build compared with a clean
  one), `NodeHashTests`, `ValueAccessTests`.
- **Races found by `IncrementalEqualsCleanTests`** in repeated runs on ARM64: Fluid's `TextSpanStatement` published its
  prepared text without a barrier, so a page rendering a freshly parsed layout concurrently could lose a text segment
  (fixed by sebastienros/fluid#1059, in Fluid 3.0.0-preview-781); and lazily loaded metadata and cached `Hash128?` values were published the same way
  (`Metadata._load` is volatile, `HashSlot` replaces `Hash128?` caches).
- **Benchmarks** (`bench/README.md`, "Phase 5"): synthetic 32k clean 5.5 s (6.8 s writing the cache), no change 1.1 s
  (0 units executed), one page edited 1.7 s (8 units), one tag edited 1.5 s; aspire API pages 3.5 s, no change 1.1 s.
  Peak RSS 2.3 GB for the synthetic site (target 1.5 GB, missed: 9.2), 2.4 GB for the aspire pages. `run --cache
  clean|warm|edit|tag` measures builds from the cache; `run-all.sh` includes them.
- **Left for phase 6.** Deletions and renames (stale outputs are not removed), keeping results in memory across
  rebuilds, `TemplateLoader.Invalidate` use. A worker command step starts its process even when every unit replays.

### Phase 6: watch mode, dev server, live reload (High, review)

**Goal.** `douze watch` and `douze serve` with partial replays, deletions and renames handled
through provenance, live reload of affected URLs, and the incremental-equals-clean test with
random edit sequences including adds, deletes and renames.

**Owns.** `Douze/Workflow/Watch/*` (`FileWatcher.cs`, `ChangeSet.cs`, `IncrementalBuilder.cs`,
`DevServer.cs`, `LiveReload.cs`, `livereload.js` embedded), `Douze/Cli/WatchCommand.cs`,
`Douze/Cli/CommandLine.cs` (`--port`), `Douze/Workflow/Steps/WriteStep.cs` (skip unchanged, delete
removed), `Douze/Liquid/TemplateLoader.cs` (`Invalidate`), README section "Watch and serve".

First task: verify `System.Net.HttpListener` under the AOT publish on macOS and Linux; if it
warns or fails, implement `DevServer` on `TcpListener` with a minimal HTTP/1.1 responder (GET
only, `Content-Type` by extension, `Connection: close`, SSE for `/__douze/events`).

**Tests.** `ChangeSetTests` (4: debounce merges events, rename is delete plus add, output and cache
folders ignored, mapping to root nodes), `IncrementalBuilderTests` (6: delete removes outputs,
rename moves outputs, include edit re-renders users only, data edit, new file added to collections,
template cache invalidated), `DevServerTests` (4: static file, injected script in HTML only, SSE
event after rebuild, 404), `IncrementalEqualsCleanTests` (the key test): a fixture site generated
in a `MemoryFileSystem` with 200 pages, 3 layouts, 4 includes, 6 data files, tags and a paginated
page; for 25 seeds, apply 12 random edits each (modify body, modify front matter tags or title,
add page, delete page, rename page, edit include, edit layout, edit data file, add data file, edit
paginated source), rebuild incrementally after each, snapshot the output file system as
`path -> content hash`, and compare with a clean build of the same source; on failure the message
prints the seed and the edit sequence. The theory is marked `[Theory]` over the seeds so a failing
seed is rerunnable alone.

**Acceptance.** Shared criteria; `douze serve -s Sample` serves the site, editing `index.md` reloads
only `/` (checked manually, noted in the PR); the key test passes for 25 seeds in under 60 seconds.

### Summary table

| Phase | Depends on | Parallel with | Model and effort | Review pass |
|---|---|---|---|---|
| 1a engine core | | | Opus, High | yes |
| 1b built-ins and CLI | 1a | | Opus, Medium | yes |
| 2 YAML | 1b | 3, 4, B | Opus, Medium | no |
| 3 commands | 1b | 2, 4, B | Opus, Medium | no |
| 4 console and trace | 1b | 2, 3, B | Opus, Medium | no |
| B benchmarks | 1b | 2, 3, 4 | Opus, Medium | no |
| 5 cache and early cutoff | 2, 3, 4 | | Opus, High | yes |
| 6 watch and serve | 5 | | Opus, High | yes |

---

## 9. Benchmarks and validation

### 9.1 Harness (`bench/`, phase B)

`bench/Douze.Bench/` is a console project (not packed, not part of `dotnet test`) with three
commands:

- `synthetic --pages N --out <dir>`: generates a site with N Markdown pages of about 4 KB with front
  matter (title, date, 2 tags out of 20), 1 layout chain of 2 levels, 3 includes used by the layout,
  `_data` with 5 files, a nav include that loops over 50 items of `collections.all`, 1 paginated
  page over a 2,000-item data file (size 20), and 200 static assets (css, js, png). N defaults to
  32,000, which is aspire.dev's page count.
- `aspire-api --data <dir> --out <dir>`: copies `pkgs/*.json` and `ts-modules/*.json` into a
  site with the workflow of 4.7 and two templates (`csharp-package.liquid`, `ts-module.liquid`)
  that render one page per type and per module item from the JSON, so the output size is
  comparable to the real site (about 24k pages). The data is obtained with
  `git -C ~/github/aspire.dev show origin/main:src/frontend/src/data/pkgs/<file>` for each file
  listed by `git ls-tree` (202 files, 15.6 MB, and 140 files, 62.9 MB; the largest is 14.1 MB), by
  `bench/fetch-aspire-data.sh`. The data is gitignored.
- `run --site <dir> --out <dir> [--douze <path>] [--repeat 3]`: runs the published AOT binary,
  reports wall time, user and system CPU, peak RSS (`/usr/bin/time -l` on macOS, `-v` on Linux,
  `Process.PeakWorkingSet64` on Windows), and the number of output files, as a Markdown table row.
  With `--incremental` it edits one page between runs and reports the rebuild time (phase 5).

`bench/README.md` keeps the commands and a results table that implementers fill in per phase.

As implemented (phase B):

- `synthetic` writes layouts `post.html -> base.html`, includes `header`, `nav`, `footer`, and the data files
  `layout.json` (`"post.html"`), `site.json`, `authors.yml`, `menu.json` and `products.json` (2,000 items).
  `--nav index` (the nav indexes `collections.all[i]`) and `--nav none` isolate the cost of the nav loop.
- `aspire-api` builds with the default workflow until phase 2 lands: the JSON files are site data
  (`_data/pkgs/<slug>.json`, `_data/tsmodules/<slug>.json`), each package folder has its page index as folder data
  (`pages.json`), and one template per aspire.dev route paginates over it with a size of 1: C# package, type and
  member kind pages, TypeScript module, item and method pages, 19,712 pages (not 24k: the Markdown twins of some routes
  are not generated). `--mode data` writes the data and a single page, for the parse target. The workflow of 4.7 is a
  second variant to add after phase 2.
- The data lives in `src/frontend/src/data/{pkgs,ts-modules}` of aspire.dev; `bench/fetch-aspire-data.sh` fetches
  `origin` and extracts both folders of `origin/main` with `git archive`, so the checkout is never touched.
- `run` takes `--label` and passes the options after `--` to douze; `--incremental` waits for phase 5.
  `bench/run-all.sh` publishes the AOT binary and prints the whole results table.
- The results table and the bottlenecks found, with suggested fixes, are in `bench/README.md`.

### 9.2 Targets

Measured on the published native AOT binary, 8 cores, warm file cache, median of 3 runs.

| Scenario | Target | Why |
|---|---|---|
| Synthetic 32k pages, clean build | wall under 30 s, peak RSS under 1.5 GB | The gap analysis measured about 1,100 pages/s with the old engine on 3k pages; streaming and shared data should keep that linear |
| Synthetic 32k pages, second build from disk cache, no change | under 3 s | Phase 5; dominated by hashing source files |
| Synthetic 32k pages, one page edited, watch mode | under 500 ms to the written output | Phase 6; map path only, collections untouched by early cutoff |
| Synthetic 32k pages, one tag edited | under 5 s | Collections re-run plus every page that reads `collections` (the nav include on every page in this synthetic site is the worst case) |
| aspire API data: parse 78 MB of JSON | under 4 s, RSS under 1.2 GB for the data | Node tree conversion roughly doubles the footprint (gap B6); if the target is missed, implement the lazy `Node` over `JsonElement` noted in section 11 |
| aspire API data: render about 24k pages | wall under 60 s, peak RSS under 2.5 GB | Replaces about 18 minutes and an 8 GB heap today |
| Worker step round trip | under 2 ms per file of overhead beyond the worker's own work | JSON lines, no process start per file |

Memory is measured as peak RSS of the Douze process only; worker processes are reported
separately.

Douze runs on .NET 11 with runtime async and the non concurrent GC since the integration: on .NET 11 the
concurrent GC throttles the allocating threads (40% slower on the synthetic site), without it the synthetic site
builds in 33 s instead of 43 s on .NET 10 (`bench/README.md`, ".NET 11 and runtime async", finding 7). Phase 5
re-measured the GC modes after its memory changes: non concurrent stays the default (Server GC with DATAS ties, the
others are slower, none lowers the peak, which is the live set).

*Phase 5 results* (`bench/README.md`, "Phase 5"): every target is met except the synthetic site's peak RSS (2.3 GB
clean without the cache, 2.7 GB writing it, against 1.5 GB): pages wait in the unbounded channels in front of `write`,
the slowest step; backpressure on the channels that do not feed a barrier is the follow-up.

### 9.3 Validation beyond tests

- Build the Sample and a copy of the Sample with a `douze.yml` that reproduces the default
  workflow explicitly; outputs must be identical.
- Run the synthetic site through `--trace` and `--timeline` and check in the Perfetto UI that
  `markdown` and `render` overlap (streaming) and that `collect` is a single short span.
- Phase 3: run the three command modes with real Node on macOS, Linux and Windows (CI matrix).

---

## 10. Gaps from `aspire-dev-gap.md`

| Gap | Status | Where |
|---|---|---|
| A1 passthrough copy of every file type | Solved naturally: unclaimed files flow to `write` | 1b |
| A2 `{% include %}` / `{% render %}` file provider | Solved naturally: `TemplateLoader` is the Fluid file provider | 1a, 1b |
| A4 Liquid before Markdown | Solved: the default workflow runs Liquid first, `liquid: false` opts a page or folder out, `douze.yml` can order steps differently | 2 (configurable), integration (default flip, open question 3) |
| A12 / B1 data keyed by file name and folder | Solved: `data` step, `scope: site` | 1b |
| A13 / B2 pagination with permalink templates | Solved: `paginate` resolves `permalink` per page | 1b |
| A14 non-HTML templates | Solved by `match` on `liquid` and `layout`, and `.md` outputs through `with.permalink` | 2 (needs YAML to express) |
| A17 ignore and draft rules | `permalink: false` (1b) and `inputs` exclusions (2) | 1b, 2 |
| A18 site config file | `site:` section | 2 |
| A26 dev server with watch and live reload | Phase 6 | 6 |
| A27 build errors fail the build with file and line | Solved: `BuildError`, exit code 1, Fluid positions | 1a, 1b |
| A29 / B15 quiet logging and progress | Solved: one line per step, live view, `--quiet` | 1b, 4 |
| A30 `Program.cs` folder bug | Solved: `SiteOptions.DetectAsync` | 1b |
| B4 shared, never-cloned data | Solved: `Metadata` + `ScopeSet` | 1a |
| B5 parse templates once | Solved: `TemplateLoader` cache by content hash | 1b |
| B6 memory for large JSON | Measured in phase B; lazy `Node` over `JsonElement` is a follow-up if the target is missed | B |
| B7 multiple outputs from the same data | Two `liquid` steps over the same `parse` output (4.7) | 2 |
| B9 / B10 / B11 / B14 xref, api tag, pre-highlighted signatures, slugs | Generator side in aspire.dev, not Douze | out of scope |
| B12 per-package sidebar cache | `group-by: file` renders once per package; a `{% cache %}` tag is a follow-up | out of scope |
| A8 twoslash, A22 OG images, A23 Pagefind | `worker`, `batch`, `batch` command steps | 3 |
| A3 shortcodes, A5 Markdig extensions, A6 headings, A16 Markdown includes, A10 sidebar helpers, A11 i18n, A19/A20 images and fingerprints, A25 git dates, A28 link validation | Independent features that fit as built-in steps or filters after phase 2; `with.extensions` on `markdown` is wired in 1b | separate work |
| A7 highlighting | Decision outside this plan; a `worker` step with Shiki is the lowest-risk path | separate work |

---

## 11. Risks and open questions

Each item has a recommended answer; implementers use it unless the user decides otherwise.

1. **stderr fails the step by default, but Node prints warnings to stderr** (for example
   `ExperimentalWarning` for `--experimental-*` flags, and some tools print progress there). The
   brief's decision stands: `stderr: fail` is the default. The plan adds `stderr: warn | ignore` per
   step and the README tells Node users to set `NODE_NO_WARNINGS=1` in `env`. Recommended: keep.

2. **Data keyed by file name changes today's `_data` semantics** (a file's top-level keys were
   merged into the site). The Sample and two tests must change (`layout.json` containing
   `"layout.html"`). Recommended: adopt Eleventy's convention without a compatibility switch, as
   `AGENTS.md` allows; it is required by gaps A12 and B1.

3. **Liquid before Markdown as the default.** Flipping the default changes the Sample output (the
   captured `footer` in `index.md` would hold raw Markdown). Recommended: keep Markdown first in
   the default workflow during this plan; sites choose their order in `douze.yml`; flip the default
   in a separate change that updates the Sample, after phase 2.
   *Resolved at the integration of phases 2 to B (approved by the user):* Liquid runs first, with
   `liquid: false` as the per-page (or per-folder) opt-out; the Sample's `footer` capture is written
   in HTML. See 4.6.

4. **`page.date` rendering parity.** Today `page.date` reaches Fluid as a `NodeValue<DateTime>`
   wrapped in a reflection `ObjectValue`, and the `date` filter parses its `ToString()`. The explicit
   `DateTimeValue` conversion of phase 1a may apply a different time zone or precision, which
   would change `posts/post1.md`'s permalink and the `Content modified on` line. Recommended: in
   phase 1a's `NodeValuesTests`, render `{{ d | date: '%Y/%m/%d %H:%M:%S' }}` through the old and
   the new paths for a UTC value near midnight and make them equal before merging; the comparison
   script catches any residual difference.

5. **Pass-through semantics of `match` might surprise users** when a `run` step expects to see
   every file. The rule is uniform (consumed files are replaced by the step's outputs, the rest
   pass through) and `--print-workflow` plus `--verbose` make it visible. Recommended: keep;
   document with the table in 4.3.

6. **Unbounded channels.** Memory is bounded by the number of files because content is shared,
   and a bounded channel feeding a reduce barrier cannot deadlock (the reduce node drains
   continuously). Recommended: unbounded; revisit only if the benchmark shows pressure.

7. **Global CPU slots and command steps.** A command step waiting on an external process should
   not hold a CPU slot. Recommended (and specified in 2.6): command steps take slots from
   `BuildContext.ProcessSlots` (a second semaphore sized to `MaxDegreeOfParallelism`), built-in
   steps take `Parallelism` slots.

8. **`HttpListener` under native AOT** is untested. Recommended: verify first in phase 6; fall back
   to a 150-line `TcpListener` server.

9. **Large worker payloads.** A 14 MB JSON file sent inline through a JSON line works in Node but
   costs a base64 or escaping pass. Recommended: inline for protocol 1; reserve `contentPath` (a
   temp file instead of `content`) for protocol 2 if a benchmark shows it matters.

10. **Scope precedence between several reduce contributions** reaching one step (two `collect`
    steps with different names, or a future `sitemap` step). Recommended: descending topological
    index, as specified; contributions never share top-level keys in the default workflow, and the
    YAML author controls names.

11. **`with` on command steps.** Section 4.5 forbids `with` without `uses`, but the worker
    handshake carries `options`. Recommended: allow `with` on `run` steps with `mode: worker`
    only; the loader rule becomes "'with' is only valid with 'uses' or with 'mode: worker'".
    *Resolved in phase 3:* the command steps raise that message (with the step name) for `with` on a
    filter or batch step, so the rule holds for workflows built in code too.

12. **Group barrier.** Groups wait for the whole input, like reduce, because a group cannot be
    known complete earlier. For `group-by: file` this delays streaming by one step. Recommended:
    accept; a `group-by: file` command batch is the only case and it is dominated by the process
    cost anyway. A future optimization can treat `group-by: file` as map.

13. **Regex-based globs** are interpreted under AOT. At 32k files and about 20 patterns this is
    well under 100 ms. Recommended: keep; `GlobSet.FixedPrefix` narrows enumeration where it
    matters.

14. **Node tree footprint for 78 MB of JSON** (gap B6). Recommended: measure in phase B; if the
    4 s / 1.2 GB target is missed, add a `JsonElement` backed `Node` implementation behind the
    same abstract class, used by the `data` and `parse` steps for files over 1 MB.

15. **Trace lane assignment is not deterministic** across runs. It is diagnostic output only.
    Recommended: accept.

16. **Incremental correctness when a step implementation reads scopes directly.** Forbidden by the
    plan; the review passes of phases 1a, 5 and 6 check every step for `TryResolve` usage. A step
    that cannot record its reads must set `Incremental = false` in its definition.

17. **CLI flags removed** (`-c`, `-d`, `-i`). Recommended: remove now, before any public release,
    and document `site:` in the README.

18. **Every file is retained in memory until the build ends.** A consumed file is loaded to be
    hashed (its ref is part of every result key) and `StepRun.Results` keeps every output for the
    replay, so a one-shot build holds the whole site's content, including images and videos that
    only pass through to `write`. Recommended: accept for now and measure in phase B (gap B6 covers
    the data side); if it matters, phase 5 can hash pass-through files by streaming and release
    the content of files already written when not in watch mode.
    *Phase 5:* unit results keep refs, not files (`UnitResult.Files` only with `BuildOptions.KeepFiles`, for tests),
    `BuildResult.Outputs` is refs, `write` keeps the paths it wrote, the source files are released once every root has
    read them, and page templates are held weakly by the template cache. A file's content stays in memory while a step
    or a channel holds it; a replayed file's content is read from the cache only when a step reads it.

19. **The default workflow cannot paginate collections.** `paginate` runs before `collect` (4.6), so
    `pagination.data: collections.post`, the blog index pattern of Eleventy, resolves to nothing and
    the page passes through unchanged. Reordering to `permalink -> collections -> paginate -> render`
    would work: `paginate` already resolves each page's `permalink` and path itself and would set
    `url` too; the paginated source file would then be in `collections.all` once, unpaginated, which
    is Eleventy's default (`addAllPagesToCollections` off); the barrier before `render` already
    exists (it needs `collections`), so nothing streams later. The Sample output would not change.
    Recommended: keep 4.6 during this plan (a `douze.yml` chooses its order from phase 2), and flip
    the default in a separate change after phase 2, together with open question 3.
    *Resolved at the integration of phases 2 to B (approved by the user):* the default is
    `permalink -> collections -> paginate -> render -> markdown -> layout` (4.6). Markdown pages keep
    their `.md` extension through `permalink` so that steps can still match them as Markdown.
    `DefaultWorkflowTests.PagesCanPaginateCollections` covers it.

20. **Fluid's `for ... limit:` enumerates the whole array** (benchmark finding 1): a nav include
    looping over `collections.all limit: 50` costs O(N) per page, O(N²) per build (39.5 s against
    6.9 s for the 32k synthetic site when the loop indexes instead). Recommended: fix it in Fluid
    (`limit`/`offset` should stop the enumeration, and `NodeArrayValue` can expose indexed access),
    as a candidate for phase 5, following the "Fluid changes" rule of `AGENTS.md` (separate Fluid
    worktree and branch, tests in Fluid, validated in Douze with a locally packed preview; Douze keeps
    the published preview until the change ships).
    *Resolved in phase 5:* Fluid 3.0.0-preview-780 adds `IFluidArray` (sebastienros/fluid#1057), which
    `NodeArrayValue` implements: `for` with `limit`/`offset`, `tablerow` and the `first`, `last` and `slice` filters
    convert only the items they use. The synthetic 32k site builds in 6.1 s instead of 34.3 s (`bench/README.md`).

---

## Appendix A: the Sample through the default workflow

- `_data/characters.yml`, `_data/colors.json`, `_data/layout.json` enter `site-data` and become
  the keys `characters`, `colors`, `layout` of the `data` site scope. No files leave the step.
- `_content/articles/articles.yml` and `_content/articles/article1/douze.yml` enter `folder-data`
  and become the folder scopes of `/_content/articles/` and `/_content/articles/article1/`.
- Every other file under `_content/` enters `pages`. Markdown and HTML files get their front
  matter in Own and `page.date`; `posts/images/*.jpg` pass through untouched.
- `permalink` computes paths: `/index.md`, `/articles/index.md`, `/articles/article1/index.md`,
  `/articles/article2/index.md`, `/posts/2020/01/01/my-awesome-title/index.md` (from the fixed
  modification time of the comparison script), `/posts/api.json`, `/products/index.md`, and strips
  `/_content/` (Markdown pages keep `.md` until `markdown`; their `url` is the `.html` path). Images
  keep their path minus `/_content/`.
- `collections` builds `all` and `article` (two articles, sorted by date then path) and emits no
  files.
- `paginate` expands `products.md` into 4 pages, `/products/index.md`, `/products/1/index.md`, ...,
  each with its `url`; everything else passes through.
- `render` evaluates Liquid in every `.md`, `.html` and `.json` file with Own, the folder scopes,
  `collections` and the data scope in reach; the `footer` capture of `index.md` lands in its Own.
- `markdown` renders the `.md` files to `.html`; `api.json` passes through.
- `layout` resolves `layout` to `articles.html` for the articles (folder scope), `layout.html` for
  the others (data scope), `null` for `api.html` (own front matter), chains `articles.html` to
  `layout.html`, and uses the layout's `title: My Rad Blog` only when nothing above defines one.
- `write` writes everything, including the two images.

## Appendix B: test inventory after phase 6

| Area | Existing | Added | Notes |
|---|---|---|---|
| Nodes | 8 | 0 | unchanged |
| File systems | 8 | 0 | unchanged |
| Globs | 5 | 5 | `IncludeRuleTests` becomes `GlobTests` |
| Common (end to end through the default workflow) | 15 | 0 | rewritten against `SiteBuilder` |
| Engine core (1a) | | about 60 | hash, content, metadata, scopes, graph, scheduler, views, trace |
| Built-in steps (1b) | | about 34 | |
| YAML (2) | | about 25 | |
| Commands (3) | | about 27 | plus 3 Node tests skipped without `node` |
| Console and trace (4) | | about 15 | |
| Cache and early cutoff (5) | | about 16 | |
| Watch and serve (6) | | about 14 + 25 seeds | the incremental-equals-clean theory |
