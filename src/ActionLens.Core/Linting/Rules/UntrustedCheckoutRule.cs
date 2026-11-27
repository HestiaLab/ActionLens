using ActionLens.Core.Workflows;

namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL002 — a <c>pull_request_target</c> workflow checks the pull request's own code out.
/// </summary>
/// <remarks>
/// The event is the switch that makes the shape dangerous. <c>pull_request_target</c> runs in the base
/// context — full secrets, the token scoped by the base file's <c>permissions:</c> — while the contributor's
/// code arrives as payload. Point <c>actions/checkout</c> at that payload's head revision and unreviewed code
/// is sitting on a runner that can read everything, which is the pwn request GitHub's own documentation names
/// for this event.
/// <para>
/// The two shapes read are the ones an author writes on purpose: <c>with: ref:</c> pointing at
/// <c>github.event.pull_request.head.*</c> or <c>github.head_ref</c>, and <c>with: fetch-depth: 0</c>, which
/// pulls the whole history so the PR's commits reach the runner even without an explicit <c>ref:</c>. Both
/// positions are the input's own value, not the step's, because that is the line to edit.
/// </para>
/// <para>
/// The trigger is declared at workflow level, so every job of such a file is in scope; there is no per-event
/// job selector in the workflow syntax to check instead.
/// </para>
/// </remarks>
internal sealed class UntrustedCheckoutRule : ILintRule
{
    private static readonly string[] HeadRefPaths = ["github.head_ref", "github.event.pull_request.head"];

    public LintRuleDescriptor Descriptor => LintRuleTable.UntrustedHeadCheckout;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Workflow.Triggers.Find("pull_request_target") is null)
        {
            return [];
        }

        var findings = new List<LintFinding>();
        foreach (var (job, step) in context.Steps())
        {
            if (!ActionReference.TryParse(step.Uses, out var action) || action is null)
            {
                continue;
            }

            if (Input(step, "ref") is { } refValue && ReferencesHead(refValue.Text))
            {
                findings.Add(Finding(job, step, refValue,
                    $"This workflow runs on pull_request_target and '{action.Name}' checks the pull request's own " +
                        $"head out (`ref: {refValue.Text}`), so unreviewed code lands on a runner that holds the " +
                        "base workflow's secrets and permissions."));
            }
            else if (action.IsCheckout && Input(step, "fetch-depth") is { } depth && depth.Text.Trim() == "0")
            {
                findings.Add(Finding(job, step, depth,
                    $"This workflow runs on pull_request_target and '{action.Name}' fetches the whole history " +
                        "(`fetch-depth: 0`), which puts the pull request's commits on a runner that holds the base " +
                        "workflow's secrets and permissions."));
            }
        }

        return findings;
    }

    /// <summary>
    /// The checkout inputs are matched case-insensitively, which is how the runner reads them; the model keeps
    /// the keys as the author spelled them.
    /// </summary>
    private static WorkflowValue? Input(StepDefinition step, string name)
    {
        foreach (var pair in step.With)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static bool ReferencesHead(string? text) =>
        text is not null
        && ExpressionTextScanner.Interpolations(text).Any(expression =>
            HeadRefPaths.Any(path => ExpressionTextScanner.Mentions(expression.Body, path)));

    private static LintFinding Finding(JobDefinition job, StepDefinition step, WorkflowValue input, string message) => new()
    {
        Message = message,
        Range = input.Range.IsKnown ? input.Range : LintContext.StepRange(step, "with"),
        Suggestion = "Check out the base revision — the default when no `ref:` is written — and read the pull " +
            "request's code in a separate job that gets no secrets and a read-only token.",
        Scope = LintContext.StepScope(job, step),
    };
}
