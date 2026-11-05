using System.Reflection;

namespace ActionLens.Ui.Services;

/// <summary>
/// One bundled workflow, read out of the UI library's own assembly rather than fetched.
/// </summary>
/// <param name="FileName">The file name the analysis reports against, e.g. <c>atlas-ci.yml</c>.</param>
/// <param name="Title">A short label for the picker.</param>
/// <param name="Yaml">The workflow text.</param>
public sealed record WorkflowExample(string FileName, string Title, string Yaml);

/// <summary>
/// One bundled event payload, paired with the trigger event it describes.
/// </summary>
/// <param name="FileName">The file name, e.g. <c>pull-request.json</c>.</param>
/// <param name="EventName">The event the payload is for, taken from its own <c>event_name</c>.</param>
/// <param name="Json">The payload text.</param>
public sealed record EventPayloadExample(string FileName, string? EventName, string Json);

/// <summary>
/// The workflows in <c>samples/workflows</c> and the payloads in <c>samples/payloads</c>, embedded in the
/// <c>ActionLens.Ui</c> assembly.
/// </summary>
/// <remarks>
/// Embedded rather than served from <c>wwwroot</c> for two reasons: the app's first paint can show a real
/// conclusion without waiting on a request, and the local-first promise (<c>ADR 0003</c>) is easier to
/// hold when the examples travel inside the downloaded bundle. <c>ActionLens.Core</c> stays free of this —
/// reading an assembly manifest is a presentation concern, not an engine one. The resources are embedded
/// by <c>ActionLens.Ui.csproj</c>, the project that owns this class: a glob that matches nothing here
/// crashed the app on boot once already, so <c>ActionLens.Ui.Tests</c> pins the three fixtures and four
/// payloads by name.
/// </remarks>
public static class WorkflowExamples
{
    private const string YamlSuffix = ".yml";
    private const string JsonSuffix = ".json";

    private static readonly Lazy<IReadOnlyList<WorkflowExample>> Examples = new(LoadWorkflows);
    private static readonly Lazy<IReadOnlyList<EventPayloadExample>> Payloads = new(LoadPayloads);

    /// <summary>The bundled workflows, ordered by file name so the picker is stable.</summary>
    public static IReadOnlyList<WorkflowExample> Workflows => Examples.Value;

    /// <summary>The bundled event payloads, ordered by file name.</summary>
    public static IReadOnlyList<EventPayloadExample> EventPayloads => Payloads.Value;

    /// <summary>The workflow the app opens with: the one that produces a conclusion on its own.</summary>
    public static WorkflowExample DefaultWorkflow =>
        Workflows.FirstOrDefault(example => string.Equals(example.FileName, "atlas-ci.yml", StringComparison.Ordinal))
        ?? Workflows[0];

    /// <summary>
    /// The payload that suits <paramref name="eventName"/>, or the first one when nothing matches. A null
    /// event name takes the first payload, which is the one whose event the default workflow declares.
    /// </summary>
    public static EventPayloadExample? PayloadFor(string? eventName)
    {
        if (EventPayloads.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(eventName))
        {
            return EventPayloads[0];
        }

        return EventPayloads.FirstOrDefault(payload =>
                string.Equals(payload.EventName, eventName, StringComparison.OrdinalIgnoreCase))
            ?? EventPayloads[0];
    }

    /// <summary>
    /// Reads a manifest resource by file-name suffix. The suffix match is deliberate: the manifest name
    /// depends on how MSBuild flattens the <c>Link</c> path, and the engine's positions are what a reader
    /// checks, not the resource id.
    /// </summary>
    internal static string ReadEmbedded(Assembly assembly, string fileName)
    {
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            throw new InvalidOperationException($"Embedded example '{fileName}' is missing from {assembly.GetName().Name}.");
        }

        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded example '{name}' could not be opened.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static List<WorkflowExample> LoadWorkflows()
    {
        Assembly assembly = typeof(WorkflowExamples).Assembly;
        List<WorkflowExample> loaded = [];
        foreach (string fileName in ResourceNames(assembly, YamlSuffix))
        {
            string text = ReadEmbedded(assembly, fileName);
            loaded.Add(new WorkflowExample(fileName, Title(text, fileName), text));
        }

        return loaded;
    }

    private static List<EventPayloadExample> LoadPayloads()
    {
        Assembly assembly = typeof(WorkflowExamples).Assembly;
        List<EventPayloadExample> loaded = [];
        foreach (string fileName in ResourceNames(assembly, JsonSuffix))
        {
            string text = ReadEmbedded(assembly, fileName);
            loaded.Add(new EventPayloadExample(fileName, ReadEventName(text), text));
        }

        return loaded;
    }

    /// <summary>
    /// The example file names, taken from the manifest and stripped of the
    /// <c>{AssemblyName}.Examples.</c> prefix the csproj's <c>Link</c> metadata produces.
    /// </summary>
    private static IEnumerable<string> ResourceNames(Assembly assembly, string suffix)
    {
        string prefix = assembly.GetName().Name + ".Examples.";
        return assembly.GetManifestResourceNames()
            .Where(candidate => candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)
                ? candidate[prefix.Length..]
                : candidate)
            .OrderBy(candidate => candidate, StringComparer.Ordinal);
    }

    /// <summary>The <c>name:</c> the author wrote, falling back to the file name. Only used as a picker label.</summary>
    private static string Title(string yaml, string fileName)
    {
        foreach (string line in yaml.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("name:", StringComparison.Ordinal))
            {
                string value = trimmed[5..].Trim();
                if (value.Length > 0)
                {
                    return value.Trim('"', '\'');
                }
            }
        }

        return fileName;
    }

    private static string? ReadEventName(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("event_name", out System.Text.Json.JsonElement name)
                && name.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return name.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A payload that cannot be read is the engine's finding to report, not this loader's to hide.
        }

        return null;
    }
}
