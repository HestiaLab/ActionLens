using ActionLens.Ui.Services;
using Microsoft.JSInterop;

namespace ActionLens.App.Services;

/// <summary>
/// Loads the CodeMirror bundle as a JS module, once, and remembers whether it could be loaded at all.
/// This is the host half of the <see cref="IEditorModule"/> seam the Ui components inject; bUnit tests
/// substitute a stub because the JS bundle cannot load outside a browser.
/// </summary>
/// <remarks>
/// The module is <c>wwwroot/js/dist/actionlens-editor.js</c>, produced by
/// <c>npm run build:editor</c> from <c>ClientScripts/editor.ts</c>; the App project's build fails if the
/// bundle is missing, so a 404 here means a deployment problem rather than a developer machine without
/// Node. Components ask for the module and fall back to a text area when this returns null, which keeps
/// the degraded path visible instead of blank.
/// </remarks>
public sealed class EditorModule(IJSRuntime runtime) : IEditorModule, IAsyncDisposable
{
    /// <summary>Path the browser resolves against the app's base href.</summary>
    public const string ModulePath = "./js/dist/actionlens-editor.js";

    private IJSObjectReference? _module;
    private Task<IJSObjectReference?>? _loading;
    private bool _disposed;

    /// <summary>Why the module could not be loaded, when it could not. Shown in the status strip.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>True once the module has been imported successfully.</summary>
    public bool IsLoaded => _module is not null;

    /// <summary>
    /// The module reference, or null when it is unavailable. Concurrent callers share one import.
    /// </summary>
    public async Task<IJSObjectReference?> GetAsync()
    {
        // After disposal the cached import task may still hold a reference we have already released.
        // Handing that out is what let a component call into a dead JS object and skip editor.ts's
        // dispose(), leaving its ResizeObserver behind.
        if (_disposed)
        {
            return null;
        }

        if (_module is not null)
        {
            return _module;
        }

        if (FailureReason is not null)
        {
            return null;
        }

        Task<IJSObjectReference?>? loading = _loading ??= ImportAsync();
        try
        {
            return await loading.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
            // ImportAsync records the reason; this only keeps a caller from seeing a raw exception.
            return null;
        }
    }

    private async Task<IJSObjectReference?> ImportAsync()
    {
        try
        {
            IJSObjectReference reference = await runtime
                .InvokeAsync<IJSObjectReference>("import", ModulePath)
                .ConfigureAwait(false);

            // Confirm the bundle is the one this build expects before any component trusts it.
            bool available = await reference.InvokeAsync<bool>("isAvailable").ConfigureAwait(false);
            if (!available || _disposed)
            {
                FailureReason = _disposed
                    ? "The editor module was disposed while it was loading."
                    : $"{ModulePath} loaded but did not report itself available.";
                await reference.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            _module = reference;
            return reference;
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
            FailureReason = exception.Message;
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IJSObjectReference? module = _module;
        _module = null;
        _loading = null;
        if (module is null)
        {
            return;
        }

        try
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or ObjectDisposedException or InvalidOperationException)
        {
            // The page is going away; a reference we could not release dies with it.
        }
    }
}
