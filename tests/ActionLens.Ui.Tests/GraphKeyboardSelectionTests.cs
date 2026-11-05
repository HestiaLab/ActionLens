using ActionLens.Ui.Components;
using ActionLens.Ui.Tests.Supporting;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ActionLens.Ui.Tests;

/// <summary>
/// Keyboard and mouse selection on the graph — the one behaviour the W4 browser pass could not confirm
/// by hand (docs/w4-verification.md), and the reason the Inspector reads its selection from the session
/// rather than from the canvas. Enter and Space must select the job whose node has focus, a click must
/// do the same, and the Inspector must follow in the very same render pass.
/// </summary>
public sealed class GraphKeyboardSelectionTests : IDisposable
{
    private readonly ComponentHarness _harness = new();

    [Fact]
    public async Task EnterOnGraphNode_SelectsTheJobAndTheInspectorFollowsAsync()
    {
        var cut = RenderCanvasAndInspector();
        await cut.Find(".al-graph-node").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        AssertSelectedAndHighlightingFollows(cut, FirstNodeId());
    }

    [Fact]
    public async Task SpaceOnGraphNode_SelectsTheJobAndTheInspectorFollowsAsync()
    {
        var cut = RenderCanvasAndInspector();
        await cut.Find(".al-graph-node").KeyDownAsync(new KeyboardEventArgs { Key = " " });

        AssertSelectedAndHighlightingFollows(cut, FirstNodeId());
    }

    [Fact]
    public async Task ClickOnGraphNode_SelectsTheJobAndTheInspectorFollowsAsync()
    {
        var cut = RenderCanvasAndInspector();
        await cut.Find(".al-graph-node").ClickAsync(new MouseEventArgs());

        AssertSelectedAndHighlightingFollows(cut, FirstNodeId());
    }

    [Fact]
    public async Task ArrowKeyWithoutTheJsHelper_AnnouncesTheFallback_InsteadOfGoingQuietAsync()
    {
        var cut = RenderCanvasAndInspector();

        // The arrow keys move focus through the editor bundle, which cannot load in this harness.
        // Honesty rule: the canvas must say the helper is unavailable rather than swallow the key.
        await cut.Find(".al-graph-node").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });

        string status = cut.Find(".al-visually-hidden[role=status]").TextContent;
        Assert.Contains("keyboard helper is unavailable", status, StringComparison.Ordinal);
        Assert.Null(_harness.Session.SelectedJobId);
    }

    private IRenderedComponent<IComponent> RenderCanvasAndInspector()
    {
        // LoadExample analyses synchronously, so the report exists before the first render.
        _harness.LoadExample("unsafe-demo.yml");
        return _harness.Context.Render(builder =>
        {
            builder.OpenComponent<CanvasPanel>(0);
            builder.CloseComponent();
            builder.OpenComponent<InspectorPanel>(1);
            builder.CloseComponent();
        });
    }

    private string FirstNodeId()
    {
        // The first focusable box of the unsafe-demo grid, taken from the report rather than hardcoded:
        // if the engine reorders, the test follows the same node the keyboard would reach.
        return _harness.Session.Report!.Graph.Nodes[0].JobId;
    }

    private void AssertSelectedAndHighlightingFollows(IRenderedComponent<IComponent> cut, string jobId)
    {
        Assert.Equal(jobId, _harness.Session.SelectedJobId);

        // The canvas announces the selection…
        string selection = cut.Find(".al-graph-selection").TextContent;
        Assert.Contains(jobId, selection, StringComparison.Ordinal);

        // …and the Inspector highlights the same job — the session is the wire between them.
        string inspectorNote = cut.Find(".al-inspector-selection").TextContent;
        Assert.Contains(jobId, inspectorNote, StringComparison.Ordinal);

        var selectedRow = cut
            .FindAll(".al-job")
            .FirstOrDefault(row => row.GetAttribute("class")!.Contains("al-job--selected", StringComparison.Ordinal));
        Assert.NotNull(selectedRow);
        Assert.Equal("true", selectedRow!.GetAttribute("aria-current"));
        Assert.Equal(jobId, selectedRow.QuerySelector(".al-job-id")!.TextContent.Trim());
    }

    public void Dispose() => _harness.Dispose();
}
