using ActionLens.Core.Text;

namespace ActionLens.Core.Expressions;

internal sealed partial class ExpressionBodyParser
{
    private ExpressionNode? ParseExpressionNode(int minPrecedence)
    {
        var left = ParseUnary();
        if (left is null)
        {
            return null;
        }

        var start = left.Range.Start;
        while (!_aborted)
        {
            var token = Current;
            var precedence = PrecedenceOf(token.Kind);
            if (precedence == 0 || precedence < minPrecedence)
            {
                break;
            }

            if (token.Kind is ExpressionTokenKind.And or ExpressionTokenKind.Or)
            {
                var run = ParseLogicalRun(token.Kind, precedence, left, start);
                if (run is null)
                {
                    return null;
                }

                left = run;
                continue;
            }

            if (!TryComparisonOperator(token.Kind, out var comparison))
            {
                return FailHere("Expected a comparison operator.", null);
            }

            _index++;

            // precedence + 1 is what makes these operators left-associative: the right operand cannot
            // swallow another operator of the same level, so `3 < 2 < 1` groups as `(3 < 2) < 1`.
            var right = ParseExpressionNode(precedence + 1);
            if (right is null)
            {
                return null;
            }

            left = new ComparisonNode(comparison, left, right, new SourceRange(start, right.Range.End));
        }

        return left;
    }

    /// <summary>
    /// Collects a whole run of <c>&amp;&amp;</c> (or of <c>||</c>) into one n-ary node, iteratively, so the
    /// operand count does not grow the tree. Operands are parsed above this operator's precedence,
    /// which is what keeps <c>a &amp;&amp; b || c</c> as <c>(a &amp;&amp; b) || c</c>: the run stops at <c>||</c>
    /// and the caller picks it up at the lower level.
    /// </summary>
    /// <remarks>
    /// Each operand after the first charges a depth level, and a run at one level charges nothing extra
    /// for its length: the corpus accepts a 60-operand chain (<c>1 || 2 || … || 60</c>) but rejects the
    /// 25-deep <c>format(… || format(… || …))</c> nesting that carries one more operand
    /// (<c>syntax-errors.json</c>). That is the same distinction the runner draws — nesting, not token
    /// count — so the charge is taken around the operand parse rather than once per operator.
    /// </remarks>
    private ExpressionNode? ParseLogicalRun(ExpressionTokenKind kind, int precedence, ExpressionNode first, SourcePosition start)
    {
        var operands = new List<ExpressionNode>(8) { first };
        var end = first.Range.End;

        while (Current.Kind == kind)
        {
            _index++;
            if (!EnterLevel())
            {
                return null;
            }

            var operand = ParseExpressionNode(precedence + 1);
            ExitLevel();
            if (operand is null)
            {
                return null;
            }

            operands.Add(operand);
            end = operand.Range.End;
        }

        if (operands.Count == 1)
        {
            return operands[0];
        }

        var logicalOperator = kind == ExpressionTokenKind.And ? LogicalOperator.And : LogicalOperator.Or;
        return new LogicalNode(logicalOperator, operands, new SourceRange(start, end));
    }

    /// <summary>
    /// <c>!</c> is the only prefix operator and binds right-to-left, so <c>!a == b</c> is <c>(!a) == b</c>
    /// and <c>!!x</c> is two nodes.
    /// </summary>
    private ExpressionNode? ParseUnary()
    {
        if (Current.Kind != ExpressionTokenKind.Not)
        {
            return ParsePrimaryWithPostfix();
        }

        var token = Current;
        if (!EnterLevel())
        {
            return null;
        }

        _index++;
        var operand = ParseExpressionNode(NotPrecedence);
        ExitLevel();
        if (operand is null)
        {
            return null;
        }

        return new UnaryNotNode(operand, new SourceRange(token.Range.Start, operand.Range.End));
    }
}
