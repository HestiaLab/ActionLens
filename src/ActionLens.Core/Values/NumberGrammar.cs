using System.Globalization;

namespace ActionLens.Core.Values;

/// <summary>
/// The two numeric grammars Actions has, kept in one place because they are easy to tell apart only
/// side by side: the grammar a <em>literal</em> is written in, and the grammar <em>text</em> is read
/// with when it is coerced to a number.
/// </summary>
/// <remarks>
/// Both grammars are the JavaScript number grammar, because the number type Actions models is an
/// IEEE-754 double with JavaScript's conversions, and the official
/// <c>tests/golden/languageservices</c> corpus pins the forms the documentation never lists:
/// <list type="bullet">
///   <item><c>' 0xff ' == 255</c>, <c>' 0o10 ' == 8</c> and the literals <c>0xfF</c>/<c>0o777</c> —
///     radix spellings exist (<c>coerce_number.json</c>, <c>number.json</c>);</item>
///   <item><c>' Infinity ' == Infinity</c>, <c>' -Infinity ' == -Infinity</c> and the literal
///     <c>+Infinity</c> — the infinity and NaN words are numbers, not identifiers
///     (<c>coerce_number.json</c>, <c>number.json</c>, <c>format.json</c>);</item>
///   <item><c>' abc ' == NaN</c> is <c>false</c> and <c>' abc ' != NaN</c> is <c>true</c> — text that
///     is not a number still converts, to <c>NaN</c>, so a comparison against it is decidable
///     (<c>coerce_number.json</c>).</item>
/// </list>
/// The grammars differ in exactly two ways, both pinned by <c>number.json</c>: coercion ignores the
/// whitespace around a number while a literal may not contain any, and a signed radix spelling
/// (<c>-0xFF</c>) converts as text but is a lexing error as a literal, next to <c>0xFZ</c> and the
/// hex-float <c>0x01p2</c>.
/// <para>
/// <c>0b</c> is the one form the corpus does not pin. It is accepted in both grammars because it
/// belongs to the same ECMA-262 <c>Number</c> grammar as the pinned <c>0x</c> and <c>0o</c> forms; that
/// is an inference from the grammar, not an observation.
/// </para>
/// </remarks>
internal static class NumberGrammar
{
    private const NumberStyles PlainNumber =
        NumberStyles.AllowLeadingSign
            | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowExponent;

    /// <summary>
    /// Actions' text-to-number coercion: the whole text is a number, or it is <c>NaN</c>. Blank text is
    /// <c>0</c>, which is why <c>'' == 0</c> and <c>'   ' == 0</c> are true.
    /// </summary>
    internal static bool TryConvertText(string text, out double value)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            value = 0d;
            return true;
        }

        return TryScan(trimmed, out value, radixWithSign: true);
    }

    /// <summary>
    /// The lexer's literal grammar: no surrounding whitespace, and no sign in front of a radix prefix.
    /// </summary>
    internal static bool TryReadLiteral(string text, out double value) =>
        TryScan(text, out value, radixWithSign: false);

    /// <summary>
    /// Scans an already-whitespace-free numeric text. .NET's own float grammar covers the decimal
    /// spellings and the <c>Infinity</c>/<c>NaN</c> words — among letter-initial words it accepts only
    /// those two, in any case, which is what makes it safe to route a word-shaped token through here
    /// without swallowing a real context name — so only the radix forms need a path of their own.
    /// </summary>
    private static bool TryScan(string text, out double value, bool radixWithSign)
    {
        if (TryReadRadix(text, out var radixValue, out var signed))
        {
            if (signed && !radixWithSign)
            {
                value = 0d;
                return false;
            }

            value = radixValue;
            return true;
        }

        // .NET refuses a bare ".5", which Actions accepts, so the leading zero is supplied rather than
        // written by hand everywhere a number may start with a point.
        var candidate = text[0] == '.' ? "0" + text : text;
        return double.TryParse(candidate, PlainNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads <c>0x</c>/<c>0o</c>/<c>0b</c> followed by at least one digit valid in that base, and nothing
    /// else. Any other shape — a bad digit, an empty digit run, trailing characters — reports false so
    /// the caller falls through and fails on the whole text instead of on the leftover.
    /// </summary>
    private static bool TryReadRadix(string text, out double value, out bool signed)
    {
        value = 0d;
        signed = false;

        var digits = text.AsSpan();
        if (digits.Length > 0 && digits[0] is '-' or '+')
        {
            signed = digits[0] == '-';
            digits = digits[1..];
        }

        if (digits.Length < 3 || digits[0] != '0')
        {
            return false;
        }

        var (baseValue, valid) = digits[1] switch
        {
            'x' or 'X' => (16, "0123456789abcdefABCDEF"),
            'o' or 'O' => (8, "01234567"),
            'b' or 'B' => (2, "01"),
            _ => (0, string.Empty),
        };
        if (baseValue == 0)
        {
            return false;
        }

        digits = digits[2..];
        if (digits.Length == 0)
        {
            return false;
        }

        var accumulator = 0d;
        foreach (var c in digits)
        {
            if (valid.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return false;
            }

            var digit = char.IsAsciiDigit(c) ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);
            accumulator = (accumulator * baseValue) + digit;
        }

        value = signed ? -accumulator : accumulator;
        return true;
    }
}
