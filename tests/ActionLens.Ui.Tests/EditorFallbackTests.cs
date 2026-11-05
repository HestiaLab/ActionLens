using ActionLens.Ui.Components;
using ActionLens.Ui.Tests.Supporting;
using Bunit;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The JS bundle cannot load under bUnit — this project therefore doubles as the proof that the
/// <c>IEditorModule</c> seam is the real degraded path and not a test-only fiction: with no
/// module, the editor shows the documented textarea plus warning, and the status strip says so.
/// </summary>
public sealed class EditorFallbackTests
{
    [Fact]
    public void CodeEditor_WithoutTheJsBundle_RendersTheDocumentedTextareaFallback()
    {
        using ComponentHarness harness = new();
        IRenderedComponent<CodeEditor> cut = harness.Context.Render<CodeEditor>(parameters => parameters
            .Add(p => p.Text, "name: demo\non:\n  push:\njobs:\n  build:\n    runs-on: ubuntu-latest\n")
            .Add(p => p.Language, "yaml")
            .Add(p => p.AriaLabel, "Workflow YAML")
            .Add(p => p.SurfaceLabel, "workflow editor"));

        // The first render asks for the module; the stub says no; the component re-renders into the
        // fallback. WaitForState lets the after-render pass land before we assert on it.
        cut.WaitForState(() => cut.Find("textarea") is not null);

        var textarea = cut.Find("textarea");
        Assert.Equal("Workflow YAML", textarea.GetAttribute("aria-label"));
        Assert.Contains("Degraded editor", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, harness.Surfaces.FallbackCount);
        Assert.Equal(0, harness.Surfaces.CodeMirrorCount);
    }

    [Fact]
    public void StatusStrip_SaysOutLoud_WhenAnEditorHasDegraded()
    {
        using ComponentHarness harness = new();
        var cut = harness.Context.Render(builder =>
        {
            builder.OpenComponent<CodeEditor>(0);
            builder.AddComponentParameter(1, "Text", "name: demo\n");
            builder.AddComponentParameter(2, "SurfaceLabel", "workflow editor");
            builder.CloseComponent();
            builder.OpenComponent<StatusStrip>(3);
            builder.CloseComponent();
        });

        cut.WaitForState(() => cut.Find(".al-statusbar").TextContent.Contains("textarea fallback", StringComparison.Ordinal));
        Assert.Contains("editor: textarea fallback (1)", cut.Find(".al-statusbar").TextContent, StringComparison.Ordinal);
    }
}
