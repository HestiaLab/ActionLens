namespace ActionLens.Core.Tests;

/// <summary>
/// The complete Core export surface. Everything else is implementation, even when it is useful inside the
/// engine or its tests, so a future "make it public, it's easier" edit has to change this list deliberately.
/// </summary>
public sealed class PublicSurfaceTests
{
    private static readonly string[] ContractTypes =
    [
        "ActionLens.Core.Diagnostics.Diagnostic",
        "ActionLens.Core.Diagnostics.DiagnosticSeverity",
        "ActionLens.Core.Diagnostics.DiagnosticSource",
        "ActionLens.Core.Diagnostics.DiagnosticStage",
        "ActionLens.Core.Expressions.TriKind",
        "ActionLens.Core.Expressions.TriValue",
        "ActionLens.Core.Expressions.UnknownReason",
        "ActionLens.Core.Expressions.UnknownReasonKind",
        "ActionLens.Core.Graph.LayoutEdge",
        "ActionLens.Core.Graph.LayoutNode",
        "ActionLens.Core.Reporting.AnalysisReport",
        "ActionLens.Core.Reporting.AnalysisReportJson",
        "ActionLens.Core.Reporting.DeclaredTrigger",
        "ActionLens.Core.Reporting.EventReport",
        "ActionLens.Core.Reporting.GraphReport",
        "ActionLens.Core.Reporting.JobReport",
        "ActionLens.Core.Reporting.MatrixReport",
        "ActionLens.Core.Reporting.NodePlacement",
        "ActionLens.Core.Reporting.ReportTotals",
        "ActionLens.Core.Reporting.ScenarioPin",
        "ActionLens.Core.Reporting.ScenarioReport",
        "ActionLens.Core.Reporting.TriggerReport",
        "ActionLens.Core.Reporting.WorkflowIdentity",
        "ActionLens.Core.Scenarios.ConditionTrace",
        "ActionLens.Core.Scenarios.JobOutcome",
        "ActionLens.Core.Scenarios.JobOutcomeText",
        "ActionLens.Core.Scenarios.MatrixRun",
        "ActionLens.Core.Scenarios.NeedOutcome",
        "ActionLens.Core.Scenarios.RunState",
        "ActionLens.Core.Scenarios.Scenario",
        "ActionLens.Core.Scenarios.ScenarioJobState",
        "ActionLens.Core.Scenarios.StepRunResult",
        "ActionLens.Core.Text.SourcePosition",
        "ActionLens.Core.Text.SourceRange",
        "ActionLens.Core.Triggers.FilterDecision",
        "ActionLens.Core.Triggers.FilterListMode",
        "ActionLens.Core.Triggers.TriggerEventSource",
        "ActionLens.Core.Triggers.TriggerVerdict",
        "ActionLens.Core.Values.GitHubValue",
        "ActionLens.Core.Values.GitHubValueKind",
        "ActionLens.Core.WorkflowAnalyzer",
    ];

    [Fact]
    public void ExportedTypes_AreExactlyTheContractAllowlist()
    {
        var exported = typeof(WorkflowAnalyzer).Assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ContractTypes.Order(StringComparer.Ordinal), exported);
    }

    /// <summary>These are internal machinery; exposing them again would widen the API for no reason.</summary>
    [Theory]
    [InlineData("ActionLens.Core.Scenarios.ScenarioEngine")]
    [InlineData("ActionLens.Core.Scenarios.ScenarioRunner")]
    [InlineData("ActionLens.Core.Expressions.ConditionEvaluator")]
    [InlineData("ActionLens.Core.Expressions.ExpressionFunctions")]
    [InlineData("ActionLens.Core.Linting.Rules.ScriptInjectionRule")]
    [InlineData("ActionLens.Core.Linting.Rules.RunnerLabelTypoRule")]
    [InlineData("ActionLens.Core.Yaml.WorkflowLoader")]
    [InlineData("ActionLens.Core.Triggers.TriggerEvaluator")]
    public void ImplementationTypes_AreNotExported(string fullName)
    {
        var type = typeof(WorkflowAnalyzer).Assembly.GetType(fullName);
        Assert.NotNull(type);
        Assert.False(type!.IsVisible, $"{fullName} is internal machinery and must not be exported.");
    }

    [Fact]
    public void NoMoreThanOneEntryPointHasTheWordEngine()
    {
        var publicEngines = typeof(WorkflowAnalyzer).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("Engine", StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("ScenarioEngine", publicEngines);
        Assert.DoesNotContain("LintEngine", publicEngines);
        Assert.NotNull(typeof(WorkflowAnalyzer).Assembly.GetType("ActionLens.Core.WorkflowAnalyzer"));
    }
}
