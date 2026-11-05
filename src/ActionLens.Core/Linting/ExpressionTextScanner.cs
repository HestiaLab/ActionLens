namespace ActionLens.Core.Linting;

/// <summary>
/// The reads every expression-shaped rule needs, done on the author's text rather than on a parsed tree.
/// </summary>
/// <remarks>
/// The lint layer deliberately does not run the expression parser: the rules ask "does this text mention that
/// context, and where", which a tree would answer equally well but at the cost of re-parsing what the
/// scenario engine already parsed, and of throwing away the character offset a positioned finding needs.
/// Everything here is a scan over one string with no file, no culture and no clock in it.
/// </remarks>
internal static class ExpressionTextScanner
{
    /// <summary>
    /// Each <c>${{ … }}</c> in the text, with the index of its opening brace and the body between the braces.
    /// An unterminated <c>${{</c> yields nothing: without a closing brace there is no expression to judge,
    /// and the syntax stage reports it.
    /// </summary>
    internal static List<Interpolation> Interpolations(string text)
    {
        var found = new List<Interpolation>();
        if (text.Length == 0)
        {
            return found;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var open = text.IndexOf("${{", cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var body = open + 3;
            var close = text.IndexOf("}}", body, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            found.Add(new Interpolation(open, text[body..close]));
            cursor = close + 2;
        }

        return found;
    }

    /// <summary>
    /// Whether the text reads <paramref name="contextName"/> as a context root — <c>secrets.FOO</c>,
    /// <c>secrets['FOO']</c> — and not as a property that merely has that name.
    /// </summary>
    /// <remarks>
    /// Single-quoted string literals are skipped, so <c>contains(github.event.head_commit.message, 'secrets')</c>
    /// does not read as a secrets reference. A root is a match that begins at a word edge and is followed by the
    /// dereference, which is what keeps <c>my_secrets.token</c> and <c>steps.secrets.outputs.x</c> out.
    /// </remarks>
    internal static bool ReferencesContext(string? text, string contextName)
    {
        if (text is null || text.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                index = SkipQuotedLiteral(text, index);
                continue;
            }

            if (!text.AsSpan(index).StartsWith(contextName, StringComparison.OrdinalIgnoreCase)
                || (index > 0 && IsContinuation(text[index - 1]))
                || index + contextName.Length == text.Length)
            {
                continue;
            }

            var after = index + contextName.Length;
            while (after < text.Length && char.IsWhiteSpace(text[after]))
            {
                after++;
            }

            if (after < text.Length && (text[after] == '.' || text[after] == '['))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a body mentions one of these context paths, e.g. <c>github.event.pull_request.head</c>.</summary>
    internal static bool Mentions(string interpolationBody, string contextPath) =>
        interpolationBody.Contains(contextPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsContinuation(char previous) =>
        char.IsLetterOrDigit(previous) || previous == '_' || previous == '-' || previous == '.';

    /// <summary>The index of the closing quote of the literal that starts at <paramref name="start"/>.</summary>
    private static int SkipQuotedLiteral(string text, int start)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] != '\'')
            {
                index++;
                continue;
            }

            // '' inside a literal is an escaped quote, not the end of it.
            if (index + 1 < text.Length && text[index + 1] == '\'')
            {
                index += 2;
                continue;
            }

            return index;
        }

        return text.Length;
    }
}

/// <summary>One <c>${{ … }}</c> inside a value: where it starts and what it says.</summary>
internal readonly record struct Interpolation(int Index, string Body);
