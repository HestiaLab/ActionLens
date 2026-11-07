using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Text;
using ActionLens.Ui.Services;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The presentation layer's honesty rules: three states never collapse into two, every enum member
/// has a badge, and a matrix count the engine does not know is never printed as a fact.
/// </summary>
public sealed class ReportPresentationTests
{
    [Fact]
    public void EveryRunState_HasItsOwnLabelMarkerAndClass()
    {
        RunState[] states = Enum.GetValues<RunState>();

        Assert.All(states, state =>
        {
            Assert.False(string.IsNullOrWhiteSpace(ReportPresentation.StateLabel(state)));
            Assert.False(string.IsNullOrWhiteSpace(ReportPresentation.StateMarker(state)));
            Assert.False(string.IsNullOrWhiteSpace(ReportPresentation.StateClass(state)));
            Assert.False(string.IsNullOrWhiteSpace(ReportPresentation.StateMeaning(state)));
        });
        Assert.Equal(states.Length, states.Select(ReportPresentation.StateLabel).Distinct().Count());
        Assert.Equal(states.Length, states.Select(ReportPresentation.StateMarker).Distinct().Count());
        Assert.Equal(states.Length, states.Select(ReportPresentation.StateClass).Distinct().Count());
    }

    [Fact]
    public void Unknown_IsNeverPresentedAsSkipped()
    {
        Assert.Equal("Unknown", ReportPresentation.StateLabel(RunState.Unknown));
        Assert.Equal("?", ReportPresentation.StateMarker(RunState.Unknown));
        Assert.Contains("unknown", ReportPresentation.StateClass(RunState.Unknown));
        Assert.Contains("not the same as skipped", ReportPresentation.StateMeaning(RunState.Unknown));
        Assert.Equal("? unknown", ReportPresentation.StateChip(RunState.Unknown));
    }

    [Fact]
    public void EverySeverity_HasALabelAndAClass_AndTheTokenMatches()
    {
        foreach (DiagnosticSeverity severity in Enum.GetValues<DiagnosticSeverity>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReportPresentation.SeverityLabel(severity)));
            Assert.StartsWith("al-severity--", ReportPresentation.SeverityClass(severity));
            Assert.Equal(ReportPresentation.SeverityLabel(severity), ReportPresentation.SeverityToken(severity));
        }
    }

    [Fact]
    public void TriKind_AnswersInThreeWords()
    {
        Assert.Equal("true", ReportPresentation.TriLabel(TriKind.True));
        Assert.Equal("false", ReportPresentation.TriLabel(TriKind.False));
        Assert.Equal("unknown", ReportPresentation.TriLabel(TriKind.Unknown));
        Assert.Equal(3, Enum.GetValues<TriKind>().Select(ReportPresentation.TriClass).Distinct().Count());
    }

    [Fact]
    public void InstanceSummary_SaysUnknownRatherThanGuessing()
    {
        Assert.Equal("1 run", ReportPresentation.InstanceSummary(null));

        var dynamic = new MatrixReport
        {
            IsDynamic = true,
            DynamicDimensions = ["os", "node"],
            ProducedCount = 0,
            Truncated = false,
        };
        Assert.Equal("runs ×? (dynamic: os, node)", ReportPresentation.InstanceSummary(dynamic));

        var truncated = new MatrixReport
        {
            IsDynamic = false,
            CombinationCount = 256,
            ProducedCount = 300,
            Truncated = true,
        };
        Assert.Equal("runs × 256 of 300 (256 cap)", ReportPresentation.InstanceSummary(truncated));

        var plain = new MatrixReport { IsDynamic = false, CombinationCount = 4, ProducedCount = 4, Truncated = false };
        Assert.Equal("runs × 4", ReportPresentation.InstanceSummary(plain));
    }

    [Fact]
    public void Position_NamesTheLineAndColumn_OrSaysThereIsNone()
    {
        Assert.Equal("line 3, column 5", ReportPresentation.Position(new SourceRange(new SourcePosition(3, 5))));
        Assert.Equal("no position", ReportPresentation.Position(SourceRange.Unknown));
    }
}