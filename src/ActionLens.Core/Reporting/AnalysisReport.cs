using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Graph;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Triggers;

namespace ActionLens.Core.Reporting;

/// <summary>
/// Everything one analysis answered: which jobs run under which scenario, the trace behind each answer,
/// the matrix instances, the findings from every stage, and the layout the DAG should be drawn in.
/// </summary>
/// <remarks>
/// The report is data, and it is the only thing the analysis gives a caller. A broken workflow still
/// produces one: <see cref="Diagnostics"/> carries the reason there is nothing else, and
/// <see cref="IsBlocked"/> says whether the model behind the numbers can be trusted.
/// <para>
/// Nothing in here is a timestamp, an identifier generated for the run, or a value that depends on
/// dictionary order, so two analyses of the same inputs export the same bytes — which is what a shared
/// report and a golden file both need.
/// </para>
/// </remarks>
public sealed record AnalysisReport
{
    /// <summary>
    /// The shape of the JSON export. Written into the export itself, so a reader can tell what it is
    /// holding before it interprets anything.
    /// </summary>
    public const int SchemaVersion = 1;

    public required WorkflowIdentity Workflow { get; init; }

    public required EventReport Event { get; init; }

    /// <summary>
    /// What the file declares under <c>on:</c>, and the verdict for the event that was asked about. The
    /// trigger answer has its own type rather than fields spread over the report, so it grows without
    /// reshaping anything else — which is how "did this trigger fire, and why not" arrived.
    /// </summary>
    public required TriggerReport Trigger { get; init; }

    public required ScenarioReport Scenario { get; init; }

    /// <summary>Every job in document order, with its state, condition trace, matrix and steps.</summary>
    public required IReadOnlyList<JobReport> Jobs { get; init; }

    /// <summary>The graph the jobs form, and the layer/slot grid to draw it in.</summary>
    public required GraphReport Graph { get; init; }

    /// <summary>
    /// Findings from every stage, in stage order: load, matrix, graph, scenario, trigger — then the lint
    /// layer's own findings, ordered by position and rule id. A finding a lint rule took over from a stage
    /// (AL004, AL006) keeps the stage's slot with the rule's id on it, so one problem never appears twice.
    /// </summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    public required ReportTotals Totals { get; init; }

    /// <summary>
    /// True when a finding of error severity means the numbers in this report must not be trusted — the
    /// workflow did not load, or its model is incomplete. The report is still the answer to give.
    /// </summary>
    public required bool IsBlocked { get; init; }
}

/// <summary>What the file is.</summary>
public sealed record WorkflowIdentity
{
    /// <summary>The <c>name:</c> the author wrote, or the file name when they did not write one.</summary>
    public required string Name { get; init; }

    public string? FileName { get; init; }

    /// <summary><c>run-name:</c> as written, when present.</summary>
    public string? RunName { get; init; }

    public required int JobCount { get; init; }

    /// <summary>The <c>permissions:</c> keys, which the report keeps because a missing one is a finding later.</summary>
    public IReadOnlyList<string> PermissionKeys { get; init; } = Array.Empty<string>();

    public string? ConcurrencyGroup { get; init; }

    public bool CancelInProgress { get; init; }
}

/// <summary>
/// The outside world the analysis was given: which event, and whether the caller supplied variables or
/// dispatch inputs. Recorded because an <c>unknown</c> that came from a missing input is a different
/// answer from one that came from the runtime.
/// </summary>
public sealed record EventReport
{
    public required bool HasEventPayload { get; init; }

    /// <summary>The event name the payload declared, null when it did not declare one.</summary>
    public string? EventName { get; init; }

    public required bool VarsSupplied { get; init; }

    public required bool InputsSupplied { get; init; }
}

/// <summary>
/// What <c>on:</c> declares, and — when an event was asked about — whether it fired.
/// </summary>
/// <remarks>
/// <see cref="Verdict"/> is the answer to "did this workflow run, and if not, which filter said no"; it is
/// null when nobody named an event, because that question has no answer for an event nobody chose. The
/// declared list stays beside it rather than inside it, so a report of a file's triggers reads the same
/// whether or not a payload came with the question.
/// </remarks>
public sealed record TriggerReport
{
    /// <summary>The declared events in the order they were written.</summary>
    public required IReadOnlyList<DeclaredTrigger> Declared { get; init; }

    /// <summary>True when <c>on:</c> was a bare string or list, e.g. <c>on: push</c>.</summary>
    public required bool IsShorthand { get; init; }

    /// <summary>The verdict for the event that was selected, null when none was.</summary>
    public TriggerVerdict? Verdict { get; init; }
}

/// <summary>One event under <c>on:</c>, with the filter keys it carries.</summary>
/// <remarks>
/// The filter <em>values</em> stay in the workflow model: this is the summary the report shows, and the
/// names are enough to tell a reader which filters a verdict would have had to consult.
/// </remarks>
public sealed record DeclaredTrigger(
    string Name,
    bool HasConfiguration,
    IReadOnlyList<string> DeclaredFilters);

/// <summary>The scenario this report answers, described in the report's own words.</summary>
public sealed record ScenarioReport
{
    /// <summary>The caller's label, or <c>"default"</c> when nobody named it.</summary>
    public required string Label { get; init; }

    /// <summary>The pins and the cancellation, in the order the jobs appear in the file.</summary>
    public required string Description { get; init; }

    public required bool WorkflowCancelled { get; init; }

    /// <summary>Jobs the user pinned, in document order, then any id the scenario named that the file has not.</summary>
    public IReadOnlyList<ScenarioPin> Pins { get; init; } = Array.Empty<ScenarioPin>();
}

/// <summary>One scenario pin, kept as text so an export stays readable when the enum grows.</summary>
public sealed record ScenarioPin(string JobId, ScenarioJobState State);

/// <summary>One job, with everything the analysis can say about it.</summary>
public sealed record JobReport
{
    public required string JobId { get; init; }

    /// <summary>The job's <c>name:</c> as written — usually an expression, so it is shown, not resolved.</summary>
    public string? DeclaredName { get; init; }

    public required RunState State { get; init; }

    /// <summary>How the job ends, which is what a dependent's status functions read.</summary>
    public required JobOutcome Outcome { get; init; }

    /// <summary>
    /// Set exactly when <see cref="State"/> is <see cref="RunState.Unknown"/>. An unknown without a reason
    /// is a bug, so nothing in the pipeline is allowed to produce one.
    /// </summary>
    public UnknownReason? Reason { get; init; }

    /// <summary>The job's own <c>if:</c>, after the implicit <c>success()</c> wrap.</summary>
    public ConditionTrace? Condition { get; init; }

    public IReadOnlyList<string> Needs { get; init; } = Array.Empty<string>();

    /// <summary>The upstream jobs and the outcomes this job's conditions were read against.</summary>
    public IReadOnlyList<NeedOutcome> NeedsOutcomes { get; init; } = Array.Empty<NeedOutcome>();

    /// <summary>The matrix summary for this job; null when it declares no matrix at all.</summary>
    public MatrixReport? Matrix { get; init; }

    /// <summary>One entry per combination, with the steps evaluated under it.</summary>
    public required IReadOnlyList<MatrixRun> Runs { get; init; }

    public required bool IsPinnedByScenario { get; init; }

    public ScenarioJobState? ScenarioPin { get; init; }

    /// <summary>Where the job sits in the layout, or null when the graph could not place it.</summary>
    public NodePlacement? Placement { get; init; }
}

/// <summary>A job's position in the layer/slot grid, in the units the drawing component scales.</summary>
public readonly record struct NodePlacement(int Layer, int Slot);

/// <summary>What one job's matrix turned into.</summary>
public sealed record MatrixReport
{
    public required bool IsDynamic { get; init; }

    /// <summary>Dimensions whose values only a run can produce, named so the reader knows what is missing.</summary>
    public IReadOnlyList<string> DynamicDimensions { get; init; } = Array.Empty<string>();

    /// <summary>Dimension names in the order the author wrote them.</summary>
    public IReadOnlyList<string> Dimensions { get; init; } = Array.Empty<string>();

    /// <summary>How many combinations remain after <c>exclude</c> and <c>include</c>; null when only a run can say.</summary>
    public int? CombinationCount { get; init; }

    /// <summary>How many the expansion produced before the 256-per-run cap, and whether the tail was dropped.</summary>
    public required int ProducedCount { get; init; }

    public required bool Truncated { get; init; }

    /// <summary>A label per combination, in expansion order — the job names a reader counts.</summary>
    public IReadOnlyList<string> CombinationLabels { get; init; } = Array.Empty<string>();
}

/// <summary>The dependency shape, the cycles in it, and the layout to draw it in.</summary>
public sealed record GraphReport
{
    public required bool IsAcyclic { get; init; }

    public required int JobCount { get; init; }

    public required int EdgeCount { get; init; }

    /// <summary>Each cycle written as the job path that closes it, e.g. <c>lint -&gt; lint</c>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; } = Array.Empty<IReadOnlyList<string>>();

    /// <summary>Jobs the graph could not order: inside a cycle or behind one.</summary>
    public IReadOnlyList<string> UnplacedJobs { get; init; } = Array.Empty<string>();

    public required IReadOnlyList<LayoutNode> Nodes { get; init; }

    public required IReadOnlyList<LayoutEdge> Edges { get; init; }

    public required int LayerCount { get; init; }

    public required int MaxLayerWidth { get; init; }
}

/// <summary>Counts, so a UI badge and a golden test can read the same numbers.</summary>
public sealed record ReportTotals
{
    public required int Jobs { get; init; }

    public required int WillRun { get; init; }

    public required int WillNotRun { get; init; }

    public required int UnknownJobs { get; init; }

    public required int Steps { get; init; }

    /// <summary>The total number of job instances a run would create, or null when a dynamic matrix hides it.</summary>
    public int? JobInstances { get; init; }

    public required int Diagnostics { get; init; }

    public required int Criticals { get; init; }

    public required int Errors { get; init; }

    public required int Warnings { get; init; }

    public required int Infos { get; init; }
}
