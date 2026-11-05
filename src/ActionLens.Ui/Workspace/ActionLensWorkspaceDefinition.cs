using Atlas.Core;
using Atlas.Core.Definitions;
using Atlas.Core.Items;
using Atlas.Core.Layout;
using Atlas.Core.Placement;

namespace ActionLens.Ui.Workspace;

/// <summary>
/// The content kinds ActionLens registers with the Atlas host.
/// </summary>
/// <remarks>
/// These strings are part of the persisted layout contract: Atlas stores a
/// <see cref="ContentReference"/> per open tab, so a rename silently degrades an old layout to a
/// placeholder. Treat them like the diagnostic codes — added to, never reused or renamed.
/// </remarks>
public static class ActionLensContentKinds
{
    public const string Workflow = "actionlens-workflow";
    public const string Payload = "actionlens-payload";
    public const string Canvas = "actionlens-canvas";
    public const string Inspector = "actionlens-inspector";
    public const string Diagnostics = "actionlens-diagnostics";
    public const string Summary = "actionlens-summary";

    /// <summary>Every kind the shell can render, for a restore that must know what is registered.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Workflow,
        Payload,
        Canvas,
        Inspector,
        Diagnostics,
        Summary,
    };
}

/// <summary>Stable identifiers for the shell's items and groups.</summary>
public static class ActionLensItemIds
{
    public const string Workflow = "actionlens-item-workflow";
    public const string Payload = "actionlens-item-payload";
    public const string Canvas = "actionlens-item-canvas";
    public const string Inspector = "actionlens-item-inspector";
    public const string Diagnostics = "actionlens-item-diagnostics";
    public const string Summary = "actionlens-item-summary";

    public const string EditorGroup = "actionlens-editor";
    public const string CanvasGroup = "actionlens-canvas-group";
    public const string InspectorGroup = "actionlens-inspector-group";
    public const string DiagnosticsGroup = "actionlens-diagnostics-group";
    public const string SummaryGroup = "actionlens-summary-group";
}

/// <summary>
/// The programmatic workspace definition for ActionLens: a document group holding the two editors, a
/// centre tool group for the DAG canvas, a right tool group for the inspector, and a bottom dock split
/// between the diagnostics list and the run summary.
/// </summary>
/// <remarks>
/// Built programmatically rather than declaratively because the shell drives panel state through
/// <see cref="IAtlasWorkspace.ExecuteAsync(Atlas.Core.Operations.LayoutOperation, CancellationToken)"/>
/// (activating the workflow tab when a finding is clicked), which needs a workspace the app owns.
/// <para>
/// Four tool groups use <see cref="LogicalRegion"/> slots
/// (<c>InlineStartLower</c>, <c>InlineEndUpper</c>, <c>BlockEndInlineStart</c>, <c>BlockEndInlineEnd</c>).
/// Atlas allows at most one tool group per region, so two slots are left free and the ceiling is honoured.
/// The centre canvas has no region of its own — 0.4.0 has no region for "middle" — so its rail entry
/// lands on the inline-start side, which is the documented gap in
/// <c>docs/notes/atlas-0.4.0-api.md</c> §8 caveat C2.
/// </para>
/// <para>
/// Sizing follows Atlas' elastic-absorb rule (ADR-0021): the docked tracks are
/// <see cref="SplitBasis.FixedPixels(double, SplitAnchor)"/> and the document track is the elastic one.
/// </para>
/// </remarks>
public static class ActionLensWorkspaceDefinition
{
    /// <summary>Height the bottom dock opens at.</summary>
    public const double BottomDockPixels = 232d;

    /// <summary>Width the editor column opens at.</summary>
    public const double EditorColumnPixels = 460d;

    /// <summary>Width the inspector column opens at.</summary>
    public const double InspectorColumnPixels = 340d;

    /// <summary>Builds the definition for one workspace id.</summary>
    public static AtlasWorkspaceDefinition Create(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);

        DockItem workflow = Document(ActionLensItemIds.Workflow, ActionLensContentKinds.Workflow, "Workflow", isPinned: true);
        DockItem payload = Document(ActionLensItemIds.Payload, ActionLensContentKinds.Payload, "Event payload", isPinned: true);
        DockItem canvas = Tool(ActionLensItemIds.Canvas, ActionLensContentKinds.Canvas, "Graph (W5)");
        DockItem inspector = Tool(ActionLensItemIds.Inspector, ActionLensContentKinds.Inspector, "Inspector");
        DockItem diagnostics = Tool(ActionLensItemIds.Diagnostics, ActionLensContentKinds.Diagnostics, "Diagnostics");
        DockItem summary = Tool(ActionLensItemIds.Summary, ActionLensContentKinds.Summary, "Run summary");

        GroupNode editorGroup = new(
            Id(ActionLensItemIds.EditorGroup),
            DockItemKind.Document,
            GroupRetentionPolicy.Persistent,
            GroupVisibility.Expanded,
            new[] { workflow.Id, payload.Id },
            workflow.Id);

        GroupNode canvasGroup = ToolGroup(
            ActionLensItemIds.CanvasGroup,
            LogicalRegion.InlineStartLower,
            canvas.Id);

        GroupNode inspectorGroup = ToolGroup(
            ActionLensItemIds.InspectorGroup,
            LogicalRegion.InlineEndUpper,
            inspector.Id);

        GroupNode diagnosticsGroup = ToolGroup(
            ActionLensItemIds.DiagnosticsGroup,
            LogicalRegion.BlockEndInlineStart,
            diagnostics.Id);

        GroupNode summaryGroup = ToolGroup(
            ActionLensItemIds.SummaryGroup,
            LogicalRegion.BlockEndInlineEnd,
            summary.Id);

        SplitNode centreAndInspector = Split(
            "actionlens-centre-right",
            SplitAxis.InlineChildren,
            SplitBasis.FixedPixels(InspectorColumnPixels, SplitAnchor.Second),
            canvasGroup.Id,
            inspectorGroup.Id,
            new SplitConstraints(280d, null, 220d, 460d));

        SplitNode main = Split(
            "actionlens-main",
            SplitAxis.InlineChildren,
            SplitBasis.FixedPixels(EditorColumnPixels, SplitAnchor.First),
            editorGroup.Id,
            centreAndInspector.Id,
            new SplitConstraints(260d, 680d, 320d, null));

        SplitNode bottomDock = Split(
            "actionlens-bottom",
            SplitAxis.InlineChildren,
            SplitBasis.Proportional(0.6d),
            diagnosticsGroup.Id,
            summaryGroup.Id,
            new SplitConstraints(220d, null, 180d, null));

        SplitNode root = Split(
            "actionlens-root",
            SplitAxis.BlockChildren,
            SplitBasis.FixedPixels(BottomDockPixels, SplitAnchor.Second),
            main.Id,
            bottomDock.Id,
            new SplitConstraints(240d, null, 120d, 420d));

        LayoutNode[] nodes =
        [
            root,
            main,
            centreAndInspector,
            bottomDock,
            editorGroup,
            canvasGroup,
            inspectorGroup,
            diagnosticsGroup,
            summaryGroup,
        ];

        DockItem[] items = [workflow, payload, canvas, inspector, diagnostics, summary];

        ToolBarState[] toolBars =
        [
            new(LogicalSide.InlineStart, ToolBarVisibility.Expanded),
            new(LogicalSide.InlineEnd, ToolBarVisibility.Expanded),
        ];

        ToolBarOrderState[] toolBarOrders =
        [
            new(LogicalRegion.InlineStartLower, new[] { canvas.Id }),
            new(LogicalRegion.InlineEndUpper, new[] { inspector.Id }),
            new(LogicalRegion.BlockEndInlineStart, new[] { diagnostics.Id }),
            new(LogicalRegion.BlockEndInlineEnd, new[] { summary.Id }),
        ];

        return new AtlasWorkspaceDefinition(
            new WorkspaceId(workspaceId),
            root.Id,
            nodes,
            items,
            toolBars,
            toolBarOrders);
    }

    private static GroupNode ToolGroup(string id, LogicalRegion region, params DockItemId[] itemIds) =>
        new(
            Id(id),
            DockItemKind.Tool,
            GroupRetentionPolicy.Persistent,
            GroupVisibility.Expanded,
            itemIds,
            itemIds[0],
            toolbarRegion: region);

    private static SplitNode Split(
        string id,
        SplitAxis axis,
        SplitBasis basis,
        LayoutNodeId firstId,
        LayoutNodeId secondId,
        SplitConstraints constraints) =>
        new(Id(id), axis, basis, constraints, firstId, secondId);

    private static DockItem Tool(string id, string contentKind, string title) =>
        new(
            new DockItemId(id),
            DockItemKind.Tool,
            new ContentReference(contentKind, id),
            DockItemCapabilityPresets.Tool,
            title,
            id);

    private static DockItem Document(string id, string contentKind, string title, bool isPinned) =>
        new(
            new DockItemId(id),
            DockItemKind.Document,
            new ContentReference(contentKind, id),
            DockItemCapabilityPresets.Document,
            title,
            id,
            new DocumentState(isPinned, isPreview: false));

    private static LayoutNodeId Id(string value) => new(value);
}
