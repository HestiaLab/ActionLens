using System.Text;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Tests;

/// <summary>
/// The hard contract from SECURITY.md: a malformed or undecidable input produces a positioned finding
/// and an <c>unknown</c> with a reason — never an <see cref="DiagnosticCodes.AnalysisFailed"/>, and never
/// an internal type name in a message a reader is meant to understand.
/// </summary>
/// <remarks>
/// Every case here was an observed failure of the 1405-test suite: the workflows are legal enough to
/// reach the analyser, and the engine used to escape through an exception that the catch-all reported as
/// "ActionLens stopped partway through this analysis". Assertions are on codes, reason kinds and
/// positions, not on message wording, except where the wording itself is the defect (an internal type
/// dump reaching the export).
/// </remarks>
public sealed class ContractRegressionTests
{
    private static readonly string[] SortedMatrixKeys = ["alpha", "os", "zeta"];

    private const string JobHead = """
        name: probe
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
        """;

    private static AnalysisReport Analyze(string yaml, string? triggerEvent = null) =>
        WorkflowAnalyzer.Analyze(yaml, "inline.yml", (string?)null, null, triggerEvent);

    private static string Condition(string expression) => JobHead + "\n    if: " + expression + "\n    steps:\n      - run: echo hi\n";

    private static string[] Codes(AnalysisReport report) =>
        report.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray();

    /// <summary>
    /// The fixture form of §8 item 7: one file holding every reproduced failure shape, analysed end to
    /// end. The cases below retest each shape in isolation with a smaller workflow; this one is the
    /// fixture the review asked for, so the input exists outside a string literal and can be analysed
    /// by another tool.
    /// </summary>
    [Fact]
    public void TheContractFixtureAnalyzesEndToEndWithoutInternalFailures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contract", "regressions.yml");
        Assert.True(File.Exists(path), $"Contract fixture is missing at {path}.");
        var yaml = File.ReadAllText(path);

        var report = WorkflowAnalyzer.Analyze(yaml, "regressions.yml", triggerEvent: "push");

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        Assert.Equal(3, report.Diagnostics.Count(diagnostic => diagnostic.Code == DiagnosticCodes.FunctionArity));
        Assert.Contains(DiagnosticCodes.MatrixInvalidShape, Codes(report));
        Assert.Equal(5, report.Jobs.Count);

        // The `branches-ignore`-only trigger cannot be decided without a payload; it must still name
        // the pattern and point at it rather than crash.
        Assert.NotNull(report.Trigger.Verdict);
        Assert.Contains(report.Trigger.Verdict!.Decisions, decision =>
            decision.Filter == "branches" && decision.Pattern == "wip/**" && decision.PatternRange.IsKnown);

        // Every unknown state still carries a reason — the product's central promise.
        Assert.All(report.Jobs.Where(job => job.State == RunState.Unknown), job => Assert.NotNull(job.Reason));
    }

    [Theory]
    [InlineData("join()")]
    [InlineData("toJSON()")]
    [InlineData("fromJSON()")]
    public void AFunctionCalledWithoutArgumentsReportsArityRatherThanAnInternalFailure(string call)
    {
        var report = Analyze(Condition(call));

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        Assert.Contains(DiagnosticCodes.FunctionArity, Codes(report));
        var job = Assert.Single(report.Jobs);
        Assert.NotNull(job.Condition);
        Assert.Equal(TriKind.Unknown, job.Condition.Result);
        Assert.NotNull(job.Condition.Reason);
    }

    [Fact]
    public void APushWithOnlyBranchesIgnoreAndNoPayloadIsUndecidedRatherThanAnInternalFailure()
    {
        const string yaml = """
            name: ignore-only
            on:
              push:
                branches-ignore:
                  - 'wip/**'
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
            """;

        var report = Analyze(yaml, "push");

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        Assert.NotNull(report.Trigger.Verdict);
        var undecided = report.Trigger.Verdict.Decisions
            .Where(decision => decision.Result == TriKind.Unknown)
            .ToArray();
        Assert.NotEmpty(undecided);
        Assert.Contains(undecided, decision => decision.Filter == "branches"
            && decision.Pattern == "wip/**"
            && decision.PatternRange.IsKnown);
    }

    [Fact]
    public void AnIncludeEntryWithKeysDifferingOnlyByCaseIsReportedRatherThanThrown()
    {
        const string yaml = """
            name: case-collision
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest]
                    include:
                      - OS: windows-latest
                        os: ubuntu-latest
                steps:
                  - run: echo hi
            """;

        var report = Analyze(yaml);

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        var finding = Assert.Single(report.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.MatrixInvalidShape);
        Assert.True(finding.Range.IsKnown);
        Assert.True(finding.Message.Contains("case", StringComparison.OrdinalIgnoreCase));
        var job = Assert.Single(report.Jobs);
        Assert.NotNull(job.Matrix);
        Assert.NotEmpty(job.Runs);
    }

    [Fact]
    public void HashFilesWithAnUnknownArgumentKeepsItsOwnReason()
    {
        var parsed = ExpressionParser.ParseCondition("hashFiles(github.event.missing)");
        var root = Assert.IsType<FunctionCallNode>(parsed.Root);
        var evaluated = new ExpressionEvaluator(new UnknownContexts()).Evaluate(root);

        Assert.False(evaluated.IsKnown);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, evaluated.Reason!.Kind);
    }

    [Fact]
    public void AnUnknownPredicateCannotMakeCaseReturnADefiniteBranch()
    {
        // Case() itself would return a definite value if it ever saw an unknown predicate; the early
        // guard in EvaluateFunction is what prevents that. This test locks the guard in place.
        var parsed = ExpressionParser.ParseCondition("case(hashFiles('x') == 'a', 'A', true, 'B', 'C')");
        var root = Assert.IsType<FunctionCallNode>(parsed.Root);
        var evaluated = new ExpressionEvaluator(new UnknownContexts()).Evaluate(root);

        Assert.False(evaluated.IsKnown);
        Assert.Equal(UnknownReasonKind.Propagated, evaluated.Reason!.Kind);
    }

    [Fact]
    public void EveryUnknownReasonPointsIntoTheFileAndNamesAReadablePath()
    {
        const string yaml = """
            name: positioned
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                if: ${{ 'x' == github.event.missing }}
                steps:
                  - run: echo hi
            """;

        var report = Analyze(yaml);
        var job = Assert.Single(report.Jobs);
        var reason = Assert.IsType<UnknownReason>(job.Condition!.Reason);
        var nodes = Flatten(reason).ToArray();

        Assert.True(nodes.Length > 1, "the reason trace must keep its nested causes");
        Assert.All(nodes, node => Assert.True(node.Range.IsKnown));
        Assert.DoesNotContain(nodes, node => node.Range.Start.Line == 1 && node.Range.Start.Column == 1);
        Assert.Contains(nodes, node => node.Message.Contains("github.event.missing", StringComparison.Ordinal));

        var json = AnalysisReportJson.ToJson(report);
        Assert.False(json.Contains("MemberAccessNode", StringComparison.Ordinal));
        Assert.False(json.Contains("ContextNode", StringComparison.Ordinal));
        Assert.False(json.Contains("Node {", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpressionPositionsInsideAValueAreOffsetOntoTheFile()
    {
        // A single-line `if:` used to clamp every token to 1:1. With the value starting at 6:9, a
        // token inside the condition text has to land on line 6, not line 1.
        var parsed = ExpressionParser.ParseCondition("github.event.missing == 'x'", new SourcePosition(6, 9));
        var segment = Assert.Single(parsed.Segments);

        Assert.Equal(6, segment.Range.Start.Line);
        Assert.True(segment.Range.Start.Column >= 9);
    }

    [Fact]
    public void EqualObjectsWithDifferentInsertionOrderHashTheSame()
    {
        // The same two key/value pairs, inserted in opposite orders. Equals() is order-insensitive, so
        // GetHashCode() has to be too — mixing each pair into a running hash used to break that contract.
        static GitHubValue Make(string firstKey, string secondKey) => GitHubValue.FromObject(
            new Dictionary<string, GitHubValue>(StringComparer.Ordinal)
            {
                [firstKey] = GitHubValue.FromString(firstKey),
                [secondKey] = GitHubValue.FromString(secondKey),
            });

        var left = Make("alpha", "beta");
        var right = Make("beta", "alpha");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void DeclaredTriggersFollowDocumentOrderNotDictionaryOrder()
    {
        // Written as workflow_dispatch, push, pull_request; alphabetically it would be pull_request,
        // push, workflow_dispatch. The report has to keep what the file says.
        const string yaml = """
            name: order
            on:
              workflow_dispatch:
              push:
                branches: [main]
              pull_request:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
            """;

        var report = Analyze(yaml);

        Assert.Equal(
            ["workflow_dispatch", "push", "pull_request"],
            report.Trigger.Declared.Select(trigger => trigger.Name).ToArray());
    }

    [Fact]
    public void MatrixRunValuesAreWrittenInAStableKeyOrder()
    {
        const string yaml = """
            name: stable
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest]
                    include:
                      - os: ubuntu-latest
                        zeta: last
                        alpha: first
                steps:
                  - run: echo hi
            """;

        var report = Analyze(yaml);
        var json = AnalysisReportJson.ToJson(report);

        // The in-memory dictionary keeps insertion order (os, then zeta, then alpha); it is the export
        // that must be stable. Read the keys out of the JSON object itself so an unrelated "os"
        // elsewhere in the report cannot stand in for the one inside "values".
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var values = document.RootElement.GetProperty("jobs")[0].GetProperty("runs")[0].GetProperty("values");
        var keys = values.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(SortedMatrixKeys, keys);
    }

    [Fact]
    public void ADeclaredFailFastIsReportedRatherThanSilentlyDropped()
    {
        const string yaml = """
            name: scheduling
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  fail-fast: false
                  matrix:
                    os: [ubuntu-latest]
                steps:
                  - run: echo hi
            """;

        var report = Analyze(yaml);
        var finding = Assert.Single(report.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.MatrixSchedulingNotModelled);

        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
        Assert.True(finding.Range.IsKnown);
        Assert.False(report.IsBlocked);
    }

    [Fact]
    public void AHugeMatrixIsRejectedBeforeAnyCombinationIsBuilt()
    {
        // Ten dimensions of ten values is 10^10 combinations. Building that product before applying
        // the 256 cap used to exhaust memory; the product is now computed arithmetically and refused.
        var dimensions = string.Join("\n", Enumerable.Range(0, 10)
            .Select(index => $"            d{index}: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]"));
        var yaml = "name: bomb\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    strategy:\n      matrix:\n"
            + dimensions + "\n    steps:\n      - run: echo hi\n";

        var report = Analyze(yaml);

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        Assert.Contains(DiagnosticCodes.MatrixCapReached, Codes(report));
        var job = Assert.Single(report.Jobs);
        Assert.NotNull(job.Matrix);
        Assert.Equal(0, job.Matrix.CombinationCount);
        Assert.Empty(job.Runs);
    }

    [Fact]
    public void AnOversizedEventPayloadIsAPositionedFindingNotAnException()
    {
        var payload = "{\"pad\":\"" + new string('x', AnalysisInput.MaxPayloadBytes + 1024) + "\"}";

        var report = WorkflowAnalyzer.Analyze(JobHead + "\n    steps:\n      - run: echo hi\n", "inline.yml", payload);

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        var finding = Assert.Single(report.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.YamlSizeLimit
                && diagnostic.Stage == DiagnosticStage.Scenario);
        Assert.True(finding.Range.IsKnown);
    }

    [Fact]
    public void ACyclicAnchorIsRejectedWithoutAnInternalFailure()
    {
        const string yaml = """
            name: cycle
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps: &loop
                  - run: echo hi
                  - *loop
            """;

        var report = Analyze(yaml);

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        Assert.Contains(DiagnosticCodes.YamlExpansionLimit, Codes(report));
    }

    [Fact]
    public void AnAliasExpansionBombIsRejectedWithoutAnInternalFailure()
    {
        // Each level references the previous one twice, so the expanded graph doubles per line while
        // the text itself stays small. Counting distinct node objects would miss this entirely.
        var yaml = new StringBuilder("name: chain\non: push\nx: &a0 [1]\n");
        for (var level = 1; level <= 20; level++)
        {
            yaml.Append('x').Append(level).Append(": &a").Append(level)
                .Append(" [*a").Append(level - 1).Append(", *a").Append(level - 1).Append("]\n");
        }

        yaml.Append("jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n");

        var report = Analyze(yaml.ToString());

        Assert.DoesNotContain(DiagnosticCodes.AnalysisFailed, Codes(report));
        Assert.Contains(DiagnosticCodes.YamlExpansionLimit, Codes(report));
    }

    private static IEnumerable<UnknownReason> Flatten(UnknownReason reason)
    {
        yield return reason;
        foreach (var cause in reason.Causes)
        {
            foreach (var nested in Flatten(cause))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Every context exists but has no static value, so paths become unknown with a cause trace.</summary>
    private sealed class UnknownContexts : IContextProvider
    {
        public bool IsAvailable(string contextName) => true;

        public bool TryGetContext(string contextName, out GitHubValue context)
        {
            context = GitHubValue.Null;
            return false;
        }

        public UnknownReason ExplainContextUnknown(string contextName) =>
            new(UnknownReasonKind.ValueMissing, $"'{contextName}' has no static value.", SourceRange.Unknown);

        public UnknownReason ExplainUnavailable(string symbolName) =>
            new(UnknownReasonKind.ContextNotAvailable, $"'{symbolName}' is not available here.", SourceRange.Unknown);

        public bool IsFunctionAvailable(string functionName) => true;

        public bool TryGetStatus(StatusFunction statusFunction, out TriValue value)
        {
            value = TriValue.True;
            return true;
        }
    }
}
