namespace ActionLens.Core.Text;

/// <summary>
/// A half-open-ish region of the workflow text. <see cref="End"/> is optional because the YAML
/// representation model does not always carry a meaningful end mark for a node.
/// </summary>
public readonly record struct SourceRange(SourcePosition Start, SourcePosition End)
{
    public SourceRange(SourcePosition start)
        : this(start, start)
    {
    }

    /// <summary>Standing for "this finding has no position", which is a bug rather than an answer.
    /// </summary>
    public static SourceRange Unknown { get; } = new(SourcePosition.Unknown, SourcePosition.Unknown);

    public bool IsKnown => Start.IsKnown;

    public override string ToString() =>
        !IsKnown
            ? "?"
            : End == Start
                ? Start.ToString()
                : $"{Start}..{End}";
}
