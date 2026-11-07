using ActionLens.Core.Expressions;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Triggers;

public static partial class TriggerEvaluator
{
    // ------------------------------------------------------------------ the verdict

    private static TriggerVerdict EvaluateEvent(TriggerSection triggers, string eventName, TriggerPayload payload)
    {
        var decisions = new List<FilterDecision>();
        var pinned = payload.OccurrenceIsPinnedBy(eventName);
        var source = string.Equals(payload.EventName, eventName, StringComparison.Ordinal)
            ? TriggerEventSource.Payload
            : TriggerEventSource.Caller;
        var trigger = triggers.Find(eventName);
        var range = trigger?.Range ?? OnSectionRange(triggers);

        if (trigger is null)
        {
            decisions.Add(new FilterDecision
            {
                Filter = "on",
                Consulted = false,
                Result = TriKind.False,
                Note = $"'on:' declares no '{eventName}' event, so nothing in this file can be triggered by it.",
            });

            return Verdict(TriValue.False);
        }

        var dimensions = new List<Dimension>();
        Add(dimensions, OccurrenceDimension(trigger, eventName, payload, pinned, decisions));
        Add(dimensions, TypesDimension(trigger, payload, decisions));
        Add(dimensions, RefDimension(trigger, eventName, payload, decisions));
        Add(dimensions, PathDimension(trigger, eventName, payload, decisions));

        if (dimensions.FirstOrDefault(dimension => dimension.Dominates) is { Dominates: true } dominating)
        {
            // GitHub's own limit, not the file's patterns, decides the run here, so no filter answer about
            // this event can be given honestly — not even a "no" from a branch filter.
            return Verdict(dominating.Value);
        }

        return Verdict(And(dimensions, decisions, trigger.Range));

        // Every exit carries the same identity fields; only the answer differs.
        TriggerVerdict Verdict(TriValue runs) => new()
        {
            Event = eventName,
            Source = source,
            PayloadEventName = payload.EventName,
            OccurrencePinnedByPayload = pinned,
            Range = range,
            Runs = runs,
            Decisions = decisions,
        };

        static void Add(List<Dimension> target, Dimension? dimension)
        {
            if (dimension is { } value)
            {
                target.Add(value);
            }
        }
    }

    /// <summary>
    /// <c>schedule</c> and <c>workflow_dispatch</c> have no filter to consult: the answer is whether the
    /// clock arrived or a person pressed the button, which is a supplied fact or it is nothing.
    /// </summary>
    private static Dimension? OccurrenceDimension(
        EventTrigger trigger,
        string eventName,
        TriggerPayload payload,
        bool pinned,
        List<FilterDecision> decisions)
    {
        if (eventName is not ("workflow_dispatch" or "schedule"))
        {
            return null;
        }

        var isDispatch = eventName == "workflow_dispatch";
        var declared = isDispatch
            ? (trigger.Inputs.Count == 0 ? null : string.Join(", ", trigger.Inputs.Keys))
            : trigger.Cron.Count > 0 ? trigger.Cron[0].Text : null;
        var declaredRange = !isDispatch && trigger.Cron.Count > 0
            ? trigger.Cron[0].Range
            : trigger.Range;

        var supplied = isDispatch
            ? pinned || payload.Inputs is { Count: > 0 }
            : pinned;

        if (supplied)
        {
            var names = isDispatch && payload.Inputs is { } inputs && inputs.Count > 0
                ? string.Join(", ", inputs.Keys.OrderBy(key => key, StringComparer.Ordinal))
                : null;
            decisions.Add(new FilterDecision
            {
                Filter = isDispatch ? "inputs" : "schedule",
                Result = TriKind.True,
                Value = names,
                Pattern = declared,
                PatternRange = declaredRange,
                Note = isDispatch
                    ? names is null
                        ? "The payload records a 'workflow_dispatch' event, so a person did request this run."
                        : $"The payload carries the dispatch inputs ({names}), so a person did request this run."
                    : "The payload records a scheduled event, so the schedule fired; which cron entry let it through is GitHub's clock to say.",
            });
            return new Dimension(TriValue.True, false);
        }

        var reason = isDispatch
            ? new UnknownReason(
                UnknownReasonKind.RuntimeFunction,
                "A 'workflow_dispatch' run is a person's decision: the payload supplies no 'inputs' object and does not record a dispatch, so ActionLens cannot say the run was requested.",
                declaredRange)
            : new UnknownReason(
                UnknownReasonKind.RuntimeFunction,
                declared is null
                    ? "The 'schedule' event declares no cron entry, and whether a schedule fires is GitHub's clock to say; ActionLens reads none."
                    : $"The cron entry '{declared}' can only be settled by a clock, which ActionLens does not read: supply a payload whose 'event_name' is 'schedule' to ask about a run that already happened.",
                declaredRange);

        // The clock and the person are both supplied facts, so a caller can pin them; naming the missing
        // input is what makes the unknown actionable rather than a shrug.
        decisions.Add(UnknownDecision(isDispatch ? "inputs" : "schedule", reason, declared, declaredRange));
        return new Dimension(TriValue.Unknown(reason), false);
    }

    private static Dimension? TypesDimension(EventTrigger trigger, TriggerPayload payload, List<FilterDecision> decisions)
    {
        if (trigger.Types.Count == 0)
        {
            return null;
        }

        var first = trigger.Types[0];
        if (payload.Action is not { } action)
        {
            var reason = new UnknownReason(
                UnknownReasonKind.ValueMissing,
                $"The payload does not say which activity type the event was. '{first.Text}' and the rest of 'types' cannot be decided without 'action' in the event payload.",
                first.Range);
            decisions.Add(UnknownDecision("types", reason, Join(trigger.Types), first.Range, FilterListMode.Include));
            return new Dimension(TriValue.Unknown(reason), false);
        }

        // Activity types are exact names, not glob patterns: nothing in the sources gives 'types' a wildcard.
        WorkflowValue? matched = null;
        foreach (var value in trigger.Types)
        {
            if (string.Equals(value.Text, action, StringComparison.Ordinal))
            {
                matched = value;
                break;
            }
        }

        decisions.Add(new FilterDecision
        {
            Filter = "types",
            Mode = FilterListMode.Include,
            Result = matched is null ? TriKind.False : TriKind.True,
            Value = action,
            ValuesTested = 1,
            Pattern = matched?.Text ?? Join(trigger.Types),
            PatternRange = (matched ?? first).Range,
            Note = matched is null
                ? $"'{action}' is none of the declared activity types ({Join(trigger.Types)}), so this event never reaches the jobs."
                : null,
        });
        return new Dimension(matched is not null, false);
    }

    /// <summary>
    /// The ref half of the filter: branches and tags, against the ref the event touched — which for a pull
    /// request is the <em>base</em> branch, and for a push is the ref type that decides which key applies.
    /// </summary>
    private static Dimension? RefDimension(
        EventTrigger trigger,
        string eventName,
        TriggerPayload payload,
        List<FilterDecision> decisions)
    {
        var hasBranches = trigger.Branches.Count > 0 || trigger.BranchesIgnore.Count > 0;
        var hasTags = trigger.Tags.Count > 0 || trigger.TagsIgnore.Count > 0;
        if (!hasBranches && !hasTags)
        {
            return null;
        }

        if (eventName is "pull_request" or "pull_request_target")
        {
            if (hasTags)
            {
                var tagValues = trigger.Tags.Count > 0 ? trigger.Tags : trigger.TagsIgnore;
                decisions.Add(new FilterDecision
                {
                    Filter = trigger.Tags.Count > 0 ? "tags" : "tags-ignore",
                    Consulted = false,
                    Result = TriKind.True,
                    Pattern = Join(tagValues),
                    PatternRange = tagValues[0].Range,
                    Note = "A pull request touches a branch, not a tag, so GitHub never consults this key. It is reported as a schema error above.",
                });
            }

            return hasBranches
                ? ListDimension(
                    "branches",
                    trigger.Branches,
                    trigger.BranchesIgnore,
                    payload.BaseRef,
                    "the pull request's base branch",
                    "the base branch ('base_ref', or 'pull_request.base.ref'), which is what 'branches' filters for this event; the head branch needs a condition on github.head_ref",
                    decisions)
                : new Dimension(TriValue.True, false);
        }

        if (eventName == "push")
        {
            var kind = RefTypeOf(payload);
            if (kind is null)
            {
                // An author may declare only `branches-ignore:` (or only `tags-ignore:`), so the plain
                // list is empty even though the filter exists. Reading values[0] from it used to throw.
                var values = hasBranches
                    ? DeclaredValues(trigger.Branches, trigger.BranchesIgnore)
                    : DeclaredValues(trigger.Tags, trigger.TagsIgnore);
                var patternRange = FilterRange(values, trigger);
                var kindName = hasBranches ? "branches" : "tags";
                var reason = new UnknownReason(
                    UnknownReasonKind.ValueMissing,
                    $"The payload gives no 'ref', so ActionLens cannot tell whether this push touched a branch or a tag, and the '{kindName}' filter reads only one of the two.",
                    patternRange);
                decisions.Add(UnknownDecision(hasBranches ? "branches" : "tags", reason, Join(values), patternRange));
                return new Dimension(TriValue.Unknown(reason), false);
            }

            var isBranch = kind == "branch";
            if (!(isBranch ? hasBranches : hasTags))
            {
                // Documented: a push that declares only one kind of ref never runs for the other kind.
                var other = isBranch ? (trigger.Tags, trigger.TagsIgnore) : (trigger.Branches, trigger.BranchesIgnore);
                var declared = DeclaredValues(other.Item1, other.Item2);
                decisions.Add(new FilterDecision
                {
                    Filter = isBranch ? "branches" : "tags",
                    Consulted = false,
                    Result = TriKind.False,
                    Value = payload.RefName,
                    Pattern = Join(declared),
                    PatternRange = FilterRange(declared, trigger),
                    Note = isBranch
                        ? $"'push' declares only {Join(declared)} under tags/tags-ignore, and a push that defines only tag filters does not run for branch pushes."
                        : "'push' declares only branches/branches-ignore, so a tag push never runs.",
                });
                return new Dimension(TriValue.False, false);
            }

            return ListDimension(
                isBranch ? "branches" : "tags",
                isBranch ? trigger.Branches : trigger.Tags,
                isBranch ? trigger.BranchesIgnore : trigger.TagsIgnore,
                payload.RefName,
                isBranch ? "branch" : "tag",
                isBranch ? "the pushed branch name ('ref')" : "the pushed tag name ('ref')",
                decisions);
        }

        // Any other event that carries a ref (workflow_run and friends) is filtered the same way against
        // the ref the payload names; an event that carries none leaves it unknown rather than assumed.
        var branchSide = hasBranches;
        return ListDimension(
            branchSide ? "branches" : "tags",
            branchSide ? trigger.Branches : trigger.Tags,
            branchSide ? trigger.BranchesIgnore : trigger.TagsIgnore,
            payload.RefName,
            "ref",
            "the ref the event carried ('ref')",
            decisions);
    }

    /// <summary>
    /// The path half. Not consulted for a tag push (documented), and never answered from a webhook's capped
    /// per-commit lists, which are not the diff GitHub filtered against.
    /// </summary>
    private static Dimension? PathDimension(
        EventTrigger trigger,
        string eventName,
        TriggerPayload payload,
        List<FilterDecision> decisions)
    {
        var hasPaths = trigger.Paths.Count > 0;
        var hasIgnore = trigger.PathsIgnore.Count > 0;
        if (!hasPaths && !hasIgnore)
        {
            return null;
        }

        var values = hasPaths ? trigger.Paths : trigger.PathsIgnore;
        var mode = hasPaths ? FilterListMode.Include : FilterListMode.Ignore;
        var key = hasPaths ? "paths" : "paths-ignore";
        var first = values[0];

        if (eventName == "push" && RefTypeOf(payload) == "tag")
        {
            decisions.Add(new FilterDecision
            {
                Filter = key,
                Consulted = false,
                Mode = mode,
                Result = TriKind.True,
                Pattern = Join(values),
                PatternRange = first.Range,
                Note = "GitHub does not evaluate path filters for a tag push, so this key says nothing about the event.",
            });
            return new Dimension(TriValue.True, false);
        }

        if (payload.ForcesAnUnfilteredRun)
        {
            return Forced(decisions, key, mode, first, values, payload,
                $"GitHub documents that the workflow will always run when a push carries more than {TriggerPayload.CommitsThatForceAnUnfilteredRun:N0} commits or its diff generation times out, whatever the filters say, so no filter answer about '{key}' can be given for this event.");
        }

        if (payload.DiffTruncated == true)
        {
            return Forced(decisions, key, mode, first, values, payload,
                $"The diff held more than {payload.DiffFileLimit:N0} files{(payload.GitHubEnterprise == true ? " (GitHub Enterprise Server's ceiling)" : " (the GitHub-hosted ceiling)")}, and GitHub reads only the first {payload.DiffFileLimit:N0} of them. Whether the matching file sat past that line is not in the payload, and when it does the workflow does not run however the '{key}' patterns read.");
        }

        if (payload.ChangedFiles is not { } files)
        {
            var stated = payload.ChangedFileCount is { } count
                ? $" The payload gives a count ({count:N0}) but no names."
                : string.Empty;
            var reason = new UnknownReason(
                UnknownReasonKind.ValueMissing,
                $"The payload supplies no list of changed files, so '{key}' cannot be decided.{stated} Add \"changed_files\": [\"path/one\"] to the payload.",
                first.Range);
            decisions.Add(UnknownDecision(key, reason, Join(values), first.Range, mode));
            return new Dimension(TriValue.Unknown(reason), false);
        }

        if (files.Count == 0)
        {
            var include = mode == FilterListMode.Include;
            decisions.Add(new FilterDecision
            {
                Filter = key,
                Mode = mode,
                Result = include ? TriKind.False : TriKind.True,
                ValuesTested = 0,
                Pattern = Join(values),
                PatternRange = first.Range,
                Note = include
                    ? "The diff changed no files at all, and GitHub does not run a path-filtered workflow when nothing changed."
                    : "The diff changed no files, so the 'paths-ignore' list had nothing to exclude. ActionLens reads that as no constraint; the docs' 'if there are no files changed, the workflow will not run' sentence is stated for the include case, and the gap is recorded in the doc's ambiguities section.",
            });
            return new Dimension(include ? TriValue.False : TriValue.True, false);
        }

        var compiled = FilterMatcher.CompileAll(values);
        var match = FilterMatcher.MatchAnyFile(compiled, files, mode);
        var deciding = match.DecidingPattern ?? compiled[0];
        if (payload.ChangedFilesFromCommits)
        {
            var partial = new UnknownReason(
                UnknownReasonKind.RuntimeFunction,
                $"The changed-files list was assembled from the payload's per-commit arrays, which a webhook body caps at 20 commits, so ActionLens cannot rule out the cases where GitHub runs the workflow whatever '{key}' says. On the partial list of {files.Count:N0} files the match was {(match.Satisfied ? "found" : "not found")}.",
                deciding.Range);
            decisions.Add(UnknownDecision(key, partial, deciding.RawText, deciding.Range, mode) with
            {
                Value = match.Value,
                ValuesTested = files.Count,
            });
            return new Dimension(TriValue.Unknown(partial), Dominates: true);
        }

        decisions.Add(new FilterDecision
        {
            Filter = key,
            Mode = mode,
            Result = match.Satisfied ? TriKind.True : TriKind.False,
            Value = match.Value,
            ValuesTested = files.Count,
            Pattern = deciding.RawText,
            PatternRange = deciding.Range,
            Note = match.Satisfied
                ? null
                : $"None of the {files.Count:N0} changed files satisfies the '{key}' list, so this filter stopped the run.",
        });
        return new Dimension(match.Satisfied, false);
    }

    private static Dimension Forced(
        List<FilterDecision> decisions,
        string key,
        FilterListMode mode,
        WorkflowValue first,
        IReadOnlyList<WorkflowValue> values,
        TriggerPayload payload,
        string message)
    {
        var reason = new UnknownReason(UnknownReasonKind.RuntimeFunction, message, first.Range);
        decisions.Add(UnknownDecision(key, reason, Join(values), first.Range, mode) with
        {
            ValuesTested = payload.ChangedFiles?.Count ?? payload.ChangedFileCount ?? 0,
        });
        return new Dimension(TriValue.Unknown(reason), Dominates: true);
    }

    /// <summary>
    /// One include/exclude pair matched against one value, with the pattern that decided it recorded with
    /// its position — which is the "which filter said no" the module exists to answer.
    /// </summary>
    private static Dimension ListDimension(
        string key,
        IReadOnlyList<WorkflowValue> include,
        IReadOnlyList<WorkflowValue> ignore,
        string? value,
        string kindLabel,
        string missingHint,
        List<FilterDecision> decisions)
    {
        var usingIgnore = include.Count == 0;
        var patterns = usingIgnore ? ignore : include;
        var mode = usingIgnore ? FilterListMode.Ignore : FilterListMode.Include;
        var name = usingIgnore ? $"{key}-ignore" : key;
        var first = patterns[0];

        if (value is null)
        {
            var reason = new UnknownReason(
                UnknownReasonKind.ValueMissing,
                $"The payload names no {kindLabel} to match against. '{name}' cannot be decided without {missingHint}.",
                first.Range);
            decisions.Add(UnknownDecision(name, reason, Join(patterns), first.Range, mode));
            return new Dimension(TriValue.Unknown(reason), false);
        }

        var match = FilterMatcher.Match(FilterMatcher.CompileAll(patterns), value, mode);
        var deciding = match.DecidingPattern;
        decisions.Add(new FilterDecision
        {
            Filter = name,
            Mode = mode,
            Result = match.Satisfied ? TriKind.True : TriKind.False,
            Value = value,
            ValuesTested = 1,
            Pattern = deciding?.RawText ?? Join(patterns),
            PatternRange = deciding?.Range ?? first.Range,
            Note = deciding switch
            {
                null when mode == FilterListMode.Include => $"'{value}' matches no pattern in '{name}', so the include list did not admit it.",
                null => $"'{value}' matches no pattern in '{name}', so the exclude list let it through.",
                { Negated: true } => $"'{deciding.RawText}' matched '{value}' and negates it, so this list excluded it.",
                _ => $"'{deciding.RawText}' matched '{value}'.",
            },
        });
        return new Dimension(match.Satisfied, false);
    }
}
