using ActionLens.Core.Text;

namespace ActionLens.Core.Workflows;

/// <summary>
/// A workflow file read as data. Nothing here is evaluated: conditions, <c>uses</c> strings and
/// matrix dimensions keep the text the author wrote plus the position it came from, so that later
/// stages can explain themselves instead of restating a guess.
/// </summary>
public sealed record WorkflowDocument
{
    /// <summary>The workflow's <c>name:</c>, or the file name when the author did not set one.</summary>
    public required string Name { get; init; }

    public string? RunName { get; init; }

    public required TriggerSection Triggers { get; init; }

    /// <summary>Jobs in document order. Job ids are unique because duplicate keys are rejected.</summary>
    public required IReadOnlyList<JobDefinition> Jobs { get; init; }

    public IReadOnlyDictionary<string, WorkflowValue> Env { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    public DefaultsSection Defaults { get; init; } = DefaultsSection.Empty;

    public ConcurrencySection? Concurrency { get; init; }

    public IReadOnlyDictionary<string, WorkflowValue> Permissions { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    public int? TimeoutMinutes { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;

    public JobDefinition? FindJob(string jobId) =>
        Jobs.FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.Ordinal));
}

/// <summary><c>defaults:</c> — applied to every step that does not say otherwise.</summary>
public sealed record DefaultsSection
{
    public static DefaultsSection Empty { get; } = new();

    public string? WorkingDirectory { get; init; }

    public string? Shell { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;
}

/// <summary><c>concurrency:</c> — a group name plus whether pending runs are cancelled.</summary>
public sealed record ConcurrencySection
{
    public required string Group { get; init; }

    public bool CancelInProgress { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;
}
