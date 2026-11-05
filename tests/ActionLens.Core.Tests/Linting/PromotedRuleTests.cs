using ActionLens.Core.Diagnostics;
using ActionLens.Core.Linting;
using ActionLens.Core.Scenarios;

namespace ActionLens.Core.Tests.Linting;

/// <summary>
/// AL004 and AL006, the two rules that take a stage finding over instead of detecting the problem again — and
/// AL005, which needs no such help because nothing upstream asks about <c>timeout-minutes</c>.
/// </summary>
/// <remarks>
/// The assertion that matters here is <c>exactly one</c>: the expression layer already rejects a context that
/// is illegal in its position, and the graph layer already names a missing or cyclic <c>needs</c>. A reader
/// who saw both would have two problems to fix for one mistake, so these tests fail if either code appears
/// beside the rule id.
/// </remarks>
public sealed class PromotedRuleTests
{
    private const string SecretsInStepCondition = """
        name: secret-step
        on:
          push:
        jobs:
          build:
            runs-on: ubuntu-latest
            timeout-minutes: 5
            steps:
              - run: echo gated
                id: gate
                if: secrets.PREVIEW_TOKEN != ''
        """;

    [Fact]
    public void SecretsInAStepCondition_AreOneFindingUnderTheRuleId()
    {
        var finding = LintTestHarness.FiresOnce(SecretsInStepCondition, LintRuleTable.SecretsInCondition);

        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal("build.gate", finding.Scope);
        Assert.Contains("step-level if:", finding.Message, StringComparison.Ordinal);

        // The engine's own explanation was kept, not rewritten: it names the availability table for the
        // position, which is the sentence that tells the author why GitHub would reject the file.
        Assert.Contains("GitHub's context availability table", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            LintTestHarness.Run(SecretsInStepCondition).Diagnostics,
            engine => engine.Code == DiagnosticCodes.ContextNotAvailable);
    }

    [Fact]
    public void SecretsInAJobCondition_AreOneFinding_WhileAnIllegalOrdinaryContextKeepsItsOwn()
    {
        // One condition, two contexts GitHub keeps out of jobs.<job_id>.if. Only the secret is AL004's;
        // `env` is the expression layer's finding to make, and promoting it would claim it is a secrets misuse.
        const string yaml = """
            name: two-contexts
            on:
              push:
            env:
              STAGE: prod
            jobs:
              gated:
                if: env.STAGE == 'prod' && secrets.ADMIN_DEPLOY != ''
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo gated
            """;

        var report = LintTestHarness.Run(yaml);

        var promoted = Assert.Single(
            report.Diagnostics,
            finding => finding.Code == LintRuleTable.SecretsInCondition.Id);
        Assert.Contains("job-level if:", promoted.Message, StringComparison.Ordinal);
        Assert.Equal("gated", promoted.Scope);

        var leftAlone = Assert.Single(
            report.Diagnostics,
            finding => finding.Code == DiagnosticCodes.ContextNotAvailable);
        Assert.Contains("'env' is not available", leftAlone.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStage.Expression, leftAlone.Stage);
    }

    [Fact]
    public void SecretsInAConditionTheEvaluatorNeverReaches_AreStillOneFinding()
    {
        // `&&` short-circuits: with the dependency failed, success() is already false and the evaluator does
        // not look at the right-hand side, so the engine produces no unavailable-context finding at all. The
        // rule is about what GitHub accepts, so the finding stands on its own — and still exactly one.
        const string yaml = """
            name: short-circuit
            on:
              push:
            jobs:
              first:
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: exit 1
              gated:
                needs: first
                if: secrets.ADMIN_DEPLOY != ''
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo gated
            """;

        var report = WorkflowAnalyzer.Analyze(yaml, "short-circuit.yml", scenario: Scenario.Default.WithJobFailed("first"));

        var finding = Assert.Single(report.Diagnostics, item => item.Code == LintRuleTable.SecretsInCondition.Id);
        LintTestHarness.AssertWellFormed(finding);
        Assert.DoesNotContain(report.Diagnostics, item => item.Code == DiagnosticCodes.ContextNotAvailable);

        // The rule's own sentence, not the engine's: nothing upstream reached the condition, so this finding is
        // the static half of AL004 rather than a promotion.
        Assert.StartsWith("'secrets' appears in a job-level if:", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A secret read where GitHub allows it: env indirection and a script, never an `if:`.
    [InlineData("""
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo "$TOKEN"
                env:
                  TOKEN: ${{ secrets.API_TOKEN }}
        """)]
    [InlineData("""
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo gated
                if: steps.gate.outputs.ok == 'yes'
                id: gate
        """)]
    [InlineData("""
        jobs:
          build:
            runs-on: ubuntu-latest
            container:
              image: alpine
            steps:
              - run: echo ${{ github.event.repository.name }}
        """)]
    public void SecretsUsedWhereTheyAreLegal_StaySilent(string jobs)
    {
        var yaml = $"name: legal-secret\non:\n  push:\n{jobs}";
        LintTestHarness.StaysSilent(yaml, LintRuleTable.SecretsInCondition);
    }

    // ------------------------------------------------------------------ AL006

    [Fact]
    public void NeedsThatNamesNoJob_IsOneFindingUnderTheRuleId()
    {
        const string yaml = """
            name: missing-needs
            on:
              push:
            jobs:
              build:
                needs: ghost
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo build
            """;

        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.NeedsIntegrity);

        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal("build", finding.Scope);
        Assert.Contains("ghost", finding.Message, StringComparison.Ordinal);
        Assert.Contains("no job with that id", finding.Message, StringComparison.Ordinal);

        var report = LintTestHarness.Run(yaml);
        Assert.DoesNotContain(report.Diagnostics, item => item.Code == DiagnosticCodes.NeedsUnknownJob);
        Assert.Equal(DiagnosticStage.Lint, finding.Stage);
    }

    [Fact]
    public void ANeedsCycle_IsOneFindingAndKeepsThePathTheGraphLayerFound()
    {
        const string yaml = """
            name: cycle
            on:
              push:
            jobs:
              one:
                needs: two
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo one
              two:
                needs: one
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo two
            """;

        var report = LintTestHarness.Run(yaml);
        var finding = Assert.Single(report.Diagnostics, item => item.Code == LintRuleTable.NeedsIntegrity.Id);

        LintTestHarness.AssertWellFormed(finding);

        // The cycle path is the engine's contribution, and it survives the promotion.
        Assert.Contains("->", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Diagnostics, item => item.Code == DiagnosticCodes.NeedsCycle);
        Assert.False(report.Graph.IsAcyclic);
    }

    [Fact]
    public void LegalNeeds_StaySilent()
    {
        const string yaml = """
            name: legal-needs
            on:
              push:
            jobs:
              one:
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo one
              two:
                needs: [one]
                runs-on: ubuntu-latest
                timeout-minutes: 5
                steps:
                  - run: echo two
            """;

        LintTestHarness.StaysSilent(yaml, LintRuleTable.NeedsIntegrity);
    }

    // ------------------------------------------------------------------ AL005

    [Fact]
    public void AJobWithNoTimeout_IsTheOnlyFindingAboutThatJob()
    {
        var yaml = LintTestHarness.OneJob("- run: echo hi");
        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.MissingTimeout);

        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
        Assert.Equal("build", finding.Scope);

        // The job id is the thing to edit, so that is where the finding points: `  build:` on line 5.
        Assert.Equal(5, finding.Range.Start.Line);
        Assert.Equal(3, finding.Range.Start.Column);
        Assert.Contains("timeout-minutes", finding.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout-minutes: 15\n    steps:\n      - run: echo hi")]
    [InlineData("uses: ./.github/workflows/_build.yml")]
    public void TimeoutThatIsAbsentOrNotApplicable_StaysSilent(string jobBody)
    {
        var yaml = $"name: timeout-case\non:\n  push:\njobs:\n  build:\n    runs-on: ubuntu-latest\n    {jobBody}\n";
        LintTestHarness.StaysSilent(yaml, LintRuleTable.MissingTimeout);
    }
}
