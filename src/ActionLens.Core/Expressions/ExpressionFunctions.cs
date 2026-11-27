namespace ActionLens.Core.Expressions;

/// <summary>
/// The functions Actions actually has, and how many arguments each takes.
/// </summary>
/// <remarks>
/// <para>
/// Names are resolved case-insensitively by the runner, so <c>CONTAINS(a, b)</c> is the same call as
/// <c>contains(a, b)</c>; a node keeps the author's own spelling so messages can quote it back. This
/// table is the parser's only semantic knowledge, and it is deliberately not a syntax check: an
/// unrecognised name is legal syntax, and reporting it is the evaluator's call because only it knows
/// which position the expression sits in.
/// </para>
/// <para>
/// <c>format</c> and <c>hashFiles</c> top out at 255 because that is what
/// <c>ExpressionConstants.Functions.MaxArguments</c> in the runner allows. <c>format(1, 255)</c> means a
/// single argument is legal even though the documentation says "at least one replaceValue and string":
/// both implementations accept <c>format(null)</c> and <c>format('')</c>, so we do not reject one.
/// </para>
/// </remarks>
internal static class ExpressionFunctions
{
    private static readonly Dictionary<string, FunctionSignature> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["contains"] = new(2, 2),
        ["startsWith"] = new(2, 2),
        ["endsWith"] = new(2, 2),
        ["join"] = new(1, 2),
        ["format"] = new(1, 255),
        ["toJSON"] = new(1, 1),
        ["fromJSON"] = new(1, 1),
        ["hashFiles"] = new(1, 255),
        ["success"] = new(0, -1),
        ["failure"] = new(0, -1),
        ["always"] = new(0, 0),
        ["cancelled"] = new(0, 0),
        ["case"] = new(3, -1),
    };

    /// <summary>Every known function name, in the spelling the documentation uses.</summary>
    private static readonly string[] KnownNames = [
        "contains",
        "startsWith",
        "endsWith",
        "join",
        "format",
        "toJSON",
        "fromJSON",
        "hashFiles",
        "success",
        "failure",
        "always",
        "cancelled",
        "case",
    ];

    /// <summary>Every known function name, in the spelling the documentation uses.</summary>
    public static ReadOnlySpan<string> Known => KnownNames;

    /// <summary>Whether <paramref name="name"/> is a function Actions has, ignoring case.</summary>
    public static bool IsKnown(string name) =>
        name is not null && Signatures.ContainsKey(name);

    /// <summary>
    /// The argument count of <paramref name="name"/> as <c>(min, max)</c>, where <c>-1</c> is unbounded
    /// and <c>(0, 0)</c> takes no arguments at all. Unknown names report <c>(0, -1)</c> because nothing
    /// is known about them.
    /// </summary>
    public static (int Min, int Max) ArityOf(string name)
    {
        if (name is not null && Signatures.TryGetValue(name, out var signature))
        {
            return (signature.Min, signature.Max);
        }

        return (0, -1);
    }

    /// <summary>Whether a call with <paramref name="count"/> arguments is legal for <paramref name="name"/>.</summary>
    public static bool HasArity(string name, int count)
    {
        var (min, max) = ArityOf(name);
        return count >= min
            && (max < 0 || count <= max)
            && (!RequiresOddArgumentCount(name) || count % 2 == 1);
    }

    /// <summary>
    /// Whether the function's shape, not just its bounds, only closes on an odd argument count.
    /// <c>case()</c> is predicate/value pairs and a trailing default, so four arguments is not a call the
    /// function can have at all: the runner rejects it at parse time
    /// (<c>Even number of parameters supplied, requires an odd number of parameters</c>,
    /// <c>case.json</c>).
    /// </summary>
    public static bool RequiresOddArgumentCount(string name) =>
        name is not null && string.Equals(name, "case", StringComparison.OrdinalIgnoreCase);

    private readonly record struct FunctionSignature(int Min, int Max);
}
