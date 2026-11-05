using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Graph;

/// <summary>
/// Turns a workflow's jobs into a graph, and every illegal <c>needs</c> into a finding that says
/// where it is written. Internal, so the only entry point callers see is
/// <see cref="JobGraph.Build(WorkflowDocument)"/> and a graph can never be obtained without the
/// diagnostics that qualify it.
/// </summary>
/// <remarks>
/// Every traversal iterates jobs in document order and each job's <c>needs</c> in written order, and
/// no result is read out of a hash set. Two runs over the same YAML therefore produce byte-identical
/// graphs, findings, layers and slots — which is what the golden files and a diff view depend on.
/// </remarks>
internal sealed class JobGraphAnalyzer
{
    private const string NeedsHelpLink =
        "https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idneeds";

    // Colours for the depth-first walk in FindCycles.
    private const byte Unvisited = 0;
    private const byte OnPath = 1;
    private const byte Done = 2;

    private readonly List<Diagnostic> _diagnostics = [];
    private readonly List<JobDefinition> _jobs = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string[]> _dependencyNames = [];
    private readonly List<int[]> _dependencyIndexes = [];

    public static JobGraphAnalysisResult Analyze(IReadOnlyList<JobDefinition> jobs)
    {
        var analyzer = new JobGraphAnalyzer();
        return analyzer.Run(jobs);
    }

    private JobGraphAnalysisResult Run(IReadOnlyList<JobDefinition> jobs)
    {
        CollectJobs(jobs);
        var dependentIndexes = LinkDependents();
        var cycles = FindCycles(out var inCycle);
        var (order, blocked) = SortAcyclic(dependentIndexes);

        var jobIds = _jobs.Select(job => job.Id).ToArray();
        var edges = new List<JobGraphEdge>();
        for (var i = 0; i < jobIds.Length; i++)
        {
            edges.AddRange(_dependencyNames[i].Select(need => new JobGraphEdge { Job = jobIds[i], Needs = need }));
        }

        var graph = new JobGraph(
            jobIds,
            [.. _dependencyNames],
            dependentIndexes.Select(dependents => dependents.Select(i => jobIds[i]).ToArray()).ToArray(),
            [.. edges],
            [.. order.Select(i => jobIds[i])],
            [.. blocked.Select(i => jobIds[i])],
            cycles.Select(path => path.Select(i => jobIds[i]).ToArray()).ToArray(),
            inCycle);

        // The layout is part of the result rather than an afterthought so that the picture and the
        // findings that qualify it always travel together.
        return new JobGraphAnalysisResult
        {
            Graph = graph,
            Layout = graph.ToLayout(),
            Diagnostics = [.. _diagnostics],
        };
    }

    /// <summary>
    /// Nodes in document order. A repeated id keeps its first definition: duplicate job keys are
    /// rejected by the schema stage, and if one ever reaches here a second node would split every
    /// later lookup in two.
    /// </summary>
    private void CollectJobs(IReadOnlyList<JobDefinition> jobs)
    {
        foreach (var job in jobs)
        {
            if (string.IsNullOrEmpty(job.Id) || !_index.TryAdd(job.Id, _jobs.Count))
            {
                continue;
            }

            _jobs.Add(job);
            _dependencyNames.Add([]);
            _dependencyIndexes.Add([]);
        }
    }

    /// <summary>
    /// Reads each job's <c>needs</c> in written order, dropping unknown ids (GRPH001) and repeats
    /// (a warning) and keeping everything else — including a job's reference to itself, so that a
    /// self-cycle falls out of the cycle walk instead of needing a second rule.
    /// </summary>
    /// <returns>The reverse edges: for each job index, the indexes of the jobs that need it.</returns>
    private int[][] LinkDependents()
    {
        var dependents = new List<List<int>>(_jobs.Count);
        for (var i = 0; i < _jobs.Count; i++)
        {
            dependents.Add([]);
        }

        for (var i = 0; i < _jobs.Count; i++)
        {
            var job = _jobs[i];
            var names = new List<string>();
            var targets = new List<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in job.Needs)
            {
                // An empty entry is the schema stage's finding to make; there is no id to resolve here.
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!seen.Add(name))
                {
                    // VAL005 rather than a new GRPH number: the graph codes are all claimed by an
                    // existing meaning, and DiagnosticCodes is out of reach for this task.
                    Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Warning,
                        $"'{job.Id}' lists '{name}' twice in its needs; the second entry changes nothing.",
                        job,
                        "Keep one copy — GitHub ignores the rest anyway.");
                    continue;
                }

                if (!_index.TryGetValue(name, out var target))
                {
                    Add(DiagnosticCodes.NeedsUnknownJob, DiagnosticSeverity.Error,
                        $"'{job.Id}' needs '{name}', but this workflow has no job with that id.",
                        job,
                        SuggestJob(name) ?? "Name a job defined in this workflow, or drop the entry.");
                    continue;
                }

                names.Add(name);
                targets.Add(target);
                dependents[target].Add(i);
            }

            _dependencyNames[i] = [.. names];
            _dependencyIndexes[i] = [.. targets];
        }

        // Filled while walking the jobs in document order, so the reverse edges need no sort to be
        // deterministic.
        return [.. dependents.Select(list => list.ToArray())];
    }

    /// <summary>
    /// Colours the graph depth-first, starting from each job in document order. A dependency already
    /// on the current path closes a cycle, and the slice of the path from that job to the top of the
    /// stack is the cycle — so the finding names <c>build -&gt; deploy -&gt; build</c> instead of only
    /// saying something is circular. Each cycle is reported once, by the edge that closed it, in
    /// discovery order; which cycle counts as first is that deterministic walk, not a preference.
    /// </summary>
    /// <param name="inCycle">Per job index, whether it takes part in at least one cycle.</param>
    private List<int[]> FindCycles(out bool[] inCycle)
    {
        inCycle = new bool[_jobs.Count];
        var cycles = new List<int[]>();
        var state = new byte[_jobs.Count];
        var path = new List<int>();
        var stack = new Stack<(int Job, int NextDependency)>();

        for (var start = 0; start < state.Length; start++)
        {
            if (state[start] != Unvisited)
            {
                continue;
            }

            state[start] = OnPath;
            path.Add(start);
            stack.Push((start, 0));

            while (stack.Count > 0)
            {
                var (job, next) = stack.Pop();
                var targets = _dependencyIndexes[job];

                if (next >= targets.Length)
                {
                    state[job] = Done;
                    path.RemoveAt(path.Count - 1);
                    continue;
                }

                stack.Push((job, next + 1));
                var target = targets[next];

                if (state[target] == Unvisited)
                {
                    state[target] = OnPath;
                    path.Add(target);
                    stack.Push((target, 0));
                    continue;
                }

                if (state[target] != OnPath)
                {
                    continue;
                }

                var from = path.IndexOf(target);
                var cycle = path.GetRange(from, path.Count - from);
                cycle.Add(target);
                cycles.Add([.. cycle]);

                // The job that wrote the closing edge is the one the author has to edit, and the one
                // whose needs: range the UI should jump to.
                var closer = _jobs[cycle[^2]];
                Add(DiagnosticCodes.NeedsCycle, DiagnosticSeverity.Error,
                    $"'{closer.Id}' needs '{_jobs[target].Id}', which closes a needs cycle: " +
                    $"{string.Join(" -> ", cycle.Select(i => _jobs[i].Id))}.",
                    closer,
                    "Break the loop by dropping one needs entry on this path, or splitting the jobs it links.");

                foreach (var member in cycle[..^1])
                {
                    inCycle[member] = true;
                }
            }
        }

        return cycles;
    }

    /// <summary>
    /// Kahn's algorithm taking the ready job with the lowest document position each time, so a graph
    /// with several legal orders still yields one answer. Jobs that never become ready — inside a
    /// cycle, or waiting behind one — come back as blocked instead of being squeezed into the order,
    /// because inventing a position for them is the guess Gate 1 exists to prevent.
    /// </summary>
    private (List<int> Order, List<int> Blocked) SortAcyclic(int[][] dependents)
    {
        var waiting = new int[_jobs.Count];
        for (var i = 0; i < _jobs.Count; i++)
        {
            waiting[i] = _dependencyIndexes[i].Length;
        }

        var order = new List<int>(_jobs.Count);
        var placed = new bool[_jobs.Count];
        while (true)
        {
            // The lowest document position among the jobs that are ready. Scanning instead of sorting
            // keeps this O(jobs^2) with a workflow's few hundred jobs, and it is obvious that ties are
            // broken by position rather than by whatever order the reverse edges happened to arrive in.
            var next = -1;
            for (var i = 0; i < waiting.Length; i++)
            {
                if (waiting[i] == 0 && !placed[i])
                {
                    next = i;
                    break;
                }
            }

            if (next < 0)
            {
                break;
            }

            placed[next] = true;
            order.Add(next);

            foreach (var dependent in dependents[next])
            {
                waiting[dependent]--;
            }
        }

        var blocked = new List<int>();
        for (var i = 0; i < _jobs.Count; i++)
        {
            if (!placed[i])
            {
                blocked.Add(i);
            }
        }

        return (order, blocked);
    }

    private void Add(string code, DiagnosticSeverity severity, string message, JobDefinition job, string suggestion)
    {
        _diagnostics.Add(new Diagnostic
        {
            Code = code,
            Severity = severity,
            Stage = DiagnosticStage.Graph,
            Message = message,
            // Points the UI at the needs list that caused the finding; jobs read through a merge key,
            // or built by hand in a test, may carry no position at all, so the job key is the fallback.
            Range = NeedsRange(job),
            Suggestion = suggestion,
            HelpLink = NeedsHelpLink,
            Scope = job.Id,
        });
    }

    private static SourceRange NeedsRange(JobDefinition job)
    {
        var range = job.Sources.Get("needs");
        return range.IsKnown ? range : job.KeyRange;
    }

    /// <summary>
    /// The one job id that looks like a mis-typed version of <paramref name="name"/>, or null. The
    /// candidate has to be strictly closest: with two equally plausible readings, picking one is a
    /// guess, and the finding is better for saying nothing.
    /// </summary>
    private string? SuggestJob(string name)
    {
        var budget = name.Length <= 4 ? 1 : 2;
        var best = int.MaxValue;
        var tied = false;
        string? suggestion = null;

        foreach (var job in _jobs)
        {
            // A distance of 0 is the plain capitalisation typo and the best answer there is, so only
            // the budget filters candidates out; identical ids never reach this method.
            var distance = Distance(name, job.Id);
            if (distance > budget)
            {
                continue;
            }

            if (distance < best)
            {
                best = distance;
                suggestion = job.Id;
                tied = false;
            }
            else if (distance == best)
            {
                tied = true;
            }
        }

        return tied || suggestion is null
            ? null
            : $"Did you mean '{suggestion}'?";

        static int Distance(string left, string right)
        {
            // Case-insensitive on purpose: a capitalisation slip is the friendliest typo to catch, and
            // the message still quotes the id with its real spelling.
            var a = left.ToLowerInvariant();
            var b = right.ToLowerInvariant();
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
