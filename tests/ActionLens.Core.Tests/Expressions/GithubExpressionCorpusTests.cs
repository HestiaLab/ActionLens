using System.Text;
using System.Text.Json;
using ActionLens.Core.Expressions;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Tests.Expressions;

/// <summary>
/// Runs the expression cases GitHub publishes for its own reference implementations through
/// ActionLens' parser and evaluator.
/// </summary>
/// <remarks>
/// <c>actions/languageservices</c> states that its <c>expressions/testdata</c> folder "contains test
/// cases that all implementations should pass", so this is the most neutral possible check that the
/// coercion, precedence and function rules implemented here agree with GitHub's. The files are vendored
/// at a pinned commit; see <c>tests/golden/languageservices/README.md</c> for provenance and licence.
/// <para>
/// A case can also be skipped because ActionLens deliberately answers differently: a function that
/// needs the checkout or a finished run stays unknown here, where the corpus expects whatever the
/// machine under test produced. Those exclusions are listed in one place, by rule rather than by case,
/// so they cannot quietly widen.
/// </para>
/// </remarks>
public sealed class GithubExpressionCorpusTests
{
    private static string CorpusDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "golden", "languageservices");

    private static List<string> CaseKeys { get; } = LoadCaseKeys();

    /// <summary>
    /// Case identifiers as <c>file|group|index</c>. The JSON is re-read inside the test so that theory
    /// data stays a plain string, which also keeps the failing case name readable in output.
    /// </summary>
    private static List<string> LoadCaseKeys()
    {
        // A missing corpus used to yield an empty theory and a green run. Failing loudly at type
        // initialisation is the only honest outcome: 0 passing cases is not evidence about semantics.
        if (!Directory.Exists(CorpusDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The vendored expression corpus is missing at {CorpusDirectory}. " +
                "Restore tests/golden/languageservices/ before trusting the expression suite.");
        }

        var keys = new List<string>();
        foreach (var file in Directory.GetFiles(CorpusDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            foreach (var group in document.RootElement.EnumerateObject())
            {
                var index = 0;
                foreach (var unused in group.Value.EnumerateArray())
                {
                    _ = unused;
                    keys.Add($"{Path.GetFileName(file)}|{group.Name}|{index}");
                    index++;
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Splits a case key back into its parts. A group name may itself contain <c>|</c> — the precedence
    /// corpus names its groups after the operators, so <c>operator_precedence_&amp;&amp;_is_higher_than_||</c>
    /// is one group — which means the separators are the first and last characters, not every one.
    /// </summary>
    private static (string File, string Group, int Index) SplitCaseKey(string caseKey)
    {
        var first = caseKey.IndexOf('|');
        var last = caseKey.LastIndexOf('|');
        return (
            caseKey[..first],
            caseKey[(first + 1)..last],
            int.Parse(caseKey[(last + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    public static IEnumerable<object[]> Cases() => CaseKeys.Select(key => new object[] { key });

    /// <summary>
    /// The corpus is a pinned artifact, not a directory we happen to find files in. Every count in the
    /// documentation is asserted here so a re-vendor, a lost file or a widened exclusion rule fails the
    /// suite instead of quietly shrinking what the green run actually proved.
    /// </summary>
    [Fact]
    public void TheVendoredCorpusKeepsItsPinnedShape()
    {
        Assert.True(Directory.Exists(CorpusDirectory), $"Corpus directory is missing: {CorpusDirectory}");
        Assert.Equal(27, Directory.GetFiles(CorpusDirectory, "*.json").Length);
        Assert.Equal(1003, CaseKeys.Count);

        // The exclusion sets are free-form string lists. Locking their contents is what stops someone
        // from adding a marker to get a failing case to go away (CONTRIBUTING.md "What not to do").
        Assert.Equal("hashFiles|success(|failure(|cancelled(|always(", string.Join('|', DeferredFunctions));
        Assert.Equal("maximum allowed memory size", Assert.Single(DeferredRuntimeLimits));
        Assert.Equal("Unrecognized function", Assert.Single(DeferredShortCircuitRejections));
        Assert.Equal("Unrecognized named-value", Assert.Single(DeferredNameRejections));

        var (byRule, strongAssertions) = Classify();
        Assert.Equal(10, byRule);
        Assert.Equal(993, strongAssertions);
        Assert.Equal(1003, byRule + strongAssertions);
    }

    /// <summary>
    /// Counts cases that a documented exclusion rule handles. Six of the ten are not skipped outright —
    /// they take the deliberately weaker "never a confident value" assertion in
    /// <see cref="AssertFailureCase"/> — but all ten are cases the rule, not the semantics, accounts for.
    /// </summary>
    private static (int ByRule, int StrongAssertions) Classify()
    {
        var byRule = 0;
        var total = 0;
        foreach (var file in Directory.GetFiles(CorpusDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            foreach (var group in document.RootElement.EnumerateObject())
            {
                foreach (var testCase in group.Value.EnumerateArray())
                {
                    total++;
                    var expression = testCase.GetProperty("expr").GetString() ?? string.Empty;
                    if (DeferredFunctions.Any(marker => expression.Contains(marker, StringComparison.Ordinal))
                        || IsDeferredEmptyBody(expression))
                    {
                        byRule++;
                        continue;
                    }

                    if (!testCase.TryGetProperty("err", out var error))
                    {
                        continue;
                    }

                    var message = error.GetProperty("value").GetString() ?? string.Empty;
                    if (DeferredRuntimeLimits.Any(marker => message.Contains(marker, StringComparison.Ordinal))
                        || DeferredShortCircuitRejections.Any(marker => message.Contains(marker, StringComparison.Ordinal))
                        || DeferredNameRejections.Any(marker => message.Contains(marker, StringComparison.Ordinal)))
                    {
                        byRule++;
                    }
                }
            }
        }

        return (byRule, total - byRule);
    }

    /// <summary>Expressions whose answer depends on files or on a run, which ActionLens never consults.</summary>
    private static string[] DeferredFunctions { get; } = ["hashFiles", "success(", "failure(", "cancelled(", "always("];

    /// <summary>
    /// Corpus errors that describe a runtime limit rather than a rule of the language, matched on the
    /// corpus's own wording. A run can exhaust its memory on an input that is short, legal and finite, and
    /// no static reading of the expression text can predict which — ActionLens evaluates the same
    /// expression to the value the machine would have held had it survived. Skipping the case is the honest
    /// answer; inventing an allocation budget to match would be guessing.
    /// </summary>
    private static string[] DeferredRuntimeLimits { get; } = ["maximum allowed memory size"];

    /// <summary>
    /// Corpus errors GitHub raises while parsing because a name is not one it knows, matched on the
    /// corpus's own wording. ActionLens deliberately parses the name and answers at evaluation time, because
    /// which names are legal depends on the position the expression sits in and only the evaluator has that
    /// (<c>docs/expression-semantics.md</c>, context availability) — so a corpus case that fails to parse
    /// there is checked here for the property that does carry over: ActionLens must never report a confident
    /// value for it. That is a weaker assertion than the file makes elsewhere, and it is narrowed to exactly
    /// these two messages rather than to error kinds in general.
    /// </summary>
    private static string[] DeferredNameRejections { get; } = ["Unrecognized named-value"];

    /// <summary>
    /// As <see cref="DeferredNameRejections"/>, but for a name the runner never even looks at: a settled
    /// <c>&amp;&amp;</c> or <c>||</c> chain short-circuits and returns an operand, while GitHub rejects the
    /// whole expression before evaluation, so the two answers are <c>false</c> and an error rather than
    /// unknown and an error. Nothing about the operand is wrong — the disagreement is that ActionLens
    /// reports the condition while GitHub would not have run the workflow at all — so the case is set aside
    /// in full and recorded in <c>docs/expression-semantics.md</c> instead of being asserted away.
    /// </summary>
    private static string[] DeferredShortCircuitRejections { get; } = ["Unrecognized function"];

    /// <summary>An expression the corpus evaluates but ActionLens does not parse at all.</summary>
    private static bool IsDeferredEmptyBody(string expression) =>
        // The corpus treats an empty body as the expression that produces null; a missing value in a
        // workflow is the schema stage's question here, and ParseCondition deliberately returns no tree and
        // no finding for it, so there is nothing to compare.
        expression.Trim().Length == 0;

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesGitHubReferenceBehaviour(string caseKey)
    {
        var (file, group, index) = SplitCaseKey(caseKey);
        var expression = ReadCase(file, group, index);
        if (expression is null)
        {
            Assert.Fail($"Corpus case {caseKey} could not be read from {CorpusDirectory}.");
            return;
        }

        if (DeferredFunctions.Any(marker => expression.Contains(marker, StringComparison.Ordinal)))
        {
            return;
        }

        var stored = ReadCaseElement(file, group, index);
        Assert.True(stored.HasValue, $"Corpus case {caseKey} could not be read.");
        var testCase = stored!.Value;

        if (IsDeferredEmptyBody(expression))
        {
            return;
        }

        var outcome = ExpressionParser.ParseCondition(expression);

        if (testCase.TryGetProperty("err", out var error))
        {
            AssertFailureCase(outcome, testCase, expression, caseKey, error);
            return;
        }

        Assert.Empty(outcome.Diagnostics);
        var evaluator = new ExpressionEvaluator(CorpusContext.From(testCase));
        var result = evaluator.Evaluate(outcome.Root!);
        Assert.NotNull(outcome.Root);
        Assert.True(result.IsKnown, $"'{expression}' should have a value but reported unknown: {result.Reason?.Message}");
        AssertExpectedValue(result.Value!, testCase.GetProperty("result"), $"'{expression}' ({caseKey})");
    }

    /// <summary>
    /// A corpus case that expects an error has to be answered by ActionLens with either a parse
    /// diagnostic or an unknown — never with a confident value. Which of the two, and the exact wording,
    /// is not asserted: the corpus messages are GitHub's, not ours.
    /// </summary>
    private static void AssertFailureCase(ExpressionParseOutcome outcome, JsonElement testCase, string expression, string caseKey, JsonElement error)
    {
        var corpusMessage = error.GetProperty("value").GetString() ?? string.Empty;
        if (DeferredRuntimeLimits.Any(marker => corpusMessage.Contains(marker, StringComparison.Ordinal))
            || DeferredShortCircuitRejections.Any(marker => corpusMessage.Contains(marker, StringComparison.Ordinal)))
        {
            return;
        }

        var deferredByName = DeferredNameRejections.Any(marker => corpusMessage.Contains(marker, StringComparison.Ordinal));
        if (error.GetProperty("kind").GetString() is "parsing" or "lexing" && !deferredByName)
        {
            Assert.True(outcome.Diagnostics.Count > 0 || outcome.Root is null,
                $"'{expression}' ({caseKey}) should not parse cleanly; corpus says: {corpusMessage}");
            return;
        }

        if (outcome.Root is null)
        {
            return;
        }

        var evaluated = new ExpressionEvaluator(CorpusContext.From(testCase)).Evaluate(outcome.Root);
        Assert.True(!evaluated.IsKnown || outcome.Diagnostics.Count > 0,
            $"'{expression}' ({caseKey}) should not produce a definite value; corpus says: {corpusMessage}. ActionLens produced {evaluated.Value}.");
    }

    private static string? ReadCase(string file, string group, int index)
    {
        var element = ReadCaseElement(file, group, index);
        return element?.GetProperty("expr").GetString();
    }

    private static JsonElement? ReadCaseElement(string file, string group, int index)
    {
        var path = Path.Combine(CorpusDirectory, file);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!document.RootElement.TryGetProperty(group, out var cases))
        {
            return null;
        }

        var position = 0;
        foreach (var candidate in cases.EnumerateArray())
        {
            if (position++ == index)
            {
                return candidate.Clone();
            }
        }

        return null;
    }

    private static void AssertExpectedValue(GitHubValue actual, JsonElement expected, string label)
    {
        // Only the whole result carries a kind: the elements of an array and the members of an object are
        // written as plain JSON, so a nested expectation is read as a value of whatever the JSON says it
        // is. Looking for "kind" inside one throws before anything is compared.
        if (expected.ValueKind != JsonValueKind.Object || !expected.TryGetProperty("kind", out _))
        {
            AssertRawExpectedValue(actual, expected, label);
            return;
        }

        var kind = expected.GetProperty("kind").GetString()!;
        var payload = expected.TryGetProperty("value", out var value) ? value : default;
        switch (kind)
        {
            case "Null":
                Assert.Equal(GitHubValueKind.Null, actual.Kind);
                break;
            case "Boolean":
                Assert.True(actual.Kind == GitHubValueKind.Boolean, $"{label}: expected Boolean, got {actual.Kind} ({actual})");
                Assert.Equal(payload.GetBoolean(), actual.Boolean);
                break;
            case "Number":
                Assert.True(actual.Kind == GitHubValueKind.Number, $"{label}: expected Number, got {actual.Kind} ({actual})");
                Assert.Equal(payload.GetDouble(), actual.Number, precision: 9);
                break;
            case "String":
                var text = actual.Kind == GitHubValueKind.String ? actual.String : actual.Stringify();
                Assert.Equal(payload.GetString(), text);
                break;
            case "Array":
                Assert.Equal(GitHubValueKind.Array, actual.Kind);
                Assert.Equal(payload.GetArrayLength(), actual.Array.Count);
                for (var i = 0; i < actual.Array.Count; i++)
                {
                    AssertExpectedValue(actual.Array[i], payload[i], $"{label}[{i}]");
                }

                break;
            case "Object":
                Assert.Equal(GitHubValueKind.Object, actual.Kind);
                foreach (var property in payload.EnumerateObject())
                {
                    Assert.True(actual.Object.TryGetValue(property.Name, out var member), $"{label}: missing '{property.Name}'");
                    AssertExpectedValue(member!, property.Value, $"{label}.{property.Name}");
                }

                break;
            default:
                Assert.Fail($"{label}: unknown expected kind '{kind}'");
                break;
        }
    }

    /// <summary>
    /// Compares a nested expectation, which has no tagged kind. Its kind comes from the JSON token and is
    /// required, not coerced: a wildcard path that yields the number <c>1</c> where the corpus wrote
    /// <c>"1"</c> is a difference worth failing on, even though the top-level <c>String</c> case above
    /// accepts a value that converts to the same text.
    /// </summary>
    private static void AssertRawExpectedValue(GitHubValue actual, JsonElement expected, string label)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.Equal(GitHubValueKind.Null, actual.Kind);
                break;
            case JsonValueKind.True or JsonValueKind.False:
                Assert.True(actual.Kind == GitHubValueKind.Boolean, $"{label}: expected Boolean, got {actual.Kind} ({actual})");
                Assert.Equal(expected.GetBoolean(), actual.Boolean);
                break;
            case JsonValueKind.Number:
                Assert.True(actual.Kind == GitHubValueKind.Number, $"{label}: expected Number, got {actual.Kind} ({actual})");
                Assert.Equal(expected.GetDouble(), actual.Number, precision: 9);
                break;
            case JsonValueKind.String:
                Assert.True(actual.Kind == GitHubValueKind.String, $"{label}: expected String, got {actual.Kind} ({actual})");
                Assert.Equal(expected.GetString(), actual.String);
                break;
            case JsonValueKind.Array:
                Assert.Equal(GitHubValueKind.Array, actual.Kind);
                Assert.Equal(expected.GetArrayLength(), actual.Array.Count);
                var index = 0;
                foreach (var element in expected.EnumerateArray())
                {
                    AssertRawExpectedValue(actual.Array[index], element, $"{label}[{index}]");
                    index++;
                }

                break;
            default:
                Assert.Equal(GitHubValueKind.Object, actual.Kind);
                foreach (var property in expected.EnumerateObject())
                {
                    Assert.True(actual.Object.TryGetValue(property.Name, out var member), $"{label}: missing '{property.Name}'");
                    AssertRawExpectedValue(member!, property.Value, $"{label}.{property.Name}");
                }

                break;
        }
    }

    /// <summary>
    /// Contexts as the corpus declares them: a named context the case provides is available and known,
    /// and one it does not provide is refused the way GitHub refuses an unrecognized named-value.
    /// </summary>
    private sealed class CorpusContext : IContextProvider
    {
        private readonly Dictionary<string, GitHubValue> _contexts = new(StringComparer.OrdinalIgnoreCase);

        public static CorpusContext From(JsonElement testCase)
        {
            var context = new CorpusContext();
            if (testCase.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return context;
            }

            if (testCase.TryGetProperty("contexts", out var declared))
            {
                foreach (var property in declared.EnumerateObject())
                {
                    context._contexts[property.Name] = CorpusValue.Read(property.Value);
                }
            }

            return context;
        }

        public bool IsAvailable(string contextName) => _contexts.ContainsKey(contextName);

        public bool TryGetContext(string contextName, out GitHubValue context)
        {
            var found = _contexts.TryGetValue(contextName, out var value);
            context = value ?? GitHubValue.Null;
            return found;
        }

        public UnknownReason ExplainContextUnknown(string contextName) => new(
            UnknownReasonKind.ValueMissing,
            $"No value is given for '{contextName}'.",
            SourceRange.Unknown);

        public UnknownReason ExplainUnavailable(string symbolName) => new(
            UnknownReasonKind.ContextNotAvailable,
            $"Unrecognized named-value: '{symbolName}'.",
            SourceRange.Unknown);

        public bool IsFunctionAvailable(string functionName) => true;

        public bool TryGetStatus(StatusFunction function, out TriValue value)
        {
            // The corpus carries no scenario. "Everything succeeded so far" is the only reading under
            // which these cases mean anything, and the deferred-function filter keeps the cases that
            // would depend on it out of the run anyway.
            value = function is StatusFunction.Success or StatusFunction.Always ? TriValue.True : TriValue.False;
            return true;
        }
    }

    private static class CorpusValue
    {
        internal static GitHubValue Read(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Null => GitHubValue.Null,
            JsonValueKind.True => GitHubValue.True,
            JsonValueKind.False => GitHubValue.False,
            JsonValueKind.String => GitHubValue.FromString(element.GetString()!),
            JsonValueKind.Number => GitHubValue.FromNumber(element.GetDouble()),
            JsonValueKind.Array => GitHubValue.FromArray(element.EnumerateArray().Select(Read).ToArray()),
            _ => ReadObject(element),
        };

        private static GitHubValue ReadObject(JsonElement element)
        {
            var members = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                members[property.Name] = Read(property.Value);
            }

            return GitHubValue.FromObject(members);
        }
    }
}
