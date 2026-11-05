using ActionLens.Core.Text;

namespace ActionLens.Core.Workflows;

/// <summary>A step as written. Either an action call (<see cref="Uses"/>) or a script (<see cref="Run"/>).</summary>
public sealed record StepDefinition
{
    /// <summary>Zero-based position in the job's <c>steps:</c> list; also used to name the step in messages.</summary>
    public required int Index { get; init; }

    /// <summary>Where the step begins, used when a finding has no better position to point at.</summary>
    public SourceRange KeyRange { get; init; }

    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>The <c>if:</c> value; null means the implicit <c>success()</c>.</summary>
    public WorkflowValue? Condition { get; init; }

    /// <summary>The action reference, e.g. <c>actions/checkout@v4</c>. Null for a script step.</summary>
    public string? Uses { get; init; }

    /// <summary>The script text for a <c>run:</c> step, exactly as written.</summary>
    public string? Run { get; init; }

    public string? Shell { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, WorkflowValue> With { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, WorkflowValue> Env { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    /// <summary><c>continue-on-error:</c>, resolved when literal and kept as text when it is an expression.</summary>
    public WorkflowValue? ContinueOnError { get; init; }

    public int? TimeoutMinutes { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;

    public bool IsActionCall => Uses is not null;

    /// <summary>
    /// How the step is addressed in expressions. A step without <c>id:</c> cannot be referenced at all,
    /// which is worth saying out loud rather than inventing a name for it.
    /// </summary>
    public string? ContextKey => Id is null ? null : $"steps.{Id}";
}
