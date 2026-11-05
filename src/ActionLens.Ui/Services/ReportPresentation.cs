using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;

namespace ActionLens.Ui.Services;

/// <summary>
/// The words and class names the shell puts on the engine's answers.
/// </summary>
/// <remarks>
/// The three states must never collapse into two: <see cref="RunState.Unknown"/> is not "skipped" and not
/// "running", so it gets its own label, its own class, and its own non-colour marker (a question glyph
/// plus text) — colour alone is not the channel, because a reader who cannot see the difference between
/// the badges must still get the answer.
/// </remarks>
public static class ReportPresentation
{
    /// <summary>The badge a job's state gets: label, short marker, and the class the CSS colours.</summary>
    public static string StateLabel(RunState state) => state switch
    {
        RunState.WillRun => "Will run",
        RunState.WillNotRun => "Will not run",
        _ => "Unknown",
    };

    /// <summary>A one-character marker that survives greyscale and screen readers reading the badge text.</summary>
    public static string StateMarker(RunState state) => state switch
    {
        RunState.WillRun => "+",
        RunState.WillNotRun => "−",
        _ => "?",
    };

    /// <summary>The CSS class for a state badge.</summary>
    public static string StateClass(RunState state) => state switch
    {
        RunState.WillRun => "al-state al-state--run",
        RunState.WillNotRun => "al-state al-state--skip",
        _ => "al-state al-state--unknown",
    };

    /// <summary>
    /// The badge exactly as the canvas draws it inside a node: marker and word together, so a reader who
    /// cannot see the colour still gets <c>+ Will run</c> / <c>− Will not</c> / <c>? unknown</c>.
    /// </summary>
    public static string StateChip(RunState state) => $"{StateMarker(state)} {StateWord(state)}";

    /// <summary>The state word without its marker, for an accessible name that already carries the marker.</summary>
    public static string StateWord(RunState state) => state switch
    {
        RunState.WillRun => "Will run",
        RunState.WillNotRun => "Will not",
        _ => "unknown",
    };

    /// <summary>The shape a node gets for this state, which is the non-colour half of the encoding.</summary>
    public static string StateTone(RunState state) => state switch
    {
        RunState.WillRun => "run",
        RunState.WillNotRun => "skip",
        _ => "unknown",
    };

    /// <summary>
    /// How many runs one job box stands for. A dynamic matrix and a truncated one are said out loud rather
    /// than rounded to a number the engine does not actually know.
    /// </summary>
    public static string InstanceSummary(MatrixReport? matrix)
    {
        if (matrix is null)
        {
            return "1 run";
        }

        if (matrix.IsDynamic)
        {
            string dimensions = matrix.DynamicDimensions.Count > 0
                ? string.Join(", ", matrix.DynamicDimensions)
                : "a value only a run can produce";
            return $"runs ×? (dynamic: {dimensions})";
        }

        int count = matrix.CombinationCount ?? matrix.ProducedCount;
        return matrix.Truncated
            ? $"runs × {count} of {matrix.ProducedCount} (256 cap)"
            : $"runs × {count}";
    }

    /// <summary>Plain-language gloss on what a state means, for the badge's accessible description.</summary>
    public static string StateMeaning(RunState state) => state switch
    {
        RunState.WillRun => "ActionLens found no reason this job would not start.",
        RunState.WillNotRun => "The condition is definitely false for this run.",
        _ => "ActionLens cannot decide from the file and the inputs supplied; this is not the same as skipped.",
    };

    /// <summary>How a job ends, in Actions' own spelling.</summary>
    public static string OutcomeLabel(JobOutcome outcome) => JobOutcomeText.Of(outcome);

    /// <summary>The badge class for a diagnostic severity.</summary>
    public static string SeverityClass(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => "al-severity--critical",
        DiagnosticSeverity.Error => "al-severity--error",
        DiagnosticSeverity.Warning => "al-severity--warning",
        _ => "al-severity--info",
    };

    /// <summary>The severity word the list shows.</summary>
    public static string SeverityLabel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => "critical",
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>A severity token the CSS bundle uses for the editor's highlight class.</summary>
    public static string SeverityToken(DiagnosticSeverity severity) => SeverityLabel(severity);

    /// <summary>The three-state word for a condition's answer.</summary>
    public static string TriLabel(TriKind kind) => kind switch
    {
        TriKind.True => "true",
        TriKind.False => "false",
        _ => "unknown",
    };

    /// <summary>The badge class for a condition's answer.</summary>
    public static string TriClass(TriKind kind) => kind switch
    {
        TriKind.True => "al-state al-state--run",
        TriKind.False => "al-state al-state--skip",
        _ => "al-state al-state--unknown",
    };

    /// <summary>Where a finding points, the way the list labels it.</summary>
    public static string Position(ActionLens.Core.Text.SourceRange range) =>
        range.IsKnown ? $"line {range.Start.Line}, column {range.Start.Column}" : "no position";
}
