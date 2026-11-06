using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

internal sealed partial class ExpressionBodyParser
{
    /// <summary>
    /// Case-insensitive on purpose: the runner compares these keywords with
    /// <c>StringComparison.OrdinalIgnoreCase</c>, so <c>TRUE</c> and <c>Null</c> are literals rather than
    /// context names. Pinned by <c>ExpressionParserTests.KeywordsAreCaseInsensitive</c>.
    /// </summary>
    private static bool TryKeyword(string name, out GitHubValue value)
    {
        if (string.Equals(name, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = GitHubValue.True;
            return true;
        }

        if (string.Equals(name, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = GitHubValue.False;
            return true;
        }

        if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
        {
            value = GitHubValue.Null;
            return true;
        }

        value = GitHubValue.Null;
        return false;
    }

    private static bool TryComparisonOperator(ExpressionTokenKind kind, out ComparisonOperator comparison)
    {
        switch (kind)
        {
            case ExpressionTokenKind.Equal:
                comparison = ComparisonOperator.Equal;
                return true;
            case ExpressionTokenKind.NotEqual:
                comparison = ComparisonOperator.NotEqual;
                return true;
            case ExpressionTokenKind.LessThan:
                comparison = ComparisonOperator.LessThan;
                return true;
            case ExpressionTokenKind.LessThanOrEqual:
                comparison = ComparisonOperator.LessThanOrEqual;
                return true;
            case ExpressionTokenKind.GreaterThan:
                comparison = ComparisonOperator.GreaterThan;
                return true;
            case ExpressionTokenKind.GreaterThanOrEqual:
                comparison = ComparisonOperator.GreaterThanOrEqual;
                return true;
            default:
                comparison = ComparisonOperator.Equal;
                return false;
        }
    }

    private static int PrecedenceOf(ExpressionTokenKind kind) => kind switch
    {
        ExpressionTokenKind.Or => OrPrecedence,
        ExpressionTokenKind.And => AndPrecedence,
        ExpressionTokenKind.Equal or ExpressionTokenKind.NotEqual => EqualityPrecedence,
        ExpressionTokenKind.GreaterThan or ExpressionTokenKind.GreaterThanOrEqual
            or ExpressionTokenKind.LessThan or ExpressionTokenKind.LessThanOrEqual => RelationalPrecedence,
        _ => 0,
    };
}
