using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Linting;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;

namespace ActionLens.Core.Tests.Scenarios;

/// <summary>
/// The scenario engine: how a claim about a run becomes a state for every job and step, and what the two
/// evaluation positions are allowed to read.
/// </summary>
/// <remarks>
/// Workflows are written inline here, one rule per test, because a rule the documentation does not settle
/// has to be seen in isolation — a real fixture mixes four of them at once. The assertions are on states
/// and reason kinds, and on the reasons' text only where the text is the answer (a skipped ancestor, an
/// unavailable context), never on wording for its own sake.
/// </remarks>
public sealed class ScenarioEngineTests
{
    private const string Chain = """
        name: chain
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo build
          test:
            needs: [build]
            runs-on: ubuntu-latest
            steps:
              - run: echo test
        """;

    private static AnalysisReport Run(string yaml, Scenario? scenario = null, string? payload = null) =>
        WorkflowAnalyzer.Analyze(yaml, "inline.yml", payload, scenario);

    /// <summary>A document order for the two jobs of <see cref="Chain"/>, reused so the array is not rebuilt.</summary>
    private static readonly IReadOnlyList<string> DocumentOrder = new[] { "build", "test" };

    private static JobReport Job(AnalysisReport report, string jobId) =>
        report.Jobs.Single(job => string.Equals(job.JobId, jobId, StringComparison.Ordinal));

    private static StepRunResult Step(AnalysisReport report, string jobId, int index) =>
        Job(report, jobId).Runs[0].Steps[index];

    private static IEnumerable<UnknownReasonKind> Kinds(UnknownReason? reason)
    {
        if (reason is null)
        {
            yield break;
        }

        yield return reason.Kind;
        foreach (var cause in reason.Causes)
        {
            foreach (var nested in Kinds(cause))
            {
                yield return nested;
            }
        }
    }

    private static string Text(UnknownReason? reason) => reason is null
        ? string.Empty
        : string.Join(" ", new[] { reason.Message }.Concat(reason.Causes.Select(Text)));

    // ------------------------------------------------------------------ skip propagation

    [Fact]
    public void DefaultScenario_RunsEveryJobAndAppliesTheImplicitSuccess()
    {
        var report = Run(Chain);

        Assert.False(report.IsBlocked);
        Assert.Equal(RunState.WillRun, Job(report, "build").State);
        Assert.Equal(RunState.WillRun, Job(report, "test").State);
        Assert.Equal(JobOutcome.Success, Job(report, "test").Outcome);

        // The author wrote no condition at all, and what was evaluated is the status check GitHub adds.
        var condition = Job(report, "test").Condition;
        Assert.Equal("", condition?.Text);
        Assert.True(condition?.ImplicitSuccessApplied);
        Assert.Equal("success()", condition?.EvaluatedAs);
        Assert.Equal(TriKind.True, condition?.Result);
        Assert.Null(condition?.Reason);

        // The needs context the condition was read against, spelled out for the reader of the report.
        var need = Assert.Single(Job(report, "test").NeedsOutcomes);
        Assert.Equal("build", need.JobId);
        Assert.Equal(JobOutcome.Success, need.Outcome);
    }

    [Fact]
    public void FailedDependency_SkipsTheJobThatNeedsIt()
    {
        var report = Run(Chain, Scenario.Default.WithJobFailed("build"));

        Assert.Equal(RunState.WillRun, Job(report, "build").State);
        Assert.Equal(JobOutcome.Failure, Job(report, "build").Outcome);
        Assert.Equal(RunState.WillNotRun, Job(report, "test").State);
        Assert.Equal(JobOutcome.Skipped, Job(report, "test").Outcome);
        Assert.Equal(TriKind.False, Job(report, "test").Condition?.Result);
        Assert.Equal(1, report.Totals.WillRun);
        Assert.Equal(1, report.Totals.WillNotRun);
    }

    [Fact]
    public void AChain_SkipsTransitively()
    {
        const string yaml = """
            name: chain-of-three
            on: push
            jobs:
              a:
                runs-on: ubuntu-latest
                steps:
                  - run: echo a
              b:
                needs: [a]
                runs-on: ubuntu-latest
                steps:
                  - run: echo b
              c:
                needs: [b]
                runs-on: ubuntu-latest
                steps:
                  - run: echo c
            """;

        var report = Run(yaml, Scenario.Default.WithJobFailed("a"));

        Assert.Equal(RunState.WillNotRun, Job(report, "b").State);

        // `c` needs `b`, which did not run but also did not fail: it was skipped. The documentation does not
        // settle what that does to success(), so the transitive case is unknown rather than not-running.
        Assert.Equal(RunState.Unknown, Job(report, "c").State);
        Assert.Equal(UnknownReasonKind.ScenarioDependent, Job(report, "c").Reason?.Kind);
        Assert.Contains("skipped", Text(Job(report, "c").Reason), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlwaysCondition_ContinuesPastAFailedDependency()
    {
        const string yaml = """
            name: notify
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo build
              notify:
                needs: [build]
                if: always()
                runs-on: ubuntu-latest
                steps:
                  - run: echo notify
            """;

        var report = Run(yaml, Scenario.Default.WithJobFailed("build"));

        Assert.Equal(RunState.WillRun, Job(report, "notify").State);
        Assert.False(Job(report, "notify").Condition?.ImplicitSuccessApplied);
        Assert.Equal("always()", Job(report, "notify").Condition?.EvaluatedAs);
    }

    [Fact]
    public void FailureCondition_IsFalseWhileEverythingSucceededAndTrueAfterAFailure()
    {
        const string yaml = """
            name: triage
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo build
              triage:
                needs: [build]
                if: failure()
                runs-on: ubuntu-latest
                steps:
                  - run: echo triage
            """;

        Assert.Equal(RunState.WillNotRun, Job(Run(yaml), "triage").State);
        Assert.Equal(RunState.WillRun, Job(Run(yaml, Scenario.Default.WithJobFailed("build")), "triage").State);
    }

    [Fact]
    public void NeedsResult_CarriesTheComputedOutcomesIntoACondition()
    {
        const string yaml = """
            name: result-check
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo build
              publish:
                needs: [build]
                if: needs.build.result == 'success'
                runs-on: ubuntu-latest
                steps:
                  - run: echo publish
            """;

        Assert.Equal(RunState.WillRun, Job(Run(yaml), "publish").State);

        // The status check GitHub adds still applies to a condition that names no function, so a failed
        // dependency answers before the comparison is reached.
        var failing = Run(yaml, Scenario.Default.WithJobFailed("build"));
        Assert.Equal(RunState.WillNotRun, Job(failing, "publish").State);
        Assert.Equal(TriKind.False, Job(failing, "publish").Condition?.Result);
    }

    /// <summary>
    /// The "unless its own condition makes it continue" clause, on a skipped dependency. This is the only
    /// shape that reaches it: <c>always()</c> first, because a bare result comparison is wrapped in the
    /// implicit <c>success()</c> and lands on unknown.
    /// </summary>
    [Fact]
    public void ConditionAfterAlways_ContinuesPastASkippedDependency()
    {
        const string yaml = """
            name: skip-aware
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo build
              report:
                needs: [build]
                if: always() && needs.build.result == 'skipped'
                runs-on: ubuntu-latest
                steps:
                  - run: echo report
            """;

        Assert.Equal(RunState.WillRun, Job(Run(yaml, Scenario.Default.WithJobSkipped("build")), "report").State);
        Assert.Equal(RunState.WillNotRun, Job(Run(yaml), "report").State);
    }

    // ------------------------------------------------------------------ the documentation gap

    [Fact]
    public void SkippedDependency_LeavesTheDependentUnknownWithTheReasonSpelledOut()
    {
        var report = Run(Chain, Scenario.Default.WithJobSkipped("build"));

        Assert.Equal(RunState.WillNotRun, Job(report, "build").State);
        Assert.Equal(JobOutcome.Skipped, Job(report, "build").Outcome);

        var test = Job(report, "test");
        Assert.Equal(RunState.Unknown, test.State);
        Assert.Equal(JobOutcome.Unknown, test.Outcome);
        Assert.NotNull(test.Reason);
        Assert.Equal(UnknownReasonKind.ScenarioDependent, test.Reason!.Kind);

        // Both readings have to be visible, or the reader cannot check the claim.
        Assert.Contains("needs page", test.Reason.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status-checks page", test.Reason.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TriKind.Unknown, test.Condition?.Result);
        Assert.NotNull(test.Condition?.Reason);
    }

    [Fact]
    public void UnknownDependency_LeavesTheDependentUnknownToo()
    {
        const string yaml = """
            name: gated
            on: push
            jobs:
              decide:
                if: github.event_name == 'pull_request'
                runs-on: ubuntu-latest
                steps:
                  - run: echo decide
              after:
                needs: [decide]
                runs-on: ubuntu-latest
                steps:
                  - run: echo after
            """;

        // No event payload, so `decide` cannot be settled — and `after` inherits the gap through its needs.
        var report = Run(yaml);

        Assert.Equal(RunState.Unknown, Job(report, "decide").State);
        Assert.Equal(RunState.Unknown, Job(report, "after").State);
        Assert.Equal(UnknownReasonKind.ScenarioDependent, Job(report, "after").Reason?.Kind);
        Assert.Contains("decide", Text(Job(report, "after").Reason), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public void CancelledScenario_AnswersCancelledAndKeepsTheScopeMismatchVisible()
    {
        const string yaml = """
            name: cancel-aware
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo build
              cleanup:
                needs: [build]
                if: cancelled()
                runs-on: ubuntu-latest
                steps:
                  - run: echo cleanup
            """;

        // Never cancelled: cancelled() is false, so the job does not run.
        Assert.Equal(RunState.WillNotRun, Job(Run(yaml), "cleanup").State);

        // This very job pinned as cancelled: that is a definite answer about the scope the docs agree on.
        var jobCancelled = Run(yaml, Scenario.Default.WithJob("cleanup", ScenarioJobState.IsCancelled));
        Assert.Equal(RunState.WillRun, Job(jobCancelled, "cleanup").State);
        Assert.Equal(JobOutcome.Cancelled, Job(jobCancelled, "cleanup").Outcome);

        // The workflow cancelled but not this job: the two documented scopes disagree, so neither answer is
        // available and the reason says which sentence is missing.
        var workflowCancelled = Run(yaml, Scenario.Default.WithWorkflowCancelled());
        var cleanup = Job(workflowCancelled, "cleanup");
        Assert.Equal(RunState.Unknown, cleanup.State);
        Assert.Contains("workflow", Text(cleanup.Reason), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ context legality

    [Theory]
    // Every context GitHub's availability table keeps out of jobs.<job_id>.if, plus secrets which is in no
    // position's list at all.
    [InlineData("env.STAGE == 'prod'")]
    [InlineData("matrix.os == 'ubuntu-latest'")]
    [InlineData("strategy.fail == true")]
    [InlineData("steps.build.outputs.note != ''")]
    [InlineData("job.status == 'success'")]
    [InlineData("runner.os == 'Linux'")]
    [InlineData("secrets.ADMIN_TOKEN != ''")]
    [InlineData("hashFiles('package.json') != ''")]
    public void IllegalInAJobCondition_IsAContextAvailabilityFinding(string condition)
    {
        var yaml = $$"""
            name: job-condition
            on: push
            env:
              STAGE: prod
            jobs:
              gated:
                if: {{condition}}
                runs-on: ubuntu-latest
                steps:
                  - run: echo gated
            """;

        var report = Run(yaml);

        // The secrets case reaches the reader as AL004: the lint layer promotes the engine's finding for the
        // one context no position may read instead of reporting the same illegality twice.
        var finding = Assert.Single(report.Diagnostics, item =>
            item.Code == DiagnosticCodes.ContextNotAvailable
            || item.Code == LintRuleTable.SecretsInCondition.Id);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Contains("job-level if:", finding.Message, StringComparison.Ordinal);

        var gated = Job(report, "gated");
        Assert.Equal(RunState.Unknown, gated.State);
        Assert.Contains(UnknownReasonKind.ContextNotAvailable, Kinds(gated.Reason));

        // The position, not the knowledge: the message names what a job condition may read.
        Assert.Contains("github, inputs, needs, vars", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // The same names are all legal one level down, where the job exists and its matrix is applied.
    [InlineData("env.STAGE == 'prod'", TriKind.True)]
    [InlineData("matrix.os == 'ubuntu-latest'", TriKind.True)]
    [InlineData("strategy.fail == true", TriKind.True)]
    [InlineData("steps.build.outputs.note != ''", TriKind.Unknown)]
    [InlineData("job.status == 'success'", TriKind.Unknown)]
    [InlineData("runner.os == 'Linux'", TriKind.Unknown)]
    public void LegalInAStepCondition_IsNotAContextAvailabilityFinding(string condition, TriKind expected)
    {
        var yaml = $$"""
            name: step-condition
            on: push
            env:
              STAGE: prod
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest]
                steps:
                  - run: echo gated
                    if: {{condition}}
            """;

        var report = Run(yaml);

        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == DiagnosticCodes.ContextNotAvailable);
        Assert.Equal(expected, Step(report, "build", 0).Condition?.Result);
    }

    [Fact]
    public void Secrets_IsRefusedAtTheStepPositionToo()
    {
        const string yaml = """
            name: secret-step
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo gated
                    if: secrets.PREVIEW_TOKEN != ''
            """;

        var report = Run(yaml);

        // Promoted to the rule id by the lint layer, and still exactly one finding for the one condition.
        var finding = Assert.Single(report.Diagnostics, item => item.Code == LintRuleTable.SecretsInCondition.Id);
        Assert.Contains("step-level if:", finding.Message, StringComparison.Ordinal);

        // A secret is not "unknown because it is secret": the reason tree says the position forbids it.
        Assert.Contains(UnknownReasonKind.ContextNotAvailable, Kinds(Step(report, "build", 0).Reason));
    }

    [Fact]
    public void RuntimeContexts_AreUnknownBecauseNothingHasRun()
    {
        const string yaml = """
            name: nothing-has-run
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo first
                    id: probe
                  - run: echo second
                    if: steps.probe.outputs.note == 'note'
            """;

        var report = Run(yaml);
        var second = Step(report, "build", 1);

        Assert.Equal(RunState.Unknown, second.State);
        Assert.Contains(UnknownReasonKind.RuntimeContext, Kinds(second.Reason));
        Assert.Contains("no step", Text(second.Reason), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingInput_LeavesTheGithubContextUnknownAndSaysSo()
    {
        const string yaml = """
            name: needs-payload
            on: push
            jobs:
              gated:
                if: github.event_name == 'pull_request'
                runs-on: ubuntu-latest
                steps:
                  - run: echo gated
            """;

        var without = Run(yaml);
        Assert.Equal(RunState.Unknown, Job(without, "gated").State);
        Assert.Contains(UnknownReasonKind.ValueMissing, Kinds(Job(without, "gated").Reason));
        Assert.False(without.Event.HasEventPayload);

        var with = Run(yaml, payload: """{ "event_name": "pull_request", "repository": "acme/widget" }""");
        Assert.Equal(RunState.WillRun, Job(with, "gated").State);
        Assert.Equal("pull_request", with.Event.EventName);

        var otherEvent = Run(yaml, payload: """{ "event_name": "push" }""");
        Assert.Equal(RunState.WillNotRun, Job(otherEvent, "gated").State);
    }

    // ------------------------------------------------------------------ env inheritance

    [Fact]
    public void EnvChain_InheritsWorkflowThenJobThenStep()
    {
        const string yaml = """
            name: env-chain
            on: push
            env:
              LEVEL: workflow
              FROM_WORKFLOW: workflow
            jobs:
              build:
                runs-on: ubuntu-latest
                env:
                  LEVEL: job
                  FROM_JOB: job
                steps:
                  - run: echo inherits
                    if: env.FROM_WORKFLOW == 'workflow'
                  - run: echo job-wins
                    if: env.LEVEL == 'job'
                  - run: echo step-wins
                    env:
                      LEVEL: step
                    if: env.LEVEL == 'step'
                  - run: echo still-there
                    env:
                      LEVEL: step
                    if: env.FROM_JOB == 'job'
            """;

        var report = Run(yaml);

        Assert.Equal(RunState.WillRun, Step(report, "build", 0).State);
        Assert.Equal(RunState.WillRun, Step(report, "build", 1).State);
        Assert.Equal(RunState.WillRun, Step(report, "build", 2).State);
        Assert.Equal(RunState.WillRun, Step(report, "build", 3).State);
        // Nothing about the env chain produced a finding. The lint layer's note on the job's missing
        // timeout-minutes answers a different question, so it is not this test's subject.
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Stage != DiagnosticStage.Lint);
    }

    [Fact]
    public void EnvValueReadsTheMatrixCombinationItIsRunningUnder()
    {
        const string yaml = """
            name: env-matrix
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                env:
                    SELECTED: ${{ matrix.os }}
                steps:
                  - run: echo on-ubuntu
                    if: env.SELECTED == 'ubuntu-latest'
            """;

        var report = Run(yaml);

        // The first combination is ubuntu-latest, the second windows-latest: the env value is per run.
        Assert.Equal(RunState.WillRun, report.Jobs[0].Runs[0].Steps[0].State);
        Assert.Equal(RunState.WillNotRun, report.Jobs[0].Runs[1].Steps[0].State);
    }

    [Fact]
    public void EnvCannotReadEnv_IsReportedAsAnUnavailableContext()
    {
        const string yaml = """
            name: env-self-reference
            on: push
            env:
              FIRST: one
              SECOND: ${{ env.FIRST }}
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo gated
                    if: env.SECOND == 'one'
            """;

        var report = Run(yaml);

        // `env` is legal in the position but not while an env value is being defined, so SECOND has no
        // static value; the finding names the key rather than leaving the gap invisible.
        Assert.Contains(report.Diagnostics, finding => finding.Code == DiagnosticCodes.ScenarioValueUnresolved);
        Assert.Equal(RunState.WillNotRun, Step(report, "build", 0).State);
    }

    // ------------------------------------------------------------------ matrix instances

    [Fact]
    public void DynamicMatrix_KeepsTheCombinationCountUnknownAndTheStepsThatRead_It_Unknown()
    {
        const string yaml = """
            name: dynamic-matrix
            on: push
            jobs:
              setup:
                runs-on: ubuntu-latest
                steps:
                  - run: echo setup
              build:
                needs: [setup]
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: ${{ fromJSON(needs.setup.outputs.matrix) }}
                steps:
                  - run: echo always-runs
                  - run: echo reads-matrix
                    if: matrix.os == 'ubuntu-latest'
            """;

        var report = Run(yaml);
        var build = Job(report, "build");

        // The job itself runs — its own condition does not read the matrix.
        Assert.Equal(RunState.WillRun, build.State);
        Assert.True(build.Matrix?.IsDynamic);
        Assert.Null(build.Matrix?.CombinationCount);
        Assert.Equal(["os"], build.Matrix?.DynamicDimensions);

        // One representative row, labelled as not a real combination.
        var row = Assert.Single(build.Runs);
        Assert.False(row.CombinationIsKnown);

        // A step that does not read matrix.* is still decided; one that does is unknown with a reason.
        Assert.Equal(RunState.WillRun, build.Runs[0].Steps[0].State);
        Assert.Equal(RunState.Unknown, build.Runs[0].Steps[1].State);
        Assert.Contains(UnknownReasonKind.ValueMissing, Kinds(build.Runs[0].Steps[1].Reason));

        // And the total job count is unknown rather than partial: MTX002 is the finding that says so.
        Assert.Null(report.Totals.JobInstances);
        Assert.Contains(report.Diagnostics, finding => finding.Code == DiagnosticCodes.MatrixDynamic);
    }

    [Fact]
    public void MatrixExpandsToNothing_LeavesTheJobWithoutInstances()
    {
        const string yaml = """
            name: empty-matrix
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: []
                steps:
                  - run: echo never
            """;

        var report = Run(yaml);

        Assert.Empty(Job(report, "build").Runs);
        Assert.Equal(0, Job(report, "build").Matrix?.CombinationCount);
        Assert.Contains(report.Diagnostics, finding => finding.Code == DiagnosticCodes.MatrixValueInvalid);
    }

    // ------------------------------------------------------------------ scenario bookkeeping

    [Fact]
    public void PinNamingNoJob_IsAWarningAndChangesNothing()
    {
        var report = Run(Chain, Scenario.Default.WithJobFailed("deploy"));

        var finding = Assert.Single(report.Diagnostics, item => item.Code == DiagnosticCodes.ScenarioUnknownJob);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains("deploy", finding.Message, StringComparison.Ordinal);
        Assert.Equal(RunState.WillRun, Job(report, "test").State);
    }

    [Fact]
    public void PinThatContradictsItsOwnNeeds_IsReportedAndKept()
    {
        var report = Run(Chain, Scenario.Default.WithJobFailed("build").WithJobFailed("test"));

        // The user says both fail; a real run could only skip `test`, so the contradiction is a finding and
        // the pin still wins, because a scenario is a claim about the run rather than a derivation.
        var finding = Assert.Single(report.Diagnostics, item => item.Code == DiagnosticCodes.ScenarioConflict);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Equal(RunState.WillRun, Job(report, "test").State);
        Assert.Equal(JobOutcome.Failure, Job(report, "test").Outcome);
        Assert.True(Job(report, "test").IsPinnedByScenario);
    }

    [Fact]
    public void ScenarioDescribe_ListsPinsInDocumentOrder()
    {
        var scenario = Scenario.Default.WithJobFailed("test").WithJobSkipped("build");

        Assert.Equal("every job succeeds", Scenario.Default.Describe(DocumentOrder));
        Assert.Equal("build is skipped; test fails", scenario.Describe(DocumentOrder));
        Assert.Equal(
            "build is skipped; test fails; the run was cancelled",
            scenario.WithWorkflowCancelled().Describe(DocumentOrder));
    }

    // ------------------------------------------------------------------ the step layer

    [Fact]
    public void StepOfAFailedJob_DoesNotRunExceptUnderAlways()
    {
        const string yaml = """
            name: upload-after-failure
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo compile
                  - run: echo upload
                    if: always()
                  - run: echo publish
                    if: success()
            """;

        var report = Run(yaml, Scenario.Default.WithJobFailed("build"));
        var build = Job(report, "build");

        Assert.Equal(RunState.WillRun, build.State);
        Assert.Equal(JobOutcome.Failure, build.Outcome);
        Assert.Equal(RunState.WillNotRun, build.Runs[0].Steps[0].State);
        Assert.Equal(RunState.WillRun, build.Runs[0].Steps[1].State);
        Assert.Equal(RunState.WillNotRun, build.Runs[0].Steps[2].State);

        // A step-level success() reads the job's status, so a step skipped by its own condition cannot make
        // a later step fail — nothing here models step results at all.
        Assert.Equal("success()", build.Runs[0].Steps[2].Condition?.EvaluatedAs);
    }

    [Fact]
    public void UnknownJobs_LeaveTheirStepsUnknownWithTheJobNamed()
    {
        var report = Run(Chain, Scenario.Default.WithJobSkipped("build"));
        var step = Step(report, "test", 0);

        Assert.Equal(RunState.Unknown, step.State);
        Assert.NotNull(step.Reason);
        Assert.Contains(UnknownReasonKind.ScenarioDependent, Kinds(step.Reason));
        Assert.Contains("job itself may not run", Text(step.Reason), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryUnknownStateCarriesAReasonAcrossEveryScenario()
    {
        var scenarios = new[]
        {
            Scenario.Default,
            Scenario.Default.WithJobFailed("build"),
            Scenario.Default.WithJobSkipped("build"),
            Scenario.Default.WithJobCancelled("build"),
            Scenario.Default.WithWorkflowCancelled(),
            Scenario.Default.WithJobFailed("no-such-job"),
        };

        foreach (var scenario in scenarios)
        {
            var report = Run(Chain, scenario);
            Assert.All(report.Jobs, job =>
            {
                if (job.State == RunState.Unknown)
                {
                    Assert.NotNull(job.Reason);
                    Assert.False(string.IsNullOrWhiteSpace(job.Reason!.Message));
                }

                if (job.Condition?.Result == TriKind.Unknown)
                {
                    Assert.NotNull(job.Condition.Reason);
                }

                Assert.All(job.Runs, run => Assert.All(run.Steps, step =>
                {
                    if (step.State == RunState.Unknown)
                    {
                        Assert.NotNull(step.Reason);
                        Assert.False(string.IsNullOrWhiteSpace(step.Reason!.Message));
                    }
                }));
            });
        }
    }

    // ------------------------------------------------------------------ report wiring

    [Fact]
    public void ReportCarriesLayoutAndConditionTraceForEveryJob()
    {
        var report = Run(Chain);

        Assert.Equal(2, report.Totals.Jobs);
        Assert.Equal(2, report.Totals.Steps);
        Assert.Equal(2, report.Totals.JobInstances);
        Assert.Equal(2, report.Graph.Nodes.Count);
        var edge = Assert.Single(report.Graph.Edges);
        Assert.Equal("build", edge.From);
        Assert.Equal("test", edge.To);
        Assert.Equal(new NodePlacement(0, 0), Job(report, "build").Placement);
        Assert.Equal(new NodePlacement(1, 0), Job(report, "test").Placement);
    }

    [Fact]
    public void BrokenWorkflow_StillProducesAReportWithFindings()
    {
        const string yaml = """
            name: broken
            on: [push
            jobs:
              build:
                runs-on: ubuntu-latest
            """;

        var report = Run(yaml);

        Assert.NotNull(report);
        Assert.True(report.IsBlocked);
        Assert.NotEmpty(report.Diagnostics);
        Assert.Empty(report.Jobs);
        Assert.Equal(0, report.Totals.Jobs);
    }

    [Fact]
    public void EmptyText_IsAFindingNotAnException()
    {
        var report = Run("   ");

        Assert.True(report.IsBlocked);
        Assert.Equal(DiagnosticCodes.YamlSyntax, Assert.Single(report.Diagnostics).Code);
        // With no model at all, the file name is the only identity left.
        Assert.Equal("inline.yml", report.Workflow.Name);
        Assert.Equal("inline.yml", report.Workflow.FileName);
    }
}
