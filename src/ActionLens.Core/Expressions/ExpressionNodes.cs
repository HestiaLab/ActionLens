using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// A node in a parsed GitHub Actions expression. Nodes are data only: nothing here knows about
/// three-state logic or about which contexts are available where, which keeps parsing a question of
/// syntax and evaluation a question of meaning.
/// </summary>
public abstract record ExpressionNode(SourceRange Range);

/// <summary>A quoted string, a number, or one of <c>true</c>, <c>false</c>, <c>null</c>.</summary>
internal sealed record LiteralNode(GitHubValue Value, SourceRange Range) : ExpressionNode(Range);

/// <summary>
/// The root of a context path: <c>github</c> in <c>github.event.action</c>. Whether the name is a
/// legal context in this position is an evaluation question, so parsing keeps it as a bare name.
/// </summary>
public sealed record ContextNode(string Name, SourceRange Range) : ExpressionNode(Range);

/// <summary><c>a.b</c>.</summary>
public sealed record MemberAccessNode(ExpressionNode Target, string Name, SourceRange Range) : ExpressionNode(Range);

/// <summary><c>a['b']</c> or <c>a[0]</c>. The index is any expression, because GitHub allows one.</summary>
internal sealed record IndexAccessNode(ExpressionNode Target, ExpressionNode Index, SourceRange Range) : ExpressionNode(Range);

/// <summary>
/// <c>a.*</c>, also written <c>a[*]</c>: the value of one link across every element or property of what
/// comes before it. A quoted <c>a['*']</c> is the ordinary index of a property literally named <c>*</c>,
/// which is why the wildcard is a token rather than an index value.
/// </summary>
/// <remarks>
/// GitHub documents this as <c>.*</c> and its reference corpus
/// (<c>tests/golden/languageservices/op_idx_star.json</c>) pins what it means: an array, always an array,
/// holding one entry per child of the target — and every link after it maps over that array, dropping the
/// children the link does not find rather than reading them as <c>null</c>. That last part is why the node
/// stands on its own instead of being an <see cref="IndexAccessNode"/> with a special index: the shape of
/// the path, not the value of an index, decides whether the following access maps or reads.
/// </remarks>
internal sealed record WildcardAccessNode(ExpressionNode Target, SourceRange Range) : ExpressionNode(Range);

/// <summary><c>!x</c>.</summary>
internal sealed record UnaryNotNode(ExpressionNode Operand, SourceRange Range) : ExpressionNode(Range);

/// <summary>Logical operators, in the order the source writes them.</summary>
internal enum LogicalOperator
{
    And,
    Or,
}

/// <summary>
/// <c>&amp;&amp;</c> and <c>||</c>, flattened into one n-ary node per run of the same operator.
/// </summary>
/// <remarks>
/// GitHub's operators return an operand rather than a boolean (<c>false || 0</c> is the number 0),
/// and a chain of the same operator is left-associative, so a flat list is both smaller and closer to
/// what the runner does than a binary tree.
/// </remarks>
internal sealed record LogicalNode(
    LogicalOperator Operator,
    IReadOnlyList<ExpressionNode> Operands,
    SourceRange Range) : ExpressionNode(Range);

/// <summary>The six comparison operators.</summary>
internal enum ComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

/// <summary><c>a == b</c>, <c>a &lt; b</c> and friends.</summary>
internal sealed record ComparisonNode(
    ComparisonOperator Operator,
    ExpressionNode Left,
    ExpressionNode Right,
    SourceRange Range) : ExpressionNode(Range);

/// <summary>
/// A function call such as <c>contains(github.event.files, 'x')</c>. Function names are
/// case-insensitive in Actions; the node keeps the text as written so messages can quote the author.
/// </summary>
public sealed record FunctionCallNode(
    string Name,
    IReadOnlyList<ExpressionNode> Arguments,
    SourceRange Range) : ExpressionNode(Range);
