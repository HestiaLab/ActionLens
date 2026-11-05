namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL005 — a job declares no <c>timeout-minutes</c> of its own.
/// </summary>
/// <remarks>
/// Info, not warning: the workflow does exactly what it says, and GitHub's own default (360 minutes for a job,
/// see the usage-limits page) applies. The finding is worth making because a hung test run holds a hosted
/// runner for six hours before that default stops it, and the author usually meant five minutes.
/// <para>
/// A job that calls a reusable workflow (<c>uses:</c> instead of <c>steps:</c>) is skipped: the workflow-call
/// job has no <c>timeout-minutes</c> key to write, so asking for one would be a finding nobody can fix.
/// </para>
/// </remarks>
public sealed class MissingTimeoutRule : ILintRule
{
    public LintRuleDescriptor Descriptor => LintRuleTable.MissingTimeout;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<LintFinding>();
        foreach (var job in context.Workflow.Jobs)
        {
            if (job.TimeoutMinutes is not null || job.ReusableWorkflow is not null)
            {
                continue;
            }

            findings.Add(new LintFinding
            {
                Message = $"The job '{job.Id}' declares no timeout-minutes, so it can hold a runner for the " +
                    "full 360 minutes GitHub allows a job before it stops it.",
                Range = job.KeyRange.IsKnown ? job.KeyRange : job.Sources.Get("job"),
                Suggestion = $"Add `timeout-minutes:` to '{job.Id}' with the number the work actually needs — " +
                    "15 is a common choice for a build and test job.",
                Scope = job.Id,
            });
        }

        return findings;
    }
}
