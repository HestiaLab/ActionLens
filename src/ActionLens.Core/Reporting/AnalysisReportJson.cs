using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Text;
using ActionLens.Core.Triggers;

namespace ActionLens.Core.Reporting;

/// <summary>
/// The JSON export of one report.
/// </summary>
/// <remarks>
/// Written with a hand-ordered <see cref="Utf8JsonWriter"/> rather than by reflecting over the record
/// types, for one reason: reflection gives no promise about member order, and the key order is part of
/// what makes an export a file a user can diff. Everything the writer emits comes from the report, in the
/// order this method spells out, and nothing comes from a clock, a generator or a hash — so two exports
/// of the same analysis are the same bytes, on every machine.
/// <para>
/// Enumerations leave as the same kebab-case words they were written with, because an export that changes
/// when a member is renamed is not an export anyone can depend on.
/// </para>
/// </remarks>
public static class AnalysisReportJson
{
    /// <summary>The export as text, indented with <c>\n</c> so a shared report reads the same everywhere.</summary>
    public static string ToJson(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            Write(report, writer);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes the report into any writer, so a caller can embed it in a larger document.</summary>
    public static void Write(AnalysisReport report, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", AnalysisReport.SchemaVersion);
        WriteWorkflow(writer, report.Workflow);
        WriteEvent(writer, report.Event);
        WriteTrigger(writer, report.Trigger);
        WriteScenario(writer, report.Scenario);
        WriteTotals(writer, report.Totals);
        WriteGraph(writer, report.Graph);

        writer.WriteNumber("jobCount", report.Jobs.Count);
        writer.WriteStartArray("jobs");
        foreach (var job in report.Jobs)
        {
            WriteJob(writer, job);
        }

        writer.WriteEndArray();

        writer.WriteNumber("diagnosticCount", report.Diagnostics.Count);
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in report.Diagnostics)
        {
            WriteDiagnostic(writer, diagnostic);
        }

        writer.WriteEndArray();

        writer.WriteBoolean("blocked", report.IsBlocked);
        writer.WriteEndObject();
    }

    // ------------------------------------------------------------------ sections

    private static void WriteWorkflow(Utf8JsonWriter writer, WorkflowIdentity workflow)
    {
        writer.WriteStartObject("workflow");
        writer.WriteString("name", workflow.Name);
        WriteNullable(writer, "fileName", workflow.FileName);
        WriteNullable(writer, "runName", workflow.RunName);
        writer.WriteNumber("jobCount", workflow.JobCount);
        WriteStrings(writer, "permissionKeys", workflow.PermissionKeys);
        WriteNullable(writer, "concurrencyGroup", workflow.ConcurrencyGroup);
        writer.WriteBoolean("cancelInProgress", workflow.CancelInProgress);
        writer.WriteEndObject();
    }

    private static void WriteEvent(Utf8JsonWriter writer, EventReport report)
    {
        writer.WriteStartObject("event");
        writer.WriteBoolean("hasEventPayload", report.HasEventPayload);
        WriteNullable(writer, "eventName", report.EventName);
        writer.WriteBoolean("varsSupplied", report.VarsSupplied);
        writer.WriteBoolean("inputsSupplied", report.InputsSupplied);
        writer.WriteEndObject();
    }

    private static void WriteTrigger(Utf8JsonWriter writer, TriggerReport trigger)
    {
        writer.WriteStartObject("trigger");
        writer.WriteBoolean("isShorthand", trigger.IsShorthand);
        writer.WriteStartArray("declaredEvents");
        foreach (var declared in trigger.Declared)
        {
            writer.WriteStartObject();
            writer.WriteString("name", declared.Name);
            writer.WriteBoolean("hasConfiguration", declared.HasConfiguration);
            WriteStrings(writer, "declaredFilters", declared.DeclaredFilters);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (trigger.Verdict is { } verdict)
        {
            WriteVerdict(writer, verdict);
        }
        else
        {
            // Present and null rather than absent: a reader has to be able to tell "nobody asked" apart
            // from a report that predates the trigger layer.
            writer.WriteNull("verdict");
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The trigger answer, with every filter's decision beside it. The reason tree is written for the
    /// verdict and for each decision that came back unknown, so "which input was missing" survives the
    /// export rather than only the in-memory report.
    /// </summary>
    private static void WriteVerdict(Utf8JsonWriter writer, TriggerVerdict verdict)
    {
        writer.WriteStartObject("verdict");
        writer.WriteString("event", verdict.Event);
        writer.WriteString("source", Text(verdict.Source));
        WriteNullable(writer, "payloadEventName", verdict.PayloadEventName);
        writer.WriteBoolean("occurrencePinnedByPayload", verdict.OccurrencePinnedByPayload);
        writer.WriteString("range", RangeText(verdict.Range));
        writer.WriteString("runs", Text(verdict.Runs.Kind));
        WriteReason(writer, "reason", verdict.Runs.Reason);

        if (verdict.SaidNo is { } blocker)
        {
            writer.WriteString("blockedBy", blocker.Filter);
        }
        else
        {
            writer.WriteNull("blockedBy");
        }

        writer.WriteStartArray("decisions");
        foreach (var decision in verdict.Decisions)
        {
            WriteDecision(writer, decision);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDecision(Utf8JsonWriter writer, FilterDecision decision)
    {
        writer.WriteStartObject();
        writer.WriteString("filter", decision.Filter);
        writer.WriteBoolean("consulted", decision.Consulted);
        writer.WriteString("mode", Text(decision.Mode));
        writer.WriteString("result", Text(decision.Result));
        WriteNullable(writer, "value", decision.Value);
        writer.WriteNumber("valuesTested", decision.ValuesTested);
        WriteNullable(writer, "pattern", decision.Pattern);
        writer.WriteString("patternRange", RangeText(decision.PatternRange));
        WriteNullable(writer, "note", decision.Note);
        WriteReason(writer, "reason", decision.Reason);
        writer.WriteEndObject();
    }

    private static void WriteScenario(Utf8JsonWriter writer, ScenarioReport scenario)
    {
        writer.WriteStartObject("scenario");
        writer.WriteString("label", scenario.Label);
        writer.WriteString("description", scenario.Description);
        writer.WriteBoolean("workflowCancelled", scenario.WorkflowCancelled);
        writer.WriteStartArray("pins");
        foreach (var pin in scenario.Pins)
        {
            writer.WriteStartObject();
            writer.WriteString("job", pin.JobId);
            writer.WriteString("state", Text(pin.State));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTotals(Utf8JsonWriter writer, ReportTotals totals)
    {
        writer.WriteStartObject("totals");
        writer.WriteNumber("jobs", totals.Jobs);
        writer.WriteNumber("willRun", totals.WillRun);
        writer.WriteNumber("willNotRun", totals.WillNotRun);
        writer.WriteNumber("unknownJobs", totals.UnknownJobs);
        writer.WriteNumber("steps", totals.Steps);
        WriteNullable(writer, "jobInstances", totals.JobInstances);
        writer.WriteNumber("diagnostics", totals.Diagnostics);
        writer.WriteNumber("criticals", totals.Criticals);
        writer.WriteNumber("errors", totals.Errors);
        writer.WriteNumber("warnings", totals.Warnings);
        writer.WriteNumber("infos", totals.Infos);
        writer.WriteEndObject();
    }

    private static void WriteGraph(Utf8JsonWriter writer, GraphReport graph)
    {
        writer.WriteStartObject("graph");
        writer.WriteBoolean("isAcyclic", graph.IsAcyclic);
        writer.WriteNumber("jobCount", graph.JobCount);
        writer.WriteNumber("edgeCount", graph.EdgeCount);
        writer.WriteNumber("layerCount", graph.LayerCount);
        writer.WriteNumber("maxLayerWidth", graph.MaxLayerWidth);

        writer.WriteStartArray("cycles");
        foreach (var cycle in graph.Cycles)
        {
            writer.WriteStringValue(string.Join(" -> ", cycle));
        }

        writer.WriteEndArray();

        WriteStrings(writer, "unplacedJobs", graph.UnplacedJobs);

        writer.WriteStartArray("nodes");
        foreach (var node in graph.Nodes)
        {
            writer.WriteStartObject();
            writer.WriteString("job", node.JobId);
            writer.WriteNumber("layer", node.Layer);
            writer.WriteNumber("slot", node.Slot);
            WriteStrings(writer, "needs", node.Dependencies);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("edges");
        foreach (var edge in graph.Edges)
        {
            writer.WriteStartObject();
            writer.WriteString("from", edge.From);
            writer.WriteString("to", edge.To);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteJob(Utf8JsonWriter writer, JobReport job)
    {
        writer.WriteStartObject();
        writer.WriteString("job", job.JobId);
        WriteNullable(writer, "declaredName", job.DeclaredName);
        writer.WriteString("state", Text(job.State));
        writer.WriteString("outcome", Text(job.Outcome));
        writer.WriteBoolean("pinnedByScenario", job.IsPinnedByScenario);

        if (job.ScenarioPin is { } pin)
        {
            writer.WriteString("scenarioPin", Text(pin));
        }
        else
        {
            writer.WriteNull("scenarioPin");
        }

        if (job.Placement is { } placement)
        {
            writer.WriteStartObject("placement");
            writer.WriteNumber("layer", placement.Layer);
            writer.WriteNumber("slot", placement.Slot);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("placement");
        }

        WriteStrings(writer, "needs", job.Needs);

        writer.WriteStartArray("needsOutcomes");
        foreach (var need in job.NeedsOutcomes)
        {
            writer.WriteStartObject();
            writer.WriteString("job", need.JobId);
            writer.WriteString("result", JobOutcomeText.Of(need.Outcome));
            WriteStrings(writer, "outputs", need.Outputs.Keys.OrderBy(key => key, StringComparer.Ordinal));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        WriteCondition(writer, "condition", job.Condition);
        WriteReason(writer, "reason", job.Reason);

        if (job.Matrix is { } matrix)
        {
            writer.WriteStartObject("matrix");
            writer.WriteBoolean("isDynamic", matrix.IsDynamic);
            WriteStrings(writer, "dynamicDimensions", matrix.DynamicDimensions);
            WriteStrings(writer, "dimensions", matrix.Dimensions);
            WriteNullable(writer, "combinationCount", matrix.CombinationCount);
            writer.WriteNumber("producedCount", matrix.ProducedCount);
            writer.WriteBoolean("truncated", matrix.Truncated);
            WriteStrings(writer, "combinationLabels", matrix.CombinationLabels);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("matrix");
        }

        writer.WriteStartArray("runs");
        foreach (var run in job.Runs)
        {
            WriteRun(writer, run);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRun(Utf8JsonWriter writer, MatrixRun run)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", run.Index);
        writer.WriteBoolean("combinationIsKnown", run.CombinationIsKnown);
        WriteNullable(writer, "label", run.Label);

        writer.WriteStartObject("values");
        // A combination's keys arrive from a dictionary whose insertion order follows include entries.
        // Sorting them is what makes the exported bytes a function of the workflow, not of that order.
        foreach (var pair in run.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();

        writer.WriteStartArray("steps");
        foreach (var step in run.Steps)
        {
            WriteStep(writer, step);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStep(Utf8JsonWriter writer, StepRunResult step)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", step.Index);
        WriteNullable(writer, "id", step.StepId);
        WriteNullable(writer, "name", step.Name);
        writer.WriteString("state", Text(step.State));
        WriteCondition(writer, "condition", step.Condition);
        WriteReason(writer, "reason", step.Reason);
        writer.WriteEndObject();
    }

    private static void WriteCondition(Utf8JsonWriter writer, string name, ConditionTrace? condition)
    {
        if (condition is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("text", condition.Text);
        writer.WriteString("evaluatedAs", condition.EvaluatedAs);
        writer.WriteString("result", Text(condition.Result));
        writer.WriteBoolean("implicitSuccessApplied", condition.ImplicitSuccessApplied);
        writer.WriteString("range", RangeText(condition.Range));
        WriteReason(writer, "reason", condition.Reason);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The unknown-cause tree, written recursively. It is a value in the export rather than a string to
    /// print: a reader has to be able to tell which context, which value and which sub-expression forced
    /// the answer, and to do that from the JSON alone.
    /// </summary>
    private static void WriteReason(Utf8JsonWriter writer, string name, UnknownReason? reason)
    {
        if (reason is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        WriteReasonBody(writer, reason);
        writer.WriteEndObject();
    }

    /// <summary>The reason's own members, written into the object the caller has already opened.</summary>
    private static void WriteReasonBody(Utf8JsonWriter writer, UnknownReason reason)
    {
        writer.WriteString("kind", Text(reason.Kind));
        writer.WriteString("message", reason.Message);
        writer.WriteString("range", RangeText(reason.Range));
        writer.WriteStartArray("causes");
        foreach (var cause in reason.Causes)
        {
            // No name here: a property name inside an array is not JSON, and the causes are an array.
            writer.WriteStartObject();
            WriteReasonBody(writer, cause);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteDiagnostic(Utf8JsonWriter writer, Diagnostic diagnostic)
    {
        writer.WriteStartObject();
        writer.WriteString("code", diagnostic.Code);
        writer.WriteString("severity", Text(diagnostic.Severity));
        writer.WriteString("stage", Text(diagnostic.Stage));
        writer.WriteString("message", diagnostic.Message);
        writer.WriteString("range", RangeText(diagnostic.Range));
        WriteNullable(writer, "suggestion", diagnostic.Suggestion);
        WriteNullable(writer, "helpLink", diagnostic.HelpLink);
        WriteNullable(writer, "scope", diagnostic.Scope);
        writer.WriteEndObject();
    }

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
