using ActionLens.Core.Diagnostics;
using ActionLens.Core.Graph;
using ActionLens.Core.Linting;
using ActionLens.Core.Matrix;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Text;
using ActionLens.Core.Triggers;
using ActionLens.Core.Workflows;
using ActionLens.Core.Yaml;

namespace ActionLens.Core;

/// <summary>
/// The one entry point: workflow text, an optional event payload and an optional scenario in, one
/// <see cref="AnalysisReport"/> out.
/// </summary>
/// <remarks>
/// The pipeline is the one in <c>ARCHITECTURE.md</c> — load, expand each job's matrix, build the graph,
/// run the scenario, assemble the report — and every stage's findings travel with it, so a caller that
/// shows the report cannot hide the reason part of it is qualified.
/// <para>
/// It does not throw. A workflow that cannot be read still produces a report holding the findings that
/// say why there is nothing else, and an internal defect in one stage produces the same shape with an
/// <c>INT001</c> finding, because a preflight tool that disappears on a malformed file is worse than one
/// that reports it.
/// </para>
/// <para>
/// Nothing here reads a file, opens a socket, reads a clock or draws a random number, and no stage of it
/// knows Blazor exists. The same inputs give the same report bytes.
/// </para>
/// <para>
/// For a chosen event it also answers whether the trigger fired and which filter said no, in
/// <see cref="TriggerReport.Verdict"/>. A <c>schedule</c> and an unrecorded <c>workflow_dispatch</c> come
/// back as <c>unknown</c> with the reason that says so — that is the point of the verdict, not a gap in
/// it: the facts they would need are a clock and a person.
/// </para>
/// </remarks>
public static class WorkflowAnalyzer
{
    /// <summary>
    /// Analyses one workflow.
    /// </summary>
    /// <param name="yaml">The workflow text. Null or empty is a finding, not an exception.</param>
    /// <param name="fileName">The file's name, used as the workflow's identity when it has no <c>name:</c>.</param>
    /// <param name="eventPayloadJson">
    /// The event payload as JSON, from which the <c>github</c> context and — when it carries them — the
    /// <c>inputs</c> and <c>vars</c> contexts are derived. See
    /// <see cref="AnalysisInput.FromEventPayload(string?, string?)"/> for the exact keys and what each one
    /// becomes. Null means nothing about the event is knowable, which the report shows as unknowns rather
    /// than as guesses.
    /// </param>
    /// <param name="scenario">The scenario to answer for; <see cref="Scenario.Default"/> when null.</param>
    /// <param name="triggerEvent">
    /// Which event to answer "did this trigger fire" for. Null takes it from the payload's own
    /// <c>event_name</c>, which is how a recorded run says what triggered it, and no verdict is produced
    /// when neither is available — see <see cref="TriggerEvaluator.Evaluate"/>. A caller that lets the
    /// reader pick an event (the UI's event dropdown) passes it here, and a selection that disagrees with
    /// the payload is reported as a finding rather than silently preferred.
    /// </param>
    public static AnalysisReport Analyze(
        string? yaml,
        string? fileName = null,
        string? eventPayloadJson = null,
        Scenario? scenario = null,
        string? triggerEvent = null)
    {
        // github.workflow comes from the workflow's own name, so the payload is read after the load and the
        // two findings lists stay separate until the report is assembled. The payload is untrusted input
        // too, so it gets the same "never escape as an exception" treatment as the YAML pass.
        var load = TryLoad(yaml, fileName);
        var input = TryReadPayload(eventPayloadJson, load.Workflow?.Name ?? fileName);
        return AnalyzeCore(load, input, scenario, fileName, triggerEvent);
    }

    /// <summary>
    /// The same analysis with an input the caller built, for the contexts a payload cannot carry —
    /// repository variables and dispatch inputs supplied out of band.
    /// </summary>
    public static AnalysisReport Analyze(
        string? yaml,
        string? fileName,
        AnalysisInput? input,
        Scenario? scenario = null,
        string? triggerEvent = null)
    {
        return AnalyzeCore(TryLoad(yaml, fileName), input, scenario, fileName, triggerEvent);
    }

    /// <summary>
    /// Derives the input from the payload, turning any reader failure into a finding. The public
    /// <c>Analyze</c> must not throw even when a payload is hostile, and this call used to sit outside
    /// <see cref="AnalyzeCore"/>'s catch-all.
    /// </summary>
    private static AnalysisInput TryReadPayload(string? eventPayloadJson, string? workflowName)
    {
        try
        {
            return AnalysisInput.FromEventPayload(eventPayloadJson, workflowName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            return new AnalysisInput
            {
                Diagnostics =
                [
                    new Diagnostic
                    {
                        Code = DiagnosticCodes.InvalidValue,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Scenario,
                        Message = $"The event payload could not be read: {ex.Message}",
                        Range = new SourceRange(new SourcePosition(1, 1)),
                        Suggestion = "Paste the event body as one JSON object.",
                    },
                ],
            };
        }
    }

    /// <summary>
    /// Reads the file, and turns a reader that throws into a finding.
    /// </summary>
    /// <remarks>
    /// <c>WorkflowLoader.Load</c> is built to answer with diagnostics, but YamlDotNet's scanner throws a
    /// plain <see cref="InvalidOperationException"/> from inside a scalar scan on an unterminated flow
    /// sequence, which is not the <c>YamlException</c> the loader catches. The analyzer's promise is that
    /// no input escapes as an exception, so the hole is closed here rather than by changing the reader.
    /// </remarks>
    private static WorkflowLoadResult TryLoad(string? yaml, string? fileName)
    {
        try
        {
            return WorkflowLoader.Load(yaml ?? string.Empty, fileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            return new WorkflowLoadResult
            {
                Diagnostics =
                [
                    new Diagnostic
                    {
                        Code = DiagnosticCodes.YamlSyntax,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Yaml,
                        Message = $"The workflow text could not be read as YAML: {ex.Message}",
                        Range = new SourceRange(new SourcePosition(1, 1)),
                        Suggestion = "Check for an unbalanced '[' or '{' in a flow-style value.",
                    },
                ],
            };
        }
    }

    /// <summary>Runs the whole pipeline over an already-loaded workflow and a caller-built input.</summary>
    public static AnalysisReport Analyze(
        WorkflowLoadResult load,
        AnalysisInput? input = null,
        Scenario? scenario = null,
        string? triggerEvent = null) => AnalyzeCore(load, input, scenario, fileName: null, triggerEvent);

    private static AnalysisReport AnalyzeCore(
        WorkflowLoadResult load,
        AnalysisInput? input,
        Scenario? scenario,
        string? fileName,
        string? triggerEvent)
    {
        ArgumentNullException.ThrowIfNull(load);

        var analysisInput = input ?? AnalysisInput.Empty;
        var activeScenario = scenario ?? Scenario.Default;
        var diagnostics = new List<Diagnostic>();
        AddAll(diagnostics, load.Diagnostics);
        AddAll(diagnostics, analysisInput.Diagnostics);

        try
        {
            return Build(load, analysisInput, activeScenario, diagnostics, fileName, triggerEvent);
        }
        catch (Exception ex)
        {
            // Reached only by a defect in ActionLens: every input problem is already a positioned finding.
            // The report that comes back says so instead of taking the answer away from the reader.
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AnalysisFailed,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Internal,
                Message = $"ActionLens stopped partway through this analysis: {ex.GetType().Name}: {ex.Message}",
                Range = new SourceRange(new SourcePosition(1, 1)),
                Suggestion = "Report this as a bug; the findings above are the part that did complete.",
            });
            return EmptyReport(load, activeScenario, analysisInput, diagnostics, fileName);
        }
    }

    private static AnalysisReport Build(
        WorkflowLoadResult load,
        AnalysisInput input,
        Scenario scenario,
        List<Diagnostic> diagnostics,
        string? fileName,
        string? triggerEvent)
    {
        if (load.Workflow is not { } workflow)
        {
            // No model, so nothing downstream has anything to work on. The findings are the report.
            return EmptyReport(load, scenario, input, diagnostics, fileName);
        }

        var matrices = ExpandMatrices(workflow, diagnostics);
        var graph = JobGraph.Build(workflow);
        AddAll(diagnostics, graph.Diagnostics);

        var evaluation = ScenarioEngine.Run(workflow, graph.Graph, matrices, scenario, input);
        AddAll(diagnostics, evaluation.Diagnostics);

        // Last, because it reads the same model the stages above read and its findings belong after theirs.
        var trigger = EvaluateTriggers(workflow, input, triggerEvent, diagnostics);

        // After every stage, because AL004 and AL006 promote the findings those stages already made: the lint
        // layer reads their positions and their explanations rather than detecting the same problems twice.
        var findings = LintEngine
            .Run(new LintContext { Workflow = workflow, EngineFindings = diagnostics })
            .MergeInto(diagnostics);

        var documentOrder = workflow.Jobs.Select(job => job.Id).ToArray();
        var jobs = new List<JobReport>(evaluation.Jobs.Count);
        var steps = 0;
        int? instances = 0;
        foreach (var result in evaluation.Jobs)
        {
            var expansion = matrices.TryGetValue(result.JobId, out var found) ? found : MatrixExpansion.NoMatrix;
            var job = workflow.FindJob(result.JobId);
            steps += job?.Steps.Count ?? 0;
            var placement = graph.Layout.NodeFor(result.JobId);

            if (expansion.IsDynamic)
            {
                // A dimension produced at run time hides the total: a partial count would read as a fact.
                instances = null;
            }
            else if (instances is { } counted)
            {
                instances = counted + expansion.Combinations.Count;
            }

            jobs.Add(new JobReport
            {
                JobId = result.JobId,
                DeclaredName = result.DeclaredName,
                State = result.State,
                Outcome = result.Outcome,
                Reason = result.Reason,
                Condition = result.Condition,
                Needs = job?.Needs ?? [],
                NeedsOutcomes = result.Needs,
                Matrix = Summarise(job, expansion),
                Runs = result.Runs,
                IsPinnedByScenario = result.IsPinnedByScenario,
                ScenarioPin = result.ScenarioPin,
                Placement = placement is null ? null : new NodePlacement(placement.Layer, placement.Slot),
            });
        }

        return new AnalysisReport
        {
            Workflow = Identity(workflow, fileName),
            Event = BuildEvent(input),
            Trigger = BuildTrigger(workflow, trigger.Verdict),
            Scenario = BuildScenario(scenario, documentOrder),
            Jobs = jobs,
            Graph = BuildGraph(graph),
            Diagnostics = findings,
            Totals = Totals(findings, jobs, steps, instances),
            IsBlocked = findings.Any(finding => finding.Severity >= DiagnosticSeverity.Error),
        };
    }

    // ------------------------------------------------------------------ stages

    /// <summary>
    /// The trigger layer: which event the question was about, and what its filters answered.
    /// </summary>
    /// <remarks>
    /// The event name is taken from the caller's selection when there is one and from the payload's own
    /// <c>event_name</c> otherwise: a recorded run carries its own event, while a reader picking an event
    /// in the UI has not supplied a payload for it yet. When the two disagree the selection is what the
    /// verdict answers, and the disagreement is a finding — see <see cref="TriggerEvaluator"/>.
    /// </remarks>
    private static TriggerEvaluation EvaluateTriggers(
        WorkflowDocument workflow,
        AnalysisInput input,
        string? triggerEvent,
        List<Diagnostic> diagnostics)
    {
        var selected = triggerEvent is { Length: > 0 } selection ? selection : input.EventName;
        var payload = TriggerPayload.FromContext(input.GitHub, input.Inputs);
        var evaluation = TriggerEvaluator.Evaluate(workflow.Triggers, selected, payload);
        AddAll(diagnostics, evaluation.Diagnostics);
        return evaluation;
    }

    /// <summary>
    /// Expands every job's matrix once, up front. The scenario needs the combinations to name each job
    /// instance and the report needs them to say how many runners a job turns into; expanding per position
    /// would let the two disagree.
    /// </summary>
    private static Dictionary<string, MatrixExpansion> ExpandMatrices(
        WorkflowDocument workflow,
        List<Diagnostic> diagnostics)
    {
        var expansions = new Dictionary<string, MatrixExpansion>(StringComparer.Ordinal);
        foreach (var job in workflow.Jobs)
        {
            var matrix = job.Strategy?.Matrix;
            if (matrix is null)
            {
                expansions[job.Id] = MatrixExpansion.NoMatrix;
                continue;
            }

            var range = job.Sources.Get("strategy");
            var expansion = MatrixExpander.Expand(job.Strategy, matrix, range.IsKnown ? range : job.KeyRange);
            expansions[job.Id] = expansion;
            foreach (var finding in expansion.Diagnostics)
            {
                diagnostics.Add(finding with { Scope = job.Id });
            }
        }

        return expansions;
    }

    private static MatrixReport? Summarise(JobDefinition? job, MatrixExpansion expansion)
    {
        var matrix = job?.Strategy?.Matrix;
        if (matrix is null)
        {
            return null;
        }

        return new MatrixReport
        {
            IsDynamic = expansion.IsDynamic,
            DynamicDimensions = expansion.DynamicDimensions,
            Dimensions = [.. matrix.Dimensions.Select(dimension => dimension.Name)],
            CombinationCount = expansion.JobCount,
            ProducedCount = expansion.ProducedCount,
            Truncated = expansion.Truncated,
            CombinationLabels = [.. expansion.Combinations.Select(combination => combination.DisplayName)],
        };
    }

    private static WorkflowIdentity Identity(WorkflowDocument workflow, string? fileName) => new()
    {
        // The loader already falls back to the file name for `name:`, so this only repeats it.
        Name = workflow.Name,
        FileName = fileName,
        RunName = workflow.RunName,
        JobCount = workflow.Jobs.Count,
        PermissionKeys = [.. workflow.Permissions.Keys.OrderBy(key => key, StringComparer.Ordinal)],
        ConcurrencyGroup = workflow.Concurrency?.Group,
        CancelInProgress = workflow.Concurrency?.CancelInProgress ?? false,
    };

    private static EventReport BuildEvent(AnalysisInput input) => new()
    {
        HasEventPayload = input.HasEventPayload,
        EventName = input.EventName,
        VarsSupplied = input.Vars is not null,
        InputsSupplied = input.Inputs is not null,
    };

    /// <summary>
    /// What <c>on:</c> declares, plus the verdict for the event that was asked about when one was. The
    /// declared list is the file's own shape and travels even when no event was selected; the verdict is
    /// the answer with the pattern that decided it, so a reader can see which line said no.
    /// </summary>
    private static TriggerReport BuildTrigger(WorkflowDocument workflow, TriggerVerdict? verdict)
    {
        var declared = new List<DeclaredTrigger>(workflow.Triggers.Events.Count);
        // Document order comes from the event keys' positions, never from the dictionary's enumeration
        // order. The name is a tie-breaker for two events recorded at the same position.
        foreach (var trigger in workflow.Triggers.Events.Values
                     .OrderBy(trigger => trigger.Range.Start.Line)
                     .ThenBy(trigger => trigger.Range.Start.Column)
                     .ThenBy(trigger => trigger.Name, StringComparer.Ordinal))
        {
            declared.Add(new DeclaredTrigger(trigger.Name, trigger.HasConfiguration, FilterNames(trigger)));
        }

        return new TriggerReport { Declared = declared, IsShorthand = workflow.Triggers.IsShorthand, Verdict = verdict };
    }

    private static List<string> FilterNames(EventTrigger trigger)
    {
        // A fixed order, independent of how the author happened to write the keys, so two files declaring
        // the same filters export the same text.
        var names = new List<string>();
        Add("types", trigger.Types.Count);
        Add("branches", trigger.Branches.Count);
        Add("branches-ignore", trigger.BranchesIgnore.Count);
        Add("tags", trigger.Tags.Count);
        Add("tags-ignore", trigger.TagsIgnore.Count);
        Add("paths", trigger.Paths.Count);
        Add("paths-ignore", trigger.PathsIgnore.Count);
        Add("inputs", trigger.Inputs.Count);
        Add("cron", trigger.Cron.Count);
        return names;

        void Add(string name, int count)
        {
            if (count > 0)
            {
                names.Add(name);
            }
        }
    }

    private static GraphReport BuildGraph(JobGraphAnalysisResult result) => new()
    {
        IsAcyclic = result.Graph.IsAcyclic,
        JobCount = result.Graph.JobCount,
        EdgeCount = result.Graph.Edges.Count,
        Cycles = result.Graph.Cycles,
        UnplacedJobs = result.Graph.BlockedJobs,
        Nodes = result.Layout.Nodes,
        Edges = result.Layout.Edges,
        LayerCount = result.Layout.LayerCount,
        MaxLayerWidth = result.Layout.MaxLayerWidth,
    };

    private static ScenarioReport BuildScenario(Scenario scenario, IReadOnlyList<string> documentOrder)
    {
        var pins = new List<ScenarioPin>();
        foreach (var jobId in documentOrder.Where(jobId => scenario.JobStates.ContainsKey(jobId)))
        {
            pins.Add(new ScenarioPin(jobId, scenario.JobStates[jobId]));
        }

        // An id the file does not have still belongs in the export, listed after the real pins and sorted,
        // because a scenario that names nothing is exactly the mistake a reader needs to see.
        foreach (var pair in scenario.JobStates
                 .Where(entry => !documentOrder.Contains(entry.Key, StringComparer.Ordinal))
                 .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            pins.Add(new ScenarioPin(pair.Key, pair.Value));
        }

        return new ScenarioReport
        {
            Label = scenario.Name ?? "default",
            Description = scenario.Describe(documentOrder),
            WorkflowCancelled = scenario.WorkflowCancelled,
            Pins = pins,
        };
    }

    private static ReportTotals Totals(
        IReadOnlyList<Diagnostic> diagnostics,
        List<JobReport> jobs,
        int steps,
        int? jobInstances) => new()
        {
            Jobs = jobs.Count,
            WillRun = jobs.Count(job => job.State == RunState.WillRun),
            WillNotRun = jobs.Count(job => job.State == RunState.WillNotRun),
            UnknownJobs = jobs.Count(job => job.State == RunState.Unknown),
            Steps = steps,
            JobInstances = jobInstances,
            Diagnostics = diagnostics.Count,
            Criticals = diagnostics.Count(finding => finding.Severity == DiagnosticSeverity.Critical),
            Errors = diagnostics.Count(finding => finding.Severity == DiagnosticSeverity.Error),
            Warnings = diagnostics.Count(finding => finding.Severity == DiagnosticSeverity.Warning),
            Infos = diagnostics.Count(finding => finding.Severity == DiagnosticSeverity.Info),
        };

    // ------------------------------------------------------------------ degradation

    /// <summary>
    /// A report for the case where there is no model to analyse. The findings still come back, the job list
    /// is empty rather than missing, and the layout is the empty graph's, so one code path in a UI can
    /// handle a broken file and a healthy one.
    /// </summary>
    private static AnalysisReport EmptyReport(
        WorkflowLoadResult load,
        Scenario scenario,
        AnalysisInput input,
        List<Diagnostic> diagnostics,
        string? fileName)
    {
        var workflow = load.Workflow;
        var documentOrder = workflow?.Jobs.Select(job => job.Id).ToArray() ?? [];
        var graph = JobGraph.Build(workflow?.Jobs ?? []);

        return new AnalysisReport
        {
            Workflow = new WorkflowIdentity
            {
                Name = workflow?.Name ?? fileName ?? "unnamed workflow",
                FileName = fileName,
                RunName = workflow?.RunName,
                JobCount = workflow?.Jobs.Count ?? 0,
            },
            Event = BuildEvent(input),
            Trigger = workflow is null
                ? new TriggerReport { Declared = [], IsShorthand = false }
                : BuildTrigger(workflow, verdict: null),
            Scenario = BuildScenario(scenario, documentOrder),
            Jobs = [],
            Graph = BuildGraph(graph),
            Diagnostics = diagnostics,
            Totals = Totals(diagnostics, [], 0, 0),
            IsBlocked = true,
        };
    }

    private static void AddAll(List<Diagnostic> target, IReadOnlyList<Diagnostic> source)
    {
        foreach (var finding in source)
        {
            target.Add(finding);
        }
    }
}
