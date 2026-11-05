using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Graph;
using ActionLens.Core.Matrix;
using ActionLens.Core.Text;
using ActionLens.Core.Values;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// Turns the verified parts — the workflow model, the expanded matrices, the job graph and the three-state
/// evaluator — into one answer for one scenario: which jobs run, which do not, and why.
/// </summary>
/// <remarks>
/// The engine walks the graph in topological order and never looks ahead, so a job's <c>needs</c> always
/// hold finished outcomes by the time its condition is read. Two rules do most of the work:
/// <list type="number">
///   <item>skip propagation is expressed through the status functions, not through a separate short-circuit.
///     A dependency that failed makes <c>success()</c> false, and because a condition without a status
///     function is evaluated as <c>success() &amp;&amp; (condition)</c>, the dependent job lands on
///     <see cref="RunState.WillNotRun"/> — while the same job with <c>if: always()</c> keeps running, which
///     is exactly the "unless its own condition makes it continue" clause of the documentation;</item>
///   <item>a dependency that was <em>skipped</em>, or whose outcome the scenario does not settle, lands on
///     <see cref="RunState.Unknown"/> instead. The two documented readings of that case disagree, so
///     ActionLens reports the disagreement rather than picking a side.</item>
/// </list>
/// Jobs the graph could not order — inside a <c>needs</c> cycle, or behind one — are never assigned a
/// position either: their state is unknown with a reason naming the cycle, even when their condition would
/// have evaluated to true.
/// </remarks>
public static class ScenarioEngine
{
    /// <summary>
    /// Evaluates one workflow under one scenario.
    /// </summary>
    /// <param name="workflow">The loaded model. Never null: a file that did not load has no scenario to run.</param>
    /// <param name="graph">The job graph, including the jobs it could not order.</param>
    /// <param name="matrices">
    /// Each job's expansion, keyed by job id. A job missing here is treated as having no matrix, which is
    /// what <see cref="MatrixExpansion.NoMatrix"/> means.
    /// </param>
    /// <param name="scenario">The scenario to evaluate under; <see cref="Scenario.Default"/> when null.</param>
    /// <param name="input">The caller's event payload, variables and inputs; nothing when null.</param>
    public static ScenarioEvaluation Run(
        WorkflowDocument workflow,
        JobGraph graph,
        IReadOnlyDictionary<string, MatrixExpansion>? matrices,
        Scenario? scenario = null,
        AnalysisInput? input = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(graph);

        return new ScenarioRunner(
            workflow,
            graph,
            matrices ?? new Dictionary<string, MatrixExpansion>(StringComparer.Ordinal),
            scenario ?? Scenario.Default,
            input ?? AnalysisInput.Empty).Run();
    }
}

/// <summary>
/// The one-pass evaluation behind <see cref="ScenarioEngine"/>. A class rather than a pile of static
/// helpers because the walk carries state: the outcomes computed so far, the findings collected on the
/// way, and the scenario the whole pass answers for.
/// </summary>
internal sealed class ScenarioRunner
{
    private static readonly Dictionary<string, GitHubValue> NoOutputs =
        new Dictionary<string, GitHubValue>(StringComparer.Ordinal);

    private readonly WorkflowDocument _workflow;
    private readonly JobGraph _graph;
    private readonly IReadOnlyDictionary<string, MatrixExpansion> _matrices;
    private readonly Scenario _scenario;
    private readonly AnalysisInput _input;
    private readonly Dictionary<string, JobDefinition> _jobs;
    private readonly Dictionary<string, JobOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobScenarioResult> _results = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _findings = [];

    internal ScenarioRunner(
        WorkflowDocument workflow,
        JobGraph graph,
        IReadOnlyDictionary<string, MatrixExpansion> matrices,
        Scenario scenario,
        AnalysisInput input)
    {
        _workflow = workflow;
        _graph = graph;
        _matrices = matrices;
        _scenario = scenario;
        _input = input;

        // Document order, first definition wins: the graph keeps the same rule, and two jobs with one id
        // would otherwise give the two stages different answers.
        _jobs = new Dictionary<string, JobDefinition>(StringComparer.Ordinal);
        foreach (var job in workflow.Jobs)
        {
            _jobs.TryAdd(job.Id, job);
        }
    }

    internal ScenarioEvaluation Run()
    {
        ReportPinsThatNameNoJob();
        var outputs = CollectJobOutputs();

        foreach (var jobId in _graph.TopologicalOrder)
        {
            Place(jobId, outputs);
        }

        foreach (var jobId in _graph.BlockedJobs)
        {
            PlaceWithoutOrder(jobId, outputs);
        }

        var jobs = new List<JobScenarioResult>(_graph.JobIds.Count);
        foreach (var jobId in _graph.JobIds)
        {
            if (_results.TryGetValue(jobId, out var result))
            {
                jobs.Add(result);
            }
        }

        return new ScenarioEvaluation
        {
            Jobs = jobs,
            Diagnostics = Distinct(_findings),
        };
    }

    // ------------------------------------------------------------------ job placement

    private void Place(string jobId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, GitHubValue>> outputs)
    {
        var job = _jobs[jobId];
        var expansion = ExpansionFor(jobId);
        var needs = NeedsFor(jobId, outputs);
        var pinned = _scenario.IsPinned(jobId);
        var pin = pinned ? _scenario.StateOf(jobId) : (ScenarioJobState?)null;

        // The scenario's claim about how this job itself ends, which is what its own cancelled() reads.
        var claimed = pin.HasValue ? OutcomeOf(pin.Value) : JobOutcome.Success;
        var scope = new EvaluationScope
        {
            Workflow = _workflow,
            Job = job,
            Scenario = _scenario,
            Input = _input,
            Needs = needs,
            JobOutcome = claimed,
            MatrixIsDynamic = expansion.IsDynamic,
            ConditionRange = RangeOf(job.Condition, job.KeyRange),
        };

        var trace = Trace(scope, job.Condition, isStep: false, StepLabel(job, null));
        RunState state;
        JobOutcome outcome;
        UnknownReason? reason = null;

        if (pin.HasValue)
        {
            // A pin is a claim about the run, so it wins over the derivation — and the report says which
            // jobs came from the user rather than from the workflow.
            state = pin.Value == ScenarioJobState.IsSkipped ? RunState.WillNotRun : RunState.WillRun;
            outcome = OutcomeOf(pin.Value);
            ReportConflictWithNeeds(jobId, pin.Value, needs);
        }
        else if (trace.Result == TriKind.True)
        {
            state = RunState.WillRun;
            outcome = JobOutcome.Success;
        }
        else if (trace.Result == TriKind.False)
        {
            state = RunState.WillNotRun;
            outcome = JobOutcome.Skipped;
        }
        else
        {
            state = RunState.Unknown;
            outcome = JobOutcome.Unknown;
            reason = trace.Reason;
        }

        _outcomes[jobId] = outcome;
        _results[jobId] = new JobScenarioResult
        {
            JobId = jobId,
            State = state,
            Outcome = outcome,
            Condition = trace,
            Reason = reason,
            IsPinnedByScenario = pinned,
            ScenarioPin = pin,
            Needs = needs,
            DeclaredName = job.Name,
            Runs = EvaluateRuns(job, expansion, scope, state, outcome),
        };
    }

    /// <summary>
    /// A job the graph could not order. Its condition is still read, because a context that is illegal in
    /// an <c>if:</c> is a finding the author needs whether or not the job can be placed — but no order means
    /// no answer about running, and the cycle is named in the reason instead of a position being invented.
    /// </summary>
    private void PlaceWithoutOrder(string jobId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, GitHubValue>> outputs)
    {
        var job = _jobs[jobId];
        var expansion = ExpansionFor(jobId);
        var needs = NeedsFor(jobId, outputs);
        var scope = new EvaluationScope
        {
            Workflow = _workflow,
            Job = job,
            Scenario = _scenario,
            Input = _input,
            Needs = needs,
            JobOutcome = JobOutcome.Unknown,
            MatrixIsDynamic = expansion.IsDynamic,
            ConditionRange = RangeOf(job.Condition, job.KeyRange),
        };

        var trace = Trace(scope, job.Condition, isStep: false, StepLabel(job, null));
        var cycle = DescribeCycle(jobId);
        var causes = trace.Reason is null ? new[] { cycle } : new[] { cycle, trace.Reason };
        _outcomes[jobId] = JobOutcome.Unknown;
        _results[jobId] = new JobScenarioResult
        {
            JobId = jobId,
            State = RunState.Unknown,
            Outcome = JobOutcome.Unknown,
            Condition = trace,
            Reason = new UnknownReason(
                UnknownReasonKind.ScenarioDependent,
                $"'{jobId}' cannot be placed in the run order, so whether it runs is not derivable.",
                scope.ConditionRange,
                causes),
            IsPinnedByScenario = _scenario.IsPinned(jobId),
            ScenarioPin = _scenario.IsPinned(jobId) ? _scenario.StateOf(jobId) : null,
            Needs = needs,
            DeclaredName = job.Name,
            Runs = EvaluateRuns(job, expansion, scope, RunState.Unknown, JobOutcome.Unknown),
        };
    }

    private UnknownReason DescribeCycle(string jobId)
    {
        var cycle = _graph.Cycles
            .FirstOrDefault(path => path.Contains(jobId, StringComparer.Ordinal));

        return new UnknownReason(
            UnknownReasonKind.ScenarioDependent,
            cycle is null
                ? $"'{jobId}' waits behind a needs cycle, so it has no position in the run order."
                : $"Its needs form a cycle: {string.Join(" -> ", cycle)}.",
            _jobs.TryGetValue(jobId, out var job) ? job.KeyRange : SourceRange.Unknown);
    }

    private void ReportConflictWithNeeds(string jobId, ScenarioJobState pin, IReadOnlyList<NeedOutcome> needs)
    {
        if (pin == ScenarioJobState.IsSkipped)
        {
            return;
        }

        foreach (var need in needs)
        {
            if (need.Outcome is JobOutcome.Failure or JobOutcome.Skipped or JobOutcome.Cancelled)
            {
                _findings.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.ScenarioConflict,
                    Severity = DiagnosticSeverity.Warning,
                    Stage = DiagnosticStage.Scenario,
                    Message = $"The scenario pins '{jobId}' to {ScenarioPhrase(pin)}, but '{need.JobId}' — which it " +
                        "needs — does not succeed, so a real run would skip it rather than run it. The pin is kept.",
                    Range = _jobs[jobId].KeyRange,
                    Scope = jobId,
                    Suggestion = "Pin the dependency as well, or drop this pin and let the scenario derive the job.",
                });
                return;
            }
        }
    }

    private void ReportPinsThatNameNoJob()
    {
        foreach (var pair in _scenario.JobStates.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (_jobs.ContainsKey(pair.Key))
            {
                continue;
            }

            _findings.Add(new Diagnostic
            {
                Code = DiagnosticCodes.ScenarioUnknownJob,
                Severity = DiagnosticSeverity.Warning,
                Stage = DiagnosticStage.Scenario,
                Message = $"The scenario pins '{pair.Key}' to {ScenarioPhrase(pair.Value)}, but this workflow has no job with that id.",
                Range = new SourceRange(new SourcePosition(1, 1)),
                Suggestion = $"Name one of: {string.Join(", ", _jobs.Keys.OrderBy(key => key, StringComparer.Ordinal))}.",
            });
        }
    }

    // ------------------------------------------------------------------ steps

    private List<MatrixRun> EvaluateRuns(
        JobDefinition job,
        MatrixExpansion expansion,
        EvaluationScope jobScope,
        RunState jobState,
        JobOutcome outcome)
    {
        // The step position reads the job's derived outcome, not the placeholder the job's own condition
        // was read with.
        var scope = jobScope with { JobOutcome = outcome };

        if (expansion.IsDynamic)
        {
            // One representative row: the combination is real but unknowable, and every step condition that
            // reads matrix.* comes back unknown for that reason.
            return
            [
                new MatrixRun
                {
                    Index = 0,
                    CombinationIsKnown = false,
                    Label = null,
                    Steps = StepsFor(job, scope with { Combination = null, MatrixIsDynamic = true }, jobState),
                },
            ];
        }

        if (expansion.Combinations.Count == 0)
        {
            return [];
        }

        // A job with no matrix runs once, and its single row gets no label: there is nothing to name.
        var hasMatrix = job.Strategy?.Matrix is not null;
        var runs = new List<MatrixRun>(expansion.Combinations.Count);
        foreach (var combination in expansion.Combinations)
        {
            runs.Add(new MatrixRun
            {
                Index = combination.Index,
                CombinationIsKnown = true,
                Label = hasMatrix ? combination.DisplayName : null,
                Values = combination.Values.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Text ?? pair.Value.Value.Stringify(),
                    StringComparer.Ordinal),
                Steps = StepsFor(job, scope with { Combination = combination, MatrixIsDynamic = false }, jobState),
            });
        }

        return runs;
    }

    private List<StepRunResult> StepsFor(
        JobDefinition job,
        EvaluationScope runScope,
        RunState jobState)
    {
        var steps = new List<StepRunResult>(job.Steps.Count);
        foreach (var step in job.Steps)
        {
            if (jobState == RunState.WillNotRun)
            {
                // A step inside a job that does not run never reaches its condition, so there is nothing
                // to evaluate and nothing to claim.
                steps.Add(new StepRunResult
                {
                    Index = step.Index,
                    StepId = step.Id,
                    Name = step.Name,
                    State = RunState.WillNotRun,
                });
                continue;
            }

            var scope = runScope.ForStep(step, runScope.JobOutcome, RangeOf(step.Condition, step.KeyRange));
            var trace = Trace(scope, step.Condition, isStep: true, StepLabel(job, step));

            RunState state;
            UnknownReason? reason;
            if (trace.Result == TriKind.False)
            {
                // Definitely false settles it whichever way the job itself goes.
                state = RunState.WillNotRun;
                reason = null;
            }
            else if (jobState == RunState.WillRun && trace.Result == TriKind.True)
            {
                state = RunState.WillRun;
                reason = null;
            }
            else
            {
                state = RunState.Unknown;
                reason = StepReason(jobState, trace, scope);
            }

            steps.Add(new StepRunResult
            {
                Index = step.Index,
                StepId = step.Id,
                Name = step.Name,
                State = state,
                Condition = trace,
                Reason = reason,
            });
        }

        return steps;
    }

    /// <summary>
    /// Why a step is undecided: its own open condition, the job that may not run, or both.
    /// </summary>
    private static UnknownReason? StepReason(RunState jobState, ConditionTrace trace, EvaluationScope scope)
    {
        var causes = new List<UnknownReason>();
        if (jobState == RunState.Unknown)
        {
            causes.Add(new UnknownReason(
                UnknownReasonKind.ScenarioDependent,
                "The job itself may not run, so no step of it is settled either.",
                scope.ConditionRange));
        }

        if (trace.Reason is { } reason)
        {
            causes.Add(reason);
        }

        return causes.Count switch
        {
            0 => new UnknownReason(
                UnknownReasonKind.ScenarioDependent,
                "Neither the job nor this condition settles whether the step runs.",
                scope.ConditionRange),
            1 => causes[0],
            _ => new UnknownReason(
                UnknownReasonKind.Propagated,
                "Both this condition and the job it belongs to are undecided.",
                scope.ConditionRange,
                causes),
        };
    }

    // ------------------------------------------------------------------ shared reading

    private ConditionTrace Trace(EvaluationScope scope, WorkflowValue? condition, bool isStep, string? scopeLabel)
    {
        EvaluationPosition position = isStep ? new StepPositionContext(scope) : new JobPositionContext(scope);
        foreach (var diagnostic in position.Diagnostics)
        {
            _findings.Add(diagnostic);
        }

        var outcome = new ConditionEvaluator(position).Evaluate(condition);
        foreach (var diagnostic in outcome.Diagnostics)
        {
            _findings.Add(diagnostic with { Scope = scopeLabel });
        }

        return new ConditionTrace
        {
            Text = outcome.Text,
            EvaluatedAs = outcome.Root is null
                ? (condition?.Text?.Trim() ?? string.Empty)
                : ExpressionTreeText.Render(outcome.Root),
            Result = outcome.Value.Kind,
            Reason = outcome.Value.Reason,
            ImplicitSuccessApplied = outcome.ImplicitSuccessApplied,
            Range = scope.ConditionRange,
        };
    }

    private List<NeedOutcome> NeedsFor(
        string jobId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, GitHubValue>> outputs)
    {
        var needs = new List<NeedOutcome>();
        foreach (var dependency in _graph.DependenciesOf(jobId))
        {
            var outcome = _outcomes.TryGetValue(dependency, out var computed) ? computed : JobOutcome.Unknown;
            needs.Add(new NeedOutcome(
                dependency,
                outcome,
                outputs.TryGetValue(dependency, out var published) ? published : NoOutputs));
        }

        return needs;
    }

    /// <summary>
    /// The outputs each job publishes, keeping only the values a static read can resolve. A run produces
    /// the rest, and a dependent that reads one gets <c>null</c> rather than an unknown, so the finding
    /// names the key instead of leaving the gap invisible.
    /// </summary>
    private Dictionary<string, IReadOnlyDictionary<string, GitHubValue>> CollectJobOutputs()
    {
        var published = new Dictionary<string, IReadOnlyDictionary<string, GitHubValue>>(StringComparer.Ordinal);
        foreach (var job in _workflow.Jobs)
        {
            var values = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
            foreach (var pair in job.Outputs)
            {
                var text = pair.Value.Text ?? string.Empty;
                if (text.Contains("${{", StringComparison.Ordinal))
                {
                    _findings.Add(new Diagnostic
                    {
                        Code = DiagnosticCodes.ScenarioValueUnresolved,
                        Severity = DiagnosticSeverity.Info,
                        Stage = DiagnosticStage.Scenario,
                        Message = $"'{job.Id}' publishes '{pair.Key}' from an expression, so needs.{job.Id}.outputs.{pair.Key} " +
                            "has no static value: a condition that reads one gets null, which is not the same as an unknown.",
                        Range = pair.Value.Range.IsKnown ? pair.Value.Range : job.KeyRange,
                        Scope = job.Id,
                    });
                    continue;
                }

                values[pair.Key] = pair.Value.Value;
            }

            published[job.Id] = values;
        }

        return published;
    }

    private MatrixExpansion ExpansionFor(string jobId) =>
        _matrices.TryGetValue(jobId, out var expansion) ? expansion : MatrixExpansion.NoMatrix;

    private static JobOutcome OutcomeOf(ScenarioJobState pin) => pin switch
    {
        ScenarioJobState.Succeeds => JobOutcome.Success,
        ScenarioJobState.Fails => JobOutcome.Failure,
        ScenarioJobState.IsSkipped => JobOutcome.Skipped,
        _ => JobOutcome.Cancelled,
    };

    private static string ScenarioPhrase(ScenarioJobState pin) => pin switch
    {
        ScenarioJobState.Succeeds => "succeed",
        ScenarioJobState.Fails => "fail",
        ScenarioJobState.IsSkipped => "be skipped",
        _ => "be cancelled",
    };

    private static SourceRange RangeOf(WorkflowValue? value, SourceRange fallback) =>
        value is { } condition && condition.Range.IsKnown ? condition.Range : fallback;

    private static string? StepLabel(JobDefinition job, StepDefinition? step) => step is null
        ? job.Id
        : step.Id is { } id ? $"{job.Id}.{id}" : $"{job.Id}#{step.Index + 1}";

    /// <summary>
    /// Keeps the first of any repeated finding. A job's <c>env</c> chain is rebuilt for every step of every
    /// matrix combination, so the same unresolvable value would otherwise be reported once per combination.
    /// </summary>
    private static List<Diagnostic> Distinct(List<Diagnostic> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<Diagnostic>(findings.Count);
        foreach (var finding in findings)
        {
            var key = $"{finding.Code}|{finding.Severity}|{finding.Stage}|{finding.Message}|{finding.Range}|{finding.Scope}";
            if (seen.Add(key))
            {
                kept.Add(finding);
            }
        }

        return kept;
    }
}
