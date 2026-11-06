using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;

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
internal sealed partial class ExpressionBodyParser
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

}
