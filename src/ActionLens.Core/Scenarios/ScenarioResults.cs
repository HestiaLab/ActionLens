using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Text;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// One <c>if:</c> condition, answered, with everything that explains the answer.
/// </summary>
/// <remarks>
/// <see cref="EvaluatedAs"/> is the tree the engine actually walked, rendered back as normalised
/// expression text. That is where the implicit status check becomes visible: a job written as
/// <c>if: github.ref == 'refs/heads/main'</c> is evaluated as
/// <c>success() &amp;&amp; (github.ref == 'refs/heads/main')</c>, and a reader who does not see the wrap
/// cannot tell why the job does not run when an ancestor failed.
/// </remarks>
public sealed record ConditionTrace
{
    /// <summary>The condition as the author wrote it, empty when they wrote none.</summary>
    public required string Text { get; init; }

    /// <summary>The expression that was evaluated, after the implicit <c>success()</c> was applied.</summary>
    public required string EvaluatedAs { get; init; }

    public required TriKind Result { get; init; }

    /// <summary>True when ActionLens added the status check GitHub adds; the author never wrote one.</summary>
    public required bool ImplicitSuccessApplied { get; init; }

    /// <summary>Set exactly when <see cref="Result"/> is <see cref="TriKind.Unknown"/>.</summary>
    public UnknownReason? Reason { get; init; }

    public SourceRange Range { get; init; } = SourceRange.Unknown;

    public bool IsKnown => Result != TriKind.Unknown;

    public override string ToString() =>
        $"{EvaluatedAs} = {Result.ToString().ToLowerInvariant()}";
}

/// <summary>One step of one job, under one matrix combination.</summary>
public sealed record StepRunResult
{
    public required int Index { get; init; }

    /// <summary>The step's <c>id:</c>, which is how <c>steps.&lt;id&gt;</c> addresses it. Null when unnamed.</summary>
    public string? StepId { get; init; }

    public string? Name { get; init; }

    public required RunState State { get; init; }

    /// <summary>
    /// Null when the step's condition was not read because its job does not run: a step inside a skipped
    /// job never reaches its <c>if:</c>, so showing an answer for it would be a guess.
    /// </summary>
    public ConditionTrace? Condition { get; init; }

    /// <summary>Set whenever <see cref="State"/> is <see cref="RunState.Unknown"/>; never set otherwise.</summary>
    public UnknownReason? Reason { get; init; }
}

/// <summary>
/// One job instance: a matrix combination, or the single run of a job that has no matrix.
/// </summary>
public sealed record MatrixRun
{
    public required int Index { get; init; }

    /// <summary>
    /// False when the combination itself is not knowable — a dimension fed by <c>fromJSON()</c> — which is
    /// recorded rather than papered over with an empty value set that would read as "no matrix".
    /// </summary>
    public required bool CombinationIsKnown { get; init; }

    /// <summary>The combination as labelled text, e.g. <c>ubuntu-latest, 22.04</c>. Null for a job with no matrix.</summary>
    public string? Label { get; init; }

    /// <summary>Matrix keys to their text, in the order the combination holds them.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<StepRunResult> Steps { get; init; } = Array.Empty<StepRunResult>();
}

/// <summary>What the scenario decided about one job.</summary>
internal sealed record JobScenarioResult
{
    public required string JobId { get; init; }

    public required RunState State { get; init; }

    /// <summary>
    /// How the job ends, which is what a dependent's status functions and <c>needs.*.result</c> read.
    /// A job that does not run ends as <see cref="JobOutcome.Skipped"/>; a job whose order cannot be
    /// derived ends as <see cref="JobOutcome.Unknown"/>.
    /// </summary>
    public required JobOutcome Outcome { get; init; }

    public ConditionTrace? Condition { get; init; }

    /// <summary>Set whenever <see cref="State"/> is <see cref="RunState.Unknown"/>; never set otherwise.</summary>
    public UnknownReason? Reason { get; init; }

    /// <summary>True when the user pinned this job rather than the engine deriving it from the DAG.</summary>
    public required bool IsPinnedByScenario { get; init; }

    public ScenarioJobState? ScenarioPin { get; init; }

    /// <summary>The jobs this one needs, with the outcome each was given.</summary>
    public IReadOnlyList<NeedOutcome> Needs { get; init; } = Array.Empty<NeedOutcome>();

    /// <summary>One entry per matrix combination, or one synthetic entry when the matrix is dynamic.</summary>
    public IReadOnlyList<MatrixRun> Runs { get; init; } = Array.Empty<MatrixRun>();

    /// <summary>The job name as the author wrote it, kept as text because it usually holds an expression.</summary>
    public string? DeclaredName { get; init; }
}

/// <summary>
/// The whole run the scenario describes: every job in document order, with its state, its condition
/// trace and its steps, plus the findings the three positions made on the way.
/// </summary>
internal sealed record ScenarioEvaluation
{
    public required IReadOnlyList<JobScenarioResult> Jobs { get; init; }

    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    public JobScenarioResult? Job(string jobId) =>
        Jobs.FirstOrDefault(result => string.Equals(result.JobId, jobId, StringComparison.Ordinal));

    public JobOutcome OutcomeOf(string jobId) =>
        Job(jobId)?.Outcome ?? JobOutcome.Unknown;

    public int CountWith(RunState state) => Jobs.Count(result => result.State == state);
}
