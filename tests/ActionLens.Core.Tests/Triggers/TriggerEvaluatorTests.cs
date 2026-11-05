using System.Text.Json;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Triggers;
using ActionLens.Core.Yaml;

namespace ActionLens.Core.Tests.Triggers;

/// <summary>
/// The documented <c>on:</c> rules, applied: which filter answered, which pattern decided it, where that
/// pattern sits, and every case the module refuses to answer definitely.
/// </summary>
/// <remarks>
/// <para>
/// Each expectation was worked out from <c>docs/expression-semantics.md</c> ("Trigger filters") and the
/// limit notes in [Workflow syntax] and [Events that trigger workflows], then written as a literal. The
/// line numbers come from the file the helper builds, which is always
/// </para>
/// <code>
/// 1  name: t
/// 2  on:
/// 3..  the on-block lines passed in
///      jobs:  (then five more lines)
/// </code>
/// <para>
/// so the second pattern of a <c>branches:</c> list under <c>push:</c> sits on line 6 — and the assertions
/// say so rather than reading a position off the engine.
/// </para>
/// </remarks>
public sealed class TriggerEvaluatorTests
{
    private const string PushMain = """{ "event_name": "push", "ref": "refs/heads/main" }""";

    private sealed record Case(TriggerEvaluation Evaluation, WorkflowLoadResult Load);

    /// <summary>Builds a one-job workflow around the given <c>on:</c> lines and asks about one event.</summary>
    private static Case Analyse(string? eventName, string? payloadJson, params string[] onLines)
    {
        var load = Load(onLines);
        Assert.NotNull(load.Workflow);
        var workflow = load.Workflow!;
        var input = AnalysisInput.FromEventPayload(payloadJson, "t");
        var payload = TriggerPayload.FromContext(input.GitHub, input.Inputs);

        return new Case(TriggerEvaluator.Evaluate(workflow.Triggers, eventName, payload), load);
    }

    /// <summary>Reads the same file without evaluating it, for the cases where the YAML is the point.</summary>
    private static WorkflowLoadResult Load(params string[] onLines)
    {
        var lines = new List<string> { "name: t", "on:" };
        lines.AddRange(onLines);
        lines.AddRange(
            [
                "jobs:",
                "  build:",
                "    runs-on: ubuntu-latest",
                "    steps:",
                "      - run: echo",
            ]);
        return WorkflowLoader.Load(string.Join('\n', lines), "test.yml");
    }

    private static TriggerVerdict Verdict(Case study)
    {
        Assert.NotNull(study.Evaluation.Verdict);
        return study.Evaluation.Verdict!;
    }

    private static FilterDecision Decision(Case study, string filter) =>
        Verdict(study).Decisions.Single(decision => decision.Filter == filter);

    private static bool HasDecision(Case study, string filter) =>
        Verdict(study).Decisions.Any(decision => decision.Filter == filter);

    /// <summary>Asserts the verdict is <c>unknown</c> and hands back the reason, which the tests then read.</summary>
    private static UnknownReason RunsReason(Case study)
    {
        var verdict = Verdict(study);
        Assert.Equal(TriKind.Unknown, verdict.Runs.Kind);
        Assert.NotNull(verdict.Runs.Reason);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Runs.Reason!.Message));
        return verdict.Runs.Reason!;
    }

    private static UnknownReasonKind DeepestKind(UnknownReason reason)
    {
        var current = reason;
        while (current.Causes.Count > 0)
        {
            current = current.Causes[^1];
        }

        return current.Kind;
    }

    private static string ReasonText(UnknownReason? reason) => reason is null
        ? string.Empty
        : string.Join(' ', new[] { reason.Message }.Concat(reason.Causes.Select(ReasonText)));

    // ------------------------------------------------------------------ schema findings

    [Fact]
    public void BranchesAndBranchesIgnoreTogetherIsARejectedWorkflowAtTheLaterList()
    {
        var study = Analyse("push", null,
            "  push:",              // 3
            "    branches:",        // 4
            "      - main",         // 5
            "    branches-ignore:", // 6
            "      - dev");         // 7

        var finding = Assert.Single(study.Evaluation.Diagnostics);

        // GitHub does not allow both for one event, and the line the author has to delete is the second list.
        Assert.Equal(DiagnosticCodes.TriggerFilterConflict, finding.Code);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal(DiagnosticStage.Schema, finding.Stage);
        Assert.Equal("push", finding.Scope);
        Assert.Equal(7, finding.Range.Start.Line);
        Assert.Equal(9, finding.Range.Start.Column);
        Assert.Contains("branches-ignore", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TagsAndPathsPairsAreCheckedTheSameWay()
    {
        var tags = Analyse("push", null, "  push:", "    tags:", "      - v1", "    tags-ignore:", "      - v0");
        var paths = Analyse("push", null, "  push:", "    paths:", "      - src/**", "    paths-ignore:", "      - docs/**");

        var tagFinding = Assert.Single(tags.Evaluation.Diagnostics);
        var pathFinding = Assert.Single(paths.Evaluation.Diagnostics);
        Assert.Equal(DiagnosticCodes.TriggerFilterConflict, tagFinding.Code);
        Assert.Equal(DiagnosticCodes.TriggerFilterConflict, pathFinding.Code);

        // Both point at the second list's first pattern, on line 7 of the generated file.
        Assert.Equal(7, tagFinding.Range.Start.Line);
        Assert.Equal(7, pathFinding.Range.Start.Line);
    }

    [Fact]
    public void TheConflictingListWrittenFirstIsTheOneThatStays()
    {
        // The finding names whichever list came second in the text, not whichever the model stores second.
        var study = Analyse("push", null,
            "  push:", "    branches-ignore:", "      - dev", "    branches:", "      - main");

        Assert.Equal(7, Assert.Single(study.Evaluation.Diagnostics).Range.Start.Line);
    }

    [Fact]
    public void TagsUnderAPullRequestIsNotAValidKeyAtAll()
    {
        var study = Analyse("pull_request", null, "  pull_request:", "    tags:", "      - v1");

        var finding = Assert.Single(study.Evaluation.Diagnostics);
        Assert.Equal(DiagnosticCodes.TriggerFilterUnsupported, finding.Code);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Contains("pull_request", finding.Message, StringComparison.Ordinal);

        // The position is the key's first pattern: the trigger model keeps a range per pattern, and the
        // task's rule is that a rejected filter is reported "with the pattern's position".
        Assert.Equal(5, finding.Range.Start.Line);
    }

    [Fact]
    public void WorkflowDispatchTakesOnlyInputs()
    {
        var study = Analyse("workflow_dispatch", null,
            "  workflow_dispatch:", "    branches:", "      - main");

        var finding = Assert.Single(study.Evaluation.Diagnostics);
        Assert.Equal(DiagnosticCodes.TriggerFilterUnsupported, finding.Code);
        Assert.Contains("takes only 'inputs'", finding.Message, StringComparison.Ordinal);
        Assert.Equal(5, finding.Range.Start.Line);
    }

    [Fact]
    public void AScheduleWithAFilterKeyIsRejectedButCronAloneIsFine()
    {
        // The documented shape is a list of cron entries, and it earns no finding.
        var clean = Analyse("schedule", null, "  schedule:", "    - cron: '0 0 * * *'");
        Assert.Empty(clean.Evaluation.Diagnostics);
        Assert.Equal("0 0 * * *", Decision(clean, "schedule").Pattern);

        // A schedule written as a mapping of filters is the shape GitHub does not accept; the loader
        // reports the wrong shape and the trigger layer reports the key that has no place here.
        var study = Analyse("schedule", null,
            "  schedule:", "    cron: '0 0 * * *'", "    branches:", "      - main");

        var finding = Assert.Single(study.Evaluation.Diagnostics, f => f.Code == DiagnosticCodes.TriggerFilterUnsupported);
        Assert.Contains("'schedule' takes only cron entries", finding.Message, StringComparison.Ordinal);
        Assert.Equal(6, finding.Range.Start.Line);
    }

    [Fact]
    public void ANegatedPatternNeedsAnUnPrefixedPatternBesideIt()
    {
        var study = Analyse("push", PushMain, "  push:", "    branches:", "      - '!renovate/**'");

        var finding = Assert.Single(study.Evaluation.Diagnostics);
        Assert.Equal(DiagnosticCodes.TriggerPatternInvalid, finding.Code);
        Assert.Contains("at least one un-prefixed pattern", finding.Message, StringComparison.Ordinal);
        Assert.Equal(5, finding.Range.Start.Line);
    }

    [Fact]
    public void AnUnreadablePatternIsAFindingNotASilentNoMatch()
    {
        var study = Analyse("push", PushMain, "  push:", "    branches:", "      - '[z-a]'");

        var finding = Assert.Single(study.Evaluation.Diagnostics);
        Assert.Equal(DiagnosticCodes.TriggerPatternInvalid, finding.Code);
        Assert.Contains("reversed", finding.Message, StringComparison.Ordinal);
        Assert.Equal(5, finding.Range.Start.Line);

        // The pattern that could not be read never claims a match, so the run is refused rather than faked.
        Assert.Equal(TriKind.False, Verdict(study).Runs.Kind);
    }

    [Fact]
    public void QuotedPatternsAreReadAsAuthoredAndBareOnesDoNotSlipThrough()
    {
        // The YAML quoting trap: a plain scalar may not begin with `*`, `[` or `!`, so `'**/README.md'` is a
        // pattern and an unquoted one is a file that does not load as a workflow at all.
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": ["src/README.md"] }""";
        var quoted = Analyse("push", Payload, "  push:", "    paths:", "      - '**/README.md'");
        Assert.Empty(quoted.Evaluation.Diagnostics);
        Assert.Equal(TriKind.True, Verdict(quoted).Runs.Kind);
        Assert.Equal("**/README.md", Decision(quoted, "paths").Pattern);

        var bare = Load("  push:", "    paths:", "      - **/README.md");
        Assert.Null(bare.Workflow);
        Assert.Contains(bare.Diagnostics, finding => finding.Stage == DiagnosticStage.Yaml);
    }

    // ------------------------------------------------------------------ branches AND paths

    [Fact]
    public void BranchesAndPathsAreAndedSoBothMustBeSatisfied()
    {
        const string DocsOnly = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": ["docs/readme.md"] }""";
        var study = Analyse("push", DocsOnly,
            "  push:",           // 3
            "    branches:",     // 4
            "      - main",      // 5
            "    paths:",        // 6
            "      - 'src/**'"); // 7

        Assert.Equal(TriKind.False, Verdict(study).Runs.Kind);

        // The branch filter said yes; the path filter is the one that stopped the run, on line 7's pattern.
        var branches = Decision(study, "branches");
        Assert.Equal(TriKind.True, branches.Result);
        Assert.Equal("main", branches.Pattern);
        Assert.Equal(5, branches.PatternRange.Start.Line);

        var paths = Decision(study, "paths");
        Assert.Equal(TriKind.False, paths.Result);
        Assert.Equal("docs/readme.md", paths.Value);
        Assert.Equal("src/**", paths.Pattern);
        Assert.Equal(7, paths.PatternRange.Start.Line);
        Assert.Equal("paths", Verdict(study).SaidNo?.Filter);

        // The same event with one of the listed files changes the answer, which is the AND working.
        const string WithSource = """
            { "event_name": "push", "ref": "refs/heads/main",
              "changed_files": ["docs/readme.md", "src/App.cs"] }
            """;
        var both = Analyse("push", WithSource,
            "  push:", "    branches:", "      - main", "    paths:", "      - 'src/**'");
        Assert.Equal(TriKind.True, Verdict(both).Runs.Kind);
        Assert.Equal("src/App.cs", Decision(both, "paths").Value);
        Assert.Null(Verdict(both).SaidNo);
    }

    [Fact]
    public void PathsIgnoreIsTheMirrorImageOfPaths()
    {
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": ["docs/readme.md"] }""";
        var study = Analyse("push", Payload, "  push:", "    paths-ignore:", "      - 'docs/**'");

        Assert.Equal(TriKind.False, Verdict(study).Runs.Kind);
        var decision = Decision(study, "paths-ignore");
        Assert.Equal(FilterListMode.Ignore, decision.Mode);
        Assert.Equal("docs/**", decision.Pattern);
        Assert.Equal("docs/readme.md", decision.Value);

        // The same list lets through a file it does not name.
        const string Other = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": ["src/App.cs"] }""";
        var cleared = Analyse("push", Other, "  push:", "    paths-ignore:", "      - 'docs/**'");
        Assert.Equal(TriKind.True, Verdict(cleared).Runs.Kind);
    }

    [Fact]
    public void AFilePatternIsAnchoredAtTheRepositoryRoot()
    {
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": ["nested/README.md"] }""";
        var study = Analyse("push", Payload, "  push:", "    paths:", "      - README.md");

        // `README.md` is the root file; a change under `nested/` does not satisfy it.
        Assert.Equal(TriKind.False, Verdict(study).Runs.Kind);
        Assert.Equal("README.md", Decision(study, "paths").Pattern);
    }

    // ------------------------------------------------------------------ negation ordering

    [Fact]
    public void ANegativePatternAfterAPositiveMatchExcludesTheRef()
    {
        var study = Analyse("push", PushMain,
            "  push:", "    branches:", "      - main", "      - '!ma*'");

        Assert.Equal(TriKind.False, Verdict(study).Runs.Kind);
        var decision = Decision(study, "branches");
        Assert.Equal("!ma*", decision.Pattern);
        Assert.Equal(6, decision.PatternRange.Start.Line);
        Assert.Contains("!ma*", decision.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void APositivePatternAfterANegativeMatchLetsTheRefBackIn()
    {
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/feature/hotfix" }""";
        var study = Analyse("push", Payload,
            "  push:", "    branches:", "      - '!feature/**'", "      - feature/hotfix");

        Assert.Equal(TriKind.True, Verdict(study).Runs.Kind);
        Assert.Equal("feature/hotfix", Decision(study, "branches").Pattern);
        Assert.Equal(6, Decision(study, "branches").PatternRange.Start.Line);

        // The same two patterns the other way round exclude: order is the rule, not the pair.
        var reversed = Analyse("push", Payload,
            "  push:", "    branches:", "      - feature/hotfix", "      - '!feature/**'");
        Assert.Equal(TriKind.False, Verdict(reversed).Runs.Kind);
        Assert.Equal("!feature/**", Decision(reversed, "branches").Pattern);
    }

    [Fact]
    public void AnIgnoreListWithANegationPutsTheExcludedRefBackIn()
    {
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/renovate/keep" }""";
        var study = Analyse("push", Payload,
            "  push:", "    branches-ignore:", "      - renovate/**", "      - '!renovate/keep'");

        Assert.Equal(TriKind.True, Verdict(study).Runs.Kind);

        const string Dropped = """{ "event_name": "push", "ref": "refs/heads/renovate/drop" }""";
        var excluded = Analyse("push", Dropped,
            "  push:", "    branches-ignore:", "      - renovate/**", "      - '!renovate/keep'");
        Assert.Equal(TriKind.False, Verdict(excluded).Runs.Kind);
    }

    // ------------------------------------------------------------------ push: branch versus tag

    [Fact]
    public void APushThatDeclaresOnlyTagsNeverRunsForABranchAndViceVersa()
    {
        const string TagPush = """{ "event_name": "push", "ref": "refs/tags/v1.2" }""";

        var tagsOnly = Analyse("push", PushMain, "  push:", "    tags:", "      - v*");
        Assert.Equal(TriKind.False, Verdict(tagsOnly).Runs.Kind);
        var branchDecision = Decision(tagsOnly, "branches");
        Assert.False(branchDecision.Consulted);
        Assert.Equal(TriKind.False, branchDecision.Result);
        Assert.Contains("only tag filters", branchDecision.Note, StringComparison.Ordinal);

        var branchesOnly = Analyse("push", TagPush, "  push:", "    branches:", "      - main");
        Assert.Equal(TriKind.False, Verdict(branchesOnly).Runs.Kind);
        Assert.Contains("a tag push never runs", Decision(branchesOnly, "tags").Note, StringComparison.Ordinal);

        // Declaring both kinds lets both through, each matched against its own key.
        var bothKinds = Analyse("push", TagPush, "  push:", "    tags:", "      - v*", "    branches:", "      - main");
        Assert.Equal(TriKind.True, Verdict(bothKinds).Runs.Kind);
        Assert.Equal("v*", Decision(bothKinds, "tags").Pattern);
        Assert.False(HasDecision(bothKinds, "branches"));

        // Declaring neither runs for either kind, and no filter is consulted at all.
        var neither = Analyse("push", TagPush, "  push:");
        Assert.Equal(TriKind.True, Verdict(neither).Runs.Kind);
        Assert.Empty(Verdict(neither).Decisions);
    }

    [Fact]
    public void PathFiltersAreNotEvaluatedForATagPush()
    {
        const string TagPush = """{ "event_name": "push", "ref": "refs/tags/v1.2" }""";
        var study = Analyse("push", TagPush, "  push:", "    tags:", "      - v*", "    paths:", "      - 'src/**'");

        // There is no changed-files list, and that does not matter: GitHub never reaches the key here.
        Assert.Equal(TriKind.True, Verdict(study).Runs.Kind);
        var paths = Decision(study, "paths");
        Assert.False(paths.Consulted);
        Assert.Equal(TriKind.True, paths.Result);
        Assert.Contains("tag push", paths.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void APushWithNoRefInThePayloadCannotBeDecided()
    {
        var study = Analyse("push", """{ "event_name": "push" }""", "  push:", "    branches:", "      - main");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.ValueMissing, reason.Kind);
        Assert.Contains("branch or a tag", reason.Message, StringComparison.Ordinal);

        // The reason names the filter it blocks, not just the missing field.
        Assert.Contains("branches", reason.Message, StringComparison.Ordinal);
        Assert.Equal(5, reason.Range.Start.Line);
    }

    // ------------------------------------------------------------------ pull_request

    [Fact]
    public void BranchesFilterTheBaseBranchOfAPullRequestNotTheHead()
    {
        const string OntoMain = """
            { "event_name": "pull_request",
              "pull_request": { "base": { "ref": "main" }, "head": { "ref": "feature/x" } } }
            """;
        var study = Analyse("pull_request", OntoMain, "  pull_request:", "    branches:", "      - main");

        Assert.Equal(TriKind.True, Verdict(study).Runs.Kind);
        var decision = Decision(study, "branches");
        Assert.Equal("main", decision.Value);
        Assert.Equal("main", decision.Pattern);

        // The head branch is in the same payload and is not what the key reads.
        const string OntoRelease = """
            { "event_name": "pull_request",
              "pull_request": { "base": { "ref": "release/10" }, "head": { "ref": "main" } } }
            """;
        var headOnly = Analyse("pull_request", OntoRelease, "  pull_request:", "    branches:", "      - main");
        Assert.Equal(TriKind.False, Verdict(headOnly).Runs.Kind);
        Assert.Equal("release/10", Decision(headOnly, "branches").Value);
    }

    [Fact]
    public void APullRequestWithoutABaseBranchIsUnknownAndSaysWhatItNeeds()
    {
        var study = Analyse("pull_request", """{ "event_name": "pull_request" }""",
            "  pull_request:", "    branches:", "      - main");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.ValueMissing, reason.Kind);
        Assert.Contains("base_ref", reason.Message, StringComparison.Ordinal);

        // The docs' other half: the head branch needs a condition on github.head_ref, not a filter.
        Assert.Contains("github.head_ref", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TagsUnderAPullRequestAreReportedAndNeverConsulted()
    {
        const string Payload = """
            { "event_name": "pull_request",
              "pull_request": { "base": { "ref": "main" } } }
            """;
        var study = Analyse("pull_request", Payload,
            "  pull_request:", "    tags:", "      - v*", "    branches:", "      - main");

        Assert.Single(study.Evaluation.Diagnostics);
        Assert.Equal(TriKind.True, Verdict(study).Runs.Kind);
        Assert.False(Decision(study, "tags").Consulted);
    }

    // ------------------------------------------------------------------ activity types

    [Fact]
    public void TypesIsMatchedAgainstTheActivityTypeThePayloadNames()
    {
        const string Opened = """{ "event_name": "pull_request", "action": "opened", "base_ref": "main" }""";
        const string Synced = """{ "event_name": "pull_request", "action": "synchronize", "base_ref": "main" }""";

        var opened = Analyse("pull_request", Opened,
            "  pull_request:", "    types:", "      - opened", "      - synchronize");
        Assert.Equal(TriKind.True, Verdict(opened).Runs.Kind);
        Assert.Equal("opened", Decision(opened, "types").Pattern);

        var synced = Analyse("pull_request", Synced, "  pull_request:", "    types:", "      - opened");
        Assert.Equal(TriKind.False, Verdict(synced).Runs.Kind);
        Assert.Equal("types", Verdict(synced).SaidNo?.Filter);
        Assert.Equal(5, Decision(synced, "types").PatternRange.Start.Line);
    }

    [Fact]
    public void ADeclaredTypesFilterWithoutAnActionIsUnknown()
    {
        var study = Analyse("pull_request", """{ "event_name": "pull_request", "base_ref": "main" }""",
            "  pull_request:", "    types:", "      - opened");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.ValueMissing, reason.Kind);
        Assert.Contains("'action'", reason.Message, StringComparison.Ordinal);
        Assert.Contains("types", reason.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ forced unknowns

    [Fact]
    public void APathFilterWithoutAnyChangedFilesListIsUnknown()
    {
        var study = Analyse("push", PushMain, "  push:", "    paths:", "      - 'src/**'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.ValueMissing, reason.Kind);
        Assert.Contains("changed_files", reason.Message, StringComparison.Ordinal);
        Assert.Contains("'paths'", reason.Message, StringComparison.Ordinal);
        Assert.Equal(5, reason.Range.Start.Line);

        // The decision carries the same sentence, so a reader cannot see "unknown" without the why.
        Assert.Equal(reason.Message, Decision(study, "paths").Note);
        Assert.Equal(reason, Decision(study, "paths").Reason);
    }

    [Fact]
    public void ACountOfChangedFilesIsNotAListOfThem()
    {
        // A real pull_request body carries `changed_files` as a number: the size of the diff, not its names.
        var study = Analyse("pull_request", """{ "event_name": "pull_request", "changed_files": 250 }""",
            "  pull_request:", "    paths:", "      - 'src/**'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.ValueMissing, reason.Kind);
        Assert.Contains("250", reason.Message, StringComparison.Ordinal);
        Assert.Contains("no names", reason.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "event_name": "push", "ref": "refs/heads/dev", "commits_over_limit": true }""")]
    [InlineData("""{ "event_name": "push", "ref": "refs/heads/dev", "commit_count": 1500 }""")]
    [InlineData("""{ "event_name": "push", "ref": "refs/heads/dev", "diff_timed_out": true }""")]
    public void MoreThanAThousandCommitsOrATimedOutDiffRunsWhateverTheFiltersSay(string payload)
    {
        var study = Analyse("push", payload,
            "  push:", "    branches:", "      - main", "    paths:", "      - 'src/**'");

        // The ref filter says no and the path filter cannot be read; GitHub's documented limit says the run
        // happened regardless, so neither answer can be reported and the verdict is unknown.
        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, DeepestKind(reason));
        Assert.Contains("always run", reason.Message, StringComparison.Ordinal);
        Assert.Equal(TriKind.False, Decision(study, "branches").Result);
        Assert.Equal(TriKind.Unknown, Decision(study, "paths").Result);
    }

    [Fact]
    public void ATruncatedDiffOnTheHostedServiceIsUnknownAndNamesTheCeiling()
    {
        const string Payload = """
            { "event_name": "push", "ref": "refs/heads/main",
              "changed_files": ["src/App.cs"], "diff_truncated": true }
            """;
        var study = Analyse("push", Payload, "  push:", "    paths:", "      - 'src/**'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, reason.Kind);
        Assert.Contains("3,000", reason.Message, StringComparison.Ordinal);
        Assert.Contains("does not run", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGitHubEnterpriseServerDiffIsMeasuredAgainstThreeHundredFiles()
    {
        const string Payload = """
            { "event_name": "push", "ref": "refs/heads/main",
              "changed_files": ["src/App.cs"], "diff_truncated": true, "ghes": true }
            """;
        var study = Analyse("push", Payload, "  push:", "    paths:", "      - 'src/**'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, reason.Kind);
        Assert.Contains("300", reason.Message, StringComparison.Ordinal);
        Assert.Contains("Enterprise Server", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileListTakenFromTheWebhooksCommitArrayIsNotTheDiff()
    {
        // A push body's `commits` list carries at most 20 entries, each with its own added/modified/removed
        // arrays. That is a list, but it is not the diff GitHub ran the filter against.
        const string Payload = """
            { "event_name": "push", "ref": "refs/heads/main",
              "commits": [ { "added": ["src/App.cs"], "modified": [], "removed": [] } ] }
            """;
        var study = Analyse("push", Payload, "  push:", "    paths:", "      - 'src/**'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, reason.Kind);
        Assert.Contains("20 commits", reason.Message, StringComparison.Ordinal);

        // The partial evidence is still shown, because it is the best answer to "what was tested".
        Assert.Equal("src/App.cs", Decision(study, "paths").Value);
        Assert.Equal(TriKind.Unknown, Decision(study, "paths").Result);
    }

    [Fact]
    public void AnEmptyChangedFilesListIsADefiniteAnswerForPaths()
    {
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": [] }""";
        var included = Analyse("push", Payload, "  push:", "    paths:", "      - 'src/**'");

        // Documented: "If there are no files changed, the workflow will not run."
        Assert.Equal(TriKind.False, Verdict(included).Runs.Kind);
        Assert.Equal(0, Decision(included, "paths").ValuesTested);
        Assert.Contains("no files at all", Decision(included, "paths").Note, StringComparison.Ordinal);

        var ignored = Analyse("push", Payload, "  push:", "    paths-ignore:", "      - 'docs/**'");

        // An exclusion list with nothing to exclude has stopped nothing. That reading is recorded as an
        // ambiguity in the doc rather than presented as GitHub's own words.
        Assert.Equal(TriKind.True, Verdict(ignored).Runs.Kind);
        Assert.Contains("ambiguities", Decision(ignored, "paths-ignore").Note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ clock and person

    [Fact]
    public void AScheduleCannotBeAnsweredWithoutTheClockAndSaysWhichCronIsAtIssue()
    {
        var study = Analyse("schedule", null, "  schedule:", "    - cron: '17 3 * * *'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, reason.Kind);
        Assert.Contains("17 3 * * *", reason.Message, StringComparison.Ordinal);
        Assert.Contains("clock", reason.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schedule", reason.Message, StringComparison.Ordinal);

        var decision = Decision(study, "schedule");
        Assert.Equal("17 3 * * *", decision.Pattern);
        Assert.Equal(4, decision.PatternRange.Start.Line);
    }

    [Fact]
    public void APayloadThatRecordsAScheduledRunIsTheClockAnswering()
    {
        var study = Analyse("schedule", """{ "event_name": "schedule" }""", "  schedule:", "    - cron: '17 3 * * *'");

        Assert.Equal(TriKind.True, Verdict(study).Runs.Kind);
        Assert.True(Verdict(study).OccurrencePinnedByPayload);
        Assert.Equal(TriggerEventSource.Payload, Verdict(study).Source);
    }

    [Fact]
    public void ADispatchNeedsAPersonUnlessThePayloadSuppliedItsInputs()
    {
        var unasked = Analyse("workflow_dispatch", null, "  workflow_dispatch:");
        var reason = RunsReason(unasked);
        Assert.Equal(UnknownReasonKind.RuntimeFunction, reason.Kind);
        Assert.Contains("person", reason.Message, StringComparison.Ordinal);
        Assert.Contains("inputs", reason.Message, StringComparison.Ordinal);

        const string Dispatch = """{ "event_name": "workflow_dispatch", "inputs": { "stage": "prod" } }""";
        var dispatched = Analyse("workflow_dispatch", Dispatch,
            "  workflow_dispatch:", "    inputs:", "      stage:", "        type: string");
        Assert.Equal(TriKind.True, Verdict(dispatched).Runs.Kind);
        Assert.Equal("stage", Decision(dispatched, "inputs").Value);

        // A recorded dispatch with no inputs is still a recorded dispatch: a person did press the button.
        var bare = Analyse("workflow_dispatch", """{ "event_name": "workflow_dispatch" }""", "  workflow_dispatch:");
        Assert.Equal(TriKind.True, Verdict(bare).Runs.Kind);
    }

    // ------------------------------------------------------------------ asking about an event

    [Fact]
    public void AnEventTheFileDoesNotDeclareNeverRunsIt()
    {
        var study = Analyse("pull_request", PushMain, "  push:", "    branches:", "      - main");

        Assert.Equal(TriKind.False, Verdict(study).Runs.Kind);
        var decision = Decision(study, "on");
        Assert.False(decision.Consulted);
        Assert.Contains("declares no 'pull_request'", decision.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventNobodySelectedProducesFindingsButNoVerdict()
    {
        var study = Analyse(null, null,
            "  push:", "    branches:", "      - main", "    branches-ignore:", "      - dev");

        // The conflict is a property of the file, so it stands whether or not anybody asked about it.
        Assert.Single(study.Evaluation.Diagnostics);
        Assert.Null(study.Evaluation.Verdict);
    }

    [Fact]
    public void ASelectionThatDisagreesWithThePayloadIsAFinding()
    {
        var study = Analyse("workflow_dispatch", PushMain, "  push:", "  workflow_dispatch:");

        var finding = Assert.Single(study.Evaluation.Diagnostics);
        Assert.Equal(DiagnosticCodes.TriggerEventMismatch, finding.Code);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains("describes a 'push' event", finding.Message, StringComparison.Ordinal);

        // The selection is still what gets answered, because that is what the caller asked.
        Assert.Equal("workflow_dispatch", Verdict(study).Event);
        Assert.Equal("push", Verdict(study).PayloadEventName);
        Assert.Equal(4, Verdict(study).Range.Start.Line);
    }

    [Fact]
    public void TheAndOfTwoUnknownsNamesBothGapsUnderOneOperator()
    {
        // Nothing in the payload but the event name: the ref is missing for `branches`, the list for `paths`.
        var study = Analyse("push", """{ "event_name": "push" }""",
            "  push:", "    branches:", "      - main", "    paths:", "      - 'src/**'");

        var reason = RunsReason(study);
        Assert.Equal(UnknownReasonKind.Propagated, reason.Kind);
        Assert.Equal(2, reason.Causes.Count);
        Assert.All(reason.Causes, cause => Assert.Equal(UnknownReasonKind.ValueMissing, cause.Kind));
        Assert.Contains("branches && paths", reason.Message, StringComparison.Ordinal);

        // One gap alone is not wrapped: the leaf already names the filter it blocks.
        var single = Analyse("push", PushMain, "  push:", "    paths:", "      - 'src/**'");
        Assert.Equal(UnknownReasonKind.ValueMissing, RunsReason(single).Kind);
    }

    // ------------------------------------------------------------------ through the analyzer

    [Fact]
    public void TheAnalyzerTakesTheEventFromThePayloadAndTheCallerInThatOrder()
    {
        const string Yaml = """
            name: t
            on:
              push:
                branches:
                  - main
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo
            """;

        // No selection and no payload: the declared events are reported, and no verdict is invented.
        var bare = WorkflowAnalyzer.Analyze(Yaml, "t.yml");
        Assert.Null(bare.Trigger.Verdict);
        Assert.Equal(["push"], bare.Trigger.Declared.Select(trigger => trigger.Name));

        // The payload says what happened, so it selects the event by itself.
        var fromPayload = WorkflowAnalyzer.Analyze(Yaml, "t.yml", PushMain);
        Assert.NotNull(fromPayload.Trigger.Verdict);
        Assert.Equal("push", fromPayload.Trigger.Verdict!.Event);
        Assert.Equal(TriggerEventSource.Payload, fromPayload.Trigger.Verdict!.Source);
        Assert.Equal(TriKind.True, fromPayload.Trigger.Verdict!.Runs.Kind);

        // A caller that picks the event before any payload exists gets the answer the filters allow.
        var selected = WorkflowAnalyzer.Analyze(Yaml, "t.yml", triggerEvent: "push");
        Assert.NotNull(selected.Trigger.Verdict);
        Assert.Equal(TriKind.Unknown, selected.Trigger.Verdict!.Runs.Kind);
        Assert.Equal(TriggerEventSource.Caller, selected.Trigger.Verdict!.Source);
        Assert.NotNull(selected.Trigger.Verdict!.Runs.Reason);
        Assert.Equal(UnknownReasonKind.ValueMissing, selected.Trigger.Verdict!.Runs.Reason!.Kind);

        // The same selection with the ref supplied is a definite yes, decided on line 5's pattern.
        var answered = WorkflowAnalyzer.Analyze(Yaml, "t.yml", """{ "ref": "refs/heads/main" }""", triggerEvent: "push");
        Assert.NotNull(answered.Trigger.Verdict);
        Assert.Equal(TriKind.True, answered.Trigger.Verdict!.Runs.Kind);
        var decision = answered.Trigger.Verdict!.Decisions.Single(item => item.Filter == "branches");
        Assert.Equal("main", decision.Pattern);
        Assert.Equal(5, decision.PatternRange.Start.Line);

        // A list of nothing but negations is a finding of its own, and it blocks the report.
        var rejected = WorkflowAnalyzer.Analyze(Yaml.Replace("- main", "- '!only-negated'"), "t.yml", PushMain);
        Assert.Contains(rejected.Diagnostics, finding => finding.Code == DiagnosticCodes.TriggerPatternInvalid);
        Assert.True(rejected.IsBlocked);
    }

    [Fact]
    public void TheVerdictSurvivesTheJsonExportWithItsReasonAndPositions()
    {
        const string Payload = """{ "event_name": "push", "ref": "refs/heads/main", "changed_files": ["docs/readme.md"] }""";
        var study = Analyse("push", Payload,
            "  push:", "    branches:", "      - main", "    paths:", "      - 'src/**'");
        var report = Wrap(study.Evaluation.Verdict);

        using var document = JsonDocument.Parse(AnalysisReportJson.ToJson(report));
        var verdict = document.RootElement.GetProperty("trigger").GetProperty("verdict");

        Assert.Equal("push", verdict.GetProperty("event").GetString());
        Assert.Equal("false", verdict.GetProperty("runs").GetString());
        Assert.Equal("paths", verdict.GetProperty("blockedBy").GetString());

        var paths = verdict.GetProperty("decisions").EnumerateArray()
            .Single(decision => decision.GetProperty("filter").GetString() == "paths");
        Assert.Equal("src/**", paths.GetProperty("pattern").GetString());
        Assert.StartsWith("7:", paths.GetProperty("patternRange").GetString());
        Assert.Equal("false", paths.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, paths.GetProperty("reason").ValueKind);

        // An unknown keeps its whole reason tree as data, not as a sentence a reader has to re-parse.
        var unknown = Wrap(Analyse("push", """{ "event_name": "push" }""",
            "  push:", "    branches:", "      - main", "    paths:", "      - 'src/**'").Evaluation.Verdict);
        using var unknownDocument = JsonDocument.Parse(AnalysisReportJson.ToJson(unknown));
        var unknownVerdict = unknownDocument.RootElement.GetProperty("trigger").GetProperty("verdict");
        Assert.Equal("unknown", unknownVerdict.GetProperty("runs").GetString());
        Assert.Equal("propagated", unknownVerdict.GetProperty("reason").GetProperty("kind").GetString());
        Assert.Equal(2, unknownVerdict.GetProperty("reason").GetProperty("causes").GetArrayLength());

        // A report with no verdict says so as data rather than hiding the key.
        var noVerdict = Wrap(null);
        using var emptyDocument = JsonDocument.Parse(AnalysisReportJson.ToJson(noVerdict));
        Assert.Equal(JsonValueKind.Null, emptyDocument.RootElement.GetProperty("trigger").GetProperty("verdict").ValueKind);

        static AnalysisReport Wrap(TriggerVerdict? verdict) => new()
        {
            Workflow = new WorkflowIdentity { Name = "t", JobCount = 1 },
            Event = new EventReport { HasEventPayload = true, EventName = "push", VarsSupplied = false, InputsSupplied = false },
            Trigger = new TriggerReport
            {
                Declared = [new DeclaredTrigger("push", true, ["branches", "paths"])],
                IsShorthand = false,
                Verdict = verdict,
            },
            Scenario = new ScenarioReport { Label = "default", Description = "nothing pinned", WorkflowCancelled = false },
            Jobs = [],
            Graph = new GraphReport
            {
                IsAcyclic = true,
                JobCount = 1,
                EdgeCount = 0,
                Nodes = [],
                Edges = [],
                LayerCount = 1,
                MaxLayerWidth = 1,
            },
            Diagnostics = [],
            Totals = new ReportTotals
            {
                Jobs = 1,
                WillRun = 0,
                WillNotRun = 0,
                UnknownJobs = 0,
                Steps = 0,
                JobInstances = 1,
                Diagnostics = 0,
                Criticals = 0,
                Errors = 0,
                Warnings = 0,
                Infos = 0,
            },
            IsBlocked = false,
        };
    }
}
