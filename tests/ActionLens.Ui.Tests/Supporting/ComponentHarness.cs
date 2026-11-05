using ActionLens.Ui.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace ActionLens.Ui.Tests.Supporting;

/// <summary>
/// Stands in for the host's CodeMirror interop. The JS bundle cannot load under bUnit — that is a fact
/// of the harness, not a defect — so every component under test sees the documented degraded path:
/// <see cref="GetAsync"/> returns null and the components take their textarea/announce fallbacks.
/// Keyboard selection still works because it is a Blazor event handler, not JS.
/// </summary>
public sealed class StubEditorModule : IEditorModule
{
    public string? FailureReason => "The editor bundle cannot load in a bUnit harness.";

    public bool IsLoaded => false;

    public Task<IJSObjectReference?> GetAsync() => Task.FromResult<IJSObjectReference?>(null);
}

/// <summary>
/// One bUnit context plus the services the components inject, wired the way the host wires them.
/// xunit.v3 supplies no bUnit glue (bunit.xunit is preview-only for v3), so the context is built per
/// test and disposed with the test — that is the whole integration.
/// </summary>
public sealed class ComponentHarness : IDisposable
{
    public ComponentHarness()
    {
        Session = new AnalysisSession();
        Surfaces = new EditorSurfaceMonitor();
        Context = new BunitContext();
        Context.Services.AddSingleton(Session);
        Context.Services.AddSingleton(Surfaces);
        Context.Services.AddSingleton<IEditorModule>(new StubEditorModule());
    }

    public BunitContext Context { get; }

    public AnalysisSession Session { get; }

    public EditorSurfaceMonitor Surfaces { get; }

    /// <summary>Loads one bundled example into the session the way the first paint does.</summary>
    public void LoadExample(string fileName, string? triggerEvent = null)
    {
        WorkflowExample workflow = WorkflowExamples.Workflows
            .Single(example => string.Equals(example.FileName, fileName, StringComparison.Ordinal));
        Session.LoadExample(workflow, WorkflowExamples.PayloadFor(triggerEvent), triggerEvent);
    }

    public void Dispose()
    {
        Context.Dispose();
        Session.Dispose();
    }
}
