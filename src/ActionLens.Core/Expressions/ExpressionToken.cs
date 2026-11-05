using ActionLens.Core.Text;

namespace ActionLens.Core.Expressions;

/// <summary>
/// The terminal symbols of the Actions expression grammar. Mirrors
/// <c>src/Sdk/DTExpressions2/Expressions2/Tokens/Token.cs</c> in actions/runner, minus the wrapper
/// tokens: <c>${{</c> and <c>}}</c> are stripped before lexing because a condition may be written
/// without them.
/// </summary>
internal enum ExpressionTokenKind
{
    /// <summary>Sentinel for the end of the expression body.</summary>
    End,

    /// <summary><c>(</c>, precedence 20.</summary>
    LeftParen,

    /// <summary><c>)</c>, precedence 20.</summary>
    RightParen,

    /// <summary><c>[</c>, precedence 19.</summary>
    LeftBracket,

    /// <summary><c>]</c>, precedence 19.</summary>
    RightBracket,

    /// <summary><c>.</c>, precedence 19.</summary>
    Dot,

    /// <summary>
    /// <c>*</c>, legal only after a <c>.</c> or as the whole of an index, where it makes the path a
    /// wildcard. Actions has no arithmetic, so anywhere else it is simply not a token the parser accepts.
    /// </summary>
    Star,

    /// <summary><c>(</c> that opens an argument list, precedence 19.</summary>
    OpenArguments,

    /// <summary><c>)</c> that closes an argument list, precedence 19.</summary>
    CloseArguments,

    /// <summary><c>,</c> between function arguments.</summary>
    Comma,

    /// <summary><c>!</c>, precedence 16, right-to-left.</summary>
    Not,

    /// <summary><c>||</c>, precedence 5, left-to-right.</summary>
    Or,

    /// <summary><c>&amp;&amp;</c>, precedence 6, left-to-right.</summary>
    And,

    /// <summary><c>&gt;</c>, precedence 11, left-to-right.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>, precedence 11, left-to-right.</summary>
    GreaterThanOrEqual,

    /// <summary><c>&lt;</c>, precedence 11, left-to-right.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>, precedence 11, left-to-right.</summary>
    LessThanOrEqual,

    /// <summary><c>==</c>, precedence 10, left-to-right.</summary>
    Equal,

    /// <summary><c>!=</c>, precedence 10, left-to-right.</summary>
    NotEqual,

    /// <summary>A number literal, already widened to <see cref="double"/>.</summary>
    Number,

    /// <summary>A single-quoted string literal, with every <c>''</c> already un-escaped.</summary>
    String,

    /// <summary>A context root, a member name after <c>.</c>, or a function name.</summary>
    Name,
}

/// <summary>One lexeme of an expression body, with its position in the owning document.</summary>
internal readonly struct ExpressionToken
{
    internal ExpressionToken(ExpressionTokenKind kind, string text, double number, SourceRange range)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Range = range;
    }

    internal ExpressionTokenKind Kind { get; }

    /// <summary>
    /// The decoded payload for <see cref="ExpressionTokenKind.String"/>; otherwise the exact source
    /// text of the token.
    /// </summary>
    internal string Text { get; }

    /// <summary>The value of a <see cref="ExpressionTokenKind.Number"/> token; 0 for anything else.</summary>
    internal double Number { get; }

    internal SourceRange Range { get; }

    /// <summary>The token's first character position, used to order diagnostics.</summary>
    internal SourcePosition Start => Range.Start;

    internal bool IsOperator => Kind switch
    {
        ExpressionTokenKind.Not
            or ExpressionTokenKind.Or
            or ExpressionTokenKind.And
            or ExpressionTokenKind.GreaterThan
            or ExpressionTokenKind.GreaterThanOrEqual
            or ExpressionTokenKind.LessThan
            or ExpressionTokenKind.LessThanOrEqual
            or ExpressionTokenKind.Equal
            or ExpressionTokenKind.NotEqual => true,
        _ => false,
    };

    public override string ToString() => $"{Kind} {Text}";
}
