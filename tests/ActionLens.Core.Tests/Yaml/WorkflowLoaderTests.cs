using ActionLens.Core.Diagnostics;
using ActionLens.Core.Values;
using ActionLens.Core.Yaml;

namespace ActionLens.Core.Tests.Yaml;

/// <summary>
/// Tests for reading workflow YAML: positions, the YAML 1.1 traps, hostile input, and schema
/// findings. Positions are asserted as exact line:column pairs because every downstream feature —
/// clicking a problem to jump to the line, pointing a lint rule at one value — depends on them.
/// </summary>
public sealed class WorkflowLoaderTests
{


    private const string SimpleWorkflow = """
        name: Build
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
        """;

    [Fact]
    public void ReadsNameTriggersAndJobs()
    {
        var result = WorkflowLoader.Load(SimpleWorkflow, "ci.yml");

        Assert.NotNull(result.Workflow);
        Assert.Equal("Build", result.Workflow!.Name);
        Assert.Single(result.Workflow.Triggers.Events);
        Assert.NotNull(result.Workflow.Triggers.Find("push"));
        Assert.Equal("build", Assert.Single(result.Workflow.Jobs).Id);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RecordsPositionsOfEveryInterestingValue()
    {
        var result = WorkflowLoader.Load(SimpleWorkflow, "ci.yml");
        var job = Assert.Single(result.Workflow!.Jobs);
        var step = Assert.Single(job.Steps);

        // The convention: a recorded field is its VALUE node's start, so 'runs-on' points at
        // 'ubuntu-latest' on line 5 (not at line 2, which is 'on:'). 'uses' belongs to the STEP's map —
        // the job's map has no such field and returns SourceRange.Unknown — and points at the reference on
        // line 7. KeyRange is the exception by definition: it is the job's key.
        Assert.Equal("5:14", job.Sources.Get("runs-on").Start.ToString());
        Assert.Equal("7:15", step.Sources.Get("uses").Start.ToString());
        Assert.Equal("4:3", job.KeyRange.Start.ToString());
    }

    [Fact]
    public void FallsBackToFileNameWhenWorkflowHasNoName()
    {
        var result = WorkflowLoader.Load("on: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: echo\n", "release.yml");

        Assert.Equal("release.yml", result.Workflow!.Name);
    }

    [Theory]
    [InlineData("on: [push, pull_request]", 2)]
    [InlineData("on:\n  push:\n    branches: [main]\n  schedule:\n    - cron: '0 0 * * *'", 2)]
    [InlineData("on: workflow_dispatch", 1)]
    public void ReadsEveryTriggerShape(string on, int expectedEvents)
    {
        var result = WorkflowLoader.Load($"name: n\n{on}\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: echo\n", "w.yml");

        Assert.Equal(expectedEvents, result.Workflow!.Triggers.Events.Count);
    }

    [Fact]
    public void ReadsFilterListsAndTypes()
    {
        const string Workflow = """
            name: n
            on:
              pull_request:
                types: [opened, synchronize]
                branches: [main, 'release/*']
                paths-ignore:
                  - docs/**
            jobs:
              a:
                runs-on: x
                steps:
                  - run: echo
            """;

        var trigger = WorkflowLoader.Load(Workflow, "w.yml").Workflow!.Triggers.Find("pull_request");

        Assert.NotNull(trigger);
        Assert.True(trigger!.HasConfiguration);
        Assert.Equal(["opened", "synchronize"], trigger.Types.Select(value => value.Text));
        Assert.Equal(["main", "release/*"], trigger.Branches.Select(value => value.Text));
        Assert.Equal(["docs/**"], trigger.PathsIgnore.Select(value => value.Text));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("YES")]
    public void AcceptsEveryYaml11SpellingOfTheTriggerKey(string key)
    {
        var result = WorkflowLoader.Load($"name: n\n{key}: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: echo\n", "w.yml");

        Assert.Single(result.Workflow!.Triggers.Events);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.UnknownField);
    }

    [Fact]
    public void ReportsDuplicateKeyWithBothPositions()
    {
        const string Workflow = """
            name: one
            name: two
            jobs: {}
            """;

        var result = WorkflowLoader.Load(Workflow, "w.yml");

        var duplicate = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.YamlDuplicateKey);
        Assert.Equal("2:1", duplicate.Range.Start.ToString());
        Assert.Contains("1:1", duplicate.Suggestion);
        Assert.Null(result.Workflow);
    }

    [Fact]
    public void ReportsEveryDuplicateKeyNotJustTheFirst()
    {
        var result = WorkflowLoader.Load("a: 1\na: 2\nb: 1\nb: 2\n", "w.yml");

        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == DiagnosticCodes.YamlDuplicateKey));
    }

    [Fact]
    public void RejectsMultipleDocuments()
    {
        var result = WorkflowLoader.Load("name: a\njobs: {}\n---\nname: b\njobs: {}\n", "w.yml");

        Assert.Contains(DiagnosticCodes.YamlMultipleDocuments, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Null(result.Workflow);
    }

    [Fact]
    public void RejectsNonMappingRoot()
    {
        var result = WorkflowLoader.Load("just a string", "w.yml");

        Assert.Contains(DiagnosticCodes.YamlRootNotMapping, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Null(result.Workflow);
    }

    [Fact]
    public void SyntaxErrorBecomesADiagnosticWithAPosition()
    {
        var result = WorkflowLoader.Load("name: demo\n  bad indent: [1, 2\n", "w.yml");

        var syntax = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.YamlSyntax);
        Assert.True(syntax.Range.IsKnown);
        Assert.Null(result.Workflow);
    }

    [Fact]
    public void UndefinedAnchorIsReportedNotThrown()
    {
        var result = WorkflowLoader.Load("name: *missing\n", "w.yml");

        Assert.NotEmpty(result.Diagnostics);
        Assert.Null(result.Workflow);
    }

    [Fact]
    public void EmptyInputIsADiagnostic()
    {
        var result = WorkflowLoader.Load("   \n", "w.yml");

        Assert.Contains(DiagnosticCodes.YamlSyntax, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void RefusesInputAboveTheSizeLimit()
    {
        var huge = "# " + new string('x', WorkflowLoader.MaxInputBytes);

        var result = WorkflowLoader.Load(huge, "w.yml");

        Assert.Contains(DiagnosticCodes.YamlSizeLimit, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void RefusesAnAliasBomb()
    {
        var builder = new System.Text.StringBuilder("name: n\njobs: {}\na: &x [1,2,3]\n");
        for (var i = 0; i < 200; i++)
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"b{i}: [*x, *x]");
        }

        var result = WorkflowLoader.Load(builder.ToString(), "w.yml");

        Assert.Contains(DiagnosticCodes.YamlAliasLimit, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Null(result.Workflow);
    }

    [Fact]
    public void RefusesExcessivelyDeepNesting()
    {
        var result = WorkflowLoader.Load(new string('[', 200) + new string(']', 200), "w.yml");

        Assert.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code is DiagnosticCodes.YamlDepthLimit or DiagnosticCodes.YamlSyntax),
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void AnchorsReuseContentWithoutConfusingPositions()
    {
        const string Workflow = """
            defaults: &d
              shell: bash
            jobs:
              a:
                runs-on: x
                steps:
                  - <<: *d
                    run: echo
            """;

        var result = WorkflowLoader.Load(Workflow, "w.yml");

        var step = Assert.Single(result.Workflow!.Jobs[0].Steps);
        Assert.Equal("bash", step.Shell);
        Assert.True(step.Sources.IsInherited("shell"));
        Assert.Equal("2:10", step.Sources.Get("shell").Start.ToString());
    }

    [Fact]
    public void MergeKeyOverrideIsNotMistakenForADuplicate()
    {
        // The shape that makes YamlDotNet's own MergingParser report a duplicate key: the step merges
        // an anchor and then overrides one of its keys, which YAML 1.1 says is legal.
        const string Workflow = """
            base: &b
              shell: bash
              timeout-minutes: 5
            jobs:
              a:
                runs-on: x
                steps:
                  - <<: *b
                    timeout-minutes: 20
                    run: echo
            """;

        var result = WorkflowLoader.Load(Workflow, "w.yml");

        Assert.DoesNotContain(DiagnosticCodes.YamlDuplicateKey, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        var step = Assert.Single(result.Workflow!.Jobs[0].Steps);
        Assert.Equal(20, step.TimeoutMinutes);
    }

    [Fact]
    public void UnknownFieldIsRejectedWithTheNearestRealName()
    {
        var result = WorkflowLoader.Load("name: n\non: push\njobs:\n  a:\n    run-on: x\n    steps: []\n", "w.yml");

        var unknown = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.UnknownField);
        Assert.Contains("runs-on", unknown.Suggestion);
        // The finding is about the NAME GitHub did not accept, so it points at the key ('run-on' at 5:5),
        // not at the value — the value-node convention that SourceMap follows does not apply here.
        Assert.Equal("5:5", unknown.Range.Start.ToString());
    }

    [Fact]
    public void JobWithoutRunsOnOrStepsIsReported()
    {
        var result = WorkflowLoader.Load("name: n\non: push\njobs:\n  a:\n    name: x\n", "w.yml");

        var codes = result.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray();
        Assert.Contains(DiagnosticCodes.MissingRequired, codes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("runs-on"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("steps"));
    }

    [Fact]
    public void StepWithBothUsesAndRunIsRejected()
    {
        var result = WorkflowLoader.Load("on: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - uses: actions/checkout@v4\n        run: echo\n", "w.yml");

        Assert.Contains(DiagnosticCodes.InvalidValue, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Theory]
    [InlineData("actions/checkout@v4")]
    [InlineData("actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683")]
    [InlineData("owner/repo/.github/workflows/reusable.yml@main")]
    [InlineData("./.github/actions/my-action")]
    [InlineData("docker://alpine:3.16")]
    [InlineData("${{ inputs.action }}")]
    public void AcceptsEveryUsesShape(string uses)
    {
        var result = WorkflowLoader.Load($"on: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - uses: {uses}\n", "w.yml");

        Assert.DoesNotContain(DiagnosticCodes.InvalidValue, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Theory]
    [InlineData("actions/checkout")]
    [InlineData("just-a-name")]
    [InlineData("@v4")]
    public void RejectsMalformedUses(string uses)
    {
        // Quoted on purpose: YAML 1.1 reserves '@' as an indicator, so a plain '- uses: @v4' is a YAML001
        // scanner error that never reaches the schema stage. The reference itself is what this test judges.
        var result = WorkflowLoader.Load($"on: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - uses: '{uses}'\n", "w.yml");

        Assert.Contains(DiagnosticCodes.InvalidValue, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void ReadsNeedsAsBothStringAndList()
    {
        var result = WorkflowLoader.Load(
            "on: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: echo\n  b:\n    needs: a\n    runs-on: x\n    steps:\n      - run: echo\n  c:\n    needs: [a, b]\n    runs-on: x\n    steps:\n      - run: echo\n",
            "w.yml");

        var jobs = result.Workflow!.Jobs;
        Assert.Equal(["a"], jobs[1].Needs);
        Assert.Equal(["a", "b"], jobs[2].Needs);
    }

    [Fact]
    public void KeepsMatrixDimensionsIncludeAndExcludeWithPositions()
    {
        const string Workflow = """
            on: push
            jobs:
              test:
                runs-on: x
                strategy:
                  matrix:
                    os: [ubuntu, windows]
                    node: ['20', '22']
                    include:
                      - os: ubuntu
                        coverage: true
                    exclude:
                      - os: windows
                        node: '20'
                steps:
                  - run: echo
            """;

        var matrix = WorkflowLoader.Load(Workflow, "w.yml").Workflow!.Jobs[0].Strategy!.Matrix!;

        // Again the value-node convention: a dimension's range is its list ('[ubuntu, windows]' on 7:13),
        // and 'include' points at the first entry of its sequence (the '-' on 10:11), not at the key.
        Assert.Equal(["os", "node"], matrix.Dimensions.Select(dimension => dimension.Name));
        Assert.Equal(2, matrix.Dimensions[0].Values!.Count);
        Assert.Equal("7:13", matrix.Dimensions[0].Range.Start.ToString());
        var include = Assert.Single(matrix.Include);
        Assert.Equal(GitHubValue.True, include["coverage"].Value);
        var exclude = Assert.Single(matrix.Exclude);
        Assert.Equal("windows", exclude["os"].Value.String);
        Assert.True(matrix.Sources.TryGet("include", out var includeRange));
        Assert.Equal("10:11", includeRange.Start.ToString());
    }

    [Fact]
    public void MarksDynamicMatrixDimensionsInsteadOfGuessing()
    {
        const string Workflow = """
            on: push
            jobs:
              test:
                runs-on: x
                strategy:
                  matrix:
                    os: ${{ fromJSON(needs.setup.outputs.os) }}
                steps:
                  - run: echo
            """;

        var dimension = Assert.Single(WorkflowLoader.Load(Workflow, "w.yml").Workflow!.Jobs[0].Strategy!.Matrix!.Dimensions);

        Assert.Null(dimension.Values);
        Assert.Contains("fromJSON", dimension.DynamicExpression);
    }

    [Fact]
    public void ResolvesMatrixValuesWithTheirYamlTypes()
    {
        // YAML 1.1 reads 3.10 as the number 3.1, which is the whole reason people quote versions in a
        // matrix. ActionLens has to show the same surprise rather than the author's intention.
        const string Workflow = """
            on: push
            jobs:
              test:
                runs-on: x
                strategy:
                  matrix:
                    version: [3.10, 3.9]
                    flag: [true, 'true']
                    name: [linux]
                steps:
                  - run: echo
            """;

        var matrix = WorkflowLoader.Load(Workflow, "w.yml").Workflow!.Jobs[0].Strategy!.Matrix!;

        var versions = matrix.Dimensions[0].Values!;
        Assert.Equal(GitHubValueKind.Number, versions[0].Value.Kind);
        Assert.Equal(3.1, versions[0].Value.Number, precision: 10);
        Assert.Equal("3.10", versions[0].Text);

        var flags = matrix.Dimensions[1].Values!;
        Assert.Equal(GitHubValueKind.Boolean, flags[0].Value.Kind);
        Assert.Equal(GitHubValueKind.String, flags[1].Value.Kind);

        Assert.Equal(GitHubValueKind.String, matrix.Dimensions[2].Values![0].Value.Kind);
    }

    [Fact]
    public void DistinguishesAPlainBooleanConditionFromAQuotedString()
    {
        const string Workflow = """
            on: push
            jobs:
              plain:
                if: false
                runs-on: x
                steps:
                  - run: echo
              quoted:
                if: 'false'
                runs-on: x
                steps:
                  - run: echo
            """;

        var jobs = WorkflowLoader.Load(Workflow, "w.yml").Workflow!.Jobs;

        Assert.Equal(GitHubValueKind.Boolean, jobs[0].Condition!.Value.Value.Kind);
        Assert.Equal(GitHubValueKind.String, jobs[1].Condition!.Value.Value.Kind);
    }

    [Fact]
    public void ReadsWorkflowDispatchInputsWithTypesAndDefaults()
    {
        const string Workflow = """
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    type: choice
                    description: Where to deploy
                    default: staging
                    options: [staging, production]
                  dry-run:
                    type: boolean
                    required: false
                    default: true
            jobs:
              a:
                runs-on: x
                steps:
                  - run: echo
            """;

        var inputs = WorkflowLoader.Load(Workflow, "w.yml").Workflow!.Triggers.Find("workflow_dispatch")!.Inputs;

        Assert.Equal("choice", inputs["environment"].Type);
        Assert.Equal("staging", inputs["environment"].Default!.Value.Text);
        Assert.Equal(["staging", "production"], inputs["environment"].Options.Select(option => option.Text));
        Assert.True(inputs["dry-run"].Default!.Value.Value.Boolean);
    }

    [Fact]
    public void ReportsInvalidInputType()
    {
        var result = WorkflowLoader.Load(
            "on:\n  workflow_dispatch:\n    inputs:\n      x:\n        type: textarea\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: echo\n",
            "w.yml");

        Assert.Contains(DiagnosticCodes.InvalidValue, result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void ReadsEnvOutputsAndDefaultsAsTypedValues()
    {
        const string Workflow = """
            name: n
            on: push
            env:
              FULL: '${{ github.ref }}'
              COUNT: 3
            defaults:
              run:
                shell: bash
                working-directory: ./src
            jobs:
              a:
                runs-on: x
                env:
                  JOB_ONLY: yes
                outputs:
                  id: '${{ steps.x.outputs.id }}'
                steps:
                  - run: echo
            """;

        var workflow = WorkflowLoader.Load(Workflow, "w.yml").Workflow!;

        Assert.Equal("${{ github.ref }}", workflow.Env["FULL"].Text);
        Assert.Equal(GitHubValueKind.Number, workflow.Env["COUNT"].Value.Kind);
        Assert.Equal("bash", workflow.Defaults.Shell);
        Assert.Equal("./src", workflow.Defaults.WorkingDirectory);
        Assert.Equal("yes", workflow.Jobs[0].Env["JOB_ONLY"].Text);
        Assert.Equal(GitHubValueKind.Boolean, workflow.Jobs[0].Env["JOB_ONLY"].Value.Kind);
        Assert.Contains("steps.x.outputs.id", workflow.Jobs[0].Outputs["id"].Text);
    }

    [Fact]
    public void ReadsRunsOnAsScalarListAndGroupMapping()
    {
        var scalar = WorkflowLoader.Load("on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n", "w.yml").Workflow!;
        Assert.Equal(["ubuntu-latest"], scalar.Jobs[0].RunsOn.Labels.Select(label => label.Text));
        Assert.False(scalar.Jobs[0].RunsOn.IsExpression);

        var list = WorkflowLoader.Load("on: push\njobs:\n  a:\n    runs-on: [self-hosted, linux, ARM64]\n    steps:\n      - run: echo\n", "w.yml").Workflow!;
        Assert.Equal(3, list.Jobs[0].RunsOn.Labels.Count);

        var group = WorkflowLoader.Load("on: push\njobs:\n  a:\n    runs-on:\n      group: my-group\n      labels: gpu\n    steps:\n      - run: echo\n", "w.yml").Workflow!;
        Assert.Equal(["gpu"], group.Jobs[0].RunsOn.Labels.Select(label => label.Text));

        var expression = WorkflowLoader.Load("on: push\njobs:\n  a:\n    runs-on: '${{ matrix.os }}'\n    steps:\n      - run: echo\n", "w.yml").Workflow!;
        Assert.True(expression.Jobs[0].RunsOn.IsExpression);
    }

    [Fact]
    public void ReportsMatrixDeclaredDirectlyOnTheJob()
    {
        var result = WorkflowLoader.Load(
            "on: push\njobs:\n  a:\n    runs-on: x\n    matrix:\n      os: [linux]\n    steps:\n      - run: echo\n",
            "w.yml");

        // Exactly one finding, and it is the one that says where the key belongs: the job-level 'matrix'
        // is read through to the strategy builder rather than dropped as an unknown word, so the generic
        // "'matrix' is not a workflow job property" is suppressed. Position is the key, as for any
        // name-level finding.
        var unknown = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.UnknownField && diagnostic.Message.Contains("strategy"));
        Assert.Equal("5:5", unknown.Range.Start.ToString());
    }

    [Fact]
    public void ReportsMatrixDimensionCaseInsensitiveCollision()
    {
        var result = WorkflowLoader.Load(
            "on: push\njobs:\n  a:\n    runs-on: x\n    strategy:\n      matrix:\n        OS: [linux]\n        os: [mac]\n    steps:\n      - run: echo\n",
            "w.yml");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("case-insensitive")
            || diagnostic.Code == DiagnosticCodes.MatrixValueInvalid);
    }

    [Fact]
    public void ReadsWithoutThrowingOnPathologicalButLegalInput()
    {
        var input = string.Concat(Enumerable.Repeat("a: &x [*x]\n", 20));

        var result = WorkflowLoader.Load(input + "jobs: {}\n", "w.yml");

        Assert.NotNull(result.Diagnostics);
    }
}
