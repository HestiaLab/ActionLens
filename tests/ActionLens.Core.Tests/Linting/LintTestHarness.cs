using System.Text;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Linting;
using ActionLens.Core.Reporting;

namespace ActionLens.Core.Tests.Linting;

/// <summary>
/// The shared reads for the lint tests: run one workflow, take the findings of one rule, and check the
/// invariants every lint finding owes the reader.
/// </summary>
/// <remarks>
/// <see cref="AssertWellFormed(IReadOnlyList{Diagnostic})"/> is the point of the layer rather than of any one
/// rule: a lint finding with no position cannot be jumped to, and one with no fix or no doc link tells the
/// reader something is wrong without letting them do anything about it. Every rule test runs its findings
/// through it, so a rule cannot pass by producing a sentence and a location alone.
/// </remarks>
internal static class LintTestHarness
{
    /// <summary>Analyses one workflow the way the app would: text in, report out.</summary>
    internal static AnalysisReport Run(string yaml) => WorkflowAnalyzer.Analyze(yaml, "lint-case.yml");

    /// <summary>The lint findings a workflow earns, in the order the report carries them.</summary>
    internal static Diagnostic[] LintFindings(string yaml) =>
        [.. Run(yaml).Diagnostics.Where(finding => finding.Stage == DiagnosticStage.Lint)];

    /// <summary>The findings of one rule id.</summary>
    internal static Diagnostic[] FindingsFor(string yaml, LintRuleDescriptor rule) =>
        [.. LintFindings(yaml).Where(finding => finding.Code == rule.Id)];

    /// <summary>
    /// Checks the invariants: position known, message a sentence, fix concrete, doc link on the current
    /// GitHub documentation host, stage <see cref="DiagnosticStage.Lint"/>, and the id and severity the table
    /// promises rather than the ones the rule happened to write.
    /// </summary>
    internal static void AssertWellFormed(IReadOnlyList<Diagnostic> findings)
    {
        Assert.NotEmpty(findings);
        foreach (var finding in findings)
        {
            Assert.Equal(DiagnosticStage.Lint, finding.Stage);
            Assert.True(finding.Range.IsKnown, $"{finding.Code} has no position: {finding.Message}");
            Assert.True(finding.Range.Start.Column > 0, $"{finding.Code} has no column: {finding.Message}");
            Assert.False(string.IsNullOrWhiteSpace(finding.Message));
            Assert.False(string.IsNullOrWhiteSpace(finding.Suggestion));
            Assert.StartsWith("https://docs.github.com/", finding.HelpLink, StringComparison.Ordinal);

            // The id and severity the table promises, not the ones the rule happened to write.
            var descriptor = Assert.Single(LintRuleTable.All, candidate => candidate.Id == finding.Code);
            Assert.Equal(descriptor.Severity, finding.Severity);
            Assert.Equal(descriptor.HelpLink, finding.HelpLink);
        }
    }

    internal static void AssertWellFormed(Diagnostic finding) => AssertWellFormed([finding]);

    /// <summary>Asserts the rule fired exactly once, and that the finding is well formed.</summary>
    internal static Diagnostic FiresOnce(string yaml, LintRuleDescriptor rule)
    {
        var findings = FindingsFor(yaml, rule);
        Assert.Single(findings);
        AssertWellFormed(findings);
        return findings[0];
    }

    /// <summary>Asserts the rule fired at least once and that every one of its findings is well formed.</summary>
    internal static Diagnostic[] Fires(string yaml, LintRuleDescriptor rule)
    {
        var findings = FindingsFor(yaml, rule);
        AssertWellFormed(findings);
        return findings;
    }

    /// <summary>Asserts the rule stayed silent about a workflow.</summary>
    internal static void StaysSilent(string yaml, LintRuleDescriptor rule) =>
        Assert.Empty(FindingsFor(yaml, rule));

    /// <summary>
    /// A workflow whose single job holds these steps. The steps begin on line 7, so a test that points at one
    /// of them can count: line 7 is <c>steps:</c>, line 8 is the first step. Multi-line cases are written as a
    /// whole file instead, because their value's own indentation is what the position arithmetic is about.
    /// </summary>
    internal static string OneJob(params string[] steps)
    {
        var text = new StringBuilder();
        text.Append("""
            name: lint-case
            on:
              push:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
            """);

        foreach (var step in steps)
        {
            // Six spaces put a step under `steps:`; the caller writes it from its leading `-`.
            text.Append("\n      ").Append(step.TrimStart('\n').TrimEnd('\n'));
        }

        return text.Append('\n').ToString();
    }
}
