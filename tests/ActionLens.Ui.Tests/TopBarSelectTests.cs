using ActionLens.Core.Scenarios;
using ActionLens.Ui.Components;
using ActionLens.Ui.Tests.Supporting;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace ActionLens.Ui.Tests;

/// <summary>
/// Pins the W3 defect: all three top-bar dropdowns changed the DOM but not app state, and 1377 green
/// tests did not notice. One test per control — the defect was simultaneous, so one shared test would
/// not have told us which binding regressed — plus the snap-back rule that a rejected choice must not
/// keep displaying a value the analysis never used.
/// </summary>
public sealed class TopBarSelectTests
{
    [Fact]
    public void ExampleSelect_ChangesTheSessionFileAndTriggersReanalysis()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("atlas-ci.yml");

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();
        cut.Find("#al-example").Change("matrix-build.yml");

        Assert.Equal("matrix-build.yml", harness.Session.FileName);

        // Re-analysis, not just a new label: atlas-ci has 3 jobs, matrix-build has 6. Before the fix the
        // select moved in the DOM and the session kept answering about the old file.
        Assert.NotNull(harness.Session.Report);
        Assert.Equal(6, harness.Session.Report!.Jobs.Count);
        Assert.Equal("Prod", harness.Session.Report.Workflow.Name);
    }

    [Fact]
    public void ExampleSelect_RejectsAnUnknownFile_AndSnapsTheControlBackToReality()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("atlas-ci.yml");

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();

        // A raw change event naming a file that does not exist: the binding must fall back to what the
        // session actually holds rather than display a choice the analysis never used.
        cut.Find("#al-example").TriggerEvent("onchange", new ChangeEventArgs { Value = "ghost.yml" });

        Assert.Equal("atlas-ci.yml", harness.Session.FileName);
        Assert.Equal("atlas-ci.yml", Required(cut.Find("#al-example"), "value"));
    }

    [Fact]
    public void TriggerEventSelect_ChangesTheSessionEventAndAnswersTheVerdictForIt()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("matrix-build.yml", triggerEvent: "push");

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();
        cut.Find("#al-event").Change("pull_request");

        Assert.Equal("pull_request", harness.Session.TriggerEvent);
        Assert.NotNull(harness.Session.Report!.Trigger.Verdict);
        Assert.Equal("pull_request", harness.Session.Report!.Trigger.Verdict!.Event);
    }

    [Fact]
    public void TriggerEventSelect_RejectsAnUndeclaredEvent_AndSnapsBackToTheOneThatWasUsed()
    {
        using ComponentHarness harness = new();
        // matrix-build declares push and pull_request only; 'schedule' is neither, nor the payload's event.
        harness.LoadExample("matrix-build.yml", triggerEvent: "push");

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();
        cut.Find("#al-event").TriggerEvent("onchange", new ChangeEventArgs { Value = "schedule" });

        // The session falls back to the payload's own event…
        Assert.Equal("push", harness.Session.TriggerEvent);
        // …and the control is re-pointed at it rather than left displaying 'schedule'.
        Assert.Equal("push", Required(cut.Find("#al-event"), "value"));
    }

    [Fact]
    public void ScenarioSelect_ChangesTheScenarioAndReanswersEveryJob()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("matrix-build.yml", triggerEvent: "push");

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();

        // Option 2 is 'build is skipped' (0 = every job succeeds, then fail/skip per job in document
        // order). The counts are the browser-verified answer from docs/w4-verification.md §A2
        // (+0 will run · −2 will not · ?4 unknown), so a select that only moved the DOM cannot produce
        // them and a select that moved the app state will.
        cut.Find("#al-scenario").Change("2");

        Assert.Equal(ScenarioJobState.IsSkipped, harness.Session.Scenario.JobStates["build"]);

        // Stronger than 'no other pins': exactly one pin, and it is the one this option names.
        KeyValuePair<string, ScenarioJobState> onlyPin = Assert.Single(harness.Session.Scenario.JobStates);
        Assert.Equal("build", onlyPin.Key);
        Assert.NotNull(harness.Session.Report);
        Assert.Equal(0, harness.Session.Report!.Totals.WillRun);
        Assert.Equal(2, harness.Session.Report!.Totals.WillNotRun);
        Assert.Equal(4, harness.Session.Report!.Totals.UnknownJobs);
    }

    [Fact]
    public void ScenarioSelect_ClampsAnOutOfRangeIndex_WithoutSelectingAScenarioThatDoesNotExist()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("atlas-ci.yml");

        IRenderedComponent<TopBar> cut = harness.Context.Render<TopBar>();
        int scenarioCountBefore = cut.Find("#al-scenario").QuerySelectorAll("option").Length;

        cut.Find("#al-scenario").TriggerEvent("onchange", new ChangeEventArgs { Value = "999" });

        // Clamped to 0: the default scenario stays in force, and the control displays the clamp.
        Assert.Equal(Scenario.Default, harness.Session.Scenario);
        Assert.Equal("0", Required(cut.Find("#al-scenario"), "value"));
        Assert.Equal(scenarioCountBefore, cut.Find("#al-scenario").QuerySelectorAll("option").Length);
    }

    /// <summary>The binding writes the chosen value into the <c>value</c> attribute; its absence is a
    /// regression in the binding itself, so read it required rather than optional.</summary>
    private static string Required(AngleSharp.Dom.IElement element, string attributeName)
    {
        string? value = element.GetAttribute(attributeName);
        Assert.NotNull(value);
        return value!;
    }
}
