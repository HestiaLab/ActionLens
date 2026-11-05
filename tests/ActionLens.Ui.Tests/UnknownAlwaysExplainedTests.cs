using ActionLens.Ui.Components;
using ActionLens.Ui.Tests.Supporting;
using Bunit;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The product promise is that <c>unknown</c> is an answer with an explanation, never a bare marker.
/// The engine already guarantees a reason on every unknown job (pinned in Core.Tests); these tests pin
/// the rendering half — that the shell actually shows the reason tree, in the inspector, on the canvas
/// node itself, and in the trigger verdict.
/// </summary>
public sealed class UnknownAlwaysExplainedTests : IDisposable
{
    private readonly ComponentHarness _harness = new();

    [Fact]
    public void Inspector_EveryUnknownJobRendersAReason_AndNeverTheBugPlaceholder()
    {
        _harness.LoadExample("unsafe-demo.yml");
        var cut = _harness.Context.Render<InspectorPanel>();

        var unknownRows = cut.FindAll(".al-job--unknown");
        Assert.NotEmpty(unknownRows);

        foreach (var row in unknownRows)
        {
            // The distinction is said out loud, and at least one concrete reason is rendered.
            Assert.Contains("Unknown, not skipped.", row.TextContent, StringComparison.Ordinal);
            var messages = row.QuerySelectorAll(".al-reason-message");
            Assert.NotEmpty(messages);
            Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message.TextContent)));

            // The placeholder this project reserves for 'the engine gave unknown without a reason'.
            Assert.DoesNotContain("is an ActionLens bug", row.TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GraphCanvas_UnknownNode_WordsItsState_InsteadOfPaintingOnlyAGlyph()
    {
        _harness.LoadExample("unsafe-demo.yml");
        var cut = _harness.Context.Render<GraphCanvas>(parameters =>
            parameters.Add(p => p.Report, _harness.Session.Report!));

        // 'deploy' is the unknown job in the grid. Its non-colour channels: the word 'unknown' is
        // drawn inside the node, not just the '?' glyph — a glyph alone is the bare marker the
        // promise forbids.
        var unknownState = cut
            .FindAll(".al-graph-node")
            .First(candidate => (candidate.GetAttribute("class") ?? string.Empty)
                .Contains("al-node--unknown", StringComparison.Ordinal));
        string stateText = unknownState.QuerySelector(".al-node-state")!.TextContent;
        Assert.Contains("?", stateText, StringComparison.Ordinal);
        Assert.Contains("unknown", stateText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", unknownState.GetAttribute("aria-label") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryPanel_UnknownTriggerVerdict_SaysWhyInWordsAndInReasons()
    {
        _harness.LoadExample("atlas-ci.yml");

        // Asking atlas-ci about workflow_dispatch without a payload is a question about whether a
        // person pressed a button: the engine answers unknown, and the panel must not render it as a
        // bare '?' — it owes the reader the words and the reason tree.
        _harness.Session.SelectTriggerEvent("workflow_dispatch");
        var cut = _harness.Context.Render<SummaryPanel>();

        string summary = cut.Find(".al-summary").TextContent;
        Assert.Contains("cannot be decided", summary, StringComparison.Ordinal);
        Assert.Contains("not the same", summary, StringComparison.Ordinal);

        var reasons = cut.FindAll(".al-reason-list .al-reason-message");
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.False(string.IsNullOrWhiteSpace(reason.TextContent)));
    }

    public void Dispose() => _harness.Dispose();
}
