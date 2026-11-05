using System.Text.Json;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Tests.Values;

/// <summary>
/// Q6 second batch: the two copies of each conversion used to be kept in sync by a comment. These
/// tests pin the behaviour of the single shared implementation, so a future edit cannot silently
/// change what <c>fromJSON()</c> or an event payload reader sees.
/// </summary>
public sealed class SharedConvertersTests
{
    [Theory]
    [InlineData("build", "Build", 0)]
    [InlineData("build", "buidl", 2)]
    [InlineData("build", "deploy", 5)]
    [InlineData("", "abc", 3)]
    [InlineData("ubuntu-latest", "ubuntu-latest", 0)]
    public void EditDistance_IgnoresCaseAndCountsSingleCharacterEdits(string left, string right, int expected)
    {
        Assert.Equal(expected, EditDistance.LevenshteinIgnoreCase(left, right));
        Assert.Equal(expected, EditDistance.LevenshteinIgnoreCase(right, left));
    }

    [Fact]
    public void JsonConverter_MapsEveryKindTheWayTheExpressionRuntimeDoes()
    {
        using var document = JsonDocument.Parse("""
            {
              "null": null, "true": true, "false": false, "text": "hi",
              "number": 2.5, "array": [1, "x", null], "object": { "nested": true }
            }
            """);

        var root = JsonValueConverter.ToValue(document.RootElement);
        Assert.True(root.TryGetMember(GitHubValue.FromString("null"), StringComparison.Ordinal, out var nullValue));
        Assert.Equal(GitHubValueKind.Null, nullValue.Kind);

        Assert.True(root.TryGetMember(GitHubValue.FromString("true"), StringComparison.Ordinal, out var trueValue));
        Assert.True(trueValue.IsTruthy());

        Assert.True(root.TryGetMember(GitHubValue.FromString("false"), StringComparison.Ordinal, out var falseValue));
        Assert.False(falseValue.IsTruthy());

        Assert.True(root.TryGetMember(GitHubValue.FromString("text"), StringComparison.Ordinal, out var text));
        Assert.Equal("hi", text.String);

        Assert.True(root.TryGetMember(GitHubValue.FromString("number"), StringComparison.Ordinal, out var number));
        Assert.Equal(2.5, number.Number);

        Assert.True(root.TryGetMember(GitHubValue.FromString("array"), StringComparison.Ordinal, out var array));
        Assert.Equal(3, array.Array.Count);

        Assert.True(root.TryGetMember(GitHubValue.FromString("object"), StringComparison.Ordinal, out var nested));
        Assert.True(nested.TryGetMember(GitHubValue.FromString("nested"), StringComparison.Ordinal, out var nestedTrue));
        Assert.True(nestedTrue.IsTruthy());
    }

    [Fact]
    public void JsonConverter_ReadObjectSkipsNamedKeysAndWrapRejectsNull()
    {
        using var document = JsonDocument.Parse("""{ "event_name": "push", "keep": 7 }""");
        var map = JsonValueConverter.ReadObject(document.RootElement, ["event_name"]);

        Assert.False(map.ContainsKey("event_name"));
        Assert.Equal(7, map["keep"].Number);

        using var nullDocument = JsonDocument.Parse("null");
        Assert.False(JsonValueConverter.TryWrap(nullDocument.RootElement, out var value));
        Assert.Equal(GitHubValueKind.Null, value.Kind);
    }
}
