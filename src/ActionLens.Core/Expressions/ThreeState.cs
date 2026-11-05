using ActionLens.Core.Text;

namespace ActionLens.Core.Expressions;

/// <summary>The three states an ActionLens condition can resolve to.</summary>
public enum TriKind
{
    /// <summary>GitHub will not run this — established, not assumed.</summary>
    False,

    /// <summary>GitHub may run this or not, and the missing input is a runtime value.</summary>
    Unknown,

    /// <summary>GitHub will run this — established, not assumed.</summary>
    True,
}

/// <summary>
/// Why something is not knowable. Leaf reasons name the context and value that is missing; composite
/// reasons name the operator that propagated the gap, so the reader can follow the explanation from
/// the top-level condition down to the specific <c>steps.build.outputs.version</c> that made it
/// unknowable.
/// </summary>
public enum UnknownReasonKind
{
    /// <summary>The context does not exist until something runs: <c>steps</c>, <c>job</c>, <c>secrets</c>.</summary>
    RuntimeContext,

    /// <summary>The context is legal here but this particular value was not supplied by the event payload or the user.</summary>
    ValueMissing,

    /// <summary>The context is not available in this position at all, so GitHub would reject the expression.</summary>
    ContextNotAvailable,

    /// <summary>A function whose answer depends on the filesystem or the run: <c>hashFiles</c>, <c>fromJSON</c> of a step output.</summary>
    RuntimeFunction,

    /// <summary>The answer depends on how the run went, which the scenario decides — including whether an ancestor was skipped.</summary>
    ScenarioDependent,

    /// <summary>An operand was unknown, so the operator's answer is unknown.</summary>
    Propagated,

    /// <summary>The expression does not parse, so nothing can be said about it.</summary>
    InvalidSyntax,
}

/// <summary>A node of an unknown-cause trace.</summary>
public sealed record UnknownReason(
    UnknownReasonKind Kind,
    string Message,
    SourceRange Range,
    IReadOnlyList<UnknownReason> Causes)
{
    public UnknownReason(UnknownReasonKind kind, string message, SourceRange range)
        : this(kind, message, range, Array.Empty<UnknownReason>())
    {
    }

    /// <summary>
    /// The context name or function this reason is about, when it names one. Downstream rules match on
    /// this rather than reading the message, so rewording an explanation cannot silently disable them.
    /// </summary>
    public string? Symbol { get; init; }

    /// <summary>Wraps one cause under an operator that could not decide.</summary>
    public static UnknownReason Propagating(string operatorText, SourceRange range, UnknownReason cause) =>
        new(UnknownReasonKind.Propagated, $"'{operatorText}' cannot be decided yet.", range, new[] { cause });

    public static UnknownReason Propagating(string operatorText, SourceRange range, IReadOnlyList<UnknownReason> causes) =>
        new(UnknownReasonKind.Propagated, $"'{operatorText}' cannot be decided yet.", range, causes);
}

/// <summary>
/// A three-state boolean with, when the state is <see cref="TriKind.Unknown"/>, the trace that
/// explains it. An unknown without a reason is treated as a bug rather than as acceptable output.
/// </summary>
public readonly record struct TriValue(TriKind Kind, UnknownReason? Reason)
{
    public static TriValue True { get; } = new(TriKind.True, null);

    public static TriValue False { get; } = new(TriKind.False, null);

    public bool IsKnown => Kind != TriKind.Unknown;

    public static TriValue Unknown(UnknownReason reason) => new(TriKind.Unknown, reason);

    /// <summary>The value when GitHub would refuse to run, used by callers that only branch on definite answers.</summary>
    public bool DefinitelyFalse => Kind == TriKind.False;

    public static implicit operator TriValue(bool value) => value ? True : False;

    public override string ToString() => Kind switch
    {
        TriKind.True => "true",
        TriKind.False => "false",
        _ => $"unknown({Reason?.Message ?? "no reason given"})",
    };
}
