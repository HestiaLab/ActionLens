namespace ActionLens.Core.Tests;

/// <summary>
/// Decision F asks for one public entry point. §7 recorded that roughly 55 of 60 Core files exposed a
/// public type; after the post-gate narrowing pass, the model and finding types that the JSON export
/// and the UI actually consume remain public, while implementation details are internal.
/// This test pins that boundary so a future "make it public, it's easier" edit cannot quietly widen
/// the API again. The compiler already enforces the other direction: a type used in a public signature
/// cannot be made internal (CS0050/CS0051/CS0053/CS0060).
/// </summary>
public sealed class PublicSurfaceTests
{
    /// <summary>The types that are genuinely part of the contract the reader and the UI rely on.</summary>
    [Theory]
    [InlineData("ActionLens.Core.WorkflowAnalyzer")]
    [InlineData("ActionLens.Core.Reporting.AnalysisReport")]
    [InlineData("ActionLens.Core.Diagnostics.Diagnostic")]
    [InlineData("ActionLens.Core.Scenarios.Scenario")]
    public void ContractTypes_ArePublic(string fullName)
    {
        var type = typeof(WorkflowAnalyzer).Assembly.GetType(fullName);
        Assert.NotNull(type);
        Assert.True(type!.IsPublic, $"{fullName} is part of the observable contract and must stay public.");
    }

    /// <summary>These are internal machinery; exposing them again would widen the API for no reason.</summary>
    [Theory]
    [InlineData("ActionLens.Core.Scenarios.ScenarioEngine")]
    [InlineData("ActionLens.Core.Scenarios.ScenarioRunner")]
    [InlineData("ActionLens.Core.Expressions.ConditionEvaluator")]
    [InlineData("ActionLens.Core.Expressions.ExpressionFunctions")]
    [InlineData("ActionLens.Core.Linting.Rules.ScriptInjectionRule")]
    [InlineData("ActionLens.Core.Linting.Rules.RunnerLabelTypoRule")]
    public void ImplementationTypes_AreNotExported(string fullName)
    {
        var type = typeof(WorkflowAnalyzer).Assembly.GetType(fullName);
        Assert.NotNull(type);
        Assert.False(type!.IsVisible, $"{fullName} is internal machinery and must not be exported.");
    }

    [Fact]
    public void NoMoreThanOneEntryPointHasTheWordEngine()
    {
        // A coarse guard: only the analyzer is a public "engine". ScenarioEngine, LintEngine and the
        // evaluators are stages it drives, not alternative front doors.
        var publicEngines = typeof(WorkflowAnalyzer).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("Engine", StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("ScenarioEngine", publicEngines);
        Assert.DoesNotContain("LintEngine", publicEngines);
        Assert.NotNull(typeof(WorkflowAnalyzer).Assembly.GetType("ActionLens.Core.WorkflowAnalyzer"));
    }
}
