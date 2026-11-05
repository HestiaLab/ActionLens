using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;

namespace ActionLens.Core.Linting;

/// <summary>
/// What a rule says about one place in the file: the sentence, the position, the fix, and the workflow
/// object the finding belongs to. The rule's id, severity and help link are not here because they come from
/// <see cref="LintRuleDescriptor"/>, which the registry stamps onto every finding a rule returns — that is
/// what keeps a rule from being able to report itself under someone else's id.
/// </summary>
public sealed record LintFinding
{
    /// <summary>The value of <see cref="PromotesEngineFindingAt"/> for a finding that stands on its own.</summary>
    public const int NotAPromotion = -1;

    public required string Message { get; init; }

    public required SourceRange Range { get; init; }

    public required string Suggestion { get; init; }

    /// <summary>The job id, or the step label, the finding belongs to.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// The index in <see cref="LintContext.EngineFindings"/> of the stage finding this one takes over, or
    /// <see cref="NotAPromotion"/>. AL004 and AL006 use it: the expression and graph layers already detected
    /// the problem and already know where it is, so a lint rule that scanned again would show the reader two
    /// findings for one problem. See <see cref="Promoting"/>.
    /// </summary>
    public int PromotesEngineFindingAt { get; init; } = NotAPromotion;

    /// <summary>
    /// A finding that adopts a stage finding: it keeps that finding's message, position and scope, because
    /// the engine's own explanation is the better sentence, and adds the rule id, help link and fix.
    /// </summary>
    public static LintFinding Promoting(int engineIndex, Diagnostic promoted, string suggestion) => new()
    {
        Message = promoted.Message,
        Range = promoted.Range,
        Suggestion = suggestion,
        Scope = promoted.Scope,
        PromotesEngineFindingAt = engineIndex,
    };
}
