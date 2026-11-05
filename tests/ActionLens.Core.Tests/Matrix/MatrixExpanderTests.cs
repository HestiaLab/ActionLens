using ActionLens.Core.Diagnostics;
using ActionLens.Core.Matrix;
using ActionLens.Core.Text;
using ActionLens.Core.Values;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Tests.Matrix;

/// <summary>
/// Matrix expansion, tested against the documented behaviours rather than against intuition: the
/// <c>include</c> rules in particular read surprisingly, and each test names the rule it locks.
/// </summary>
public sealed class MatrixExpanderTests
{
    private static SourceRange Nowhere { get; } = new(new SourcePosition(1, 1));

    private static WorkflowValue Value(string text) => new(YamlScalarResolverProbe.Resolve(text), text, Nowhere);

    private static MatrixDimension Dimension(string name, params string[] values) =>
        new() { Name = name, Values = values.Select(Value).ToArray(), Range = Nowhere };

    private static MatrixDimension Dynamic(string name, string expression) =>
        new() { Name = name, DynamicExpression = expression, Range = Nowhere };

    private static Dictionary<string, WorkflowValue> Entry(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.OrdinalIgnoreCase);

    private static MatrixExpansion Expand(
        IReadOnlyList<MatrixDimension> dimensions,
        IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>>? include = null,
        IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>>? exclude = null)
    {
        var matrix = new MatrixSection
        {
            Dimensions = dimensions,
            Include = include ?? Array.Empty<IReadOnlyDictionary<string, WorkflowValue>>(),
            Exclude = exclude ?? Array.Empty<IReadOnlyDictionary<string, WorkflowValue>>(),
        };
        return MatrixExpander.Expand(new StrategySection { Matrix = matrix }, matrix, Nowhere);
    }

    [Fact]
    public void TwoByTwoExpandsToFourCombinationsWithTheFirstDimensionVaryingSlowest()
    {
        var result = Expand([Dimension("os", "linux", "mac"), Dimension("node", "20", "22")]);

        Assert.Equal(4, result.Combinations.Count);
        Assert.Equal(
            ["linux, 20", "linux, 22", "mac, 20", "mac, 22"],
            result.Combinations.Select(combination => combination.DisplayName));
        Assert.Equal(4, result.JobCount);
        Assert.All(result.Combinations, combination => Assert.True(combination.IsPureProduct));
    }

    [Fact]
    public void ThreeDimensionsMultiply()
    {
        var result = Expand([
            Dimension("a", "1", "2"),
            Dimension("b", "x", "y"),
            Dimension("c", "p", "q", "r"),
        ]);

        // 2 x 2 x 3 is 12, not 18 — the original expectation was an arithmetic error in this test.
        Assert.Equal(12, result.Combinations.Count);
    }

    [Fact]
    public void NoMatrixRunsTheJobExactlyOnce()
    {
        var result = MatrixExpander.Expand(strategy: null, matrix: null, Nowhere);

        Assert.Single(result.Combinations);
        Assert.Equal(1, result.JobCount);
        Assert.Empty(result.Combinations[0].Values);
    }

    [Fact]
    public void EmptyDimensionCollapsesTheProduct()
    {
        var result = Expand([Dimension("os"), Dimension("node", "20")]);

        Assert.Empty(result.Combinations);
        Assert.Contains(DiagnosticCodes.MatrixValueInvalid, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void ExcludeIsAPartialMatchAndRemovesEveryCombinationItCovers()
    {
        // The documented nine-jobs-from-twelve example, in miniature: one key removes a whole slice.
        var result = Expand(
            [Dimension("os", "linux", "windows", "macos"), Dimension("version", "12", "16")],
            exclude: [Entry(("os", "windows"))]);

        Assert.Equal(4, result.Combinations.Count);
        Assert.DoesNotContain(result.Combinations, combination => combination.Values["os"].Text == "windows");
    }

    [Fact]
    public void ExcludeWithSeveralKeysRemovesOnlyTheExactSlice()
    {
        var result = Expand(
            [Dimension("os", "linux", "windows"), Dimension("version", "12", "16")],
            exclude: [Entry(("os", "windows"), ("version", "16"))]);

        Assert.Equal(3, result.Combinations.Count);
    }

    [Fact]
    public void ExcludeKeyThatIsNotADimensionIsAnErrorAndMatchesNothing()
    {
        var result = Expand(
            [Dimension("os", "linux")],
            exclude: [Entry(("colour", "red"))]);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.MatrixInvalidShape && diagnostic.Message.Contains("colour"));
        Assert.Single(result.Combinations);
    }

    [Fact]
    public void EmptyExcludeEntryIsRefusedRatherThanRemovingEverything()
    {
        var result = Expand([Dimension("os", "linux", "mac")], exclude: [Entry()]);

        Assert.Contains(DiagnosticCodes.MatrixInvalidShape, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(2, result.Combinations.Count);
    }

    [Fact]
    public void IncludeWithOnlyNewKeysAddsThemToEveryCombination()
    {
        var result = Expand(
            [Dimension("os", "linux", "mac")],
            include: [Entry(("coverage", "true"))]);

        Assert.Equal(2, result.Combinations.Count);
        Assert.All(result.Combinations, combination => Assert.Equal("true", combination.Values["coverage"].Text));
        Assert.All(result.Combinations, combination => Assert.False(combination.IsPureProduct));
        Assert.All(result.Combinations, combination => Assert.True(combination.AddedKeys.Contains("coverage")));
    }

    [Fact]
    public void IncludeFilterNarrowsWhichCombinationsReceiveTheExtraValue()
    {
        var result = Expand(
            [Dimension("os", "linux", "mac"), Dimension("arch", "x64", "arm")],
            include: [Entry(("os", "mac"), ("xcode", "16"))]);

        Assert.Equal(2, result.Combinations.Count(combination => combination.Values.ContainsKey("xcode")));
        Assert.Equal(2, result.Combinations.Count(combination => !combination.Values.ContainsKey("xcode")));
    }

    [Fact]
    public void IncludeWhoseFilterMatchesNothingCreatesANewCombination()
    {
        // A filter only exists when the entry names a matrix variable. Docs, "Expanding or adding
        // matrix configurations": '{fruit: banana} cannot be added to any original matrix combination
        // without overwriting a value, so it is added as an additional matrix combination' — here the
        // variable is 'animal' and no original combination holds 'fish'. An entry with only new keys
        // ({color: green} in the docs) has an empty filter and is spread over all of them instead,
        // which IncludeWithOnlyNewKeysAddsThemToEveryCombination pins.
        var result = Expand(
            [Dimension("animal", "dog", "cat")],
            include: [Entry(("animal", "fish"))]);

        Assert.Equal(3, result.Combinations.Count);
        Assert.Equal(["dog", "cat", "fish"], result.Combinations.Select(combination => combination.Values["animal"].Text));
        var extra = result.Combinations[^1];
        Assert.True(extra.AddedKeys.Contains("animal"));
        Assert.False(extra.IsPureProduct);
    }

    [Fact]
    public void IncludeMatchingOnlyAgainstOriginalCombinationsIgnoresEarlierIncludeEntries()
    {
        // The second entry's filter matches only the combination the first entry appended. Docs:
        // '{fruit: banana, animal: cat} ... It does not add to the {fruit: banana} matrix combination
        // because that combination was not one of the original matrix combinations' — so it becomes a
        // third combination rather than merging into the second.
        var result = Expand(
            [Dimension("animal", "dog")],
            include:
            [
                Entry(("animal", "fish")),
                Entry(("animal", "fish"), ("color", "yellow")),
            ]);

        Assert.Equal(3, result.Combinations.Count);
        Assert.Equal(["dog", "fish", "fish"], result.Combinations.Select(combination => combination.Values["animal"].Text));
        Assert.DoesNotContain(result.Combinations, combination => combination.Values.Count == 3);
        Assert.Equal("yellow", result.Combinations[2].Values["color"].Text);
        Assert.False(result.Combinations[0].Values.ContainsKey("color"));
    }

    [Fact]
    public void IncludeCanOverwriteAValueThatIncludeAddedButNotADimensionValue()
    {
        var result = Expand(
            [Dimension("os", "linux")],
            include:
            [
                Entry(("colour", "green")),
                Entry(("colour", "pink")),
                Entry(("os", "windows")),
            ]);

        // The Assert.Single this test used to open with contradicted its own count two lines below, and
        // the count is the documented one: the docs' '{color: pink, animal: cat}' step "overwrites the
        // color: green that was added by the previous include entry" (so 'colour' becomes 'pink'), while
        // an entry whose filter matches nothing is "added as an additional matrix combination" (so
        // 'os: windows' joins the list instead of replacing os=linux).
        Assert.Equal(2, result.Combinations.Count);
        Assert.Equal("pink", result.Combinations[0].Values["colour"].Text);

        // 'os' names a declared dimension, so the third entry is a filter, not an overwrite.
        Assert.Equal("linux", result.Combinations[0].Values["os"].Text);
        Assert.Equal("windows", result.Combinations[1].Values["os"].Text);
        Assert.True(result.Combinations[1].AddedKeys.Contains("os"));
    }

    [Fact]
    public void ExcludeIsAppliedBeforeIncludeSoIncludeCanAddBack()
    {
        var result = Expand(
            [Dimension("os", "linux", "mac")],
            include: [Entry(("os", "mac"))],
            exclude: [Entry(("os", "mac"))]);

        // The exclusion removes mac from the product; the include entry then names a dimension key that
        // no surviving combination matches, so it comes back as an added combination.
        Assert.Equal(2, result.Combinations.Count);
        Assert.Equal("linux", result.Combinations[0].Values["os"].Text);
        Assert.Equal("mac", result.Combinations[1].Values["os"].Text);
        Assert.True(result.Combinations[1].AddedKeys.Contains("os"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveOnKeysAndUsesActionsEqualityOnValues()
    {
        var result = Expand(
            [Dimension("OS", "Linux")],
            include: [Entry(("os", "linux"), ("coverage", "on"))]);

        // 'os' equals the declared dimension 'OS', and the value compares equal case-insensitively, so
        // this is a filter that matches — and 'coverage' is the only extra.
        var combination = Assert.Single(result.Combinations);
        Assert.True(combination.AddedKeys.Contains("coverage"));
        Assert.False(combination.AddedKeys.Contains("os"));
    }

    [Fact]
    public void NumericAndTextMatrixValuesCompareEqualWhenActionsSaysTheyDo()
    {
        var result = Expand(
            [Dimension("node", "20")],
            include: [Entry(("node", 20.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("extra", "yes"))]);

        Assert.Single(result.Combinations);
        Assert.False(result.Combinations[0].AddedKeys.Contains("node"));
    }

    [Fact]
    public void CombinationCapTruncatesAndSaysSo()
    {
        var values = Enumerable.Range(0, 300).Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        var result = Expand([Dimension("i", values)]);

        Assert.Equal(300, result.ProducedCount);
        Assert.Equal(MatrixExpander.MaxCombinations, result.Combinations.Count);
        Assert.True(result.Truncated);
        Assert.Contains(DiagnosticCodes.MatrixCapReached, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void ProductBelowTheCapIsNotMarkedTruncated()
    {
        var values = Enumerable.Range(0, 256).Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        var result = Expand([Dimension("i", values)]);

        Assert.False(result.Truncated);
        Assert.Equal(256, result.Combinations.Count);
    }

    [Fact]
    public void DynamicDimensionRefusesToGuessTheCombinationCount()
    {
        var result = Expand([Dynamic("os", "${{ fromJSON(needs.setup.outputs.matrix) }}"), Dimension("node", "20")]);

        Assert.True(result.IsDynamic);
        Assert.Equal(["os"], result.DynamicDimensions);
        Assert.Null(result.JobCount);
        Assert.Empty(result.Combinations);
        Assert.Contains(DiagnosticCodes.MatrixDynamic, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void CombinationsExposeAContextObjectForExpressions()
    {
        var result = Expand([Dimension("os", "linux"), Dimension("node", "20")]);

        var context = result.Combinations[0].ToContextValue();

        Assert.Equal(GitHubValueKind.Object, context.Kind);
        Assert.True(context.TryGetMember(GitHubValue.FromString("os"), StringComparison.OrdinalIgnoreCase, out var os));
        Assert.Equal("linux", os.String);
    }

    /// <summary>
    /// Mirrors what the loader does to a plain scalar, so these tests exercise the expander with the
    /// same typed values a real workflow produces.
    /// </summary>
    private static class YamlScalarResolverProbe
    {
        internal static GitHubValue Resolve(string text) =>
            text switch
            {
                "true" => GitHubValue.True,
                "false" => GitHubValue.False,
                _ when int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    => GitHubValue.FromNumber(number),
                _ => GitHubValue.FromString(text),
            };
    }
}
