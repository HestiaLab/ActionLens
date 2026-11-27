namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL008 — a script still publishes its output or state with the deprecated workflow commands.
/// </summary>
/// <remarks>
/// <c>::set-output name=x::v</c> and <c>::save-state name=x::v</c> were deprecated in favour of the environment
/// files (<c>$GITHUB_OUTPUT</c>, <c>$GITHUB_STATE</c>) because a command printed to stdout lets anything the
/// step runs forge outputs — the same reason the runner now warns on them.
/// <para>
/// The scan is over the script text, once per occurrence, at the offset of the command: it does not try to
/// work out whether the line is a shell comment or inside a heredoc. A command in a comment is dead code the
/// author can delete on reading the finding, and teaching the rule to tell shell comments from scripts is a
/// parser this layer is not; the limit is stated here rather than hidden.
/// </para>
/// </remarks>
internal sealed class DeprecatedWorkflowCommandRule : ILintRule
{
    private static readonly (string Command, string Replacement)[] Deprecated =
    [
        ("::set-output", "$GITHUB_OUTPUT"),
        ("::save-state", "$GITHUB_STATE"),
    ];

    public LintRuleDescriptor Descriptor => LintRuleTable.DeprecatedWorkflowCommand;

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
            foreach (var (command, target) in Deprecated)
            {
                var index = script.IndexOf(command, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    findings.Add(new LintFinding
                    {
                        Message = $"The script publishes with the deprecated {command} workflow command, which " +
                            "the runner warns about and can stop honouring at any time.",
                        Range = LintTextLocator.Locate(scriptRange, script, index, command.Length),
                        Suggestion = $"Write the same value to {target} instead: `echo 'name=value' >> {target}`.",
                        Scope = LintContext.StepScope(job, step),
                    });

                    index = script.IndexOf(command, index + command.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return findings;
    }
}
