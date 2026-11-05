namespace ActionLens.Core.Linting;

/// <summary>
/// One lint rule: an id with a severity and a doc link, plus a function from a loaded workflow to findings.
/// </summary>
/// <remarks>
/// Rules read the model the YAML stage built — they never re-parse the text, and a rule that could not locate
/// something in the model says nothing rather than guessing. Two rules (AL004, AL006) promote findings the
/// expression and graph layers already produced instead of detecting the same problem twice; see
/// <see cref="LintFinding.Promoting"/>.
/// </remarks>
public interface ILintRule
{
    /// <summary>The rule's id, severity and help link, taken from <see cref="LintRuleTable"/>.</summary>
    LintRuleDescriptor Descriptor { get; }

    /// <summary>Everything this rule finds in one workflow, in the order the file writes them.</summary>
    IReadOnlyList<LintFinding> Evaluate(LintContext context);
}
