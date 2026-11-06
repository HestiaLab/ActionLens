using System.Text.Json;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Text;
using ActionLens.Core.Triggers;

namespace ActionLens.Core.Reporting;

public static partial class AnalysisReportJson
{
    // ------------------------------------------------------------------ primitives

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string RangeText(SourceRange range) => range.ToString() ?? string.Empty;

    private static string Text(TriKind kind) => kind switch
    {
        TriKind.True => "true",
        TriKind.False => "false",
        _ => "unknown",
    };

    private static string Text(TriggerEventSource source) => source switch
    {
        TriggerEventSource.Payload => "payload",
        _ => "caller",
    };

    private static string Text(FilterListMode mode) => mode switch
    {
        FilterListMode.Include => "include",
        _ => "ignore",
    };

    private static string Text(RunState state) => state switch
    {
        RunState.WillRun => "will-run",
        RunState.WillNotRun => "will-not-run",
        _ => "unknown",
    };

    private static string Text(JobOutcome outcome) => JobOutcomeText.Of(outcome);

    private static string Text(ScenarioJobState state) => state switch
    {
        ScenarioJobState.Succeeds => "succeeds",
        ScenarioJobState.Fails => "fails",
        ScenarioJobState.IsSkipped => "skipped",
        _ => "cancelled",
    };

    private static string Text(UnknownReasonKind kind) => kind switch
    {
        UnknownReasonKind.RuntimeContext => "runtime-context",
        UnknownReasonKind.ValueMissing => "value-missing",
        UnknownReasonKind.ContextNotAvailable => "context-not-available",
        UnknownReasonKind.RuntimeFunction => "runtime-function",
        UnknownReasonKind.ScenarioDependent => "scenario-dependent",
        UnknownReasonKind.Propagated => "propagated",
        _ => "invalid-syntax",
    };

    private static string Text(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Info => "info",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Error => "error",
        _ => "critical",
    };

    private static string Text(DiagnosticStage stage) => stage switch
    {
        DiagnosticStage.Yaml => "yaml",
        DiagnosticStage.Schema => "schema",
        DiagnosticStage.Expression => "expression",
        DiagnosticStage.Matrix => "matrix",
        DiagnosticStage.Graph => "graph",
        DiagnosticStage.Scenario => "scenario",
        DiagnosticStage.Lint => "lint",
        _ => "internal",
    };
}
