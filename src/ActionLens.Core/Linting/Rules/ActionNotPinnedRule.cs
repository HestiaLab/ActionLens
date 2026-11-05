namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL001 — a remote action is called by tag or branch instead of by commit.
/// </summary>
/// <remarks>
/// Tags move. <c>actions/checkout@v4</c> is a pointer that the action's own maintainers can retarget at any
/// commit, including one nobody reviewed, and the workflow keeps running the new code with no diff to review.
/// A full commit SHA cannot be retargeted, which is the whole difference and the whole finding.
/// <para>
/// Only <c>owner/repo@ref</c> and <c>owner/repo/path@ref</c> are judged; see
/// <see cref="ActionReference.TryParse"/> for the forms this refuses, and note that a 40-character hex ref is
/// the only accepted pin — a short SHA is called out as truncated rather than counted as pinned.
/// </para>
/// </remarks>
internal sealed class ActionNotPinnedRule : ILintRule
{
    public LintRuleDescriptor Descriptor => LintRuleTable.ActionNotPinned;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<LintFinding>();
        foreach (var (job, step) in context.Steps())
        {
            if (!ActionReference.TryParse(step.Uses, out var parsed) || parsed is null || parsed.IsPinnedToCommit)
            {
                continue;
            }

            findings.Add(new LintFinding
            {
                Message = Describe(parsed),
                Range = LintContext.StepRange(step, "uses"),
                Suggestion = $"Pin the commit instead of the ref: `uses: {parsed.Name}@<the 40-character SHA of " +
                    $"{parsed.Ref}>`, and keep `{parsed.Ref}` as the trailing comment so the pin still reads.",
                Scope = LintContext.StepScope(job, step),
            });
        }

        return findings;
    }

    private static string Describe(ActionReference reference) =>
        ActionReference.IsHex(reference.Ref) && reference.Ref.Length is >= 7 and < 40
            ? $"'{reference.Name}' is pinned to the {reference.Ref.Length}-character SHA '{reference.Ref}', " +
                "which the runner does not resolve: only the full 40-character commit SHA is immutable."
            : $"'{reference.Name}' is called by the ref '{reference.Ref}' rather than by a full commit SHA, " +
                "so whoever moves that tag or branch changes what this step runs.";
}
