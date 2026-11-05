using ActionLens.Core.Diagnostics;
using ActionLens.Core.Linting.Rules;

namespace ActionLens.Core.Linting;

/// <summary>
/// The lint layer: the registry of rules, and the merge that puts their findings next to the pipeline's.
/// </summary>
/// <remarks>
/// <para>
/// Rules run in id order and are pure reads of <see cref="LintContext"/>: no file, no socket, no clock, no
/// randomness, so the same workflow always produces the same findings in the same places.
/// </para>
/// <para>
/// The registry stamps each rule's <see cref="LintRuleDescriptor"/> onto what it returns, which is what makes
/// "a finding carries its rule id, severity, stage, position, fix and doc link" a property of the layer rather
/// than of eight authors' good intentions.
/// </para>
/// </remarks>
public static class LintEngine
{
    /// <summary>Every rule ActionLens ships, in id order — AL001 through AL008.</summary>
    public static IReadOnlyList<ILintRule> Rules { get; } =
    [
        new ActionNotPinnedRule(),
        new UntrustedCheckoutRule(),
        new ScriptInjectionRule(),
        new SecretsInConditionRule(),
        new MissingTimeoutRule(),
        new NeedsIntegrityRule(),
        new RunnerLabelTypoRule(),
        new DeprecatedWorkflowCommandRule(),
    ];

    /// <summary>Runs every rule over one loaded workflow and its stage findings.</summary>
    public static LintResult Run(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();
        var promotions = new Dictionary<int, Diagnostic>();

        foreach (var rule in Rules)
        {
            foreach (var finding in rule.Evaluate(context))
            {
                var diagnostic = Describe(rule.Descriptor, finding);
                if (finding.PromotesEngineFindingAt == LintFinding.NotAPromotion)
                {
                    findings.Add(diagnostic);
                }
                else
                {
                    // One engine finding can only be taken over once; the rules read disjoint codes, so the
                    // guard is a statement of the design rather than a routine branch.
                    promotions[finding.PromotesEngineFindingAt] = diagnostic;
                }
            }
        }

        // Position first, then id: a reader working down the file meets the findings in the order they wrote
        // the shapes, and two findings on one line still land the same way on every machine.
        findings.Sort(CompareByPositionThenId);

        return new LintResult { Findings = findings, Promotions = promotions };
    }

    /// <summary>
    /// Stamps the rule's identity — id, severity, the lint stage, its doc link — onto what the rule found.
    /// This is where "every lint finding carries a position, a fix and a help link" is guaranteed.
    /// </summary>
    private static Diagnostic Describe(LintRuleDescriptor descriptor, LintFinding finding) => new()
    {
        Code = descriptor.Id,
        Severity = descriptor.Severity,
        Stage = DiagnosticStage.Lint,
        Message = finding.Message,
        Range = finding.Range,
        Suggestion = finding.Suggestion,
        HelpLink = descriptor.HelpLink,
        Scope = finding.Scope,
    };

    private static int CompareByPositionThenId(Diagnostic left, Diagnostic right)
    {
        var byLine = left.Range.Start.Line.CompareTo(right.Range.Start.Line);
        if (byLine != 0)
        {
            return byLine;
        }

        var byColumn = left.Range.Start.Column.CompareTo(right.Range.Start.Column);
        if (byColumn != 0)
        {
            return byColumn;
        }

        var byCode = string.CompareOrdinal(left.Code, right.Code);
        if (byCode != 0)
        {
            return byCode;
        }

        // Scope and message only break ties between two same-id findings at one position, which two steps of
        // a merge-keyed job can genuinely produce; without them the order would come from the rule's own walk.
        var byScope = string.CompareOrdinal(left.Scope ?? string.Empty, right.Scope ?? string.Empty);
        return byScope != 0 ? byScope : string.CompareOrdinal(left.Message, right.Message);
    }
}

/// <summary>
/// What one lint pass produced: the findings that stand on their own, and the stage findings a rule took over.
/// </summary>
public sealed record LintResult
{
    /// <summary>New lint findings, ordered by position then id.</summary>
    public required IReadOnlyList<Diagnostic> Findings { get; init; }

    /// <summary>
    /// Replacements for stage findings, keyed by their index in <see cref="LintContext.EngineFindings"/>. A
    /// promoted entry keeps the engine's message, position and scope and gains the rule id, severity, fix and
    /// doc link — one finding for one problem, which is the point of promoting instead of re-detecting.
    /// </summary>
    public required IReadOnlyDictionary<int, Diagnostic> Promotions { get; init; }

    /// <summary>
    /// The list a report should carry: <paramref name="engineFindings"/> with each promoted entry replaced in
    /// the slot it held — so the stage order the report documents survives — followed by this layer's own
    /// findings in position order.
    /// </summary>
    public IReadOnlyList<Diagnostic> MergeInto(IReadOnlyList<Diagnostic> engineFindings)
    {
        ArgumentNullException.ThrowIfNull(engineFindings);

        var merged = new List<Diagnostic>(engineFindings.Count + Findings.Count);
        merged.AddRange(engineFindings);

        // Ordered by index so a dictionary's own enumeration order cannot reach the output.
        foreach (var pair in Promotions.OrderBy(entry => entry.Key, Comparer<int>.Default))
        {
            if (pair.Key >= 0 && pair.Key < merged.Count)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        merged.AddRange(Findings);
        return merged;
    }
}
