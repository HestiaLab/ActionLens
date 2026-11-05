using ActionLens.Core.Diagnostics;

namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL004 — <c>secrets.*</c> read in an <c>if:</c> condition.
/// </summary>
/// <remarks>
/// GitHub's context availability table (see <c>docs/expression-semantics.md</c>) lists
/// <c>github, needs, vars, inputs</c> for a job condition and ten contexts for a step condition, and
/// <c>secrets</c> is in neither; the documentation states directly that a secret cannot be referenced in an
/// <c>if:</c>. The runner rejects it as a parse-level error, which is what makes this an error rather than a
/// warning.
/// <para>
/// The detection itself is the expression layer's: <see cref="DiagnosticCodes.ContextNotAvailable"/> is
/// already produced for a context that is illegal in its position, with the position and the availability
/// table in its message. So this rule <em>promotes</em> that finding — same sentence, same place, rule id and
/// fix attached — instead of scanning again and giving the reader two findings for one problem. The scan here
/// only answers "which conditions mention <c>secrets</c>", because that is what tells the two apart from an
/// <c>env</c> or <c>steps</c> misuse, and because a condition the evaluator never reached (a scenario that
/// short-circuits the <c>&amp;&amp;</c> before it) still deserves the finding: GitHub rejects the file when it
/// parses it, whatever this analysis decided to evaluate.
/// </para>
/// </remarks>
public sealed class SecretsInConditionRule : ILintRule
{
    /// <summary>
    /// The engine marks a context that is illegal in its position with the offending named value in
    /// <see cref="Diagnostic.Symbol"/>, so this matches the field rather than the sentence. Keying on the
    /// message would mean any rewording of an explanation silently turns the rule off; context names are
    /// compared case-insensitively because Actions resolves them that way.
    /// </summary>
    private const string SecretsSymbol = "secrets";

    private const string Fix = "Move the comparison into the script or an `env:` value and branch on what it " +
        "writes, or gate the job on something an `if:` can see — `github.event.sender.login`, a protected " +
        "`environment:`, or an `inputs.*` value.";

    public LintRuleDescriptor Descriptor => LintRuleTable.SecretsInCondition;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<LintFinding>();
        foreach (var condition in context.Conditions())
        {
            if (!ExpressionTextScanner.ReferencesContext(condition.Condition.Text, "secrets"))
            {
                continue;
            }

            var engine = context.FindEngineFinding(
                DiagnosticCodes.ContextNotAvailable,
                condition.Range,
                static finding => string.Equals(finding.Symbol, SecretsSymbol, StringComparison.OrdinalIgnoreCase));

            findings.Add(engine is { } found
                ? LintFinding.Promoting(found.Index, found.Finding, Fix)
                : new LintFinding
                {
                    Message = $"'secrets' appears in {condition.PositionPhrase} condition, and GitHub's context " +
                        "availability table puts it in no position's list, so the workflow is rejected when it is parsed.",
                    Range = condition.Range,
                    Suggestion = Fix,
                    Scope = condition.Scope,
                });
        }

        return findings;
    }
}
