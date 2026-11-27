using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Expressions;

/// <summary>
/// The answer to one <c>if:</c> condition, with everything that explains it.
/// </summary>
internal sealed record ConditionOutcome
{
    public required TriValue Value { get; init; }

    /// <summary>Findings produced while reading the condition, including contexts used where GitHub forbids them.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();

    /// <summary>The tree that was evaluated, after the implicit <c>success()</c> was applied.</summary>
    public ExpressionNode? Root { get; init; }

    /// <summary>What the author wrote, so the UI can show the condition next to its answer.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>True when ActionLens had to add the implicit status check rather than the author writing one.</summary>
    public bool ImplicitSuccessApplied { get; init; }
}

/// <summary>
/// Turns a workflow's <c>if:</c> text into a three-state answer.
/// </summary>
/// <remarks>
/// The one piece of behaviour that is easy to miss is the implicit status check. GitHub evaluates a
/// condition that mentions no status function as <c>success() &amp;&amp; (condition)</c> — confirmed both
/// by the documented runtime log line (<c>Evaluating: (success() &amp;&amp; ((github.repository ==
/// '...')))</c>) and by the reference converter — so a job whose <c>if:</c> is merely
/// <c>github.ref == 'refs/heads/main'</c> still does not run when an ancestor failed. Evaluating the
/// bare condition instead would report jobs as running that GitHub would skip.
/// </remarks>
internal sealed class ConditionEvaluator
{
    private static readonly string[] StatusFunctionNames = ["success", "failure", "always", "cancelled"];

    private readonly ExpressionEvaluator _evaluator;

    public ConditionEvaluator(IContextProvider contexts)
    {
        _evaluator = new ExpressionEvaluator(contexts);
    }

    public ConditionOutcome Evaluate(WorkflowValue? condition)
    {
        var range = condition?.Range ?? SourceRange.Unknown;
        var text = condition?.Text?.Trim() ?? string.Empty;

        if (condition is null || text.Length == 0)
        {
            // No condition at all is GitHub's implicit success().
            var implicitRoot = BuildImplicitRoot(null, range);
            return new ConditionOutcome
            {
                Value = _evaluator.Evaluate(implicitRoot).ToCondition(),
                Text = text,
                Root = implicitRoot,
                ImplicitSuccessApplied = true,
            };
        }

        // Token positions are measured inside the condition's own text. Passing its file origin makes
        // every node in the unknown-reason trace point at a real place in the workflow; without it the
        // whole trace collapsed onto 1:1. Diagnostics still name the whole value (docs/limitations.md).
        var parsed = ExpressionParser.ParseCondition(text, range.Start);
        var diagnostics = parsed.Diagnostics
            .Select(diagnostic => diagnostic with { Range = range })
            .ToList();

        if (parsed.Root is null)
        {
            return new ConditionOutcome
            {
                Value = TriValue.Unknown(new UnknownReason(
                    UnknownReasonKind.InvalidSyntax,
                    diagnostics.Count > 0 ? diagnostics[0].Message : "The condition could not be parsed.",
                    range)),
                Diagnostics = diagnostics,
                Text = text,
            };
        }

        var containsStatus = HasStatusFunction(parsed.Root);
        var root = containsStatus ? parsed.Root : BuildImplicitRoot(parsed.Root, range);
        var value = _evaluator.Evaluate(root).ToCondition();

        // A context that is not available in this position is a schema error GitHub would reject the
        // workflow for, not merely a gap in what ActionLens can know, so it is reported both ways: as a
        // reason in the trace and as a finding in the problem list.
        foreach (var unavailable in CollectUnavailable(value.Reason))
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.ContextNotAvailable,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Expression,
                Message = unavailable.Message,
                Range = range,
                Symbol = unavailable.Symbol,
                Suggestion = "Move the check somewhere this context is available, or compare a value the condition can see.",
            });
        }

        return new ConditionOutcome
        {
            Value = value,
            Diagnostics = diagnostics,
            Root = root,
            Text = text,
            ImplicitSuccessApplied = !containsStatus,
        };
    }

    private static ExpressionNode BuildImplicitRoot(ExpressionNode? condition, SourceRange range) =>
        condition is null
            ? new FunctionCallNode("success", Array.Empty<ExpressionNode>(), range)
            : new LogicalNode(
                LogicalOperator.And,
                new ExpressionNode[] { new FunctionCallNode("success", Array.Empty<ExpressionNode>(), range), condition },
                range);

    /// <summary>
    /// Whether the condition mentions a status function anywhere in its tree. GitHub's own converter
    /// walks the whole tree, so a status function inside a function argument or in an untaken branch of
    /// an <c>&amp;&amp;</c> still suppresses the implicit wrap.
    /// </summary>
    private static bool HasStatusFunction(ExpressionNode node) => node switch
    {
        FunctionCallNode call => StatusFunctionNames.Contains(call.Name, StringComparer.OrdinalIgnoreCase)
            || call.Arguments.Any(HasStatusFunction),
        MemberAccessNode member => HasStatusFunction(member.Target),
        IndexAccessNode index => HasStatusFunction(index.Target) || HasStatusFunction(index.Index),
        WildcardAccessNode wildcard => HasStatusFunction(wildcard.Target),
        UnaryNotNode notNode => HasStatusFunction(notNode.Operand),
        LogicalNode logical => logical.Operands.Any(HasStatusFunction),
        ComparisonNode comparison => HasStatusFunction(comparison.Left) || HasStatusFunction(comparison.Right),
        _ => false,
    };

    private static IEnumerable<UnknownReason> CollectUnavailable(UnknownReason? reason)
    {
        if (reason is null)
        {
            return Array.Empty<UnknownReason>();
        }

        return Flatten(reason).Where(node => node.Kind == UnknownReasonKind.ContextNotAvailable);
    }

    private static IEnumerable<UnknownReason> Flatten(UnknownReason reason)
    {
        yield return reason;
        foreach (var cause in reason.Causes)
        {
            foreach (var nested in Flatten(cause))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Exposes the evaluator for callers that need the value of a non-condition expression.</summary>
    public Evaluated EvaluateValue(ExpressionNode node) => _evaluator.Evaluate(node);
}
