using ActionLens.Core.Expressions;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Triggers;

public static partial class TriggerEvaluator
{
    // ------------------------------------------------------------------ helpers

    /// <summary>One filter's contribution to the AND, and whether it settles the answer on its own.</summary>
    private readonly record struct Dimension(TriValue Value, bool Dominates);

    /// <summary>
    /// Kleene AND over the consulted filters, matching the table in <c>ARCHITECTURE.md</c>: one definite no
    /// is enough to say no, and an unknown survives only where nothing said no.
    /// </summary>
    private static TriValue And(IReadOnlyList<Dimension> dimensions, IReadOnlyList<FilterDecision> decisions, SourceRange range)
    {
        if (dimensions.Any(dimension => dimension.Value.Kind == TriKind.False))
        {
            return TriValue.False;
        }

        var unknowns = dimensions.Where(dimension => dimension.Value.Kind == TriKind.Unknown).ToArray();
        if (unknowns.Length == 0)
        {
            return TriValue.True;
        }

        if (unknowns.Length == 1 && dimensions.Count == 1 && unknowns[0].Value.Reason is { } single)
        {
            // One filter, one gap: naming the operator that combined nothing would only add a level.
            return TriValue.Unknown(single);
        }

        var causes = new List<UnknownReason>(unknowns.Length);
        foreach (var dimension in unknowns)
        {
            if (dimension.Value.Reason is { } cause)
            {
                causes.Add(cause);
            }
        }

        var labels = string.Join(" && ", decisions
            .Where(decision => decision.Consulted && decision.Result == TriKind.Unknown)
            .Select(decision => decision.Filter));
        return TriValue.Unknown(new UnknownReason(
            UnknownReasonKind.Propagated,
            $"'{labels}' cannot be decided together.",
            range,
            causes));
    }

    private static string Join(IReadOnlyList<WorkflowValue> values) =>
        string.Join(", ", values.Select(value => value.Text));

    /// <summary>
    /// The patterns a filter key actually declares. An author is allowed to write only the
    /// <c>-ignore</c> half, so the include list is not necessarily the one holding the values.
    /// </summary>
    private static IReadOnlyList<WorkflowValue> DeclaredValues(
        IReadOnlyList<WorkflowValue> include, IReadOnlyList<WorkflowValue> ignore) =>
        include.Count > 0 ? include : ignore;

    /// <summary>Where a finding about a filter points, even when neither of its lists has an entry.</summary>
    private static SourceRange FilterRange(IReadOnlyList<WorkflowValue> values, EventTrigger trigger) =>
        values.Count > 0 ? values[0].Range : trigger.Range;

    private static string? RefTypeOf(TriggerPayload payload) => payload.RefType switch
    {
        "branch" => "branch",
        "tag" => "tag",
        _ => null,
    };

    private static bool ComesBefore(SourceRange first, SourceRange second) =>
        first.Start.Line < second.Start.Line
        || (first.Start.Line == second.Start.Line && first.Start.Column <= second.Start.Column);

    /// <summary>
    /// Where the <c>on:</c> key is, for a finding about an event the file does not declare and so has no
    /// event range of its own.
    /// </summary>
    private static SourceRange OnSectionRange(TriggerSection triggers)
    {
        foreach (var field in triggers.Sources.Fields)
        {
            var range = triggers.Sources.Get(field);
            if (range.IsKnown)
            {
                return range;
            }
        }

        return triggers.Events.Values.FirstOrDefault()?.Range ?? SourceRange.Unknown;
    }

    private static IReadOnlyList<WorkflowValue> ValuesUnder(EventTrigger trigger, string key) => key switch
    {
        "branches" => trigger.Branches,
        "branches-ignore" => trigger.BranchesIgnore,
        "tags" => trigger.Tags,
        "tags-ignore" => trigger.TagsIgnore,
        "paths" => trigger.Paths,
        "paths-ignore" => trigger.PathsIgnore,
        "types" => trigger.Types,
        "schedule" => trigger.Cron,
        _ => Array.Empty<WorkflowValue>(),
    };

    /// <summary>
    /// The six ref and file lists of one event. <c>types</c> and cron entries are deliberately absent:
    /// they are exact names and cron text, not filter patterns, so the pattern grammar does not apply to
    /// them and claiming it did would reject files GitHub accepts.
    /// </summary>
    private static (string Key, IReadOnlyList<WorkflowValue> Values)[] GlobFilters(EventTrigger trigger) =>
    [
        ("branches", trigger.Branches),
        ("branches-ignore", trigger.BranchesIgnore),
        ("tags", trigger.Tags),
        ("tags-ignore", trigger.TagsIgnore),
        ("paths", trigger.Paths),
        ("paths-ignore", trigger.PathsIgnore),
    ];
}
