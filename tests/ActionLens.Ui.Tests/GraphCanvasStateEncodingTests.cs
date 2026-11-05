using ActionLens.Core;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Ui.Components;
using ActionLens.Ui.Services;
using ActionLens.Ui.Tests.Supporting;
using AngleSharp.Dom;
using Bunit;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The canvas must encode the three states without relying on colour: a class (shape), a glyph
/// (+ / − / ? / ↻), drawn state words, and an accessible name that carries the job id, the state, and
/// the matrix run counts. The engine side of these states is pinned in ActionLens.Core.Tests; this
/// file pins the drawing of them, which is the half no engine test can see.
/// </summary>
public sealed class GraphCanvasStateEncodingTests : IDisposable
{
    private readonly ComponentHarness _harness = new();

    [Fact]
    public void DefaultRun_EncodesRunUnknownAndCycleStatesWithoutColour()
    {
        var cut = RenderCanvas(Scenario.Default);

        // unsafe-demo under the default scenario: the jobs that survive are run, deploy is unknown
        // (a secrets comparison is not decidable), and lint sits in a cycle the layout refused to place.
        AssertNode(cut, "matrix-build", tone: "run", glyph: "+", stateWords: "Will run");
        AssertNode(cut, "deploy", tone: "unknown", glyph: "?", stateWords: "unknown");
        AssertNode(cut, "lint", tone: "cycle", glyph: "↻", stateWords: "unknown");

        // Every node carries all three channels: a non-colour class, a glyph, and state words —
        // a bare glyph with no words is exactly the 'colour-only' encoding the promise forbids.
        foreach (var node in cut.FindAll(".al-graph-node"))
        {
            string classes = node.GetAttribute("class") ?? string.Empty;
            Assert.Matches("al-node--(run|skip|unknown|cycle|blocked)", classes);
            Assert.False(string.IsNullOrWhiteSpace(node.QuerySelector(".al-node-glyph")!.TextContent));
            Assert.False(string.IsNullOrWhiteSpace(node.QuerySelector(".al-node-state")!.TextContent));
        }
    }

    [Fact]
    public void FailedAncestor_EncodesWillNotRunAlongsideRunAndCycle()
    {
        var cut = RenderCanvas(Scenario.Default.WithJobFailed("matrix-build"));

        // One canvas holding WillRun, WillNotRun, and an in-cycle job together: the mix the reader
        // judges the picture by.
        AssertNode(cut, "matrix-build", tone: "run", glyph: "+", stateWords: "Will run");
        AssertNode(cut, "pr-target-repro", tone: "skip", glyph: "−", stateWords: "Will not");
        AssertNode(cut, "deploy", tone: "skip", glyph: "−", stateWords: "Will not");
        AssertNode(cut, "lint", tone: "cycle", glyph: "↻", stateWords: "unknown");
    }

    [Fact]
    public void AccessibleName_CarriesJobIdStateAndMatrixRunCounts()
    {
        var cut = RenderCanvas(Scenario.Default);

        // matrix-build carries a 4-combination matrix; a screen-reader user must hear the job id, the
        // state with its marker, and what the box stands for — the same channels a sighted reader gets.
        string? label = FindNode(cut, "matrix-build").GetAttribute("aria-label");
        Assert.NotNull(label);
        Assert.Contains("matrix-build", label!, StringComparison.Ordinal);
        Assert.Contains("+ Will run", label!, StringComparison.Ordinal);
        Assert.Contains("runs × 4", label!, StringComparison.Ordinal);

        // The cycle chip names the cycle path in its accessible name, because 'not placed' is the
        // non-colour signal that this job has no position in the schedule.
        string? lintLabel = FindNode(cut, "lint").GetAttribute("aria-label");
        Assert.NotNull(lintLabel);
        Assert.Contains("inside a cycle", lintLabel!, StringComparison.Ordinal);
    }

    private IRenderedComponent<GraphCanvas> RenderCanvas(Scenario scenario)
    {
        WorkflowExample example = WorkflowExamples.Workflows
            .Single(w => string.Equals(w.FileName, "unsafe-demo.yml", StringComparison.Ordinal));
        AnalysisReport report = WorkflowAnalyzer.Analyze(
            example.Yaml, example.FileName, eventPayloadJson: null, scenario, triggerEvent: null);
        return _harness.Context.Render<GraphCanvas>(parameters => parameters.Add(p => p.Report, report));
    }

    private static void AssertNode(
        IRenderedComponent<GraphCanvas> cut,
        string jobId,
        string tone,
        string glyph,
        string stateWords)
    {
        IElement node = FindNode(cut, jobId);
        string classes = node.GetAttribute("class") ?? string.Empty;
        Assert.Contains($"al-node--{tone}", classes, StringComparison.Ordinal);
        Assert.Equal(glyph, node.QuerySelector(".al-node-glyph")!.TextContent.Trim());
        Assert.Contains(stateWords, node.QuerySelector(".al-node-state")!.TextContent, StringComparison.Ordinal);
    }

    private static IElement FindNode(IRenderedComponent<GraphCanvas> cut, string jobId)
    {
        var node = cut
            .FindAll(".al-graph-node")
            .FirstOrDefault(candidate =>
                (candidate.GetAttribute("aria-label") ?? string.Empty).StartsWith(jobId, StringComparison.Ordinal));
        Assert.NotNull(node);
        return node!;
    }

    public void Dispose() => _harness.Dispose();
}
