namespace ActionLens.Ui.Services;

/// <summary>The surface an editor is currently showing.</summary>
public enum EditorSurface
{
    /// <summary>Waiting for the CodeMirror module to load.</summary>
    Loading,

    /// <summary>CodeMirror 6 is running in this panel.</summary>
    CodeMirror,

    /// <summary>CodeMirror could not be loaded; the documented <c>&lt;textarea&gt;</c> fallback is showing.</summary>
    TextareaFallback,
}

/// <summary>
/// Which editor surfaces the app actually got, so the status strip can say "CodeMirror is running" or
/// "degraded" instead of the reader having to work it out from a blank panel.
/// </summary>
/// <remarks>
/// The fallback is a documented degraded path, not a shipped second editor: it exists because the bundle
/// is a static asset, and a missing asset must produce a usable text area and an explicit warning rather
/// than an empty region.
/// </remarks>
public sealed class EditorSurfaceMonitor
{
    private int _codeMirrorCount;
    private int _fallbackCount;

    /// <summary>Raised when a surface changes state.</summary>
    public event Action? Changed;

    /// <summary>How many editors are running CodeMirror right now.</summary>
    public int CodeMirrorCount => _codeMirrorCount;

    /// <summary>How many editors fell back to a text area.</summary>
    public int FallbackCount => _fallbackCount;

    /// <summary>True when at least one editor has CodeMirror and none has fallen back.</summary>
    public bool IsFullyLoaded => _fallbackCount == 0 && _codeMirrorCount > 0;

    /// <summary>The first failure reason recorded, kept for the status strip and for bug reports.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Records the surface one editor ended up on.</summary>
    /// <param name="surface">The new state of that editor.</param>
    /// <param name="label">Which editor it belongs to, for the failure message.</param>
    /// <param name="reason">Why it degraded, when it degraded.</param>
    public void Report(EditorSurface surface, string label, string? reason = null)
    {
        switch (surface)
        {
            case EditorSurface.CodeMirror:
                _codeMirrorCount++;
                break;
            case EditorSurface.TextareaFallback:
                _fallbackCount++;
                LastFailure ??= string.IsNullOrEmpty(reason) ? $"{label}: editor module unavailable." : $"{label}: {reason}";
                break;
            default:
                return;
        }

        Changed?.Invoke();
    }

    /// <summary>Reverses <see cref="Report"/> when an editor is disposed.</summary>
    public void Forget(EditorSurface surface)
    {
        switch (surface)
        {
            case EditorSurface.CodeMirror when _codeMirrorCount > 0:
                _codeMirrorCount--;
                break;
            case EditorSurface.TextareaFallback when _fallbackCount > 0:
                _fallbackCount--;
                break;
            default:
                return;
        }

        Changed?.Invoke();
    }
}
