using ActionLens.Core.Text;

namespace ActionLens.Core.Workflows;

/// <summary>
/// The <c>on:</c> section. GitHub reads a workflow file as YAML 1.1, where a plain <c>on</c> is the
/// boolean <c>true</c>, so the loader normalises every spelling of that key into this model rather
/// than expecting authors to quote it.
/// </summary>
public sealed record TriggerSection
{
    /// <summary>Events keyed by name, in the order written.</summary>
    public required IReadOnlyDictionary<string, EventTrigger> Events { get; init; }

    /// <summary>True when <c>on:</c> was a bare string or list, e.g. <c>on: push</c>, rather than a mapping.</summary>
    public bool IsShorthand { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;

    public EventTrigger? Find(string eventName) =>
        Events.TryGetValue(eventName, out var trigger) ? trigger : null;
}

/// <summary>One event under <c>on:</c>, with whatever filters the author attached.</summary>
public sealed record EventTrigger
{
    public required string Name { get; init; }

    /// <summary>False when the event was listed without a body, e.g. <c>on: [push, pull_request]</c>.</summary>
    public bool HasConfiguration { get; init; }

    public IReadOnlyList<WorkflowValue> Types { get; init; } = Array.Empty<WorkflowValue>();

    public IReadOnlyList<WorkflowValue> Branches { get; init; } = Array.Empty<WorkflowValue>();

    public IReadOnlyList<WorkflowValue> BranchesIgnore { get; init; } = Array.Empty<WorkflowValue>();

    public IReadOnlyList<WorkflowValue> Tags { get; init; } = Array.Empty<WorkflowValue>();

    public IReadOnlyList<WorkflowValue> TagsIgnore { get; init; } = Array.Empty<WorkflowValue>();

    public IReadOnlyList<WorkflowValue> Paths { get; init; } = Array.Empty<WorkflowValue>();

    public IReadOnlyList<WorkflowValue> PathsIgnore { get; init; } = Array.Empty<WorkflowValue>();

    /// <summary><c>schedule:</c> cron expressions; empty for every other event.</summary>
    public IReadOnlyList<WorkflowValue> Cron { get; init; } = Array.Empty<WorkflowValue>();

    /// <summary><c>workflow_dispatch</c> / <c>workflow_call</c> inputs, keyed by name.</summary>
    public IReadOnlyDictionary<string, WorkflowInput> Inputs { get; init; } =
        new Dictionary<string, WorkflowInput>(StringComparer.Ordinal);

    public SourceRange Range { get; init; }
}

/// <summary>A declared <c>workflow_dispatch</c> or <c>workflow_call</c> input.</summary>
public sealed record WorkflowInput
{
    /// <summary><c>string</c>, <c>boolean</c>, <c>number</c> or <c>choice</c>; null when not declared.</summary>
    public string? Type { get; init; }

    public string? Description { get; init; }

    public bool Required { get; init; }

    public WorkflowValue? Default { get; init; }

    public IReadOnlyList<WorkflowValue> Options { get; init; } = Array.Empty<WorkflowValue>();

    public SourceRange Range { get; init; }
}
