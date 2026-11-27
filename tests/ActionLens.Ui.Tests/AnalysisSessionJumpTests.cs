using ActionLens.Core.Diagnostics;
using ActionLens.Ui.Services;
using ActionLens.Ui.Tests.Supporting;

namespace ActionLens.Ui.Tests;

public sealed class AnalysisSessionJumpTests
{
    [Fact]
    public void CurrentReport_AllowsDiagnosticJump()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("atlas-ci.yml");
        Diagnostic finding = harness.Session.Report!.Diagnostics[0];
        int requests = 0;
        harness.Session.JumpRequested += _ => requests++;

        Assert.True(harness.Session.RequestJump(EditorTarget.Workflow, finding.Range, finding));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void DiagnosticFromBeforeAnEdit_IsRejectedUntilTheNextReport()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("atlas-ci.yml");
        Diagnostic finding = harness.Session.Report!.Diagnostics[0];
        int requests = 0;
        harness.Session.JumpRequested += _ => requests++;

        harness.Session.ReplaceText(EditorTarget.Workflow, harness.Session.WorkflowText + "\n# changed\n");

        Assert.False(harness.Session.RequestJump(EditorTarget.Workflow, finding.Range, finding));
        Assert.Equal(0, requests);
    }
}
