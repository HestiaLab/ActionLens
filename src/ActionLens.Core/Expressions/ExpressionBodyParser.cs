using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// Precedence-climbing parser for expression bodies. It mirrors
/// <c>src/Sdk/DTExpressions2/Expressions2/ExpressionParser.cs</c> in actions/runner and the TypeScript
/// parser in actions/languageservices, which were cross-checked against each other and agree.
/// </summary>
/// <remarks>
/// <para>
/// Precedence, highest binds tightest (the runner's <c>GetPriority</c> in <c>Tokens/Token.cs</c>):
/// <list type="bullet">
/// <item><description>20 <c>( )</c> grouping;</description></item>
/// <item><description>19 <c>[ ]</c> index, <c>( )</c> call, <c>.</c> member access;</description></item>
/// <item><description>16 <c>!</c> NOT, right-to-left;</description></item>
/// <item><description>11 <c>&gt; &gt;= &lt; &lt;=</c> relational, left-to-right;</description></item>
/// <item><description>10 <c>== !=</c> equality, left-to-right;</description></item>
/// <item><description>6 <c>&amp;&amp;</c> AND, left-to-right; 5 <c>||</c> OR, left-to-right.</description></item>
/// </list>
/// Relational binding tighter than equality is the surprising consequence: <c>a == b &lt; c</c> is
/// <c>a == (b &lt; c)</c>, not the C-family <c>(a == b) &lt; c</c>.
/// </para>
/// <para>
/// Nothing here throws. Every failure path records a positioned diagnostic, sets <c>_aborted</c> and
/// returns null, which unwinds the rest of the body without piling a second, misleading error on top of
/// the characters the first one already explained.
/// </para>
/// <para>
/// One instance parses all the bodies of one <c>ExpressionParser</c> call, so <see cref="Diagnostics"/>
/// is the complete, document-ordered result and the depth limit is raised once per call rather than
/// once per nesting level or per token.
/// </para>
/// </remarks>
internal sealed class ExpressionBodyParser
{
    private const int MinPrecedence = 1;
    private const int NotPrecedence = 16;
    private const int RelationalPrecedence = 11;
    private const int EqualityPrecedence = 10;
    private const int AndPrecedence = 6;
    private const int OrPrecedence = 5;

    private readonly string _source;
    private readonly SourcePositionMap _positions;
    private readonly List<Diagnostic> _diagnostics = new();

    private ExpressionToken[] _tokens = [];
    private int _index;
    private int _depth;
    private bool _aborted;
    private bool _depthLimitReported;

    internal ExpressionBodyParser(string source, SourcePositionMap positions)
    {
        _source = source;
        _positions = positions;
    }

    /// <summary>Every diagnostic raised across the bodies parsed so far, in document order.</summary>
    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Parses the half-open window <c>[start, end)</c> of the document as one expression body.</summary>
    internal ExpressionNode? ParseBody(int start, int end)
    {
        var lexer = new ExpressionLexer(_source, _positions, start, end);
        lexer.Run();
        if (lexer.Error is { } lexical)
        {
            _aborted = true;
            _diagnostics.Add(lexical);
            return null;
        }

        var tokens = ClassifyParentheses(lexer.Tokens);
        if (tokens.Length == 1)
        {
            // Only the End token: the window was empty or pure whitespace, which is `${{ }}`.
            return Fail(
                "The expression has no body.",
                BodyRange(start, end),
                "Write an expression between '${{' and '}}'.");
        }

        _tokens = tokens;
        _index = 0;
        _depth = 0;
        _aborted = false;

        var root = ParseExpressionNode(MinPrecedence);
        if (_aborted || root is null)
        {
            return null;
        }

        if (Current.Kind != ExpressionTokenKind.End)
        {
            return Fail(
                $"Unexpected {Describe(Current)} after the end of the expression.",
                Current.Range,
                "Remove the trailing text, or it belongs inside the expression.");
        }

        return root;
    }

    /// <summary>
    /// The span to blame when the problem is the window itself rather than a token inside it. The end
    /// advances one character when the window is empty, so the range always has a known position.
    /// </summary>
    private SourceRange BodyRange(int start, int end) =>
        new(_positions.At(start), _positions.At(Math.Max(end, start + 1)));

    /// <summary>
    /// <c>(</c> and <c>)</c> are the same characters for grouping and for an argument list, and the runner
    /// resolves which one it is by looking at what came before (<c>GetNextPriority</c> in
    /// <c>ExpressionParser.cs</c>): a <c>(</c> directly after a name belongs to that name.
    /// </summary>
    /// <remarks>
    /// Any name qualifies, not only the functions Actions has. <c>foo(1)</c> is legal syntax with an
    /// unknown callee, and saying so is the evaluator's job - it is the only stage that knows what a name
    /// means in the position it was written, which is why <see cref="ExpressionFunctions"/> exists as a
    /// helper instead of being used as a gate here.
    /// </remarks>
    private static ExpressionToken[] ClassifyParentheses(IReadOnlyList<ExpressionToken> tokens)
    {
        var result = new ExpressionToken[tokens.Count];
        for (var copy = 0; copy < tokens.Count; copy++)
        {
            result[copy] = tokens[copy];
        }
        var callFrames = new Stack<bool>();
        var previous = ExpressionTokenKind.End;

        for (var i = 0; i < result.Length; i++)
        {
            var token = result[i];
            switch (token.Kind)
            {
                case ExpressionTokenKind.LeftParen when previous == ExpressionTokenKind.Name:
                    token = Retype(token, ExpressionTokenKind.OpenArguments);
                    callFrames.Push(true);
                    break;

                case ExpressionTokenKind.LeftParen:
                    callFrames.Push(false);
                    break;

                case ExpressionTokenKind.RightParen when callFrames.Count > 0 && callFrames.Pop():
                    token = Retype(token, ExpressionTokenKind.CloseArguments);
                    break;
            }

            result[i] = token;
            previous = token.Kind;
        }

        return result;
    }

    private static ExpressionToken Retype(ExpressionToken token, ExpressionTokenKind kind) =>
        new(kind, token.Text, token.Number, token.Range);

    private ExpressionToken Current => _tokens[_index];

    private static string Describe(ExpressionToken token) => token.Kind switch
    {
        ExpressionTokenKind.End => "the end of the expression",
        ExpressionTokenKind.Name => $"'{token.Text}'",
        ExpressionTokenKind.String => $"'{token.Text}'",
        ExpressionTokenKind.Number => token.Text,
        ExpressionTokenKind.Comma => "','",
        ExpressionTokenKind.OpenArguments or ExpressionTokenKind.LeftParen => "'('",
        ExpressionTokenKind.CloseArguments or ExpressionTokenKind.RightParen => "')'",
        ExpressionTokenKind.RightBracket => "']'",
        _ => token.Text,
    };

    private ExpressionNode? Fail(string message, SourceRange range, string? suggestion)
    {
        _aborted = true;
        _diagnostics.Add(ExpressionErrors.Syntax(message, range, suggestion));
        return null;
    }

    private ExpressionNode? FailHere(string message, string? suggestion) => Fail(message, Current.Range, suggestion);

    /// <summary>
    /// Charges one unit of parser recursion, allowed up to <see cref="ExpressionParser.MaxDepth"/>.
    /// What costs a unit is nesting, not length: a prefix <c>!</c>, a member or index link, a call, a
    /// group and each operand of a logical run all descend one level, while the next operand of the same
    /// run climbs back up before it is parsed, so a 60-operand <c>&amp;&amp;</c> chain stays two levels deep
    /// and parses. The corpus pins both edges — 49 of anything parses, 50 does not.
    /// </summary>
    private bool EnterLevel()
    {
        _depth++;
        if (_depth < ExpressionParser.MaxDepth)
        {
            return true;
        }

        _aborted = true;
        if (!_depthLimitReported)
        {
            _depthLimitReported = true;
            _diagnostics.Add(ExpressionErrors.TooComplex(
                $"Exceeded max expression depth {ExpressionParser.MaxDepth}.",
                Current.Range,
                "Split the expression into smaller parts."));
        }

        return false;
    }

    private void ExitLevel() => _depth--;

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

    /// <summary>
    /// One primary value followed by the postfix operators that bind tighter than any binary operator:
    /// <c>.</c>, <c>[ ]</c> and a continued argument list. The loop, not recursion, keeps
    /// <c>a.b['c'][0].d</c> shallow in the tree, but each link still charges a depth unit because the
    /// chain is exactly what a runaway expression looks like.
    /// </summary>
    private ExpressionNode? ParsePrimaryWithPostfix()
    {
        var target = ParsePrimary();
        if (target is null)
        {
            return null;
        }

        var charged = 0;
        while (!_aborted)
        {
            var token = Current;
            switch (token.Kind)
            {
                case ExpressionTokenKind.Dot:
                    {
                        if (!EnterLevel())
                        {
                            return null;
                        }

                        charged++;
                        _index++;
                        var member = Current;
                        if (member.Kind == ExpressionTokenKind.Star)
                        {
                            _index++;
                            target = new WildcardAccessNode(
                                target,
                                new SourceRange(target.Range.Start, member.Range.End));
                            break;
                        }

                        if (member.Kind != ExpressionTokenKind.Name)
                        {
                            return FailHere(
                                "Expected a name after '.'.",
                                "'github.' has no member; drop the dot, name the member, or write '.*' for every member.");
                        }

                        if (Peek(1).Kind is ExpressionTokenKind.OpenArguments or ExpressionTokenKind.LeftParen)
                        {
                            return Fail(
                                $"'{member.Text}()' cannot be called: Actions calls functions by name only.",
                                member.Range,
                                "There is no method call on a context value.");
                        }

                        _index++;
                        target = new MemberAccessNode(
                            target,
                            member.Text,
                            new SourceRange(target.Range.Start, member.Range.End));
                        break;
                    }

                case ExpressionTokenKind.LeftBracket:
                    {
                        if (!EnterLevel())
                        {
                            return null;
                        }

                        charged++;
                        _index++;
                        if (Current.Kind == ExpressionTokenKind.Star)
                        {
                            // `a[*]` is the other spelling of `a.*`, so it builds the same node; only the
                            // closing bracket makes it an index-shaped token sequence.
                            _index++;
                            if (Current.Kind != ExpressionTokenKind.RightBracket)
                            {
                                return FailHere("Expected ']' to close the index.", null);
                            }

                            var starClose = Current;
                            _index++;
                            target = new WildcardAccessNode(
                                target,
                                new SourceRange(target.Range.Start, starClose.Range.End));
                            break;
                        }

                        var index = ParseOperand("an index expression", "a ']'");
                        if (index is null)
                        {
                            return null;
                        }

                        if (Current.Kind != ExpressionTokenKind.RightBracket)
                        {
                            return FailHere("Expected ']' to close the index.", null);
                        }

                        var close = Current;
                        _index++;
                        target = new IndexAccessNode(
                            target,
                            index,
                            new SourceRange(target.Range.Start, close.Range.End));
                        break;
                    }

                case ExpressionTokenKind.OpenArguments when target is FunctionCallNode call:
                    {
                        if (!EnterLevel())
                        {
                            return null;
                        }

                        charged++;
                        var arguments = ParseArguments(call.Name, call.Range.Start);
                        if (arguments is null)
                        {
                            return null;
                        }

                        target = new FunctionCallNode(
                            call.Name,
                            arguments.Value.Arguments,
                            new SourceRange(call.Range.Start, arguments.Value.CloseEnd));
                        break;
                    }

                default:
                    for (var i = 0; i < charged; i++)
                    {
                        ExitLevel();
                    }

                    return target;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses one primary value: a literal, a context root, a parenthesised expression or a function
    /// call. Keywords are recognised only here, which is why <c>a.true</c> is a member named <c>true</c>
    /// while a bare <c>true</c> is the boolean.
    /// </summary>
    private ExpressionNode? ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case ExpressionTokenKind.String:
                _index++;
                return new LiteralNode(GitHubValue.FromString(token.Text), token.Range);

            case ExpressionTokenKind.Number:
                _index++;
                return new LiteralNode(GitHubValue.FromNumber(token.Number), token.Range);

            case ExpressionTokenKind.Name:
                return ParseName(token);

            case ExpressionTokenKind.LeftParen:
                return ParseGrouping();

            default:
                return FailHere(
                    $"Expected a value, a '!' or a '(' , found {Describe(token)}.",
                    "Values are single-quoted strings, numbers, 'true', 'false', 'null' and context paths.");
        }
    }

    private ExpressionNode? ParseName(ExpressionToken token)
    {
        if (TryKeyword(token.Text, out var keyword))
        {
            _index++;
            return new LiteralNode(keyword, token.Range);
        }

        if (Peek(1).Kind != ExpressionTokenKind.OpenArguments)
        {
            _index++;
            return new ContextNode(token.Text, token.Range);
        }

        if (!EnterLevel())
        {
            return null;
        }

        _index++;
        var arguments = ParseArguments(token.Text, token.Range.Start);
        ExitLevel();
        if (arguments is null)
        {
            return null;
        }

        return new FunctionCallNode(
            token.Text,
            arguments.Value.Arguments,
            new SourceRange(token.Range.Start, arguments.Value.CloseEnd));
    }

    /// <summary>
    /// A parenthesised expression. The parentheses are punctuation, so the node keeps the range of what
    /// is inside them; an enclosing construct such as <c>!(a &amp;&amp; b)</c> spans the parens anyway.
    /// </summary>
    private ExpressionNode? ParseGrouping()
    {
        if (!EnterLevel())
        {
            return null;
        }

        _index++;
        var inner = ParseOperand("an expression", "a ')'");
        if (inner is null)
        {
            return null;
        }

        if (Current.Kind != ExpressionTokenKind.RightParen)
        {
            return FailHere("Expected ')' to close the group.", null);
        }

        // Consume the parenthesis. It is punctuation with no node of its own, so forgetting it here
        // leaves it to the caller, which can only report it as trailing text — the shape every grouped
        // case in operators_precedence.json was failing with.
        _index++;
        ExitLevel();
        return inner;
    }

    /// <summary>
    /// Parses a nested expression at full precedence, so <c>a[b || c]</c>, <c>(a &amp;&amp; b)</c> and
    /// <c>contains(a, b == c)</c> all work. The caller has already charged the depth level this lives in.
    /// </summary>
    private ExpressionNode? ParseOperand(string what, string expectedClose)
    {
        if (Current.Kind is ExpressionTokenKind.End or ExpressionTokenKind.Comma
            or ExpressionTokenKind.RightParen or ExpressionTokenKind.RightBracket)
        {
            return FailHere(
                $"Expected {what}, found {Describe(Current)}.",
                $"Expected {expectedClose} or another operand here.");
        }

        return ParseExpressionNode(MinPrecedence);
    }

    private readonly record struct CallShape(IReadOnlyList<ExpressionNode> Arguments, SourcePosition CloseEnd);

    /// <summary>
    /// Reads an argument list, having already consumed the opening paren, and consumes the closing one.
    /// A known function called with the wrong argument count is a parse-time diagnostic, the same as in
    /// the C# runner, but it does not damage the tree, so parsing carries on.
    /// </summary>
    private CallShape? ParseArguments(string name, SourcePosition start)
    {
        _index++;
        var arguments = new List<ExpressionNode>(4);
        while (Current.Kind != ExpressionTokenKind.CloseArguments)
        {
            if (Current.Kind is ExpressionTokenKind.End or ExpressionTokenKind.RightParen
                or ExpressionTokenKind.RightBracket or ExpressionTokenKind.Comma)
            {
                Fail(
                    $"Expected an argument, found {Describe(Current)}.",
                    Current.Range,
                    $"'{name}(' is missing an argument or its closing ')' - an empty call is written '{name}()'.");
                return null;
            }

            // An argument costs no depth of its own: the call that contains it already charged one level,
            // which is what makes 49 nested calls legal and 50 illegal, exactly as the corpus reads them
            // (<c>fromjson(tojson(…))</c> four times deeper than the limit's edge fails, one level less
            // does not). Charging call and argument separately halves both limits.
            var argument = ParseExpressionNode(MinPrecedence);
            if (argument is null)
            {
                return null;
            }

            arguments.Add(argument);
            if (Current.Kind == ExpressionTokenKind.Comma)
            {
                _index++;
                continue;
            }

            if (Current.Kind != ExpressionTokenKind.CloseArguments)
            {
                Fail(
                    $"Expected ',' or ')' inside {name}().",
                    Current.Range,
                    "Arguments are separated by commas.");
                return null;
            }
        }

        var close = Current;
        if (ExpressionFunctions.IsKnown(name) && !ExpressionFunctions.HasArity(name, arguments.Count))
        {
            var (min, max) = ExpressionFunctions.ArityOf(name);
            var supplied = $"{name}() was called with {arguments.Count}";
            var complaint = ExpressionFunctions.RequiresOddArgumentCount(name)
                ? $"{name}() takes a predicate and a value for every branch plus one default, so an even argument count is not a call it can have; {supplied}."
                : $"{name}() takes {DescribeArity(min, max)} but {supplied}.";
            _diagnostics.Add(ExpressionErrors.Arity(
                complaint,
                new SourceRange(start, close.Range.End),
                min == max && max >= 0
                    ? $"Call {name}() with {DescribeArity(min, max)}."
                    : null));
        }

        _index++;
        return new CallShape(arguments, close.Range.End);
    }

    private static string DescribeArity(int min, int max)
    {
        if (max < 0)
        {
            return min == 0 ? "any number of arguments" : $"{min} or more arguments";
        }

        if (min == max)
        {
            return min == 0 ? "no arguments" : $"{min} argument{(min == 1 ? string.Empty : "s")}";
        }

        return $"{min} to {max} arguments";
    }

    private ExpressionToken Peek(int offset)
    {
        var at = _index + offset;
        return at < _tokens.Length ? _tokens[at] : _tokens[^1];
    }

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
