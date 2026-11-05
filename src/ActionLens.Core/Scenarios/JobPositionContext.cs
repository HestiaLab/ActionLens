using ActionLens.Core.Expressions;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// The evaluation position of <c>jobs.&lt;job_id&gt;.if</c>.
/// </summary>
/// <remarks>
/// This is where context legality is encoded. The condition is read before the job starts, so
/// <c>env</c>, <c>matrix</c>, <c>strategy</c>, <c>steps</c>, <c>job</c> and <c>runner</c> are refused as
/// <see cref="UnknownReasonKind.ContextNotAvailable"/> rather than treated as unknown values, and
/// <c>secrets</c> is refused because it is in no position's list at all. The status functions are answered
/// from the upstream jobs the engine already resolved, which is why this class takes the computed
/// <see cref="EvaluationScope.Needs"/> instead of asking the run anything.
/// </remarks>
public sealed class JobPositionContext : EvaluationPosition
{
    public JobPositionContext(EvaluationScope scope)
        : base(scope, EvaluationPositionKind.Job)
    {
    }

    /// <summary>
    /// A job's own status functions look at the jobs it needs, not at anything that has run inside it:
    /// the job has not started yet when its condition is read.
    /// </summary>
    public override bool TryGetStatus(StatusFunction statusFunction, out TriValue value)
    {
        value = statusFunction switch
        {
            StatusFunction.Success => SuccessOfNeeds(Scope.Needs, "success()"),
            StatusFunction.Failure => FailureOfNeeds(Scope.Needs),
            StatusFunction.Always => TriValue.True,
            _ => CancelledFor(Scope.JobOutcome, $"the job '{Scope.Job.Id}'"),
        };
        return true;
    }
}
