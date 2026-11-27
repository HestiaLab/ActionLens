namespace ActionLens.Core.Text;

/// <summary>
/// Case-insensitive Levenshtein distance, used for "did you mean" suggestions in the job graph and the
/// runner-label lint rule. Both used to carry their own copy of the same two-row dynamic program; a
/// divergence between them would have made the same typo get different advice in two places.
/// </summary>
/// <remarks>
/// Case is folded on purpose: a capitalisation slip is the friendliest typo to catch, and the message
/// still quotes the id or label with the spelling the author used. This is a suggestion heuristic, not
/// a general-purpose string metric — callers own their own distance budget.
/// </remarks>
internal static class EditDistance
{
    /// <summary>The number of single-character edits between two strings, ignoring case.</summary>
    public static int LevenshteinIgnoreCase(string left, string right)
    {
        var a = left.ToLowerInvariant();
        var b = right.ToLowerInvariant();
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
