using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Text;

namespace ActionLens.Core.Triggers;

/// <summary>
/// Everything one trigger analysis produced: the findings about the <c>on:</c> section, and — when an
/// event was asked about — the answer for it.
/// </summary>
/// <remarks>
/// The findings come back whether or not an event was selected, because a workflow that declares
/// <c>branches</c> and <c>branches-ignore</c> for one event is a workflow GitHub rejects no matter which
/// event a reader is asking about. The verdict, by contrast, only exists once somebody names an event:
/// "did this trigger fire" has no answer for an event nobody chose.
/// </remarks>
public sealed record TriggerEvaluation
{
    /// <summary>Findings from the <c>on:</c> section, in the order the events were written.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>The answer for the selected event; null when no event was selected.</summary>
    public TriggerVerdict? Verdict { get; init; }
}

/// <summary>Where the event a verdict is about came from.</summary>
public enum TriggerEventSource
{
    /// <summary>The payload's own <c>event_name</c>, which is how a recorded run says what triggered it.</summary>
    Payload,

    /// <summary>A caller that named the event itself, before any payload exists — the UI's event picker.</summary>
    Caller,
}

/// <summary>
/// The answer to "did this workflow run for this event, and if not, which filter said no".
/// </summary>
public sealed record TriggerVerdict
{
    /// <summary>The event the verdict is about.</summary>
    public required string Event { get; init; }

    /// <summary>Whether the payload named it or the caller picked it.</summary>
    public required TriggerEventSource Source { get; init; }

    /// <summary><c>github.event_name</c> as written, kept so a reader can see a selection that disagrees with the payload.</summary>
    public string? PayloadEventName { get; init; }

    /// <summary>True when the payload states this event, so the event happening is a supplied fact rather than a question.</summary>
    public required bool OccurrencePinnedByPayload { get; init; }

    /// <summary>Where the event sits in the YAML: the event key's range, or the <c>on:</c> section's for an event the file does not declare.</summary>
    public SourceRange Range { get; init; }

    /// <summary>
    /// Did the workflow run. An <c>unknown</c> here always carries the reason, and the reason is set beside
    /// the decision that forced it rather than only here, because several filters can each be unanswerable
    /// for a different missing input.
    /// </summary>
    public TriValue Runs { get; init; }

    /// <summary>One entry per filter consulted, in the order consulted, naming the pattern that decided it.</summary>
    public required IReadOnlyList<FilterDecision> Decisions { get; init; }

    /// <summary>The filter that said no, when one did: the sentence a reader is looking for.</summary>
    public FilterDecision? SaidNo =>
        Decisions.FirstOrDefault(decision => decision.Consulted && decision.Result == TriKind.False);

    /// <summary>The filter that could not be answered, when the answer is unknown.</summary>
    public FilterDecision? SaidUnknown =>
        Decisions.FirstOrDefault(decision => decision.Consulted && decision.Result == TriKind.Unknown);
}

/// <summary>How one filter of one event answered, with the pattern that answered it and where that pattern sits.</summary>
public sealed record FilterDecision
{
    /// <summary>The filter key as the author wrote it: <c>branches</c>, <c>paths-ignore</c>, <c>types</c>, <c>cron</c>.</summary>
    public required string Filter { get; init; }

    /// <summary>True when the filter was consulted at all; false for one GitHub does not reach, such as paths on a tag push.</summary>
    public bool Consulted { get; init; } = true;

    /// <summary>Include or ignore: which sense the list works in.</summary>
    public FilterListMode Mode { get; init; }

    /// <summary>Satisfied, not satisfied, or not decidable from what was supplied.</summary>
    public required TriKind Result { get; init; }

    /// <summary>The ref name, activity type or path the patterns were run against, when there was one.</summary>
    public string? Value { get; init; }

    /// <summary>How many values the list was run against; more than one only for paths.</summary>
    public int ValuesTested { get; init; }

    /// <summary>The pattern that decided it, as authored, null when no pattern decided.</summary>
    public string? Pattern { get; init; }

    /// <summary>Where that pattern is in the YAML, so the reader can jump to the line that said no.</summary>
    public SourceRange PatternRange { get; init; }

    /// <summary>Why the filter was not consulted, or why it cannot be decided — the reader's sentence.</summary>
    public string? Note { get; init; }

    /// <summary>Why this is <c>unknown</c>; null for a definite answer.</summary>
    public UnknownReason? Reason { get; init; }
}
