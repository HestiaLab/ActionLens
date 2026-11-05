using System.Text.Json;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// What the caller supplies about the run besides the workflow text: the event payload, the repository
/// variables and the <c>workflow_dispatch</c> inputs. These are the three contexts that are legal in an
/// <c>if:</c> and knowable only from outside the file, so this record is the whole outside world.
/// </summary>
/// <remarks>
/// Absent means unknown, and the distinction matters: <see cref="GitHub"/> null is "no payload was
/// supplied, so nothing about the event is knowable", while an object missing a property is GitHub's own
/// documented behaviour (a property that does not exist reads as <c>null</c>). Building one but not the
/// other would turn a missing input into a definite <c>false</c>, which is the failure mode this whole
/// layer exists to avoid.
/// <para>
/// Everything here is derived from data the caller passed in: no network, no clock, no file reads. Two
/// analyses of the same inputs therefore produce the same report bytes.
/// </para>
/// </remarks>
public sealed record AnalysisInput
{
    /// <summary>
    /// The payload is as untrusted as the workflow text and gets the same byte ceiling. Without it a
    /// pasted multi-megabyte JSON string reaches <c>JsonDocument.Parse</c> unbounded.
    /// </summary>
    public const int MaxPayloadBytes = 1_048_576;

    /// <summary>The input that knows nothing, which is what a bare <c>Analyze(yaml)</c> call uses.</summary>
    public static AnalysisInput Empty { get; } = new();

    /// <summary>
    /// The derived <c>github</c> context, or null when no payload was supplied.
    /// </summary>
    public GitHubValue? GitHub { get; init; }

    /// <summary>The <c>vars</c> context, or null when the caller supplied no repository variables.</summary>
    public GitHubValue? Vars { get; init; }

    /// <summary>The <c>inputs</c> context, or null when the run's dispatch inputs were not supplied.</summary>
    public GitHubValue? Inputs { get; init; }

    /// <summary>The event name the payload declares, for the report's trigger block. Null when absent.</summary>
    public string? EventName { get; init; }

    /// <summary>
    /// Findings from reading the payload (bad JSON, a payload that is not an object, a key that cannot be
    /// derived). The analyzer appends these; a broken payload is never an exception.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();

    /// <summary>True when the payload gave ActionLens anything to work with.</summary>
    public bool HasEventPayload => GitHub is not null;

    /// <summary>
    /// Derives the input from an event payload written as JSON.
    /// </summary>
    /// <param name="eventPayloadJson">
    /// <para>
    /// A JSON <b>object</b> — the webhook body for the event, or the recorded event of a past run. Keys
    /// are read case-sensitively, as JSON is. The recognised keys and what they become:
    /// </para>
    /// <list type="table">
    ///   <item><term><c>event_name</c> (string)</term><description><c>github.event_name</c>. Not part of a
    ///   webhook body, so it has to be stated; it is removed from <c>github.event</c> because GitHub does
    ///   not put it there either. Without it, <c>github.event_name</c> reads as <c>null</c>.</description></item>
    ///   <item><term><c>ref</c> (string)</term><description><c>github.ref</c>.</description></item>
    ///   <item><term><c>sha</c> (string)</term><description><c>github.sha</c>.</description></item>
    ///   <item><term><c>repository</c> (string, or object with <c>full_name</c>)</term>
    ///   <description><c>github.repository</c>.</description></item>
    ///   <item><term><c>sender.login</c> (object), else <c>actor</c> (string)</term>
    ///   <description><c>github.actor</c>.</description></item>
    ///   <item><term><c>pull_request.head.ref</c> / <c>pull_request.base.ref</c></term>
    ///   <description><c>github.head_ref</c> / <c>github.base_ref</c>, which is how the runner spells them.</description></item>
    ///   <item><term><c>inputs</c> (object)</term><description>the <c>inputs</c> context, and still visible
    ///   as <c>github.event.inputs</c> — this is where a real <c>workflow_dispatch</c> body puts them.</description></item>
    ///   <item><term><c>vars</c> (object)</term><description>the <c>vars</c> context. Repository variables
    ///   are not in any GitHub payload, so this key is ActionLens's own way of supplying them; it is
    ///   removed from <c>github.event</c>.</description></item>
    ///   <item><term><c>event</c> (object)</term><description>used verbatim as <c>github.event</c> instead of
    ///   the body itself, for callers that paste a run's recorded event.</description></item>
    ///   <item><term>every other key</term><description>kept as <c>github.event.&lt;key&gt;</c>, so
    ///   <c>github.event.pull_request.user.login</c> works on a real payload.</description></item>
    /// </list>
    /// <para>
    /// <c>github.workflow</c> comes from the workflow's own <c>name:</c> rather than the payload, because
    /// it is static. A property this derivation does not produce reads as <c>null</c> — the documented
    /// Actions behaviour for a property that does not exist — so <c>github.run_id</c>, <c>github.action</c>
    /// and friends are <em>not</em> modelled and a condition on them settles to a value the run may
    /// disagree with. Supply the key in the payload when it matters.
    /// </para>
    /// </param>
    /// <param name="workflowName">The workflow's name, for <c>github.workflow</c>. Null when unknown.</param>
    public static AnalysisInput FromEventPayload(string? eventPayloadJson, string? workflowName = null)
    {
        if (string.IsNullOrWhiteSpace(eventPayloadJson))
        {
            // No payload means no github context at all — reported as unknown rather than as an object
            // full of nulls, which would make `github.event_name == 'push'` an answer instead of a question.
            return Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var range = new SourceRange(new SourcePosition(1, 1));
        var bytes = System.Text.Encoding.UTF8.GetByteCount(eventPayloadJson);
        if (bytes > MaxPayloadBytes)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.YamlSizeLimit,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Scenario,
                Message = $"The event payload is {bytes:N0} bytes, above the {MaxPayloadBytes:N0} byte limit ActionLens reads.",
                Range = range,
                Suggestion = "Paste only the fields the condition reads, or a trimmed recorded event.",
            });
            return new AnalysisInput { Diagnostics = diagnostics };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(eventPayloadJson);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.InvalidValue,
                Severity = DiagnosticSeverity.Warning,
                Stage = DiagnosticStage.Scenario,
                Message = $"The event payload is not readable JSON, so no github context was derived: {ex.Message}",
                Range = range,
                Suggestion = "Paste the event body as one JSON object, e.g. {\"event_name\":\"push\",\"ref\":\"refs/heads/main\"}.",
            });
            return new AnalysisInput { Diagnostics = diagnostics };
        }

        using (document)
        {
            return Derive(document.RootElement, workflowName, diagnostics);
        }
    }

    private static AnalysisInput Derive(
        JsonElement root,
        string? workflowName,
        List<Diagnostic> diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.InvalidValue,
                Severity = DiagnosticSeverity.Warning,
                Stage = DiagnosticStage.Scenario,
                Message = $"The event payload is {Describe(root)}, not a JSON object, so no github context was derived.",
                Range = new SourceRange(new SourcePosition(1, 1)),
                Suggestion = "GitHub's event payloads are always one object; paste the body rather than a value.",
            });
            return new AnalysisInput { Diagnostics = diagnostics };
        }

        // Keys the runner does not carry inside github.event, so they must not leak into it either.
        var eventSource = root.TryGetProperty("event", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;
        var githubEvent = EventPayloadReader.ReadObject(eventSource, ["event_name", "vars"]);
        var github = new Dictionary<string, GitHubValue>(StringComparer.Ordinal)
        {
            ["event"] = GitHubValue.FromObject(githubEvent),
        };

        CopyText(root, "ref", github, "ref");
        CopyText(root, "sha", github, "sha");
        CopyText(root, "actor", github, "actor");
        CopyText(root, "event_name", github, "event_name");

        if (root.TryGetProperty("repository", out var repository))
        {
            if (repository.ValueKind == JsonValueKind.String)
            {
                github["repository"] = GitHubValue.FromString(repository.GetString() ?? string.Empty);
            }
            else if (TryPath(repository, "full_name", out var fullName))
            {
                github["repository"] = fullName;
            }
        }

        if (!github.ContainsKey("actor")
            && root.TryGetProperty("sender", out var sender)
            && TryPath(sender, "login", out var login))
        {
            github["actor"] = login;
        }

        // The runner spells github.head_ref / github.base_ref from the pull request object rather than
        // carrying them as payload keys, so the derivation does the same.
        if (root.TryGetProperty("pull_request", out var pullRequest) && pullRequest.ValueKind == JsonValueKind.Object)
        {
            if (TryText(pullRequest, "head", "ref", out var headRef))
            {
                github["head_ref"] = GitHubValue.FromString(headRef);
            }

            if (TryText(pullRequest, "base", "ref", out var baseRef))
            {
                github["base_ref"] = GitHubValue.FromString(baseRef);
            }
        }

        if (!string.IsNullOrEmpty(workflowName))
        {
            github["workflow"] = GitHubValue.FromString(workflowName);
        }

        var inputs = ReadMap(root, "inputs");
        var vars = ReadMap(root, "vars");
        var eventName = github.TryGetValue("event_name", out var declared)
            && declared.Kind == GitHubValueKind.String
            ? declared.String
            : null;

        return new AnalysisInput
        {
            GitHub = GitHubValue.FromObject(github),
            Vars = vars is null ? null : GitHubValue.FromObject(vars),
            Inputs = inputs is null ? null : GitHubValue.FromObject(inputs),
            EventName = eventName,
            Diagnostics = [.. diagnostics],
        };

        static void CopyText(JsonElement source, string key, Dictionary<string, GitHubValue> target, string name)
        {
            if (source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                target[name] = GitHubValue.FromString(value.GetString() ?? string.Empty);
            }
        }
    }

    private static bool TryPath(JsonElement element, string property, out GitHubValue value)
    {
        value = GitHubValue.Null;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var child)
            && EventPayloadReader.TryWrap(child, out value);
    }

    /// <summary>Reads <c>parent.child.property</c> when all three exist and the last one is text.</summary>
    private static bool TryText(JsonElement parent, string child, string property, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(child, out var middle) || middle.ValueKind != JsonValueKind.Object
            || !middle.TryGetProperty(property, out var text) || text.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = text.GetString() ?? string.Empty;
        return true;
    }

    private static Dictionary<string, GitHubValue>? ReadMap(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            map[property.Name] = EventPayloadReader.ToValue(property.Value);
        }

        return map;
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "a JSON array",
        JsonValueKind.String => "a JSON string",
        JsonValueKind.Number => "a JSON number",
        JsonValueKind.True or JsonValueKind.False => "a JSON boolean",
        _ => "null",
    };
}

/// <summary>
/// Turns JSON elements into workflow values, the same six types <c>fromJSON()</c> produces. Kept next to
/// <see cref="AnalysisInput"/> rather than in the evaluator because the payload reader is the only other
/// caller and the two must agree on what a JSON number becomes.
/// </summary>
internal static class EventPayloadReader
{
    internal static Dictionary<string, GitHubValue> ReadObject(JsonElement element, string[] skip)
    {
        var map = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(skip, property.Name) >= 0)
            {
                continue;
            }

            map[property.Name] = ToValue(property.Value);
        }

        return map;
    }

    internal static GitHubValue ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => GitHubValue.Null,
        JsonValueKind.True => GitHubValue.True,
        JsonValueKind.False => GitHubValue.False,
        JsonValueKind.String => GitHubValue.FromString(element.GetString() ?? string.Empty),
        JsonValueKind.Number => GitHubValue.FromNumber(element.GetDouble()),
        JsonValueKind.Array => GitHubValue.FromArray(
            element.EnumerateArray().Select(ToValue).ToArray()),
        JsonValueKind.Object => GitHubValue.FromObject(ReadObject(element, [])),
        _ => GitHubValue.Null,
    };

    /// <summary>Wraps any JSON value, but reports a missing or null one as "no value" rather than a member.</summary>
    internal static bool TryWrap(JsonElement element, out GitHubValue value)
    {
        value = ToValue(element);
        return element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
