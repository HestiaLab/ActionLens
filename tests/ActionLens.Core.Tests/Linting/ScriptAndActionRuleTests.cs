using ActionLens.Core.Diagnostics;
using ActionLens.Core.Linting;
using ActionLens.Core.Text;

namespace ActionLens.Core.Tests.Linting;

/// <summary>
/// The four rules that read what the author wrote — AL001, AL002, AL003, AL008 — each with its firing shape,
/// its passing shape, and the position the finding points at.
/// </summary>
public sealed class ScriptAndActionRuleTests
{
    // ------------------------------------------------------------------ AL001

    [Fact]
    public void ActionByTag_FiresAtTheUsesValue()
    {
        var yaml = LintTestHarness.OneJob("- uses: actions/checkout@v4");
        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.ActionNotPinned);

        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Equal("build#1", finding.Scope);
        Assert.Equal(new SourcePosition(8, 15), finding.Range.Start);
        Assert.Contains("v4", finding.Message, StringComparison.Ordinal);
        Assert.Contains("actions/checkout", finding.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionByBranchInAPathForm_Fires()
    {
        var yaml = LintTestHarness.OneJob("- uses: aws/actions/deploy-lambda@main");

        Assert.Contains("deploy-lambda", LintTestHarness.FiresOnce(yaml, LintRuleTable.ActionNotPinned).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionByShortSha_SaysItIsTruncated_RatherThanCallingItAPin()
    {
        var yaml = LintTestHarness.OneJob("- uses: actions/checkout@11bd719");
        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.ActionNotPinned);

        Assert.Contains("7-character", finding.Message, StringComparison.Ordinal);
        Assert.Contains("40-character", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionPinnedToAFullSha_StaysSilent()
    {
        var yaml = LintTestHarness.OneJob(
            "- uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2");

        LintTestHarness.StaysSilent(yaml, LintRuleTable.ActionNotPinned);
    }

    [Theory]
    // The forms the rule cannot judge, each of which is correct usage of its own kind.
    [InlineData("- uses: ./my-local-action")]
    [InlineData("- uses: docker://ghcr.io/acme/builder:1.2")]
    [InlineData("- uses: '${{ inputs.action }}'")]
    [InlineData("- run: echo hi")]
    public void UnjudgeableUsesValues_StaysSilent(string step)
    {
        LintTestHarness.StaysSilent(LintTestHarness.OneJob(step), LintRuleTable.ActionNotPinned);
    }

    // ------------------------------------------------------------------ AL002

    private const string PullRequestTargetCheckout = """
        name: pwn-request
        on:
          pull_request_target:
            types: [opened]
        jobs:
          build:
            runs-on: ubuntu-latest
            timeout-minutes: 10
            steps:
              - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683
                with:
                  ref: ${{ github.event.pull_request.head.sha }}

        """;

    [Fact]
    public void PullRequestTarget_CheckingOutTheHeadRef_FiresAtTheInputValue()
    {
        var finding = LintTestHarness.FiresOnce(PullRequestTargetCheckout, LintRuleTable.UntrustedHeadCheckout);

        Assert.Equal(DiagnosticSeverity.Critical, finding.Severity);
        Assert.Equal("build#1", finding.Scope);
        Assert.Equal(12, finding.Range.Start.Line);
        Assert.Equal(16, finding.Range.Start.Column);
        Assert.Contains("pull_request_target", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestTarget_HeadRefThroughGithubHeadRef_Fires()
    {
        const string yaml = """
            name: head-ref
            on:
              pull_request_target:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                    with:
                      ref: ${{ github.head_ref }}
            """;

        LintTestHarness.FiresOnce(yaml, LintRuleTable.UntrustedHeadCheckout);
    }

    [Fact]
    public void PullRequestTarget_FullFetchDepth_FiresBecauseThePrcommitsReachTheRunner()
    {
        const string yaml = """
            name: full-history
            on:
              pull_request_target:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                    with:
                      fetch-depth: 0
            """;

        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.UntrustedHeadCheckout);
        Assert.Contains("fetch-depth", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameCheckoutUnderPullRequest_StaysSilent()
    {
        // The event is what makes the shape dangerous: pull_request runs the merge commit in a context that
        // gets no secrets, so an identical `ref:` is not this rule's problem.
        const string yaml = """
            name: safe-event
            on:
              pull_request:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                    with:
                      ref: ${{ github.event.pull_request.head.sha }}
            """;

        LintTestHarness.StaysSilent(yaml, LintRuleTable.UntrustedHeadCheckout);
    }

    [Theory]
    [InlineData("ref: refs/heads/main")]
    [InlineData("ref: ${{ github.event.repository.default_branch_ref }}")]
    [InlineData("fetch-depth: 3")]
    public void PullRequestTarget_CheckoutShapesThatStayOnTheBase_StaysSilent(string input)
    {
        var yaml = $$"""
            name: base-only
            on:
              pull_request_target:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                    with:
                      {{input}}
            """;

        LintTestHarness.StaysSilent(yaml, LintRuleTable.UntrustedHeadCheckout);
    }

    // ------------------------------------------------------------------ AL003

    private const string BlockScalarScript = """
        name: script-injection
        on:
          pull_request:
        jobs:
          build:
            runs-on: ubuntu-latest
            timeout-minutes: 5
            steps:
              - name: Show PR metadata
                run: |
                  echo "title: ${{ github.event.pull_request.title }}"
                  echo "author: ${{ github.event.pull_request.user.login }}"
        """;

    [Fact]
    public void EventPayloadInABlockScalar_IsLocatedOnItsOwnLine()
    {
        var findings = LintTestHarness.Fires(BlockScalarScript, LintRuleTable.PayloadIntoScript);

        Assert.Equal(2, findings.Length);

        // The script is a block scalar, so the model holds the header line rather than the content columns:
        // the finding lands on the line the interpolation is actually written on, at its start, and quotes the
        // expression itself.
        Assert.Equal(new SourcePosition(11, 1), findings[0].Range.Start);
        Assert.Equal(11, findings[0].Range.End.Line);
        Assert.Equal(new SourcePosition(12, 1), findings[1].Range.Start);
        Assert.Equal(DiagnosticSeverity.Critical, findings[0].Severity);
        Assert.Equal("build#1", findings[0].Scope);
        Assert.Contains("github.event.pull_request.title", findings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadRefInAScript_Fires()
    {
        var yaml = LintTestHarness.OneJob(@"- run: git checkout ${{ github.head_ref }}");
        var finding = LintTestHarness.FiresOnce(yaml, LintRuleTable.PayloadIntoScript);

        Assert.Equal(new SourcePosition(8, 27), finding.Range.Start);
        Assert.Contains("github.head_ref", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // The payload reaches the script through `env:` here, which is the fix the finding asks for.
    [InlineData("""
        - name: Hash the title safely
          env:
            PR_TITLE: ${{ github.event.pull_request.title }}
          run: echo "$PR_TITLE" | sha256sum
        """)]
    [InlineData("- run: echo ${{ matrix.os }}")]
    [InlineData("- run: echo ${{ inputs.environment }}")]
    [InlineData(@"- run: echo 'secrets are not the payload' && echo ${{ github.ref }}")]
    public void ScriptThatDoesNotInterpolateThePayload_StaysSilent(string step)
    {
        LintTestHarness.StaysSilent(LintTestHarness.OneJob(step), LintRuleTable.PayloadIntoScript);
    }

    // ------------------------------------------------------------------ AL008

    [Theory]
    [InlineData(@"echo ""::set-output name=note::1""", "::set-output")]
    [InlineData(@"echo ""::save-state name=stage::build""", "::save-state")]
    public void DeprecatedWorkflowCommand_FiresAtTheCommand(string script, string command)
    {
        var finding = LintTestHarness.FiresOnce(LintTestHarness.OneJob($"- run: {script}"), LintRuleTable.DeprecatedWorkflowCommand);

        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(command, finding.Message, StringComparison.Ordinal);

        // `- run: ` begins at column 7, the quoted script at 14, and `echo ` puts the command at 20.
        Assert.Equal(new SourcePosition(8, 20), finding.Range.Start);
        Assert.Contains("$GITHUB", finding.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"echo ""note=1"" >> $GITHUB_OUTPUT")]
    [InlineData(@"echo ""the word set-output appears in a log line""")]
    public void CurrentOutputIdiom_StaysSilent(string script)
    {
        LintTestHarness.StaysSilent(
            LintTestHarness.OneJob($"- run: {script}"),
            LintRuleTable.DeprecatedWorkflowCommand);
    }

    [Fact]
    public void BothDeprecatedCommandsInOneScript_AreTwoFindings()
    {
        var yaml = LintTestHarness.OneJob(
            @"- run: echo ""::set-output name=a::1"" && echo ""::save-state name=b::2""");

        Assert.Equal(2, LintTestHarness.Fires(yaml, LintRuleTable.DeprecatedWorkflowCommand).Length);
    }
}
