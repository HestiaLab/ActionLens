using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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
public static partial class AnalysisReportJson
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

}
