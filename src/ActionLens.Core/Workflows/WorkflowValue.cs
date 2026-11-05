using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Workflows;

/// <summary>
/// A value taken from the workflow text: the YAML type it resolved to, the text as the author wrote
/// it, and where it came from.
/// </summary>
/// <remarks>
/// The resolved type is kept next to the raw text on purpose. <c>if: false</c> and <c>if: 'false'</c>
/// are the same four characters but behave differently in Actions — one arrives as a boolean, the
/// other as a non-empty string — and keeping only one of the two would make that invisible.
/// </remarks>
public readonly record struct WorkflowValue(GitHubValue Value, string Text, SourceRange Range)
{
    public bool IsString => Value.Kind == GitHubValueKind.String;
}
