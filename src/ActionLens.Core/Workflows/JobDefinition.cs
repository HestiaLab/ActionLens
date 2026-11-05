using ActionLens.Core.Text;

namespace ActionLens.Core.Workflows;

/// <summary>A job as written in the YAML, before any condition is evaluated.</summary>
public sealed record JobDefinition
{
    /// <summary>The key under <c>jobs:</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Where the job id itself is written, used when a finding has no better position to point at.</summary>
    public SourceRange KeyRange { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// The <c>if:</c> value. Null means the job has no condition, which GitHub reads as an implicit
    /// <c>success()</c> — a rule owned by the scenario engine, not by this model.
    /// </summary>
    public WorkflowValue? Condition { get; init; }

    /// <summary>Job ids listed in <c>needs:</c>, in the order written.</summary>
    public IReadOnlyList<string> Needs { get; init; } = Array.Empty<string>();

    public RunsOn RunsOn { get; init; } = RunsOn.Missing;

    public StrategySection? Strategy { get; init; }

    public IReadOnlyList<StepDefinition> Steps { get; init; } = Array.Empty<StepDefinition>();

    public IReadOnlyDictionary<string, WorkflowValue> Env { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, WorkflowValue> Outputs { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    public DefaultsSection Defaults { get; init; } = DefaultsSection.Empty;

    public ConcurrencySection? Concurrency { get; init; }

    public int? TimeoutMinutes { get; init; }

    /// <summary>Set for a callable job (<c>uses: ./other.yml</c> or <c>owner/repo/.../x.yml@ref</c>) instead of <see cref="Steps"/>.</summary>
    public string? ReusableWorkflow { get; init; }

    public IReadOnlyDictionary<string, WorkflowValue> With { get; init; } =
        new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);

    public bool HasContainer { get; init; }

    public bool HasServices { get; init; }

    /// <summary>
    /// True when the job declares <c>environment:</c>. Not modelled further: a deployment needs a real
    /// environment, which is runtime state ActionLens does not have.
    /// </summary>
    public bool HasEnvironment { get; init; }

    /// <summary>True when <c>secrets:</c> is passed to a reusable workflow call.</summary>
    public bool PassesAllSecrets { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;
}

/// <summary>
/// <c>runs-on:</c> — one label, a list of labels, or an expression only a runner could resolve. In the
/// last case the labels stay unresolved and say so instead of being guessed.
/// </summary>
public sealed record RunsOn
{
    public static RunsOn Missing { get; } = new() { IsPresent = false };

    /// <summary>False when the job declares no <c>runs-on</c> at all, which is an error unless it is a reusable call.</summary>
    public bool IsPresent { get; init; }

    public IReadOnlyList<WorkflowValue> Labels { get; init; } = Array.Empty<WorkflowValue>();

    /// <summary>True when any label carries an expression, so the runner cannot be named statically.</summary>
    public bool IsExpression { get; init; }

    /// <summary>
    /// True when the value was written as the <c>group:</c>/<c>labels:</c> mapping rather than as a label or a
    /// list of labels. The labels of that form address a runner group the organisation owns, so a rule that
    /// judged them would be judging names it has no way to know; the form is recorded so they can be left alone.
    /// </summary>
    public bool UsesGroupMapping { get; init; }
}

/// <summary><c>strategy:</c> — the matrix plus the two knobs that change how failures spread.</summary>
public sealed record StrategySection
{
    public MatrixSection? Matrix { get; init; }

    public bool? FailFast { get; init; }

    public int? MaxParallel { get; init; }

    public SourceMap Sources { get; init; } = SourceMap.Empty;
}

/// <summary><c>strategy.matrix:</c> as written. Turning it into combinations is <c>ActionLens.Core.Matrix</c>'s job.</summary>
public sealed record MatrixSection
{
    /// <summary>Named dimensions, in the order written.</summary>
    public IReadOnlyList<MatrixDimension> Dimensions { get; init; } = Array.Empty<MatrixDimension>();

    /// <summary>Each <c>include:</c> entry as written; entries are matched by key set, not by position.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>> Include { get; init; } =
        Array.Empty<IReadOnlyDictionary<string, WorkflowValue>>();

    public IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>> Exclude { get; init; } =
        Array.Empty<IReadOnlyDictionary<string, WorkflowValue>>();

    public SourceMap Sources { get; init; } = SourceMap.Empty;
}

/// <summary>One <c>strategy.matrix</c> dimension: a static list of values, or an expression.</summary>
public sealed record MatrixDimension
{
    public required string Name { get; init; }

    /// <summary>Null when the values come from an expression such as <c>fromJSON(...)</c>.</summary>
    public IReadOnlyList<WorkflowValue>? Values { get; init; }

    /// <summary>The raw expression text when <see cref="Values"/> is null.</summary>
    public string? DynamicExpression { get; init; }

    public SourceRange Range { get; init; }
}
