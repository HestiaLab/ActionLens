using ActionLens.Core.Diagnostics;
using ActionLens.Core.Linting;
using ActionLens.Core.Text;

namespace ActionLens.Core.Tests.Linting;

/// <summary>
/// The layer rather than a rule: the table the eight ids come from, the registry that runs them, and the order
/// their findings reach a report in.
/// </summary>
public sealed class LintEngineTests
{
    [Fact]
    public void Table_HoldsTheEightIdsWithTheSeverityAndLinkEachPromises()
    {
        var expected = new[]
        {
            ("AL001", DiagnosticSeverity.Warning),
            ("AL002", DiagnosticSeverity.Critical),
            ("AL003", DiagnosticSeverity.Critical),
            ("AL004", DiagnosticSeverity.Error),
            ("AL005", DiagnosticSeverity.Info),
            ("AL006", DiagnosticSeverity.Error),
            ("AL007", DiagnosticSeverity.Warning),
            ("AL008", DiagnosticSeverity.Warning),
        };

        Assert.Equal(
            expected.Select(pair => pair.Item1),
            LintRuleTable.All.Select(descriptor => descriptor.Id));
        Assert.Equal(
            expected.Select(pair => pair.Item2),
            LintRuleTable.All.Select(descriptor => descriptor.Severity));

        // The documentation moved: the paths that redirect are not the ones to print in a report.
        Assert.All(LintRuleTable.All, descriptor =>
        {
            Assert.StartsWith("https://docs.github.com/en/actions/", descriptor.HelpLink, StringComparison.Ordinal);
            Assert.DoesNotContain("learn-github-actions", descriptor.HelpLink, StringComparison.Ordinal);
            Assert.DoesNotContain("using-workflows", descriptor.HelpLink, StringComparison.Ordinal);
        });

        // Every rule in the registry reports under its own table entry, so no rule can rename another's finding.
        Assert.Equal(
            LintRuleTable.All.Select(descriptor => descriptor.Id),
            LintEngine.Rules.Select(rule => rule.Descriptor.Id));
        Assert.Equal(8, LintEngine.Rules.Count);
    }

    [Fact]
    public void Findings_ReachTheReportAfterTheStageFindingsAndInPositionOrder()
    {
        // Four shapes in one file, none of which a stage also reports: AL005 on the job, then AL001, AL008 and
        // AL003 on its steps, in the order the author wrote them.
        const string yaml = """
            name: ordering
            on:
              push:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: echo "::set-output name=note::1"
                  - run: echo "${{ github.event.pull_request.title }}"
            """;

        var report = LintTestHarness.Run(yaml);

        Assert.DoesNotContain(report.Diagnostics, finding => finding.Stage != DiagnosticStage.Lint);
        var positions = report.Diagnostics.Select(finding => finding.Range.Start).ToArray();
        Assert.NotEmpty(positions);
        for (var index = 1; index < positions.Length; index++)
        {
            var before = positions[index - 1];
            var after = positions[index];
            Assert.True(
                before.Line < after.Line || (before.Line == after.Line && before.Column <= after.Column),
                $"{report.Diagnostics[index - 1].Code} at {before} sorts after {report.Diagnostics[index].Code} at {after}");
        }

        Assert.Equal(
            ["AL005", "AL001", "AL008", "AL003"],
            report.Diagnostics.Select(finding => finding.Code).ToArray());
    }

    [Fact]
    public void TwoFindingsOnOneLine_AreOrderedByColumn()
    {
        // One script, two rules: the deprecated command at the start of the line and the payload
        // interpolation further along it. The line is shared, so the column is what decides the order.
        const string yaml = """
            name: one-line
            on:
              push:
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo "::set-output name=x::${{ github.event.pull_request.title }}"
            """;

        var findings = LintTestHarness.LintFindings(yaml);
        var deprecated = Assert.Single(findings, finding => finding.Code == LintRuleTable.DeprecatedWorkflowCommand.Id);
        var injection = Assert.Single(findings, finding => finding.Code == LintRuleTable.PayloadIntoScript.Id);

        // `- run: ` ends at column 13, so the command starts at 20 and the interpolation at 41.
        Assert.Equal(new SourcePosition(9, 20), deprecated.Range.Start);
        Assert.Equal(new SourcePosition(9, 41), injection.Range.Start);
        Assert.True(deprecated.Range.Start.Column < injection.Range.Start.Column);
        Assert.Equal(
            [LintRuleTable.DeprecatedWorkflowCommand.Id, LintRuleTable.PayloadIntoScript.Id],
            findings.Select(finding => finding.Code).ToArray());
    }

    [Fact]
    public void TheSameInput_TwiceGivesTheSameFindingsInSameOrder()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "workflows", "unsafe-demo.yml"));
        var first = LintTestHarness.Run(yaml).Diagnostics.Select(Describe).ToArray();
        var second = LintTestHarness.Run(yaml).Diagnostics.Select(Describe).ToArray();

        Assert.Equal(first, second);

        static string Describe(Diagnostic finding) =>
            $"{finding.Code}|{finding.Severity}|{finding.Stage}|{finding.Range}|{finding.Scope}|{finding.Message}";
    }

    [Fact]
    public void EveryFindingOfEveryRule_CarriesPositionFixAndHelpLink()
    {
        // The fixture that has a triggering shape for all eight rules, checked as a whole rather than per rule:
        // the invariant belongs to the layer, so it is asserted over everything the layer produced.
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "workflows", "unsafe-demo.yml"));
        LintTestHarness.AssertWellFormed(LintTestHarness.LintFindings(yaml));
    }
}
