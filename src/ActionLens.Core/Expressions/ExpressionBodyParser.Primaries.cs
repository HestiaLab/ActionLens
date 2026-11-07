using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

internal sealed partial class ExpressionBodyParser
{
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
            switch (Current.Kind)
            {
                case ExpressionTokenKind.Dot:
                    target = ParsePostfixMember(target, ref charged);
                    break;

                case ExpressionTokenKind.LeftBracket:
                    target = ParsePostfixIndex(target, ref charged);
                    break;

                case ExpressionTokenKind.OpenArguments when target is FunctionCallNode call:
                    target = ParsePostfixArguments(call, ref charged);
                    break;

                default:
                    for (var i = 0; i < charged; i++)
                    {
                        ExitLevel();
                    }

                    return target;
            }

            if (target is null)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// One <c>.</c> link: a named member or the <c>.*</c> wildcard. The charge is taken as soon as the
    /// level is entered, and only the caller's default branch gives the levels back — a failed link
    /// returns null without exiting, exactly as the inline loop did.
    /// </summary>
    private ExpressionNode? ParsePostfixMember(ExpressionNode target, ref int charged)
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
            return new WildcardAccessNode(
                target,
                new SourceRange(target.Range.Start, member.Range.End));
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
        return new MemberAccessNode(
            target,
            member.Text,
            new SourceRange(target.Range.Start, member.Range.End));
    }

    /// <summary>One <c>[ ]</c> link: an index expression, or <c>[*]</c> which is the other spelling of <c>.*</c>.</summary>
    private ExpressionNode? ParsePostfixIndex(ExpressionNode target, ref int charged)
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
            return new WildcardAccessNode(
                target,
                new SourceRange(target.Range.Start, starClose.Range.End));
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
        return new IndexAccessNode(
            target,
            index,
            new SourceRange(target.Range.Start, close.Range.End));
    }

    /// <summary>A continued argument list, which only a function-call target can accept.</summary>
    private FunctionCallNode? ParsePostfixArguments(FunctionCallNode call, ref int charged)
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

        return new FunctionCallNode(
            call.Name,
            arguments.Value.Arguments,
            new SourceRange(call.Range.Start, arguments.Value.CloseEnd));
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
}
