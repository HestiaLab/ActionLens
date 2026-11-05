using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// The four status check functions GitHub allows in <c>if:</c> conditions. Their answer comes from how
/// the run went, which is what the scenario engine decides.
/// </summary>
public enum StatusFunction
{
    Success,
    Failure,
    Always,
    Cancelled,
}

/// <summary>
/// The result of evaluating an expression: a value, or the reason no value is knowable.
/// </summary>
/// <remarks>
/// Only one side is ever set. Keeping both in one small type means every caller has to decide what to
/// do with ignorance, which is the point — silently reading <see cref="Value"/> as if it were always
/// present is how a static analyzer ends up reporting guesses as facts.
/// </remarks>
public readonly record struct Evaluated
{
    public GitHubValue? Value { get; }

    /// <summary>The reason nothing is known, set exactly when <see cref="Value"/> is null.</summary>
    public UnknownReason? Reason { get; }

    private Evaluated(GitHubValue? value, UnknownReason? reason)
    {
        Value = value;
        Reason = reason;
    }

    public bool IsKnown => Value is not null;

    public static Evaluated Known(GitHubValue value) => new(value, null);

    public static Evaluated Unknown(UnknownReason reason) => new(null, reason);

    /// <summary>The value, when known. Throws when it is not, which is a programming error in the caller.</summary>
    public GitHubValue Required =>
        Value ?? throw new InvalidOperationException("The expression has no statically known value: " + Reason?.Message);

    /// <summary>Coerces to a three-state condition the way Actions coerces an <c>if:</c> result.</summary>
    public TriValue ToCondition() =>
        Value is null
            ? TriValue.Unknown(Reason ?? new UnknownReason(UnknownReasonKind.Propagated, "No value was produced.", SourceRange.Unknown))
            : Value.IsTruthy() ? TriValue.True : TriValue.False;
}

/// <summary>
/// Supplies expression context values to <see cref="ExpressionEvaluator"/>. One small interface keeps
/// the evaluator independent of where values come from — a job scope, a step scope, a test double —
/// and makes "we cannot know this" a first-class answer rather than a fake value.
/// </summary>
public interface IContextProvider
{
    /// <summary>
    /// Whether the context name may appear in this position at all. GitHub restricts contexts per
    /// workflow key — <c>env</c>, <c>matrix</c> and <c>steps</c> are not available in a job's <c>if:</c>,
    /// and <c>secrets</c> is available in neither — which is a legality question, not a knowledge one.
    /// </summary>
    bool IsAvailable(string contextName);

    /// <summary>
    /// The value of a whole context such as <c>github</c> or <c>matrix</c>. Returns <see langword="false"/>
    /// when the context exists but its contents are only known at runtime.
    /// </summary>
    bool TryGetContext(string contextName, out GitHubValue context);

    /// <summary>Why <see cref="TryGetContext"/> could not answer, phrased for a reader of the workflow.</summary>
    UnknownReason ExplainContextUnknown(string contextName);

    /// <summary>
    /// Why a context name or function is not usable in this position at all, which is a schema error
    /// rather than a gap in knowledge — <c>secrets</c> in an <c>if:</c>, or <c>hashFiles()</c> in a
    /// job-level <c>if:</c>.
    /// </summary>
    UnknownReason ExplainUnavailable(string symbolName);

    /// <summary>
    /// Whether a function may be used in this position. GitHub allows the status functions and
    /// <c>hashFiles()</c> in step conditions but not everywhere, which is again a legality question.
    /// </summary>
    bool IsFunctionAvailable(string functionName);

    /// <summary>
    /// The scenario's answer for a status function. Returns <see langword="false"/> when the scenario
    /// cannot decide — for instance when an ancestor job may have been skipped, which the
    /// documentation does not resolve for <c>success()</c>.
    /// </summary>
    bool TryGetStatus(StatusFunction statusFunction, out TriValue value);
}
