using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Triggers;

/// <summary>
/// Answers "did this workflow run for this event, and if not, which filter said no", and reports the
/// schema findings that belong to an <c>on:</c> section.
/// </summary>
/// <remarks>
/// <para>
/// The rules come from the "Trigger filters" section of <c>docs/expression-semantics.md</c>, which cites
/// [Events that trigger workflows] and [Workflow syntax]: <c>branches</c> and <c>branches-ignore</c>
/// cannot both be used for one event (and the same for <c>tags</c>/<c>paths</c>), <c>branches</c> and
/// <c>paths</c> are ANDed, <c>pull_request</c> filters the base branch and has no <c>tags</c> key at all,
/// a <c>push</c> that declares only <c>tags</c> never runs for a branch and one that declares neither
/// runs for both, path filters are not evaluated for a tag push, and <c>workflow_dispatch</c> and
/// <c>schedule</c> accept only <c>inputs</c> and cron entries respectively.
/// </para>
/// <para>
/// The other half of the job is refusing to answer. A trigger verdict is a claim about what GitHub's
/// server-side filter did, and several of its inputs are not in the file: the changed-file list, whether
/// the push carried more than 1,000 commits, whether the diff timed out, whether the diff was truncated
/// at 3,000 files (300 on GHES), whether a cron fired, whether a person pressed the button. Each of those
/// is an <c>unknown</c> carrying an <see cref="UnknownReason"/> that names the missing input and the
/// filter it blocks, in the same shape the expression layer uses ([ADR 0002]). An unknown without a
/// reason is treated as a bug everywhere else in ActionLens, and it is a bug here too.
/// </para>
/// <para>
/// Nothing here reads a clock or a file: the schedule and dispatch answers are unknown precisely because
/// the facts they would need are a clock and a person ([ADR 0001]).
/// </para>
/// </remarks>
public static partial class TriggerEvaluator
{
    /// <summary>The three include/exclude pairs the documentation forbids on one event.</summary>
    private static readonly (string Include, string Ignore)[] ExclusivePairs =
    [
        ("branches", "branches-ignore"),
        ("tags", "tags-ignore"),
        ("paths", "paths-ignore"),
    ];

    /// <summary>
    /// Reads the <c>on:</c> section: always produces its findings, and a verdict when an event was asked
    /// about.
    /// </summary>
    /// <param name="triggers">The <c>on:</c> section as loaded.</param>
    /// <param name="eventName">
    /// The event to answer for, or null for "nobody asked", which produces findings but no verdict —
    /// "did this trigger fire" has no answer for an event nobody named.
    /// </param>
    /// <param name="payload">What the event carried; <see cref="TriggerPayload.Empty"/> when nothing was supplied.</param>
    public static TriggerEvaluation Evaluate(
        TriggerSection triggers,
        string? eventName,
        TriggerPayload payload)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(payload);

        var diagnostics = new List<Diagnostic>(Validate(triggers));
        if (eventName is not { Length: > 0 })
        {
            return new TriggerEvaluation { Diagnostics = diagnostics };
        }

        var verdict = EvaluateEvent(triggers, eventName, payload);
        if (payload.EventName is { } declared
            && !string.Equals(declared, eventName, StringComparison.Ordinal))
        {
            // The UI can pick an event and paste a payload for another one; that is worth a finding,
            // because every filter below was then read against the wrong event's data.
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.TriggerEventMismatch,
                Severity = DiagnosticSeverity.Warning,
                Stage = DiagnosticStage.Schema,
                Message = $"The event asked about is '{eventName}', but the payload describes a '{declared}' event, so its filters were read against a different event's data.",
                Range = verdict.Range,
                Suggestion = $"Make the payload's 'event_name' '{eventName}', or ask about '{declared}'.",
                Scope = eventName,
            });
        }

        return new TriggerEvaluation { Diagnostics = diagnostics, Verdict = verdict };
    }

}
