# Atlas.Blazor 0.4.0 — grounded API note for the ActionLens UI shell

Scope: everything the UI-shell task is allowed to code against. No API name in this document is
inferred; each claim carries a source. Where a source disagrees with the package, the package wins
and the disagreement is recorded in §7 (gap table) or §9 (open questions).

Paths used below (all read-only):

| Short | Absolute |
| --- | --- |
| `[PKG]` | `C:\Users\World\.nuget\packages\atlas.blazor\0.4.0\` |
| `[XML]` | `[PKG]lib\net10.0\Atlas.Blazor.xml` (1916 lines, shipped inside the package) |
| `[ATLAS]` | `C:\Users\World\source\repos\Atlas\` |
| `[SAMPLE]` | `[ATLAS]samples\Atlas.Showcase\` |

## 0. Method, and the trap it closes

**Package lineage.** `[PKG]atlas.blazor.nuspec` pins the build to repository commit
`ea1e6b58af121a8a85cbbcbebc9229d6c3f1fe20`. In `[ATLAS]` that commit is `HEAD~2`:
`f2def23 feat(workspace): 实现浮动窗口与 Schema V3 (#75)` and `e3cffe9 infra(license): Apache-2.0`
are both **after** the packaged commit. Anything introduced by `#75` is unreleased.

**How the API surface was obtained.** Public types/members were read from the assembly metadata of
`[PKG]lib\net10.0\Atlas.Blazor.dll` and `[PKG]lib\net10.0\Atlas.Core.dll`
(`System.Reflection.Metadata.PEReader` / `MetadataReader`, membership filtered to
`TypeAttributes.Public` / `MethodAttributes.Public` / `FieldAttributes.Public`, parameter types
decoded with `ISignatureTypeProvider`). `ilspycmd` could not be installed: `dotnet tool install`
fails with `无法加载源 https://api.nuget.org/v3/index.json` on this machine, so no decompiler was
used and no decompiled body text is quoted anywhere in this note.

**Critical caveat about the XML doc.** `[XML]` documents **internal** types as well as public ones —
60 `<member name="T:…">` entries exist, only **31 types are public** in the assembly. Therefore:

> An entry in `Atlas.Blazor.xml` is **not** proof the type or member is usable. `AtlasFocusCoordinator`,
> `AtlasGroupView`, `AtlasPopupMenu`, `AtlasContentOutlet`, `AtlasDomIdentity`, `AtlasItemDescriptor`,
> `AtlasGroupDescriptor`, `AtlasSplitDescriptor`, `AtlasContentCatalog`, `AtlasContentRegistration`,
> `AtlasRootInteractionController`, `AtlasInteractionMessage`, `AtlasDockIntentFactory` and
> `AtlasContentErrorBoundary` are all documented in `[XML]` and all `internal` in 0.4.0. Their XML line
> numbers are cited in this note only as *behavioural* evidence, never as an API you may call.

**How the sample can mislead.** `[SAMPLE]Atlas.Showcase.csproj:10-11` uses `ProjectReference` to
`src\Atlas.Blazor` and `src\Atlas.Core`, not the NuGet package, so the sample compiles against `HEAD`
(including `#75`). Its `bin\...\Atlas.Core.dll` confirms this: it contains
`Atlas.Core.Operations.FloatToolOperation`, `SetFloatingWindowGeometryOperation`,
`DockFloatingToolOperation`, `Atlas.Core.Placement.FloatingWindowGeometry/FloatingWindowState/
FloatingReturnPlacement/FloatingReturnKind/FloatingWindowLimits`, `WorkspaceSnapshot.FloatingWindows`,
`WorkspaceErrorCode.FloatingWindowLimit` and `AtlasWorkspaceCommands.FloatToolAsync/
SetFloatingWindowGeometryAsync/DockFloatingToolAsync` — **none of which exist in `[PKG]`**. See §7.

---

## 1. Package facts

| Fact | Value | Source |
| --- | --- | --- |
| Package id / version | `Atlas.Blazor` `0.4.0` | `[PKG]atlas.blazor.nuspec` |
| Target framework | **`net10.0` only** (`lib/net10.0/`, no other TFM folder) | package layout; nuspec dependency group `targetFramework="net10.0"` |
| Assemblies shipped | `Atlas.Blazor.dll`, `Atlas.Core.dll` (Core is **embedded**, not a package dependency) | `[PKG]lib\net10.0\`; `[ATLAS]docs\zh\adr\0019-single-package-atlas-blazor.md` decision 1–3 |
| NuGet dependency | exactly one: `Microsoft.AspNetCore.Components.Web` **`10.0.14`** (`exclude="Build,Analyzers"`) | `[PKG]atlas.blazor.nuspec` `<dependencies>` |
| License as shipped | `MIT` (expression) | `[PKG]atlas.blazor.nuspec` `<license>` — note the repo moved to Apache-2.0 *after* this package |
| Content files | `contentFiles/any/net10.0/ClientScripts/dom-metadata-manifest.json`, `buildAction="Content"`, `copyToOutput=false` | nuspec `<contentFiles>`; `[PKG]ActionLens` restore graph shows `copyToOutput: false` |
| Build props | `build/`, `buildTransitive/`, `buildMultiTargeting/Atlas.Blazor.props`, each importing `Microsoft.AspNetCore.StaticWebAssets.props` + `Microsoft.AspNetCore.StaticWebAssetEndpoints.props` | `[PKG]build\Atlas.Blazor.props` |

**Static web assets shipped** (all under `BasePath = _content/Atlas.Blazor`):

| RelativePath | Fingerprint | Bytes | Source |
| --- | --- | --- | --- |
| `atlas-v2/atlas.css` | `0hvbj1zllf` | 17109 | `[PKG]build\Microsoft.AspNetCore.StaticWebAssets.props` |
| `atlas-v2/atlas.js` | `ecksvjecd5` | 62282 | same file |
| `atlas-v2/atlas.js.map` | `cnwr1w4e0y` | 137627 | same file |
| `atlas-v2/themes/ide.css` | (listed in props) | — | same file; `[PKG]staticwebassets\atlas-v2\themes\ide.css` |

### 1.1 What ActionLens must put in `index.html`

* **CSS: yes, two `<link>` tags, in this order.** Nothing in the package auto-injects a stylesheet —
  `build/Atlas.Blazor.props` only registers static web assets. The contract, from
  `[ATLAS]docs\zh\theming.md:9-11` and `[SAMPLE]wwwroot\index.html:10-12`:

  ```
  _content/Atlas.Blazor/atlas-v2/atlas.css          (required, default theme)
  _content/Atlas.Blazor/atlas-v2/themes/ide.css     (optional compact IDE look, ADR-0039)
  css/app.css                                       (your overrides, LAST)
  ```

  `[SAMPLE]Components\ShowcaseSnippets.cs:105` states the rule verbatim: “在 atlas.css 之后链接可选
  IDE 主题”, and `theming.md:35` — overrides must be *after* the Atlas links. Current
  `src\ActionLens.App\wwwroot\index.html` links only `css/app.css`, so both Atlas links have to be
  inserted **above** it.
* **JS: no `<script>` tag.** The host component imports the module itself. Evidence: the UTF-16 string
  heap of `[PKG]lib\net10.0\Atlas.Blazor.dll` contains the literal
  `./_content/Atlas.Blazor/atlas-v2/atlas.js` next to the interop target `import`; corroborating
  (HEAD) source constants are `[ATLAS]src\Atlas.Blazor\Components\Workspace\AtlasPopupMenu.cs:17` and
  `...\Interop\AtlasRootInteractionController.cs:16`. The sample's `index.html` contains no Atlas script
  tag (`[SAMPLE]wwwroot\index.html:1-30`, only `_framework/blazor.webassembly.js`).
* Do **not** copy `atlas.css`/`atlas.js` into `wwwroot` — `[ATLAS]docs\zh\adr\0017` decision 5 forbids
  consumers copying RCL assets, and `theming.md:151-159` presents the copy path as a last resort that
  stops tracking upgrades.

---

## 2. Complete public surface of `Atlas.Blazor.dll` 0.4.0 (31 types)

Anything the shell uses must be on this list.

`Atlas.Blazor.Components`: `AtlasWorkspaceHost`, `AtlasSplitDefinition`, `AtlasToolGroupDefinition`,
`AtlasDocumentGroupDefinition`, `AtlasToolItemDefinition`, `AtlasDocumentItemDefinition`,
`AtlasContentRoute`, `AtlasIcon`, `AtlasIconName`, `AtlasKeyboardCommand`, `AtlasKeyboardChord`,
`AtlasKeyboardBinding`, `AtlasKeyboardCommandMap`, `AtlasSplitterOptions`, `AtlasSplitterPresentation`,
`AtlasSandboxedFrameOptions`, `AtlasHostError`, `AtlasHostErrorCode`, `AtlasContentLifetimeMode`,
`WorkspaceContentLifetimeOptions`, `WorkspaceOwnership`.

`Atlas.Blazor.Content`: `AtlasContentContext`, `AtlasContentCommands`, `AtlasContentComponent`,
`AtlasEmptyStateContext`, `AtlasItemHeaderContext`, `AtlasContentMap`, `AtlasContentMapBuilder`,
`AtlasComponentBinding<TComponent>`, `IAtlasContentStateScope`.

`Atlas.Blazor.DependencyInjection`: `AtlasServiceCollectionExtensions` —
`AddAtlasWorkspace(this IServiceCollection)`,
`AddAtlasContent<T>(this IServiceCollection, String kind)`.

Namespaces to `@using`: `[SAMPLE]Components\ShowcaseWorkspace.razor:2-3` (`Atlas.Blazor.Components`,
`Atlas.Blazor.Content`); Core namespaces per `[SAMPLE]Components\ShowcaseSnippets.cs:16-21`
(`Atlas.Core`, `Atlas.Core.Items`, `Atlas.Core.Layout`, `Atlas.Core.Placement`), plus
`Atlas.Core.Definitions` / `Atlas.Core.Operations` / `Atlas.Core.Persistence`
(`[SAMPLE]Pages\TabsAutoHideExample.razor:6-11`). Note `[SAMPLE]\_Imports.razor` does **not** import any
Atlas namespace — each page/component imports explicitly.

---

## 3. Component surface for this layout

### 3.1 `AtlasWorkspaceHost` — the resizable workspace root

`public sealed class AtlasWorkspaceHost : ComponentBase`, implements `System.IAsyncDisposable`, public
`ValueTask DisposeAsync()` (InterfaceImplementation + method tables of the 0.4.0 assembly; type doc
`[XML]:1135`).
Two mutually exclusive modes (`AtlasHostErrorCode.InitializationModeConflict`, `[XML]:22`).

| Parameter | Exact type | Notes / source |
| --- | --- | --- |
| `Workspace` | `Atlas.Core.IAtlasWorkspace` | programmatic mode; “更改它会移除旧绑定并添加新绑定” `[XML]:1156` |
| `WorkspaceId` | `Nullable<Atlas.Core.WorkspaceId>` | declarative mode, required there; “更改它会创建新的 Host 工作区” `[XML]:1163` |
| `WorkspaceOptions` | `Atlas.Core.AtlasWorkspaceOptions` | `[XML]:1170` |
| `WorkspaceOwnership` | enum `Automatic`/`External`/`Host`, default `Automatic` | `External` = 应用拥有并释放, `Host` = Host 拥有并释放 `[XML]:1341-1360` |
| `ChildContent` | `RenderFragment` | holds the declarative definition tree; “每次渲染批次变化都会原子对账” `[XML]:1183` |
| `TopToolbarContent` | `RenderFragment` | fixed top slot, app-owned `[XML]:1190` |
| `StatusBarContent` | `RenderFragment` | fixed bottom slot, app-owned `[XML]:1197` |
| `ContentRoutes` | `RenderFragment` | host-local `AtlasContentRoute` declarations `[XML]:1204` |
| `ContentMap` | `Atlas.Blazor.Content.AtlasContentMap` | functional mapping alternative `[XML]:1211` |
| `SandboxedFrameResolver` | `Func<AtlasContentContext, AtlasSandboxedFrameOptions>` | `[XML]:1219` |
| `ContentLifetimeOptions` | `WorkspaceContentLifetimeOptions` | default = unload inactive content `[XML]:1225` |
| `ToolEmptyTemplate` / `DocumentEmptyTemplate` | `RenderFragment<AtlasEmptyStateContext>` | `[XML]:1231`, `1237` |
| `ToolHeaderTemplate` / `DocumentHeaderTemplate` | `RenderFragment<AtlasItemHeaderContext>` | `[XML]:1243`, `1249` |
| `ErrorTemplate` | `RenderFragment<AtlasHostError>` | `[XML]:1255`; usage `[SAMPLE]Components\ShowcaseWorkspace.razor:101-105` |
| `OnError` | `EventCallback<AtlasHostError>` | “在 Core 工作区锁之外运行” `[XML]:1261` |
| `EnableInteractions` | `bool`, **default `true`** | toggles the browser controller `[XML]:1267` |
| `SplitterOptions` | `AtlasSplitterOptions` | `[XML]:1274` |
| `KeyboardCommands` | `AtlasKeyboardCommandMap` | `[XML]:1280` |
| `SplitterPresentation` | `Func<Atlas.Core.LayoutNodeId, AtlasSplitterPresentation>` | per-splitter lock/collapse in programmatic mode `[XML]:1286` |

Injected (not settable): `IAtlasWorkspaceFactory Factory`, `IServiceProvider`, `IJSRuntime` — assembly
metadata. Real usage: `[SAMPLE]Components\ShowcaseWorkspace.razor:23-106` (programmatic + `External` +
`EnableInteractions="true"`) and `[SAMPLE]Pages\DeclarativeExample.razor:20-84` (declarative).

### 3.2 Definition components (tab containers, panels, splits)

`AtlasSplitDefinition` (`sealed`, `[XML]:579`): `Id` `LayoutNodeId` *(EditorRequired)*, `Axis`
`Atlas.Core.Layout.SplitAxis` `{ InlineChildren, BlockChildren }`, `Basis` `SplitBasis`,
`Constraints` `SplitConstraints` `[XML]:599`, `IsLocked` bool `[XML]:604`, `SplitterClass` string
`[XML]:609`, `CanCollapseFirst` bool `[XML]:614`, `CanCollapseSecond` bool `[XML]:619`, `ChildContent`
`RenderFragment` *(EditorRequired)*. Usage: `[SAMPLE]Pages\DeclarativeExample.razor:65-82`,
`[SAMPLE]Pages\TabsAutoHideExample.razor:47-56`.

`AtlasToolGroupDefinition` (a **tab container of tool panels**; `[XML]:635`): `Id` `LayoutNodeId`
*(EditorRequired)*, `LogicalRegion` `Atlas.Core.Placement.LogicalRegion` *(EditorRequired)* `[XML]:645`,
`RetentionPolicy` `GroupRetentionPolicy` `{Persistent, Dynamic}` `[XML]:650`, `Visibility`
`GroupVisibility` `{Expanded, Collapsed}` `[XML]:655`, `SelectedItemId` `Nullable<DockItemId>` `[XML]:660`,
`ChildContent` `RenderFragment` `[XML]:665`.

`AtlasDocumentGroupDefinition` (a **tab container of editor tabs**; `[XML]:676`): `Id` `LayoutNodeId`
*(EditorRequired)*, `RetentionPolicy`, `OverflowMode` `EditorOverflowMode` `{Scroll, Menu, Wrap}`
`[XML]:691`, `ActivationPolicy` `EditorActivationPolicy` `{Adjacent, MostRecentlyVisited}` `[XML]:696`,
`SelectedItemId`, `ChildContent`. Three-way overflow demo: `[SAMPLE]Pages\TabsAutoHideExample.razor:35-118`.

`AtlasToolItemDefinition` (`[XML]:717`): `Id` `DockItemId` *(EditorRequired)*, `ContentKind` `string`
*(EditorRequired)* `[XML]:727`, `ResourceId` `string` *(EditorRequired)* `[XML]:732`, `Title` `string?`
`[XML]:737`, `IconKey` `string?` `[XML]:742`, `Capabilities` `DockItemCapabilities` `[XML]:747`.

`AtlasDocumentItemDefinition` (`[XML]:755`): same six plus `IsPinned` bool `[XML]:790`.

`AtlasContentRoute` (`[XML]:352`): `Kind` `string` *(EditorRequired)* `[XML]:361`,
`ChildContent` `RenderFragment<Atlas.Blazor.Content.AtlasContentContext>` *(EditorRequired)* `[XML]:366`.
Usage pattern (`Context="content"` is required because the fragment is contextual):
`[SAMPLE]Components\ShowcaseWorkspace.razor:52-98`, `[SAMPLE]Pages\DeclarativeExample.razor:33-61`,
snippet `[SAMPLE]Components\ShowcaseSnippets.cs:54-61`.

`AtlasIcon`: `Name` `AtlasIconName` *(EditorRequired)*, `Size` `int`, `CssClass` `string?` — the enum has
exactly five values: `Close, ChevronUp, ChevronDown, ChevronLeft, ChevronRight`
(assembly metadata; `[XML]:97-140`). No general icon catalogue exists in 0.4.0 (ADR-0040 covers only
Atlas' own chrome glyphs).

### 3.3 Panel collapse / resize / sizing constraints

* **Geometry model** (all public, `Atlas.Core.dll` 0.4.0):
  `SplitBasis.FixedPixels(double, SplitAnchor)` / `SplitBasis.Proportional(double)`;
  `new SplitConstraints(double firstMinimum, double? firstMaximum, double secondMinimum, double? secondMaximum)`;
  `new SplitNode(LayoutNodeId, SplitAxis, SplitBasis, SplitConstraints, LayoutNodeId firstId, LayoutNodeId secondId)`;
  `new GroupNode(LayoutNodeId, DockItemKind, GroupRetentionPolicy, GroupVisibility, IEnumerable<DockItemId>,
  Nullable<DockItemId> selectedItemId, [opt] EditorGroupState?, [opt] Nullable<LogicalRegion> toolbarRegion)`;
  `new AtlasWorkspaceDefinition(WorkspaceId, Nullable<LayoutNodeId> root, IEnumerable<LayoutNode>,
  IEnumerable<DockItem>, [opt] IEnumerable<ToolBarState>, [opt] IEnumerable<ToolBarOrderState>,
  [opt] IEnumerable<AutoHideStripState>)`.
  Real usage with the exact values a 3-column + bottom-strip IDE layout needs:
  `[SAMPLE]Components\ShowcaseWorkspaceDefinition.cs:47-113`.
* **Elastic-absorb rule (constraint on you):** dock/tool panels use
  `SplitBasis.FixedPixels(pixels, anchor)`; the editor track is the single elastic track;
  `Proportional` is reserved for non-critical subdivisions *inside* one panel —
  `[ATLAS]docs\zh\adr\0021-elastic-absorb-split-sizing.md` §“弹性吸收模型”. The same split nesting as
  `[SAMPLE]Components\ShowcaseWorkspaceDefinition.cs:69-96`.
* **Collapse**: `AtlasSplitDefinition.CanCollapseFirst/CanCollapseSecond` +
  `AtlasToolGroupDefinition.Visibility` (declarative) or `SetGroupVisibilityOperation(LayoutNodeId,
  GroupVisibility, long?)` (programmatic, used at `[SAMPLE]Components\ShowcaseWorkspace.razor.cs:176, 242`).
  In programmatic mode the per-splitter collapse/lock surface is
  `AtlasSplitterPresentation(bool isLocked, string? cssClass, bool canCollapseFirst, bool canCollapseSecond)`
  `[XML]` ctor `M:…AtlasSplitterPresentation.#ctor(System.Boolean,System.String,System.Boolean,System.Boolean)`
  supplied through `AtlasWorkspaceHost.SplitterPresentation`.
* **Invariant you must respect**: an *empty Persistent tool group is auto-Collapsed*, and a Document
  group can never be Collapsed — `[ATLAS]docs\zh\adr\0022-group-visibility-invariant.md` §不变量 1–2.
* **Region budget**: each `LogicalRegion` may host **at most one** tool group; a second group with the
  same region is a `DuplicateLogicalRegion` invariant violation
  (`[ATLAS]src\Atlas.Core\Validation\PlacementInvariantRules.cs:54-62`; the enum has exactly six values:
  `InlineStartUpper, InlineStartLower, InlineEndUpper, InlineEndLower, BlockEndInlineStart,
  BlockEndInlineEnd` — `Atlas.Core.dll` 0.4.0 metadata). So: at most 6 tool tab-containers, i.e. 6 docked panels.
* **Splitter interaction tuning**: `new AtlasSplitterOptions([opt] double keyboardStepPixels, [opt] int
  keyboardCommitDelayMilliseconds)` — “键盘步长必须为正且有限，提交延迟不能为负” (XML doc for that ctor).
  Presentation per split in programmatic mode:
  `new AtlasSplitterPresentation([opt] bool isLocked, [opt] string? cssClass, [opt] bool canCollapseFirst,
  [opt] bool canCollapseSecond)`.

### 3.4 Persisted-layout API (all public in `[PKG]lib\net10.0\Atlas.Core.dll`)

```
AtlasXmlLayoutSerializer (sealed, implements IAtlasLayoutSerializer)   [verified interface table]
  string Serialize(WorkspaceSnapshot)
  WorkspaceResult<LayoutRestoreDocument> Deserialize(string, WorkspaceId, LayoutPersistenceLimits)
  WorkspaceResult<LayoutRestoreDocument> DeserializeWithOptions(string, WorkspaceId, LayoutRestoreOptions)
LayoutRestoreOptions([opt] LayoutPersistenceLimits limits,
                     [opt] Func<ContentReference,bool>? isContentRegistered,
                     [opt] MissingContentPolicy missingContentPolicy,
                     [opt] IEnumerable<DockItemId>? reservedItemIds)     // all four optional
MissingContentPolicy { PreservePlaceholder, DropUnknownItems, FailRestore }
RestoreWorkspaceOperation(LayoutRestoreDocument, long? baseRevision)
IWorkspaceLayoutStore      { SaveAsync(WorkspaceLayoutDocument, CT), TryLoadAsync(CT) }
ILayoutPresetStore         { SaveAsync, TryLoadAsync, DeleteAsync, ListAsync }
LayoutAutoPersistence(IAtlasWorkspace, IAtlasLayoutSerializer, IWorkspaceLayoutStore,
                      [opt] TimeSpan? debounce, [opt] Action<WorkspacePersistenceDiagnostic>?)
  : IAsyncDisposable; RestoreIfAvailableAsync(CT) / FlushAsync(CT) / DisposeAsync()
LayoutPresetService(IAtlasWorkspace, IAtlasLayoutSerializer, ILayoutPresetStore)   // plain class
  SaveAsync/ApplyAsync/DeleteAsync/ListAsync
WorkspaceLayoutDocument(string schemaVersion, long savedRevision, string xml)
LayoutSchemaVersion.Current  ->  "2.0"   (assembly string heap has `urn:atlas:workspace:2`)
```

(`[opt]` = parameter carries `ParameterAttributes.Optional` in the 0.4.0 assembly metadata; interface
implementation read from the InterfaceImplementation table of the same assemblies.)

Working end-to-end capture/restore: `[SAMPLE]Components\ShowcaseWorkspace.razor.cs:325-368`, snippet
`[SAMPLE]Components\ShowcaseSnippets.cs:114-128`.

**Only `InMemoryWorkspaceLayoutStore` and `InMemoryLayoutPresetStore` ship.** A grep of
`[ATLAS]src` and `[ATLAS]tests` for `: IWorkspaceLayoutStore` / `: ILayoutPresetStore` finds only those
two plus test doubles — there is **no browser/localStorage store in the library**. Persistence across
reload therefore needs an ActionLens-owned `IWorkspaceLayoutStore` over `IJSRuntime` + `localStorage`
(§8, §9).

---

## 4. Programmatic workspace API (what the shell's buttons call)

`IAtlasWorkspace` (`Atlas.Core.dll` 0.4.0): `Id`, `Current` (`WorkspaceSnapshot`), `CanUndo`, `CanRedo`,
`DomainEvents` (`IAtlasWorkspaceDomainEvents`), event `Changed` (`WorkspaceChangedEventArgs`),
`ExecuteAsync(LayoutOperation, CT)`, `UndoAsync(CT)`, `RedoAsync(CT)`; `DisposeAsync()` on the
concrete workspace (called by the sample at `[SAMPLE]Components\ShowcaseWorkspace.razor.cs:118`).

`IAtlasWorkspaceFactory.Create(AtlasWorkspaceDefinition, AtlasWorkspaceOptions?)` →
`WorkspaceResult<IAtlasWorkspace>`. Registration: `AddAtlasWorkspace()` (`[SAMPLE]
ShowcaseServiceCollectionExtensions.cs:15`; readme Quick Start in `[PKG]package-readme.md`).

`WorkspaceSnapshot` (0.4.0) exposes exactly: `Id, Revision, RootNodeId, Nodes`,
`Items`, `ToolBars`, `ToolBarOrders`, `AutoHideStrips`. **No `FloatingWindows`** — see §7.

`AtlasWorkspaceCommands` (0.4.0) methods: `OpenItemAsync`, `OpenItemFromRequestAsync`, `CloseItemAsync`,
`ActivateItemAsync`, `SetToolVisibilityAsync`, `SetToolBarVisibilityAsync`, `ResetWorkspaceAsync`,
`RestoreAsync`, `ActivateFirstAsync`. `IAtlasWorkspace.Current` + `ExecuteAsync` are used by the sample
directly with operations instead (`[SAMPLE]Components\ShowcaseWorkspace.razor.cs:185, 225, 280`).

Operation ctors available in 0.4.0 (assembly metadata; the trailing `baseRevision` `Nullable<long>` — and
where shown the `targetIndex` / new-group ids — carry `ParameterAttributes.Optional`, so they may be
omitted):
`OpenItemOperation(DockItem, LayoutNodeId, ContentDeduplicationMode, [opt] int?, [opt] long?)`,
`OpenItemRequestOperation(OpenItemRequest, LayoutNodeId?, ContentDeduplicationMode, int?, long?)`,
`CloseItemOperation(DockItemId, [opt] long?)`, `SelectItemOperation(DockItemId, [opt] long?)`,
`MoveToolOperation(DockItemId, LayoutNodeId, [opt] int? targetIndex, [opt] long?)`,
`ReorderItemOperation(DockItemId, LayoutNodeId, int, [opt] long?)`,
`DockDocumentOperation(DockItemId, LayoutNodeId, DocumentDockZone, [opt] LayoutNodeId?, [opt] LayoutNodeId?, [opt] long?)`
with `DocumentDockZone { Center, InlineStart, InlineEnd, BlockStart, BlockEnd }`,
`ResizeSplitOperation(LayoutNodeId, SplitBasis, [opt] long?)` + static `CreateKeyboard(LayoutNodeId, SplitBasis)`,
`EqualizeSplitOperation(LayoutNodeId, [opt] long?)`,
`SetGroupVisibilityOperation(LayoutNodeId, GroupVisibility, [opt] long?)`,
`SetToolBarVisibilityOperation(LogicalSide, ToolBarVisibility, [opt] long?)`,
`ReorderToolBarEntryOperation(DockItemId, LogicalRegion, int, [opt] long?)`,
`UpdateItemPresentationOperation(DockItemId, string? title, string? iconKey, [opt] long?)`,
`ResetWorkspaceOperation([opt] long?)`, `RestoreWorkspaceOperation(LayoutRestoreDocument, [opt] long?)`,
`ReconcileDefinitionOperation(AtlasWorkspaceDefinition, [opt] long?)`.
`OpenItemRequest(DockItemKind, ContentReference, DockItemCapabilities, [opt] DockItemId?, [opt] string? title,
[opt] string? iconKey, [opt] DocumentState?)`.

Domain events (`IAtlasWorkspaceDomainEvents`): `ItemOpened, ItemClosed, ItemActivated, ToolCreated,
ToolRemoved, WorkspaceRestored`; `LayoutChangeSet` carries
`BeforeRevision/AfterRevision/ChangeKind/Added|Removed|Updated Nodes|Items|ToolBars|ToolBarOrders|
AutoHideStrips/HistoryRecorded/IsHistoryBarrier` — this is what a status line reads
(`[SAMPLE]Components\ShowcaseWorkspace.razor.cs:400-415`).

**Auto-hide has no public operation in 0.4.0.** `LayoutOperationKind` has `SetAutoHide` and
`RestoreAutoHiddenItem` fields, but no `SetAutoHideOperation` type is public; the only public way to get
auto-hidden panels is to declare them in the definition:
`AutoHideStripState(WorkspaceEdge, IEnumerable<AutoHideEntry>)`,
`new AutoHideEntry(DockItemId, new DockBack(LayoutNodeId preferredGroup, int index, WorkspaceEdge fallback))`
inside `AtlasWorkspaceDefinition(..., autoHideStrips:)` — real usage
`[SAMPLE]Pages\TabsAutoHideExample.razor:186-216`; `WorkspaceEdge { InlineStart, InlineEnd, BlockEnd }`.

---

## 5. Theming entry points

* **Link order** — §1.1. `theming.md:35`: defaults live on the `.atlas-v2-workspace` element, so any
  selector matching the workspace or an ancestor can override.
* **Token contract** — the public variables actually present in `[PKG]staticwebassets\atlas-v2\atlas.css`
  are 55; verified list (grep of that file): sizes `--atlas-splitter-size`,
  `--atlas-splitter-hit-area`, `--atlas-chrome-height`, `--atlas-gap`, `--atlas-padding-compact`,
  `--atlas-radius`, `--atlas-radius-sm`, `--atlas-border-width`, `--atlas-focus-ring-width`,
  `--atlas-drag-preview-max-width`, `--atlas-popup-inline-size`, `--atlas-popup-max-block-size`,
  `--atlas-popup-item-padding`, `--atlas-popup-item-radius`, `--atlas-flyout-inline-size`,
  `--atlas-flyout-block-size`, `--atlas-tab-overflow-button-inline-size`, `--atlas-group-border-width`,
  `--atlas-group-radius`, `--atlas-tab-indicator-width`; colors `--atlas-accent` (`Highlight`),
  `--atlas-surface` (`Canvas`), `--atlas-text` (`CanvasText`), `--atlas-drag-preview-shadow`,
  `--atlas-popup-bg/text/border/shadow/backdrop`, `--atlas-flyout-shadow`, `--atlas-group-bg`,
  `--atlas-chrome-bg`, `--atlas-tab-text`, `--atlas-tab-text-active`, `--atlas-tab-bg-active`,
  `--atlas-tab-bg-hover`, `--atlas-tab-indicator-color`, `--atlas-border-color`,
  `--atlas-splitter-color`; opacity `--atlas-splitter-opacity{,-hover,-focus,-disabled}`,
  `--atlas-drop-highlight-alpha-{soft,strong}`, `--atlas-toolbar-divider-opacity`,
  `--atlas-drag-preview-opacity`, `--atlas-popup-disabled-opacity`; layers `--atlas-layer-content`,
  `--atlas-layer-chrome`, `--atlas-layer-drag`, `--atlas-layer-popup-backdrop`, `--atlas-layer-popup`,
  plus the grid tracks `--atlas-first-track` / `--atlas-second-track`.
* **Dark / light**: there is **no theme-switch API** — `theming.md:26` “无 JavaScript、无运行时主题 API”,
  and `[ATLAS]docs\zh\adr\0039-opt-in-ide-theme.md` decision 1 (“激活方式仅为链接，不新增组件参数、属性
  或 JavaScript API”). Mechanism, read from the packaged file: the shipped
  `[PKG]staticwebassets\atlas-v2\themes\ide.css` declares `color-scheme: light dark` (line 80) and a dark
  palette block `@media (prefers-color-scheme: dark)` (line 83). With `atlas.css` alone, dark rendering comes from
  the host declaring `:root { color-scheme: light dark }` (`theming.md:168`, last bullet). The sample does
  exactly that (`[SAMPLE]wwwroot\css\app.css:12`).
* **Reduced motion**: handled for you. `atlas.css` has both `@media (prefers-reduced-motion: reduce)`
  and an attribute rule for `.atlas-v2-workspace[data-atlas-reduced-motion="true"]`
  (`[PKG]staticwebassets\atlas-v2\atlas.css:620-637`), and the JS controller sets that attribute from
  `matchMedia("(prefers-reduced-motion: reduce)")` including a live `change` listener
  (`[PKG]staticwebassets\atlas-v2\atlas.js:1295-1388`). Consumer action: none, beyond linking
  `atlas.css`. `[ATLAS]docs\zh\SRS.md:4841-4855` (REQ-NF-008) is the requirement.
* **Content-side palette**: Atlas themes only Atlas chrome — “面板内容、顶部栏/状态栏内部内容与命令面板
  仍由应用负责” (`theming.md:25`). The sample keeps its own `--showcase-*` variables for that
  (`[SAMPLE]wwwroot\css\app.css:17-34, 36-56`) — that is the pattern ActionLens should follow.
* **Host must give the workspace a height**: `[SAMPLE]wwwroot\css\app.css:59-68` sets
  `width/height:100%; overflow:hidden` on `html, body, #app, .showcase-shell, .showcase-shell >
  .atlas-v2-workspace`. Without it the grid collapses (`atlas.css:1-78` root is
  `display:grid; grid-template-rows: auto minmax(0,1fr) auto`).

---

## 6. Focus, keyboard navigation, ARIA

* **Keyboard commands (public).** `AtlasKeyboardCommandMap.Default` and `.Bindings`
  (assembly metadata; `[XML]` `P:Atlas.Blazor.Components.AtlasKeyboardCommandMap.Default`).
  Custom map: `new AtlasKeyboardCommandMap(IEnumerable<AtlasKeyboardBinding>)`,
  `new AtlasKeyboardBinding(AtlasKeyboardChord, AtlasKeyboardCommand, [opt] bool isGlobal)`,
  `new AtlasKeyboardChord(string code, [opt] bool control, [opt] bool shift, [opt] bool alt, [opt] bool meta)` where `Code` is
  the physical `KeyboardEvent.code` (`[XML]` `P:Atlas.Blazor.Components.AtlasKeyboardChord.Code`).
  Commands (all seven, assembly metadata): `NextTab, PreviousTab, CloseActiveDocument, FocusInlineStart,
  FocusInlineEnd, FocusBlockStart, FocusBlockEnd`. Documented defaults + a working custom-map snippet:
  `[SAMPLE]Components\ShowcaseSnippets.cs:89-103`; behaviour text
  `[SAMPLE]Components\KeyboardPanel.razor:7-11` (Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+W / Alt+arrows,
  arrows resize the focused splitter). Wire it with `AtlasWorkspaceHost.KeyboardCommands`.
* **Focus primitives: none are public.** `AtlasFocusCoordinator` (arbitrating cross-render focus claims,
  `[XML]:933`ff) is `internal`. The only public focus signal in 0.4.0 is
  `AtlasHostErrorCode.FocusFailed` (`[XML]:42`) delivered via `AtlasWorkspaceHost.OnError` /
  `ErrorTemplate`. Claiming focus from the app is done *indirectly* through
  `SelectItemOperation(itemId)` — the sample relies on this for “打开面板并激活”
  (`[SAMPLE]Components\ShowcaseWorkspace.razor.cs:185-189`; `[ATLAS]docs\zh\SRS.md:1597` states the same
  user-facing behaviour: clicking a ToolBar entry opens/activates and focuses).
* **ARIA: the library emits it; it provides no helper you can call.** Roles found as string literals in
  `[PKG]lib\net10.0\Atlas.Blazor.dll`: `tab`, `tablist`, `tabpanel`, `region`, `separator`, `button`;
  attributes: `aria-controls, aria-disabled, aria-expanded, aria-haspopup, aria-hidden, aria-label,
  aria-labelledby, aria-level, aria-orientation, aria-selected, aria-valuemax, aria-valuenow,
  aria-valuetext`. The DOM contract is machine-readable in
  `[PKG]content\ClientScripts\dom-metadata-manifest.json`: the workspace root must carry
  `.atlas-v2-workspace` + `data-atlas-host-id/-workspace-id/-protocol-version/-revision/
  -reduced-motion` + `role` + `aria-label` — all rendered by the host, not by the consumer. Collapse
  controls announce as “Collapse region”/“Expand region”. There is **no public ARIA helper class**
  (`AtlasDomIdentity` is internal, `[XML]:898`). Requirement reference: `[ATLAS]docs\zh\SRS.md:4819-4837`
  (REQ-NF-007), `:4714-4743` (REQ-NF-003).
* **What ActionLens owns:** a11y of its *own* top bar / status bar / panel content. Copy the sample's
  patterns: `role="status" aria-live="polite" aria-atomic="true"` on the status bar
  (`[SAMPLE]Components\ShowcaseStatusBar.razor:3-8`), `aria-haspopup`/`aria-expanded`/`aria-controls` +
  `role="dialog" aria-modal="false" aria-labelledby tabindex="-1"` + `@onkeydown` for its own popovers
  (`[SAMPLE]Components\ShowcaseTopToolbar.razor:23-50`), `disabled` + `aria-label` on undo/redo
  (`:19-20`).
* **Interaction plumbing you get for free but must not fight**: the controller listens to `pointerdown`
  on the workspace root (`[PKG]staticwebassets\atlas-v2\atlas.js:1305`) and starts a drag only when the
  hit element resolves through `closest('[data-atlas-drag-kind]')`, panel-header, toolbar-entry, splitter
  or tab-strip selectors (`atlas.js:207-311, 1638, 1686`); it explicitly exempts
  `input,textarea,select,[contenteditable=true]` (`atlas.js:1692`). During a drag the root gets
  `[data-atlas-dragging="true"]`, which applies `cursor:grabbing; user-select:none`
  (`atlas.css:615-617`). Custom pointer handling inside panel content is outside the exemption list —
  see §9.

---

## 7. Gap table — `Atlas.Showcase` / `HEAD` API **absent from 0.4.0**

Verified by diffing public metadata of `[PKG]lib\net10.0\*.dll` against the sample's
`bin\Debug\net10.0\Atlas.Core.dll` / `Atlas.Blazor.dll` (HEAD build).

| # | What the sample (HEAD) uses | Where | What 0.4.0 actually offers | Consequence for ActionLens |
| --- | --- | --- | --- | --- |
| G1 | `Atlas.Core.Placement.FloatingWindowGeometry` (`new FloatingWindowGeometry(360,60,360,420)`) | `[SAMPLE]Pages\FloatingWindowExample.razor:155, 173` | **Type does not exist.** No floating layer at all | Do not design any “tear-off panel” interaction |
| G2 | `WorkspaceErrorCode.FloatingWindowLimit`, `Atlas.Core.Placement.FloatingWindowLimits` (`MaximumOpenWindows=8`, min 240×160) | `[ATLAS]src\Atlas.Core\Model\Placement\FloatingWindowLimits.cs` (added by `#75`) | enum has 13 fields in 0.4.0, no `FloatingWindowLimit` | n/a |
| G3 | `FloatToolOperation`, `SetFloatingWindowGeometryOperation`, `DockFloatingToolOperation` | `[SAMPLE]Pages\FloatingWindowExample.razor:152-186` | **None exist.** `LayoutOperationKind` in 0.4.0 ends at `…History`; no `FloatTool`/`DockFloatingTool` fields | Use `MoveToolOperation` (cross-group), `DockDocumentOperation` (editor split), `AutoHideStripState` (declared at definition time) |
| G4 | `workspace.Current.FloatingWindows` (dictionary), `FloatingWindowState`, `FloatingReturnPlacement`, `FloatingReturnKind`, `DockItemPlacementKind.Floating` | `[SAMPLE]Pages\FloatingWindowExample.razor:94, 165, 183` | `WorkspaceSnapshot` in 0.4.0 exposes only `Id, Revision, RootNodeId, Nodes, Items, ToolBars, ToolBarOrders, AutoHideStrips` | Status readouts must count tabs/groups, never floating windows |
| G5 | `AtlasWorkspaceCommands.FloatToolAsync / SetFloatingWindowGeometryAsync / DockFloatingToolAsync` | `[ATLAS]src\Atlas.Core\AtlasWorkspaceCommands.cs` (+44 lines in `#75`) | 0.4.0 `AtlasWorkspaceCommands` has the 9 methods listed in §4 | n/a |
| G6 | `Atlas.Blazor` internal `AtlasFloatingWindow` component + floating layer rendering + `role="dialog"` for float windows + 8-way resize | `[ATLAS]src\Atlas.Blazor\Components\Workspace\AtlasFloatingWindow.cs` (251 new lines, HEAD) | Not in the shipped assembly (verified: absent from the full type table, public *and* internal, of 0.4.0 `Atlas.Blazor.dll`) | n/a |
| G7 | Persistence **Schema V3** (`urn:atlas:workspace:3`, `schemaVersion="3.0"`, V2/V1 rejected) | `[ATLAS]docs\zh\adr\0043-…md` §7 | 0.4.0 is **V2 only**: assembly string heap has `urn:atlas:workspace:2`, `LayoutSchemaVersion.Current` is the 2.0 constant (`[ATLAS]docs\zh\adr\0031-…md` §1) | Any stored ActionLens layout from a later Atlas will not read; pin behaviour to V2 and treat `LayoutVersionIncompatible` as “reset to default” |
| G8 | Floating theme tokens `--atlas-floating-bg/border/header-bg/shadow`, `--atlas-layer-floating` | `[ATLAS]docs\zh\theming.md:96-99, 132` | **Not present** in `[PKG]staticwebassets\atlas-v2\atlas.css` (the 55 tokens in §5) | Never reference `--atlas-floating-*` / `--atlas-layer-floating` — they would be dead CSS |
| G9 | Sample page `/floating-windows` reachable from the toolbar | `[SAMPLE]Components\ShowcaseTopToolbar.razor:12` | the page itself will not compile against 0.4.0 | Treat `[SAMPLE]Pages\FloatingWindowExample.razor` as **non-documentation** |
| G10 | `LayoutPersistenceLimits.MaxMigrationSteps` | present in 0.4.0, **removed at HEAD** | inverse gap: 0.4.0 has it | fine to use on 0.4.0, but know it is not a durable surface |
| G11 | `package-readme.md` Quick Start markup `<AtlasWorkspaceHost WorkspaceId="my-workspace">` and `<AtlasToolItemDefinition Id="project" Kind="my-tool" …/>` | `[PKG]package-readme.md` | **Readme is wrong.** `WorkspaceId` is `Nullable<WorkspaceId>` with no `op_Implicit(string)` in 0.4.0 metadata, and the parameter is `ContentKind` + `ResourceId` (both `string`, EditorRequired), not `Kind` | Follow `[SAMPLE]Pages\DeclarativeExample.razor:65-81` and `[SAMPLE]Components\ShowcaseSnippets.cs:22-41`, which use `@(new WorkspaceId("…"))`, `@(new LayoutNodeId("…"))`, `@(new DockItemId("…"))`, `ContentKind=`, `ResourceId=` |
| G12 | Programmatic auto-hide toggle (drag a panel to the toolbar to auto-hide) | implied by `LayoutOperationKind.SetAutoHide`/`RestoreAutoHiddenItem` existing in 0.4.0 | 0.4.0 has **no public operation type** for it; only `AtlasWorkspaceDefinition(autoHideStrips:)` (`[SAMPLE]Pages\TabsAutoHideExample.razor:203-216`) | Auto-hide layout must be declared up front, not toggled from the toolbar |

Also note: `[ATLAS]docs\zh\SRS.md:506` describes Floating Window as an implemented committed placement.
The SRS is a `HEAD` document — it is **not** a 0.4.0 authority for capability questions, only for
requirement wording. Cross-check every SRS-cited capability against §2/§3 of this note.

---

## 8. Recommended minimal shell for ActionLens on 0.4.0 only

Mode: **programmatic** (`Factory.Create(AtlasWorkspaceDefinition)` → `Workspace` +
`WorkspaceOwnership.External` + `EnableInteractions="true"`), exactly as
`[SAMPLE]Components\ShowcaseWorkspace.razor.cs:85-97` + `[SAMPLE]Components\ShowcaseWorkspace.razor:23-26`.
Reason: the shell needs toolbar-driven collapse/expand, move, undo/redo, reset and layout capture, all of
which go through `ExecuteAsync` on a workspace the app holds; the declarative mode (`WorkspaceId` +
`ChildContent`) is viable but re-reconciles the definition on every render batch (`[XML]:1183`) and gives
no handle for `AtlasWorkspaceDefinition(autoHideStrips:)` (G12).

Component tree (structure only, no code):

```
index.html  ── atlas.css → themes/ide.css → css/app.css (§1.1)
Program.cs  ── builder.Services.AddAtlasWorkspace()
_Imports.razor ── @using Atlas.Blazor.Components / Atlas.Blazor.Content /
                  Atlas.Core / Atlas.Core.Items / Atlas.Core.Layout / Atlas.Core.Placement
App.razor → single page → ActionLensWorkspace (owns IAtlasWorkspace, IDisposable/IAsyncDisposable)
   └─ <AtlasWorkspaceHost Workspace WorkspaceOwnership EnableInteractions KeyboardCommands
                          SplitterOptions ContentRoutes ErrorTemplate OnError>
        ├─ TopToolbarContent  → ActionLensTopBar          (app markup; open panels, undo/redo, reset,
        │                                                   export/share buttons, theme note)
        ├─ StatusBarContent   → ActionLensStatusStrip     (app markup; role="status" aria-live,
        │                                                   revision + last diagnostic id)
        ├─ ChildContent       → AtlasSplitDefinition "root"  Axis=BlockChildren
        │                        Basis=FixedPixels(bottom strip height, SplitAnchor.Second)
        │                        Constraints=(min main, max main, min strip, max strip)
        │     ├─ AtlasSplitDefinition "main"  Axis=InlineChildren
        │     │        Basis=FixedPixels(left workflow width, SplitAnchor.First)
        │     │        Constraints=(150–380 / ≥360) — same shape as
        │     │        [SAMPLE]Components\ShowcaseWorkspaceDefinition.cs:76-82
        │     │    ├─ AtlasSplitDefinition "left-dock" Axis=BlockChildren (0.5)
        │     │    │     ├─ AtlasToolGroupDefinition "left-upper"  LogicalRegion=InlineStartUpper
        │     │    │     │     └─ AtlasToolItemDefinition ×N → workflow files / repository picker
        │     │    │     └─ AtlasToolGroupDefinition "left-lower"  LogicalRegion=InlineStartLower
        │     │    │           └─ AtlasToolItemDefinition ×N → problem list / filter facet
        │     │    ├─ AtlasSplitDefinition "editor-right" Axis=InlineChildren
        │     │    │        Basis=FixedPixels(inspector width, SplitAnchor.Second)
        │     │    │        Constraints=(≥280 / 160–420)  [same shape as :69-75]
        │     │    │     ├─ AtlasDocumentGroupDefinition "editor" OverflowMode=Scroll
        │     │    │     │     ├─ AtlasDocumentItemDefinition → YAML editor (one tab per workflow)
        │     │    │     │     └─ AtlasDocumentItemDefinition → DAG canvas  ← see caveat C1
        │     │    │     └─ AtlasToolGroupDefinition "right-upper" LogicalRegion=InlineEndUpper
        │     │    │           └─ AtlasToolItemDefinition → inspector (expression/step detail)
        │     │    └─ (AtlasToolGroupDefinition "right-lower" LogicalRegion=InlineEndLower
        │     │            └─ AtlasToolItemDefinition → scenario timeline)   ← see caveat C2
        │     └─ AtlasSplitDefinition "bottom-dock" Axis=InlineChildren (0.62)
        │           ├─ AtlasToolGroupDefinition "bottom-left"  LogicalRegion=BlockEndInlineStart
        │           │     └─ AtlasToolItemDefinition → matrix table
        │           └─ AtlasToolGroupDefinition "bottom-right" LogicalRegion=BlockEndInlineEnd
        │                 └─ AtlasToolItemDefinition → report summary
        └─ ContentRoutes: one <AtlasContentRoute Kind="…"> per content kind, each ChildContent
               Context="content" rendering the ActionLens component; content components may
               inherit AtlasContentComponent to receive `Content` (AtlasContentContext) —
               [XML] T:Atlas.Blazor.Content.AtlasContentComponent
```

Every group above is a tab container (`AtlasToolGroupDefinition` / `AtlasDocumentGroupDefinition`), every
panel inside it is a tab. This uses all six `LogicalRegion` slots, which is the hard maximum (§3.3), and
the four nested `AtlasSplitDefinition` levels give the left/centre/right/bottom resizability.

Caveats forced by missing API (call these out in the implementation task):

* **C1 — DAG canvas inside an Atlas panel.** The elastic-absorb model (ADR-0021) gives the *document
  group* the elastic track, so putting the canvas in `AtlasDocumentGroupDefinition "editor"` is the only
  way to get "canvas absorbs the space the docks release". The cost: the canvas shares the editor tab
  strip with the YAML editor, so "see YAML and graph at once" requires dragging the tab out into a split
  (public `DockDocumentOperation`), not a fixed side-by-side. There is no public API to dock two document
  groups side by side from the app beyond the definition tree, and I could not confirm from the package
  whether **two** `AtlasDocumentGroupDefinition`s are legal (§9 Q3).
* **C2 — scenario timeline.** A centre-bottom timeline has no natural slot: the only "bottom" regions
  (`BlockEndInlineStart/End`) belong to the full-width bottom dock. Options, in order of fidelity to
  Atlas' model: (a) make it a second tab in the editor document group; (b) put it in
  `BlockEndInlineStart` and move the matrix table to `BlockEndInlineEnd`, dropping the "split bottom
  strip" idea; (c) nest a `BlockChildren` split *under* the editor with a **tool** group tagged
  `LogicalRegion.BlockEndInlineStart`, accepting that its ToolBar entry appears on the bottom strip
  rather than under the canvas. 0.4.0 cannot express a timeline pinned under the canvas and above the
  bottom dock without consuming one of the six regions — this is a gap, not a preference.
* **C3 — collapse affordances.** Splitter collapse buttons come only from
  `AtlasSplitDefinition.CanCollapseFirst/CanCollapseSecond` (declarative) or `SplitterPresentation`
  (programmatic) + `SetGroupVisibilityOperation`. Panel-level "hide" is a tool-group collapse, and an
  emptied Persistent tool group collapses itself (ADR-0022) — do not fight it.
* **C4 — cross-reload layout persistence must be written by ActionLens**: implement
  `IWorkspaceLayoutStore` over `localStorage` (only in-memory stores ship, §3.4), wrap it in
  `LayoutAutoPersistence` with a debounce, and gate restore through
  `LayoutRestoreOptions(isContentRegistered: kind => …, missingContentPolicy: PreservePlaceholder)`
  as in `[SAMPLE]Components\ShowcaseWorkspace.razor.cs:346-348`.
* **C5 — no icon set.** `AtlasIconName` has 5 glyphs (§3.2); ActionLens' own content must ship its own
  SVG/markup icons.
* **C6 — the app must give the workspace a height** (§5) and keep `css/app.css` after the Atlas links.

---

## 9. Open questions the implementation task must answer itself

1. **Does `AtlasWorkspaceHost` need `EnableInteractions="true"` explicitly?** The XML doc says the
   default is already `true` (`[XML]:1267-1272`), but the sample sets it explicitly anyway
   (`[SAMPLE]Components\ShowcaseWorkspace.razor:26`). Checked: the shipped docs. Not checked: whether
   the default flips in some mode combination. Decision: keep it explicit.
2. **Is `atlas.js` imported when `EnableInteractions="false"`?** Checked: the module path string and
   `import` target exist in `[PKG]Atlas.Blazor.dll`; `atlas.js` attaches on `pointerdown` only through
   `AtlasInteractionController`. Not checked: the exact call site conditions — I cannot read the
   0.4.0 method bodies (decompiler unavailable offline; and reading `[ATLAS]src` bodies would be reading
   unreleased code). Verify in the browser (network tab) before shipping.
3. **May the tree contain two `AtlasDocumentGroupDefinition`s?** Checked: `PlacementInvariantRules.cs:22-62`
   only enforces uniqueness of *tool-group* `LogicalRegion` (HEAD source), and `GroupNode`'s
   Document-can't-be-Collapsed rule is per-group (`[ATLAS]docs\zh\adr\0022`). Not checked: any
   validator inside the 0.4.0 assembly (IL bodies are not observable from metadata). Empirical test
   required: build a definition with two document groups and inspect `Factory.Create` result for
   `WorkspaceErrorCode`/`AtlasHostErrorCode.DefinitionInvalid`. If it fails, C1 falls back to a single
   document group.
4. **Does the DAG canvas (SVG/div with pointer + wheel handlers) survive inside a panel?** Checked: the
   controller's hit rules (`atlas.js:207-311, 1638, 1686, 1692`) start a drag only from
   `[data-atlas-drag-kind]`/panel-header/toolbar-entry/splitter/tab-strip elements, and only
   `input,textarea,select,[contenteditable=true]` are exempted. Not determined: whether the root
   `pointerdown` listener or `[data-atlas-dragging] user-select:none` interferes with canvas pan/zoom in
   practice. Browser test required; fallback design is the plain-div canvas discussed in Q3/C1.
5. **Does a `string` literal bind to `WorkspaceId` / `LayoutNodeId` / `DockItemId` parameters?** Checked:
   those structs have no `op_Implicit(string)` in 0.4.0 metadata; Blazor only auto-converts strings for
   enums and a fixed set of framework types. Expected answer: no — use `@(new WorkspaceId("…"))` as the
   sample does (`[SAMPLE]Pages\DeclarativeExample.razor:20`). Confirm with one compile.
6. **Should ActionLens link `themes/ide.css` at all?** Closed part: the floating-window theme surface
   described by `[ATLAS]docs\zh\theming.md:96-99, 132` and `:25` does not exist in the package — grep of
   both `[PKG]staticwebassets\atlas-v2\atlas.css` and `themes\ide.css` returns **0** occurrences of
   `floating`, and neither declares `--atlas-floating-*` / `--atlas-layer-floating` (G8). Open part: the
   packaged `ide.css` sets `background`, `color` and `color-scheme: light dark` on the workspace
   (`:80`), which ActionLens' own panel content will inherit (`theming.md:27`); whether that inheritance
   is acceptable for the DAG canvas and the matrix table is a visual judgement I could not make from the
   sources. Decide with a screenshot, or skip `ide.css` and drive everything from `--atlas-*` overrides.
7. **Where does ActionLens' “share/export” UI live — top toolbar slot or a tool panel?** 0.4.0 has no
   command-palette or floating surface (G1–G6), and `StatusBarContent` is a fixed app-owned strip
   (`[XML]:1197`), so anything beyond the six regions × tabs must either be inside `TopToolbarContent` or
   inside a tab. This is a product decision the missing API does not make for us.
8. **Which `ContentKind` strings are stable enough to persist?** Restore validates registered kinds via
   `LayoutRestoreOptions.IsContentRegistered` and the V2 document stores `ContentReference(Kind,
   ResourceId)`; renaming a kind silently degrades restores to placeholders. Freeze the constant list
   (pattern: `[SAMPLE]Components\ShowcasePanelDescriptor.cs:11-35`) before writing the first test.

---

## 10. Restore / build state of `src/ActionLens.App` (verified on this machine)

* `dotnet build src/ActionLens.App -v q` → **0 warnings, 0 errors** (2.9 s). `dotnet build
  ActionLens.sln` → 0/0. `dotnet format --verify-no-changes` → exit code 0.
* **Atlas.Blazor resolves.** `src/ActionLens.App/obj/project.assets.json` contains
  `Atlas.Blazor/0.4.0` with dependency `Microsoft.AspNetCore.Components.Web 10.0.14` and compile+runtime
  assets for **both** `lib/net10.0/Atlas.Blazor.dll` and `lib/net10.0/Atlas.Core.dll`. No extra
  `Atlas.Core` reference is needed or wanted (`[ATLAS]docs\zh\adr\0019` decision 1–3).
* **Central package management is active**: `Directory.Packages.props` sets
  `ManagePackageVersionsCentrally=true` **and `CentralPackageTransitivePinningEnabled=true`**, with
  `<PackageVersion Include="Atlas.Blazor" Version="0.4.0" />`; the App's `<PackageReference
  Include="Atlas.Blazor" />` carries no version (correct under CPM). Because transitive pinning is on, a
  future Atlas that requires a newer `Microsoft.AspNetCore.Components.Web` than the pinned
  `10.0.14` (`Directory.Packages.props`, shared with the WebAssembly/DevServer packages) will surface as a
  restore-time pin error, not a silent upgrade. No transitive pin entry for Atlas types is needed:
  `Atlas.Core` is embedded, so it never enters the dependency graph.
* **Static web assets already work.** `src/ActionLens.App/bin/Debug/net10.0/
  ActionLens.App.staticwebassets.runtime.json` registers content root
  `C:\Users\World\.nuget\packages\atlas.blazor\0.4.0\staticwebassets\` and the four assets under
  `_content/Atlas.Blazor/atlas-v2/…`; the endpoints file lists both the plain and fingerprinted URLs
  (`atlas.css`, `atlas.css.gz`, `atlas.0hvbj1zllf.css`, `atlas.js`, `themes/ide.css`, …). **Nothing to add
  to the csproj** — `buildTransitive/Atlas.Blazor.props` does it. No `dotnet ef`, no npm/Node tooling,
  no `BlazorWebAssembly*` properties are required for these assets.
* The App is still the `blazorwasm --empty` template: `Program.cs` does **not** call
  `AddAtlasWorkspace()`, `_Imports.razor` has no Atlas `@using`, `App.razor` uses
  `MainLayout`/`NotFoundPage`, and `wwwroot/index.html` lacks the two Atlas `<link>` tags and contains a
  `<script type="importmap"></script>` plus `blazor.webassembly#[.{fingerprint}].js` (the csproj sets
  `OverrideHtmlAssetPlaceholders=true`). Keep the un-fingerprinted `_content/Atlas.Blazor/atlas-v2/atlas.js`
  reachable — the library imports that literal path at runtime; the fingerprinted placeholders in
  `index.html` are optional and only affect the tags in that file.
* Files I did not touch and that must not be touched: everything under `C:\Users\World\source\repos\Atlas`
  (read-only reference; note its `samples\Atlas.Showcase\bin\` is a `HEAD` build and is **not** evidence
  about 0.4.0). This repository has no `.git` directory, so there is nothing to commit — `git status`
  here fails with `fatal: not a git repository`.
