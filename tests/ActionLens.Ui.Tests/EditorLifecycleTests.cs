using System.Reflection;
using ActionLens.Ui.Components;
using ActionLens.Ui.Services;
using Microsoft.JSInterop;

namespace ActionLens.Ui.Tests;

public sealed class EditorLifecycleTests
{
    [Fact]
    public async Task AttachCompletingAfterDispose_IsDiscardedAndNeverReportedAsLoadedAsync()
    {
        var attach = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new PendingEditorModule(attach.Task);
        var surfaces = new EditorSurfaceMonitor();
        var editor = new CodeEditor();
        SetService(editor, "Module", module);
        SetService(editor, "Surfaces", surfaces);

        var attachTask = (Task)typeof(CodeEditor)
            .GetMethod("AttachAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null)!;

        await module.AttachStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        await editor.DisposeAsync();
        Assert.True((bool)typeof(CodeEditor).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!);
        attach.SetResult(true);
        await attachTask.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.True((bool)typeof(CodeEditor).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!);
        Assert.False((bool)typeof(CodeEditor).GetField("_attached", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!);
        Assert.Equal(1, module.DisposeCount);
        Assert.True(module.Disposed.Task.IsCompleted, module.Calls);
        Assert.Equal(0, surfaces.CodeMirrorCount);
        Assert.Equal(0, surfaces.FallbackCount);
    }

    private static void SetService<T>(CodeEditor editor, string name, T value) =>
        typeof(CodeEditor).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);

    private sealed class PendingEditorModule : IEditorModule, IAsyncDisposable
    {
        private readonly PendingEditorJs _js;

        public string? FailureReason => null;

        public bool IsLoaded => true;

        public TaskCompletionSource AttachStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => _js.DisposeCount;

        public string Calls => _js.Calls;

        public PendingEditorModule(Task<bool> attach)
        {
            _js = new PendingEditorJs(attach, AttachStarted, Disposed);
        }

        public Task<IJSObjectReference?> GetAsync() => Task.FromResult<IJSObjectReference?>(_js);

        public ValueTask DisposeAsync() => _js.DisposeAsync();
    }

    private sealed class PendingEditorJs(
        Task<bool> attach,
        TaskCompletionSource attachStarted,
        TaskCompletionSource disposed) : IJSObjectReference
    {
        public int DisposeCount { get; private set; }

        public string Calls { get; private set; } = string.Empty;

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls += identifier + ";";
            if (string.Equals(identifier, "attach", StringComparison.Ordinal))
            {
                attachStarted.TrySetResult();
                return (TValue)(object)await attach.ConfigureAwait(false);
            }

            RecordDispose(identifier);
            return default!;
        }

        ValueTask<TValue> IJSObjectReference.InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask InvokeVoidAsync(string identifier, object?[]? args)
        {
            Calls += identifier + ";";
            RecordDispose(identifier);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void RecordDispose(string identifier)
        {
            if (string.Equals(identifier, "dispose", StringComparison.Ordinal))
            {
                DisposeCount++;
                disposed.TrySetResult();
            }
        }
    }
}
