using System.Globalization;
using System.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// Renders a parsed tree back to normalized expression text, adding only the parentheses the
/// precedence table actually needs.
/// </summary>
/// <remarks>
/// Two uses. The tests compare against this text instead of unwinding object graphs, which keeps a
/// golden table readable and pins associativity directly: <c>a == (b &lt; c)</c> and
/// <c>(a == b) &lt; c</c> cannot both render to <c>a == b &lt; c</c>. Later, explanations and problem
/// messages can quote the author's own sub-expression back to them.
/// <para>
/// The text is normalized, not the original: whitespace, the <c>${{ }}</c> wrapper and the difference
/// between <c>a.b</c> and <c>a['b']</c> are not preserved, while string content is re-escaped with
/// <c>''</c> so it round-trips.
/// </para>
/// </remarks>
internal static class ExpressionTreeText
{
    private const int PostfixPrecedence = 19;
    private const int NotPrecedence = 16;
    private const int RelationalPrecedence = 11;
    private const int EqualityPrecedence = 10;
    private const int AndPrecedence = 6;
    private const int OrPrecedence = 5;

    internal static string Render(ExpressionNode node) => Render(node, explicitGrouping: false);

    /// <summary>
    /// Renders the tree, optionally putting parentheses around every operator that is nested inside
    /// another operator even when precedence makes them unnecessary.
    /// </summary>
    /// <remarks>
    /// <paramref name="explicitGrouping"/> is what makes a golden table able to show associativity:
    /// <c>3 &lt; 2 &lt; 1</c> left-nested needs no parentheses by precedence alone, so the plain form
    /// cannot tell <c>(3 &lt; 2) &lt; 1</c> from itself, while the explicit form writes the first as
    /// <c>(3 &lt; 2) &lt; 1</c> and the second as <c>3 &lt; (2 &lt; 1)</c>.
    /// </remarks>
    internal static string Render(ExpressionNode node, bool explicitGrouping)
    {
        var builder = new StringBuilder();
        Append(builder, node, parentPrecedence: 0, parentIsBinary: false, isRightChild: false, explicitGrouping);
        return builder.ToString();
    }

    /// <summary>The text of each direct operand of a node, in the order the source wrote them.</summary>
    internal static IReadOnlyList<string> RenderOperands(ExpressionNode node) => node switch
    {
        LogicalNode logical => [.. logical.Operands.Select(Render)],
        FunctionCallNode call => [.. call.Arguments.Select(Render)],
        ComparisonNode comparison => [Render(comparison.Left), Render(comparison.Right)],
        IndexAccessNode index => [Render(index.Target), Render(index.Index)],
        WildcardAccessNode wildcard => [Render(wildcard.Target)],
        UnaryNotNode not => [Render(not.Operand)],
        _ => [Render(node)],
    };

    private static void Append(
        StringBuilder builder,
        ExpressionNode node,
        int parentPrecedence,
        bool parentIsBinary,
        bool isRightChild,
        bool explicitGrouping)
    {
        var precedence = PrecedenceOf(node);
        var nestedBinary = parentIsBinary && node is ComparisonNode or LogicalNode;
        var needsParentheses = precedence < parentPrecedence
            || (precedence == parentPrecedence && parentIsBinary && isRightChild)
            || (explicitGrouping && nestedBinary);

        if (needsParentheses)
        {
            builder.Append('(');
        }

        switch (node)
        {
            case LiteralNode literal:
                builder.Append(RenderLiteral(literal));
                break;

            case ContextNode context:
                builder.Append(context.Name);
                break;

            case MemberAccessNode member:
                Append(builder, member.Target, PostfixPrecedence, parentIsBinary: false, isRightChild: false, explicitGrouping);
                builder.Append('.').Append(member.Name);
                break;

            case IndexAccessNode index:
                Append(builder, index.Target, PostfixPrecedence, parentIsBinary: false, isRightChild: false, explicitGrouping);
                builder.Append('[');
                Append(builder, index.Index, 0, parentIsBinary: false, isRightChild: false, explicitGrouping);
                builder.Append(']');
                break;

            // Always the dot spelling, so a tree that uses `a[*]` and one that uses `a.*` render alike:
            // the two are the same operator and the normalized text has one form per meaning.
            case WildcardAccessNode wildcard:
                Append(builder, wildcard.Target, PostfixPrecedence, parentIsBinary: false, isRightChild: false, explicitGrouping);
                builder.Append(".*");
                break;

            case FunctionCallNode call:
                builder.Append(call.Name).Append('(');
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    // An argument is a complete expression of its own, so nothing inside it needs
                    // protecting against what surrounds the call.
                    Append(builder, call.Arguments[i], 0, parentIsBinary: false, isRightChild: false, explicitGrouping);
                }

                builder.Append(')');
                break;

            case UnaryNotNode not:
                builder.Append('!');
                Append(builder, not.Operand, NotPrecedence, parentIsBinary: false, isRightChild: false, explicitGrouping);
                break;

            case LogicalNode logical:
                {
                    var text = logical.Operator == LogicalOperator.And ? " && " : " || ";
                    var level = logical.Operator == LogicalOperator.And ? AndPrecedence : OrPrecedence;
                    for (var i = 0; i < logical.Operands.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(text);
                        }

                        // Operands of the same operator cannot be nested, so a run of them prints flat.
                        Append(builder, logical.Operands[i], level, parentIsBinary: true, isRightChild: false, explicitGrouping);
                    }

                    break;
                }

            case ComparisonNode comparison:
                {
                    var level = comparison.Operator is ComparisonOperator.Equal or ComparisonOperator.NotEqual
                        ? EqualityPrecedence
                        : RelationalPrecedence;
                    Append(builder, comparison.Left, level, parentIsBinary: true, isRightChild: false, explicitGrouping);
                    builder.Append(' ').Append(OperatorText(comparison.Operator)).Append(' ');
                    Append(builder, comparison.Right, level, parentIsBinary: true, isRightChild: true, explicitGrouping);
                    break;
                }

            default:
                builder.Append("<unknown node>");
                break;
        }

        if (needsParentheses)
        {
            builder.Append(')');
        }
    }

    private static int PrecedenceOf(ExpressionNode node) => node switch
    {
        UnaryNotNode => NotPrecedence,
        LogicalNode logical => logical.Operator == LogicalOperator.And ? AndPrecedence : OrPrecedence,
        ComparisonNode comparison => comparison.Operator is ComparisonOperator.Equal or ComparisonOperator.NotEqual
            ? EqualityPrecedence
            : RelationalPrecedence,
        _ => PostfixPrecedence,
    };

    private static string OperatorText(ComparisonOperator operatorName) => operatorName switch
    {
        ComparisonOperator.Equal => "==",
        ComparisonOperator.NotEqual => "!=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        _ => ">=",
    };

    private static string RenderLiteral(LiteralNode literal) => literal.Value.Kind switch
    {
        GitHubValueKind.String => $"'{literal.Value.String.Replace("'", "''", StringComparison.Ordinal)}'",
        GitHubValueKind.Number => RenderNumber(literal.Value.Number),
        _ => literal.Value.Stringify(),
    };

    /// <summary>
    /// Numbers print the way Actions coerces them, except that a value which was written with a
    /// fraction or an exponent keeps its decimal point, so <c>1.0</c> round-trips visibly in a golden
    /// table while <c>1</c> stays <c>1</c>.
    /// </summary>
    private static string RenderNumber(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value == Math.Truncate(value) && Math.Abs(value) < 1e15 && !double.IsNegative(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
