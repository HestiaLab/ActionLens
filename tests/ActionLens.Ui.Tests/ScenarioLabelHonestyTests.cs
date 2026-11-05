using ActionLens.Ui.Components;
using ActionLens.Ui.Tests.Supporting;
using Bunit;

namespace ActionLens.Ui.Tests;

/// <summary>
/// Pins defect #16: the Run summary paired the scenario's Description with the report's Label, which
/// falls back to "default" for every scenario the top bar offers — so the summary claimed "default"
/// even while the same line said a job was skipped. The fix renders the description alone; these tests
/// make the lie impossible to reintroduce silently.
/// </summary>
public sealed class ScenarioLabelHonestyTests
{
    [Fact]
    public void SummaryPanel_NeverSaysDefault_WhenTheScenarioHasBeenModified()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("matrix-build.yml", triggerEvent: "push");

        // Top bar and summary in one render pass: the scenario is changed through the real control, so
        // the assertion covers the label the reader actually sees after a real selection.
        var cut = harness.Context.Render(builder =>
        {
            builder.OpenComponent<TopBar>(0);
            builder.CloseComponent();
            builder.OpenComponent<SummaryPanel>(1);
            builder.CloseComponent();
        });

        cut.Find("#al-scenario").Change("2"); // 'build is skipped'

        string summaryText = cut.Find(".al-summary").TextContent;
        Assert.Contains("build is skipped", summaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("default", summaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummaryPanel_KeepsDescribingTheDefaultRunInItsOwnWords()
    {
        using ComponentHarness harness = new();
        harness.LoadExample("matrix-build.yml", triggerEvent: "push");

        var cut = harness.Context.Render(builder =>
        {
            builder.OpenComponent<TopBar>(0);
            builder.CloseComponent();
            builder.OpenComponent<SummaryPanel>(1);
            builder.CloseComponent();
        });

        // The default scenario is described, not named: the panel must say what the run is rather than
        // echo a label, so both branches of the honesty rule render descriptions.
        string summaryText = cut.Find(".al-summary").TextContent;
        Assert.Contains("every job succeeds", summaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default", summaryText, StringComparison.OrdinalIgnoreCase);
    }
}
