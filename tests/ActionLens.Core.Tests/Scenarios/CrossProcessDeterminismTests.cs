using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;

namespace ActionLens.Core.Tests.Scenarios;

/// <summary>
/// The other half of <see cref="ReportDeterminismTests"/>: those tests prove one process produces the
/// same bytes twice. <c>docs/review-v1-grill.md</c> §7 listed cross-process variation as an untested
/// risk, because .NET randomises string hashes per process and a <c>Dictionary</c> walked without
/// sorting can therefore enumerate differently in two runs. Two genuinely separate processes are the
/// only way to close that question.
/// </summary>
/// <remarks>
/// The probe is a test method in this same assembly, selected with MTP's <c>--filter-method</c>. No new
/// project, solution entry or CI step is needed: the child process is the test runner itself, writing
/// its export to a path given by an environment variable.
/// </remarks>
public sealed class CrossProcessDeterminismTests
{
    private const string ExportPathVariable = "ACTIONLENS_EXPORT_PATH";

    private const string ProbeMethod =
        "ActionLens.Core.Tests.Scenarios.CrossProcessDeterminismTests.ExportProbe_WritesBundleToTheRequestedPath";

    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "samples", "workflows");

    /// <summary>
    /// A bundle of reports rather than one: matrix <c>include</c> keys, several <c>on:</c> events, lint
    /// findings and a scenario pin all exercise dictionaries with more than one entry. A single small
    /// workflow would leave the ordering question untested.
    /// </summary>
    private static byte[] ExportBundle()
    {
        var payload = """{ "event_name": "push", "ref": "refs/heads/main", "repository": "acme/widget" }""";
        var dispatch = """{ "event_name": "workflow_dispatch", "ref": "refs/heads/main", "inputs": { "stage": "prod" } }""";
        var shape = """
            name: export-shape
            on:
              push:
                branches: [main]
              workflow_dispatch:
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                steps:
                  - run: echo build
              gate:
                needs: [build]
                runs-on: ubuntu-latest
                steps:
                  - run: echo gated
            """;

        var reports = new[]
        {
            Analyse("atlas-ci.yml", payload),
            Analyse("atlas-ci.yml", dispatch, triggerEvent: "push"),
            Analyse("matrix-build.yml", payload),
            Analyse("unsafe-demo.yml", null),
            WorkflowAnalyzer.Analyze(shape, "shape.yml", payload),
            WorkflowAnalyzer.Analyze(shape, "shape.yml", payload, Scenario.Default.WithJobFailed("build")),
        };

        var text = new StringBuilder();
        foreach (var report in reports)
        {
            text.Append(AnalysisReportJson.ToJson(report)).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static ActionLens.Core.Reporting.AnalysisReport Analyse(
        string file,
        string? payload,
        string? triggerEvent = null) =>
        WorkflowAnalyzer.Analyze(
            File.ReadAllText(Path.Combine(FixtureDirectory, file)),
            file,
            payload,
            null,
            triggerEvent);

    /// <summary>
    /// The child-process half. In the ordinary suite this is still a real assertion; when the parent
    /// supplies a path it writes the bundle there instead, with no console output that could disturb the
    /// comparison.
    /// </summary>
    [Fact]
    public void ExportProbe_WritesBundleToTheRequestedPath()
    {
        var bytes = ExportBundle();
        var path = Environment.GetEnvironmentVariable(ExportPathVariable);
        if (path is null)
        {
            Assert.NotEmpty(bytes);
            Assert.Equal(bytes, ExportBundle());
            return;
        }

        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void TwoSeparateProcesses_ExportIdenticalBytes()
    {
        var assembly = typeof(CrossProcessDeterminismTests).Assembly.Location;
        var first = Path.Combine(Path.GetTempPath(), $"actionlens-determinism-{Guid.NewGuid():N}.bin");
        var second = Path.Combine(Path.GetTempPath(), $"actionlens-determinism-{Guid.NewGuid():N}.bin");

        try
        {
            RunProbe(assembly, first);
            RunProbe(assembly, second);

            var fromFirst = File.ReadAllBytes(first);
            var fromSecond = File.ReadAllBytes(second);

            // Byte equality, not object equality: a report a reader diffs must be the same file.
            Assert.True(fromFirst.AsSpan().SequenceEqual(fromSecond),
                "Two separate processes produced different report bytes for identical input.");
            Assert.True(fromFirst.AsSpan().SequenceEqual(ExportBundle()),
                "A child process disagreed with the parent process's export.");
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    /// <summary>
    /// A deterministic complement to the cross-process check. Hash randomisation only *sometimes*
    /// changes enumeration order, so this one constructs the difference deliberately: the same matrix
    /// <c>include</c> keys written in two orders must export the same structured <c>runs[].values</c>.
    /// It fails immediately if anyone removes the sort from <c>AnalysisReportJson.WriteRun</c>,
    /// regardless of hash seed.
    /// <para>
    /// <c>combinationLabels</c> is deliberately excluded: it is a display string joined from the YAML's
    /// own key order, which is stable for a given input and more readable that way. Requiring equal
    /// labels would be requiring two <em>different</em> inputs to produce the same report.
    /// </para>
    /// </summary>
    [Fact]
    public void IncludeKeyInsertionOrder_DoesNotChangeTheExportedValues()
    {
        const string head = """
            name: key-order
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest]
                    include:
            """;

        const string forward = """
                      - os: ubuntu-latest
                        node: '20'
                        shard: a
                        region: west
            """;

        const string reversed = """
                      - region: west
                        shard: a
                        node: '20'
                        os: ubuntu-latest
            """;

        const string tail = """
                steps:
                  - run: echo hi
            """;

        var a = AnalysisReportJson.ToJson(WorkflowAnalyzer.Analyze(head + "\n" + forward + "\n" + tail, "keys.yml"));
        var b = AnalysisReportJson.ToJson(WorkflowAnalyzer.Analyze(head + "\n" + reversed + "\n" + tail, "keys.yml"));

        using var first = JsonDocument.Parse(a);
        using var second = JsonDocument.Parse(b);

        var valuesOf = (JsonDocument document) =>
            document.RootElement.GetProperty("jobs")[0].GetProperty("runs")[0]
                .GetProperty("values").EnumerateObject().Select(property => (property.Name, property.Value.GetString()))
                .ToArray();

        var firstValues = valuesOf(first);
        var secondValues = valuesOf(second);

        Assert.Equal(firstValues, secondValues);
        // The keys are sorted, not merely stable: an unsorted dictionary walk would still be stable
        // for one insertion order and would drift across processes.
        Assert.Equal([.. firstValues.Select(pair => pair.Name).Order(StringComparer.Ordinal)],
            firstValues.Select(pair => pair.Name));
        Assert.Equal(4, firstValues.Length);
    }

    private static void RunProbe(string assembly, string outputPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add(ProbeMethod);
        start.ArgumentList.Add("--progress");
        start.ArgumentList.Add("off");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add("Minimal");
        start.Environment[ExportPathVariable] = outputPath;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the export probe process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The export probe did not finish within 60 seconds.");
        Assert.True(process.ExitCode == 0,
            $"The export probe exited with {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        Assert.True(File.Exists(outputPath), $"The export probe did not write {outputPath}.{Environment.NewLine}{stdout}");
    }
}
