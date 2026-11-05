using System.Text.Json;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;

namespace ActionLens.Core.Tests.Scenarios;

/// <summary>
/// The report's JSON export, checked as an artifact: byte-stable, ordered, and structured enough that an
/// unknown's reason tree survives the trip as data rather than as a printed sentence.
/// </summary>
/// <remarks>
/// A shared report and a golden file both depend on the same promise, which is why these tests re-read the
/// export as JSON instead of grepping it: the shape has to be navigable, not merely present.
/// </remarks>
public sealed class ReportDeterminismTests
{
    private const string Workflow = """
        name: export-shape
        on:
          push:
            branches: [main]
          workflow_dispatch:
            inputs:
              stage:
                type: string
                default: prod
        env:
          STAGE: prod
        jobs:
          build:
            runs-on: ubuntu-latest
            strategy:
              matrix:
                os: [ubuntu-latest, windows-latest]
            steps:
              - run: echo build
              - run: echo publish
                if: matrix.os == 'ubuntu-latest'
          gate:
            needs: [build]
            if: secrets.DEPLOY_KEY != '' && github.repository == 'acme/widget'
            runs-on: ubuntu-latest
            steps:
              - run: echo gated
        """;

    private const string Payload = """
        { "event_name": "push", "ref": "refs/heads/main", "repository": "acme/widget", "sender": { "login": "world" } }
        """;

    private static string JsonOf(Scenario? scenario = null, string? payload = Payload) =>
        AnalysisReportJson.ToJson(WorkflowAnalyzer.Analyze(Workflow, "export.yml", payload, scenario));

    [Fact]
    public void SameInput_ExportsIdenticalBytesTwice()
    {
        var first = JsonOf();
        var second = JsonOf();

        // Not "equal objects": the promise in ARCHITECTURE.md is byte-identical output for the same input.
        Assert.Equal(first, second);
        Assert.Equal(JsonOf(), JsonOf());

        // And through a fresh analyzer call, so nothing is cached across a process.
        Assert.Equal(first, AnalysisReportJson.ToJson(WorkflowAnalyzer.Analyze(Workflow, "export.yml", Payload)));
    }

    [Fact]
    public void ScenarioChange_ChangesTheReportButNotItsShape()
    {
        var defaultRun = JsonOf();
        var failing = JsonOf(Scenario.Default.WithJobFailed("build"));

        Assert.NotEqual(defaultRun, failing);

        Assert.Equal(TopLevelKeys(defaultRun), TopLevelKeys(failing));
    }

    [Fact]
    public void Export_KeyOrderIsTheOrderTheWriterSpelledOut()
    {
        var keys = TopLevelKeys(JsonOf());

        // Pinned as an ordered list because reflection gives no promise about member order, and a report a
        // user diffs has to put the same key in the same place every time.
        Assert.Equal(TopLevelOrder, keys);
    }

    [Fact]
    public void Export_CarriesNoGeneratedOrClockDependentValues()
    {
        foreach (var json in new[] { JsonOf(), JsonOf(Scenario.Default.WithJobFailed("build").WithWorkflowCancelled()) })
        {
            // A Guid is 8-4-4-4-12 hex digits; the dashes keep a 40-character action pin from matching it.
            Assert.DoesNotMatch(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", json);

            // An ISO-8601 timestamp, to the minute.
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", json);

            // A short of either, spelled the way a .NET type name would.
            Assert.DoesNotMatch(@"(Guid|DateTime|Random)", json);
        }
    }

    [Fact]
    public void UnknownReasonTree_SurvivesTheExportAsDataNotAsText()
    {
        using var document = JsonDocument.Parse(JsonOf(payload: null));
        var gate = JobElement(document, "gate");

        // The job is unknown because a secret is not readable in a job condition, and the payload is absent.
        Assert.Equal("unknown", gate.GetProperty("state").GetString());
        var reason = gate.GetProperty("reason");
        Assert.Equal("propagated", reason.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(reason.GetProperty("message").GetString()));
        Assert.Equal(JsonValueKind.Array, reason.GetProperty("causes").ValueKind);
        Assert.True(reason.GetProperty("causes").GetArrayLength() > 0);

        // Every node of the tree keeps the same four keys, so a reader can walk it without guessing.
        foreach (var node in Descend(reason))
        {
            Assert.NotNull(node.GetProperty("kind").GetString());
            Assert.NotNull(node.GetProperty("message").GetString());
            Assert.NotNull(node.GetProperty("range").GetString());
            Assert.Equal(JsonValueKind.Array, node.GetProperty("causes").ValueKind);
        }

        // The illegality is named, not swallowed: an unavailable context is a distinct kind from a gap.
        Assert.Contains(
            Descend(gate.GetProperty("condition").GetProperty("reason")),
            node => node.GetProperty("kind").GetString() == "context-not-available");
    }

    [Fact]
    public void Export_WritesTheConditionTraceTheEngineEvaluated()
    {
        using var document = JsonDocument.Parse(JsonOf());
        var build = JobElement(document, "build");
        var matrix = build.GetProperty("matrix");

        Assert.False(matrix.GetProperty("isDynamic").GetBoolean());
        Assert.Equal(2, matrix.GetProperty("combinationCount").GetInt32());
        Assert.Equal(2, matrix.GetProperty("producedCount").GetInt32());
        Assert.Equal(OneDimension, matrix.GetProperty("dimensions").EnumerateArray().Select(text => text.GetString()));
        Assert.Equal(TwoOperatingSystems, matrix.GetProperty("combinationLabels").EnumerateArray().Select(text => text.GetString()));

        // Two runs, and the step that reads matrix.* answers differently for each of them.
        var runs = build.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Equal("will-run", runs[0].GetProperty("steps").EnumerateArray().Skip(1).First().GetProperty("state").GetString());
        Assert.Equal("will-not-run", runs[1].GetProperty("steps").EnumerateArray().Skip(1).First().GetProperty("state").GetString());
        Assert.Equal(
            "success() && matrix.os == 'ubuntu-latest'",
            runs[0].GetProperty("steps").EnumerateArray().Skip(1).First().GetProperty("condition").GetProperty("evaluatedAs").GetString());
    }

    [Fact]
    public void Export_ReportsTheScenarioThatWasAskedFor()
    {
        using var document = JsonDocument.Parse(JsonOf(Scenario.Default.WithJobFailed("build").WithWorkflowCancelled()));
        var scenario = document.RootElement.GetProperty("scenario");

        Assert.Equal("default", scenario.GetProperty("label").GetString());
        Assert.Equal("build fails; the run was cancelled", scenario.GetProperty("description").GetString());
        Assert.True(scenario.GetProperty("workflowCancelled").GetBoolean());

        var pin = Assert.Single(scenario.GetProperty("pins").EnumerateArray());
        Assert.Equal("build", pin.GetProperty("job").GetString());
        Assert.Equal("fails", pin.GetProperty("state").GetString());

        // The pin is visible on the job it was written for, next to the state it forced.
        var build = JobElement(document, "build");
        Assert.True(build.GetProperty("pinnedByScenario").GetBoolean());
        Assert.Equal("fails", build.GetProperty("scenarioPin").GetString());
        Assert.Equal("will-run", build.GetProperty("state").GetString());
        Assert.Equal("failure", build.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Export_KeepsTheGraphLayoutAndFindingsTogether()
    {
        using var document = JsonDocument.Parse(JsonOf());
        var graph = document.RootElement.GetProperty("graph");

        Assert.True(graph.GetProperty("isAcyclic").GetBoolean());
        Assert.Equal(2, graph.GetProperty("jobCount").GetInt32());
        Assert.Equal(1, graph.GetProperty("edgeCount").GetInt32());
        Assert.Equal(2, graph.GetProperty("layerCount").GetInt32());
        Assert.Equal("build", graph.GetProperty("edges")[0].GetProperty("from").GetString());
        Assert.Equal("gate", graph.GetProperty("edges")[0].GetProperty("to").GetString());
        Assert.Equal(0, graph.GetProperty("unplacedJobs").GetArrayLength());
        Assert.Empty(graph.GetProperty("cycles").EnumerateArray());

        // The secrets misuse in gate's condition reaches the reader as the lint rule that owns it: the engine
        // finding is promoted to AL004, so one problem is one finding.
        var finding = Assert.Single(
            document.RootElement.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "AL004");
        Assert.Equal("error", finding.GetProperty("severity").GetString());
        Assert.Equal("lint", finding.GetProperty("stage").GetString());
        Assert.Equal("gate", finding.GetProperty("scope").GetString());
        Assert.Contains("job-level if:", finding.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("https://docs.github.com/", finding.GetProperty("helpLink").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("suggestion").GetString()));

        // Positions travel with findings, because a finding without one cannot be jumped to.
        Assert.NotEqual("?", finding.GetProperty("range").GetString());
    }

    private static readonly string[] TopLevelOrder =
    [
        "schemaVersion", "workflow", "event", "trigger", "scenario", "totals", "graph",
        "jobCount", "jobs", "diagnosticCount", "diagnostics", "blocked",
    ];

    private static readonly string[] OneDimension = ["os"];

    private static readonly string[] TwoOperatingSystems = ["ubuntu-latest", "windows-latest"];

    private static IEnumerable<JsonElement> Descend(JsonElement reason)
    {
        yield return reason;
        foreach (var cause in reason.GetProperty("causes").EnumerateArray())
        {
            foreach (var nested in Descend(cause))
            {
                yield return nested;
            }
        }
    }

    private static JsonElement JobElement(JsonDocument document, string jobId) =>
        document.RootElement.GetProperty("jobs").EnumerateArray()
            .Single(job => string.Equals(job.GetProperty("job").GetString(), jobId, StringComparison.Ordinal));

    private static string[] TopLevelKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }
}
