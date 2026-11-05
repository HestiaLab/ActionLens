using ActionLens.Ui.Components;
using ActionLens.Ui.Services;
using ActionLens.Ui.Tests.Supporting;
using Bunit;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The boot-crash class of defect: the embed glob lives in MSBuild, an empty wildcard match is legal,
/// and the first thing that then fails is <c>Workflows[0]</c> inside the component — only visible as an
/// unhandled exception in a browser console. These tests run against the ActionLens.Ui assembly's own
/// manifest, so an embed glob that matches nothing fails here rather than on first paint.
/// </summary>
public sealed class EmbeddedExampleTests
{
    [Fact]
    public void UiAssembly_EmbedsAllThreeWorkflowFixtures_ByExactName()
    {
        Assert.Equal(
            ["atlas-ci.yml", "matrix-build.yml", "unsafe-demo.yml"],
            WorkflowExamples.Workflows.Select(example => example.FileName).ToArray());
        Assert.All(WorkflowExamples.Workflows, example => Assert.False(string.IsNullOrWhiteSpace(example.Yaml)));
        Assert.Equal("atlas-ci.yml", WorkflowExamples.DefaultWorkflow.FileName);
    }

    [Fact]
    public void UiAssembly_EmbedsAllFourEventPayloads_AndPairsThemWithTheirEvents()
    {
        Assert.Equal(
            ["pull-request.json", "push.json", "schedule.json", "workflow-dispatch.json"],
            WorkflowExamples.EventPayloads.Select(payload => payload.FileName).ToArray());
        Assert.All(WorkflowExamples.EventPayloads, payload => Assert.False(string.IsNullOrWhiteSpace(payload.Json)));

        Assert.Equal("push.json", WorkflowExamples.PayloadFor("push")!.FileName);
        Assert.Equal("pull-request.json", WorkflowExamples.PayloadFor("pull_request")!.FileName);
        Assert.Equal("schedule.json", WorkflowExamples.PayloadFor("schedule")!.FileName);
        Assert.Equal("workflow-dispatch.json", WorkflowExamples.PayloadFor("workflow_dispatch")!.FileName);
    }

    [Fact]
    public void FirstPaint_LoadsTheDefaultExampleWithoutTouchingTheNetwork()
    {
        // The exact call ActionLensWorkspace makes in OnInitialized; before the glob fix it threw
        // IndexOutOfRange out of an empty Workflows list.
        using AnalysisSession session = new();
        session.LoadInitialExample();

        Assert.NotNull(session.Report);
        Assert.Equal("atlas-ci.yml", session.FileName);
        Assert.False(string.IsNullOrWhiteSpace(session.PayloadText));
        Assert.Equal("CI", session.Report!.Workflow.Name);
    }

    [Fact]
    public void TopBar_OffersEveryBundledWorkflowInTheExamplePicker()
    {
        using ComponentHarness harness = new();
        harness.Session.LoadInitialExample();

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();
        var options = cut.Find("#al-example").QuerySelectorAll("option");
        Assert.Equal(3, options.Length);
        Assert.Contains("matrix-build.yml", options[1].GetAttribute("value"));

        // The bound value is the file the session actually analysed — the picker and the state agree
        // from the first paint, which is the part no browser-independent test could check before.
        Assert.Equal("atlas-ci.yml", cut.Find("#al-example").GetAttribute("value"));
    }
}
