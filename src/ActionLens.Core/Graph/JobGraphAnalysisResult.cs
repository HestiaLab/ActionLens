using ActionLens.Core.Diagnostics;

namespace ActionLens.Core.Graph;

/// <summary>
/// What the graph stage produced: the graph itself and the findings that say why parts of it cannot
/// be trusted. There is no way to get a <see cref="JobGraph"/> without its diagnostics, so a caller
/// cannot accidentally show a layout while hiding the cycle behind it.
/// </summary>
public sealed record JobGraphAnalysisResult
{
    public required JobGraph Graph { get; init; }

    /// <summary>
    /// The grid the UI draws, computed once when the result is built so that a caller cannot show the
    /// chart without also holding <see cref="Diagnostics"/> — the cycle findings are what explain any
    /// job missing from it.
    /// </summary>
    public required LayeredLayout Layout { get; init; }

    /// <summary>
    /// Findings in a stable order: first the <c>needs</c> problems job by job in document order (and
    /// within a job in written order), then one entry per cycle in discovery order.
    /// </summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>
    /// True when the graph is both cycle-free and error-free, i.e. Gate 1's "the DAG is trustworthy".
    /// Warnings (a duplicated <c>needs</c> entry) do not break it.
    /// </summary>
    public bool IsTrustworthy =>
        Graph.IsAcyclic &&
        !Diagnostics.Any(static diagnostic =>
            diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical);

    public override string ToString() =>
        $"{Graph.JobCount} jobs, {Graph.Edges.Count} edges, {Graph.Cycles.Count} cycles, {Diagnostics.Count} findings";
}
