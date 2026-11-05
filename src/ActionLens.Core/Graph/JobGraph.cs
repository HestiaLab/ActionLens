using ActionLens.Core.Workflows;

namespace ActionLens.Core.Graph;

/// <summary>
/// One <c>needs</c> relationship: <see cref="Job"/> cannot start before <see cref="Needs"/> has
/// finished. The direction is the one the author wrote, so a message about this edge can point at
/// <see cref="Job"/>'s <c>needs:</c> list.
/// </summary>
public sealed record JobGraphEdge
{
    /// <summary>The job that declares the dependency.</summary>
    public required string Job { get; init; }

    /// <summary>The job id that <see cref="Job"/> lists under <c>needs:</c>.</summary>
    public required string Needs { get; init; }

    public override string ToString() => $"{Job} -> {Needs}";
}

/// <summary>
/// The dependency shape of a workflow's jobs: who needs whom, whether that is legal, and in which
/// order the jobs could run. Built once from the workflow model and read many times — the type has
/// no setters, so a UI or a rule cannot quietly change the graph another consumer is walking.
/// </summary>
/// <remarks>
/// This answers structure only. Whether a job <em>will</em> run is the scenario engine's question:
/// it needs <c>if:</c>, matrix and trigger state, none of which live here.
/// </remarks>
public sealed class JobGraph
{
    private readonly string[] _jobIds;
    private readonly string[][] _dependencies;
    private readonly string[][] _dependents;
    private readonly bool[] _jobsInCycles;
    private readonly Dictionary<string, int> _index;

    /// <summary>
    /// Reads the jobs of <paramref name="workflow"/> and returns the graph with the findings that
    /// explain any illegal <c>needs</c>. Never throws for a malformed graph: a cycle leaves the
    /// acyclic part usable instead of discarding the whole analysis.
    /// </summary>
    public static JobGraphAnalysisResult Build(WorkflowDocument workflow) =>
        JobGraphAnalyzer.Analyze(workflow.Jobs);

    /// <summary>Same as <see cref="Build(WorkflowDocument)"/> for callers holding only the job list.</summary>
    public static JobGraphAnalysisResult Build(IReadOnlyList<JobDefinition> jobs) =>
        JobGraphAnalyzer.Analyze(jobs);

    internal JobGraph(
        string[] jobIds,
        string[][] dependencies,
        string[][] dependents,
        JobGraphEdge[] edges,
        string[] topologicalOrder,
        string[] blockedJobs,
        string[][] cycles,
        bool[] jobsInCycles)
    {
        _jobIds = jobIds;
        _dependencies = dependencies;
        _dependents = dependents;
        _jobsInCycles = jobsInCycles;
        Edges = edges;
        TopologicalOrder = topologicalOrder;
        BlockedJobs = blockedJobs;
        Cycles = cycles;
        JobsInCycles = [.. jobIds.Where((_, i) => jobsInCycles[i])];
        IsAcyclic = cycles.Length == 0;

        // Job ids are unique in a valid workflow, so this is a plain lookup; document order is kept
        // separately in _jobIds because slots and messages both need it.
        _index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < jobIds.Length; i++)
        {
            _index.TryAdd(jobIds[i], i);
        }
    }

    /// <summary>Job ids in document order — the order the author wrote them, which layout slots follow.</summary>
    public IReadOnlyList<string> JobIds => _jobIds;

    public int JobCount => _jobIds.Length;

    /// <summary>True when the graph contains no job id.</summary>
    public bool IsEmpty => _jobIds.Length == 0;

    /// <summary>Every accepted edge, ordered by job (document order) and then by written <c>needs</c> order.</summary>
    public IReadOnlyList<JobGraphEdge> Edges { get; }

    /// <summary>
    /// Execution order — a job always appears after everything it needs — for the part of the graph
    /// that has no cycle. Jobs in or downstream of a cycle are absent; see <see cref="BlockedJobs"/>.
    /// </summary>
    /// <remarks>
    /// Ties are broken by document order, so the sequence is a property of the input, not of the
    /// traversal that produced it.
    /// </remarks>
    public IReadOnlyList<string> TopologicalOrder { get; }

    /// <summary>
    /// Jobs missing from <see cref="TopologicalOrder"/>: the ones inside a cycle and the ones that
    /// (transitively) depend on one. Their run order genuinely cannot be derived, so the layout
    /// refuses to guess instead of placing them somewhere plausible.
    /// </summary>
    public IReadOnlyList<string> BlockedJobs { get; }

    /// <summary>True when every job is in <see cref="TopologicalOrder"/>, i.e. no cycle was found.</summary>
    public bool IsAcyclic { get; }

    /// <summary>
    /// The cycles that were found, each written as the job path it closes
    /// (<c>build -&gt; deploy -&gt; build</c>, a self-reference as <c>build -&gt; build</c>), in
    /// document order of the job that closes it. A cycle is listed once.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; }

    /// <summary>Every job that takes part in at least one cycle, in document order.</summary>
    public IReadOnlyList<string> JobsInCycles { get; }

    /// <summary>True when <paramref name="jobId"/> is part of a cycle, whatever direction it was reached from.</summary>
    public bool IsInCycle(string jobId) => _index.TryGetValue(jobId, out var i) && _jobsInCycles[i];

    /// <summary>True when <paramref name="jobId"/> is a job of this workflow.</summary>
    public bool Contains(string jobId) => _index.ContainsKey(jobId);

    /// <summary>
    /// The jobs <paramref name="jobId"/> needs, in written order, after duplicates and unknown ids
    /// were removed. Empty for an id this graph does not have, so a UI can ask about a filtered job
    /// without a guard.
    /// </summary>
    public IReadOnlyList<string> DependenciesOf(string jobId) =>
        _index.TryGetValue(jobId, out var i) ? _dependencies[i] : Array.Empty<string>();

    /// <summary>Jobs that list <paramref name="jobId"/> in their <c>needs:</c>, in document order.</summary>
    public IReadOnlyList<string> DependentsOf(string jobId) =>
        _index.TryGetValue(jobId, out var i) ? _dependents[i] : Array.Empty<string>();

    /// <summary>
    /// Everything <paramref name="jobId"/> transitively needs. The job itself is never in the result,
    /// even when a cycle makes it its own ancestor; visitation is guarded, so a cyclic graph
    /// terminates. Ordered by document position for stable lists.
    /// </summary>
    public IReadOnlyList<string> AncestorsOf(string jobId) => Reachable(jobId, _dependencies);

    /// <summary>
    /// Everything that transitively needs <paramref name="jobId"/>, i.e. the jobs a failure here can
    /// stop from running. Excludes the job itself and terminates on cyclic graphs.
    /// </summary>
    public IReadOnlyList<string> DescendantsOf(string jobId) => Reachable(jobId, _dependents);

    private string[] Reachable(string jobId, string[][] step)
    {
        if (!_index.TryGetValue(jobId, out var start))
        {
            return [];
        }

        var seen = new HashSet<int>();
        var pending = new Queue<int>();
        foreach (var neighbour in step[start])
        {
            if (_index.TryGetValue(neighbour, out var first) && first != start && seen.Add(first))
            {
                pending.Enqueue(first);
            }
        }

        while (pending.Count > 0)
        {
            foreach (var neighbour in step[pending.Dequeue()])
            {
                // Refusing to revisit, and refusing to count the queried job as its own ancestor, is
                // what makes this terminate on a cyclic graph.
                if (_index.TryGetValue(neighbour, out var next) && next != start && seen.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return [.. _jobIds.Where((_, i) => seen.Contains(i))];
    }

    /// <summary>
    /// The layout grid: longest-path layers, document-order slots. Unit-less coordinates — pixel maths
    /// belongs to the SVG component, which multiplies these by whatever cell size it chose.
    /// </summary>
    public LayeredLayout ToLayout() => LayeredLayout.Build(this);
}
