using System.Globalization;
using System.Text;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// Turns the inside of one <c>${{ }}</c> (or a bare <c>if:</c> value) into tokens.
/// </summary>
/// <remarks>
/// The lexer never throws: a character it cannot classify or a literal it cannot close becomes a
/// positioned <see cref="DiagnosticCodes.ExpressionSyntax"/> and stops the body, which is why the
/// parser may assume every token it receives is well-formed.
/// <para>
/// It works on the whole document text plus a half-open window, so token positions are document
/// absolute: an expression written on line 7 reports line 7, not line 1. That is what lets a value
/// extracted from YAML be diagnosed without the caller re-basing anything.
/// </para>
/// </remarks>
internal sealed class ExpressionLexer
{
    private const char Quote = '\'';

    private readonly string _text;
    private readonly SourcePositionMap _positions;
    private readonly int _end;

    private int _index;

    /// <summary>
    /// Whether the token before the one being read is a <see cref="ExpressionTokenKind.Dot"/>, which is
    /// the only position where a word that spells a number keeps being a name: <c>foo.NaN</c> and
    /// <c>foo.Infinity</c> are members GitHub's own corpus reads as property names
    /// (<c>op_dot.json</c>), while a bare <c>NaN</c> is the number.
    /// </summary>
    private bool _afterDot;

    internal ExpressionLexer(string text, SourcePositionMap positions, int start, int end)
    {
        _text = text;
        _positions = positions;
        _end = end;
        _index = start;
    }

    /// <summary>
    /// The tokens of the window, always terminated by one <see cref="ExpressionTokenKind.End"/> token.
    /// Empty when <see cref="Error"/> is set.
    /// </summary>
    internal IReadOnlyList<ExpressionToken> Tokens { get; private set; } = Array.Empty<ExpressionToken>();

    /// <summary>The first lexical error, or null when the whole window tokenized cleanly.</summary>
    internal Diagnostic? Error { get; private set; }

    /// <summary>Tokenizes the window.</summary>
    internal void Run()
    {
        var tokens = new List<ExpressionToken>();
        while (Error is null)
        {
            SkipWhitespace();
            var token = ReadToken();
            tokens.Add(token);
            _afterDot = token.Kind == ExpressionTokenKind.Dot;
            if (token.Kind == ExpressionTokenKind.End)
            {
                break;
            }
        }

        if (Error is not null)
        {
            // A half-known token stream only makes the follow-on parser errors misleading, so report
            // the lexical problem alone.
            tokens.Clear();
            Tokens = tokens;
            return;
        }

        Tokens = tokens;
    }

    private void SkipWhitespace()
    {
        while (_index < _end && IsWhitespace(_text[_index]))
        {
            _index++;
        }
    }

    private static bool IsWhitespace(char value) =>
        value is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    private static bool IsNameStart(char value) =>
        char.IsAsciiLetter(value) || value is '_';

    private static bool IsNamePart(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '_' or '-';

    private ExpressionToken ReadToken()
    {
        if (_index >= _end)
        {
            return EndToken();
        }

        var first = _text[_index];
        var next = _index + 1 < _end ? _text[_index + 1] : '\0';

        switch (first)
        {
            case '(':
                return One(ExpressionTokenKind.LeftParen, "(", 0d);
            case ')':
                return One(ExpressionTokenKind.RightParen, ")", 0d);
            case '[':
                return One(ExpressionTokenKind.LeftBracket, "[", 0d);
            case ']':
                return One(ExpressionTokenKind.RightBracket, "]", 0d);
            case ',':
                return One(ExpressionTokenKind.Comma, ",", 0d);
            case '.':
                return char.IsAsciiDigit(next)
                    ? ReadNumber()
                    : One(ExpressionTokenKind.Dot, ".", 0d);
            case '!':
                return next == '='
                    ? One(ExpressionTokenKind.NotEqual, "!=", 0d)
                    : One(ExpressionTokenKind.Not, "!", 0d);
            case '=':
                return next == '='
                    ? One(ExpressionTokenKind.Equal, "==", 0d)
                    : Unexpected("Comparison is '=='; a single '=' is not an Actions operator.",
                        "Write '==' to compare and '!=' to compare the opposite.");
            case '<':
                return next == '='
                    ? One(ExpressionTokenKind.LessThanOrEqual, "<=", 0d)
                    : One(ExpressionTokenKind.LessThan, "<", 0d);
            case '>':
                return next == '='
                    ? One(ExpressionTokenKind.GreaterThanOrEqual, ">=", 0d)
                    : One(ExpressionTokenKind.GreaterThan, ">", 0d);
            case '&':
                return next == '&'
                    ? One(ExpressionTokenKind.And, "&&", 0d)
                    : Unexpected("Logical and is '&&'; a single '&' is not an Actions operator.", null);
            case '|':
                return next == '|'
                    ? One(ExpressionTokenKind.Or, "||", 0d)
                    : Unexpected("Logical or is '||'; a single '|' is not an Actions operator.", null);
            case '*':
                // The wildcard of `a.*` and `a[*]`. It is a token and not an error because a `*` in any
                // other position is rejected by the parser, which can tell where the wildcard belongs.
                return One(ExpressionTokenKind.Star, "*", 0d);
            case Quote:
                return ReadString();
            case '"' or '/' or '%' or '^' or '~' or '?' or ':' or ';' or '#' or '@' or '`' or '{' or '}':
                return Unexpected($"Unexpected character '{first}'.", PunctuationHint(first));
            default:
                break;
        }

        if (char.IsAsciiDigit(first))
        {
            return ReadNumber();
        }

        if (IsNameStart(first))
        {
            return ReadName();
        }

        if (first is '-' or '+' && (char.IsAsciiDigit(next) || next == '.' || IsNameStart(next)))
        {
            // A sign only opens a number when something a number can start with follows it, so `- ` as
            // an attempted arithmetic operator is still the error Actions makes it. The word case is
            // `-Infinity`, which is a literal; `-Inf` stays a bad token because the whole run fails the
            // grammar rather than being cut short at the sign.
            return ReadNumber();
        }

        return Unexpected($"Unexpected character '{first}'.", PunctuationHint(first));
    }

    private static string? PunctuationHint(char value) => value switch
    {
        '"' => "Actions strings use single quotes; a \" character is not a string delimiter.",
        '+' or '-' or '/' or '%' => "Actions expressions have no arithmetic operators, so '1 + 1' does not parse.",
        '=' => "Comparison is '=='.",
        _ => null,
    };
    /// <summary>
    /// A context root, member or function name — unless the word spells one of the two number names
    /// Actions has, in which case it is a number literal.
    /// </summary>
    /// <remarks>
    /// <c>Infinity</c> and <c>NaN</c> are bare literals: the corpus compares <c>' Infinity ' == Infinity</c>
    /// and formats <c>format('{0}', NaN)</c> as numbers (<c>number.json</c>, <c>coerce_number.json</c>),
    /// and GitHub's own lexer recognises them. .NET's float grammar accepts exactly those two
    /// letter-initial words, in any case, so nothing that could be a real context name is swallowed — and
    /// the position after a <c>.</c> is excluded, because <c>foo.NaN</c> is a member
    /// (<c>op_dot.json</c>).
    /// </remarks>
    private ExpressionToken ReadName()
    {
        var from = _index;
        _index++;
        while (_index < _end && IsNamePart(_text[_index]))
        {
            _index++;
        }

        var word = _text.AsSpan(from, _index - from).ToString();
        if (!_afterDot && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return Value(ExpressionTokenKind.Number, from, _index, word, number);
        }

        return Value(ExpressionTokenKind.Name, from, _index, word, 0d);
    }

    /// <summary>
    /// A single-quoted string, with every <c>''</c> already un-escaped into one <c>'</c>.
    /// </summary>
    /// <remarks>
    /// The two reference implementations agree that <c>''</c> is the escape but disagree on how many
    /// occurrences un-escape: the C# runner replaces <em>every</em> one, the TypeScript language
    /// services replace only the <em>first</em>. <c>'a''b''c'</c> is therefore <c>a'b'c</c> here and
    /// <c>a'b''c</c> there. We deliberately follow the C# runner, because that is the code which
    /// actually evaluates workflows. Pinned by <c>ExpressionParserTests.TwoEscapedQuotesUnescapeBoth</c>.
    /// </remarks>
    private ExpressionToken ReadString()
    {
        var from = _index;
        _index++;
        var content = new StringBuilder();
        while (true)
        {
            if (_index >= _end)
            {
                return Fail("Unterminated string literal.", from, _end, "Close the string with a single quote.");
            }

            if (_text[_index] != Quote)
            {
                content.Append(_text[_index]);
                _index++;
                continue;
            }

            if (_index + 1 < _end && _text[_index + 1] == Quote)
            {
                content.Append(Quote);
                _index += 2;
                continue;
            }

            _index++;
            return Value(ExpressionTokenKind.String, from, _index, content.ToString(), 0d);
        }
    }

    /// <summary>
    /// <c>[+-]?digits[.digits][(e|E)[+-]digits]</c>, the leading-dot forms <c>.5</c> and <c>-.5</c>, the
    /// radix forms <c>0x</c>/<c>0o</c>/<c>0b</c>, and the words <c>Infinity</c> and <c>NaN</c> with or
    /// without a sign.
    /// </summary>
    /// <remarks>
    /// The whole run of number-shaped characters is scanned first and only then validated, so a spelling
    /// GitHub rejects stays one token: <c>0xFZ</c>, <c>0x01p2</c> and <c>-Inf</c> each produce a single
    /// finding that quotes the whole text, the way the corpus's own lexing errors name the whole symbol
    /// (<c>number.json</c>). Cutting the token short at the first bad character instead would read
    /// <c>0x01p2</c> as the number <c>0</c> followed by a stray name.
    /// <para>
    /// The radix forms are literals, not only coercions: <c>0xfF</c> and <c>0o777</c> parse as numbers in
    /// <c>number.json</c>. A sign in front of them does not — <c>-0xFF</c> is a lexing error there, even
    /// though <c>' -0xFF '</c> converts as text — while <c>-Infinity</c> is a literal, so the sign rules of
    /// the two grammars are kept apart in <see cref="Values.NumberGrammar"/> instead of being inferred
    /// from the documented literal set, which lists none of these spellings.
    /// </para>
    /// </remarks>
    private ExpressionToken ReadNumber()
    {
        var from = _index;
        if (_index < _end && _text[_index] is '-' or '+')
        {
            _index++;
        }

        while (true)
        {
            while (_index < _end && (char.IsAsciiLetterOrDigit(_text[_index]) || _text[_index] == '.'))
            {
                _index++;
            }

            // An exponent sign belongs to the number and nowhere else, so `1e-1` is one token while a
            // sign that leads to no digit stays outside the token.
            if (_index + 1 < _end
                && _text[_index] is '+' or '-'
                && _text[_index - 1] is 'e' or 'E'
                && char.IsAsciiDigit(_text[_index + 1]))
            {
                _index++;
                continue;
            }

            break;
        }

        var raw = _text.AsSpan(from, _index - from).ToString();
        if (TryReadNumber(raw, out var value))
        {
            return Value(ExpressionTokenKind.Number, from, _index, raw, value);
        }

        return raw.Length > 0 && raw[^1] is 'e' or 'E'
            ? Fail("An exponent needs at least one digit.", from, _index, "Write 1e2 rather than 1e.")
            : Fail($"'{raw}' is not a number Actions can read.", from, _index, null);
    }

    /// <summary>
    /// Whether the scanned text is a number Actions writes, and what it is worth. Invariant culture, so a
    /// workflow never depends on the machine's decimal separator, and no blanks: leading or trailing
    /// spaces belong to the coercion grammar, not to a literal. A magnitude too large for a
    /// <see cref="double"/> stays positive or negative infinity, which is what the JavaScript number GitHub
    /// models would also hold.
    /// </summary>
    private static bool TryReadNumber(string raw, out double value) =>
        NumberGrammar.TryReadLiteral(raw, out value);

    private ExpressionToken Unexpected(string message, string? suggestion)
    {
        var from = _index;
        _index++;
        return Fail(message, from, _index, suggestion);
    }

    private ExpressionToken Fail(string message, int from, int to, string? suggestion)
    {
        if (Error is null)
        {
            Error = ExpressionErrors.Syntax(message, new SourceRange(_positions.At(from), _positions.At(to)), suggestion);
        }

        return EndToken();
    }

    /// <summary>
    /// A token whose meaning depends on context: <c>(</c> and <c>)</c> are the same characters for
    /// grouping and for an argument list, and <see cref="ExpressionParser"/> re-classifies them once it
    /// knows which one a name is followed by.
    /// </summary>
    /// <summary>
    /// Emits an operator token and advances by its whole width. Two-character operators
    /// (<c>==</c>, <c>!=</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>&lt;=</c>, <c>&gt;=</c>) would otherwise leave their
    /// trailing character to be lexed again as a lone, invalid operator.
    /// </summary>
    private ExpressionToken One(ExpressionTokenKind kind, string raw, double number)
    {
        var from = _index;
        _index += raw.Length;
        return Value(kind, from, _index, raw, number);
    }

    /// <summary>The token that marks the end of the body. It carries a position but no width.</summary>
    private ExpressionToken EndToken()
    {
        var position = _positions.At(_index);
        return new ExpressionToken(ExpressionTokenKind.End, string.Empty, 0d, new SourceRange(position));
    }

    private ExpressionToken Value(ExpressionTokenKind kind, int from, int to, string payload, double number) =>
        new(kind, payload, number, new SourceRange(_positions.At(from), _positions.At(to)));
}

/// <summary>Maps a character index in the document to a 1-based line/column position.</summary>
internal sealed class SourcePositionMap
{
    private readonly int[] _lineStarts;
    private readonly int _length;
    private readonly SourcePosition _origin;

    private SourcePositionMap(int[] lineStarts, int length, SourcePosition origin)
    {
        _lineStarts = lineStarts;
        _length = length;
        _origin = origin;
    }

    /// <summary>Builds the index of line starts, counting each <c>\n</c> as a break.</summary>
    /// <param name="text">The text positions are measured in.</param>
    /// <param name="origin">
    /// Where that text begins in the workflow file, when it is a value taken out of one. Positions are
    /// then reported in file coordinates rather than relative to the value's own first character —
    /// otherwise every token of a single-line <c>if:</c> collapses onto <c>1:1</c>.
    /// </param>
    internal static SourcePositionMap Build(string text, SourcePosition? origin = null)
    {
        var starts = new List<int>(8) { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return new SourcePositionMap(starts.ToArray(), text.Length, origin ?? SourcePosition.Unknown);
    }

    /// <summary>The position of the character at <paramref name="index"/>; both coordinates are 1-based.</summary>
    internal SourcePosition At(int index)
    {
        // The upper bound is the text's length, not the last line's start: clamping to the latter made
        // every index on the final line (and every index at all in a single-line expression) resolve to
        // column 1 of that line.
        var clamped = Math.Min(Math.Max(index, 0), _length);
        var lo = 0;
        var hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= clamped)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var line = lo + 1;
        var column = clamped - _lineStarts[lo] + 1;
        if (!_origin.IsKnown)
        {
            return new SourcePosition(line, column);
        }

        // The first line of the value starts at the origin's column; lines after it are their own lines
        // in the file. The value's continuation indentation is not recorded by the YAML model, so those
        // lines keep the column the expression itself reports (see docs/limitations.md).
        return line == 1
            ? new SourcePosition(_origin.Line, _origin.Column + column - 1)
            : new SourcePosition(_origin.Line + line - 1, column);
    }
}

/// <summary>Builds the expression-stage diagnostics the lexer and parser raise. Positions are absolute.</summary>
internal static class ExpressionErrors
{
    internal static Diagnostic Syntax(string message, SourceRange range, string? suggestion) =>
        Build(DiagnosticCodes.ExpressionSyntax, message, range, suggestion);

    internal static Diagnostic TooComplex(string message, SourceRange range, string? suggestion) =>
        Build(DiagnosticCodes.ExpressionTooComplex, message, range, suggestion);

    internal static Diagnostic Arity(string message, SourceRange range, string? suggestion) =>
        Build(DiagnosticCodes.FunctionArity, message, range, suggestion);

    private static Diagnostic Build(string code, string message, SourceRange range, string? suggestion) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Error,
        Stage = DiagnosticStage.Expression,
        Message = message,
        Range = range,
        Suggestion = suggestion,
    };
}
