using ActionLens.Core.Expressions;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// The evaluation position of <c>jobs.&lt;job_id&gt;.steps.&lt;n&gt;.if</c>.
/// </summary>
/// <remarks>
/// Every context GitHub allows a step condition to read is allowed here — <c>env</c> built from the
/// workflow → job → step chain, <c>matrix</c> and <c>strategy</c> from the combination under test,
/// <c>needs</c> from the computed upstream outcomes — with one exception that is not an exception:
/// <c>secrets</c>, which no <c>if:</c> position may read.
/// <para>
/// <c>steps</c>, <c>job</c> and <c>runner</c> stay unknown because ActionLens evaluates conditions and
/// never executes steps, so there is no finished step, no running job and no machine to describe. That is
/// a knowledge gap, not an illegality, and the reason tree says which.
/// </para>
/// <para>
/// Status functions look at the <em>job's</em> outcome, which is what the runner does: a step skipped by
/// its own condition does not make a later step's <c>success()</c> false.
/// </para>
/// </remarks>
internal sealed class StepPositionContext : EvaluationPosition
{
    public StepPositionContext(EvaluationScope scope)
        : base(scope, EvaluationPositionKind.Step)
    {
    }

    public override bool TryGetStatus(StatusFunction statusFunction, out TriValue value)
    {
        value = statusFunction switch
        {
            StatusFunction.Success => SuccessOfJob(Scope.JobOutcome),
            StatusFunction.Failure => FailureOfJob(Scope.JobOutcome),
            StatusFunction.Always => TriValue.True,
            _ => CancelledFor(Scope.JobOutcome, $"the job '{Scope.Job.Id}'"),
        };
        return true;
    }

    private TriValue SuccessOfJob(JobOutcome outcome) => outcome switch
    {
        JobOutcome.Success => TriValue.True,
        JobOutcome.Failure => TriValue.False,
        JobOutcome.Cancelled => TriValue.False,
        JobOutcome.Skipped => TriValue.Unknown(new UnknownReason(
            UnknownReasonKind.ScenarioDependent,
            $"This job is skipped in the scenario, and whether a skipped job's success() reads as success is " +
            "the unresolved question the documentation leaves open — so the steps inside it are unknown too.",
            Scope.ConditionRange)),
        _ => TriValue.Unknown(new UnknownReason(
            UnknownReasonKind.ScenarioDependent,
            $"The scenario does not settle how job '{Scope.Job.Id}' ends, so success() has no answer here.",
            Scope.ConditionRange)),
    };

    private TriValue FailureOfJob(JobOutcome outcome) => outcome switch
    {
        // The step-level documentation is explicit that cancellation counts as a failure here, which is the
        // one place the job-level question above is not open.
        JobOutcome.Failure or JobOutcome.Cancelled => TriValue.True,
        JobOutcome.Success => TriValue.False,
        JobOutcome.Skipped => TriValue.Unknown(new UnknownReason(
            UnknownReasonKind.ScenarioDependent,
            "The job was skipped, and no source says what failure() means inside a job that never started.",
            Scope.ConditionRange)),
        _ => TriValue.Unknown(new UnknownReason(
            UnknownReasonKind.ScenarioDependent,
            $"The scenario does not settle how job '{Scope.Job.Id}' ends, so failure() has no answer here.",
            Scope.ConditionRange)),
    };
}
