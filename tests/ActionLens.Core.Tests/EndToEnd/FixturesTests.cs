using System.Text.RegularExpressions;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Triggers;

namespace ActionLens.Core.Tests.EndToEnd;

/// <summary>
/// The three fixtures in <c>samples/workflows/</c>, analysed end to end.
/// </summary>
/// <remarks>
/// <para>
/// Every expected number here was worked out from the YAML by hand and is written as a literal with its
/// arithmetic in a comment — never derived from what the code under test produced. That is the only way
/// this file can fail, which is what it is for: a fixture test that reads its expectations off the engine
/// is a restatement of the engine.
/// </para>
/// <para>
/// Two rules are asserted for all three files: the analysis completes and returns a report (a broken
/// workflow is a finding, never an exception), and nothing is <c>unknown</c> without a reason attached.
/// </para>
/// </remarks>
public sealed class FixturesTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "samples", "workflows");

    private static AnalysisReport Analyse(
        string file,
        Scenario? scenario = null,
        string? payload = null,
        string? triggerEvent = null) =>
        WorkflowAnalyzer.Analyze(
            File.ReadAllText(Path.Combine(FixtureDirectory, file)),
            file,
            payload,
            scenario,
            triggerEvent);

    private static JobReport Job(AnalysisReport report, string jobId) =>
        report.Jobs.Single(job => string.Equals(job.JobId, jobId, StringComparison.Ordinal));

    /// <summary>The one decision a verdict recorded for a filter key, or the failure that says it did not.</summary>
    private static FilterDecision Decision(AnalysisReport report, string filter)
    {
        Assert.NotNull(report.Trigger.Verdict);
        return report.Trigger.Verdict!.Decisions.Single(decision => decision.Filter == filter);
    }

    /// <summary>
    /// The invariant the whole layer is built on: <c>unknown</c> is an answer with an explanation, and an
    /// unknown without one is a bug rather than a result.
    /// </summary>
    private static void AssertEveryUnknownHasAReason(AnalysisReport report)
    {
        foreach (var job in report.Jobs)
        {
            if (job.State == RunState.Unknown)
            {
                Assert.NotNull(job.Reason);
                Assert.False(string.IsNullOrWhiteSpace(job.Reason!.Message));
            }
            else
            {
                Assert.Null(job.Reason);
            }

            AssertConditionExplainsItself(job.JobId, job.Condition);

            foreach (var run in job.Runs)
            {
                foreach (var step in run.Steps)
                {
                    if (step.State == RunState.Unknown)
                    {
                        Assert.NotNull(step.Reason);
                        Assert.False(string.IsNullOrWhiteSpace(step.Reason!.Message));
                    }
                    else
                    {
                        Assert.Null(step.Reason);
                    }

                    AssertConditionExplainsItself($"{job.JobId} step {step.Index}", step.Condition);
                }
            }
        }
    }

    private static void AssertConditionExplainsItself(string scope, ConditionTrace? condition)
    {
        if (condition is null)
        {
            return;
        }

        if (condition.Result == TriKind.Unknown)
        {
            Assert.NotNull(condition.Reason);
            Assert.False(string.IsNullOrWhiteSpace(condition.Reason!.Message));
        }
        else
        {
            Assert.Null(condition.Reason);
        }

        // The implicit wrap is part of the answer, not a detail: without it a reader cannot see why a job
        // whose only condition was a comparison stops running when an ancestor fails.
        Assert.False(string.IsNullOrWhiteSpace(condition.EvaluatedAs));
        Assert.Equal(condition.ImplicitSuccessApplied, condition.EvaluatedAs.StartsWith("success()", StringComparison.Ordinal));
        _ = scope;
    }

    // ------------------------------------------------------------------ atlas-ci.yml

    /// <summary>
    /// Atlas's real CI: a two-value matrix, <c>if: always()</c>, and — the honest gap recorded in the
    /// fixture header — no <c>needs:</c> anywhere, so the dependency graph is a single layer.
    /// </summary>
    [Fact]
    public void AtlasCi_DefaultScenario_RunsEveryJobInOneLayer()
    {
        var report = Analyse("atlas-ci.yml");

        AssertEveryUnknownHasAReason(report);
        Assert.False(report.IsBlocked);

        // Atlas's CI is a healthy file: no stage produced an error or a warning. The lint layer does have
        // news about it, and this is the honest count of it — 9 tag-pinned remote actions (build 4:
        // checkout, setup-dotnet, setup-node, upload-artifact; format 2; pack 3) and 3 jobs with no
        // timeout-minutes of their own.
        // The matrix stage also has one limitation notice: the file declares `fail-fast: false` (line 41),
        // which only a run can show the effect of. Reading that key and silently dropping it was the defect
        // fixed under review Q6, so the finding is asserted rather than excluded — this assertion is
        // narrower than the old blanket "no non-Lint finding", not weaker.
        Assert.DoesNotContain(report.Diagnostics, finding =>
            finding.Stage != DiagnosticStage.Lint && finding.Code != DiagnosticCodes.MatrixSchedulingNotModelled);
        var scheduling = Assert.Single(report.Diagnostics,
            finding => finding.Code == DiagnosticCodes.MatrixSchedulingNotModelled);
        Assert.Equal(DiagnosticSeverity.Info, scheduling.Severity);
        Assert.True(scheduling.Range.IsKnown);
        Assert.Equal(9, report.Diagnostics.Count(finding => finding.Code == "AL001"));
        Assert.Equal(3, report.Diagnostics.Count(finding => finding.Code == "AL005"));
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == "AL002");
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == "AL007");
        Assert.Equal("CI", report.Workflow.Name);
        Assert.Equal("atlas-ci.yml", report.Workflow.FileName);
        Assert.Equal(3, report.Jobs.Count);
        Assert.Equal(3, report.Totals.WillRun);
        Assert.Equal(0, report.Totals.WillNotRun);
        Assert.Equal(0, report.Totals.UnknownJobs);

        // No `needs:` in the file: no edges, one layer, three slots.
        Assert.Empty(report.Graph.Edges);
        Assert.Equal(1, report.Graph.LayerCount);
        Assert.Equal(3, report.Graph.MaxLayerWidth);
        Assert.Equal(0, Job(report, "build").Placement?.Layer ?? -1);
        Assert.Equal(1, Job(report, "format").Placement?.Slot ?? -1);

        // strategy.matrix.os: [ubuntu-latest, windows-latest] → 1 dimension × 2 values = 2 combinations,
        // and the two other jobs run once each, so 2 + 1 + 1 = 4 job instances.
        var build = Job(report, "build");
        Assert.Equal(RunState.WillRun, build.State);
        Assert.Equal(2, build.Matrix?.CombinationCount);
        Assert.Equal(["ubuntu-latest", "windows-latest"], build.Matrix?.CombinationLabels);
        Assert.Equal(4, report.Totals.JobInstances);
        Assert.Equal(2, build.Runs.Count);

        // `runs-on: ${{ matrix.os }}` is a label expression, not a condition: the job has no `if:` at all,
        // so what was evaluated is the implicit status check alone.
        Assert.True(build.Condition?.ImplicitSuccessApplied);
        Assert.Equal("success()", build.Condition?.EvaluatedAs);
        Assert.Equal("", build.Condition?.Text);
        Assert.Equal(TriKind.True, build.Condition?.Result);
    }

    [Fact]
    public void AtlasCi_AfterTheBuildJobFails_TheAlwaysStepStillRuns()
    {
        var report = Analyse("atlas-ci.yml", Scenario.Default.WithJobFailed("build"));

        AssertEveryUnknownHasAReason(report);
        var build = Job(report, "build");
        Assert.Equal(RunState.WillRun, build.State);
        Assert.Equal(JobOutcome.Failure, build.Outcome);
        Assert.True(build.IsPinnedByScenario);
        Assert.Equal(ScenarioJobState.Fails, build.ScenarioPin);

        // Atlas's ci.yml has no `needs:`, so format and pack start in parallel and are unaffected by a
        // build failure: each job's own success() only looks at the jobs it needs, and it needs none.
        Assert.Equal(RunState.WillRun, Job(report, "format").State);
        Assert.Equal(RunState.WillRun, Job(report, "pack").State);

        foreach (var run in build.Runs)
        {
            // 8 steps: checkout, setup-dotnet, setup-node, restore, npm, build, test, upload coverage.
            // The first seven carry no condition, so the implicit success() is false and they do not run.
            Assert.Equal(8, run.Steps.Count);
            Assert.Equal(7, run.Steps.Count(step => step.State == RunState.WillNotRun));

            var upload = run.Steps[7];
            Assert.Equal("Upload coverage", upload.Name);
            Assert.Equal(RunState.WillRun, upload.State);

            // always() is unconditional — and it is the author's own text, not something ActionLens added.
            Assert.Equal("always()", upload.Condition?.EvaluatedAs);
            Assert.False(upload.Condition?.ImplicitSuccessApplied);
            Assert.Equal(TriKind.True, upload.Condition?.Result);
        }
    }

    [Fact]
    public void AtlasCi_AfterTheBuildJobIsSkipped_ItsOwnStepsAreNotEvaluated()
    {
        var report = Analyse("atlas-ci.yml", Scenario.Default.WithJobSkipped("build"));

        AssertEveryUnknownHasAReason(report);
        var build = Job(report, "build");
        Assert.Equal(RunState.WillNotRun, build.State);
        Assert.Equal(JobOutcome.Skipped, build.Outcome);

        // A skipped job never reaches a step, so no step of it can claim its condition was read. Showing an
        // evaluated condition here would be a guess about a job that did not start.
        Assert.All(build.Runs, run => Assert.All(run.Steps, step =>
        {
            Assert.Equal(RunState.WillNotRun, step.State);
            Assert.Null(step.Condition);
        }));
    }

    // ------------------------------------------------------------------ matrix-build.yml

    /// <summary>
    /// prettier's <c>prod-test.yml</c>: two matrix jobs with both <c>include</c> and <c>exclude</c>, and
    /// five jobs gated by <c>needs: [build]</c>.
    /// </summary>
    [Fact]
    public void MatrixBuild_ExpandsBothMatrices_ToTheHandWorkedCounts()
    {
        var report = Analyse("matrix-build.yml");

        AssertEveryUnknownHasAReason(report);
        Assert.False(report.IsBlocked);

        // prettier's file pins every remote action to a full commit SHA and calls one local reusable
        // workflow, so AL001 has nothing to say; the three lint findings are the jobs with no timeout
        // (lint, cli, preview — test and browser declare 15, and the `uses:` job cannot declare one).
        // Two jobs declare `fail-fast: false` (lines 79 and 168), so the matrix stage reports both as
        // limitations of the model instead of silently discarding the keys (review Q6).
        Assert.DoesNotContain(report.Diagnostics, finding =>
            finding.Stage != DiagnosticStage.Lint && finding.Code != DiagnosticCodes.MatrixSchedulingNotModelled);
        Assert.Equal(2, report.Diagnostics.Count(finding =>
            finding.Code == DiagnosticCodes.MatrixSchedulingNotModelled));
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == "AL001");
        Assert.Equal(
            ["cli", "lint", "preview"],
            report.Diagnostics
                .Where(finding => finding.Code == "AL005")
                .Select(finding => finding.Scope ?? string.Empty)
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal("Prod", report.Workflow.Name);
        Assert.Equal(6, report.Jobs.Count);

        // test: os [ubuntu-latest, macos-latest, windows-latest] × node [26, 14] = 6 cross product rows.
        // exclude {os: macos-latest, node: "14"} removes 1 → 5 rows survive.
        // include, matched against those 5:
        //   {os: ubuntu-latest, node: "26", FULL_TEST: true} → filter hits 1 row, adds a value, no new row
        //   {os: ubuntu-latest, node: 24|22|20|18|16}        → 5 filters hit nothing → 5 new rows
        //   {os: macos-15-intel, node: "14"}                 → hits nothing       → 1 new row
        // 5 + 5 + 1 = 11 combinations.
        var test = Job(report, "test");
        Assert.Equal(11, test.Matrix?.CombinationCount);
        Assert.Equal(11, test.Runs.Count);
        Assert.Equal(["os", "node"], test.Matrix?.Dimensions);
        Assert.Equal(
            [
                "ubuntu-latest, 26, true", "ubuntu-latest, 14", "macos-latest, 26",
                "windows-latest, 26", "windows-latest, 14",
                "ubuntu-latest, 24", "ubuntu-latest, 22", "ubuntu-latest, 20",
                "ubuntu-latest, 18", "ubuntu-latest, 16", "macos-15-intel, 14",
            ],
            test.Matrix?.CombinationLabels);

        // The FULL_TEST flag only exists on the row the first include matched, and it arrives through
        // `env: FULL_TEST: ${{ matrix.FULL_TEST }}`; the appended rows carry no such key at all.
        Assert.Equal("true", test.Runs[0].Values["FULL_TEST"]);
        Assert.False(test.Runs[1].Values.ContainsKey("FULL_TEST"));

        // browser: os [ubuntu-latest, windows-latest] × browser [chrome, firefox] = 4 rows.
        // exclude {os: windows-latest, browser: chrome} removes 1 → 3.
        // include {os: ubuntu-latest, browser: chrome, FULL_TEST: true} filters on declared dimensions,
        // matches that surviving row and only adds a value → 3 combinations.
        var browser = Job(report, "browser");
        Assert.Equal(3, browser.Matrix?.CombinationCount);
        Assert.Equal(
            ["ubuntu-latest, chrome, true", "ubuntu-latest, firefox", "windows-latest, firefox"],
            browser.Matrix?.CombinationLabels);

        // Job instances: build 1 + lint 1 + test 11 + browser 3 + cli 1 + preview 1 = 18.
        Assert.Equal(18, report.Totals.JobInstances);

        // Five jobs list `needs: [build]`, so the graph has 5 edges over 2 layers.
        Assert.Equal(5, report.Graph.EdgeCount);
        Assert.Equal(2, report.Graph.LayerCount);
        Assert.Equal(0, Job(report, "build").Placement?.Layer ?? -1);
        Assert.Equal(1, Job(report, "lint").Placement?.Layer ?? -1);
        Assert.True(report.Graph.IsAcyclic);
        Assert.Equal(5, report.Jobs.Count(job => job.Needs.SequenceEqual(["build"])));
    }

    /// <summary>
    /// <c>preview</c> gates itself on <c>if: github.repository == 'prettier/prettier'</c>. With no event
    /// payload the repository is not knowable, so the job is unknown — not false, and not run.
    /// </summary>
    [Fact]
    public void MatrixBuild_WithoutAPayload_LeavesTheRepositoryGatedJobUnknown()
    {
        var report = Analyse("matrix-build.yml");

        AssertEveryUnknownHasAReason(report);
        Assert.False(report.Event.HasEventPayload);
        var preview = Job(report, "preview");
        Assert.Equal(RunState.Unknown, preview.State);
        Assert.Equal(TriKind.Unknown, preview.Condition?.Result);

        // What the author wrote carries no status function, so the wrap is implicit and visible.
        Assert.Equal("github.repository == 'prettier/prettier'", preview.Condition?.Text);
        Assert.Equal("success() && github.repository == 'prettier/prettier'", preview.Condition?.EvaluatedAs);
        Assert.True(preview.Condition?.ImplicitSuccessApplied);
        Assert.Equal(UnknownReasonKind.ValueMissing, DeepestKind(preview.Reason));

        // The other five need only a job that the default scenario says succeeded.
        Assert.Equal(5, report.Totals.WillRun);
        Assert.Equal(1, report.Totals.UnknownJobs);
        Assert.Equal(0, report.Totals.WillNotRun);
    }

    [Fact]
    public void MatrixBuild_WithAPayloadFromTheSameRepository_RunsThePreviewJobToo()
    {
        const string payload = """
            {
              "event_name": "push",
              "ref": "refs/heads/main",
              "sha": "d5d30c11e87a245cae6e91ce8dc910e57afddb20",
              "repository": { "full_name": "prettier/prettier", "name": "prettier" },
              "sender": { "login": "emsach" },
              "pull_request": { "head": { "ref": "feature/x" }, "base": { "ref": "main" } }
            }
            """;

        var report = Analyse("matrix-build.yml", payload: payload);

        AssertEveryUnknownHasAReason(report);
        Assert.True(report.Event.HasEventPayload);
        Assert.Equal("push", report.Event.EventName);
        Assert.Equal(6, report.Totals.WillRun);
        Assert.Equal(0, report.Totals.UnknownJobs);

        var preview = Job(report, "preview");
        Assert.Equal(RunState.WillRun, preview.State);
        Assert.Equal(TriKind.True, preview.Condition?.Result);
    }

    [Fact]
    public void MatrixBuild_ForkRepository_DoesNotRunThePreviewJob()
    {
        const string payload = """
            { "event_name": "push", "repository": "octocat/prettier", "sender": { "login": "octocat" } }
            """;

        var report = Analyse("matrix-build.yml", payload: payload);

        AssertEveryUnknownHasAReason(report);
        Assert.Equal(RunState.WillNotRun, Job(report, "preview").State);
        Assert.Equal(JobOutcome.Skipped, Job(report, "preview").Outcome);
        Assert.Equal(TriKind.False, Job(report, "preview").Condition?.Result);

        // The comparison settled without help from the unknown, so the other five jobs still run.
        Assert.Equal(5, report.Totals.WillRun);
    }

    [Fact]
    public void MatrixBuild_WhenBuildFails_EveryJobThatNeedsItStopsRunning()
    {
        var report = Analyse("matrix-build.yml", Scenario.Default.WithJobFailed("build"));

        AssertEveryUnknownHasAReason(report);
        Assert.Equal(RunState.WillRun, Job(report, "build").State);

        // Skip propagation: each of the five dependents has no status function of its own, so its implicit
        // success() is false and the job does not run — including `preview`, whose repository check is
        // never reached.
        foreach (var jobId in new[] { "lint", "test", "browser", "cli", "preview" })
        {
            var job = Job(report, jobId);
            Assert.Equal(RunState.WillNotRun, job.State);
            Assert.Equal(JobOutcome.Skipped, job.Outcome);
            Assert.Equal(TriKind.False, job.Condition?.Result);
        }

        Assert.Equal(1, report.Totals.WillRun);
        Assert.Equal(5, report.Totals.WillNotRun);

        // A job that does not run has no evaluated steps; their own conditions are GitHub's to skip.
        Assert.All(Job(report, "test").Runs, run => Assert.All(run.Steps, step =>
            Assert.Equal(RunState.WillNotRun, step.State)));

        // The matrix expansion is a property of the file, not of the scenario: the 11 rows are still the
        // 11 rows the job would have created.
        Assert.Equal(11, Job(report, "test").Matrix?.CombinationCount);
        Assert.Equal(18, report.Totals.JobInstances);
    }

    /// <summary>
    /// The documentation conflict, on a real file: a skipped ancestor does not settle a dependent's
    /// <c>success()</c>, so ActionLens says unknown instead of picking one of the two readings.
    /// </summary>
    [Fact]
    public void MatrixBuild_WhenBuildIsSkipped_ItsDependentsAreUnknownNotSkipped()
    {
        var report = Analyse("matrix-build.yml", Scenario.Default.WithJobSkipped("build"));

        AssertEveryUnknownHasAReason(report);
        Assert.Equal(RunState.WillNotRun, Job(report, "build").State);

        foreach (var jobId in new[] { "lint", "test", "browser", "cli" })
        {
            var job = Job(report, jobId);
            Assert.Equal(RunState.Unknown, job.State);
            Assert.Equal(UnknownReasonKind.ScenarioDependent, DeepestKind(job.Reason));
            Assert.Contains("skipped", job.Reason?.Message, StringComparison.OrdinalIgnoreCase);

            // The claim this test exists to protect: never silently reported as running.
            Assert.NotEqual(RunState.WillRun, job.State);
        }

        Assert.Equal(0, report.Totals.WillRun);
        Assert.Equal(5, report.Totals.UnknownJobs);
    }

    [Fact]
    public void MatrixBuild_StepConditionsAreEvaluatedPerCombination()
    {
        var report = Analyse("matrix-build.yml");
        var test = Job(report, "test");

        // Combination 0 is `ubuntu-latest` + node `26` (the row the FULL_TEST include matched).
        //   step 1 'Install yarn@3'  if: node == '14' || node == '16'  → false
        //   step 9 debug-benchmark   if: node != '14' && os != 'windows-latest' → true
        Assert.Equal("ubuntu-latest, 26, true", test.Runs[0].Label);
        Assert.Equal(RunState.WillNotRun, test.Runs[0].Steps[1].State);
        Assert.Equal(RunState.WillRun, test.Runs[0].Steps[9].State);

        // Combination 1 is `ubuntu-latest` + node `14`, where those two answers flip.
        Assert.Equal("ubuntu-latest, 14", test.Runs[1].Label);
        Assert.Equal(RunState.WillRun, test.Runs[1].Steps[1].State);
        Assert.Equal(RunState.WillNotRun, test.Runs[1].Steps[9].State);

        // And `windows-latest` + node `26` (combination 3) versus `macos-latest` + node `26` (combination 2):
        // the same node, so only the `matrix.os != 'windows-latest'` clause of step 9 can explain the
        // difference between them.
        Assert.Equal("macos-latest, 26", test.Runs[2].Label);
        Assert.Equal(RunState.WillNotRun, test.Runs[2].Steps[1].State);
        Assert.Equal(RunState.WillRun, test.Runs[2].Steps[9].State);
        Assert.Equal("windows-latest, 26", test.Runs[3].Label);
        Assert.Equal(RunState.WillNotRun, test.Runs[3].Steps[1].State);
        Assert.Equal(RunState.WillNotRun, test.Runs[3].Steps[9].State);

        // A step condition is a real expression: the chain of `||` was flattened and wrapped in the
        // implicit status check, which the trace shows.
        Assert.Equal(
            "success() && (matrix.node == '14' || matrix.node == '16')",
            test.Runs[0].Steps[1].Condition?.EvaluatedAs);
    }

    // ------------------------------------------------------------------ unsafe-demo.yml

    [Fact]
    public void UnsafeDemo_SelfReferentialNeedsProducesTheCycleFinding()
    {
        var report = Analyse("unsafe-demo.yml");

        AssertEveryUnknownHasAReason(report);

        // AL006's case: `lint` lists itself in needs. The graph layer found the cycle, and the lint layer
        // promoted its finding to the rule id rather than reporting the same edge a second time.
        var cycle = Assert.Single(report.Diagnostics, finding => finding.Code == "AL006");
        Assert.Contains("lint -> lint", cycle.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStage.Lint, cycle.Stage);
        Assert.Equal(DiagnosticSeverity.Error, cycle.Severity);
        Assert.Equal("lint", cycle.Scope);
        Assert.False(report.Graph.IsAcyclic);
        Assert.Equal(["lint"], report.Graph.UnplacedJobs);

        // A job inside a cycle has no order, so it is unknown — never placed, never silently running. The
        // cycle itself is named in the reason tree, one level below the "no position in the run order".
        var lint = Job(report, "lint");
        Assert.Equal(RunState.Unknown, lint.State);
        Assert.NotNull(lint.Reason);
        Assert.Equal(UnknownReasonKind.ScenarioDependent, lint.Reason!.Kind);
        Assert.Contains("cycle", ReasonTreeText(lint.Reason), StringComparison.Ordinal);

        // The unsafe file is unsafe on purpose: an unknown job sits behind a rejected secrets reference, so
        // the model is qualified rather than trusted.
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public void UnsafeDemo_SecretsInIfConditionsAreRejectedAtBothPositions()
    {
        var report = Analyse("unsafe-demo.yml");

        AssertEveryUnknownHasAReason(report);

        // AL004 owns this illegality in the report: the engine's unavailable-context finding is promoted to
        // the rule id, so the reader gets one finding per condition, not one from each layer.
        var findings = report.Diagnostics
            .Where(finding => finding.Code == "AL004")
            .ToArray();

        // The job-level case (deploy) and the step-level case (Deploy preview) each earn one finding, and
        // each names the position GitHub's availability table is talking about. An illegality is an error
        // because GitHub rejects the file, which is what makes this report blocked.
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding =>
        {
            Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
            Assert.Equal(DiagnosticStage.Lint, finding.Stage);
            Assert.True(finding.Range.IsKnown);
            Assert.NotNull(finding.Suggestion);
            Assert.StartsWith("https://docs.github.com/", finding.HelpLink, StringComparison.Ordinal);
        });

        // Nothing else in the report still carries the stage code for the same problem.
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == DiagnosticCodes.ContextNotAvailable);

        Assert.Contains(findings, finding => finding.Message.Contains("job-level if:", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Message.Contains("step-level if:", StringComparison.Ordinal));

        // A secret in a job condition means the job's own state cannot be decided.
        Assert.Equal(RunState.Unknown, Job(report, "deploy").State);
    }

    [Fact]
    public void UnsafeDemo_MatrixIncludeAddsValuesWithoutAddingRows()
    {
        var report = Analyse("unsafe-demo.yml");
        var build = Job(report, "matrix-build");

        // os [ubuntu-latest, windows-latest] × python-version ["3.11", "3.12"] = 4 rows.
        // include {os: ubuntu-latest, tox-args: "--cov"} filters on a declared dimension, matches the two
        // ubuntu rows and only adds tox-args to them → still 4 combinations, not 6.
        Assert.Equal(4, build.Matrix?.CombinationCount);
        Assert.Equal(
            ["ubuntu-latest, 3.11, --cov", "ubuntu-latest, 3.12, --cov", "windows-latest, 3.11", "windows-latest, 3.12"],
            build.Matrix?.CombinationLabels);

        // Job instances: matrix-build 4 + pr-target-repro 1 + lint 1 + deploy 1 + safe-verify 1 = 8.
        Assert.Equal(8, report.Totals.JobInstances);
        Assert.Equal(4, build.Runs.Count);
        Assert.Equal("ubuntu-latest", build.Runs[0].Values["os"]);
        Assert.Equal("--cov", build.Runs[0].Values["tox-args"]);
        Assert.False(build.Runs[2].Values.ContainsKey("tox-args"));
    }

    [Fact]
    public void UnsafeDemo_MatrixBuildFails_ChangesDownstreamJobsToNotRunOrUnknown()
    {
        var report = Analyse("unsafe-demo.yml", Scenario.Default.WithJobFailed("matrix-build"));

        AssertEveryUnknownHasAReason(report);

        // pr-target-repro and deploy both need matrix-build. Neither may come back as running.
        foreach (var jobId in new[] { "pr-target-repro", "deploy" })
        {
            var job = Job(report, jobId);
            Assert.True(
                job.State is RunState.WillNotRun or RunState.Unknown,
                $"'{jobId}' came back as {job.State}; a job downstream of a failure may not be reported as running.");

            if (job.State == RunState.Unknown)
            {
                Assert.NotNull(job.Reason);
            }
        }

        // Definite answers here: their implicit success() is false, and deploy's secrets comparison never
        // decides anything because `&&` short-circuits on the false status function.
        Assert.Equal(RunState.WillNotRun, Job(report, "pr-target-repro").State);
        Assert.Equal(RunState.WillNotRun, Job(report, "deploy").State);
        Assert.Equal(TriKind.False, Job(report, "deploy").Condition?.Result);

        // lint is inside the cycle, so a dependency's failure cannot make it definite either.
        Assert.Equal(RunState.Unknown, Job(report, "lint").State);

        // safe-verify needs nothing, so it is unaffected.
        Assert.Equal(RunState.WillRun, Job(report, "safe-verify").State);

        // The two `secrets.` conditions are still reported under this scenario, even though the evaluator
        // never reaches them: `&&` short-circuits on the definite false status function, so the engine's
        // unavailable-context finding is not produced here, and the static half of AL004 stands in for it.
        // GitHub rejects the file when it parses it either way, which is the claim the finding makes.
        Assert.Contains(report.Diagnostics, finding => finding.Code == "AL004");
        Assert.DoesNotContain(report.Diagnostics, finding => finding.Code == DiagnosticCodes.ContextNotAvailable);
        Assert.Contains(report.Diagnostics, finding => finding.Code == "AL006");
    }

    [Fact]
    public void UnsafeDemo_StepsThatDependOnStepOutputsStayUnknown()
    {
        var report = Analyse("unsafe-demo.yml");
        var deployPreview = Job(report, "matrix-build").Runs[0].Steps[4];

        Assert.Equal("Deploy preview", deployPreview.Name);
        Assert.Equal(RunState.Unknown, deployPreview.State);
        Assert.Equal(
            "success() && steps.vars.outputs.note != '' && secrets.PREVIEW_TOKEN != ''",
            deployPreview.Condition?.EvaluatedAs);

        // Nothing has run, so the step output is a knowledge gap while the secret is an illegality; the
        // reason tree has to keep those two apart rather than flattening them into "unknown".
        var kinds = FlattenedKinds(deployPreview.Reason).ToArray();
        Assert.Contains(UnknownReasonKind.RuntimeContext, kinds);
        Assert.Contains(UnknownReasonKind.ContextNotAvailable, kinds);
    }

    // ------------------------------------------------------------------ triggers on the real files

    /// <summary>
    /// Atlas's <c>branches: [main, master, 'feature/**', 'feat/**', 'fix/**', 'refactor/**', 'infra/**']</c>
    /// is one flow list, all of it on line 30. A push to <c>refs/heads/feature/login</c> is admitted by
    /// <c>feature/**</c> — <c>main</c> cannot admit it, because a pattern matches the whole ref name — and
    /// the report has to name the pattern that did.
    /// </summary>
    [Fact]
    public void AtlasCi_APushToAFeatureBranchFiresOnTheGlobNotOnAnEntryItResembles()
    {
        var report = Analyse("atlas-ci.yml", payload: """{ "event_name": "push", "ref": "refs/heads/feature/login" }""");
        Assert.NotNull(report.Trigger.Verdict);
        var verdict = report.Trigger.Verdict!;

        Assert.Equal("push", verdict.Event);
        Assert.Equal(TriKind.True, verdict.Runs.Kind);
        var branches = verdict.Decisions.Single(decision => decision.Filter == "branches");
        Assert.Equal("feature/**", branches.Pattern);
        Assert.Equal("feature/login", branches.Value);
        Assert.Equal(30, branches.PatternRange.Start.Line);
    }

    /// <summary>A branch outside every listed pattern is a definite no, with no pattern to blame but the list.</summary>
    [Fact]
    public void AtlasCi_APushToADependabotBranchDoesNotRun()
    {
        var report = Analyse("atlas-ci.yml", payload: """{ "event_name": "push", "ref": "refs/heads/dependabot/npm_and_yarn/caniuse-1.2" }""");
        Assert.NotNull(report.Trigger.Verdict);
        var verdict = report.Trigger.Verdict!;

        Assert.Equal(TriKind.False, verdict.Runs.Kind);
        Assert.Equal("branches", verdict.SaidNo?.Filter);

        // Nothing matched, so the decision shows the list it tested against rather than a fake winner.
        Assert.Contains("main, master", Decision2(report, "branches").Pattern, StringComparison.Ordinal);
        Assert.Contains("matches no pattern", Decision2(report, "branches").Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// For <c>pull_request</c> the same key reads the <em>base</em> branch: Atlas's
    /// <c>branches: [main, master]</c> on line 32 is about where the pull request lands, not where it came
    /// from, so a head branch named <c>main</c> onto <c>develop</c> does not run.
    /// </summary>
    [Fact]
    public void AtlasCi_PullRequestFiltersTheBaseBranchItsFileDeclares()
    {
        const string OntoMain = """
            { "event_name": "pull_request",
              "pull_request": { "base": { "ref": "main" }, "head": { "ref": "feature/login" } } }
            """;
        var ontoMain = Analyse("atlas-ci.yml", payload: OntoMain);
        Assert.NotNull(ontoMain.Trigger.Verdict);
        Assert.Equal(TriKind.True, ontoMain.Trigger.Verdict!.Runs.Kind);
        Assert.Equal(32, Decision2(ontoMain, "branches").PatternRange.Start.Line);

        const string OntoDevelop = """
            { "event_name": "pull_request",
              "pull_request": { "base": { "ref": "develop" }, "head": { "ref": "main" } } }
            """;
        var ontoDevelop = Analyse("atlas-ci.yml", payload: OntoDevelop);
        Assert.NotNull(ontoDevelop.Trigger.Verdict);
        Assert.Equal(TriKind.False, ontoDevelop.Trigger.Verdict!.Runs.Kind);
        Assert.Equal("develop", Decision2(ontoDevelop, "branches").Value);
    }

    /// <summary>
    /// Atlas also declares <c>workflow_dispatch:</c> (line 33) with no inputs. Asking about it without a
    /// payload is a question about whether a person pressed a button, so it stays unknown — and the job
    /// answers, which are about the file's conditions, are unaffected by that.
    /// </summary>
    [Fact]
    public void AtlasCi_ADispatchIsUnknownUntilAPayloadRecordsIt()
    {
        var asked = Analyse("atlas-ci.yml", triggerEvent: "workflow_dispatch");
        Assert.NotNull(asked.Trigger.Verdict);
        var verdict = asked.Trigger.Verdict!;

        Assert.Equal(TriKind.Unknown, verdict.Runs.Kind);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, verdict.Runs.Reason!.Kind);
        Assert.Contains("person", verdict.Runs.Reason!.Message, StringComparison.Ordinal);
        Assert.Equal(33, Decision2(asked, "inputs").PatternRange.Start.Line);

        // The trigger answer never changes the job answer: all three jobs still run under the default scenario.
        Assert.Equal(3, asked.Totals.WillRun);

        var recorded = Analyse("atlas-ci.yml", payload: """{ "event_name": "workflow_dispatch" }""");
        Assert.NotNull(recorded.Trigger.Verdict);
        Assert.Equal(TriKind.True, recorded.Trigger.Verdict!.Runs.Kind);
        Assert.True(recorded.Trigger.Verdict!.OccurrencePinnedByPayload);
    }

    /// <summary>
    /// prettier's real branch list, including the one pattern that only works because it is quoted:
    /// <c>"!renovate/**"</c> on line 36 excludes after the earlier positives had their chance.
    /// </summary>
    [Theory]
    [InlineData("refs/heads/main", "main", 32, true)]
    [InlineData("refs/heads/next", "next", 33, true)]
    [InlineData("refs/heads/v3.0", "v*", 34, true)]
    [InlineData("refs/heads/patch-release", "patch-release", 35, true)]
    [InlineData("refs/heads/renovate/rollup-1.2", "!renovate/**", 36, false)]
    [InlineData("refs/heads/topic", null, 0, false)]
    public void MatrixBuild_ItsRealBranchListAnswersEveryRef(
        string @ref,
        string? expectedPattern,
        int expectedLine,
        bool expectedToRun)
    {
        var report = Analyse("matrix-build.yml", payload: $$"""{ "event_name": "push", "ref": "{{@ref}}" }""");
        Assert.NotNull(report.Trigger.Verdict);
        var verdict = report.Trigger.Verdict!;

        Assert.Equal(expectedToRun ? TriKind.True : TriKind.False, verdict.Runs.Kind);
        var branches = Decision2(report, "branches");
        Assert.Equal(@ref["refs/heads/".Length..], branches.Value);

        if (expectedPattern is null)
        {
            // No pattern matched, so nothing is credited with the answer and the list's own sense refused it.
            Assert.DoesNotContain(branches.Pattern!, "renovate", StringComparison.Ordinal);
            Assert.Contains("matches no pattern", branches.Note, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(expectedPattern, branches.Pattern);
            Assert.Equal(expectedLine, branches.PatternRange.Start.Line);
        }
    }

    /// <summary>
    /// <c>pull_request:</c> in this file is written with no filters at all, so every pull request passes the
    /// trigger — the answer is true with an empty decision list, not an unknown for want of data.
    /// </summary>
    [Fact]
    public void MatrixBuild_AnUnfilteredPullRequestTriggerHasNothingToConsult()
    {
        var report = Analyse("matrix-build.yml", payload: """{ "event_name": "pull_request" }""");
        Assert.NotNull(report.Trigger.Verdict);

        Assert.Equal(TriKind.True, report.Trigger.Verdict!.Runs.Kind);
        Assert.Empty(report.Trigger.Verdict!.Decisions);

        // And the jobs are the ones the file declares, unchanged by the trigger answer.
        Assert.Equal(6, report.Jobs.Count);
    }

    /// <summary>
    /// The trigger findings are claims that GitHub rejects the file, so they must not fire on real
    /// workflows. All three fixtures are legal files with real filters.
    /// </summary>
    [Fact]
    public void Fixtures_DeclareTheirTriggersWithoutAnyTriggerFinding()
    {
        foreach (var file in new[] { "atlas-ci.yml", "matrix-build.yml", "unsafe-demo.yml" })
        {
            var report = Analyse(file);
            Assert.DoesNotContain(report.Diagnostics, finding => finding.Code.StartsWith("TRIG", StringComparison.Ordinal));
            Assert.Null(report.Trigger.Verdict);

            // No event was selected, so the report shows what the file declares and stops short of a verdict.
            Assert.NotEmpty(report.Trigger.Declared);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static FilterDecision Decision2(AnalysisReport report, string filter) =>
        report.Trigger.Verdict!.Decisions.Single(decision => decision.Filter == filter);

    private static UnknownReasonKind DeepestKind(UnknownReason? reason)
    {
        Assert.NotNull(reason);
        var current = reason!;
        while (current.Causes.Count > 0)
        {
            current = current.Causes[^1];
        }

        return current.Kind;
    }

    private static IEnumerable<UnknownReasonKind> FlattenedKinds(UnknownReason? reason)
    {
        if (reason is null)
        {
            yield break;
        }

        yield return reason.Kind;
        foreach (var cause in reason.Causes)
        {
            foreach (var nested in FlattenedKinds(cause))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Every message in the reason tree, joined, so an assertion can name a cause without pinning its depth.</summary>
    private static string ReasonTreeText(UnknownReason? reason) => reason is null
        ? string.Empty
        : string.Join(" ", new[] { reason.Message }.Concat(reason.Causes.Select(ReasonTreeText)));

    [Fact]
    public void Fixtures_ExportJsonWithoutAnyGeneratedValue()
    {
        foreach (var file in new[] { "atlas-ci.yml", "matrix-build.yml", "unsafe-demo.yml" })
        {
            var json = AnalysisReportJson.ToJson(Analyse(file));
            Assert.Matches("\"schemaVersion\": 1", json);

            // A Guid is 8-4-4-4-12 hex digits. The fixtures do contain 40-character action pins, which is
            // why the dashes are required: a bare hex run is a SHA, not an identifier.
            Assert.DoesNotMatch(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", json);

            // An ISO-8601 timestamp: no clock is allowed anywhere in the pipeline.
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", json);
        }
    }
}

/// <summary>Keeps <see cref="Regex"/> out of the analyzer path while the tests use it for shape checks.</summary>
file static class RegexShapes
{
}
