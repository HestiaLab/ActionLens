using ActionLens.Core.Diagnostics;
using ActionLens.Core.Graph;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Yaml;

namespace ActionLens.Core.Tests.Scenarios;

/// <summary>
/// §7 recorded that <c>ScenarioEngine.Run</c> takes the workflow and the job graph as two independent
/// arguments and indexes <c>_jobs[jobId]</c> directly. The in-repo caller always passes a matching pair,
/// but the API is public: a mismatched pair used to escape as a raw KeyNotFoundException from deep inside
/// the walk, which is neither a diagnostic nor an honest unknown.
/// </summary>
public sealed class ScenarioEngineMismatchTests
{
    private const string TwoJobs = """
        name: two
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
          deploy:
            needs: [build]
            runs-on: ubuntu-latest
        """;

    private const string OneJob = """
        name: one
        on: push
        jobs:
          solo:
            runs-on: ubuntu-latest
        """;

    [Fact]
    public void MismatchedGraph_DoesNotThrow_AndReportsTheUnknownJob()
    {
        var workflow = WorkflowLoader.Load(OneJob, "one.yml").Workflow!;
        var otherGraph = JobGraph.Build(WorkflowLoader.Load(TwoJobs, "two.yml").Workflow!).Graph;

        // The graph names build/deploy; the workflow only defines solo.
        Assert.DoesNotContain("build", workflow.Jobs.Select(job => job.Id));

        var evaluation = ScenarioEngine.Run(workflow, otherGraph, null);

        // No raw KeyNotFoundException, and no confident answer about a job with no definition.
        Assert.Empty(evaluation.Jobs);
        Assert.Equal(2, evaluation.Diagnostics.Count);
        Assert.All(evaluation.Diagnostics, finding =>
        {
            Assert.Equal(DiagnosticCodes.AnalysisFailed, finding.Code);
            Assert.Equal(DiagnosticStage.Internal, finding.Stage);
            Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
            Assert.Contains("same workflow", finding.Suggestion ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.Contains(evaluation.Diagnostics, finding => finding.Message.Contains("build", StringComparison.Ordinal));
        Assert.Contains(evaluation.Diagnostics, finding => finding.Message.Contains("deploy", StringComparison.Ordinal));
    }

    /// <summary>
    /// Decision F asks for one entry point. ScenarioEngine used to be public specifically so a caller
    /// could supply an unrelated graph; narrowing it removes that whole class of misuse.
    /// </summary>
    [Fact]
    public void ScenarioEngine_IsInternal_WithWorkflowAnalyzerAsThePublicEntryPoint()
    {
        Assert.False(typeof(ScenarioEngine).IsPublic);
        Assert.True(typeof(WorkflowAnalyzer).IsPublic);
    }
}
