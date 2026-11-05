using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Values;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Scenarios;

/// <summary>
/// The machinery both evaluation positions share: GitHub's context availability table, the values this
/// analysis can actually see, and a reason for every context it cannot.
/// </summary>
/// <remarks>
/// The ordering of the interface calls is the design. <see cref="IsAvailable"/> answers "does GitHub
/// allow this name here at all" — a schema question, answered from the availability table before anything
/// is looked up. Only after that does <see cref="TryGetContext"/> ask "do we know its value". Collapsing
/// the two would report <c>secrets.FOO</c> in an <c>if:</c> as a gap in ActionLens's knowledge, when it is
/// a workflow GitHub rejects.
/// <para>
/// A context that is legal here but unknowable is registered with the reason it is unknowable, so the
/// reason travels with the value in every unknown the evaluator produces.
/// </para>
/// </remarks>
public abstract class EvaluationPosition : IContextProvider
{
    /// <summary>
    /// What <c>jobs.&lt;job_id&gt;.if</c> may read. <c>env</c>, <c>matrix</c>, <c>strategy</c>,
    /// <c>steps</c>, <c>job</c> and <c>runner</c> are absent on purpose: the job condition runs before the
    /// job, and before its matrix, exists.
    /// </summary>
    private static readonly HashSet<string> JobContexts =
        new(StringComparer.OrdinalIgnoreCase) { "github", "needs", "vars", "inputs" };

    /// <summary>What a step condition may read. <c>secrets</c> is in neither list, and never in an <c>if:</c>.</summary>
    private static readonly HashSet<string> StepContexts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "github", "needs", "strategy", "matrix", "job", "runner", "env", "vars", "steps", "inputs",
        };

    private readonly Dictionary<string, GitHubValue> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnknownReason> _unknown = new(StringComparer.OrdinalIgnoreCase);

    protected EvaluationPosition(EvaluationScope scope, EvaluationPositionKind kind)
    {
        Scope = scope;
        Kind = kind;
        Contexts = kind == EvaluationPositionKind.Job ? JobContexts : StepContexts;

        Add("github", scope.Input.GitHub, Missing(
            "github",
            "No event payload was supplied, so nothing about the event — ref, event_name, repository, the " +
            "payload object — has a value to compare against."));
        Add("vars", scope.Input.Vars, Missing(
            "vars",
            "Repository and organisation variables were not supplied, so vars.* has no value here."));
        Add("inputs", scope.Input.Inputs, Missing(
            "inputs",
            "The run's workflow_dispatch or workflow_call inputs were not supplied, so inputs.* has no value here."));

        var needs = BuildNeeds(out var needsReason);
        if (needs is null)
        {
            _unknown["needs"] = needsReason!;
        }
        else
        {
            _known["needs"] = needs;
        }

        if (kind == EvaluationPositionKind.Step)
        {
            AddStepContexts();
        }
    }

    protected EvaluationScope Scope { get; }

    protected EvaluationPositionKind Kind { get; }

    /// <summary>The context names GitHub allows in this position.</summary>
    protected IReadOnlySet<string> Contexts { get; }

    /// <summary>
    /// Findings this position had to make while building its own contexts — an <c>env</c> value only a run
    /// can produce, for instance. The scenario engine collects them once per analysis.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = Array.Empty<Diagnostic>();

    /// <summary>How this position is written in the workflow, in the words a reader of the file would use.</summary>
    protected string PositionPhrase => Kind == EvaluationPositionKind.Job
        ? "a job-level if: (jobs.<job_id>.if)"
        : "a step-level if: (jobs.<job_id>.steps.<n>.if)";

    public bool IsAvailable(string contextName) => Contexts.Contains(contextName);

    public bool TryGetContext(string contextName, out GitHubValue context)
    {
        if (_known.TryGetValue(contextName, out var found) && found is not null)
        {
            context = found;
            return true;
        }

        context = GitHubValue.Null;
        return false;
    }

    public UnknownReason ExplainContextUnknown(string contextName) =>
        _unknown.TryGetValue(contextName, out var reason)
            ? reason
            : new UnknownReason(
                UnknownReasonKind.RuntimeContext,
                $"'{contextName}' describes work that only a run can report, and nothing has run.",
                Scope.ConditionRange);

    public UnknownReason ExplainUnavailable(string symbolName)
    {
        // Ordinal, not the default comparer: the message is part of the exported report, and a report has
        // to read the same on every machine.
        var allowed = string.Join(", ", Contexts.OrderBy(name => name, StringComparer.Ordinal));
        var extra = string.Equals(symbolName, "secrets", StringComparison.OrdinalIgnoreCase)
            ? " 'secrets' appears in no position's list at all: the documentation states directly that it " +
              "cannot be referenced in an if: condition, so this is a workflow GitHub would reject."
            : Kind == EvaluationPositionKind.Job
                ? " Only the four status functions are extra functions here, so hashFiles() belongs to a step."
                : string.Empty;

        return new UnknownReason(
            UnknownReasonKind.ContextNotAvailable,
            $"'{symbolName}' is not available in {PositionPhrase}. GitHub's context availability table " +
            $"limits this position to: {allowed}.{extra}",
            Scope.ConditionRange);
    }

    public bool IsFunctionAvailable(string functionName) =>
        Kind == EvaluationPositionKind.Step
        || !string.Equals(functionName, "hashFiles", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scenario's answer for one status function, or no answer at all.</summary>
    public abstract bool TryGetStatus(StatusFunction statusFunction, out TriValue value);

    /// <summary>
    /// <c>success()</c> is true only when every required upstream is definitely successful. A failed or
    /// cancelled upstream settles it to false; a skipped or undecided one settles it to nothing, because
    /// the documentation says both "a dependent job is skipped" and "a job that is skipped will report its
    /// status as Success", and job-level status functions are evaluated server-side with no public
    /// implementation to check.
    /// </summary>
    protected TriValue SuccessOfNeeds(IReadOnlyList<NeedOutcome> needs, string what)
    {
        foreach (var need in needs)
        {
            if (need.Outcome is JobOutcome.Failure or JobOutcome.Cancelled)
            {
                return TriValue.False;
            }
        }

        foreach (var need in needs)
        {
            if (need.Outcome == JobOutcome.Skipped)
            {
                return TriValue.Unknown(new UnknownReason(
                    UnknownReasonKind.ScenarioDependent,
                    $"'{need.JobId}' is skipped in this scenario, and whether {what} is true after a skipped " +
                    "ancestor is unresolved in the documentation: the needs page implies the dependent job " +
                    "does not run, the status-checks page says a skipped job reports Success. ActionLens " +
                    "reports unknown rather than choosing a reading.",
                    Scope.ConditionRange));
            }

            if (need.Outcome == JobOutcome.Unknown)
            {
                return TriValue.Unknown(new UnknownReason(
                    UnknownReasonKind.ScenarioDependent,
                    $"'{need.JobId}' is one of this job's needs and the scenario does not settle how it ends, " +
                    $"so {what} has no answer yet.",
                    Scope.ConditionRange));
            }
        }

        // No needs at all is vacuously success(): a job that waits for nothing starts as soon as the
        // workflow does, which is why every job of a workflow without `needs:` still runs.
        return TriValue.True;
    }

    /// <summary>
    /// <c>failure()</c> is true when an ancestor failed. A skipped ancestor makes it unknown for the same
    /// documented reason as <see cref="SuccessOfNeeds"/>, and a cancelled one is unknown because the docs
    /// list cancellation for step-level <c>failure()</c> but not for jobs.
    /// </summary>
    protected TriValue FailureOfNeeds(IReadOnlyList<NeedOutcome> needs)
    {
        var hesitations = new List<UnknownReason>();
        foreach (var need in needs)
        {
            if (need.Outcome == JobOutcome.Failure)
            {
                return TriValue.True;
            }

            if (need.Outcome is JobOutcome.Skipped or JobOutcome.Unknown or JobOutcome.Cancelled)
            {
                hesitations.Add(new UnknownReason(
                    UnknownReasonKind.ScenarioDependent,
                    need.Outcome switch
                    {
                        JobOutcome.Skipped => $"'{need.JobId}' was skipped, and the documentation does not " +
                            "settle what failure() means after a skipped ancestor.",
                        JobOutcome.Cancelled => $"'{need.JobId}' was cancelled. failure() is documented to " +
                            "cover cancellation for steps, but not for jobs, so the job-level answer is not stated.",
                        _ => $"'{need.JobId}' has no settled outcome, so failure() cannot say whether anything failed.",
                    },
                    Scope.ConditionRange));
            }
        }

        return hesitations.Count > 0
            ? TriValue.Unknown(new UnknownReason(
                UnknownReasonKind.ScenarioDependent,
                "failure() needs to know whether an ancestor failed, and the scenario does not say for every one of them.",
                Scope.ConditionRange,
                hesitations))
            : TriValue.False;
    }

    /// <summary>
    /// <c>cancelled()</c>, with the scope mismatch recorded. The documentation asks about the workflow; the
    /// step-level implementation compares the job's status, and whether a sibling job's cancellation reaches
    /// this job is not stated anywhere — so a workflow-level cancellation answers unknown at both positions
    /// unless the scenario pins this very job.
    /// </summary>
    protected TriValue CancelledFor(JobOutcome ownOutcome, string ownName)
    {
        if (ownOutcome == JobOutcome.Cancelled)
        {
            return TriValue.True;
        }

        if (Scope.Scenario.WorkflowCancelled)
        {
            return TriValue.Unknown(new UnknownReason(
                UnknownReasonKind.ScenarioDependent,
                $"The scenario cancels the workflow, but cancelled() is documented as a question about the " +
                $"workflow while the runner answers it from the status of {ownName} itself; whether a " +
                "cancellation elsewhere reaches this job is not documented.",
                Scope.ConditionRange));
        }

        return TriValue.False;
    }

    private void Add(string contextName, GitHubValue? value, UnknownReason? reason)
    {
        if (value is not null)
        {
            _known[contextName] = value;
        }
        else if (reason is not null)
        {
            _unknown[contextName] = reason;
        }
    }

    private void AddStepContexts()
    {
        var job = Scope.Job;
        var hasMatrix = Scope.Combination is not null;

        if (hasMatrix)
        {
            _known["matrix"] = Scope.Combination!.ToContextValue();
            _known["strategy"] = BuildStrategy();
        }
        else
        {
            var reason = Scope.MatrixIsDynamic
                ? "This job's matrix dimension values are produced at run time, so no combination — and so no " +
                  "matrix.* value — is knowable here."
                : "This job declares no matrix, so matrix.* has nothing to read.";
            _unknown["matrix"] = new UnknownReason(UnknownReasonKind.ValueMissing, reason, Scope.ConditionRange);
            _unknown["strategy"] = new UnknownReason(
                UnknownReasonKind.ValueMissing,
                Scope.MatrixIsDynamic
                    ? "The strategy context mirrors the matrix, and this job's matrix is produced at run time."
                    : "The strategy context mirrors the matrix, and this job declares no matrix.",
                Scope.ConditionRange);
        }

        // The job context is the run's own status, container and step map; ActionLens never starts a job.
        _unknown["job"] = new UnknownReason(
            UnknownReasonKind.RuntimeContext,
            "The job context describes a job while it runs — its status, container and step map — and nothing " +
            "has run: ActionLens evaluates conditions, it does not execute steps.",
            Scope.ConditionRange);
        _unknown["steps"] = new UnknownReason(
            UnknownReasonKind.RuntimeContext,
            "The steps context holds the outcomes and outputs of steps that have already finished, and no step " +
            "of this job has run yet.",
            Scope.ConditionRange);
        _unknown["runner"] = new UnknownReason(
            UnknownReasonKind.RuntimeContext,
            "The runner context is the machine that took the job — its OS, architecture and tool cache paths — " +
            "which is not knowable until a runner exists.",
            Scope.ConditionRange);

        var env = BuildEnvironment();
        _known["env"] = env;
    }

    private GitHubValue BuildStrategy()
    {
        var strategy = Scope.Job.Strategy;
        var members = new Dictionary<string, GitHubValue>(StringComparer.Ordinal)
        {
            ["fail"] = GitHubValue.FromBoolean(strategy?.FailFast ?? true),
            ["matrix"] = Scope.Combination!.ToContextValue(),
        };

        members["max-parallel"] = strategy?.MaxParallel is { } parallel
            ? GitHubValue.FromNumber(parallel)
            : GitHubValue.FromString("auto");

        return GitHubValue.FromObject(members);
    }

    private GitHubValue? BuildNeeds(out UnknownReason? reason)
    {
        var needs = Scope.Needs;
        if (needs.Count == 0)
        {
            reason = null;
            return GitHubValue.EmptyObject;
        }

        foreach (var need in needs)
        {
            if (need.Outcome == JobOutcome.Unknown)
            {
                reason = new UnknownReason(
                    UnknownReasonKind.ScenarioDependent,
                    $"'{need.JobId}' is listed in needs but the scenario does not settle how it ends, so " +
                    $"needs.{need.JobId}.result and needs.{need.JobId}.outputs have no value.",
                    Scope.ConditionRange);
                return null;
            }
        }

        var map = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
        foreach (var need in needs)
        {
            var outputs = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
            foreach (var pair in need.Outputs)
            {
                outputs[pair.Key] = pair.Value;
            }

            map[need.JobId] = GitHubValue.FromObject(new Dictionary<string, GitHubValue>(StringComparer.Ordinal)
            {
                ["result"] = GitHubValue.FromString(JobOutcomeText.Of(need.Outcome)),
                ["outputs"] = GitHubValue.FromObject(outputs),
            });
        }

        reason = null;
        return GitHubValue.FromObject(map);
    }

    /// <summary>
    /// The workflow → job → step <c>env</c> chain, with each value resolved in the scope it is written in.
    /// A value that only a run can produce is left out and reported: keeping it would mean inventing a
    /// value, while dropping it silently would read as <c>null</c>, so the finding names the key.
    /// </summary>
    private GitHubValue BuildEnvironment()
    {
        var chain = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
        var findings = new List<Diagnostic>();
        var seed = new EnvSeedContext(this);
        var evaluator = new ExpressionEvaluator(seed);

        Append(Scope.Workflow.Env, "workflow");
        Append(Scope.Job.Env, $"job '{Scope.Job.Id}'");
        if (Scope.Step is { } step)
        {
            Append(step.Env, $"step {step.Index + 1} of job '{Scope.Job.Id}'");
        }

        Diagnostics = findings;
        return GitHubValue.FromObject(chain);

        void Append(IReadOnlyDictionary<string, WorkflowValue> entries, string owner)
        {
            foreach (var pair in entries)
            {
                var resolved = Resolve(evaluator, pair.Value);
                if (resolved.IsKnown)
                {
                    chain[pair.Key] = resolved.Required;
                    continue;
                }

                // The gap is named by the reason the value gave, so an env that reads a step output and an
                // env that illegally reads env do not come back with the same sentence.
                findings.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.ScenarioValueUnresolved,
                    Severity = DiagnosticSeverity.Info,
                    Stage = DiagnosticStage.Scenario,
                    Message = $"The value of '{pair.Key}' in the {owner} env: has no static value: " +
                        $"{resolved.Reason?.Message} A condition reading env.{pair.Key} therefore sees nothing, " +
                        "which is not the same as seeing an unknown.",
                    Range = pair.Value.Range.IsKnown ? pair.Value.Range : Scope.ConditionRange,
                    Scope = Scope.Job.Id,
                    Suggestion = "Supply the value the expression needs (an event payload, a matrix value) to see the condition resolve.",
                });
            }
        }
    }

    /// <summary>
    /// Reads one value written in YAML as an expression: literal text stays as it is, a
    /// <c>${{ }}</c> body is evaluated in this scope, and anything that cannot be decided comes back with
    /// the reason it could not be.
    /// </summary>
    internal static Evaluated Resolve(ExpressionEvaluator evaluator, WorkflowValue value)
    {
        var text = value.Text ?? string.Empty;
        if (!text.Contains("${{", StringComparison.Ordinal))
        {
            return Evaluated.Known(value.Value);
        }

        var parsed = ExpressionParser.ParseEmbedded(text);
        if (parsed.Root is null)
        {
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax,
                parsed.Diagnostics.Count > 0
                    ? parsed.Diagnostics[0].Message
                    : "The value holds no expression ActionLens can read.",
                value.Range));
        }

        return evaluator.Evaluate(parsed.Root);
    }

    private UnknownReason Missing(string contextName, string why) => new(
        UnknownReasonKind.ValueMissing,
        $"{why} {contextName} is legal in {PositionPhrase}, so this is a missing input rather than an illegal one.",
        Scope.ConditionRange);

    /// <summary>
    /// The scope an <c>env:</c> value is read in. GitHub does not allow <c>env</c> to reference
    /// <c>env</c>, and a definition cannot read a step that has not run, so those three are refused here
    /// even though the enclosing position can read two of them.
    /// </summary>
    private sealed class EnvSeedContext : IContextProvider
    {
        private static readonly HashSet<string> SeedContexts =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "github", "needs", "strategy", "matrix", "vars", "inputs",
            };

        private readonly EvaluationPosition _owner;

        internal EnvSeedContext(EvaluationPosition owner) => _owner = owner;

        public bool IsAvailable(string contextName) =>
            SeedContexts.Contains(contextName) && _owner.Contexts.Contains(contextName);

        public bool TryGetContext(string contextName, out GitHubValue context)
        {
            context = GitHubValue.Null;
            return IsAvailable(contextName) && _owner.TryGetContext(contextName, out context);
        }

        public UnknownReason ExplainContextUnknown(string contextName) => _owner.ExplainContextUnknown(contextName);

        public UnknownReason ExplainUnavailable(string symbolName) =>
            string.Equals(symbolName, "env", StringComparison.OrdinalIgnoreCase)
                ? new UnknownReason(
                    UnknownReasonKind.ContextNotAvailable,
                    "'env' cannot be read while an env: value is being defined — the definition is what " +
                    "produces it. It is available in " + _owner.PositionPhrase + ".",
                    _owner.Scope.ConditionRange)
                : _owner.ExplainUnavailable(symbolName);

        public bool IsFunctionAvailable(string functionName) => _owner.IsFunctionAvailable(functionName);

        public bool TryGetStatus(StatusFunction statusFunction, out TriValue value)
        {
            // A status function has no business inside an env: value; refusing it keeps the seed honest
            // rather than quietly answering from the scenario.
            value = TriValue.Unknown(new UnknownReason(
                UnknownReasonKind.ContextNotAvailable,
                $"'{statusFunction.ToString().ToLowerInvariant()}()' is not read while an env: value is defined.",
                _owner.Scope.ConditionRange));
            return false;
        }
    }
}
