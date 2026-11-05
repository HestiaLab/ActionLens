using ActionLens.Core.Diagnostics;
using ActionLens.Ui.Services;
using ActionLens.Ui.Workspace;
using Atlas.Blazor.Components;
using Atlas.Core;
using Atlas.Core.Changes;
using Atlas.Core.Operations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ActionLens.Ui.Components;

/// <summary>
/// The Atlas shell: it owns the workspace, routes content kinds to ActionLens panels, and is the only
/// place that knows both the editors and the diagnostics list — which is what lets a clicked finding move
/// a caret.
/// </summary>
/// <remarks>
/// Programmatic mode, because the jump needs <see cref="IAtlasWorkspace.ExecuteAsync"/> on a workspace the
/// app holds (<c>WorkspaceOwnership.External</c>), and Atlas only lets the app dispose it afterwards.
/// <para>
/// The editor components live inside Atlas content routes, so Atlas may unload them when their tab is not
/// selected. A jump therefore activates the tab first and then retries until the component has actually
/// mounted and attached — the panel is data-bound to the session, so nothing is lost in the wait.
/// </para>
/// </remarks>
public partial class ActionLensWorkspace : ComponentBase, IAsyncDisposable
{
    /// <summary>The workspace id Atlas keys the layout under. Renaming it discards any stored layout.</summary>
    public const string WorkspaceIdValue = "actionlens-workspace";

    /// <summary>How long a jump waits for the target editor to mount before it gives up and says so.</summary>
    private const int JumpAttempts = 20;

    [Inject]
    private IAtlasWorkspaceFactory Factory { get; set; } = default!;

    [Inject]
    private AnalysisSession Session { get; set; } = default!;

    private readonly AtlasKeyboardCommandMap _keyboardCommands = AtlasKeyboardCommandMap.Default;
    private readonly AtlasSplitterOptions _splitterOptions = new(16d, 350);

    private IAtlasWorkspace? _workspace;
    private string? _initializationError;
    private long _revision;
    private string _announcement = string.Empty;
    private CodeEditor? _workflowEditor;
    private CodeEditor? _payloadEditor;
    private DiagnosticsPanel? _diagnosticsPanel;
    private bool _disposed;
    private long _lastJumpToken;

    /// <summary>
    /// The two session values this component's render tree actually reads. Every other panel subscribes
    /// to <see cref="AnalysisSession.Changed"/> itself, so re-rendering the whole Atlas shell on every
    /// notification — pending flag, analysis result, selection — was wasted work on each keystroke.
    /// </summary>
    private string _renderedWorkflowText = string.Empty;
    private string _renderedPayloadText = string.Empty;

    protected override void OnInitialized()
    {
        Session.JumpRequested += OnJumpRequested;
        // Load the initial example before subscribing to Changed: that load raises the event itself,
        // and at this point there is no previous document to compare against. Subscribing afterwards
        // avoids a queued re-render during the component's own first paint.
        Session.LoadInitialExample();
        _renderedWorkflowText = Session.WorkflowText;
        _renderedPayloadText = Session.PayloadText;
        Session.Changed += OnSessionChanged;
        CreateWorkspace();
    }

    /// <summary>
    /// Only re-render when an external change replaced the document text. The panels own their own
    /// subscriptions, and a notification caused by a pending flag or a finished analysis changes
    /// nothing in this component's markup.
    /// </summary>
    private void OnSessionChanged()
    {
        if (_disposed)
        {
            return;
        }

        bool documentChanged = !string.Equals(_renderedWorkflowText, Session.WorkflowText, StringComparison.Ordinal)
            || !string.Equals(_renderedPayloadText, Session.PayloadText, StringComparison.Ordinal);
        if (!documentChanged)
        {
            return;
        }

        _renderedWorkflowText = Session.WorkflowText;
        _renderedPayloadText = Session.PayloadText;
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Creates (or recreates) the Atlas workspace from the definition. A failure here is recorded as text
    /// and rendered as an alert rather than thrown, because a shell that cannot start must still tell the
    /// reader what it is holding.
    /// </summary>
    private void CreateWorkspace()
    {
        _initializationError = null;

        var result = Factory.Create(
            ActionLensWorkspaceDefinition.Create(WorkspaceIdValue));

        if (!result.Succeeded || result.Value is null)
        {
            _workspace = null;
            _revision = 0;
            _initializationError = result.Error?.DiagnosticId ?? "ATLAS-WORKSPACE-INIT-FAILED";
            return;
        }

        _workspace = result.Value;
        _workspace.Changed += OnWorkspaceChanged;
        _revision = _workspace.Current.Revision;
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs args)
    {
        _revision = args.ChangeSet.AfterRevision;
        if (!_disposed)
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }

    // The editor already holds the text it just reported. Record that before the session raises
    // Changed, so the notification is not mistaken for an external replacement and does not re-render
    // the whole shell (which would push the same value straight back into the editor).
    private void OnWorkflowTextChanged(string text)
    {
        _renderedWorkflowText = text;
        Session.ReplaceText(EditorTarget.Workflow, text);
    }

    private void OnPayloadTextChanged(string text)
    {
        _renderedPayloadText = text;
        Session.ReplaceText(EditorTarget.Payload, text);
    }

    private async Task RetryAsync()
    {
        await DisposeWorkspaceAsync().ConfigureAwait(false);
        CreateWorkspace();
        StateHasChanged();
    }

    /// <summary>Atlas reporting that it could not do something the shell asked; surfaced, never swallowed.</summary>
    private void OnHostError(AtlasHostError error)
    {
        _announcement = $"Atlas reported {error.Code}.";
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnJumpRequested(DiagnosticJump jump) => _ = HandleJumpAsync(jump);

    /// <summary>
    /// The highest-value interaction in the product: a clicked finding moves the editor to its line.
    /// </summary>
    /// <remarks>
    /// The tab is activated first because Atlas unloads inactive content, so the editor the range belongs
    /// to may not exist yet. The loop then waits for the component to mount and for CodeMirror to attach,
    /// and reports honestly when it could not move instead of silently doing nothing.
    /// </remarks>
    private async Task HandleJumpAsync(DiagnosticJump jump)
    {
        // Two quick clicks used to run two polling loops that both wrote the announcement, so the older
        // one could describe a jump that no longer matched what the editor showed. The token makes the
        // newest request the only writer, and also stops a loop that outlives the component.
        if (jump.Token < _lastJumpToken)
        {
            return;
        }

        _lastJumpToken = jump.Token;
        string severity = ReportPresentation.SeverityToken(jump.Diagnostic?.Severity ?? DiagnosticSeverity.Info);

        if (!jump.Range.IsKnown)
        {
            _announcement = Describe(jump, moved: false);
            await InvokeAsync(StateHasChanged);
            return;
        }

        await ActivateEditorAsync(jump.Target).ConfigureAwait(false);

        for (int attempt = 0; attempt < JumpAttempts; attempt++)
        {
            if (Superseded(jump))
            {
                return;
            }

            CodeEditor? editor = jump.Target == EditorTarget.Workflow ? _workflowEditor : _payloadEditor;
            if (editor is not null && await editor.RevealAsync(jump.Range, severity).ConfigureAwait(false))
            {
                if (Superseded(jump))
                {
                    return;
                }

                _announcement = Describe(jump, moved: true);
                await InvokeAsync(StateHasChanged);
                return;
            }

            await Task.Delay(60).ConfigureAwait(false);
        }

        if (Superseded(jump))
        {
            return;
        }

        _announcement = jump.Range.IsKnown
            ? $"{Describe(jump, moved: false)} The editor surface did not become ready; the text area fallback cannot move the caret."
            : Describe(jump, moved: false);
        await InvokeAsync(StateHasChanged);
    }

    private bool Superseded(DiagnosticJump jump) => _disposed || jump.Token != _lastJumpToken;

    private async Task ActivateEditorAsync(EditorTarget target)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        string itemId = target == EditorTarget.Workflow
            ? ActionLensItemIds.Workflow
            : ActionLensItemIds.Payload;

        try
        {
            await workspace.ExecuteAsync(new SelectItemOperation(new DockItemId(itemId)), CancellationToken.None);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _announcement = "The workspace is gone, so the editor cannot be opened.";
        }
    }

    /// <summary>
    /// ActionLens' own chords, kept clear of Atlas' bindings (Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W, Alt+arrows).
    /// </summary>
    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (!args.CtrlKey || !args.AltKey || args.ShiftKey || args.Repeat)
        {
            return;
        }

        switch (args.Key)
        {
            case "1":
                _ = JumpToDocumentAsync(EditorTarget.Workflow, "Workflow editor");
                break;
            case "2":
                _ = JumpToDocumentAsync(EditorTarget.Payload, "Event payload editor");
                break;
            case "3":
                _ = FocusDiagnosticsAsync();
                break;
            default:
                return;
        }
    }

    private async Task JumpToDocumentAsync(EditorTarget target, string label)
    {
        await ActivateEditorAsync(target).ConfigureAwait(false);
        CodeEditor? editor = target == EditorTarget.Workflow ? _workflowEditor : _payloadEditor;
        bool focused = editor is not null && await editor.FocusAsync().ConfigureAwait(false);
        _announcement = focused ? $"{label} focused." : $"{label} opened.";
        await InvokeAsync(StateHasChanged);
    }

    private async Task FocusDiagnosticsAsync()
    {
        if (_workspace is { } workspace)
        {
            try
            {
                await workspace.ExecuteAsync(
                    new SelectItemOperation(new DockItemId(ActionLensItemIds.Diagnostics)),
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The panel is already where it is; focus below is what the reader asked for.
            }
        }

        await Task.Delay(60).ConfigureAwait(false);
        bool focused = _diagnosticsPanel is { } panel && await panel.FocusFirstAsync().ConfigureAwait(false);
        _announcement = focused ? "Diagnostics list focused." : "Diagnostics list has no findings to focus.";
        await InvokeAsync(StateHasChanged);
    }

    private static string Describe(DiagnosticJump jump, bool moved)
    {
        if (jump.Diagnostic is not { } finding)
        {
            return moved
                ? $"Moved the editor to line {jump.Range.Start.Line}, column {jump.Range.Start.Column}."
                : $"Could not move the editor to line {jump.Range.Start.Line}, column {jump.Range.Start.Column}.";
        }

        string prefix = moved
            ? $"{ReportPresentation.SeverityLabel(finding.Severity)} {finding.Code}: editor moved to line {jump.Range.Start.Line}, column {jump.Range.Start.Column}."
            : $"{ReportPresentation.SeverityLabel(finding.Severity)} {finding.Code}: the editor could not be moved to line {jump.Range.Start.Line}, column {jump.Range.Start.Column}.";
        return prefix;
    }

    private async Task DisposeWorkspaceAsync()
    {
        IAtlasWorkspace? workspace = _workspace;
        _workspace = null;
        if (workspace is null)
        {
            return;
        }

        workspace.Changed -= OnWorkspaceChanged;
        try
        {
            await workspace.DisposeAsync();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // Already released.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session.JumpRequested -= OnJumpRequested;
        Session.Changed -= OnSessionChanged;
        await DisposeWorkspaceAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
