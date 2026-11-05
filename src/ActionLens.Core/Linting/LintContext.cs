using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Linting;

/// <summary>
/// Everything a lint rule reads: the workflow model with its source maps, and the findings the pipeline
/// stages already produced.
/// </summary>
/// <remarks>
/// The stage findings are here for AL004 and AL006, the two rules whose problem the engine has already
/// found and already positioned — an unavailable context and an illegal <c>needs</c>. Carrying the graph
/// result or the scenario report as well would only invite a rule to re-derive something the layer above
/// already answered, which is how one problem ends up as two findings.
/// <para>
/// A context is built once per analysis, after every stage has added its findings, so the indices
/// <see cref="EngineFindings"/> hands back stay valid for the merge in <see cref="LintResult"/>.
/// </para>
/// </remarks>
public sealed record LintContext
{
    public required WorkflowDocument Workflow { get; init; }

    /// <summary>The stage findings, in the order the pipeline produced them.</summary>
    public required IReadOnlyList<Diagnostic> EngineFindings { get; init; }

    /// <summary>Every step of every job, in document order, paired with the job that holds it.</summary>
    public IEnumerable<(JobDefinition Job, StepDefinition Step)> Steps()
    {
        foreach (var job in Workflow.Jobs)
        {
            foreach (var step in job.Steps)
            {
                yield return (job, step);
            }
        }
    }

    /// <summary>Every <c>if:</c> value in the file — each job's and each step's — in document order.</summary>
    public IEnumerable<LintCondition> Conditions()
    {
        foreach (var job in Workflow.Jobs)
        {
            if (job.Condition is { } jobCondition)
            {
                yield return new LintCondition(job.Id, jobCondition, job.KeyRange, IsStep: false);
            }

            foreach (var step in job.Steps)
            {
                if (step.Condition is { } stepCondition)
                {
                    yield return new LintCondition(StepScope(job, step), stepCondition, step.KeyRange, IsStep: true);
                }
            }
        }
    }

    /// <summary>
    /// How a step is named in a finding's scope. The shape matches the scenario engine's own labels — the
    /// step id when it declares one, its one-based position otherwise — so the UI groups a lint finding with
    /// the condition trace of the same step.
    /// </summary>
    public static string StepScope(JobDefinition job, StepDefinition step) =>
        step.Id is { Length: > 0 } id ? $"{job.Id}.{id}" : $"{job.Id}#{step.Index + 1}";

    /// <summary>The recorded position of one field of a step, falling back to where the step begins.</summary>
    public static SourceRange StepRange(StepDefinition step, string field)
    {
        var range = step.Sources.Get(field);
        return range.IsKnown ? range : step.KeyRange;
    }

    /// <summary>
    /// The first stage finding of <paramref name="code"/> sitting at <paramref name="range"/> that
    /// <paramref name="matches"/> accepts, with its index — the index being what
    /// <see cref="LintFinding.Promoting"/> needs to take the finding over instead of duplicating it.
    /// </summary>
    public (int Index, Diagnostic Finding)? FindEngineFinding(
        string code,
        SourceRange range,
        Func<Diagnostic, bool>? matches = null)
    {
        for (var index = 0; index < EngineFindings.Count; index++)
        {
            var finding = EngineFindings[index];
            if (string.Equals(finding.Code, code, StringComparison.Ordinal)
                && finding.Range == range
                && (matches is null || matches(finding)))
            {
                return (index, finding);
            }
        }

        return null;
    }
}

/// <summary>
/// One <c>if:</c> value with the scope it belongs to. <see cref="FallbackRange"/> is where to point when the
/// condition itself carries no position, which only happens for a model built without source maps.
/// </summary>
public sealed record LintCondition(string Scope, WorkflowValue Condition, SourceRange FallbackRange, bool IsStep)
{
    public SourceRange Range => Condition.Range.IsKnown ? Condition.Range : FallbackRange;

    public string PositionPhrase => IsStep ? "a step-level if:" : "a job-level if:";
}
