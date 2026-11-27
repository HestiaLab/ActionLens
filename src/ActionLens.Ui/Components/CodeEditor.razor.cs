using ActionLens.Core.Text;
using ActionLens.Ui.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ActionLens.Ui.Components;

/// <summary>
/// One CodeMirror 6 surface, driven from .NET through <see cref="IEditorModule"/>.
/// </summary>
/// <remarks>
/// The component is a view of text the session owns: it reports edits outward and only writes into the
/// editor when a value arrives that it did not just send out. That direction is what keeps a debounced
/// analysis re-render from fighting the caret.
/// <para>
/// When the bundle cannot be imported the component renders a <c>&lt;textarea&gt;</c> instead. That is the
/// documented degraded path — it is never the shipped primary surface, and the status strip says when it
/// is in use, because a silently missing editor is worse than a visible one.
/// </para>
/// </remarks>
public partial class CodeEditor : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IEditorModule Module { get; set; } = default!;

    [Inject]
    private EditorSurfaceMonitor Surfaces { get; set; } = default!;

    /// <summary>The text the editor should be showing.</summary>
    [Parameter, EditorRequired]
    public string Text { get; set; } = string.Empty;

    /// <summary><c>yaml</c> or <c>json</c>; anything else leaves syntax highlighting off.</summary>
    [Parameter]
    public string Language { get; set; } = "yaml";

    /// <summary>Accessible name for the editable region.</summary>
    [Parameter]
    public string AriaLabel { get; set; } = "Editor";

    /// <summary>Which editor this is, for the status strip's degraded message.</summary>
    [Parameter]
    public string SurfaceLabel { get; set; } = "editor";

    /// <summary>Raised with the new text whenever the reader edits.</summary>
    [Parameter]
    public EventCallback<string> OnTextChanged { get; set; }

    private ElementReference _host;
    private string _text = string.Empty;
    private EditorSurface _surface = EditorSurface.Loading;
    private EditorSurface _reported = EditorSurface.Loading;
    private DotNetObjectReference<EditorCallback>? _callback;
    private bool _attached;
    private bool _disposed;
    private int _attachGeneration;
    private int _replaceToken;

    /// <summary>The surface actually on screen; the markup switches on it.</summary>
    private EditorSurface Surface => _surface;

    /// <inheritdoc />
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        string previous = _text;
        await base.SetParametersAsync(parameters).ConfigureAwait(false);

        if (string.Equals(previous, Text, StringComparison.Ordinal))
        {
            return;
        }

        // A value we did not just report outward: adopt it and hand it to whichever surface is showing.
        _text = Text;
        _replaceToken++;
        await PushTextAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _attached || _disposed)
        {
            return;
        }

        await AttachAsync().ConfigureAwait(false);
    }

    private async Task AttachAsync()
    {
        int generation = ++_attachGeneration;
        IJSObjectReference? module = await Module.GetAsync().ConfigureAwait(false);
        if (_disposed || generation != _attachGeneration)
        {
            return;
        }

        if (module is null)
        {
            Degrade(Module.FailureReason);
            return;
        }

        _callback = DotNetObjectReference.Create(new EditorCallback(this));
        try
        {
            bool attached = await module.InvokeAsync<bool>(
                "attach",
                _host,
                new EditorOptions(_text, Language, AriaLabel),
                _callback).ConfigureAwait(false);

            if (_disposed || generation != _attachGeneration)
            {
                await DiscardLateAttachmentAsync(module).ConfigureAwait(false);
                return;
            }

            if (!attached)
            {
                Degrade("CodeMirror refused to start in this panel.");
                return;
            }

            _attached = true;
            SetSurface(EditorSurface.CodeMirror, null);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
        {
            if (_disposed || generation != _attachGeneration)
            {
                await DiscardLateAttachmentAsync(module).ConfigureAwait(false);
                return;
            }

            Degrade(exception.Message);
        }
    }

    private async Task DiscardLateAttachmentAsync(IJSObjectReference module)
    {
        try
        {
            await module.InvokeVoidAsync("dispose", _host).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
        {
            // The late attach may have failed before creating a view.
        }

        _callback?.Dispose();
        _callback = null;
    }

    private async Task PushTextAsync()
    {
        if (!_attached || _surface != EditorSurface.CodeMirror)
        {
            return;
        }

        IJSObjectReference? module = await Module.GetAsync().ConfigureAwait(false);
        if (module is null)
        {
            return;
        }

        try
        {
            await module.InvokeVoidAsync("setText", _host, _text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
        {
            Degrade(exception.Message);
        }
    }

    /// <summary>
    /// Moves this editor to a finding's 1-based range.
    /// </summary>
    /// <returns>True when the caret actually moved; false in the text-area fallback, where the caller
    /// announces the position as text instead.</returns>
    public async Task<EditorRevealResult?> RevealAsync(SourceRange range, string severity)
    {
        if (_disposed || !_attached || _surface != EditorSurface.CodeMirror || !range.IsKnown)
        {
            return null;
        }

        IJSObjectReference? module = await Module.GetAsync().ConfigureAwait(false);
        if (module is null)
        {
            return null;
        }

        try
        {
            return await module.InvokeAsync<EditorRevealResult?>(
                "reveal",
                _host,
                range.Start.Line,
                range.Start.Column,
                range.End.Line,
                range.End.Column,
                severity).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Gives the editor focus, used when a reader switches to a tab that is already open.</summary>
    public async Task<bool> FocusAsync()
    {
        if (_disposed || !_attached || _surface != EditorSurface.CodeMirror)
        {
            return false;
        }

        IJSObjectReference? module = await Module.GetAsync().ConfigureAwait(false);
        if (module is null)
        {
            return false;
        }

        try
        {
            return await module.InvokeAsync<bool>("focus", _host).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void OnFallbackInput(ChangeEventArgs args)
    {
        _ = NotifyOwnerAsync(args.Value?.ToString() ?? string.Empty);
    }

    private async Task NotifyOwnerAsync(string text)
    {
        if (_disposed)
        {
            return;
        }

        // Keep our own snapshot in step with what the editor now shows. SetParametersAsync compares
        // Text against _text to tell an external replacement from the parent echoing our own report;
        // without this, every keystroke looks like a new external value and is pushed back through
        // setText, which replaces the whole document, resets the caret and flattens the undo history.
        _text = text;

        try
        {
            await OnTextChanged.InvokeAsync(text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The panel is gone; the session has the text already, so nothing is lost.
        }
    }

    private void Degrade(string? reason)
    {
        SetSurface(EditorSurface.TextareaFallback, reason ?? Module.FailureReason);
    }

    private void SetSurface(EditorSurface surface, string? reason)
    {
        if (_reported == surface)
        {
            return;
        }

        if (_reported != EditorSurface.Loading)
        {
            Surfaces.Forget(_reported);
        }

        _reported = surface;
        Surfaces.Report(surface, SurfaceLabel, reason);
        _surface = surface;
        if (!_disposed)
        {
            InvokeAsync(StateHasChanged);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _attachGeneration++;

        if (_attached)
        {
            _attached = false;
            IJSObjectReference? module = await Module.GetAsync().ConfigureAwait(false);
            if (module is not null)
            {
                try
                {
                    await module.InvokeVoidAsync("dispose", _host).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
                {
                    // The page or the module is already gone.
                }
            }
        }

        _callback?.Dispose();
        _callback = null;

        if (_reported != EditorSurface.Loading)
        {
            Surfaces.Forget(_reported);
            _reported = EditorSurface.Loading;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The options object the JS module expects (serialised camelCase by the interop layer).</summary>
    private sealed record EditorOptions(string Doc, string Language, string AriaLabel);

    /// <summary>
    /// The JS-callable side of the editor. Kept as a separate small object so the component's own
    /// [JSInvokable] surface stays one method and the reference is trivially disposable.
    /// </summary>
    private sealed class EditorCallback(CodeEditor owner)
    {
        [JSInvokable]
        public Task OnTextChanged(string text) => owner.NotifyOwnerAsync(text);
    }
}
