using System.Globalization;
using ActionLens.Core.Values;

namespace ActionLens.Core.Yaml;

/// <summary>
/// Resolves a plain (unquoted) YAML scalar to a typed value the way a YAML 1.1 reader would, which
/// is the family of parsers GitHub Actions sits in — the reason <c>on:</c> is a boolean key unless it
/// is quoted is the same reason <c>- 3.10</c> in a matrix becomes <c>3.1</c>.
/// </summary>
/// <remarks>
/// Deliberate deviation from the YAML 1.1 table: a bare <c>y</c>, <c>Y</c>, <c>n</c> or <c>N</c> is
/// left as the string the author wrote instead of becoming a boolean. Those one-letter forms are far
/// more likely to be a branch name, a matrix value or a shell flag than an intended boolean, and
/// YAML 1.2 core schema — which most modern tooling uses — does not treat them as booleans either.
/// Pinned by <c>YamlScalarResolverTests.BareYAndNStayStrings</c>; revisit if GitHub's own behaviour is
/// ever confirmed differently.
/// </remarks>
internal static class YamlScalarResolver
{
    /// <summary>Resolves the text of a plain scalar. Quoted scalars never reach this: they are strings.</summary>
    internal static GitHubValue Resolve(string text)
    {
        if (text.Length == 0 || text is "~")
        {
            return GitHubValue.Null;
        }

        if (IsNullKeyword(text))
        {
            return GitHubValue.Null;
        }

        if (IsBoolean(text, out var boolean))
        {
            return GitHubValue.FromBoolean(boolean);
        }

        if (TryResolveNumber(text, out var number))
        {
            return GitHubValue.FromNumber(number);
        }

        return GitHubValue.FromString(text);
    }

    private static bool IsNullKeyword(string text) =>
        text is "null" or "Null" or "NULL";

    /// <summary>
    /// The YAML 1.1 boolean spellings that actually appear in workflow files. Matched case-exactly the
    /// way YAML does: mixed case such as <c>True</c> is allowed, but <c>tRuE</c> is not a boolean.
    /// </summary>
    private static bool IsBoolean(string text, out bool value)
    {
        switch (text)
        {
            case "true":
            case "True":
            case "TRUE":
            case "yes":
            case "Yes":
            case "YES":
            case "on":
            case "On":
            case "ON":
                value = true;
                return true;
            case "false":
            case "False":
            case "FALSE":
            case "no":
            case "No":
            case "NO":
            case "off":
            case "Off":
            case "OFF":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryResolveNumber(string text, out double value)
    {
        value = 0;
        var unsigned = text.TrimStart('+');
        if (unsigned.Length == 0)
        {
            return false;
        }

        if (unsigned is ".inf" or ".Inf" or ".INF")
        {
            value = text.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }

        if (unsigned is ".nan" or ".NaN" or ".NAN")
        {
            value = double.NaN;
            return true;
        }

        // YAML allows '_' as a digit separator in block numbers: 1_000_000.
        var cleaned = unsigned.Replace("_", string.Empty, StringComparison.Ordinal);
        if (cleaned.Length == 0)
        {
            return false;
        }

        var negative = cleaned.StartsWith('-');
        var digits = negative ? cleaned[1..] : cleaned;
        if (digits.Length == 0)
        {
            return false;
        }

        var sign = negative ? -1 : 1;

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, sign, out value);
        }

        if (digits.StartsWith("0o", StringComparison.Ordinal))
        {
            return TryParseOctal(digits[2..], sign, out value);
        }

        // YAML 1.1 reads a leading zero as octal: 0777 is 511. This surprises people, which is
        // exactly why ActionLens has to reproduce it instead of normalising it away.
        if (digits.Length > 1 && digits[0] == '0' && digits.AsSpan(1).IndexOfAnyInRange('8', '9') < 0
            && digits.All(char.IsAsciiDigit))
        {
            return TryParseOctal(digits, sign, out value);
        }

        if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && LooksNumeric(digits))
        {
            value = sign * parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rejects the shapes <see cref="double.TryParse(string, NumberStyles, IFormatProvider, out double)"/>
    /// accepts but YAML does not: "1d4", hex-looking text and whitespace-padded numbers.
    /// </summary>
    private static bool LooksNumeric(string digits) =>
        digits.All(c => char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-')
        && digits[0] is not ('.' or 'e' or 'E')
        && digits.Contains('.') != digits.EndsWith('.')
            ? true
            : !digits.EndsWith('.');

    private static bool TryParse(string text, NumberStyles style, CultureInfo provider, int sign, out double value)
    {
        value = 0;
        if (text.Length == 0 || !long.TryParse(text, style, provider, out var parsed))
        {
            return false;
        }

        value = sign * (double)parsed;
        return true;
    }

    private static bool TryParseOctal(string text, int sign, out double value)
    {
        value = 0;
        if (text.Length == 0 || !text.All(char.IsAsciiDigit) || text.Any(c => c >= '8'))
        {
            return false;
        }

        var parsed = 0L;
        foreach (var c in text)
        {
            parsed = (parsed * 8) + (c - '0');
        }

        value = sign * (double)parsed;
        return true;
    }
}
