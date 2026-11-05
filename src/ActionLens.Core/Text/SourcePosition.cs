namespace ActionLens.Core.Text;

/// <summary>
/// A 1-based line/column location inside the workflow text. Line and column are both 1-based so
/// that they match what an editor shows; <see cref="Unknown"/> (0, 0) means "we do not know".
/// </summary>
public readonly record struct SourcePosition(int Line, int Column)
{
    public static SourcePosition Unknown { get; } = new(0, 0);

    public bool IsKnown => Line > 0;

    public override string ToString() => IsKnown ? $"{Line}:{Column}" : "?";
}
