namespace ActionLens.Core.Linting;

/// <summary>
/// A <c>uses:</c> value read as <c>owner/repo[/path]@ref</c> — the only form whose pin can be judged.
/// </summary>
/// <remarks>
/// The forms that are not checkable are refused rather than guessed at: a local action (<c>./my-action</c>)
/// is whatever commit the workflow itself runs at, a container (<c>docker://…</c>) is an image tag rather
/// than an action ref, and a value built from an expression resolves to a string no static rule has seen.
/// </remarks>
internal sealed record ActionReference(string Owner, string Repository, string Path, string Ref)
{
    /// <summary>
    /// True when the ref is a full commit SHA. Deliberately strict: a 7-character SHA is what
    /// <c>git log --oneline</c> prints, not something the runner resolves as an immutable ref, so it is
    /// reported as unpinned and the message says it is truncated instead of pretending otherwise.
    /// </summary>
    internal bool IsPinnedToCommit => Ref.Length == 40 && IsHex(Ref);

    /// <summary>The action as an author would name it, path included when the call has one.</summary>
    internal string Name => Path.Length == 0 ? $"{Owner}/{Repository}" : $"{Owner}/{Repository}/{Path}";

    /// <summary>True for the checkout family, whose <c>ref</c> and <c>fetch-depth</c> decide what code lands.</summary>
    internal bool IsCheckout => Repository.Contains("checkout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a <c>uses:</c> value. Returns false for every shape this layer cannot judge, so a caller says
    /// nothing instead of reporting a ref it did not parse.
    /// </summary>
    internal static bool TryParse(string? uses, out ActionReference? reference)
    {
        reference = null;
        var text = (uses ?? string.Empty).Trim();
        if (text.Length == 0
            || text.StartsWith("./", StringComparison.Ordinal)
            || text.StartsWith("docker://", StringComparison.Ordinal)
            || text.Contains("${{", StringComparison.Ordinal))
        {
            return false;
        }

        var at = text.IndexOf('@');
        if (at <= 0 || at == text.Length - 1)
        {
            // No ref at all, or an empty one: that is the schema stage's finding to make, not this rule's.
            return false;
        }

        var segments = text[..at].Split('/', StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || segments.Any(segment => segment.Length == 0))
        {
            return false;
        }

        // GitHub reads a trailing `# tag` as a comment, so it is cut before the ref is judged; a quoted value
        // keeps its comment, which would otherwise read as a long odd ref.
        var tail = text[(at + 1)..];
        var comment = tail.IndexOf(" #", StringComparison.Ordinal);
        var value = comment < 0 ? tail : tail[..comment];

        reference = new ActionReference(segments[0], segments[1], string.Join('/', segments[2..]), value.Trim());
        return reference.Ref.Length > 0;
    }

    /// <summary>True when the text is non-empty and every character is hex, which is what a SHA is made of.</summary>
    internal static bool IsHex(string text) =>
        text.Length > 0 && text.All(Uri.IsHexDigit);
}
