using System.Globalization;
using ActionLens.Core.Graph;

namespace ActionLens.Ui.Services;

/// <summary>A rectangle in canvas pixels, with the anchors the drawing needs.</summary>
/// <remarks>
/// Pixels never leave the app: <c>ActionLens.Core</c> hands out unit-less layers and slots, and this type
/// is where those become coordinates. Every number is formatted through <see cref="GraphGeometry.Number"/>
/// so a path is the same text in every culture.
/// </remarks>
public readonly record struct PixelBox(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Right => X + Width;

    public double Top => Y;

    public double Bottom => Y + Height;

    public double MidX => X + Width / 2d;

    public double MidY => Y + Height / 2d;
}

/// <summary>The cell size a canvas draws one job box at, plus the gutters between cells.</summary>
/// <param name="NodeWidth">Box width, wide enough for a job id and two status lines.</param>
/// <param name="NodeHeight">Box height.</param>
/// <param name="ColumnGap">Horizontal room between layers, which is where the edge arrows live.</param>
/// <param name="RowGap">Vertical room between the slots of one layer, and between band rows.</param>
/// <param name="Padding">Canvas edge kept clear so an arrowhead is never clipped.</param>
public sealed record GraphCellSize(double NodeWidth, double NodeHeight, double ColumnGap, double RowGap, double Padding)
{
    /// <summary>The size the job graph canvas draws at.</summary>
    /// <remarks>
    /// Sized so a two-layer workflow with five jobs in a layer fits a default Atlas panel without the
    /// reader having to scroll, and so the three text lines of a box stay inside it.
    /// </remarks>
    public static GraphCellSize Default { get; } = new(176, 66, 58, 22, 14);

    public double ColumnStep => NodeWidth + ColumnGap;

    public double RowStep => NodeHeight + RowGap;
}

/// <summary>A job box at its pixel position, with the layout node it came from.</summary>
public sealed record PlacedNode(LayoutNode Node, PixelBox Box);

/// <summary>One drawn edge: the two job ids and the SVG path that joins them.</summary>
public sealed record DrawnEdge(string From, string To, string Path);

/// <summary>The placed part of the graph, resolved to pixels.</summary>
public sealed class GraphPlacement
{
    internal GraphPlacement(
        GraphCellSize size,
        IReadOnlyList<PlacedNode> nodes,
        IReadOnlyList<DrawnEdge> edges,
        int[] layerWidths,
        double width,
        double height)
    {
        Size = size;
        Nodes = nodes;
        Edges = edges;
        LayerWidths = layerWidths;
        Width = width;
        Height = height;
    }

    public GraphCellSize Size { get; }

    /// <summary>Nodes in Core's draw order: by layer, then by slot.</summary>
    public IReadOnlyList<PlacedNode> Nodes { get; }

    public IReadOnlyList<DrawnEdge> Edges { get; }

    /// <summary>Jobs per layer, indexed by layer — the "row 2 of 5" an accessible name needs.</summary>
    public IReadOnlyList<int> LayerWidths { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>The box of a placed job, or null when the layout refused to place it.</summary>
    public PixelBox? BoxFor(string jobId)
    {
        foreach (PlacedNode placed in Nodes)
        {
            if (string.Equals(placed.Node.JobId, jobId, StringComparison.Ordinal))
            {
                return placed.Box;
            }
        }

        return null;
    }
}

/// <summary>
/// Layer and slot to pixels, and pixel pairs to edge paths.
/// </summary>
/// <remarks>
/// Pure and deterministic: the same node list always produces the same numbers, which is a product
/// requirement rather than a nicety — a reader comparing two canvases of one file must be looking at the
/// same picture. There is no crossing minimisation and no force layout here; Core's longest-path layering
/// and document-order slots are taken as given, and the only freedoms this class exercises are the pixel
/// size of a cell and the vertical centring of a short layer against the tallest one.
/// </remarks>
public static class GraphGeometry
{
    /// <summary>The minimum horizontal reach of an edge's control points, so a short hop still curves.</summary>
    private const double EdgeControlMinimum = 26;

    /// <summary>
    /// How far below a row of chips a closing loop dips before it comes back up. Public because the canvas
    /// needs it to size the lane: the loop is the lowest thing the band draws.
    /// </summary>
    public const double LoopDip = 30;

    /// <summary>Places every node of <paramref name="nodes"/> and joins them with edge paths.</summary>
    public static GraphPlacement Build(
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutEdge> edges,
        int layerCount,
        int maxLayerWidth,
        GraphCellSize size)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(size);

        int[] widths = LayerWidthsOf(nodes, layerCount);
        double tallest = maxLayerWidth > 0 ? (maxLayerWidth - 1) * size.RowStep : 0;

        PlacedNode[] placed = new PlacedNode[nodes.Count];
        for (int index = 0; index < nodes.Count; index++)
        {
            LayoutNode node = nodes[index];
            int layer = Math.Clamp(node.Layer, 0, Math.Max(layerCount - 1, 0));
            int slot = Math.Max(node.Slot, 0);

            // A short layer is centred against the tallest one so the picture reads left-to-right instead
            // of hanging everything from the top edge. Derived only from the counts, so it stays
            // deterministic, and it moves nothing between layers.
            double bandTop = size.Padding + (tallest - ((widths[layer] - 1) * size.RowStep)) / 2d;
            placed[index] = new PlacedNode(
                node,
                new PixelBox(
                    size.Padding + (layer * size.ColumnStep),
                    bandTop + (slot * size.RowStep),
                    size.NodeWidth,
                    size.NodeHeight));
        }

        DrawnEdge[] drawn = new DrawnEdge[edges.Count];
        for (int index = 0; index < edges.Count; index++)
        {
            LayoutEdge edge = edges[index];
            PixelBox? from = BoxOf(placed, edge.From);
            PixelBox? to = BoxOf(placed, edge.To);

            // An edge with an endpoint the layout refused to place gets no path: the band below the grid
            // draws it instead, where a reader can see that it has no position.
            drawn[index] = new DrawnEdge(edge.From, edge.To, from is null || to is null ? string.Empty : EdgePath(from.Value, to.Value));
        }

        double width = layerCount > 0
            ? (size.Padding * 2) + ((layerCount - 1) * size.ColumnStep) + size.NodeWidth
            : size.Padding * 2;
        double height = maxLayerWidth > 0
            ? (size.Padding * 2) + tallest + size.NodeHeight
            : size.Padding * 2;

        return new GraphPlacement(size, placed, drawn, widths, width, height);
    }

    /// <summary>
    /// Jobs per layer, indexed by layer. Core keeps this count on <see cref="LayeredLayout"/>, but a
    /// report carries the nodes rather than the array, so the canvas recount is the seam between them.
    /// </summary>
    public static int[] LayerWidthsOf(IReadOnlyList<LayoutNode> nodes, int layerCount)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        int[] widths = new int[Math.Max(layerCount, 0)];
        foreach (LayoutNode node in nodes)
        {
            if (node.Layer >= 0 && node.Layer < widths.Length)
            {
                widths[node.Layer]++;
            }
        }

        return widths;
    }

    /// <summary>The box a band chip occupies: the grid's column rhythm, its own rows below the grid.</summary>
    public static PixelBox ChipBox(int column, int row, double rowHeight, GraphCellSize size, double top)
    {
        ArgumentNullException.ThrowIfNull(size);

        return new PixelBox(
            size.Padding + (column * size.ColumnStep),
            top + (row * (rowHeight + size.RowGap)),
            size.NodeWidth,
            rowHeight);
    }

    /// <summary>An edge from the right edge of one box to the left edge of another.</summary>
    public static string EdgePath(PixelBox from, PixelBox to)
    {
        double x1 = from.Right;
        double y1 = from.MidY;
        double x2 = to.Left;
        double y2 = to.MidY;
        double reach = Math.Max(EdgeControlMinimum, (x2 - x1) / 2d);
        return $"M {Number(x1)} {Number(y1)} C {Number(x1 + reach)} {Number(y1)}, {Number(x2 - reach)} {Number(y2)}, {Number(x2)} {Number(y2)}";
    }

    /// <summary>
    /// The edge that closes a cycle: out of the last chip's bottom, under the row, back into the first
    /// chip's bottom. It runs the other way along the lane on purpose, so it cannot be read as a
    /// dependency that flows left to right.
    /// </summary>
    public static string LoopPath(PixelBox last, PixelBox first)
    {
        double x1 = last.MidX;
        double y1 = last.Bottom;
        double x2 = first.MidX;
        double y2 = first.Bottom;
        double dip = Math.Max(y1, y2) + LoopDip;
        return $"M {Number(x1)} {Number(y1)} C {Number(x1)} {Number(dip)}, {Number(x2)} {Number(dip)}, {Number(x2)} {Number(y2)}";
    }

    /// <summary>A short arrow leaving a placed job towards a dependent the layout could not place.</summary>
    public static string DanglingPath(PixelBox from, GraphCellSize size)
    {
        ArgumentNullException.ThrowIfNull(size);

        double x1 = from.Right;
        double y1 = from.Bottom - (size.NodeHeight / 4d);
        double x2 = x1 + (size.ColumnGap * 0.55);
        return $"M {Number(x1)} {Number(y1)} C {Number(x1 + 12)} {Number(y1)}, {Number(x2 - 12)} {Number(y1)}, {Number(x2)} {Number(y1)}";
    }

    /// <summary>Culture-stable number text for an SVG attribute or path.</summary>
    public static string Number(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A job id shortened to fit a node box; the full id stays in the accessible name.</summary>
    public static string Ellipsis(string text, int maximum)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length <= maximum ? text : string.Concat(text.AsSpan(0, Math.Max(maximum - 1, 1)), "…");
    }

    private static PixelBox? BoxOf(PlacedNode[] nodes, string jobId)
    {
        foreach (PlacedNode placed in nodes)
        {
            if (string.Equals(placed.Node.JobId, jobId, StringComparison.Ordinal))
            {
                return placed.Box;
            }
        }

        return null;
    }
}
