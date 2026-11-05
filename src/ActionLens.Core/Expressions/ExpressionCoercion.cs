using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// GitHub's comparison rules, kept in one place because six operators sharing four lines of logic is
/// where divergences hide.
/// </summary>
/// <remarks>
/// The shape of the coercion comes from the documented loose-equality rules and is confirmed by both
/// reference implementations, which agree with each other:
/// <list type="bullet">
///   <item>two strings compare case-insensitively, on every operator including <c>&lt;</c> and <c>&gt;=</c>;</item>
///   <item>otherwise both sides become numbers and the comparison is numeric, so <c>'' == 0</c>,
///     <c>'0' == false</c>, <c>null == 0</c> and <c>1 == '1'</c> are all true;</item>
///   <item>text that is not a number coerces to <c>NaN</c>, and any comparison with <c>NaN</c> is
///     false — which is why <c>'false' == null</c> is false while <c>'' == null</c> is true;</item>
///   <item>arrays and objects are never coerced, and are equal only to themselves, so a comparison
///     against one is <see langword="false"/> unless both operands resolved to the same value.</item>
/// </list>
/// </remarks>
public static class ExpressionCoercion
{
    /// <summary>
    /// GitHub's loose equality. <paramref name="sameInstance"/> carries what the evaluator knows about
    /// the two operands having resolved to one and the same value, which is the only way an array or
    /// object comparison can be true.
    /// </summary>
    public static bool AreEqual(GitHubValue left, GitHubValue right, bool sameInstance = false)
    {
        if (IsComposite(left) || IsComposite(right))
        {
            // Identity is the documented rule; everything else about a composite is false.
            return sameInstance && left.Kind == right.Kind;
        }

        if (left.Kind == GitHubValueKind.String && right.Kind == GitHubValueKind.String)
        {
            // No trimming here: whitespace only disappears when text is coerced to a number, so
            // ' a ' == 'a' is false while ' 0.0 ' == false is true.
            return string.Equals(left.String, right.String, StringComparison.OrdinalIgnoreCase);
        }

        var lhs = left.ToCoercedNumber();
        var rhs = right.ToCoercedNumber();
        if (double.IsNaN(lhs) || double.IsNaN(rhs))
        {
            return false;
        }

        return lhs == rhs;
    }

    public static bool NotEqual(GitHubValue left, GitHubValue right, bool sameInstance = false) =>
        !AreEqual(left, right, sameInstance);

    public static bool LessThan(GitHubValue left, GitHubValue right) =>
        TryCompare(left, right, out var order) && order < 0;

    public static bool GreaterThan(GitHubValue left, GitHubValue right) =>
        TryCompare(left, right, out var order) && order > 0;

    /// <summary><c>&lt;=</c> is equality or less-than, matching how both implementations define it.</summary>
    public static bool LessThanOrEqual(GitHubValue left, GitHubValue right) =>
        AreEqual(left, right) || LessThan(left, right);

    public static bool GreaterThanOrEqual(GitHubValue left, GitHubValue right) =>
        AreEqual(left, right) || GreaterThan(left, right);

    /// <summary>
    /// Orders two values, or reports that they cannot be ordered at all — which every operator then
    /// answers as <see langword="false"/>.
    /// </summary>
    private static bool TryCompare(GitHubValue left, GitHubValue right, out int order)
    {
        order = 0;
        if (IsComposite(left) || IsComposite(right))
        {
            return false;
        }

        if (left.Kind == GitHubValueKind.String && right.Kind == GitHubValueKind.String)
        {
            order = string.Compare(left.String, right.String, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        var lhs = left.ToCoercedNumber();
        var rhs = right.ToCoercedNumber();
        if (double.IsNaN(lhs) || double.IsNaN(rhs))
        {
            return false;
        }

        order = lhs.CompareTo(rhs);
        return true;
    }

    private static bool IsComposite(GitHubValue value) =>
        value.Kind is GitHubValueKind.Array or GitHubValueKind.Object;
}
