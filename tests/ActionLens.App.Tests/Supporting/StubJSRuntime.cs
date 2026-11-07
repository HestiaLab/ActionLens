using Microsoft.JSInterop;

namespace ActionLens.App.Tests.Supporting;

/// <summary>
/// An IJSRuntime whose only legitimate call is the module import, answered by a caller-supplied
/// task so a test can hold the import in flight, fail it, or count how often it was attempted.
/// </summary>
internal sealed class StubJSRuntime(Func<Task<IJSObjectReference>> import) : IJSRuntime
{
    public int ImportCount { get; private set; }

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        if (identifier != "import")
        {
            throw new NotSupportedException($"Unexpected runtime call '{identifier}'.");
        }

        ImportCount++;
        return (TValue)(object)await import().ConfigureAwait(false);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);
}

/// <summary>
/// The module the stub runtime hands out: answers the availability probe and counts disposals,
/// optionally failing disposal the way a page going away does.
/// </summary>
internal sealed class StubJSObjectReference : IJSObjectReference
{
    public bool Available { get; init; } = true;

    public Exception? DisposeException { get; init; }

    public int DisposeCount { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        if (identifier != "isAvailable")
        {
            throw new NotSupportedException($"Unexpected module call '{identifier}'.");
        }

        return ValueTask.FromResult((TValue)(object)Available);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return DisposeException is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeException);
    }
}
