using ActionLens.Core.Diagnostics;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Triggers;

public static partial class TriggerEvaluator
{
    /// <summary>
    /// The findings an <c>on:</c> section carries whatever event is asked about, because each of them is a
    /// workflow GitHub will not accept.
    /// </summary>
    private static IEnumerable<Diagnostic> Validate(TriggerSection triggers)
    {
        foreach (var trigger in triggers.Events.Values)
        {
            foreach (var finding in UnsupportedKeys(trigger))
            {
                yield return finding;
            }

            foreach (var finding in MutuallyExclusivePairs(trigger))
            {
                yield return finding;
            }

            foreach (var finding in PatternFindings(trigger))
            {
                yield return finding;
            }
        }
    }

    private static IEnumerable<Diagnostic> UnsupportedKeys(EventTrigger trigger)
    {
        // A short table on purpose: only the events the documentation settles get a claim about the keys
        // they accept — 'workflow_dispatch' takes only inputs, 'schedule' takes only cron entries, and a
        // pull request has no tags to match. Anything else an author writes is read as a filter and
        // evaluated, not declared illegal, so ActionLens never rejects a file the sources do not reject.
        var (keys, phrase) = trigger.Name switch
        {
            "workflow_dispatch" => (DispatchAndCallKeys, "takes only 'inputs'"),
            "workflow_call" => (DispatchAndCallKeys, "declares inputs, secrets and outputs, and no ref or file filters"),
            "schedule" => (ScheduleKeys, "takes only cron entries"),
            "pull_request" or "pull_request_target" => (TagKeys, "matches branches and files, never tags"),
            _ => (EmptyKeys, string.Empty),
        };

        if (phrase.Length == 0)
        {
            return [];
        }

        return Reject(trigger, keys, phrase);
    }

    private static readonly string[] EmptyKeys = [];

    private static readonly string[] DispatchAndCallKeys =
    [
        "branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore", "types", "schedule",
    ];

    private static readonly string[] ScheduleKeys =
    [
        "branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore", "types",
    ];

    private static readonly string[] TagKeys = ["tags", "tags-ignore"];

    private static IEnumerable<Diagnostic> Reject(EventTrigger trigger, string[] keys, string phrase)
    {
        foreach (var key in keys)
        {
            var values = ValuesUnder(trigger, key);
            if (values.Count == 0)
            {
                continue;
            }

            yield return new Diagnostic
            {
                Code = DiagnosticCodes.TriggerFilterUnsupported,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Schema,
                Message = $"'{trigger.Name}' {phrase}, so '{key}' is a key GitHub rejects under it.",
                Range = values[0].Range,
                Suggestion = $"Remove '{key}' from '{trigger.Name}:'.",
                Scope = trigger.Name,
            };
        }
    }

    private static IEnumerable<Diagnostic> MutuallyExclusivePairs(EventTrigger trigger)
    {
        foreach (var (include, ignore) in ExclusivePairs)
        {
            var first = ValuesUnder(trigger, include);
            var second = ValuesUnder(trigger, ignore);
            if (first.Count == 0 || second.Count == 0)
            {
                continue;
            }

            // The one written later in the text is the line the author has to delete, and which of the two
            // that is is read off the positions rather than off the order the model happens to store.
            var dropped = ComesBefore(first[0].Range, second[0].Range) ? second : first;
            yield return new Diagnostic
            {
                Code = DiagnosticCodes.TriggerFilterConflict,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Schema,
                Message = $"'{trigger.Name}' uses both '{include}' and '{ignore}', and GitHub does not allow both for the same event.",
                Range = dropped[0].Range,
                Suggestion = $"Keep one of the two. To include some patterns and exclude others, keep '{include}' and write the exclusions as '!'-patterns under it.",
                Scope = trigger.Name,
            };
        }
    }

    private static IEnumerable<Diagnostic> PatternFindings(EventTrigger trigger)
    {
        foreach (var (key, values) in GlobFilters(trigger))
        {
            if (values.Count == 0)
            {
                continue;
            }

            var patterns = FilterMatcher.CompileAll(values);
            var hasPositive = patterns.Any(pattern => !pattern.Negated);
            foreach (var pattern in patterns)
            {
                if (pattern.Error is { } error)
                {
                    yield return new Diagnostic
                    {
                        Code = DiagnosticCodes.TriggerPatternInvalid,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Schema,
                        Message = $"'{pattern.RawText}' under '{key}' is not a valid filter pattern: {error}.",
                        Range = pattern.Range,
                        Suggestion = "Escape a special character with a backslash, or quote the pattern; '[]' holds only a-z, A-Z and 0-9.",
                        Scope = trigger.Name,
                    };
                    continue;
                }

                // Documented: a '!' pattern needs at least one un-prefixed pattern beside it, and a list of
                // nothing but exclusions is the shape that note exists to forbid.
                if (pattern.Negated && !hasPositive)
                {
                    yield return new Diagnostic
                    {
                        Code = DiagnosticCodes.TriggerPatternInvalid,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Schema,
                        Message = $"'{key}' under '{trigger.Name}' contains only negated patterns, and a '!' pattern requires at least one un-prefixed pattern alongside it.",
                        Range = pattern.Range,
                        Suggestion = $"Add a pattern that says what to run on, for example '{key}:' with a '\"**\"' entry above the negation.",
                        Scope = trigger.Name,
                    };
                }
            }
        }
    }
}
