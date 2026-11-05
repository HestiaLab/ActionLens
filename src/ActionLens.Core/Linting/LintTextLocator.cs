using ActionLens.Core.Text;

namespace ActionLens.Core.Linting;

/// <summary>
/// Turns an offset inside a value's own text into a position in the file.
/// </summary>
/// <remarks>
/// <para>
/// A single-line value is located exactly: the model holds the character the value starts at, the offset is
/// plain arithmetic, and the finding can highlight the offending token itself.
/// </para>
/// <para>
/// A multi-line one cannot be, and the reason is worth stating because it is the shape <c>run:</c> usually
/// takes. The position the loader records for a block scalar is its <em>header</em> — the <c>|</c> on the
/// <c>run:</c> line — while the text begins on the next line, and the block's own indent is not recorded
/// anywhere. So the line is computed (one for the header, plus the newlines before the match) and the column
/// is left at the start of that line rather than invented from a base the model does not have. The finding
/// quotes the text it objected to, which is what a reader searches for.
/// </para>
/// <para>
/// Two more limits, both inherited from reading the value rather than the file: a <c>run: &gt;</c> folded
/// scalar has fewer newlines in its value than in the text, so its line can land early, and a quoted
/// single-line value's opening quote shifts its column by one. Both are rarer than the plain and literal
/// forms, and neither is worth pretending the model knows more than it does.
/// </para>
/// </remarks>
internal static class LintTextLocator
{
    /// <summary>
    /// The region of the file that <paramref name="matchLength"/> characters starting at
    /// <paramref name="matchIndex"/> of the value occupy, or <paramref name="valueRange"/> itself when the
    /// value has no recorded position to offset from.
    /// </summary>
    internal static SourceRange Locate(SourceRange valueRange, string value, int matchIndex, int matchLength)
    {
        if (!valueRange.IsKnown || matchIndex < 0 || matchIndex >= value.Length)
        {
            return valueRange;
        }

        var newlinesBefore = 0;
        var lineStart = 0;
        for (var index = 0; index < matchIndex; index++)
        {
            if (value[index] != '\n')
            {
                continue;
            }

            newlinesBefore++;
            lineStart = index + 1;
        }

        // More than one line in the recorded range means a block scalar: its start mark is the header line,
        // and the content begins one line later.
        var isBlockScalar = valueRange.End.Line > valueRange.Start.Line;
        var line = valueRange.Start.Line + newlinesBefore + (isBlockScalar ? 1 : 0);
        if (isBlockScalar)
        {
            return new SourceRange(new SourcePosition(line, 1));
        }

        var column = valueRange.Start.Column + (matchIndex - lineStart);
        return new SourceRange(new SourcePosition(line, column), new SourcePosition(line, column + matchLength));
    }
}
