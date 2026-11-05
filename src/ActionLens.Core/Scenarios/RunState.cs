using ActionLens.Core.Values;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// Whether a job or a step is going to run in the scenario the analysis was asked about.
/// </summary>
/// <remarks>
/// <see cref="WillNotRun"/> is a definite answer, earned by a condition that is definitely false. A job
/// ActionLens cannot decide goes to <see cref="Unknown"/>, never to <see cref="WillNotRun"/>: the set of
/// jobs shown as running must never be smaller than the set GitHub would run, and it must never be
/// larger than the set the evidence supports either.
/// </remarks>
public enum RunState
{
    WillRun,
    WillNotRun,
    Unknown,
}

/// <summary>
/// How a job ends, which is what the <c>needs.*.result</c> context and the status functions read. The
/// four values mirror the results Actions reports, plus <see cref="Unknown"/> for the jobs the scenario
/// and the conditions leave undecided.
/// </summary>
public enum JobOutcome
{
    Success,
    Failure,
    Skipped,
    Cancelled,

    /// <summary>Neither the scenario nor the condition settles how this job ends.</summary>
    Unknown,
}

/// <summary>
/// One job a condition can read through <c>needs</c>: what the scenario says it ended as, and what it
/// published. Only outputs whose values are static are in <see cref="Outputs"/>; the rest exist after a
/// run, which is what the engine's <c>SCEN002</c> finding is for.
/// </summary>
public sealed record NeedOutcome(
    string JobId,
    JobOutcome Outcome,
    IReadOnlyDictionary<string, GitHubValue> Outputs);

/// <summary>Actions' own spelling of a job result, as it appears in <c>needs.&lt;id&gt;.result</c>.</summary>
public static class JobOutcomeText
{
    public static string Of(JobOutcome outcome) => outcome switch
    {
        JobOutcome.Success => "success",
        JobOutcome.Failure => "failure",
        JobOutcome.Skipped => "skipped",
        JobOutcome.Cancelled => "cancelled",
        _ => "unknown",
    };
}
