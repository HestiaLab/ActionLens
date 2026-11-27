using ActionLens.Core.Diagnostics;

namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL006 — a <c>needs</c> entry names a job that does not exist, or closes a cycle.
/// </summary>
/// <remarks>
/// Both halves are already detected, with positions and explanations, by the graph layer:
/// <see cref="DiagnosticCodes.NeedsUnknownJob"/> for the id that resolves to nothing and
/// <see cref="DiagnosticCodes.NeedsCycle"/> for the path that closes on itself, written as the job sequence
/// that caused it. A lint rule that walked <c>needs</c> again would report the same broken edge twice, so this
/// rule only promotes those findings — same message, same position, rule id, fix and doc link attached.
/// <para>
/// That is also why the finding does not depend on the scenario: a cycle is a fact about the file, and the
/// graph stage produced it before any condition was evaluated.
/// </para>
/// </remarks>
internal sealed class NeedsIntegrityRule : ILintRule
{
    public LintRuleDescriptor Descriptor => LintRuleTable.NeedsIntegrity;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<LintFinding>();
        for (var index = 0; index < context.EngineFindings.Count; index++)
        {
            var finding = context.EngineFindings[index];
            var suggestion = finding.Code switch
            {
                DiagnosticCodes.NeedsUnknownJob =>
                    "Name a job this workflow actually defines, or drop the entry — GitHub rejects the whole " +
                    "file when a needs id is missing.",
                DiagnosticCodes.NeedsCycle =>
                    "Break the loop by dropping one needs entry on the path the finding names, or by splitting " +
                    "the jobs it links into a job that needs nothing.",
                _ => null,
            };

            if (suggestion is not null)
            {
                findings.Add(LintFinding.Promoting(index, finding, suggestion));
            }
        }

        return findings;
    }
}
