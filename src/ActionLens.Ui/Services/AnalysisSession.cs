using ActionLens.Core;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Reporting;
using ActionLens.Core.Scenarios;
using ActionLens.Core.Text;

namespace ActionLens.Ui.Services;

/// <summary>Which editor surface a jump should land in.</summary>
public enum EditorTarget
{
    /// <summary>The workflow YAML editor.</summary>
    Workflow,

    /// <summary>The event payload editor.</summary>
    Payload,
}

/// <summary>
/// A request to move an editor to the position a finding points at.
/// </summary>
/// <param name="Target">The editor that holds the text the range belongs to.</param>
/// <param name="Range">The 1-based range from the report; <see cref="SourceRange.IsKnown"/> decides whether it can be honoured.</param>
/// <param name="Diagnostic">The finding the reader clicked, for the announcement and the severity styling.</param>
/// <param name="Token">Increments on every request so a repeat click on the same finding still moves the caret.</param>
public sealed record DiagnosticJump(EditorTarget Target, SourceRange Range, Diagnostic? Diagnostic, long Token);

/// <summary>
/// The app's one mutable state: what the two editors hold, what the reader asked the engine about, and
/// what the engine answered.
/// </summary>
/// <remarks>
/// Text lives here rather than in an editor component because Atlas unloads inactive panel content by
/// default (<c>WorkspaceContentLifetimeOptions</c>): a component that owned the text would lose it when
/// its tab is de-selected. The session is the single writer, the editors are views of it, and every edit
/// re-queues one analysis.
/// <para>
/// Analysis is deliberately not run inside a render pass — <see cref="WorkflowAnalyzer"/> never throws by
/// contract, but a defect there must degrade to a finding rather than take the shell down, so
/// <see cref="AnalyzeNow"/> guards the call.
/// </para>
/// </remarks>
public sealed class AnalysisSession : IDisposable
{
    /// <summary>How long typing is coalesced for. Long enough to cover a burst of keystrokes, short enough to feel live.</summary>
    public const int DebounceMilliseconds = 250;

    private CancellationTokenSource? _debounce;
    private long _jumpToken;

    /// <summary>Raised whenever the state a panel reads changed, including at the start and end of an analysis.</summary>
    public event Action? Changed;

    /// <summary>Raised when a reader activates a finding; the shell owns the editors, so the shell routes it.</summary>
    public event Action<DiagnosticJump>? JumpRequested;

    /// <summary>The workflow text the editor holds.</summary>
    public string WorkflowText { get; private set; } = string.Empty;

    /// <summary>The event payload text the editor holds.</summary>
    public string PayloadText { get; private set; } = string.Empty;

    /// <summary>The file name reported to the engine, so findings and the report name the same file.</summary>
    public string FileName { get; private set; } = "workflow.yml";

    /// <summary>The event the trigger verdict answers for; null lets the payload's own <c>event_name</c> decide.</summary>
    public string? TriggerEvent { get; private set; }

    /// <summary>The imagined run the job states are derived from.</summary>
    public Scenario Scenario { get; private set; } = Scenario.Default;

    /// <summary>The last completed report, or null before the first analysis finished.</summary>
    public AnalysisReport? Report { get; private set; }

    /// <summary>True between a scheduled analysis and its completion; the status strip shows it as pending.</summary>
    public bool IsPending { get; private set; }

    /// <summary>Set only if the engine threw, which is an ActionLens defect rather than a workflow problem.</summary>
    public string? LastEngineFailure { get; private set; }

    /// <summary>
    /// The job the reader picked, whether from the graph or from the inspector list.
    /// </summary>
    /// <remarks>
    /// Selection is session state rather than component state for the same reason the editor text is: the
    /// panels live inside Atlas content routes and may be unloaded when their tab is not active, so a
    /// selection held by the canvas would vanish the moment the reader looked at the inspector. One
    /// property, two readers — the canvas sets it on <c>Enter</c> and the inspector highlights it.
    /// </remarks>
    public string? SelectedJobId { get; private set; }

    /// <summary>
    /// Selects a job for the inspector, or clears the selection when <paramref name="jobId"/> is null or
    /// names a job the current report does not have.
    /// </summary>
    public void SelectJob(string? jobId)
    {
        string? wanted = string.IsNullOrEmpty(jobId) ? null : jobId;
        if (wanted is not null && Report?.Jobs.Any(job => string.Equals(job.JobId, wanted, StringComparison.Ordinal)) != true)
        {
            return;
        }

        if (string.Equals(SelectedJobId, wanted, StringComparison.Ordinal))
        {
            return;
        }

        SelectedJobId = wanted;
        NotifyChanged();
    }

    /// <summary>
    /// Called by an editor when its text changes. <paramref name="target"/> is which editor spoke; the
    /// session becomes the owner of the text and re-queues the analysis.
    /// </summary>
    public void ReplaceText(EditorTarget target, string text)
    {
        string current = target == EditorTarget.Workflow ? WorkflowText : PayloadText;
        if (string.Equals(current, text, StringComparison.Ordinal))
        {
            return;
        }

        if (target == EditorTarget.Workflow)
        {
            WorkflowText = text;
        }
        else
        {
            PayloadText = text;
        }

        ScheduleAnalysis();
    }

    /// <summary>Selects the event the trigger verdict answers for, and analyses at once: a dropdown change is not typing.</summary>
    public void SelectTriggerEvent(string? eventName)
    {
        if (string.Equals(TriggerEvent, eventName, StringComparison.Ordinal))
        {
            return;
        }

        TriggerEvent = eventName;
        AnalyzeNow();
    }

    /// <summary>Switches the imagined run and re-answers every job for it.</summary>
    public void SelectScenario(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Scenario = scenario;
        AnalyzeNow();
    }

    /// <summary>
    /// Puts a bundled workflow and a matching payload into the editors. Used for the first paint, so a
    /// first visit reads a real conclusion instead of an empty panel.
    /// </summary>
    public void LoadExample(WorkflowExample workflow, EventPayloadExample? payload, string? triggerEvent)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        CancelPending();
        FileName = workflow.FileName;
        WorkflowText = workflow.Yaml;
        PayloadText = payload?.Json ?? string.Empty;
        TriggerEvent = triggerEvent;
        Scenario = Scenario.Default;
        AnalyzeNow();
    }

    /// <summary>Asks the shell to move an editor to a finding's position. A finding without a position is still announced.</summary>
    public void RequestJump(EditorTarget target, SourceRange range, Diagnostic? diagnostic)
    {
        JumpRequested?.Invoke(new DiagnosticJump(target, range, diagnostic, Interlocked.Increment(ref _jumpToken)));
    }

    /// <summary>
    /// The events a reader can ask a verdict about: what the file declares, plus the payload's own event
    /// when the declaration does not list it.
    /// </summary>
    public IReadOnlyList<string> SelectableEvents()
    {
        if (Report is not { } report)
        {
            return [];
        }

        List<string> events = [.. report.Trigger.Declared.Select(trigger => trigger.Name)];
        string? payloadEvent = report.Event.EventName;
        if (!string.IsNullOrEmpty(payloadEvent) && !events.Contains(payloadEvent, StringComparer.Ordinal))
        {
            events.Add(payloadEvent);
        }

        return events;
    }

    /// <summary>
    /// The scenarios on offer: the default run, then one where each job in document order fails, then a
    /// cancelled run. Built from the report so the list can never name a job the file does not have.
    /// </summary>
    public IReadOnlyList<ScenarioOption> SelectableScenarios()
    {
        if (Report is not { } report)
        {
            return [];
        }

        List<ScenarioOption> options = [new("Every job succeeds", Scenario.Default)];
        foreach (JobReport job in report.Jobs)
        {
            options.Add(new($"{job.JobId} fails", Scenario.Default.WithJobFailed(job.JobId)));
            options.Add(new($"{job.JobId} is skipped", Scenario.Default.WithJobSkipped(job.JobId)));
        }

        options.Add(new("The run was cancelled", Scenario.Default.WithWorkflowCancelled()));
        return options;
    }

    /// <summary>
    /// Seeds the first analysis from the bundled examples, so a first visit reads a real conclusion rather
    /// than an empty editor. Does nothing once a report exists, so a reload of the shell cannot overwrite
    /// what the reader typed.
    /// </summary>
    public void LoadInitialExample()
    {
        if (Report is not null || WorkflowText.Length > 0)
        {
            return;
        }

        LoadExample(WorkflowExamples.DefaultWorkflow, WorkflowExamples.PayloadFor(null), triggerEvent: null);
    }

    /// <summary>Cancels a queued analysis and disposes the session's only resource. Panels do not need it any more once they are gone.</summary>
    public void Dispose() => CancelPending();

    private void ScheduleAnalysis()
    {
        CancelPending();
        CancellationTokenSource source = new();
        _debounce = source;
        IsPending = true;
        NotifyChanged();
        _ = RunAfterDelayAsync(source.Token);
    }

    private async Task RunAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        AnalyzeNow();
    }

    private void CancelPending()
    {
        CancellationTokenSource? source = _debounce;
        _debounce = null;
        if (source is null)
        {
            return;
        }

        source.Cancel();
        source.Dispose();
        IsPending = false;
    }

    private void AnalyzeNow()
    {
        CancelPending();
        AnalysisReport report;
        try
        {
            report = WorkflowAnalyzer.Analyze(
                WorkflowText,
                FileName,
                PayloadText.Length > 0 ? PayloadText : null,
                Scenario,
                TriggerEvent);
            LastEngineFailure = null;
        }
        catch (Exception exception)
        {
            // WorkflowAnalyzer is contracted never to throw. If it does, the shell keeps the previous
            // report and says so instead of showing a blank panel or an unhandled .NET error.
            LastEngineFailure = $"{exception.GetType().Name}: {exception.Message}";
            NotifyChanged();
            return;
        }

        Report = report;

        // A selection the new file does not have would leave the inspector highlighting nothing while the
        // canvas claims a job is picked, so the selection is dropped rather than quietly re-pointed.
        if (SelectedJobId is { Length: > 0 } picked
            && !report.Jobs.Any(job => string.Equals(job.JobId, picked, StringComparison.Ordinal)))
        {
            SelectedJobId = null;
        }

        // A selection the file does not declare cannot be answered for, so it falls back to whatever the
        // payload states rather than holding a verdict the reader did not ask for.
        if (TriggerEvent is { Length: > 0 } selected
            && !report.Trigger.Declared.Any(trigger => string.Equals(trigger.Name, selected, StringComparison.Ordinal))
            && !string.Equals(report.Event.EventName, selected, StringComparison.Ordinal))
        {
            TriggerEvent = report.Event.EventName;
        }

        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}

/// <summary>One entry in the scenario dropdown.</summary>
/// <param name="Label">What the reader sees.</param>
/// <param name="Scenario">The scenario it selects.</param>
public sealed record ScenarioOption(string Label, Scenario Scenario);
