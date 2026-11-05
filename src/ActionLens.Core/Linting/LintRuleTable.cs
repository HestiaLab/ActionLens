using ActionLens.Core.Diagnostics;

namespace ActionLens.Core.Linting;

/// <summary>
/// One lint rule's identity: the id a reader sees in the problem list, how serious the finding is, and the
/// documentation page that states the expectation. Both live here rather than on the rule itself so the
/// eight rules read as one table, and so a rule that changes its mind cannot silently change its severity.
/// </summary>
public sealed record LintRuleDescriptor(string Id, DiagnosticSeverity Severity, string HelpLink);

/// <summary>
/// The AL001–AL008 table, in id order.
/// </summary>
/// <remarks>
/// Ids are part of the observable output (JSON export, the UI's problem list), so they are never reused or
/// renumbered. They are kept out of <c>DiagnosticCodes</c> on purpose: that file lists what the pipeline
/// <em>stages</em> produce, while these are the <em>rules</em> a reader acts on, and AL004 and AL006 rename
/// a stage finding rather than adding one.
/// <para>
/// The help links use the documentation tree GitHub moved these pages into
/// (<c>…/actions/reference/workflows-and-actions/…</c> and <c>…/actions/reference/limits</c>). The old
/// <c>learn-github-actions</c> and <c>using-workflows</c> paths only redirect, so they are not linked.
/// </para>
/// </remarks>
public static class LintRuleTable
{
    private const string Syntax = "https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax";
    private const string Contexts = "https://docs.github.com/en/actions/reference/workflows-and-actions/contexts";

    /// <summary>A remote action called by tag or branch instead of by commit.</summary>
    public static LintRuleDescriptor ActionNotPinned { get; } = new(
        "AL001",
        DiagnosticSeverity.Warning,
        Syntax + "#jobsjob_idstepsuses");

    /// <summary>A <c>pull_request_target</c> workflow that checks out the pull request's own head.</summary>
    public static LintRuleDescriptor UntrustedHeadCheckout { get; } = new(
        "AL002",
        DiagnosticSeverity.Critical,
        "https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request_target");

    /// <summary>An event payload value interpolated straight into a script.</summary>
    public static LintRuleDescriptor PayloadIntoScript { get; } = new(
        "AL003",
        DiagnosticSeverity.Critical,
        Contexts + "#github-context");

    /// <summary><c>secrets.*</c> read in an <c>if:</c> condition.</summary>
    public static LintRuleDescriptor SecretsInCondition { get; } = new(
        "AL004",
        DiagnosticSeverity.Error,
        Contexts + "#context-availability");

    /// <summary>A job with no <c>timeout-minutes</c> of its own.</summary>
    public static LintRuleDescriptor MissingTimeout { get; } = new(
        "AL005",
        DiagnosticSeverity.Info,
        "https://docs.github.com/en/actions/reference/limits");

    /// <summary>A <c>needs</c> entry that names no job, or that closes a cycle.</summary>
    public static LintRuleDescriptor NeedsIntegrity { get; } = new(
        "AL006",
        DiagnosticSeverity.Error,
        Syntax + "#jobsjob_idneeds");

    /// <summary>A <c>runs-on</c> label one edit away from a documented hosted-runner label.</summary>
    public static LintRuleDescriptor RunnerLabelTypo { get; } = new(
        "AL007",
        DiagnosticSeverity.Warning,
        Syntax + "#jobsjob_idruns-on");

    /// <summary>The deprecated <c>::set-output</c> / <c>::save-state</c> workflow commands.</summary>
    public static LintRuleDescriptor DeprecatedWorkflowCommand { get; } = new(
        "AL008",
        DiagnosticSeverity.Warning,
        "https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands");

    /// <summary>The eight descriptors in id order — the table the rules are registered against.</summary>
    public static IReadOnlyList<LintRuleDescriptor> All { get; } =
    [
        ActionNotPinned,
        UntrustedHeadCheckout,
        PayloadIntoScript,
        SecretsInCondition,
        MissingTimeout,
        NeedsIntegrity,
        RunnerLabelTypo,
        DeprecatedWorkflowCommand,
    ];
}
