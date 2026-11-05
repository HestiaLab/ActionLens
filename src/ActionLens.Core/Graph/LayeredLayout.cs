namespace ActionLens.Core.Graph;

/// <summary>
/// A job's position in the layout grid. <see cref="Layer"/> is the horizontal axis (how deep into the
/// run the job is), <see cref="Slot"/> the vertical one (its rank among the jobs of that layer).
/// Both are plain counts, never pixels: the SVG component multiplies them by whatever cell size fits
/// the panel it is drawn in.
/// </summary>
public sealed record LayoutNode
{
    public required string JobId { get; init; }

    /// <summary>Zero-based; 1 + the deepest layer among this job's dependencies, 0 when it needs nothing.</summary>
    public required int Layer { get; init; }

    /// <summary>Zero-based rank inside <see cref="Layer"/>, assigned in document order of the jobs.</summary>
    public required int Slot { get; init; }

    /// <summary>The jobs this one needs, in written order — the edges that arrive at this node.</summary>
    public required IReadOnlyList<string> Dependencies { get; init; }

    public override string ToString() => $"{JobId} L{Layer} S{Slot}";
}

/// <summary>
/// An edge in draw order: <see cref="From"/> sits one or more layers to the left of
/// <see cref="To"/>, i.e. <see cref="From"/> has to finish first. That is the reverse of how
/// <see cref="JobGraphEdge"/> is written, because the author's <c>needs</c> reads backwards on a
/// left-to-right chart.
/// </summary>
public sealed record LayoutEdge
{
    public required string From { get; init; }

    public required string To { get; init; }

    public override string ToString() => $"{From} -> {To}";
}

/// <summary>
/// The layered layout of a <see cref="JobGraph"/>: longest-path layers, document-order slots. Built
/// once per graph and read by the UI; the numbers it exposes are all a renderer needs to place a node
/// without asking the graph anything else.
/// </summary>
/// <remarks>
/// Layering uses the longest path, not a breadth-first depth, because a job has to sit after the
/// <em>last</em> of its dependencies: with <c>a -&gt; b -&gt; c -&gt; d</c> plus a direct
/// <c>a -&gt; d</c>, a BFS depth would put <c>d</c> in layer 1, right next to a job it cannot start
/// before. An arrow then crosses a node that runs later, and the chart stops being a schedule.
/// </remarks>
public sealed class LayeredLayout
{
    private LayeredLayout(LayoutNode[] nodes, LayoutEdge[] edges, int[] layerWidths, string[] excludedJobs)
    {
        Nodes = nodes;
        Edges = edges;
        LayerWidths = layerWidths;
        ExcludedJobs = excludedJobs;
        LayerCount = layerWidths.Length;
        MaxLayerWidth = layerWidths.Length == 0 ? 0 : layerWidths.Max();
    }

    /// <summary>Layered jobs, ordered by layer and then by slot — the order to draw them in.</summary>
    public IReadOnlyList<LayoutNode> Nodes { get; }

    /// <summary>Edges between the layered jobs, in draw order, grouped by the job they arrive at.</summary>
    public IReadOnlyList<LayoutEdge> Edges { get; }

    /// <summary>How many jobs need no further dependency to be placed: the length of the longest path plus one.</summary>
    public int LayerCount { get; }

    /// <summary>Layer height in slots, indexed by layer. Empty for a graph with nothing to lay out.</summary>
    public IReadOnlyList<int> LayerWidths { get; }

    /// <summary>Widest layer, i.e. the vertical span the drawing needs.</summary>
    public int MaxLayerWidth { get; }

    public int NodeCount => Nodes.Count;

    /// <summary>
    /// Jobs the layout refuses to place: those inside a cycle and those waiting behind one, in
    /// document order. Any position here would be invented, so the list is handed to the caller with
    /// the cycle finding that explains it.
    /// </summary>
    public IReadOnlyList<string> ExcludedJobs { get; }

    /// <summary>Layer and slot for a job id, or null when the job is in <see cref="ExcludedJobs"/>.</summary>
    public LayoutNode? NodeFor(string jobId) =>
        Nodes.FirstOrDefault(node => string.Equals(node.JobId, jobId, StringComparison.Ordinal));

    /// <summary>Computes the grid. Only jobs the graph could order get a position; see <see cref="ExcludedJobs"/>.</summary>
    public static LayeredLayout Build(JobGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // TopologicalOrder guarantees every dependency of a laid-out job was assigned before it, so
        // one forward pass is enough — no relaxation loop, no recursion to overflow on a deep chain.
        var layers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var jobId in graph.TopologicalOrder)
        {
            var layer = 0;
            foreach (var dependency in graph.DependenciesOf(jobId))
            {
                if (layers.TryGetValue(dependency, out var depth) && depth + 1 > layer)
                {
                    layer = depth + 1;
                }
            }

            layers[jobId] = layer;
        }

        var widths = new int[layers.Count == 0 ? 0 : layers.Values.Max() + 1];
        var byLayer = new List<string>[widths.Length];
        for (var layer = 0; layer < byLayer.Length; layer++)
        {
            byLayer[layer] = [];
        }

        // Walking document order means each bucket is already slot-ordered, so the layout of the same
        // file never depends on how the dictionary happened to be built.
        foreach (var jobId in graph.JobIds)
        {
            if (layers.TryGetValue(jobId, out var layer))
            {
                widths[layer]++;
                byLayer[layer].Add(jobId);
            }
        }

        var nodes = new List<LayoutNode>(layers.Count);
        var edges = new List<LayoutEdge>(graph.Edges.Count);
        for (var layer = 0; layer < byLayer.Length; layer++)
        {
            for (var slot = 0; slot < byLayer[layer].Count; slot++)
            {
                var jobId = byLayer[layer][slot];
                var dependencies = graph.DependenciesOf(jobId);
                nodes.Add(new LayoutNode
                {
                    JobId = jobId,
                    Layer = layer,
                    Slot = slot,
                    Dependencies = dependencies,
                });

                foreach (var dependency in dependencies)
                {
                    edges.Add(new LayoutEdge { From = dependency, To = jobId });
                }
            }
        }

        return new LayeredLayout([.. nodes], [.. edges], widths, [.. graph.BlockedJobs]);
    }

    public override string ToString() => $"{NodeCount} nodes over {LayerCount} layers, {ExcludedJobs.Count} excluded";
}
