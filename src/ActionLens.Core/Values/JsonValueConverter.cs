using System.Text.Json;

namespace ActionLens.Core.Values;

/// <summary>
/// The one JSON → <see cref="GitHubValue"/> mapping in the engine. <c>fromJSON()</c> and the event
/// payload reader used to carry two copies; a comment in <c>AnalysisInput.cs</c> asked that they
/// "must agree" but nothing enforced it. Sharing this class makes agreement structural.
/// </summary>
/// <remarks>
/// YAML → <see cref="GitHubValue"/> is deliberately <em>not</em> here: scalar resolution follows YAML
/// rules (plain scalars become numbers/booleans/null) and needs the source text and position, which a
/// <see cref="JsonElement"/> does not carry.
/// </remarks>
public static class JsonValueConverter
{
    /// <summary>Turns any JSON element into the workflow value its type implies.</summary>
    public static GitHubValue ToValue(JsonElement element) => element.ValueKind switch
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

    /// <summary>Reads an object's properties, skipping the named keys.</summary>
    public static Dictionary<string, GitHubValue> ReadObject(JsonElement element, string[] skip)
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

    /// <summary>Wraps any JSON value, but reports a missing or null one as "no value" rather than a member.</summary>
    public static bool TryWrap(JsonElement element, out GitHubValue value)
    {
        value = ToValue(element);
        return element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
