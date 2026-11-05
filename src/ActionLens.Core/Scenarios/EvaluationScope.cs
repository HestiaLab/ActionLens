using ActionLens.Core.Expressions;
using ActionLens.Core.Matrix;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// Which workflow key an <c>if:</c> is written in. GitHub restricts contexts per key, so this is a
/// legality question the engine has to answer before any knowledge question — and it is the reason the
/// <see cref="IContextProvider"/> seam exists at all.
/// </summary>
public enum EvaluationPositionKind
{
    /// <summary>
    /// <c>jobs.&lt;job_id&gt;.if</c>. Evaluated before the job starts and, per the context availability
    /// table, before its matrix is applied — which is why <c>matrix</c>, <c>strategy</c>, <c>env</c>,
    /// <c>steps</c>, <c>job</c> and <c>runner</c> are all refused here.
    /// </summary>
    Job,

    /// <summary>
    /// <c>jobs.&lt;job_id&gt;.steps.&lt;step_id&gt;.if</c>. Reads one matrix combination and may name a
    /// runner context, but still cannot read <c>secrets</c>.
    /// </summary>
    Step,
}

/// <summary>
/// Everything one evaluation position needs: the workflow text, the scenario's claim about how the run
/// went, the expanded matrix combination, and whatever the caller supplied about the event.
/// </summary>
/// <remarks>
/// A record rather than eight constructor arguments because the two positions share almost all of it and
/// a caller must not be able to build a step position whose matrix belongs to another job. Everything in
/// here is data the analysis already has; nothing is fetched, timed or generated.
/// </remarks>
public sealed record EvaluationScope
{
    public required WorkflowDocument Workflow { get; init; }

    public required JobDefinition Job { get; init; }

    public required Scenario Scenario { get; init; }

    /// <summary>The caller's outside world; <see cref="AnalysisInput.Empty"/> when nothing was supplied.</summary>
    public AnalysisInput Input { get; init; } = AnalysisInput.Empty;

    /// <summary>
    /// The jobs this position reads through <c>needs</c>, in the order the author wrote them, each with
    /// the outcome the engine computed for it.
    /// </summary>
    public IReadOnlyList<NeedOutcome> Needs { get; init; } = Array.Empty<NeedOutcome>();

    /// <summary>How the job itself ends in this scenario. Only a step position reads it.</summary>
    public JobOutcome JobOutcome { get; init; } = JobOutcome.Success;

    /// <summary>The combination a step position is evaluated under. Null when there is nothing static to read.</summary>
    public MatrixCombination? Combination { get; init; }

    /// <summary>True when a matrix dimension is fed by an expression, which changes the wording of the reason.</summary>
    public bool MatrixIsDynamic { get; init; }

    /// <summary>The step a step position belongs to; null for a job position.</summary>
    public StepDefinition? Step { get; init; }

    /// <summary>
    /// Where the <c>if:</c> being evaluated is written, so that every reason this position produces is
    /// pointable. Expression nodes carry only line 1 positions for a value parsed on its own, which is
    /// why the reasons are re-based onto the YAML range instead.
    /// </summary>
    public SourceRange ConditionRange { get; init; } = SourceRange.Unknown;

    /// <summary>The scope for evaluating one of a job's step conditions.</summary>
    public EvaluationScope ForStep(StepDefinition step, JobOutcome jobOutcome, SourceRange conditionRange) =>
        this with
        {
            Step = step,
            JobOutcome = jobOutcome,
            ConditionRange = conditionRange,
        };

    /// <summary>The scope for evaluating a job's own condition, positioned on its <c>if:</c>.</summary>
    public EvaluationScope ForJobCondition(SourceRange conditionRange, JobOutcome jobOutcome) =>
        this with
        {
            Step = null,
            JobOutcome = jobOutcome,
            ConditionRange = conditionRange,
        };
}
