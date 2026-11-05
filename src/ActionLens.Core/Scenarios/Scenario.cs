namespace ActionLens.Core.Scenarios;

/// <summary>
/// How the scenario says one job went. A scenario is a claim about a run, not a computation: the user
/// pins jobs here and the engine derives everything else — which downstream jobs are skipped, what
/// <c>success()</c> answers, what <c>needs.*.result</c> holds.
/// </summary>
/// <remarks>
/// <see cref="Succeeds"/> is the standing default, so it is only ever written down to override a
/// previous pin. <see cref="IsSkipped"/> means the job never started: it is what the docs call a skipped
/// job, whose effect on a dependent's <c>success()</c> is unresolved, so the engine reports unknown
/// rather than choosing a reading there.
/// </remarks>
public enum ScenarioJobState
{
    Succeeds,
    Fails,

    /// <summary>The job did not run at all — the state a false condition or a skipped dependency produces.</summary>
    IsSkipped,

    /// <summary>The job started and the run was cancelled while it was going.</summary>
    IsCancelled,
}

/// <summary>
/// One imagined run of a workflow: the default (every job succeeds) with the jobs the user pinned
/// changed, plus whether the run was cancelled.
/// </summary>
/// <remarks>
/// Deliberately small. A scenario is not a scheduler: it says how jobs ended, and the condition
/// evaluator turns that into which jobs GitHub would have started. Cancellation is a single flag
/// rather than a per-moment timeline because <c>cancelled()</c> is the only thing that asks about it,
/// and the documentation does not settle whether that question is about the workflow or about the job
/// (see the ambiguities section of <c>docs/expression-semantics.md</c>) — so both scopes are recorded
/// and an answer that needs the missing one is unknown.
/// <para>
/// Records are compared by value, which is what lets a report say "this run is the default scenario"
/// without the caller having to keep the object alive.
/// </para>
/// </remarks>
public sealed record Scenario
{
    private static readonly IReadOnlyDictionary<string, ScenarioJobState> NoPins =
        new Dictionary<string, ScenarioJobState>(StringComparer.Ordinal);

    /// <summary>The scenario every job succeeds under, and the one <c>Analyze</c> uses when none is given.</summary>
    public static Scenario Default { get; } = new();

    /// <summary>
    /// A label for the report. Null means ActionLens describes the pins itself, which is what a
    /// programmatically built scenario gets; a UI can name the scenario the user chose.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The jobs the user pinned. Every job absent from this dictionary succeeds — the default scenario
    /// is an empty dictionary, not a special case.
    /// </summary>
    public IReadOnlyDictionary<string, ScenarioJobState> JobStates { get; init; } = NoPins;

    /// <summary>True when the run as a whole was cancelled.</summary>
    public bool WorkflowCancelled { get; init; }

    /// <summary>True when the scenario pins this job to something other than success.</summary>
    public bool IsPinned(string jobId) => JobStates.ContainsKey(jobId);

    /// <summary>The scenario's claim about one job; <see cref="ScenarioJobState.Succeeds"/> unless pinned.</summary>
    public ScenarioJobState StateOf(string jobId) =>
        JobStates.TryGetValue(jobId, out var state) ? state : ScenarioJobState.Succeeds;

    /// <summary>A scenario with <paramref name="jobId"/> pinned, leaving this one untouched.</summary>
    public Scenario WithJob(string jobId, ScenarioJobState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var pinned = new Dictionary<string, ScenarioJobState>(JobStates, StringComparer.Ordinal)
        {
            [jobId] = state,
        };
        return this with { JobStates = pinned };
    }

    public Scenario WithJobFailed(string jobId) => WithJob(jobId, ScenarioJobState.Fails);

    public Scenario WithJobSkipped(string jobId) => WithJob(jobId, ScenarioJobState.IsSkipped);

    public Scenario WithJobCancelled(string jobId) => WithJob(jobId, ScenarioJobState.IsCancelled);

    public Scenario WithWorkflowCancelled(bool cancelled = true) => this with { WorkflowCancelled = cancelled };

    /// <summary>
    /// The scenario in one line, listing the pins in <paramref name="documentOrder"/> so that the text
    /// is a property of the workflow rather than of the dictionary's enumeration order.
    /// </summary>
    public string Describe(IReadOnlyList<string> documentOrder)
    {
        ArgumentNullException.ThrowIfNull(documentOrder);

        var pins = documentOrder
            .Where(jobId => JobStates.TryGetValue(jobId, out _))
            .Select(jobId => $"{jobId} {Phrase(JobStates[jobId])}")
            .ToList();

        if (WorkflowCancelled)
        {
            pins.Add("the run was cancelled");
        }

        return pins.Count == 0
            ? "every job succeeds"
            : string.Join("; ", pins);
    }

    private static string Phrase(ScenarioJobState state) => state switch
    {
        ScenarioJobState.Succeeds => "succeeds",
        ScenarioJobState.Fails => "fails",
        ScenarioJobState.IsSkipped => "is skipped",
        _ => "is cancelled",
    };
}
