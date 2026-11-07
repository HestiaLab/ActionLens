using ActionLens.App.Services;
using ActionLens.App.Tests.Supporting;
using Microsoft.JSInterop;

namespace ActionLens.App.Tests;

/// <summary>
/// The branches of EditorModule that the browser suite can only reach by accident: shared import
/// under concurrency, sticky failure, and the disposed-while-loading race that once leaked a
/// ResizeObserver (see the comment in EditorModule.GetAsync).
/// </summary>
public sealed class EditorModuleTests
{
    [Fact]
    public async Task GetAsync_ImportsOnce_AndCachesAsync()
    {
        var reference = new StubJSObjectReference();
        var runtime = new StubJSRuntime(() => Task.FromResult<IJSObjectReference>(reference));
        var module = new EditorModule(runtime);

        Assert.Same(reference, await module.GetAsync());
        Assert.Same(reference, await module.GetAsync());
        Assert.Equal(1, runtime.ImportCount);
        Assert.True(module.IsLoaded);
        Assert.Null(module.FailureReason);
    }

    [Fact]
    public async Task GetAsync_ConcurrentCallers_ShareOneImportAsync()
    {
        var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new StubJSRuntime(() => pending.Task);
        var module = new EditorModule(runtime);

        var first = module.GetAsync();
        var second = module.GetAsync();
        Assert.Equal(1, runtime.ImportCount);

        var reference = new StubJSObjectReference();
        pending.SetResult(reference);
        Assert.Same(reference, await first);
        Assert.Same(reference, await second);
    }

    [Fact]
    public async Task GetAsync_ImportFailure_IsStickyAsync()
    {
        var runtime = new StubJSRuntime(() => Task.FromException<IJSObjectReference>(new JSException("bundle missing")));
        var module = new EditorModule(runtime);

        Assert.Null(await module.GetAsync());
        Assert.Equal("bundle missing", module.FailureReason);
        Assert.False(module.IsLoaded);

        Assert.Null(await module.GetAsync());
        Assert.Equal(1, runtime.ImportCount);
    }

    [Fact]
    public async Task GetAsync_UnavailableModule_IsDisposedAsync()
    {
        var reference = new StubJSObjectReference { Available = false };
        var runtime = new StubJSRuntime(() => Task.FromResult<IJSObjectReference>(reference));
        var module = new EditorModule(runtime);

        Assert.Null(await module.GetAsync());
        Assert.Equal(1, reference.DisposeCount);
        Assert.NotNull(module.FailureReason);
        Assert.False(module.IsLoaded);
    }

    [Fact]
    public async Task GetAsync_DisposedWhileLoading_ReleasesLateReferenceAsync()
    {
        var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new StubJSRuntime(() => pending.Task);
        var module = new EditorModule(runtime);

        var loading = module.GetAsync();
        await module.DisposeAsync();

        var reference = new StubJSObjectReference();
        pending.SetResult(reference);

        Assert.Null(await loading);
        Assert.Equal(1, reference.DisposeCount);
        Assert.NotNull(module.FailureReason);
        Assert.Null(await module.GetAsync());
        Assert.Equal(1, runtime.ImportCount);
    }

    [Fact]
    public async Task GetAsync_AfterDispose_DoesNotImportAsync()
    {
        var runtime = new StubJSRuntime(() => Task.FromResult<IJSObjectReference>(new StubJSObjectReference()));
        var module = new EditorModule(runtime);

        await module.DisposeAsync();

        Assert.Null(await module.GetAsync());
        Assert.Equal(0, runtime.ImportCount);
    }

    [Fact]
    public async Task DisposeAsync_DisposesOnce_AndIsIdempotentAsync()
    {
        var reference = new StubJSObjectReference();
        var runtime = new StubJSRuntime(() => Task.FromResult<IJSObjectReference>(reference));
        var module = new EditorModule(runtime);
        await module.GetAsync();

        await module.DisposeAsync();
        await module.DisposeAsync();

        Assert.Equal(1, reference.DisposeCount);
        Assert.False(module.IsLoaded);
        Assert.Null(await module.GetAsync());
    }

    [Fact]
    public async Task DisposeAsync_SwallowsJsDisposeFailureAsync()
    {
        var reference = new StubJSObjectReference { DisposeException = new JSException("page is going away") };
        var runtime = new StubJSRuntime(() => Task.FromResult<IJSObjectReference>(reference));
        var module = new EditorModule(runtime);
        await module.GetAsync();

        await module.DisposeAsync();

        Assert.Equal(1, reference.DisposeCount);
    }
}
