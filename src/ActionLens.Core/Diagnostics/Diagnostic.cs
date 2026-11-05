using ActionLens.Core.Text;

namespace ActionLens.Core.Diagnostics;

/// <summary>
/// Which part of the pipeline produced a finding. Lets the UI group "the YAML is broken" apart from
/// "the workflow is legal but risky".
/// </summary>
public enum DiagnosticStage
{
    /// <summary>The text could not be read as a single workflow document.</summary>
    Yaml,

    /// <summary>Read as YAML, but does not match the Actions workflow schema.</summary>
    Schema,

    /// <summary>An <c>${{ }}</c> expression could not be lexed or parsed.</summary>
    Expression,

    /// <summary>A <c>strategy.matrix</c> could not be expanded.</summary>
    Matrix,

    /// <summary>The job graph is not usable (missing or cyclic <c>needs</c>).</summary>
    Graph,

    /// <summary>
    /// Something the scenario layer had to leave unresolved, or a scenario entry that does not match the
    /// workflow: a run-time job output, an <c>env</c> value only a run can produce, an override naming a
    /// job that is not in the file.
    /// </summary>
    Scenario,

    /// <summary>A finding from a lint rule.</summary>
    Lint,

    /// <summary>
    /// The analyzer itself failed. Reserved for the last line of defence in <c>WorkflowAnalyzer</c>: an
    /// internal defect still has to come back as a report with a finding rather than as an exception.
    /// </summary>
    Internal,
}

/// <summary>
/// A single finding: what is wrong, where in the YAML it is wrong, and what to do about it.
/// Positions are mandatory-by-convention — a finding with an unknown range is a bug, because the UI
/// cannot jump to it.
/// </summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }

    public required DiagnosticSeverity Severity { get; init; }

    public required DiagnosticStage Stage { get; init; }

    /// <summary>One sentence, present tense, naming the offending thing rather than the rule.</summary>
    public required string Message { get; init; }

    public SourceRange Range { get; init; } = SourceRange.Unknown;

    /// <summary>Concrete change the author can make. Null only when there is nothing better to say.</summary>
    public string? Suggestion { get; init; }

    public string? HelpLink { get; init; }

    /// <summary>For lint rules, the workflow object the finding is attached to (job or step id).</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// The context name or function a finding is about, when there is one — <c>secrets</c> for a context
    /// used where GitHub forbids it. Rules match on this instead of on the message, because wording is
    /// ours to improve and a lint rule that keys on a sentence stops firing the moment it is rephrased.
    /// </summary>
    public string? Symbol { get; init; }

    public override string ToString()
    {
        var location = Range.IsKnown ? $" at {Range}" : string.Empty;
        return $"{Code} [{Severity}] {Stage}{location}: {Message}";
    }
}
