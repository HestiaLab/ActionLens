using ActionLens.Core.Linting;

namespace ActionLens.Core.Tests.Linting;

/// <summary>
/// AL007 — the typo check, and the false positives it exists not to produce.
/// </summary>
/// <remarks>
/// The guards matter more than the firing case: a self-hosted runner class is a string its owner invented, so
/// a whitelist reading of "unknown label" would warn on every one of them. These tests hold the line that only
/// a label one edit from a documented hosted label earns a finding.
/// </remarks>
public sealed class RunnerLabelTypoRuleTests
{
    private static string Job(string runsOn) =>
        $"name: label-case\non:\n  push:\njobs:\n  build:\n    runs-on: {runsOn}\n    timeout-minutes: 5\n    steps:\n      - run: echo hi\n";

    [Fact]
    public void AL007_TheRuleIsRegisteredWithItsSeverityAndDocLink()
    {
        var finding = LintTestHarness.FiresOnce(Job("ubuntu-latests"), LintRuleTable.RunnerLabelTypo);

        Assert.Equal("AL007", finding.Code);
        Assert.Contains("ubuntu-latest", finding.Suggestion, StringComparison.Ordinal);
        Assert.Equal("build", finding.Scope);

        // `    runs-on: ubuntu-latests` puts the value at line 6, column 14.
        Assert.Equal(6, finding.Range.Start.Line);
        Assert.Equal(14, finding.Range.Start.Column);
    }

    [Theory]
    // One edit from a documented label, in either direction: a stray character, a missing one, a wrong one.
    [InlineData("ubuntu-latests")]
    [InlineData("ubuntu-lates")]
    [InlineData("ubntu-latest")]
    [InlineData("windows-latests")]
    [InlineData("windows-20255")]
    [InlineData("macos-lates")]
    public void NearMissesOfADocumentedHostedLabel_Fire(string label)
    {
        LintTestHarness.FiresOnce(Job(label), LintRuleTable.RunnerLabelTypo);
    }

    [Theory]
    // The labels GitHub itself does not define, which a self-hosted fleet invents. None of them is near a
    // hosted name, and a warning here would be the false positive this rule is written to avoid.
    [InlineData("[self-hosted, linux, ARM64]")]
    [InlineData("[self-hosted, gpu-large, daytona]")]
    [InlineData("arms-class")]
    [InlineData("gcp-custom-ubuntu")]
    [InlineData("[builder, docker]")]
    [InlineData("macos-15-intel")]
    [InlineData("ubuntu-24.04-arm64-large")]
    public void UnknownButNotMisspelled_StaysSilent(string runsOn)
    {
        LintTestHarness.StaysSilent(Job(runsOn), LintRuleTable.RunnerLabelTypo);
    }

    [Theory]
    [InlineData("ubuntu-latest")]
    [InlineData("ubuntu-24.04")]
    [InlineData("UBUNTU-LATEST")]
    [InlineData("Windows-Latest")]
    [InlineData("windows-11-arm")]
    [InlineData("macos-15")]
    public void DocumentedHostedLabels_StaySilent(string runsOn)
    {
        LintTestHarness.StaysSilent(Job(runsOn), LintRuleTable.RunnerLabelTypo);
    }

    [Fact]
    public void AnExpressionLabel_StaysSilent()
    {
        // `${{ matrix.os }}` names nothing statically; the matrix layer answers for it.
        LintTestHarness.StaysSilent(Job("'${{ matrix.os }}'"), LintRuleTable.RunnerLabelTypo);
    }

    [Fact]
    public void TheGroupAndLabelsMapping_StaysSilent()
    {
        // Larger-runner and runner-group addressing: the labels belong to the organisation's configuration,
        // and the hosted name next to a typo is exactly the wrong thing to compare them against.
        const string yaml = """
            name: group-case
            on:
              push:
            jobs:
              build:
                runs-on:
                  group: my-runner-group
                  labels: ubuntu-latests
                timeout-minutes: 5
                steps:
                  - run: echo hi
            """;

        LintTestHarness.StaysSilent(yaml, LintRuleTable.RunnerLabelTypo);
    }

    [Fact]
    public void AJobWithNoRunsOn_StaysSilent()
    {
        // The schema stage already says the job has no `runs-on`; AL007 has no label to read.
        const string yaml = """
            name: no-runs-on
            on:
              push:
            jobs:
              build:
                timeout-minutes: 5
                steps:
                  - run: echo hi
            """;

        LintTestHarness.StaysSilent(yaml, LintRuleTable.RunnerLabelTypo);
    }

    [Fact]
    public void OnlyTheOffendingLabelOfTheListIsPointAt()
    {
        const string yaml = """
            name: list-case
            on:
              push:
            jobs:
              build:
                runs-on: [ubuntu-latest, windows-latests]
                timeout-minutes: 5
                steps:
                  - run: echo hi
            """;

        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.RunnerLabelTypo);
        Assert.Contains("windows-latests", finding.Message, StringComparison.Ordinal);

        // Both labels of the flow list are on line 6, and the second value begins at column 30.
        Assert.Equal(6, finding.Range.Start.Line);
        Assert.Equal(30, finding.Range.Start.Column);
    }
}
