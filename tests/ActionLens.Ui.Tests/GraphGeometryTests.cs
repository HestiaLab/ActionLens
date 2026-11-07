using System.Globalization;
using ActionLens.Core.Graph;
using ActionLens.Ui.Services;

namespace ActionLens.Ui.Tests;

/// <summary>
/// The pixel layer's product contract: the same layout always produces the same numbers, in every
/// culture, and an edge to an unplaced job draws nothing rather than a guess.
/// </summary>
public sealed class GraphGeometryTests
{
    private static readonly GraphCellSize Size = GraphCellSize.Default;

    [Fact]
    public void Build_EmptyGraph_IsPaddingOnly()
    {
        GraphPlacement placement = GraphGeometry.Build([], [], layerCount: 0, maxLayerWidth: 0, Size);

        Assert.Empty(placement.Nodes);
        Assert.Empty(placement.Edges);
        Assert.Equal(Size.Padding * 2, placement.Width);
        Assert.Equal(Size.Padding * 2, placement.Height);
    }

    [Fact]
    public void Build_SingleLayer_StacksSlotsVertically()
    {
        LayoutNode[] nodes =
        [
            new() { JobId = "a", Layer = 0, Slot = 0, Dependencies = [] },
            new() { JobId = "b", Layer = 0, Slot = 1, Dependencies = [] },
            new() { JobId = "c", Layer = 0, Slot = 2, Dependencies = [] },
        ];

        GraphPlacement placement = GraphGeometry.Build(nodes, [], layerCount: 1, maxLayerWidth: 3, Size);

        Assert.All(placement.Nodes, node => Assert.Equal(Size.Padding, node.Box.X));
        Assert.Equal(Size.Padding, placement.Nodes[0].Box.Y);
        Assert.Equal(Size.Padding + Size.RowStep, placement.Nodes[1].Box.Y);
        Assert.Equal(Size.Padding + (2 * Size.RowStep), placement.Nodes[2].Box.Y);
        Assert.Equal([3], placement.LayerWidths);
    }

    [Fact]
    public void Build_ShortLayer_IsCentredAgainstTheTallest()
    {
        LayoutNode[] nodes =
        [
            new() { JobId = "solo", Layer = 0, Slot = 0, Dependencies = [] },
            new() { JobId = "x", Layer = 1, Slot = 0, Dependencies = [] },
            new() { JobId = "y", Layer = 1, Slot = 1, Dependencies = [] },
            new() { JobId = "z", Layer = 1, Slot = 2, Dependencies = [] },
        ];

        GraphPlacement placement = GraphGeometry.Build(nodes, [], layerCount: 2, maxLayerWidth: 3, Size);

        PixelBox solo = Assert.Single(placement.Nodes, node => node.Node.JobId == "solo").Box;
        Assert.Equal(Size.Padding + Size.RowStep, solo.Y);
        Assert.Equal(Size.Padding + Size.ColumnStep, Assert.Single(placement.Nodes, node => node.Node.JobId == "x").Box.X);
    }

    [Fact]
    public void Build_SameInput_ProducesTheSamePicture()
    {
        LayoutNode[] nodes =
        [
            new() { JobId = "a", Layer = 0, Slot = 0, Dependencies = [] },
            new() { JobId = "b", Layer = 1, Slot = 0, Dependencies = [] },
        ];
        LayoutEdge[] edges = [new() { From = "a", To = "b" }];

        GraphPlacement first = GraphGeometry.Build(nodes, edges, layerCount: 2, maxLayerWidth: 1, Size);
        GraphPlacement second = GraphGeometry.Build(nodes, edges, layerCount: 2, maxLayerWidth: 1, Size);

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Nodes.Select(node => node.Box), second.Nodes.Select(node => node.Box));
        Assert.Equal(first.Edges.Select(edge => edge.Path), second.Edges.Select(edge => edge.Path));
        Assert.StartsWith("M ", Assert.Single(first.Edges).Path);
    }

    [Fact]
    public void Build_EdgeToUnplacedJob_DrawsNothing()
    {
        LayoutNode[] nodes = [new() { JobId = "a", Layer = 0, Slot = 0, Dependencies = [] }];
        LayoutEdge[] edges = [new() { From = "a", To = "blocked" }];

        GraphPlacement placement = GraphGeometry.Build(nodes, edges, layerCount: 1, maxLayerWidth: 1, Size);

        Assert.Equal(string.Empty, Assert.Single(placement.Edges).Path);
        Assert.Null(placement.BoxFor("blocked"));
        Assert.NotNull(placement.BoxFor("a"));
    }

    [Fact]
    public void Number_AndEdgePath_AreCultureInvariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", GraphGeometry.Number(1.5));
            string path = GraphGeometry.EdgePath(new PixelBox(0, 0.5, 176, 66), new PixelBox(234, 0.5, 176, 66));
            Assert.Contains("33.5", path);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void LayerWidthsOf_IgnoresLayersOutsideTheCount()
    {
        LayoutNode[] nodes =
        [
            new() { JobId = "a", Layer = 0, Slot = 0, Dependencies = [] },
            new() { JobId = "b", Layer = 5, Slot = 0, Dependencies = [] },
            new() { JobId = "c", Layer = -1, Slot = 0, Dependencies = [] },
        ];

        Assert.Equal([1], GraphGeometry.LayerWidthsOf(nodes, layerCount: 1));
    }
}