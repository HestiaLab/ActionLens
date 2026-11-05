using System.Globalization;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Linting;
using ActionLens.Core.Reporting;

namespace ActionLens.Core.Tests.Linting;

/// <summary>
/// <c>samples/workflows/unsafe-demo.yml</c> end to end: the file was synthesized so that every rule has a
/// triggering shape and one job has none, and this is where that claim is checked.
/// </summary>
/// <remarks>
/// The counts are worked out from the file, not read off the engine. AL001 has four unpinned remote calls
/// (checkout@v4 at 71, setup-python@v5 at 74, checkout@main at 100, checkout@v3 at 116 — the fifth call, in
/// <c>safe-verify</c>, is a real commit SHA). AL002 has the one head checkout at 102. AL003 has three
/// interpolations (81, 82, and <c>github.head_ref</c> at 107). AL004 has two conditions that read a secret
/// (the step <c>Deploy preview</c> at 91, the job <c>deploy</c> at 121). AL005 has the two jobs with no
/// timeout of their own (60, 112). AL006 has the self-referential <c>needs</c> at 114, AL007 the misspelled
/// label at 113, and AL008 the deprecated command at 87.
/// </remarks>
public sealed class UnsafeDemoLintTests
{
    private static string FixtureYaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "workflows", "unsafe-demo.yml"));

    private static AnalysisReport Report => LintTestHarness.Run(FixtureYaml);

    private static Diagnostic[] Findings => LintTestHarness.LintFindings(FixtureYaml);

    [Theory]
    [InlineData("AL001", 4)]
    [InlineData("AL002", 1)]
    [InlineData("AL003", 3)]
    [InlineData("AL004", 2)]
    [InlineData("AL005", 2)]
    [InlineData("AL006", 1)]
    [InlineData("AL007", 1)]
    [InlineData("AL008", 1)]
    public void EveryRule_HasItsShapeInTheFixture_AndIsWellFormed(string ruleId, int expectedCount)
    {
        var findings = Findings.Where(finding => finding.Code == ruleId).ToArray();

        Assert.Equal(expectedCount, findings.Length);
        LintTestHarness.AssertWellFormed(findings);

        var descriptor = LintRuleTable.All.Single(candidate => candidate.Id == ruleId);
        Assert.All(findings, finding => Assert.Equal(descriptor.Severity, finding.Severity));
    }

    [Theory]
    // The lines the fixture header cites for each rule, read out of the file by hand.
    [InlineData("AL001", "71,74,100,116")]
    [InlineData("AL002", "102")]
    [InlineData("AL003", "81,82,107")]
    [InlineData("AL004", "91,121")]
    [InlineData("AL005", "60,112")]
    [InlineData("AL006", "114")]
    [InlineData("AL007", "113")]
    [InlineData("AL008", "87")]
    public void FindingsOfOneRule_SitOnTheLinesTheFixtureCites(string ruleId, string lines)
    {
        var expected = lines
            .Split(',')
            .Select(line => int.Parse(line, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(
            expected,
            Findings.Where(finding => finding.Code == ruleId).Select(finding => finding.Range.Start.Line).ToArray());
    }

    [Fact]
    public void PromotedFindings_KeepTheirStageSlot_WhileTheRestFollowsPositionOrder()
    {
        var findings = Report.Diagnostics;
        var lastStageIndex = findings
            .Select((finding, index) => (finding, index))
            .Where(pair => pair.finding.Stage != DiagnosticStage.Lint)
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .Max();

        // AL006 sits where the graph stage put the cycle, and AL004 where the expression stage put the
        // condition: the reader meets a promoted finding beside the stage that explains it, not in a second
        // block that describes the same problem again.
        var head = findings.Take(lastStageIndex + 1).ToArray();
        Assert.Contains(head, finding => finding.Code == LintRuleTable.NeedsIntegrity.Id);
        Assert.Equal(2, head.Count(finding => finding.Code == LintRuleTable.SecretsInCondition.Id));

        // Everything the rules found themselves is appended, in the order the author wrote the shapes.
        var tail = findings.Skip(lastStageIndex + 1).ToArray();
        Assert.All(tail, finding => Assert.Equal(DiagnosticStage.Lint, finding.Stage));

        var positions = tail.Select(finding => finding.Range.Start).ToArray();
        for (var index = 1; index < positions.Length; index++)
        {
            Assert.True(
                positions[index - 1].Line < positions[index].Line
                || (positions[index - 1].Line == positions[index].Line
                    && positions[index - 1].Column <= positions[index].Column),
                $"{tail[index - 1].Code} at {positions[index - 1]} sorts after {tail[index].Code} at {positions[index]}");
        }

        // The same tail every time, which is what a report a reader shares has to be.
        Assert.Equal(tail.Select(finding => finding.Code), Report.Diagnostics.Skip(lastStageIndex + 1).Select(finding => finding.Code));
    }

    [Fact]
    public void TheCleanJob_ProducesNoFindingOfItsOwn()
    {
        // `safe-verify` is the counter-example: SHA pin, payload through env, timeout declared, real label.
        Assert.DoesNotContain(Findings, finding => BelongsTo(finding, "safe-verify"));

        // Its env value is still a run's to supply, which the scenario layer says — a note about what static
        // analysis cannot know, not a rule objecting to the file.
        var explanation = Assert.Single(
            Report.Diagnostics,
            finding => finding.Stage == DiagnosticStage.Scenario && BelongsTo(finding, "safe-verify"));
        Assert.Equal(DiagnosticCodes.ScenarioValueUnresolved, explanation.Code);
    }

    [Fact]
    public void TheTwoPromotedRules_LeaveNoStageFindingBehind()
    {
        var report = Report;

        // One finding per problem: the secrets conditions and the needs cycle exist once each, under their rule
        // ids, and no EXPR005 / GRPH001 / GRPH002 is left beside them.
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == DiagnosticCodes.ContextNotAvailable);
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == DiagnosticCodes.NeedsUnknownJob);
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == DiagnosticCodes.NeedsCycle);
        Assert.Equal(2, report.Diagnostics.Count(finding => finding.Code == LintRuleTable.SecretsInCondition.Id));
        Assert.Equal(1, report.Diagnostics.Count(finding => finding.Code == LintRuleTable.NeedsIntegrity.Id));

        // An illegality GitHub would reject the file for, so the report is blocked and says so: three errors
        // (the two secrets conditions and the cycle) and four criticals (the head checkout, three injections).
        Assert.True(report.IsBlocked);
        Assert.Equal(3, report.Totals.Errors);
        Assert.Equal(4, report.Totals.Criticals);
    }

    /// <summary>Whether a finding belongs to the given job — its own scope, or a step of it.</summary>
    private static bool BelongsTo(Diagnostic finding, string jobId) =>
        finding.Scope?.StartsWith(jobId + "#", StringComparison.Ordinal) == true
        || finding.Scope?.StartsWith(jobId + ".", StringComparison.Ordinal) == true
        || string.Equals(finding.Scope, jobId, StringComparison.Ordinal);
}
