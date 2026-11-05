using ActionLens.Core.Scenarios;
using ActionLens.Ui.Components;
using ActionLens.Ui.Services;
using ActionLens.Ui.Tests.Supporting;
using Atlas.Blazor.Components;
using Atlas.Core;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The shell subscribes to <see cref="AnalysisSession.Changed"/> only to keep the two editors'
/// <c>Text</c> parameters current. Every other notification — pending flag, finished analysis,
/// selection — belongs to the panels, which subscribe on their own. docs/review-v1-grill.md §13.2
/// records the bug this subscription fixed; this test records the cost of keeping it: a full Atlas
/// re-render on every keystroke. <c>RenderCount</c> is the only honest way to observe that, because
/// Blazor reuses DOM nodes and a browser-side DOM-identity check cannot see a re-render at all.
/// </summary>
public sealed class WorkspaceRenderTests
{
    [Fact]
    public void OnlyAChangedDocumentCausesTheShellToReRender()
    {
        using ComponentHarness harness = new();
        harness.Context.Services.AddSingleton<IAtlasWorkspaceFactory>(new AtlasWorkspaceFactory());
        // AtlasWorkspaceHost needs Atlas's content catalog and JS runtime, neither of which belongs to
        // this test. Stubbing the host leaves ActionLensWorkspace's own render tree — including the
        // Session.Changed subscription — intact, which is exactly what RenderCount measures.
        harness.Context.ComponentFactories.AddStub<AtlasWorkspaceHost>();

        IRenderedComponent<ActionLensWorkspace> cut = harness.Context.Render<ActionLensWorkspace>();
        var baseline = cut.RenderCount;

        // A selection change and a scenario change both raise Changed, without touching either
        // editor's text. Neither should re-render the shell.
        harness.Session.SelectJob("build");
        harness.Session.SelectScenario(Scenario.Default.WithJobFailed("build"));
        Assert.Equal(baseline, cut.RenderCount);

        // Calling ReplaceText directly is an *external* text change (the editor-reported path goes
        // through OnWorkflowTextChanged, which pre-synchronises the snapshot). The shell must refresh
        // the editors' Text parameters here, so a re-render is correct rather than wasteful.
        harness.Session.ReplaceText(EditorTarget.Workflow, harness.Session.WorkflowText + "\n# typed");
        Assert.True(cut.RenderCount > baseline, "An external text replacement must refresh the editors.");

        var afterReplace = cut.RenderCount;

        // An external example load likewise changes what the render tree must pass down.
        harness.LoadExample("matrix-build.yml");
        Assert.True(cut.RenderCount > afterReplace,
            "Loading a different example must refresh the editors' Text parameters.");
    }
}
