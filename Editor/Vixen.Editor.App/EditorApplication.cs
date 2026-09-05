// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Audio.Ecs;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Debugger;
using Vixen.Editor.Inspector;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.HotReload;

// ⚠ Aliased, because this file also names `Vixen.Engine.Scenes.Prefab` — the runtime's captured
// `World`, which shares a word with the editor's prefab and is not related to it. Doc 47 § 1 opens by
// saying that conflating the two is the first way to get prefabs wrong here; the compiler says the
// same thing as CS0104 the moment both usings are in one file.
using EditorPrefab = Vixen.Editor.AssetEditors.Prefabs.Prefab;
using PrefabSource = Vixen.Editor.AssetEditors.Prefabs.PrefabSource;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.App;

/// <summary>The editor as an application: a project, a scene, which panels exist, and what persists.</summary>
/// <remarks>
///     <para>
///         <b>The half of the editor that is not chrome.</b> <see cref="EditorShell" /> is a menu
///         bar, a docking workspace, a palette and a status bar with nothing in them;
///         <see cref="EditorHost" /> is a window, a device and a frame loop. This is what goes in the
///         panels and what the panels are looking <i>at</i> — which is the part a game team would
///         fork and the other two are not.
///     </para>
///     <para>
///         <b>It owns a world, and that is not the contradiction it looks like.</b>
///         <c>Program</c>'s remarks say the editor's loop is an interface and has no world. It still
///         does: while the editor is editing, nothing here ticks systems, runs a fixed step or
///         updates behaviours. The world is a <i>document</i> — the thing the hierarchy lists, the
///         inspector edits and the gizmo drags — and it starts being a running game only when play
///         mode says so, which since 2026-08-21 it can: <c>PlayModeController.Tick</c> steps an
///         <c>EngineLoop</c> over this world and <see cref="Update" /> is where it is called.
///     </para>
///     <para>
///         ⚠ <b>And it owns a simulation for exactly as long as a session lasts.</b>
///         <see cref="PlayPhysics" /> is this application's <c>IPlaySystems</c> contribution: a
///         <c>PhysicsScene</c> over the world above, built when Play is pressed and disposed when
///         Stop is. Nothing simulates while the editor is editing — a body falling under a gizmo drag
///         is a scene that edits itself — and a scene that outlived the session would be a native
///         world handed to every later reader of a bag that has no removal.
///     </para>
///     <para>
///         ⚠ <b>Every selection is polled once a frame rather than subscribed to.</b>
///         <c>Selection&lt;T&gt;</c> is signal-backed, and an <c>Effect</c> over it would be the
///         better wiring — but nothing in this loop flushes the reactive scheduler, and adding one
///         changes the loop's contract for notifications and background tasks too. A comparison of a
///         handful of handles once a frame is not a cost, and it is honest about what is here.
///     </para>
///     <para>
///         ⚠ <b>A panel's factory runs again when it is reopened</b>, so nothing durable may live in
///         one. The camera is kept as a <see cref="ViewBookmark" /> on this object and restored when
///         the scene panel is rebuilt; without that, closing and reopening the viewport would put the
///         user back at the origin.
///     </para>
/// </remarks>
sealed partial class EditorApplication : IDisposable {
    /// <summary>What the folder holding plugins is called, in both places it is looked for.</summary>
    const string PluginsFolder = "Plugins";

    readonly EditorUserStore store;
    readonly World world = new("Editor");
    readonly EditorProject project;

    /// <summary>The scene every command acts on.</summary>
    /// <remarks>
    ///     ⚠ <b>Not readonly, because doc 20's multi-scene row makes it change.</b> Half the editor
    ///     holds the active scene — the outliner, the gizmo, the inspector, the picker — so making
    ///     one of several open scenes active is an assignment to the field they all already read,
    ///     rather than an index every one of them would have to learn about. See
    ///     <see cref="SetActiveScene" />.
    /// </remarks>
    SceneDocument scene;

    /// <summary>What the host can do that this cannot ask for itself: pickers, and a browser.</summary>
    readonly EditorServices services;

    /// <summary>Where a native dialog's answer waits for the frame thread.</summary>
    readonly Deferred deferred = new();

    /// <summary>Where the user's layouts, keymap and preferences live.</summary>
    readonly string dataDirectory;

    /// <summary>Snapshot, restore, and what state the transport is in.</summary>
    readonly PlayModeController play;

    /// <summary>The ring the console reads, and what puts the editor's own messages in it.</summary>
    readonly EditorLog log = new();

    /// <summary>The console panel while it is open, or <see langword="null" />.</summary>
    ConsoleView? console;

    /// <summary>What it is showing, which outlives the panel.</summary>
    ConsoleModel? consoleModel;

    /// <summary>Whether the save-on-close prompt is already on screen.</summary>
    bool closing;

    /// <summary>What a click in the viewport is answered by.</summary>
    /// <remarks>
    ///     Held here rather than made in the panel's factory, because that factory runs again every
    ///     time the panel is reopened and this caches a mesh per shape kind — which is geometry that
    ///     never changes and would otherwise be rebuilt every time somebody closed the scene tab.
    /// </remarks>
    ScenePicker picker;
    readonly MeshEdit editing;

    /// <summary>What an asset field's button opens.</summary>
    readonly AssetPicker assetPicker = null!;

    /// <summary>What an asset dragged out of the browser and over an inspector field lands in.</summary>
    readonly AssetFieldDrop assetDrop = null!;

    /// <summary>Whether a pointer is currently down in the project browser.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes dragging an asset into an inspector field possible at all.</b> See
    ///     <see cref="ProjectBrowser.Grabbing" />: pressing a row selects the asset, a selected asset
    ///     wins the inspector from whatever entity had it, and the field the drag was aimed at is
    ///     gone several frames before the drag has even begun. <see cref="FollowSelection" /> is held
    ///     off for the length of the gesture, on the rule that a drag is not a click.
    /// </remarks>
    bool grabbingAssets;

    /// <summary>Pictures of assets for the browser's grid, decoded off the frame thread.</summary>
    readonly ThumbnailCache thumbnails;

    /// <summary>The project's own code, once it has been built and loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>Loaded once, at start-up, and never unloaded.</b> The context is collectible and
    ///     nothing calls <c>Unload</c>, because the registries a load fills have no way to empty —
    ///     see <c>ProjectAssemblies</c>. A project rebuilt while the editor is open therefore needs a
    ///     restart, which is said out loud rather than half-built.
    /// </remarks>
    readonly ProjectAssemblies code;

    /// <summary>What tells the editor that something outside it changed the assets on disk.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The panel's own remarks used to say why there wasn't one</b> — "a watcher is worth
    ///         having and is not free: it needs debouncing, a rename heuristic and a way to not fight
    ///         the editor's own writes". All three of those are <c>Vixen.Core.IO</c>'s and have been
    ///         since <c>FileChangeCoalescer</c> was written; what was missing was not the mechanism
    ///         but the wire. Saving a texture from another program left the Project panel showing the
    ///         project as it was when the editor started.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null when there is no assets directory</b>, which a scratch project opened before
    ///         anything has been saved genuinely has. A missing folder is not an error worth refusing
    ///         to start over — it is a project with nothing in it yet.
    ///     </para>
    /// </remarks>
    readonly IFileWatcher? watcher;

    /// <summary>What the watcher has to say, reused so that a quiet frame allocates nothing.</summary>
    readonly List<FileChange> changes = [];

    /// <summary>What carries a change on disk the last few metres, from the project to the open document.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the watcher that was never wired.</b> Everything else on this path reads
    ///     the drained list for its length: the browser rescans, the database rescans, the build panel
    ///     refreshes. <c>ReloadShaders</c> is the one exception and it filters to <c>.rvn</c> — so a
    ///     <c>.vxscene</c> or a <c>.vxcompositor</c> saved by another program reached the tree, the
    ///     index and the build, and did not reach the panel that had it open.
    /// </remarks>
    readonly ExternalEdits external;

    /// <summary>What a change on disk did to the open documents, for the suite that asserts about it.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it is the only honest instrument for "did the watcher reach this
    ///     document".</b> Every other observable is a proxy that something else also moves:
    ///     <c>StandardFrameDocument.Changed</c> is raised by the inspector's own write-back, and
    ///     <c>EditorDocument.IsStale</c> is <em>cleared</em> by a successful reload — so a test
    ///     watching either can be told a reload happened when none did, or told none happened when
    ///     one did. <see cref="ExternalEdits.Applied" /> fires exactly once per document a change
    ///     reached, whatever the outcome.
    /// </remarks>
    internal ExternalEdits External => external;

    readonly ContentTasks content;

    /// <summary>What puts doc 48 § D12's baked mesh maps into the project as ordinary assets.</summary>
    /// <remarks>
    ///     ⚠ <b>Held as well as published, because it has two callers and they are different
    ///     shapes.</b> A plugin resolves <see cref="IMeshMapBaker" /> out of the service list, the
    ///     way the block-out module resolves <c>IMeshBaker</c>; the editor's own Bake Mesh Maps verb
    ///     hands this to <c>ContentTasks</c>, which does the arithmetic on a pool thread and calls
    ///     <c>Write</c> back on the frame thread. Neither could reach it through the other.
    /// </remarks>
    readonly ProjectMeshMapBaker meshMaps;

    /// <summary>The project's own content, opened the way a player opens a build.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="EditorContent" /> had no caller outside its own tests.</b> It is what turns
    ///     the artefacts an import left in <c>Library/</c> into an <c>AssetManager</c>, and an
    ///     <c>AssetManager</c> is the single argument <c>WorldRenderer.Mount</c> takes — so without one
    ///     the viewport had no material source at all and drew every mesh in the grey fallback,
    ///     whatever material it named. See <see cref="MountContent" />.
    /// </remarks>
    readonly EditorContent projectContent;

    /// <summary>Where a viewport reads the geometry a scene's mesh references name.</summary>
    /// <remarks>
    ///     <b>The join that made <c>MeshRenderable</c> visible in the editor.</b> Picking and gizmos
    ///     rendered and geometry did not — the collector had no way to turn a reference into vertices,
    ///     so a level of authored meshes was a viewport of nothing while the same scene drew correctly in
    ///     a game. It is the project's import cache rather than a content build, because waiting for a
    ///     build to look at a level would make the viewport a function of the build rather than the
    ///     files.
    /// </remarks>
    internal Vixen.Editor.Assets.Content.ProjectMeshSource SceneGeometry => content.Meshes;

    /// <summary>Where a viewport reads the look of the materials a scene's entities name.</summary>
    /// <remarks>
    ///     <b>The join that made <c>MeshRenderable.Material</c> visible in the editor</b>, on
    ///     <see cref="SceneGeometry" />'s terms and for the same reason. Assigning a material and seeing
    ///     nothing happen is the shape of defect that sends an author to the game to find out whether
    ///     the assignment took.
    /// </remarks>
    internal Vixen.Editor.Assets.Content.ProjectSurfaceSource SceneSurfaces => content.Surfaces;
    readonly PluginHost plugins;

    /// <summary>The plugin host, and through it what this editor publishes to a plugin.</summary>
    /// <remarks>
    ///     Internal, for the harness. What a test needs it for is asserting an extension <i>point</i>
    ///     — that a service is published at all — which is a different claim from asserting what some
    ///     plugin did with it, and there is no other way to ask.
    /// </remarks>
    internal PluginHost PluginHost => plugins;

    /// <summary>The content browser while the Project panel is open, or <see langword="null" />.</summary>
    /// <remarks>
    ///     Internal, for the harness, and for one thing it cannot get any other way: the browser is a
    ///     plain class over two elements rather than an element itself, so a walk of the panel's tree
    ///     finds the tree and the grid and nothing that knows which of the two is showing.
    /// </remarks>
    internal ProjectBrowser? Browser => browser;

    /// <summary>Where the open scene is written, which Save As moves.</summary>
    string scenePath;

    /// <summary>What each open scene had selected when the inspector was last brought up to date.</summary>
    /// <remarks>
    ///     One entry per open scene rather than one list, because the editor's own scene is not the
    ///     only one with a selection: a scene or a prefab opened as an asset has a hierarchy of its
    ///     own, and a click in it is what this is here to notice.
    /// </remarks>
    readonly Dictionary<SceneDocument, List<Entity>> watched = [];

    /// <summary>And the same for the project browser's.</summary>
    readonly List<AssetId> shownAssets = [];

    /// <summary>The open scenes, rebuilt in place once a frame rather than allocated.</summary>
    readonly List<SceneDocument> scenes = [];

    readonly AssetEditorRegistry editors;
    readonly HashSet<string> assetPanels = new(StringComparer.Ordinal);

    /// <summary>What turns doc 34's asset paths into rigs, shape sets and scenes.</summary>

    /// <summary>The one system the editor runs, and <see cref="ResolveTransforms" /> says why.</summary>
    readonly TransformSystem transforms = new();

    /// <summary>The panes the scene is drawn in, or <see langword="null" /> while the panel is closed.</summary>
    ViewportLayout? viewports;

    /// <summary>How the scene panel is split, kept across a panel rebuild.</summary>
    ViewportArrangement arrangement = ViewportArrangement.Single;

    /// <summary>Where each pane's camera was, so reopening the panel does not go back to the origin.</summary>
    /// <remarks>
    ///     ⚠ <b>One per pane and cleared when the arrangement changes.</b> A saved camera restored
    ///     into a freshly-split layout would overwrite <c>ViewportLayout</c>'s top/front/side presets
    ///     with three copies of wherever the single pane happened to be looking — which is a four-pane
    ///     layout that comes up as four identical perspective views, the exact thing the presets exist
    ///     to prevent.
    /// </remarks>
    readonly ViewBookmark?[] cameras = new ViewBookmark?[4];

    /// <summary>What answers "what is under this ray" for placement and snapping.</summary>
    /// <remarks>
    ///     Held here for <see cref="picker" />'s reason: it caches a mesh per shape kind, and a panel's
    ///     factory runs again every time the panel is reopened.
    /// </remarks>
    SceneProbe probe;

    /// <summary>What every gizmo and every drop in the editor rounds to, as one thing.</summary>
    /// <remarks>
    ///     ⚠ <b>One per editor rather than one per pane, and doc 24's D4 is the argument.</b> Snapping
    ///     is a claim about how the user is working, not about which pane they are looking through —
    ///     a four-pane layout whose panes disagreed about whether vertex snapping was on is the same
    ///     confusion as a vertex snap that works for a drag and not for an extrude, in another dress.
    ///     It is handed to every pane's gizmo, every pane's placement and, when they exist, every
    ///     blockout tool.
    /// </remarks>
    readonly SnapContext snap = new();

    /// <summary>Where the designer is building: the grid every pane draws and everything lands on.</summary>
    /// <remarks>
    ///     ⚠ <b>One per editor, for <see cref="snap" />'s reason.</b> Doc 24's D5 is that the work
    ///     plane is the thing you move onto a wall and then build in; four panes disagreeing about
    ///     where that is would make "on the grid" mean four things at once. It is also where the snap
    ///     context reads its step from, so the grid you can see and the grid you snap to are one
    ///     number.
    /// </remarks>
    readonly WorkPlane plane = new();

    InspectorView? inspector;

    /// <summary>The component foldouts under the inspector, while its panel is open.</summary>
    ComponentsView? components;

    /// <summary>Which components and behaviours the editor can show.</summary>
    /// <remarks>
    ///     <para>
    ///         A list rather than a snapshot, so a subsystem or a plugin that registers something
    ///         after the editor is up is offered — see <c>ComponentsView.Registered</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The behaviour half is asked for the <i>current</i> document's store.</b> A
    ///         component lives in a world and a behaviour lives in a <c>BehaviorStore</c> beside it,
    ///         and that store belongs to a document — so this hands over a way to find whichever one
    ///         is open rather than a store, which would be the scene the editor started with for ever.
    ///     </para>
    /// </remarks>
    readonly IReadOnlyList<IComponentBridge> bridges;

    /// <summary>The live components of this editor's interface, and what an edit to a .vxml reaches.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 36 § P4. This existed in <c>Core/Vixen.Ui.HotReload</c> and nothing in the
    ///         editor had ever created one</b> — so the declarative path was reloadable in principle
    ///         and not in this application, which is F7 with an extra step. A markup panel mounted
    ///         through it is rebuilt when <c>dotnet watch</c> replaces the <c>Build</c> body the
    ///         markup compiled to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And "mounted through it" is a thing every markup panel has to actually be.</b>
    ///         The shell's task centre was built before this field existed — it could not be
    ///         otherwise, since the shell's constructor is what makes the document this is built over
    ///         — so the editor's one <c>.vxml</c> panel was the one panel outside the channel.
    ///         <c>EditorShell.RemountTaskCenter</c> is the second step, taken below.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Registered with <c>MetadataUpdate</c>, which holds it weakly.</b> The runtime's
    ///         callback is static and has no idea when a window closes; a strong list would be a leak
    ///         with a development-only cause and a production-shaped consequence.
    ///     </para>
    /// </remarks>
    readonly HotReloadHost hotReload;

    /// <summary>The dev-mode stylesheet watcher, or <see langword="null" /> outside it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null unless <c>--hot-reload</c> named a directory, which is the whole of its
    ///         opt-in.</b> A <c>FileSystemWatcher</c> the editor opened by itself would be a handle
    ///         and a pool callback in every CI run of <c>--frames N</c>, for a channel that run
    ///         cannot use.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It can be pointed at the editor's own chrome, and an edit there now
    ///         <i>replaces</i> the shipped sheet rather than layering over it.</b>
    ///         <c>Theming/EditorTheme.vcss</c> is a real file since doc 43's <c>@theme</c> work —
    ///         before it there was no <c>.vcss</c> in the tree at all and this channel had nothing to
    ///         watch. What it used to do with one was load it again at <c>Author</c> origin on top of
    ///         the <c>UserAgent</c> copy embedded from the same file, which made changed values live
    ///         and a <b>deleted</b> rule immortal: the copy underneath still had it. The watcher
    ///         recognises a file the document already holds and binds it to that sheet instead — see
    ///         <c>HotReloadWatcher.Load</c>. A file that matches nothing is still an overlay, because
    ///         an overlay is what a scratch directory of overrides is.
    ///     </para>
    ///     <para>
    ///         A published editor still carries the sheet in its assembly and has no file to watch,
    ///         which is why the switch names a directory and defaults to nothing.
    ///         See <c>Editor/Vixen.Editor.Host/README.md</c>.
    ///     </para>
    /// </remarks>
    HotReloadWatcher? styleWatcher;

    readonly List<ReloadReport> styleReloads = [];

    /// <summary>Every stylesheet reload the watcher has reported, in the order it reported them.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept so that a test can wait on the reload rather than on the clock.</b> A save is
    ///     delivered by the platform on its own schedule and applied by whichever frame comes after,
    ///     so "did the watcher act on it yet" has no answer a frame count can give — and the claim
    ///     that interests <c>HotReloadModeTests</c> most is a negative one, that a <i>broken</i>
    ///     sheet changed nothing. A negative claim waited out on a fixed span passes on a machine
    ///     where the save never arrived at all, which is a test that reports coverage it does not
    ///     have. This is the counter that tells the two apart.
    /// </remarks>
    internal IReadOnlyList<ReloadReport> StyleReloads => styleReloads;

    TreeView? hierarchy;
    ContextMenu? hierarchyMenu;
    ContextMenu? assetMenu;
    ProjectBrowser? browser;
    bool hierarchyStale = true;

    /// <summary>What the outliner's filter box says, or <see langword="null" /> for no filter.</summary>
    /// <remarks>
    ///     Held here rather than read from the box, because a panel's factory runs again when it is
    ///     reopened — so a filter kept in the control would be silently forgotten by closing the
    ///     panel, which looks like the outliner spontaneously showing rows that were hidden.
    /// </remarks>
    string? hierarchyFilter;

    /// <summary>Whether the tree is being told about the selection rather than reporting one.</summary>
    bool hierarchyEchoing;

    /// <summary>How the outliner's siblings are ordered.</summary>
    /// <inheritdoc cref="hierarchyFilter" select="remarks" />
    string hierarchyOrder = OutlinerOrders[0];

    /// <summary>The orders the outliner offers, the first being the one it opens with.</summary>
    /// <remarks>
    ///     ⚠ <b>Hierarchy order is first and is the default.</b> It is the only one that carries
    ///     information the others destroy — the order of siblings is something the user arranged, and
    ///     a name sort on by default would hide that permanently and look like the arrangement not
    ///     having been saved.
    /// </remarks>
    static readonly string[] OutlinerOrders = ["Hierarchy order", "Name (A–Z)", "Name (Z–A)"];

    /// <summary>Whether the inspector is showing the project's selection rather than a scene's.</summary>
    bool inspectingAssets;

    /// <summary>Which scene's selection it is showing, or <see langword="null" /> for this editor's own.</summary>
    SceneDocument? inspected;

    /// <summary>Builds the editor's interface into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="directory">Where the user's layouts, keymap and preferences live.</param>
    /// <param name="projectRoot">The project to open, or <see langword="null" /> for a scratch one.</param>
    /// <param name="services">
    ///     What the host can do — file pickers, a browser — or <see langword="null" /> for none, which
    ///     is what a test and a headless run get. The commands that need them grey themselves out
    ///     rather than being absent.
    /// </param>
    public EditorApplication(
        float width,
        float height,
        string directory,
        string? projectRoot = null,
        EditorServices? services = null,
        IEditorRegistry? extensions = null,
        IReadOnlyList<(string Id, string Name, IEditorPlugin Module)>? modules = null
    ) {
        store = new EditorUserStore(directory);
        dataDirectory = directory;
        this.services = services ?? EditorServices.None;
        Extensions = extensions ?? EditorRegistry.Default;
        this.modules = modules ?? [];

        // ⚠ The registered module, not one of our own. See EditorDiagnostics for what a second
        // instance costs.
        diagnostics = FindDiagnostics(this.modules);
        assetEditors = FindModule<AssetEditorsModule>(this.modules);

        // ⚠ Before the project, because whether this run is a first one — no history at all — is
        // what decides whether the startup Project Browser has anything to offer, and opening the
        // project is what adds this one to the list.
        Recent = new ProjectHistory(directory);
        IsScratch = projectRoot is null;

        // ⚠ The logger is the point of the third argument: `UiDocument` reports every stylesheet rule
        // it had to drop, and a document handed none reports them into `NullLogger`. Filed under the
        // cascade's own category rather than `EditorLog.Category`, so that "the styling is wrong" and
        // "the editor said something" are separable in the console's category filter. The three
        // user-agent sheets are installed inside this constructor, so the logger has to arrive with
        // it rather than be set afterwards.
        Shell = new EditorShell(width, height, logger: log.Sink.CreateLogger("Vixen.Ui.Styling"));

        // ⚠ Before anything that could notify, which on a first run is the project scan and the
        // plugin loader. A mirror attached afterwards would miss exactly the messages somebody opens
        // the console to read: the ones from start-up.
        log.Mirror(Shell.Notifications);

        // ⚠ The fourth user-agent sheet, and it is the application that loads it. `EditorShell` has
        // the three that draw the chrome and cannot have this one: it is deliberately a shell that
        // knows nothing about inspectors, and the panel it hosts is this assembly's choice. Loaded
        // after those three, so a rule of the same specificity here wins — which is what lets it
        // narrow `expander-content`'s indent and a field's background without out-specifying them.
        InspectorTheme.Install(Shell.Document);

        // ⚠ And the fifth, after it, for the same reason: the asset editors' own elements are styled
        // against the tokens the four below declare, and a rule of equal specificity here has to win
        // — an override matrix's cell is a row inside an inspector-shaped panel.
        AssetEditorTheme.Install(Shell.Document);

        // And the browser's two rules, which are this assembly's panel and nobody else's business.
        BrowserTheme.Install(Shell.Document);

        // And E5's world-building panels', on the same terms.
        WorldTheme.Install(Shell.Document);

        // A scratch project under the user's data directory, so a first run with no arguments opens
        // something real rather than refusing to start. `Open` tolerates a missing Assets directory
        // — see AssetDatabase.Scan — which is what makes a directory that does not exist yet fine.
        project = new EditorProject(new ProjectPaths(projectRoot ?? Path.Combine(directory, "Scratch")));
        project.Open();

        // One scene per project until there is a file dialog to choose another. The path is decided
        // here rather than by the document, because where a scene lives is the shell's answer and a
        // document only knows how to write itself.
        scenePath = Path.Combine(project.Paths.Assets, "Scenes", "Main" + SceneSerializer.Extension);

        scene = new SceneDocument(project, world, AssetId.Empty, "Main") {
            Writer = new SceneFileWriter(scenePath)
        };

        project.Activate(scene);
        picker = new ScenePicker(scene);
        probe = new SceneProbe(scene);

        // ⚠ One per editor and handed to every pane, for the reason `snap` is: selecting a face in the
        // perspective view and seeing it highlighted in the top view is what every reference toolset
        // does, and four of these would be four selections of one mesh with nothing reconciling them.
        editing = new MeshEdit(scene);

        // ⚠ Handed the document's behaviour store, and a `PlayModeController(world)` would be a Play
        // button that steps the graph and runs none of the scripts — the failure this whole wiring
        // exists to remove, in its quietest form. The store is the first document's: the controller
        // names anything it cannot take over, which is how a behaviour authored into an additively
        // opened scene stops being a script that silently does not run.
        play = new PlayModeController(world, scene.Behaviors, Extensions);

        // ⚠ The editor's own contribution to the frame a session runs, and the one service this
        // application can honestly own. Doc 31 § D10 said an embedding host would have to add the
        // four physics passes by hand; it does not, because `IPlaySystems` is read at every Play.
        //
        // ⚠ This comment used to go on "and this is registered before any module activates — which
        // is also what lets the terrain module's collider contribution find the scene this one
        // provides". That dependency is now declared: `PlayPhysics` carries
        // `[Provides(typeof(PhysicsScene))]` and the collider contribution `[RunsAfter]` for the
        // same type, so the sequence of these two lines in two assemblies no longer decides it.
        contributions.Add(Extensions.Add<IPlaySystems>(new PlayPhysics()));

        // ⚠ Every entity gets a *new handle* when a play-mode snapshot is restored, so the
        // document's name and stable-id tables — both keyed by handle — name nothing at all
        // afterwards. `SceneDocument.Remap` was written for exactly this and nothing called it: the
        // outliner came back from play mode as a list of blank rows, which reads as the scene having
        // been lost. Subscribed here rather than in `LeavePlay` so that it also covers a restore the
        // controller performs for any other reason.
        play.Restored += (_, translation) => {
            scene.Remap(translation);
            hierarchyStale = true;
        };

        // ⚠ Not only after this panel's own commands. An *undo* of "remove component" puts the
        // column back with nothing having been clicked, so a view that rebuilt only on its own
        // edits would show a component that is gone and hide one that is back.
        scene.ComponentsChanged += (_, changed) => {
            if (components?.Entity == changed) {
                components.Rebuild();
            }

            // ⚠ And the outliner, whose row glyph is *what the entity carries* — see `GlyphFor`. It
            // was subscribed to the structure and the rename and to nothing else, so adding a light
            // to an entity left the row drawing the plain dot until something unrelated rebuilt the
            // tree. The remark on `GlyphFor` asserted this already happened; it did not.
            hierarchyStale = true;
        };

        // ⚠ Before the scene is read, and that ordering is the whole of it. These used to be
        // registered a hundred lines further down, beside the other producer-1 contributions, and
        // touched later still — by `ComponentsView.Default`, whose remark says "before the first
        // read" and meant the Add Component menu's. A *scene file* is read before either, so a
        // project whose `Main.vxscene` named a component from a subsystem nothing had happened to
        // load did not open at all: `SceneComponentRegistry` had never heard of the alias and the
        // editor died on the way up with "Nothing in this build claims the name". Registered here
        // and touched here, so the two reads happen in the order the failure requires.
        foreach (var subsystem in BuiltInSubsystems) {
            contributions.Add(Extensions.Add(subsystem));
            subsystem.Touch();
        }

        if (SceneSerializer.Load(scene, scenePath) == 0) {
            Seed();

            // ⚠ Written immediately, which is the one time the editor saves without being asked. A
            // new project should contain the scene you are looking at rather than something that
            // exists only until the window closes — and it makes the *second* launch take the load
            // path, which is otherwise reachable only by remembering to press Save first.
            scene.Save();

            // ⚠ And scanned again, because the file was written *after* `Open` indexed the project.
            // Without this a first run shows a project browser with no scene in it — the one file the
            // editor is certain exists, because it just made it.
            project.Assets.Scan();
        }

        scene.StructureChanged += _ => hierarchyStale = true;
        scene.Renamed += (_, _) => hierarchyStale = true;

        // ⚠ One world for every scene this editor opens and a fresh one per prefab. Sharing the
        // editor's world between scenes is what makes an entity handle mean one thing across the
        // application; a prefab must not share it, because "isolated" is exactly the claim that its
        // entities are not in the level — see PrefabEditorFactory.
        editors = StandardEditors.CreateDefault(_ => world, _ => new World("Prefab"));

        // ⚠ The one document kind the *viewport* is a view of, so opening it points the panes at it
        // and every edit after that reaches them without a restart. Through `Opened` rather than a
        // line in `Open`, because that event is already the seam a module hears a document on — and
        // it deliberately does not fire for a document that was already open, which is right here:
        // re-authoring is what `StandardFrameDocument.Changed` is for.
        editors.Opened += document => {
            if (document is AssetEditors.Frame.StandardFrameDocument authoredFrame) {
                Author(authoredFrame);
            }

            // The other document kind that needs something only the host has. A graph opened before
            // there is a device gets it from `ShaderGraphPreviews`' setter instead.
            if (document is AssetEditors.Shading.ShaderGraphDocument shader) {
                Preview(shader);
            }

            // ⚠ A scene reconciles itself against its prefabs as it is read — `SceneSerializer.Open`
            // — and has nobody to tell. Everything a reconcile could not settle is a *report* and
            // never a deletion, which is exactly why it has to reach a person: the entity the template
            // no longer has is still in the level, waiting for somebody to decide between unpack and
            // delete. See docs/plan/47 § 5.
            if (document is SceneDocument opened) {
                Announce(opened);
            }
        };

        // ⚠ The scene the editor started with is the first entry of the multi-scene list rather
        // than a special case beside it. Everything that walks the open scenes — the panel, Save All
        // Scenes, the active-scene switch — would otherwise have to remember that one of them is not
        // in the list, which is exactly the kind of exception that goes wrong once and quietly.
        openScenes.Add(new(scene, scenePath) { Settings = WorldSettings.Load(scenePath) });
        world0 = openScenes[0].Settings;

        if (editors.TryGetByName("Addressable Group", out var groups)
            && groups is AddressableGroupEditorFactory addressable) {
            // The real planner, run against a workspace of its own. `ProjectWorkspace` opens the four
            // stores that have to agree about which directory they are looking at, and it must not
            // share the editor's database — `Scan` clears and repopulates its dictionaries, which is
            // the race ContentTasks already documents.
            addressable.Analyser = AnalyseContent;
        }

        // ⚠ A sequence drives the scene the editor has open, and which scene that is is this class's
        // arbitration in the way every other panel's subject is — see `EditorWorlds`. A factory that
        // reached for one would be a factory that knows what the editor has open.
        if (editors.TryGetByName("Sequence", out var sequences)
            && sequences is AssetEditors.Sequencing.SequenceEditorFactory sequencer) {
            sequencer.Scene = () => scene;
        }

        thumbnails = new ThumbnailCache(project);
        watcher = Watch(project);

        // ⚠ Constructed even when there is no watcher, because the object is what subscribes to
        // `EditorProject.DocumentSaving`, and a project with no watcher still has documents that
        // save. What a null watcher costs is the suppression, not the wiring — and a conditional
        // here would be a second place that has to know that.
        external = new(project, watcher);
        external.Applied += Announce;

        // ⚠ `BuiltInSubsystems` is registered and touched *above*, before the scene file is read —
        // see there. Doc 36 § D5 retires `ComponentsView.Prime` — three hardcoded
        // `RunModuleConstructor` calls inside the panel, which was F11's "a list, in the
        // application, of which subsystems exist". This is still a list and it is still the
        // application's; what changed is that it is a contribution, so a module can add to it and a
        // plugin's own runtime assembly can be declared by whoever shipped it.
        bridges = ComponentsView.Default(() => scene?.Behaviors, Extensions);
        code = new ProjectAssemblies(project.Paths);

        // ⚠ Before the tasks, because one of their hooks closes over it. A project that has never
        // been imported opens as a refusal rather than an exception — see the type — so this is
        // never null and `Assets` is what says whether there is anything to mount.
        projectContent = new(project);
        meshMaps = new(project);

        content = new(project, Shell) {
            // The panel's own rescan, so the browser shows what an import repaired rather than what
            // was there before it ran. Assigned rather than called by the tasks directly, because
            // the browser exists only while its panel is open.
            //
            // The build panel goes with it: its scene picker is a view of the same database, so a
            // scene that arrived in an import belongs in the list of what can be put in a build.
            Rescan = () => {
                browser?.Rescan();
                RefreshBuildPanel();

                // ⚠ And the viewport's own content, which is the frame-thread half of what
                // `Cataloged` began on the pool. A mount hands the renderer a whole new material and
                // texture source, so it belongs where the panels are touched rather than beside the
                // planner that wrote the catalog.
                MountContent();
            },

            // ⚠ On the pool. See the property: writing a catalog plans the whole project.
            Cataloged = () => projectContent.Rebuild(),

            // And its two buttons are greyed while anything is running, which is a fact about the
            // task rather than about anything the panel did.
            BusyChanged = RefreshBuildPanel
        };

        // ⚠ Before the panels, because the inspector's asset fields are built by drawers that have
        // to be pointed at a project first. `AssetDrawer` has raised `PickRequested` since it was
        // written and nothing ever listened, so the button in an asset field did nothing at all.
        // ⚠ The cache and the registry as well as the project, because the picker draws tiles now.
        // `thumbnails` is built above precisely so this line can have it: a picker handed no cache is
        // a grid of type glyphs, which is what a headless run and every test see — and the glyph says
        // what kind of thing a file is where a picture says which one, which is the whole point of a
        // picker over a list.
        assetPicker = new AssetPicker(project, Shell.Dialogs, thumbnails, Extensions);

        // The other way to fill an asset field, over the same answer about what each one takes: a
        // drop path with its own opinion is one that accepts what the picker would never have listed.
        assetDrop = new AssetFieldDrop(assetPicker);

        foreach (var drawer in DrawerRegistry.Default.Drawers.OfType<AssetDrawer>()) {
            drawer.Resolve = assetPicker.NameOf;
            drawer.PickRequested += assetPicker.Open;
        }

        Panels();

        SettingsPanels();
        BuildPanels();

        // And E5's four, for the same reason: the Sequencing preset names the scene list.
        WorldPanels();


        // ⚠ Built before the commands rather than after them, which is a change doc 36 § P3 forced.
        // `RegisterModes` activates the built-in modules, and a module's mode has to land on the mode
        // bar in the place the editor ships it in — Blockout second, before Terrain. Only the
        // *loading* of third-party plugins has to wait, and it still does: see `StartPlugins`.
        // ⚠ Before `PluginPoints`, which publishes it. A module's markup panel asks the host for one
        // and gets whatever this field held when the services were built.
        hotReload = new HotReloadHost(Shell.Document);
        MetadataUpdate.Register(hotReload);

        // ⚠ And the shell's own `.vxml` panel, which is the one the host could not have had.
        // `EditorShell` builds the task centre in its constructor and the host is built over the
        // document that constructor makes, so the only order that exists is: shell, host, and then
        // this. Without it the editor's single markup-authored panel is the single panel a
        // `dotnet watch` cannot reach — a declarative authoring path with a hole in it exactly where
        // the shipped example is. See `EditorShell.RemountTaskCenter`.
        Shell.RemountTaskCenter(hotReload.Mount);

        plugins = new PluginHost(Shell, PluginPoints());

        // ⚠ The host publishes itself, after construction because it cannot before: `PluginPoints`
        // builds the services the constructor takes. Doc 36 § P5 is why it is published at all — a
        // module that loads more modules needs somewhere to put them, and the script host's whole
        // design is that a project's `Editor/` folder is a plugin.
        plugins.Services.Add(plugins);

        // ⚠ Doc 36 § D3's attributes, for both tiers at once. The scanner lives here because the
        // attributes name `CustomInspector`, `DrawerRegistry` and `SceneTool` — types the plugin
        // contract must not reference — and `PluginHost` runs it over every plugin assembly it loads
        // and over a project's script assembly. See `DeclaredContributions`.
        plugins.Scanners.Add(new DeclaredContributions());

        Layouts();
        Commands();

        // ⚠ After every command is registered, because the entries name command ids and one that
        // resolves to nothing is a wedge that does nothing. See `RegisterSceneMenus`, which skips
        // any name it cannot find rather than putting a dead line in a menu.
        SceneMenuCommands();
        RegisterSceneMenus();

        // ⚠ After the commands and before the keymap, because the undo depth it carries is applied
        // to stacks that exist by now, and because `SavePreferences` is reachable from a panel the
        // line above has just registered.
        LoadPreferences();
        ApplyProjectSettings();

        // ⚠ Plugins go here and not later, and the two reasons are the two lines below. A plugin's
        // commands have to exist before the keymap is read or the user's override for one lands on
        // a command with no default; a plugin's panels have to be registered before the saved
        // layout is applied or an arrangement that had one comes back without it.
        //
        // ⚠ And the user's own list of what to leave alone is read before anything is activated.
        // A plugin somebody switched off because it broke the editor is exactly the one whose
        // Activate must not run.
        LoadDisabledPlugins();
        StartPlugins(directory);

        // ⚠ In this order and no other. The keymap has to be loaded after the commands that own its
        // defaults, or every override in the file lands on a command with no default and the file
        // rewrites itself with the whole map in it. The layout has to be applied after the panels
        // are registered, or a saved arrangement names panels the workspace cannot build.
        if (store.Read(EditorUserStore.KeyMapFile) is { } keymap) {
            Shell.Keys.Load(keymap);
        }

        Shell.Theme.LoadTokens(store.Read(ThemeFile));

        // ⚠ Doc 20's A6: the panels a saved arrangement names include the asset editors that were
        // open, which nothing has registered yet because they are registered on demand. This is the
        // hook that registers one when the arrangement asks for it.
        Shell.Workspace.Resolve = ReopenDocument;

        // ⚠ The one place the layout file is read without the user having asked for it, so it is the
        // one place a file that could not be used has to be said out loud. `Load` puts the default
        // preset up either way; without the notice a hand-edited or truncated file looks exactly
        // like a first run, and the response to that is re-arranging every panel over a file that
        // will fail again next time.
        if (store.LoadLayout(EditorUserStore.CurrentLayout) is { } layout) {
            if (!Shell.Workspace.Load(layout)) {
                Shell.Notifications.Show(EditorStrings.LayoutNotRestored.Text, NotificationSeverity.Warning);
            }
        } else {
            Shell.Workspace.Reset();
        }

        // ⚠ Recorded after the project has opened rather than before, so a root that turned out to
        // be unreadable is not offered as the first thing to try again next time.
        Recent.Record(project.Paths.Root, DateTime.UtcNow);

        WarnIfNewerEngine();

        Shell.Status = ProductName;

        // ⚠ A delegate rather than a number pushed on every change. Which of the several selections
        // the count is about is this class's arbitration — see `FollowSelection` — and a shell
        // holding a number would hold whichever one was written last.
        Shell.SelectionCount = () => inspectingAssets
            ? project.Selection.Count
            : (inspected ?? scene).Selection.Count;

        Retitle();
    }

    /// <summary>The interface.</summary>
    public EditorShell Shell { get; }

    /// <summary>The scene the editor is showing: the prefab being inspected, or the open one.</summary>
    /// <remarks>
    ///     ⚠ <b>What a panel that counts things has to count.</b> An editor with a prefab open is
    ///     inspecting the prefab and showing the level behind it; a statistics readout that counted
    ///     the level would report the wrong number every time somebody pressed Refresh inside a
    ///     prefab. Published as <c>IActiveScene</c> so a module can ask without holding the answer.
    /// </remarks>
    internal SceneDocument Shown => inspected ?? scene;

    /// <summary>What the viewport draws the ground from, if a terrain module contributed one.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the registry rather than owned.</b> This used to be a property on the
    ///     application because the terrain session was a partial of it; the session is
    ///     `Vixen.Editor.Terrain`'s now, and it contributes its implementation. The last contribution
    ///     wins, which is the ordinary override rule — a project shipping its own terrain module
    ///     replaces the built-in rather than fighting it.
    /// </remarks>
    internal ITerrainScene? TerrainScene => Extensions.All<ITerrainScene>() is [.., var scene] ? scene : null;

    /// <summary>Play, pause, step and stop — and what this session's frame is made of.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal for the tests that assert about the <em>frame</em> rather than the
    ///     button.</b> Every commands-level assertion goes through <c>play.play</c> and its
    ///     neighbours; what needs the object is the question "did physics actually attach", whose
    ///     honest answer is <c>PlayModeController.Session</c> and not a notification's wording.
    /// </remarks>
    internal PlayModeController PlayMode => play;

    /// <summary>What the viewport draws the painted foliage from, if a module contributed one.</summary>
    /// <remarks><see cref="TerrainScene" />'s arrangement exactly, and the last contribution wins.</remarks>
    internal IVegetationScene? VegetationScene =>
        Extensions.All<IVegetationScene>() is [.., var scene] ? scene : null;

    /// <summary>What the viewport draws the water from, if a water module contributed one.</summary>
    /// <remarks><see cref="TerrainScene" />'s arrangement exactly, and the last contribution wins.</remarks>
    internal IWaterScene? WaterScene => Extensions.All<IWaterScene>() is [.., var scene] ? scene : null;

    /// <summary>The features this editor was told to load, in the order it registers them.</summary>
    /// <remarks>
    ///     ⚠ <b>Handed in rather than listed here, and that is doc 36 § P3's whole point.</b> A
    ///     feature cannot be dereferenced by the assembly that has to instantiate it — so the list
    ///     lives in the executable, and this class knows only that some `IEditorPlugin`s exist and
    ///     what they are called. An editor constructed with none of them is the shell, the project
    ///     and the scene, which is exactly what a thumbnail renderer wants.
    /// </remarks>
    readonly IReadOnlyList<(string Id, string Name, IEditorPlugin Module)> modules;

    /// <summary>The registered asset-editors module, or null when this editor was built without it.</summary>
    /// <remarks>
    ///     ⚠ <b>The registered one, for <see cref="diagnostics" />' reason.</b> A second instance
    ///     compiles, activates nothing, and hands every freshly-opened AI editor a debug model no
    ///     panel is showing — which looks exactly like the wiring working.
    /// </remarks>
    readonly AssetEditorsModule? assetEditors;

    /// <summary>What this editor put in <see cref="Extensions" />, so shutting down takes it back out.</summary>
    readonly List<IDisposable> contributions = [];

    /// <summary>Everything the editor has been told about, whoever told it. See doc 36 § D2.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The process-wide one unless a host hands over another, because that is where a
    ///         generated registration has to go.</b> A module initializer runs with no editor to be
    ///         handed, so a contribution declared next to its code lands in
    ///         <see cref="EditorRegistry.Default" /> and this is what reads it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A test harness supplies its own, and has to.</b> One editor per process is the
    ///         product's arrangement and a shared static is right for it; a suite runs several at once
    ///         and a plugin loaded by one would appear in another's Create menu. <c>EditorSession</c>
    ///         makes a registry per session for the same reason it makes a directory per session.
    ///     </para>
    /// </remarks>
    public IEditorRegistry Extensions { get; }

    /// <summary>The scene being edited.</summary>
    public SceneDocument Scene => scene;

    /// <summary>The project the editor has open.</summary>
    public EditorProject Project => project;

    /// <summary>Where a thumbnail becomes a picture, once the host has a device to make one with.</summary>
    /// <remarks>
    ///     ⚠ <b>Set by the host after the device exists, which is after this object does.</b> The
    ///     window has to be up before a Vulkan surface can be made from it, so the application is
    ///     constructed first — and a browser that demanded its uploader up front would be one the
    ///     host could not build. Null is the ordinary state headless, and the grid draws type glyphs.
    /// </remarks>
    public IThumbnailSurface? ThumbnailSurface {
        get => thumbnails.Surface;
        set => thumbnails.Surface = value;
    }

    /// <summary>The pane a command acts on, or <see langword="null" /> while the panel is closed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The focused one, which in a single-pane layout is the only one.</b> Every scene
    ///         command — the gizmo modes, the view keys, the show flags — acts on one pane, and which
    ///         one is the question a split layout makes worth asking. <c>ViewportLayout</c> tracks it
    ///         from the controls' own focus, so clicking in a pane is what makes the next `W` change
    ///         that pane's gizmo.
    ///     </para>
    ///     <para>
    ///         Null is the ordinary case rather than an error: a layout without the scene panel in it
    ///         is one the user chose, and the host renders nothing for it.
    ///     </para>
    /// </remarks>
    public SceneViewport? Viewport => viewports?.Focused;

    /// <summary>Every pane the scene is drawn in, in reading order.</summary>
    /// <remarks>
    ///     What the host renders: one <c>ScenePresenter</c> per entry, each with its own render
    ///     target and its own image id. Empty while the panel is closed.
    /// </remarks>
    public IReadOnlyList<SceneViewport> Viewports => viewports?.Panes ?? [];

    /// <summary>How the scene panel is split.</summary>
    public ViewportArrangement Arrangement {
        get => arrangement;

        set {
            if (arrangement == value) {
                return;
            }

            arrangement = value;

            // ⚠ Forgotten before the rebuild, not after. `ViewportLayout` raises `Rearranged` from
            // inside the setter below, and the handler is what restores these — so clearing
            // afterwards would restore last arrangement's cameras into this one's panes and then
            // throw the record away.
            Array.Clear(cameras);

            if (viewports is not null) {
                viewports.Arrangement = value;
            }
        }
    }

    /// <summary>Whether the editor has been asked to close.</summary>
    public bool IsClosing { get; private set; }

    /// <summary>Whether this is the scratch project rather than one somebody chose.</summary>
    bool IsScratch { get; }

    /// <summary>Whether the host should put the project browser up on the first frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Set by the host and false everywhere else, which is what keeps it out of the
    ///     tests.</b> Doc 20 calls the startup Project Browser "the first thing a new user sees",
    ///     and it means the <i>first</i> — a browser that opened over a project the user chose last
    ///     time is a dialog they dismiss every launch. So it appears once: on a run with no
    ///     <c>--project</c>, on the scratch project, with nothing in the recent list.
    /// </remarks>
    public bool Greets { get; set; }

    /// <summary>How many render pixels one layout pixel is.</summary>
    /// <remarks>
    ///     Pushed down to the viewport rather than read from it, because the display's scale belongs
    ///     to the window and a panel has no way to ask.
    /// </remarks>
    public float RenderScale {
        get;

        set {
            field = value;

            foreach (var pane in Viewports) {
                pane.Control.RenderScale = value;
            }
        }
    } = 1f;

    /// <summary>
    ///     Brings the panels up to date with the model, once a frame, after the layout pass.
    /// </summary>
    /// <param name="delta">How long the last frame took, for the things that move by themselves.</param>
    /// <remarks>
    ///     ⚠ <b>After <c>UiDocument.Update</c> and before <c>Draw</c>.</b> The viewport measures
    ///     itself in render pixels from its own box, which the layout pass is what produces; and the
    ///     axis cross it draws comes from the camera rotation this writes. Either side of that pair
    ///     and the picture is a frame behind.
    /// </remarks>
    public void Update(TimeSpan delta) {
        // ⚠ On the first update rather than in the constructor, because the dialog service completes
        // from the tick and a question asked before the loop has started is one nothing pumps.
        // Cleared as it is consumed, so the flag is the latch — a second `greeted` field would be a
        // second thing that has to agree with this one.
        if (Greets) {
            Greets = false;

            BuildProjectCode();

            if (IsScratch && Recent.Entries.Count <= 1) {
                ShowProjectBrowser();
            }
        }

        // What a finished import or build had to say, on the thread that owns the panels it is about
        // to rebuild. See `ContentTasks` for why nothing crosses back except a queued value.
        content.Pump();

        // And what a native picker answered, for the same reason and on the same terms. Two queues
        // rather than one, because a dialog's answer must not wait behind a content build's.
        deferred.Pump();

        // ⚠ And the thumbnails, on the frame thread because the device is not thread-safe. The
        // decode happened on the pool; this is the upload.
        thumbnails.Pump();

        // ⚠ Pulled here rather than subscribed to, and the reason is threading: the sink is written
        // from the pool by a content import and by anything else the editor runs in the background,
        // so a subscription would rebuild the panel's rows off the frame thread.
        console?.Tick();

        // ⚠ Beside the console's pull and for the same reason: a capture drains the sample rings,
        // and the rings are written from every thread the editor runs work on.

        // ⚠ And E5's two moving surfaces, pulled rather than self-driving. A VFX preview and a
        // sequencer transport both advance with time, and a timer either of them started would
        // outlive the panel it was drawn in — the rule every pulled surface in this editor follows.
        AuthoringUpdate(delta);

        // ⚠ Polled, and it compares before it rebuilds. A command stack is signal-backed and nothing
        // in this loop flushes the reactive scheduler — the same trade the selections make — and the
        // panel is the one that would otherwise rewrite its whole list during a gizmo drag.
        historyView?.Tick();

        // ⚠ **The game's frame, and it replaces the editor's transform pass rather than joining
        // it.** `PlayModeController.Tick` steps an `EngineLoop`, whose `PreRender` already runs a
        // `TransformSystem` over this world — and two instances of that system keep separate
        // "what have I seen" versions, so each would answer the other's writes with "nothing
        // changed". The failure is not a double cost; it is a moved object that stops following
        // its parent, on alternate frames, only while playing.
        if (!play.Tick(delta)) {
            ResolveTransforms();
        }

        // ⚠ Immediately after them and not later in this method. Both extraction queries want
        // `WorldTransform` and neither writes one — in a game the phase and the declared access are
        // what put them after `TransformSystem`, and the editor has no graph to do that — so an
        // extraction anywhere above this line places every object where it was last frame.
        ExtractFrame();

        FollowHistory();
        Retitle();

        if (hierarchyStale) {
            hierarchyStale = false;
            RebuildHierarchy();
        }

        FollowSelection();

        // ⚠ After it, because a module's per-frame work follows the *entity* selection and this is
        // where that is arbitrated between the panels. A terrain brush pointed at ground the frame
        // has just decided is not selected any more would be a stroke on the wrong hill.
        plugins.Update(delta);

        // ⚠ After the arbitration, and every frame rather than only after a rebuild. A selection
        // made anywhere but the tree — a viewport click, a command, an undo — changes nothing
        // structural, so a sync that only ran when the rows were rebuilt would leave the outliner
        // highlighting whatever was clicked in it last. Comparing a handful of handles is the same
        // trade this class already makes for the selections themselves.
        SyncTreeSelection();
        browser?.SyncSelection();

        // ⚠ Drained here rather than raised from the platform thread, which is the whole shape of
        // `IFileWatcher`: a rescan clears and repopulates the database's dictionaries, and doing
        // that from a `FileSystemWatcher` callback would race every panel reading it.
        FollowDisk();

        // ⚠ And the dev-mode stylesheets, on the frame thread and for exactly the same reason. A
        // reload replaces the rule set and re-runs the cascade over every element in the document;
        // a `FileSystemWatcher` callback doing that from the pool would be rewriting the tree
        // underneath the layout pass, which has no lock and does not expect one. `Poll` is where the
        // coalesced changes are applied and this is the only place it is called.
        styleWatcher?.Poll();

        if (viewports is not { } layout) {
            return;
        }

        // ⚠ Every pane, not only the focused one. A pane nobody has clicked in still has a camera
        // that has to be brought up to date before its render view is read, and a four-pane layout
        // where three of the panes only redraw once somebody has clicked in them is one where the
        // other three look frozen. Which pane *flies* is decided by the focus, inside the pane.
        for (var index = 0; index < layout.Panes.Count; index++) {
            var pane = layout.Panes[index];

            pane.Update(delta);
            pane.Stats.Sample(delta);
            chrome?.Refresh(pane, ReferenceEquals(pane, layout.Focused));

            // ⚠ Kept every frame, not on the way out. A panel's factory runs again when it is
            // reopened and the SceneViewport goes with the old one, so there is no teardown hook to
            // read the camera in — and a bookmark taken once at startup would restore the origin
            // every time.
            if (index < cameras.Length) {
                cameras[index] = pane.Camera.Bookmark("current");
            }

            RememberView(index, pane);
        }

        // The inspector follows the gizmo. Reload rather than Inspect, because the rows and their
        // handlers already exist and rebuilding would take the focus out of whatever is being typed.
        if (layout.Panes.Any(static pane => pane.Gizmo.IsDragging)) {
            inspector?.Reload();
        }
    }

    /// <summary>Records what a pane is drawing, and writes it when it changes.</summary>
    /// <param name="index">Which pane, in reading order.</param>
    /// <param name="pane">The pane.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Polled beside the camera rather than raised, and for the camera's own reason.</b>
    ///         A pane is destroyed and rebuilt every time the Scene panel is closed and reopened, so
    ///         there is no teardown hook to read the flags in — and a <c>SceneViewport</c> that raised
    ///         an event per flag would still leave the arrangement's <i>other</i> panes unwritten.
    ///         Reading them is a bitwise compare.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written only when it differs from what is in the file.</b> This runs every frame
    ///         for every pane; writing unconditionally would be a YAML serialisation and a disk write
    ///         sixty times a second for the life of the editor.
    ///     </para>
    /// </remarks>
    void RememberView(int index, SceneViewport pane) {
        while (preferences.Viewports.Count <= index) {
            preferences.Viewports.Add(new ViewportPreferences());
        }

        var stored = preferences.Viewports[index];
        var show = ShowFlags.Slugs(pane.Show);
        var mode = pane.Modes.Current.ToString();

        if (string.Equals(stored.Mode, mode, StringComparison.Ordinal) && stored.Show.SequenceEqual(show, StringComparer.Ordinal)) {
            return;
        }

        stored.Show = show;
        stored.Mode = mode;

        WritePreferences();
    }

    /// <summary>Puts a pane back to what it was drawing when the editor last closed.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing at all for a pane the file has no entry for</b>, which is what leaves a
    ///     four-pane arrangement nobody has opened yet at its defaults rather than giving every pane
    ///     the first one's flags. Same rule as the saved cameras a line above it.
    /// </remarks>
    void RestoreView(int index, SceneViewport pane) {
        if (preferences.Viewports.ElementAtOrDefault(index) is not { } stored) {
            return;
        }

        if (stored.Show.Count > 0) {
            pane.Show = ShowFlags.Parse(stored.Show);
        }

        if (Enum.TryParse<ViewMode>(stored.Mode, out var mode)) {
            pane.Modes.Current = mode;
        }
    }

    /// <summary>How deep the stack the inspector is over was when its rows were last read.</summary>
    /// <inheritdoc cref="FollowHistory" select="remarks" />
    (CommandStack? Stack, int Depth) history = (null, -1);

    /// <summary>Reads the inspector's editors back after an undo or a redo moved the model.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this Ctrl+Z is a lie in the one panel that shows the number it changed.</b>
    ///         An undo puts the old value back in the world and takes the entry off the stack — the
    ///         history panel notices, the viewport redraws, the title bar's asterisk moves — and the
    ///         inspector goes on showing what was typed, because a row is read from its target when it
    ///         is built and after an edit <i>it</i> made. Nothing tells it that somebody else wrote.
    ///         The value reappears the next time the selection changes, which is what makes it look
    ///         like the undo did not happen rather than like the panel is stale.
    ///     </para>
    ///     <para>
    ///         <b><c>Reload</c> and not <c>Inspect</c>.</b> The rows and their handlers already exist
    ///         and describe the same objects; rebuilding would take the focus out of whatever is
    ///         being typed and collapse every expander. It is the same call a gizmo drag makes for
    ///         the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Polled and compared, like everything else in this loop.</b> A command stack is
    ///         signal-backed and nothing here flushes the reactive scheduler — the trade this class's
    ///         own remarks describe — and the comparison is two fields, so a frame in which nothing
    ///         was undone costs nothing. The stack is part of the key because the inspector follows
    ///         whichever document's selection won, and switching to another document is not an undo.
    ///     </para>
    /// </remarks>
    void FollowHistory() {
        var stack = inspectingAssets ? project.GlobalStack : (inspected ?? scene).Stack;
        var state = (stack, stack.Depth.Value);

        if (state == history) {
            return;
        }

        var moved = ReferenceEquals(history.Stack, stack);

        history = state;

        if (!moved) {
            return;
        }

        inspector?.Reload();

        // ⚠ And the component foldouts, which are not the inspector's rows and are the ones a
        // numeric edit usually lands in. `SetComponentCommand` announces itself only when the *set*
        // of components changed — a value edit deliberately says nothing, so that a slider drag does
        // not rebuild the panel under the pointer — which left an undone intensity showing the
        // number it had been undone from.
        components?.Reload();
    }

    /// <summary>Brings every <c>WorldTransform</c> up to date with the local one behind it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this nothing an edit does is visible.</b> <c>Transform</c> reads the
    ///         world matrix and writes the local one — deliberately, so that "position" means the
    ///         same thing to a gizmo drag and a typed number — and <c>TransformSystem</c> is what
    ///         joins the two. An editing frame runs no system graph, so a position typed into the
    ///         inspector landed in <c>LocalTransform</c> and was never turned into the matrix the
    ///         viewport draws from and the inspector reads back: the number reverted and the object
    ///         did not move. The same held for a gizmo drag, for the hierarchy's parent lines, and
    ///         for frame-selected.
    ///     </para>
    ///     <para>
    ///         Resolved here rather than by standing up a <c>SystemRunner</c>, which is what
    ///         <c>TransformSystem.Resolve</c> is public for. The world is a document until play mode
    ///         says otherwise — <see cref="EditorApplication" />'s own remarks — and one pass whose
    ///         cost is bounded by what moved is not a frame loop.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is skipped entirely on a frame play mode ticked</b>, because that frame's
    ///         graph ran a <c>TransformSystem</c> of its own. Two instances over one world keep
    ///         separate "what have I already seen" versions, so each answers the other's writes with
    ///         "nothing changed" — a moved object that stops following its parent on alternate
    ///         frames, only while playing. See <see cref="Update" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The version is advanced <i>after</i> the pass, not before.</b> The pass answers
    ///         "what has been written since I last looked" by comparing chunk versions against the
    ///         one it saw last time, and the editor has no sync point of its own to advance at —
    ///         every write is stamped with whatever <c>World.Version</c> currently is. Advancing
    ///         first would stamp this frame's edits with the same version the pass just recorded as
    ///         seen, so the next pass would answer "nothing changed" and the second edit of a
    ///         session would be the one that stopped working.
    ///     </para>
    /// </remarks>
    void ResolveTransforms() {
        transforms.Resolve(world);
        world.AdvanceVersion();
    }

    /// <summary>Writes what the user changed.</summary>
    /// <remarks>
    ///     Called on the way down rather than on every change: a splitter drag raises
    ///     <c>LayoutChanged</c> per mouse-move, and an editor that wrote a file per frame of a drag
    ///     would be one whose window layout is the noisiest thing on the disk.
    /// </remarks>
    public void Persist() {
        store.SaveLayout(EditorUserStore.CurrentLayout, Shell.Workspace.Save());
        store.Write(EditorUserStore.KeyMapFile, Shell.Keys.Save());
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The plugins go first, and before the shell.</b> Unloading is what takes their
    ///     commands and panels back out, and a plugin whose panel is closed by a disposed docking
    ///     workspace would throw on the way down — which is the one place an exception costs the
    ///     user their layout file.
    /// </remarks>
    public void Dispose() {
        plugins.UnloadAll();

        // ⚠ Before anything else, and it is not tidying. `EditorRegistry.Default` is process-wide —
        // it has to be, because a generated registration has no editor to be handed — so an editor
        // that shut down without withdrawing its own contributions would leave them there for the
        // next one to find, and would leave `Changed` holding a delegate over a disposed shell.
        // Two editors in one process is not hypothetical: it is every test run.
        Extensions.Changed -= RefreshAssetKinds;
        Extensions.Changed -= RefreshOverlays;

        // ⚠ And for the same reason. `MetadataUpdate` holds hosts weakly, so a missed unregister is
        // not a leak — but a reload delivered to a disposed shell's document is a rebuild into a
        // tree nobody is drawing, and it would happen for every editor a test run ever opened.
        MetadataUpdate.Unregister(hotReload);

        foreach (var registration in contributions) {
            registration.Dispose();
        }

        contributions.Clear();

        // Before the shell, because the images it releases are registered with the renderer the
        // shell's document draws through.
        thumbnails.Dispose();

        // ⚠ Early, so that nothing arrives during the rest of this. A watcher left running holds a
        // platform handle and keeps recording changes into a coalescer nobody will drain again.
        watcher?.Dispose();

        // And the subscription on the project's saves with it, so that the documents closing below
        // do not announce writes to a watcher that has gone.
        external.Dispose();

        // And the dev-mode one, on the same terms — plus one of its own: it is the reason
        // `--hot-reload` cannot make the process outlive its window, since a live
        // `FileSystemWatcher` is a handle the runtime will not shut down over.
        styleWatcher?.Dispose();

        viewports?.Dispose();

        // ⚠ Before the world, because the extraction holds a residency claim per entity and a claim
        // is released by looking the entity up. The host normally gets here first by setting
        // `GraphicsDevice` to null on the way down — this is the path a test that never had a device
        // takes, and the one a host that forgets takes.
        DisposeFrames();

        // ⚠ After the frames, because the renderer they hold was mounted over this manager and its
        // material and texture sources resolve through it on the way down.
        projectContent.Dispose();

        // Before the world, because it holds a snapshot of it: a controller disposed after the world
        // would be releasing chunks into a world that had already released its own.
        play.Dispose();

        Shell.Dispose();
        world.Dispose();

        // After the shell, because disposing it raises no notifications but unloading a plugin can —
        // and a mirror taken down first would lose the last thing the editor had to say.
        log.Dispose();
    }

    /// <summary>Builds and loads the project's own code, and says what happened.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On the first frame rather than in the constructor.</b> It runs a compiler, which
    ///         takes seconds on a cold restore — and the console it reports into is a panel the shell
    ///         has to have built first. An editor that blocked on <c>dotnet build</c> before drawing
    ///         anything would look like one that failed to start.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A failed build is a notification and not a refusal.</b> The project's content is
    ///         still worth opening — a scene, a texture, a material do not care whether the game's
    ///         C# compiles — and an editor that would not open a project because of a syntax error
    ///         would be one you could not use to fix the syntax error.
    ///     </para>
    /// </remarks>
    void BuildProjectCode() {
        // ⚠ Taken off before the assembly goes and put back after it returns. A behaviour somebody
        // authored is an instance of a type that is about to stop existing — leaving it attached
        // would hold the old context alive *and* lose the values, which is both failures at once.
        // Bytes and an alias are what crosses: the same alias registers again from the rebuilt
        // assembly, and the state goes into an instance of the new type.
        var authored = SaveProjectBehaviors();
        var built = code.Reload();

        if (built.Output is { Length: > 0 } said) {
            log.Write(built.Failed ? LogLevel.Error : LogLevel.Debug, said);
        }

        if (built.Failed) {
            Shell.Notifications.Show(
                "The project's code did not build",
                NotificationSeverity.Error,
                "Its components and behaviours are not available. The console has what the compiler said."
            );

            return;
        }

        if (built.Assembly is not null) {
            // The load ran the module initializers, so whatever the project declares is in the
            // registries — and `ComponentsView.Registered` re-reads them, so the Add Component menu
            // has it without being told.
            RestoreProjectBehaviors(authored);

            components?.Rebuild();
            RefreshBuildPanel();
        }
    }

    /// <summary>One authored behaviour, as something that outlives the type that held it.</summary>
    /// <param name="Entity">Which entity carried it.</param>
    /// <param name="Alias">Its name, which is what survives a rebuild.</param>
    /// <param name="State">Its values.</param>
    readonly record struct AuthoredBehavior(Entity Entity, string Alias, byte[] State);

    /// <summary>Takes every behaviour off the scene, keeping what was in it.</summary>
    /// <remarks>
    ///     ⚠ <b>Every behaviour, not only the project's.</b> Deciding which assembly a behaviour came
    ///     from means reading its type, and the whole point of this is that types are about to become
    ///     unreliable. Taking them all off and putting them all back is the same answer for both and
    ///     has no case that needs to be right.
    /// </remarks>
    List<AuthoredBehavior> SaveProjectBehaviors() {
        List<AuthoredBehavior> authored = [];

        foreach (var entity in scene.Entities) {
            foreach (var behavior in scene.Behaviors.AllOn(entity).ToArray()) {
                if (!SceneBehaviorRegistry.TryGet(behavior.GetType(), out var binder)) {
                    continue;
                }

                authored.Add(new(entity, binder.Name, binder.Save(behavior)));
                binder.RemoveFrom(scene.Behaviors, entity);
            }
        }

        return authored;
    }

    /// <summary>Puts them back, on the rebuilt types.</summary>
    /// <remarks>
    ///     ⚠ <b>A behaviour whose alias the rebuilt project no longer declares is dropped, and said
    ///     so.</b> Somebody deleted or renamed the class; there is nowhere to put its values, and
    ///     silently keeping them would mean a save that wrote a behaviour the build cannot name.
    /// </remarks>
    void RestoreProjectBehaviors(List<AuthoredBehavior> authored) {
        var lost = 0;

        foreach (var (entity, alias, state) in authored) {
            if (!scene.World.IsAlive(entity) || !SceneBehaviorRegistry.TryGet(alias, out var binder)) {
                lost++;
                continue;
            }

            binder.AttachTo(scene.Behaviors, entity, binder.Restore(state));
        }

        if (lost > 0) {
            Shell.Notifications.Show(
                $"{lost} authored behaviour(s) were dropped",
                NotificationSeverity.Warning,
                "The rebuilt project no longer declares them, so there was nowhere to put their values."
            );
        }
    }

    /// <summary>Starts watching a project's assets, if there are any to watch.</summary>
    /// <remarks>
    ///     ⚠ <b>A quarter of a second of quiet before a change counts.</b> A text editor's atomic
    ///     save is four raw events and a rename; an art tool exporting a texture atlas is one per
    ///     file. Rescanning on each would walk the project several times for one action —
    ///     <c>FileChangeCoalescer</c>'s debounce is what turns a burst into a rescan.
    /// </remarks>
    IFileWatcher? Watch(EditorProject open) {
        if (!Directory.Exists(open.Paths.Assets)) {
            return null;
        }

        try {
            return new FileWatcher(open.Paths.Assets, VirtualPath.Root) {
                Debounce = TimeSpan.FromMilliseconds(250)
            };
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A project on a share the platform cannot watch, or one whose handle limit is spent.
            // The editor still opens and Refresh still works, which is the right trade for a
            // convenience — and the console says why the panel stopped keeping up by itself.
            log.Write(
                LogLevel.Warning,
                $"Could not watch '{open.Paths.Assets}' for changes, so the Project panel will only "
                + $"update on Refresh. {exception.Message}"
            );

            return null;
        }
    }

    /// <summary>Loads every <c>.vcss</c> under a directory and reloads them as they are saved.</summary>
    /// <param name="directory">Where the developer's sheets are.</param>
    /// <returns>How many sheets were found, which is zero for a directory with nothing in it.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The style channel, and it is the one that is genuinely free</b> — the rule set is
    ///         replaced and the cascade runs again, so every element keeps its identity and therefore
    ///         its focus, its scroll offset and its animation state. Nothing is rebuilt.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Last, and at <c>Author</c> origin — for a sheet the document does not already
    ///         have.</b> The sheets the editor ships are <c>UserAgent</c>, which loses to
    ///         <c>Author</c> for every normal declaration, so a rule written in a scratch directory
    ///         beats the shipped one without having to out-specify it. That is what an overlay is
    ///         for and it is still what an unrecognised file gets.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A file that <i>is</i> one of the shipped sheets replaces it in place instead,
    ///         at its own origin.</b> Every theme in the editor is a <c>.vcss</c> beside its loader
    ///         and embedded from it, so pointing this at the source tree finds the same text twice —
    ///         and loading it twice is the difference between iterating on values and iterating on
    ///         which rules exist. See <c>HotReloadWatcher.Load</c> for how the two copies are
    ///         recognised as one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One name is skipped, and it is not a cascade sheet at all.</b>
    ///         <c>vixen.ui.vcss</c> is the <c>@theme</c> token source the utility generator reads at
    ///         <i>build</i> time — the name is the build's own, globbed as <c>**/vixen.ui.vcss</c> by
    ///         <c>Vixen.Ui.Styling.Utilities.targets</c>, one per project. Nothing loads it into a
    ///         document, so handing it to the cascade only reaches ExCSS as an at-rule nothing knows
    ///         and produces a diagnostic. That used to be harmless because nothing read the
    ///         diagnostics; they drain to the log now — see <c>StyleDiagnostics</c> — so it is a
    ///         warning on start-up and on every save of every other sheet in the folder, for a file
    ///         that could never have done anything. The README's advice was to point at a copy of the
    ///         directory instead, which is a workaround for a file the editor can recognise itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero is reported rather than swallowed.</b> A watcher over a directory with no
    ///         stylesheets in it is a channel that looks wired and does nothing, and the developer
    ///         who pointed it at the wrong folder has no other way to find out.
    ///     </para>
    /// </remarks>
    internal int WatchStyles(string directory) {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory)) {
            log.Write(LogLevel.Warning, $"There is no directory '{directory}' to reload stylesheets from.");
            return 0;
        }

        try {
            styleWatcher = new HotReloadWatcher(hotReload, directory);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            log.Write(LogLevel.Warning, $"Could not watch '{directory}' for stylesheets. {exception.Message}");
            return 0;
        }

        var found = 0;
        var replaced = 0;

        // ⚠ Ordered, because two sheets that declare the same rule are decided by which was loaded
        // second — and a load order that came from the filesystem's enumeration would put the
        // editor's look at the mercy of the order a directory happens to hand its entries back in.
        foreach (var file in Directory.EnumerateFiles(directory, "*.vcss", SearchOption.AllDirectories)
                     .Where(file => !IsBuildOutput(directory, file))
                     .Order(StringComparer.Ordinal)) {
            // The `@theme` token source, which is a build input and not a stylesheet. See the
            // remarks: loading it can only produce a diagnostic, and the diagnostics are read now.
            if (string.Equals(Path.GetFileName(file), TokenSource, StringComparison.OrdinalIgnoreCase)) {
                log.Write(
                    LogLevel.Information,
                    $"Not watching '{file}': a {TokenSource} is the utility generator's @theme source, "
                    + "read at build time, and is not a sheet the cascade can load."
                );

                continue;
            }

            try {
                styleWatcher.Load(file);
                found++;

                if (styleWatcher.Replaces(file)) {
                    replaced++;
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                log.Write(LogLevel.Warning, $"Could not read '{file}'. {exception.Message}");
            }
        }

        // ⚠ The reports go to the console rather than nowhere. A sheet that fails to load puts the
        // previous one back — see `HotReloadHost.ReloadStyles` — so without this the visible effect
        // of a typo is that saving stopped changing anything, with no way to tell that from a
        // watcher that had quietly died.
        styleWatcher.Reloaded += report => {
            styleReloads.Add(report);

            if (report.Succeeded) {
                return;
            }

            log.Write(LogLevel.Warning, $"Stylesheet not reloaded: {string.Join("; ", report.Errors)}");
        };

        // ⚠ The two numbers are separate because they mean different things to the person reading
        // them: a replaced sheet iterates on which rules exist, and an added one can only iterate on
        // values. Somebody who pointed this at the wrong copy of the tree sees `0 replace` and knows
        // why deleting a rule is doing nothing.
        log.Write(
            LogLevel.Information,
            found == 0
                ? $"Watching '{directory}' for stylesheets, and there are none in it yet."
                : $"Watching '{directory}': {found} stylesheet(s) reload on save, "
                + $"{replaced} of them replacing a sheet the editor ships."
        );

        return found;
    }

    /// <summary>The file name the utility generator's <c>@theme</c> block goes in, by convention.</summary>
    /// <remarks>
    ///     ⚠ <b>The build's own name for it, not one invented here.</b>
    ///     <c>Vixen.Ui.Styling.Utilities.targets</c> globs <c>**/vixen.ui.vcss</c> and errors on a
    ///     project with two, so this is the one thing in a source tree that is spelled <c>.vcss</c>
    ///     and is not a stylesheet.
    /// </remarks>
    const string TokenSource = "vixen.ui.vcss";

    /// <summary>Whether a path found under the watched directory is a build artefact.</summary>
    /// <param name="directory">The directory being watched.</param>
    /// <param name="file">The candidate stylesheet.</param>
    /// <returns><c>true</c> when the file is under an <c>obj</c> or <c>bin</c> segment.</returns>
    /// <remarks>
    ///     ⚠ <b>The guard <c>DesktopHotReload</c> has and this method did not, which made it the only
    ///     reader in the tree of a file that is supposed to have none.</b> The utility build step
    ///     writes <c>obj/&lt;config&gt;/&lt;tfm&gt;/Vixen/&lt;Assembly&gt;.g.vcss</c>, and that sheet
    ///     is the concatenation the document already holds — so it matches by text and
    ///     <see cref="HotReloadWatcher.Load" /> <i>binds</i> to it, which looks like an improvement
    ///     and is a trap. It is a build artefact, rewritten on every build, so every rebuild fires a
    ///     reload of a file nobody edited; and with one copy per configuration the
    ///     <c>obj/Release</c> one binds a sheet this process is not running.
    ///     <para>
    ///         Filtering by name would not have caught it. The name is the assembly's, so it is
    ///         different in every project and matches no fixed spelling the way
    ///         <see cref="TokenSource" /> does — the location is the only thing that identifies it.
    ///     </para>
    /// </remarks>
    static bool IsBuildOutput(string directory, string file) =>
        Path.GetRelativePath(directory, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            );

    /// <summary>Rescans when something outside the editor has changed the assets.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What changed is deliberately not used.</b> The database's scan is the thing that
    ///         knows how to repair a sidecar, re-GUID a duplicate and quarantine an orphan, and a
    ///         browser that applied one path at a time would be a second, worse implementation of it
    ///         that disagreed after the first rename. The changes are drained to find out
    ///         <i>whether</i> to rescan, not what to do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overflow is a rescan too.</b> Losing events is the one case where nothing that
    ///         arrives afterwards can be trusted to describe the folder — which is exactly the case a
    ///         full rescan covers, and why the watcher reports it separately.
    ///     </para>
    /// </remarks>
    void FollowDisk() {
        if (watcher is null) {
            return;
        }

        changes.Clear();

        var drained = watcher.Drain(changes) > 0;
        var overflowed = watcher.HasOverflowed;

        if (overflowed) {
            watcher.ClearOverflow();
            drained = true;
        }

        if (!drained) {
            return;
        }

        // ⚠ Before the rescan and reading `changes` rather than only its count, which is the one
        // place in this method that cares what changed. A shader edit has to reach the effect tier
        // that compiled the old one, and it is the only kind of change that does — see
        // `ReloadShaders`, which also says why a rebuild is not free enough to do unconditionally.
        ReloadShaders(changes);

        // ⚠ Through the browser when its panel is open and through the project when it is not. The
        // database is the editor's and outlives any panel — a file added while the Project tab is
        // closed has to be in the asset picker and in a build, not only in the tree.
        if (browser is not null) {
            browser.Rescan();
        } else {
            project.Assets.Scan();
            project.Assets.Save();
            project.References.Build(project.Assets);
        }

        RefreshBuildPanel();

        // ⚠ After the rescan, and this is the one ordering constraint in the seam. A path becomes a
        // document through the GUID index, and a rename is exactly the change that moves an entry in
        // it — so routing before the scan would look the new path up in an index that still has the
        // old one and find nothing open. An overflow has no paths to route, so it does the other
        // thing: re-read what can be re-read, on `ReloadShaders`' argument two paragraphs up.
        if (overflowed) {
            external.Rescan();
        } else {
            external.Apply(changes);
        }
    }

    /// <summary>Says what a change on disk did to a document that was open on it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A notification and not a dialog, deliberately.</b> The one case that needs a
    ///         person is a document with unsaved edits whose file changed underneath it, and there is
    ///         no non-destructive default to pick for them — so nothing is picked. Both copies still
    ///         exist and the document says <c>IsStale</c> until somebody picks one. A modal here
    ///         would be one that arrives while somebody is typing, about a file they may not have
    ///         looked at in an hour.
    ///     </para>
    ///     <para>
    ///         The two answers are both gestures: Ctrl+S keeps this document's version, and
    ///         <c>file.revert</c> takes the file's, asking first because it is the destructive one.
    ///         What is still missing is only the shortcut to them — a banner across the document
    ///         itself rather than a message in the corner — and that is a panel rather than a
    ///         mechanism.
    ///     </para>
    /// </remarks>
    void Announce(ExternalEdit edit) {
        var title = edit.Document.Title.Peek();

        switch (edit.Outcome) {
            case ExternalEditOutcome.Reloaded:
                log.Write(LogLevel.Information, $"'{title}' changed on disk and was read again.");
                break;

            case ExternalEditOutcome.Kept:
                Shell.Notifications.Show(
                    $"'{title}' changed on disk",
                    NotificationSeverity.Warning,
                    "It has unsaved edits, so it was left as it is. Save to keep yours, or "
                    + "File ▸ Revert to Saved to take the version on disk."
                );

                break;

            case ExternalEditOutcome.Unsupported:
                log.Write(
                    LogLevel.Information,
                    $"'{title}' changed on disk. This kind of document cannot re-read its file, so it "
                    + "still shows what was opened."
                );

                break;

            case ExternalEditOutcome.Failed:
                Shell.Notifications.Show(
                    $"'{title}' could not be read again",
                    NotificationSeverity.Warning,
                    "Its file changed on disk and reading it back did not work. What is on screen is "
                    + "what was there before."
                );

                break;
        }
    }

    /// <summary>Brings the window's title into line with what is open.</summary>
    /// <remarks>
    ///     Once a frame rather than on a change, for the same reason the selections are polled: the
    ///     dirty flag is a signal nothing here flushes, and comparing three values is not a cost.
    ///     <c>EditorShell.Describe</c> raises nothing unless the composed string actually differs, so
    ///     the host is told only when there is something to tell it.
    /// </remarks>
    void Retitle() => Shell.Describe(scene.Title.Peek(), scene.IsDirty.Value, ProductName);

    /// <summary>A scene with something in it, for a project that has none yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Only when there is no file to open.</b> A first run in an empty project opens
    ///     something rather than an empty tree and a viewport with nothing to look at; the moment
    ///     that scene is saved, this never runs again for that project.
    /// </remarks>
    void Seed() {
        var root = scene.Add("Scene Root", LocalTransform.Identity);

        // ⚠ Both carry the component their name claims, and until the component panel existed
        // neither did. A first run showing a "Directional Light" that is not a light and a "Main
        // Camera" that is not a camera was invisible while nothing drew what was on an entity; it is
        // the first thing somebody clicking those two rows now sees.
        var sun = scene.Add("Directional Light", LocalTransform.At(new Vector3(0f, 3f, 0f)) with {
            Rotation = Aimed.Rotation
        }, root);

        Lights.Attach(world, sun, LightKind.Directional);

        var camera = scene.Add("Main Camera", LocalTransform.At(new Vector3(0f, 1.5f, 6f)), root);

        world.Add(camera, new Camera());

        var ground = scene.Add("Ground", LocalTransform.Identity, root);

        // ⚠ Two of these carry a shape, so a first run shows geometry rather than a grid and four
        // crosses — which is also the only exercise the mesh path gets without somebody having used
        // the menu, and so the way a broken one shows up on launch rather than on a click.
        Shape("Crate", PrimitiveKind.Cube, new Vector3(1.5f, 0.5f, 0f), Vector3.One, ground);
        Shape("Barrel", PrimitiveKind.Cylinder, new Vector3(-2f, 0.5f, 1f), new Vector3(0.8f, 1f, 0.8f), ground);

        // ⚠ Ground stays an empty, and the temptation is a scaled Plane. Two things go wrong with
        // one. Its children inherit the scale, so a crate at 1.5 metres under a ground scaled
        // twelvefold is eighteen metres away and twelve times too big; and the plane sits at exactly
        // the height the floor grid does, so it wins the depth test and the grid vanishes underneath
        // it. Both are correct behaviour and neither is what a first run should be a picture of.
        //
        // The stack starts empty: seeding is not an edit somebody made, and an editor that opened
        // with five undo steps already on it is one where Ctrl+Z does something inexplicable.
        scene.Stack.Clear();
        scene.Stack.MarkClean();

        void Shape(string name, PrimitiveKind kind, Vector3 position, Vector3 scale, Entity parent) {
            var entity = scene.Add(
                name,
                new LocalTransform { Position = position, Rotation = Quaternion.Identity, Scale = scale },
                parent
            );

            PrimitiveShapes.Attach(world, entity, kind);
        }
    }

    /// <summary>Gives every pane of a rearranged layout what only this application can supply.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Run on every rearrangement, because a rearrangement makes new panes.</b>
    ///         <c>ViewportLayout</c> pushes the document and the target factory into new panes itself
    ///         — they are its own properties — and everything here is something it has no business
    ///         knowing about: the picker, the probe, the display scale, where the camera was, and the
    ///         chrome drawn over the top.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The focus handler is added per pane and captures that pane.</b> A handler that
    ///         read the loop variable would give every pane the last one, which is a four-pane layout
    ///         where clicking any pane focuses the fourth — and the symptom is that three of the four
    ///         ignore the keyboard.
    ///     </para>
    /// </remarks>
    void Configure(ViewportLayout layout) {
        chrome ??= new ViewportChrome(Shell);
        chrome.Forget();

        for (var index = 0; index < layout.Panes.Count; index++) {
            var pane = layout.Panes[index];

            pane.Control.RenderScale = RenderScale;

            // ⚠ Without the picker a click in the viewport selects nothing — the picking stage wants
            // a target driven by a render system the editor's viewport does not have, so the only way
            // to select an entity would be the hierarchy panel. Shared across panel rebuilds and
            // across panes, because its cache of shapes is worth keeping and a scene has one answer.
            pane.Picker = picker;
            pane.Surfaces = probe;

            // ⚠ The same source the scene's mesh components already resolve through, so the selection
            // cage cannot disagree with the surface it is drawn round about how big that surface is.
            // Read by `SceneLines` and therefore by both presenters — see `SceneViewport.Meshes`.
            pane.Meshes = SceneGeometry;

            // ⚠ This editor's registry, not the process-wide default the pane falls back to. A pane
            // reading a different registry from the one plugins were handed is a pane whose tool
            // list is empty however many tools were contributed — which is what this line was
            // written for, after exactly that.
            pane.Extensions = Extensions;

            // ⚠ The same instance in both, and the same one every other pane has. A drop and a drag
            // onto the same ramp cannot disagree about whether the thing landing on it stands up,
            // because there is one answer — see `SnapContext`.
            pane.Gizmo.Snap = snap;
            pane.Placement.Snap = snap;

            // The grid is a view of the plane and the plane is the editor's, so a pane rebuilt by a
            // rearrangement comes up drawing the wall the designer was working on rather than the
            // ground.
            pane.Grid.Plane = plane;

            // The same object as the picker and a separate property on purpose — see
            // `ISubObjectPicker`. What it caches is per shape kind, so sharing it across panes is
            // what stops a four-pane layout welding a torus four times.
            pane.SubObjects = picker;

            // Doc 24's P2: which mesh's elements are being selected, shared for the reason above.
            pane.Editing = editing;

            // ⚠ The mode's first refusal on this pane's input, and the adapter is what joins two
            // assemblies that cannot see each other: `IEditorMode` is the shell's and `IViewportInput`
            // is the pane's, and the application is the only thing that knows about both. Shared
            // across panes and across rebuilds, because it holds nothing but the mode registry.
            pane.Input = modeInput ??= new ModeInput(Shell.Modes);

            // ⚠ Restored, because this runs again every time the panel is reopened and a fresh
            // SceneViewport starts at the origin looking down −Z. Absent for a pane of an
            // arrangement nobody has looked at yet, which is what leaves the quad presets alone.
            if (index < cameras.Length && cameras[index] is { } saved) {
                pane.Camera.Restore(saved);
            }

            // ⚠ And what it was drawing, which is the same restore one shelf along: the camera
            // survives a panel being reopened because it is kept in this class, and the show flags
            // survive a *restart* because they are kept in the preferences file. Both are read here
            // because this is the one place a freshly built pane passes through.
            RestoreView(index, pane);

            var focused = pane;

            pane.Control.AddHandler<FocusEvent>(
                (_, args) => {
                    if (args.Gained) {
                        layout.Focus(focused);
                    }
                },
                handledEventsToo: true
            );

            // ⚠ One `ComponentGizmos` per pane, sharing the one bridge list. The bridges are the
            // expensive half and they are the same for every pane; what is per-pane is the show flag
            // and the line list, both of which live on the pane already.
            pane.Gizmos = new(bridges, Extensions);

            chrome.Attach(pane, this);
        }

        // ⚠ After the loop and not inside it, because the panes are what it walks. A rearrangement
        // builds new `SceneViewport`s and therefore new `ViewModes`, which know nothing about the
        // trees the renderer built before they existed — see `RegisterViewModes`.
        RegisterViewModes();
    }

    /// <summary>The toolbar, the stats readout and the rubber-band drawn over each pane.</summary>
    ViewportChrome? chrome;

    /// <summary>What gives the active editor mode first refusal on every pane's input.</summary>
    ModeInput? modeInput;

    void Panels() {
        Shell.RegisterPanel(
            "hierarchy",
            new StringId("editor.panel.hierarchy", "Hierarchy"),
            panel => {
                panel.WhenPressedIn(() => Shell.Context = SceneContext);

                // ⚠ Above the tree and outside it, because a filter is about the panel rather than
                // about the control: the tree is a view of whatever it is handed, and what it is
                // handed is this application's decision. `TreeView` has no filter of its own for
                // the same reason it has no idea what an entity is.
                var bar = panel.Add<UiElement>("outliner-filters");

                var search = bar.Add<SearchBox>();
                search.Placeholder = "Filter by name…";

                search.ValueChanged += (_, value) => {
                    hierarchyFilter = string.IsNullOrWhiteSpace(value) ? null : value;
                    hierarchyStale = true;
                };

                // ⚠ Hierarchy order is first and is the default, because it is the only one that
                // carries information the others destroy: the order of siblings is a thing the user
                // arranged, and a sort that is on by default would hide it permanently.
                var order = bar.Add<Select>();

                foreach (var mode in OutlinerOrders) {
                    order.AddOption(mode);
                }

                order.Value = hierarchyOrder;

                order.SelectionChanged += (_, value) => {
                    hierarchyOrder = value ?? OutlinerOrders[0];
                    hierarchyStale = true;
                };

                hierarchy = panel.Add<TreeView>();
                hierarchy.MultiSelect = true;
                hierarchy.AllowDrag = true;

                // ⚠ The two columns are added on bind rather than built with the row, because the
                // rows are pooled: thirty of them serve a scene of any size, so an eye appended per
                // call would add one per scrolled row for the life of the panel.
                hierarchy.RowBound += MarkRow;

                hierarchy.SelectionChanged += tree => {
                    // ⚠ Ignored while the tree is being brought into line with the selection rather
                    // than the other way round. Restoring the highlight after a rebuild raises this,
                    // and writing it straight back would be the tree overwriting the very selection
                    // it is being told about — which for a click in the viewport means the click is
                    // undone on the next frame.
                    if (hierarchyEchoing) {
                        return;
                    }

                    List<Entity> picked = [];

                    foreach (var node in tree.Selection) {
                        if (node.Tag is Entity entity) {
                            picked.Add(entity);
                        }
                    }

                    scene.Selection.Set(picked);
                };

                hierarchy.Renamed += (_, node, name) => {
                    if (node.Tag is Entity entity) {
                        scene.Rename(entity, name);
                    }
                };

                hierarchy.Moved += (_, node) => Dropped(node);

                // ⚠ Double-click renames, and it is the control's own gesture rather than three
                // lines here. A row's name is the thing you edit in place, in this panel and in the
                // content browser both — see `TreeView.RenameOnActivate`, which is where the two
                // copies of it went. F2 still comes through `edit.rename`, so the keyboard and the
                // pointer cannot disagree about what a rename is.
                hierarchy.RenameOnActivate = true;

                Contextualise(hierarchy, hierarchyMenu ??= HierarchyMenu());
                hierarchyStale = true;
            }
        );

        Shell.RegisterPanel(
            new PanelDescriptor(
                "project",
                new StringId("editor.panel.project", "Project"),
                panel => {
                    panel.WhenPressedIn(() => Shell.Context = AssetContext);

                    browser = new ProjectBrowser(project, panel, Extensions);

                    browser.Activated += Open;
                    browser.Renamed += RenameAsset;
                    browser.Moved += MoveAssets;
                    browser.DroppedOutside += Dropped;
                    browser.DraggedOutside += Dragging;
                    browser.Grabbing += down => grabbingAssets = down;
                    browser.Thumbnails = thumbnails;

                    // ⚠ Restored before the subscription, so putting the toggle back where the user
                    // left it is not itself recorded as the user having moved it — and written on
                    // change rather than on the way down, because closing the *panel* is one of the two
                    // ways the choice was being lost and nothing runs on that.
                    browser.IsGrid = preferences.ProjectGridView;

                    browser.ViewChanged += grid => {
                        preferences.ProjectGridView = grid;
                        WritePreferences();
                    };

                    browser.TileSize = preferences.ProjectTileSize;

                    browser.TileSizeChanged += size => {
                        preferences.ProjectTileSize = size;
                        WritePreferences();
                    };

                    // ⚠ Both views, and the grid was the one that had nothing. The menu was attached to
                    // the tree alone, so switching to tiles — which is the view somebody browses
                    // *assets* in — left right-click doing nothing at all: no Create, no Import, no
                    // Rename, no Show in Explorer. One menu over two elements, because the verbs act on
                    // the project's selection and both views write it.
                    assetMenu ??= AssetMenu();

                    Contextualise(browser.Tree, assetMenu);
                    Contextualise(browser.Grid, assetMenu);
                }
            ) {
                // ⚠ The other half of holding it, and the crash it prevents is not hypothetical.
                // `Update` calls `SyncSelection` every frame, which marks the grid's tiles — and a
                // tile whose panel has been closed has been removed from the document, so asking it
                // for its state throws. It went unnoticed for as long as the browser opened as a
                // tree, because the tree's own highlight is written through `TreeView` and the grid
                // was the half nothing touched. See `PanelDescriptor.Closed`.
                //
                // It is also what the null checks behind `browser is not null` have always meant:
                // the enablement of Rename, Reveal and Select Dependencies is "the Project panel is
                // open", and while this was never cleared the answer was "it has been open once".
                Closed = () => browser = null
            }
        );

        Shell.RegisterPanel(
            new PanelDescriptor(
                "scene",
                new StringId("editor.panel.scene", "Scene"),
                panel => {
                    // ⚠ The panel must not scroll, and this is the case that would be a bug rather
                    // than an annoyance. A viewport sizes its render target from its own laid-out box
                    // and turns a pointer into a pick with `(x - AbsoluteLeft) * RenderScale` — so an
                    // offset the panel applied and the viewport knew nothing about would move every
                    // click a scroll's worth away from what the user aimed at, on a panel that never
                    // needed to scroll because the viewport fills it by construction.
                    panel.Scrolls = false;

                    // ⚠ The one panel that does not report a constant, and the editor mode is why. A
                    // mode is "a statement about what the viewport's input means right now" — doc
                    // 20's A1 — and the way Blockout claims 1, 2, 3 and 4 from view-bookmark recall
                    // without taking them from anywhere else is by being the context the pane reports
                    // while it is active. A mode with no context of its own — Select — leaves the
                    // pane reporting `SceneContext`, which is the editor exactly as it was. That is
                    // why `WhenPressedIn` reads the claim on every press rather than capturing one.
                    //
                    // The other half is `RegisterModes`: entering a mode claims the context without
                    // waiting for a press, because somebody who has just pressed the Blockout button
                    // has aimed at the viewport and should not have to click it as well.
                    panel.WhenPressedIn(() => Shell.Context = Shell.Modes.Context ?? SceneContext);

                    // ⚠ A layout rather than a control, and every pane in it is a whole
                    // `SceneViewport`. Doc 11 asks for "multiple simultaneous viewports with
                    // independent cameras and render modes", and the second half of that is what
                    // forces it: a view mode is stage state, so a pane that wanted its own would
                    // silently change its neighbour's.
                    viewports = new ViewportLayout(panel, scene.Selection) {
                        Document = scene,
                        // ⚠ The element modes take the gizmo over, and the factory is where that
                        // happens rather than in the pane: what a gizmo drags is exactly what a mode
                        // says is selected, and doc 24's P2 is that inside a mesh that is a set of
                        // its corners rather than the entity round them.
                        TargetsFactory = () =>
                            editing.IsActive && !editing.Selection.IsEmpty
                                ? [new MeshGizmoTarget(scene, editing)]
                                : EntityGizmoTarget.For(world, scene.Selection),
                        Arrangement = arrangement
                    };

                    // ⚠ Subscribed after the constructor and then called by hand, because the
                    // constructor's own `Rebuild` raises this before there is anything to hear it —
                    // and the panes it made are the ones that need configuring.
                    viewports.Rearranged += Configure;
                    Configure(viewports);
                }
            ) {
                // ⚠ The other half of the factory. A closed Scene tab takes every pane's control
                // with it, and `Update` walking them on the next frame asks a removed element for
                // its width — see `PanelDescriptor.Closed`.
                Closed = () => {
                    viewports?.Dispose();
                    viewports = null;
                }
            }
        );

        Shell.RegisterPanel(
            new PanelDescriptor(
                "inspector",
                new StringId("editor.panel.inspector", "Inspector"),
                panel => {
                    // ⚠ `InspectorView` owns a scroll region of its own and keeps its header out of
                    // it on purpose, so that the search box and the lock cannot scroll away from
                    // somebody who is using them to find the row they scrolled past. A panel that
                    // scrolled the whole view would put the header back inside a scroller and give
                    // the wheel two bars to choose between.
                    panel.Scrolls = false;

                    inspector = panel.Add<InspectorView>();
                    inspector.EditedDocument = scene;

                    // This editor's registry rather than the process-wide default — see `Configure`.
                    inspector.Extensions = Extensions;

                    // ⚠ Under the inspector's rows rather than inside its model. `InspectorView` draws
                    // the members of one described type; which *types* are on an entity is a different
                    // question, and one it deliberately cannot ask — see `ComponentsView`. What it does
                    // share is the scroll region: an entity with six components is longer than any
                    // panel, and two independent scroll regions would leave half the answer off screen
                    // whichever one you moved.
                    components = inspector.Scroll.Content.Add<ComponentsView>();
                    components.Attach(scene, bridges, Extensions);

                    // ⚠ Restored before the subscription, so putting the foldouts back where the user
                    // left them is not itself recorded as a rearrangement. The order is a preference
                    // rather than anything about the entity — see `ComponentsView.Order` — which is why
                    // it lives in the preferences file and not in the scene.
                    components.Order = preferences.ComponentOrder;

                    components.Reordered += arranged => {
                        preferences.ComponentOrder = [.. arranged];
                        WritePreferences();
                    };

                    // ⚠ After it is in the tree, because the menu is a child of the document root and a
                    // control has no document until it is added to one.
                    inspector.Contextualise();

                    // ⚠ The panel refused every selection while it was locked, so it is showing
                    // something stale the moment the lock comes off — and nothing else would tell it,
                    // because the selection has not changed since.
                    inspector.LockChanged += view => {
                        if (!view.IsLocked) {
                            ShowSelection();
                        }
                    };

                    // The rows were built against the previous instance of this panel, so what is
                    // selected has to be pushed into the new one rather than waited for — and from
                    // whichever selection the inspector was already following, which is what the two
                    // fields behind `ShowSelection` are for.
                    ShowSelection();
                }
            ) {
                // ⚠ The other half of holding both. `FollowSelection` runs every frame and hands
                // whatever is selected to the inspector — so a panel closed while the editor is
                // running leaves this field pointing at a view whose elements have been removed, and
                // the next selection change throws from inside `Rebuild`. Applying a preset that
                // does not name the inspector is now one of the ways it closes, which is how this
                // came to matter. See `PanelDescriptor.Closed`.
                Closed = () => {
                    inspector = null;
                    components = null;
                }
            }
        );

        Shell.RegisterPanel(
            "console",
            new StringId("editor.panel.console", "Console"),
            panel => {
                panel.WhenPressedIn(() => Shell.Context = ConsoleContext);

                console = panel.Add<ConsoleView>();

                // ⚠ One model, kept here, rather than one per panel. A panel's factory runs again
                // every time it is reopened — see this class's own remarks — and a fresh model starts
                // at the sink's current end, so closing and reopening the console would empty it.
                // The buffer, the filters and the collapse state are all the user's and survive.
                consoleModel ??= new ConsoleModel(log.Sink);
                console.Show(consoleModel);

                console.Activated += (_, record) => Reveal(record);
            }
        );
    }

    /// <summary>Opens an asset in whatever editor claims it, for a caller outside this class.</summary>
    /// <remarks>
    ///     The same path a double-click in the browser takes — see <see cref="Open(AssetId)" /> — and
    ///     named so that a test can take it without pretending to be a pointer.
    /// </remarks>
    internal void OpenAsset(AssetId asset) => Open(asset);

    /// <summary>Opens an asset in whatever editor claims it, in a panel of its own.</summary>
    /// <param name="asset">Which asset.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A panel per document, registered on demand and named after the asset's GUID.</b> A
    ///         path would be shorter and would make moving the file leave a panel nobody can reopen;
    ///         the GUID is the identity precisely so that it does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Registered once and reopened afterwards.</b> The workspace refuses a second
    ///         registration under one id, and the document is already found by
    ///         <c>AssetEditorRegistry.TryOpen</c> — so the second double-click brings the tab forward
    ///         rather than building a second view over the same undo stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A file nothing claims is a notification, not silence.</b> Double-clicking a
    ///         <c>.txt</c> and having nothing at all happen reads as a broken editor; saying that no
    ///         editor claims it is one sentence and is true.
    ///     </para>
    /// </remarks>
    void Open(AssetId asset) {
        if (!editors.TryOpen(project, asset, out var document)) {
            Shell.Notifications.Show("No editor claims that file.");

            return;
        }

        var id = AssetPanel(asset);

        if (assetPanels.Add(id)) {
            var title = document.Title.Peek();

            Shell.RegisterPanel(
                id,
                new StringId("editor.panel." + id, title),
                panel => {
                    if (project.TryGetDocument(asset, out var open)
                        && editors.TryGetForFile(project.Assets.TryGetByGuid(asset, out var entry) ? entry.Path : title, out var editor)) {
                        Joined(editor.CreateView(open, panel));
                    }
                }
            );
        }

        Shell.Workspace.Open(id);
        project.Activate(document);
    }

    /// <summary>Connects an asset editor's view to the things only this assembly can answer.</summary>
    /// <param name="view">Whatever the factory built.</param>
    /// <remarks>
    ///     ⚠ <b>Here rather than in the factory, because the request is for another document.</b> A
    ///     material's "Open shader graph" carries an <c>AssetId</c> and stops —
    ///     <c>Vixen.Editor.AssetEditors</c> has a registry but no panels, no docking and no way to
    ///     bring a tab forward — so the button raised an event nothing listened to until a shader
    ///     graph editor existed to open. Every asset editor's factory runs again on a reopen, so this
    ///     runs again with it and subscribes the new view rather than a dead one.
    /// </remarks>
    void Joined(UiElement view) {
        if (view is AssetEditors.Materials.MaterialView material) {
            material.OpenGraphRequested += (_, graph) => Open(graph);
        }

        // ⚠ And the three AI editors, whose `Follow` methods had no non-test caller at all. The
        // model belongs to the module that owns the agent debugger — see `AgentDebuggerPanel.Follow`
        // for why the joining is there and the trigger is here.
        assetEditors?.Follow(view);
    }

    /// <summary>What a panel showing an asset's editor is called in an arrangement.</summary>
    /// <remarks>
    ///     The GUID rather than the path, so that moving the file does not leave a panel nobody can
    ///     reopen — the identity is a GUID precisely so that it does not.
    /// </remarks>
    const string AssetPrefix = "asset.";

    /// <inheritdoc cref="AssetPrefix" />
    static string AssetPanel(AssetId asset) => AssetPrefix + asset;

    /// <summary>Reads an asset panel's id back, which is what restoring an arrangement needs.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the formatter, because a format written in one place and parsed in another is
    ///     a format with two owners.</b> The parse is the half a saved layout depends on, and it was
    ///     the half that could drift silently: a mismatch does not fail, it just never restores a
    ///     document.
    /// </remarks>
    static bool TryReadAssetPanel(string id, out AssetId asset) {
        asset = default;

        return id.StartsWith(AssetPrefix, StringComparison.Ordinal)
            && AssetId.TryParse(id[AssetPrefix.Length..], out asset);
    }

    /// <summary>Reopens the document a restored arrangement names, so the panel can be built.</summary>
    /// <returns>Whether the id was an asset panel this project can open.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's A6, and it is one function rather than a list in the layout file.</b> An
    ///         asset editor's panel is registered on demand and named after the asset's GUID, so a
    ///         saved arrangement holding <c>asset.9e8a44c9…</c> named a panel nothing had declared —
    ///         the tab came back missing and the id stayed in the file. <c>DockingWorkspace.Resolve</c>
    ///         asks this before giving up, and <see cref="Open" /> registers the panel as a
    ///         side-effect of opening the document, which is the same path a double-click takes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off by preference, because reopening six asset editors costs six documents.</b>
    ///         <see cref="EditorPreferences.RestoreOpenDocuments" /> is on by default — "the editor
    ///         comes back how I left it" is what doc 20 asks for — and somebody who wants a clean
    ///         start can say so rather than deleting a layout file.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An asset that has gone answers false rather than throwing.</b> A layout outlives
    ///         the files it names — a branch switch is enough — and the id is left in the arrangement
    ///         on the same terms a plugin's unregistered panel is.
    ///     </para>
    /// </remarks>
    bool ReopenDocument(string id) {
        if (!preferences.RestoreOpenDocuments
            || !TryReadAssetPanel(id, out var asset)
            || !project.Assets.TryGetByGuid(asset, out _)) {
            return false;
        }

        Open(asset);
        return assetPanels.Contains(id);
    }

    /// <summary>Finds and starts the plugins, and says what it found.</summary>
    /// <param name="directory">The user's data directory, which holds the second plugin folder.</param>
    /// <returns>The host, kept so that the plugins can be unloaded on the way down.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Two roots, project before user</b>, so a plugin checked into a repository beats
    ///         the copy the user installed globally and everybody on a team gets the same tools.
    ///         Neither folder normally exists, which is not an error.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the errors are shown.</b> A notification per plugin would put four toasts
    ///         over the editor on every launch of a project that has plugins and is working; what
    ///         the user needs to be told is the one that did not start.
    ///     </para>
    ///     <para>
    ///         ✅ <b>Importers are published now, and the change was the one this remark asked
    ///         for.</b> It used to say there was no registry here to add to, because importers are
    ///         built per run — <c>ContentPipeline</c> calls <c>ProjectWorkspace.Importers()</c> inside
    ///         the background task, deliberately, so the editor and the CLI cannot disagree about the
    ///         set. What was needed was a set that <i>outlives</i> a run, in <c>Vixen.Editor.Assets</c>
    ///         rather than here: <c>ImporterContributions</c>, folded in by <c>BuiltInImporters</c>.
    ///         Doc 36 § F8.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Build steps are still the same shape and are still not published.</b> Nothing
    ///         holds a set of them that survives a run, so the same change is owed there and has not
    ///         been made — saying so here rather than leaving the reader to notice the asymmetry.
    ///     </para>
    /// </remarks>
    PluginServices PluginPoints() =>
        new PluginServices()
            .Add(project)
            .Add(scene)

            // The static the inspector reads by default, so a plugin's drawer is found by the panel
            // that is already open rather than by one built afterwards.
            .Add(DrawerRegistry.Default)

            // ⚠ And the importers, on the same terms and for the same reason. Doc 36 § F8: every
            // registry an import runs against is built by `BuiltInImporters.Create`, which folds this
            // in — so a plugin's importer reaches the editor's own import, the Project panel's type
            // filter and the CLI's in-process path. It does not reach an out-of-process compiler
            // worker, which has not loaded the plugin; `ImporterContributions` says so.
            .Add(ImporterContributions.Default)

            // ⚠ Doc 36 § D2's registry, and the reason the list above is no longer the extent of what
            // a plugin can reach. Every contribution kind — a Create ▸ entry, a custom inspector, a
            // scene-view tool, a gizmo, a settings page, a preview — goes through this one service,
            // so publishing it once is what widens the surface rather than a service per kind.
            .Add(Extensions)

            // ⚠ And the four a built-in module asks for, which are here because they are this
            // application's to own. The editing state and the work plane are shared across every
            // pane and outlive every scene — doc 24 § D5 — and the two mesh services are the asset
            // database's: what turns a baked file into an asset, and what reads a mesh reference
            // back. A module that cannot get one is refused with its name in the message.
            .Add(editing)
            .Add(plane)
            .Add<IMeshBaker>(new ProjectMeshBaker(Project))

            // ⚠ And doc 48 § D12's, under its own interface for the reason below. A mesh map is a
            // measurement of geometry that lands in `Assets/` as a file an artist opens, so a module
            // that generates or retopologises meshes has the same relationship to it that the
            // block-out mode has to the mesh baker: it knows what it wants baked and nothing about
            // where a GUID comes from.
            .Add<IMeshMapBaker>(meshMaps)

            // ⚠ Under the interface, not under the implementation. `PluginServices` keys on the
            // static type it is handed, so publishing this as a `ProjectMeshSource` would mean a
            // module asking for the contract finding nothing — and being refused, correctly and
            // confusingly, for a service that is right there.
            .Add<IMeshSource>(SceneGeometry)

            // ⚠ Which scene the editor is *showing*, which is not the same question as which scene is
            // open: a panel counting entities while a prefab is inspected has to count the prefab. A
            // contract rather than the document itself, because the answer moves and a module handed
            // one at activation would hold the scene that was open when it loaded.
            .Add<IActiveScene>(new ShownScene(this))

            // ⚠ And which eye it is showing it through, which is a separate question with a separate
            // answer: doc 39's resolved volume stack is per camera, because the volume fold weighs
            // every volume by how far the camera is from it. A panel with the scene and no view can
            // only answer for the origin, which is a place nobody is standing.
            .Add<IActiveView>(new ShownView(this))

            // And what Deploy means, for the half of the editor that can build a player.
            .Add<IDeviceDeploy>(new PlayerDeploy(this))

            // ⚠ And the asset-editor registry, so a module can hear that a document was opened. That
            // used to be `Bound`, a line in this class — see `AssetEditorsModule`.
            .Add(editors)

            // ⚠ Doc 36 § P4: the reload host, so a module's markup panel follows an edit to its
            // `.vxml` without the editor being restarted. The host existed in
            // `Core/Vixen.Ui.HotReload` and nothing in the editor had ever created one — the
            // declarative path was reloadable in principle and not in this application, which is F7
            // with an extra step.
            .Add(hotReload);

    void StartPlugins(string directory) {
        var report = plugins.Load(
            PluginDiscovery.Scan(
                Path.Combine(project.Paths.Root, PluginsFolder),
                Path.Combine(directory, PluginsFolder)
            )
        );

        foreach (var diagnostic in report.Diagnostics.Where(diagnostic => diagnostic.Severity == PluginSeverity.Error)) {
            Shell.Notifications.Error(diagnostic.PluginId, diagnostic.Message);
        }

        if (report.Activated.Count > 0) {
            Shell.Notifications.Show(
                $"{report.Activated.Count} plugin(s) loaded",
                NotificationSeverity.Success,
                string.Join(", ", report.Activated.Select(plugin => plugin.Manifest.Name))
            );
        }
    }

    /// <summary>Unloads every installed plugin and loads it again from disk.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A plugin that does not go away is reported.</b> Its replacement loads either way —
    ///         refusing would make one badly-behaved plugin block the whole reload — but the old copy
    ///         is still in memory with its statics in it, and that is the failure the runtime says
    ///         nothing about. Restarting the editor is the only cure and the notification says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The editor's own modules are left alone, and this is the one place that
    ///         distinction is made.</b> <c>PluginHost.Reload</c> will re-activate a built-in
    ///         perfectly well — that is what the Reload button on its row in the manager does — but
    ///         this command exists for one loop: build a plugin over the folder the editor is
    ///         watching, reload, look. Nothing on disk changed for Blockout or Terrain, so tearing
    ///         down every mode, panel and tool the editor ships in order to rebuild them identically
    ///         is churn with a failure mode and no upside.
    ///     </para>
    /// </remarks>
    void ReloadPlugins() {
        var reloaded = 0;
        var leaked = new List<string>();

        var installed = plugins.Plugins
            .Where(plugin => plugin.State == PluginState.Active && !plugin.IsBuiltIn)
            .Select(plugin => plugin.Id)
            .ToList();

        foreach (var id in installed) {
            var report = plugins.Reload(id);

            if (!plugins.WaitForCollection(id, TimeSpan.FromSeconds(2))) {
                leaked.Add(id);
            }

            foreach (var diagnostic in report.Diagnostics.Where(diagnostic => diagnostic.Severity == PluginSeverity.Error)) {
                Shell.Notifications.Error(diagnostic.PluginId, diagnostic.Message);
            }

            reloaded += report.Activated.Count;
        }

        if (leaked.Count > 0) {
            Shell.Notifications.Show(
                "Plugins did not unload cleanly",
                NotificationSeverity.Warning,
                string.Join(", ", leaked) + " — the previous version is still in memory. Restart to clear it."
            );
        }

        // ⚠ "Nothing to reload" rather than "0 plugin(s) reloaded". An editor with no third-party
        // plugins installed is the ordinary case, and a success toast reading zero is one people
        // read as a failure.
        Shell.Notifications.Show(
            installed.Count == 0
                ? "No installed plugins to reload"
                : $"{reloaded} plugin(s) reloaded",
            NotificationSeverity.Success
        );
    }

    void Layouts() {
        // The five doc 11 names. They differ in which panels they show and how the middle is split,
        // which is the whole of what a layout preset is — the shapes come from `LayoutPresets` and
        // the panel lists are this application's.
        // ⚠ Project is a tab *behind* the console rather than under the hierarchy, and the two halves
        // of that are one decision. A content browser is read in tiles — a grid of thumbnails wants
        // width, and a quarter-width left column gives it two tiles a row — while the hierarchy is a
        // list of names and wants height. Stacking them in the left column made each of them the
        // other's problem. The console keeps the front tab because it is the one that has something
        // to say without being asked.
        Shell.RegisterLayout(
            "Default",
            new StringId("editor.layout.default", "Default"),
            () => LayoutPresets.Standard(
                ["hierarchy"],
                ["scene"],
                // ⚠ Build Settings beside the inspector rather than in the middle. It is a form that
                // is read down, which is the shape the right-hand column already has — and it is
                // opened *against* a scene rather than instead of one, so a preset that gave it the
                // centre would put the viewport behind a tab.
                ["inspector", BuildPanel],
                ["console", "project"]
            )
        );

        Shell.RegisterLayout(
            "Scene",
            new StringId("editor.layout.scene", "Scene"),
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        Shell.RegisterLayout(
            "Shading",
            new StringId("editor.layout.shading", "Shading"),
            () => LayoutPresets.Standard(["project"], ["scene"], ["inspector"], ["console"])
        );

        Shell.RegisterLayout(
            "Animation",
            new StringId("editor.layout.animation", "Animation"),
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"], ["console"])
        );

        Shell.RegisterLayout(
            "Debug",
            new StringId("editor.layout.debug", "Debug"),
            () => LayoutPresets.Split(["scene"], ["console"], 0.6f)
        );

        // ⚠ Doc 20's A6 owes two more presets, "once Parts B4 and B5 exist". B4 exists now, so this
        // one does. The shape is deliberate and is not the Default's: profiling is a reading rather
        // than an edit, so the viewport is the *narrow* column and the numbers get the width — a
        // flame chart squeezed into a right-hand inspector slot is one where every bar is a pixel.
        Shell.RegisterLayout(
            "Profiling",
            new StringId("editor.layout.profiling", "Profiling"),
            () => LayoutPresets.Standard(
                ["scene"],
                ["profiler", "gpu", "frame-debugger"],
                ["statistics", "memory"],
                ["console"]
            )
        );

        // ⚠ And the second of the two A6 owes, now that B5 exists. Its shape is the Profiling
        // preset's argument turned round: a cinematic is authored *against* the viewport, so the
        // scene keeps the width and the tracks get a wide, short strip under it — a timeline in a
        // right-hand slot is one where a two-second shot is forty pixels.
        Shell.RegisterLayout(
            "Sequencing",
            new StringId("editor.layout.sequencing", "Sequencing"),
            () => LayoutPresets.Standard(["hierarchy", "scenes"], ["scene"], ["inspector"], ["console"])
        );

        Shell.Workspace.DefaultPreset = "Default";
    }

    void Commands() {
        Shell.Commands.Add(
            // Through the same request the window's close button goes through, so the menu, the
            // shortcut and the title-bar button all ask about unsaved work rather than two of three.
            new EditorCommand("file.exit", EditorStrings.CommandExit, RequestClose) {
                Category = EditorStrings.CategoryFile
            }
        );

        Shell.Commands.Add(
            new EditorCommand("view.save-layout", EditorStrings.CommandSaveLayout, SaveLayout) {
                Category = EditorStrings.CategoryView
            }
        );

        // Enabled only when there is something to write, so the menu item greys itself out from the
        // document's own dirty signal rather than from anything here deciding when.
        Shell.Commands.Add(
            new EditorCommand("file.save", new StringId("editor.command.save", "Save Scene"), SaveScene) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => scene.IsDirty.Value
            }
        );

        Shell.Keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        // Enabled only while the panel is open, because the browser is what holds the tree — and a
        // rescan with nowhere to show the result is a menu item that appears to do nothing.
        Shell.Commands.Add(
            new EditorCommand(
                "assets.refresh",
                new StringId("editor.command.refresh-assets", "Refresh Assets"),
                RefreshAssets
            ) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => browser is not null
            }
        );

        Shell.Keys.SetDefault("assets.refresh", new KeyChord(InputKey.R, ModifierKeys.Control));

        // Both greyed out while either is running: they write the same sidecars, artefact store and
        // cache file, and two at once corrupts `Library/` rather than merely producing a worse build.
        Shell.Commands.Add(
            new EditorCommand(
                "assets.import",
                new StringId("editor.command.import-assets", "Import Assets"),
                content.Import
            ) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => !content.IsBusy
            }
        );

        Shell.Commands.Add(
            new EditorCommand(
                "assets.build",
                new StringId("editor.command.build-content", "Build Content"),
                content.Build
            ) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => !content.IsBusy
            }
        );

        Shell.Keys.SetDefault("assets.build", new KeyChord(InputKey.B, ModifierKeys.Control | ModifierKeys.Shift));

        Shell.Commands.Add(
            new EditorCommand("help.about", EditorStrings.CommandAbout, About) {
                Category = EditorStrings.CategoryHelp
            }
        );

        // The plugin-development loop, reachable from the palette. Enabled only when there is
        // something to reload, so an editor with no plugins does not offer it.
        Shell.Commands.Add(
            new EditorCommand(
                "plugins.reload",
                new StringId("editor.command.reload-plugins", "Reload Plugins"),
                ReloadPlugins
            ) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => plugins.Plugins.Any(plugin => plugin.State == PluginState.Active)
            }
        );

        EditCommands();
        SceneCommands();

        // ⚠ After the scene commands and before the toolbar, and both halves matter. Some of these
        // put a keybinding on a command `SceneCommands` registered — Ctrl+Shift+N onto Create Empty —
        // which needs the command to exist; and the toolbar is built from ids, which needs every one
        // of these to exist.
        ParityCommands();

        // ⚠ A separate method rather than seven more lines inside `ParityCommands`, because these
        // are the ids that were declared-and-disabled there until E4 built the panels behind them.
        // Keeping them together is what makes "which verbs does the diagnostics milestone own"
        // answerable by reading one method.

        // ⚠ And E5's, for the same reason and on the same terms: these are the ids that were
        // declared-and-disabled until this milestone built the panels behind them.
        WorldCommands();
        DiagnosticsCommands();

        Shell.Keys.SetDefault("file.exit", new KeyChord(InputKey.Q, ModifierKeys.Control));

        // ⚠ Doc 24's D5's last sentence: "the grid I can see" and "the grid I snap to" are one number.
        // Asked of the plane on demand rather than copied into `GridStep` when it changes.
        snap.Plane = plane;

        RegisterModes();
        ParityToolbar();

        // The saved arrangements are a palette source rather than a menu, because there is no bound
        // on how many of them somebody makes and a menu with forty lines in it is a list nobody
        // reads.
        Shell.Palette.AddSource(
            new DelegatePaletteSource(
                "Layout",
                () => store.Layouts(),
                name => {
                    if (store.LoadLayout(name) is { } layout) {
                        Shell.Workspace.Load(layout);
                    }
                }
            )
        );

        SceneMenu();
    }

    /// <summary>Undo and redo, over whichever document is active.</summary>
    /// <remarks>
    ///     Bound to the document's stack rather than to a global one, and enabled from the stack's
    ///     own signal — which is what makes the menu item grey itself out with no code here saying
    ///     when. <c>UndoName</c> is read for the same reason: the label is "Undo Set Roughness"
    ///     because the command said so, not because a menu was told.
    /// </remarks>
    void EditCommands() {
        Shell.Commands.Add(
            new EditorCommand("edit.undo", EditorStrings.CommandUndo, () => scene.Stack.Undo()) {
                Category = EditorStrings.CategoryEdit,
                Enablement = () => scene.Stack.CanUndo.Value
            }
        );

        Shell.Commands.Add(
            new EditorCommand("edit.redo", EditorStrings.CommandRedo, () => scene.Stack.Redo()) {
                Category = EditorStrings.CategoryEdit,
                Enablement = () => scene.Stack.CanRedo.Value
            }
        );

        Shell.Keys.SetDefault("edit.undo", new KeyChord(InputKey.Z, ModifierKeys.Control));
        Shell.Keys.SetDefault("edit.redo", new KeyChord(InputKey.Z, ModifierKeys.Control | ModifierKeys.Shift));
    }

    /// <summary>What the viewport can be told to do, as commands rather than as a second keymap.</summary>
    /// <remarks>
    ///     Every one of these goes through the command registry, so it appears in the palette, can be
    ///     rebound, and greys itself out when there is no viewport — which is the whole reason
    ///     <c>SceneViewport</c> has no bindings of its own.
    /// </remarks>
    void SceneCommands() {
        // Enabled from the selection, so the menu item greys itself out with nothing here polling.
        Shell.Commands.Add(
            new EditorCommand(
                "scene.create-entity",
                new StringId("editor.command.create-entity", "Create Empty"),
                CreateEntity
            ) {
                Category = EditorStrings.CategoryEdit
            }
        );

        // ⚠ Delete and Rename are `edit.delete` and `edit.rename`, registered in `EditingCommands`
        // and scoped to the outliner. They used to be `scene.*` ids with the Delete key and F2 on
        // them; the move is what lets the content browser's twins claim the same keys, and it is why
        // this menu, the Edit menu and the hierarchy's context menu now all name the one pair rather
        // than two commands with the same label.
        ShapeCommands();
        ObjectCommands();

        // ⚠ Ticked, and that is what makes the three modes read as one choice rather than as three
        // buttons. A menu of Translate, Rotate and Scale with nothing saying which is current is one
        // where the only way to find out what a drag will do is to drag — and the tick costs a
        // predicate, which both the menu and the toolbar already ask for.
        Mode("scene.translate", "Translate", GizmoMode.Translate, InputKey.W);
        Mode("scene.rotate", "Rotate", GizmoMode.Rotate, InputKey.E);
        Mode("scene.scale", "Scale", GizmoMode.Scale, InputKey.R);

        // ⚠ These two say which state they are <i>in</i> rather than what pressing them does, and
        // that is the convention every 3D editor uses for exactly this pair. A button that read
        // "Local Space" in both spaces left the only way to find out which one a drag was in being
        // to drag — and a tick beside a fixed noun does not answer it either, because the reader has
        // to know whether the label names the current state or the one the click would move to.
        Add(
            "scene.toggle-space",
            "Local Space",
            pane => pane.Gizmo.Space = pane.Gizmo.Space == GizmoSpace.World ? GizmoSpace.Local : GizmoSpace.World,
            on: pane => pane.Gizmo.Space != GizmoSpace.World,
            caption: pane => pane.Gizmo.Space == GizmoSpace.World ? "World Space" : "Local Space"
        );

        Add(
            "scene.toggle-pivot",
            "Pivot at Centre",
            pane => pane.Gizmo.Pivot = pane.Gizmo.Pivot == PivotMode.Pivot ? PivotMode.Center : PivotMode.Pivot,
            on: pane => pane.Gizmo.Pivot == PivotMode.Center,
            caption: pane => pane.Gizmo.Pivot == PivotMode.Center ? "Pivot at Centre" : "Pivot at Object"
        );

        Add(
            "scene.toggle-snap",
            "Snapping",
            pane => {
                var on = !pane.Gizmo.Snap.SnapPosition;

                pane.Gizmo.Snap.SnapPosition = on;
                pane.Gizmo.Snap.SnapRotation = on;
                pane.Gizmo.Snap.SnapScale = on;
            },
            on: pane => pane.Gizmo.Snap.SnapPosition
        );

        // ⚠ The show flag, not `SceneGrid.Enabled`. The grid keeps its own switch for a host with no
        // show flags, and the editor writes exactly one of the two — see `SceneViewport.Show`, and
        // doc 20's rule about a preferences window and a menu tick disagreeing.
        Add(
            "scene.toggle-grid",
            "Grid",
            pane => pane.Show ^= SceneShow.Grid,
            on: pane => (pane.Show & SceneShow.Grid) != 0
        );

        // The rest of doc 20's E2 verbs: view modes, the other show flags, the pane count, camera
        // speed, the nine bookmarks and maximise.
        ViewportCommands();

        Add(
            "scene.toggle-projection",
            "Orthographic",
            pane => pane.Camera.IsOrthographic = !pane.Camera.IsOrthographic,
            on: pane => pane.Camera.IsOrthographic,
            key: InputKey.Keypad5
        );

        // ⚠ The three navigation preferences, as ticked commands rather than as a dialog. Which
        // point an orbit swings around is the one people notice within a minute of opening a scene
        // and cannot otherwise change; the other two are the settings the same people ask for next.
        // A palette entry and a menu tick is the whole of what a preference needs before there is a
        // preferences window to put it in — and it is what makes them rebindable and searchable.
        Add(
            "scene.orbit-around-selection",
            "Orbit Around Selection",
            pane => pane.OrbitAround =
                pane.OrbitAround == OrbitPivot.Selection ? OrbitPivot.View : OrbitPivot.Selection,
            on: pane => pane.OrbitAround == OrbitPivot.Selection
        );

        Add(
            "scene.zoom-to-cursor",
            "Zoom to Mouse Position",
            pane => pane.ZoomToCursor = !pane.ZoomToCursor,
            on: pane => pane.ZoomToCursor
        );

        Add(
            "scene.invert-orbit-y",
            "Invert Orbit Y",
            pane => pane.Camera.InvertOrbitY = !pane.Camera.InvertOrbitY,
            on: pane => pane.Camera.InvertOrbitY
        );

        Add("scene.focus", "Focus Selection", pane => pane.FocusSelection(SelectionBounds()), key: InputKey.F);
        Add("scene.frame-all", "Frame All", pane => pane.Camera.Focus(SceneBounds()), key: InputKey.A);

        // ⚠ The one reserved chord in the editor, and it earns it. Focus Selection is pressed several
        // times a minute in every mode, and a mode that bound the same key for its own context —
        // blockout's Fill Hole did — made it stop working in exactly the mode where somebody is
        // looking around the most. `KeyMap.Reserve` files the *command*, so rebinding it moves the
        // protection with it.
        Shell.Keys.Reserve("scene.focus");

        // The six axis views on the six numpad keys every 3D editor puts them on, opposites included.
        // Half of them existed and half did not, which meant the front view had a key and the back
        // view could only be reached by orbiting a hundred and eighty degrees by hand.
        View("scene.view-front", "Front View", ViewDirection.Front, InputKey.Keypad1);
        View("scene.view-back", "Back View", ViewDirection.Back, InputKey.Keypad9);
        View("scene.view-right", "Right View", ViewDirection.Right, InputKey.Keypad3);
        View("scene.view-left", "Left View", ViewDirection.Left);
        View("scene.view-top", "Top View", ViewDirection.Top, InputKey.Keypad7);
        View("scene.view-bottom", "Bottom View", ViewDirection.Bottom);

        // ⚠ In degrees, through `Turn`. A keyboard orbit expressed as a pointer drag would move when
        // the orbit speed is tuned and reverse when somebody sets "invert orbit Y" — and a key that
        // says "turn left" has no business being affected by a preference about the mouse.
        Step("scene.orbit-left", "Orbit Left", 15f, 0f, InputKey.Keypad4);
        Step("scene.orbit-right", "Orbit Right", -15f, 0f, InputKey.Keypad6);
        Step("scene.orbit-up", "Orbit Up", 0f, 15f, InputKey.Keypad8);
        Step("scene.orbit-down", "Orbit Down", 0f, -15f, InputKey.Keypad2);

        void Mode(string id, string label, GizmoMode mode, InputKey key) =>
            Add(id, label, pane => pane.Gizmo.Mode = mode, pane => pane.Gizmo.Mode == mode, key);

        void View(string id, string label, ViewDirection direction, InputKey key = InputKey.Unknown) =>
            Add(id, label, pane => pane.Camera.LookFrom(direction), key: key);

        void Step(string id, string label, float yaw, float pitch, InputKey key) =>
            Add(
                id,
                label,
                pane => pane.Camera.Turn(MathUtil.DegreesToRadians(yaw), MathUtil.DegreesToRadians(pitch)),
                key: key
            );

        void Add(
            string id,
            string label,
            Action<SceneViewport> action,
            Func<SceneViewport, bool>? on = null,
            InputKey key = InputKey.Unknown,
            Func<SceneViewport, string>? caption = null
        ) {
            Shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => {
                    if (Viewport is { } pane) {
                        action(pane);
                    }
                }
                ) {
                    Category = new StringId("editor.category.scene", "Scene"),
                    Enablement = () => Viewport is not null,

                    // ⚠ Null when the command is not a toggle, rather than a predicate that answers
                    // false. `MenuPresenter` grows the tick column only for commands that have one,
                    // so a lambda here would indent every line of the Scene menu by an empty tick.
                    Checked = on is null ? null : () => Viewport is { } pane && on(pane),

                    // ⚠ And null when the name does not move, which is all but two of these. See
                    // `EditorCommand.Caption`: a delegate asked per button per frame is not free,
                    // and the id has to stay the *same* string whatever the label says — it is what
                    // the keymap, the palette and the menu model all name.
                    Caption = caption is null
                        ? null
                        : () => new StringId(
                            "editor.command." + id,
                            Viewport is { } pane ? caption(pane) : label
                        )
                }
            );

            if (key != InputKey.Unknown) {
                Shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.None));
            }
        }
    }

    /// <summary>The Scene menu, and what the application adds to the shell's own.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The menu is described after the commands are registered, not before.</b> A menu
    ///         entry naming a command nothing has registered is skipped when the bar is built — which
    ///         is the behaviour that lets the shell name <c>file.save</c> without owning it, and
    ///         which would silently swallow every line of this menu if the order were the other way
    ///         round.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inserted rather than appended.</b> <c>MenuModel.AddMenu</c> puts a menu at the
    ///         end of the bar, which for the shell's default set is after Help — and a menu bar
    ///         reading File, Edit, View, Help, Scene is one where the most-used menu in a 3D editor
    ///         is past the point where people stop looking.
    ///     </para>
    /// </remarks>
    /// <summary>Puts a dropdown's id list on a menu, with its rules kept as separators.</summary>
    /// <remarks>
    ///     ⚠ <b>One list, two views.</b> The snap, work-plane and precision popovers on the viewport
    ///     strip and the submenus here are the same commands in the same order with the same grouping,
    ///     and they are the same array — a second copy is two places to add the next verb to, and the
    ///     one nobody remembers is the menu.
    /// </remarks>
    static void Fill(MenuGroup group, IReadOnlyList<string?> ids) {
        foreach (var id in ids) {
            if (id is null) {
                group.AddSeparator();
            } else {
                group.Add(id);
            }
        }
    }

    void SceneMenu() {
        // ⚠ First, because this is what puts Entity on the bar and the line below counts to it.
        // Assets, Entity, Play, Build and Tools are doc 20's Part C menus made of this application's
        // verbs; the shell keeps File, Edit, Window and Help.
        ParityMenus();

        // ⚠ After Entity, which is doc 20's Part C order: File, Edit, Assets, Entity, Scene, Play,
        // Window, Build, Tools, Help. Found by counting to the Entity menu rather than by a literal,
        // so that a menu added to the shell's default bar does not silently put Scene somewhere else.
        var menu = Shell.Menus.InsertMenu(
            Index(EditorStrings.MenuEntity) + 1,
            new StringId("editor.menu.scene", "Scene")
        );

        menu.Add("scene.create-entity");

        // The same submenus the hierarchy's context menu offers, from the same command ids and the
        // same code — which is the point of them being commands rather than something a menu does
        // for itself, and what stops the two drifting apart the next time one is added to.
        Creatable(menu);

        menu.Add("edit.rename", "edit.delete").AddSeparator();

        menu.AddSubmenu(new StringId("editor.menu.gizmo", "Gizmo"))
            .Add("scene.translate", "scene.rotate", "scene.scale")
            .AddSeparator()
            .Add("scene.toggle-space", "scene.toggle-pivot", "scene.toggle-snap");

        // ⚠ Its own submenu rather than more lines under Gizmo, because doc 24's D4 is that snapping
        // is a service above the gizmo rather than a setting on it — and a menu that filed it under
        // the tool would be the arrangement that view objects to, one level up.
        Fill(menu.AddSubmenu(new StringId("editor.menu.snap", "Snapping")), ViewportIds.SnapIds);

        // The work plane and the precision tools, which are doc 24's D5 and its "placement and
        // precision" group. Both are about where you are building rather than about what is selected.
        Fill(menu.AddSubmenu(new StringId("editor.menu.work-plane", "Work Plane")), ViewportIds.WorkPlaneIds);
        Fill(menu.AddSubmenu(new StringId("editor.menu.precision", "Measure")), ViewportIds.PrecisionIds);

        // ⚠ Doc 24's five blockout submenus used to be here and are now `BlockoutModule`'s, which
        // inserts them at this point through `PluginContext.AddSubmenu` — after Measure, where doc 24
        // § D5's placement-and-precision group ends. A feature that could only append would have
        // reordered this menu the day it stopped being compiled in.

        menu.AddSubmenu(new StringId("editor.menu.camera", "Camera"))
            .Add("scene.view-front", "scene.view-back")
            .Add("scene.view-right", "scene.view-left")
            .Add("scene.view-top", "scene.view-bottom")
            .AddSeparator()
            .Add("scene.toggle-projection");

        // ⚠ Its own submenu rather than three more lines on the Camera one. These are preferences —
        // they change what every future drag does rather than doing anything now — and mixing them
        // in with the six view keys would make a menu where half the entries move the camera and
        // half of them silently change how it moves.
        menu.AddSubmenu(new StringId("editor.menu.navigation", "Navigation"))
            .Add("scene.orbit-around-selection", "scene.zoom-to-cursor", "scene.invert-orbit-y")
            .AddSeparator()
            .Add("scene.orbit-left", "scene.orbit-right", "scene.orbit-up", "scene.orbit-down");

        // ⚠ Five submenus rather than five flat runs, and the grouping is doc 20's Part C. What each
        // of them holds is unbounded in the same way the Create menu is — nine view modes, eight show
        // flags, nine bookmarks — and a Scene menu with thirty-one more lines on it is one where the
        // six things people use every minute are past the point they stop reading.
        menu.AddSubmenu(new StringId("editor.menu.panes", "Viewport Layout")).Add(ViewportIds.Arrangements);

        menu.AddSubmenu(new StringId("editor.menu.view-mode", "View Mode")).Add(ViewportIds.ViewModes);

        // The grid's toggle in front of the rest, for the reason `ViewportIds.ShowFlagIds` gives: it
        // is the one show flag that had a command before there were show flags.
        menu.AddSubmenu(new StringId("editor.menu.show", "Show"))
            .Add("scene.toggle-grid")
            .AddSeparator()
            .Add(ViewportIds.ShowFlagIds);

        // ⚠ Recall above save, which is the order they are used in. Nine "Set View n" lines at the
        // top of a submenu is nine lines to scroll past every time somebody wants the view they saved.
        menu.AddSubmenu(new StringId("editor.menu.bookmarks", "Bookmarks"))
            .Add(ViewportIds.GoBookmarks)
            .AddSeparator()
            .Add(ViewportIds.SetBookmarks);

        menu.AddSubmenu(new StringId("editor.menu.speed", "Camera Speed")).Add(ViewportIds.SpeedIds);

        menu.AddSeparator().Add("scene.focus", "scene.frame-all");

        // ⚠ Doc 20's B6, on the Scene menu rather than on a menu of its own. Every one of them is
        // about *this level* — its sky, its lighting budget, its agent size, which scenes are open
        // beside it — and a "World" menu would be a second place people have to learn to look for
        // something the Scene menu is already named after.
        menu.AddSeparator()
            .Add("scene.world-settings", "scene.lighting", "scene.navigation", "scene.layers")
            .AddSeparator()
            .Add("scene.scenes", "scene.open-additive", "scene.save-all-scenes");

        menu.AddSeparator().Add("scene.maximise");

        // ⚠ Rebuilt here rather than left to the one a registration triggers, which is why this runs
        // last. Every `Commands.Add` and every `Keys.SetDefault` above rebuilt the bar against a
        // model that did not yet have this menu in it, and nothing after this point registers either.
        Shell.MenuBar.Rebuild();
    }

    /// <summary>Where a menu with a title sits on the bar.</summary>
    int Index(StringId title) {
        for (var index = 0; index < Shell.Menus.Menus.Count; index++) {
            if (Shell.Menus.Menus[index].Title.Id == title.Id) {
                return index;
            }
        }

        return Shell.Menus.Menus.Count - 1;
    }

    /// <summary>One command per built-in shape, so that spawning one is not the menu's private trick.</summary>
    /// <remarks>
    ///     ⚠ <b>Eight commands rather than one that takes an argument.</b> The registry's unit is a
    ///     command with an id, a title and an enablement — that is what the palette searches, what the
    ///     keymap binds and what a menu line is built from — so "Create Cube" being findable in the
    ///     palette and bindable to a key means it has to be its own entry. Generated from
    ///     <see cref="PrimitiveShapes.All" />, so a shape added there appears everywhere without anything
    ///     here being edited.
    /// </remarks>
    void ShapeCommands() {
        foreach (var kind in PrimitiveShapes.All) {
            var shape = kind;
            var name = PrimitiveShapes.NameOf(shape);

            Shell.Commands.Add(
                new EditorCommand(
                    ShapeCommandId(shape),
                    new StringId("editor.command.create-" + name.ToLowerInvariant(), name),
                    () => CreateShape(shape)
                ) {
                    Category = new StringId("editor.category.create", "Create")
                }
            );
        }
    }

    /// <summary>What a shape's create command is called in the registry.</summary>
    static string ShapeCommandId(PrimitiveKind kind) =>
        "scene.create-" + PrimitiveShapes.NameOf(kind).ToLowerInvariant();

    /// <summary>One command per kind of light, and one for a camera.</summary>
    /// <remarks>
    ///     ⚠ <b>Commands rather than menu lines, which is <see cref="ShapeCommands" />' argument
    ///     applied to the other half of a Create menu.</b> "Create Point Light" being findable in the
    ///     palette and bindable to a key is what makes it a thing the editor can do, rather than a
    ///     thing one menu happens to offer — and it is why the Scene menu and the hierarchy's context
    ///     menu can both name it without either of them owning it.
    /// </remarks>
    void ObjectCommands() {
        foreach (var kind in Lights.All) {
            var light = kind;
            var title = Lights.TitleOf(light);

            Shell.Commands.Add(
                new EditorCommand(
                    LightCommandId(light),
                    new StringId("editor.command.create-light-" + Lights.NameOf(light).ToLowerInvariant(), title),
                    () => CreateLight(light)
                ) {
                    Category = new StringId("editor.category.create", "Create")
                }
            );
        }

        Shell.Commands.Add(
            new EditorCommand(
                "scene.create-camera",
                new StringId("editor.command.create-camera", "Camera"),
                CreateCamera
            ) {
                Category = new StringId("editor.category.create", "Create")
            }
        );
    }

    /// <summary>What a light's create command is called in the registry.</summary>
    static string LightCommandId(LightKind kind) =>
        "scene.create-light-" + Lights.NameOf(kind).ToLowerInvariant();

    /// <summary>Everything a Create menu offers, written once for the two menus that offer it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Grouped into submenus rather than listed flat, and that is not tidiness.</b>
    ///         Thirteen create lines in the same list as Rename, Delete and Focus is a menu where the
    ///         two destructive entries are somewhere in the middle of a wall of nouns — and the wall
    ///         grows with every kind of thing the engine learns to make.
    ///     </para>
    ///     <para>
    ///         <b>Camera is a line rather than a submenu of one.</b> A submenu that opens onto a
    ///         single item is a second click for nothing, and it invites the reader to wonder what
    ///         else is in there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is absent is absent on purpose.</b> There is no UI, sprite, terrain or
    ///         particle entry because the runtime has no component for any of them — <c>Vixen.Ui</c>
    ///         is a document tree with no world-space bridge, and the others do not exist at all. A
    ///         line that created an entity called "Canvas" carrying nothing would be a menu that lies
    ///         about what the engine can do, and the bug reports it earns are about the editor rather
    ///         than about the gap. They belong here the moment there is something for them to attach.
    ///     </para>
    /// </remarks>
    static void Creatable(MenuGroup menu) {
        var shapes = menu.AddSubmenu(new StringId("editor.menu.create-shape", "3D Object"));

        foreach (var kind in PrimitiveShapes.All) {
            shapes.Add(ShapeCommandId(kind));
        }

        var lights = menu.AddSubmenu(new StringId("editor.menu.create-light", "Light"));

        foreach (var kind in Lights.All) {
            lights.Add(LightCommandId(kind));
        }

        menu.Add("scene.create-camera");
    }

    /// <summary>The menu a secondary click in the hierarchy opens.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Built once and re-attached, rather than built per panel.</b> A panel's factory
    ///         runs again every time it is reopened, and a menu is a child of the document root — so
    ///         one built in the factory would leak an invisible overlay, still listening for pointer
    ///         events, every time somebody closed and reopened the hierarchy. The handler goes on the
    ///         tree, which <i>is</i> thrown away with the panel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The click selects before the menu opens.</b> Every line here acts on the
    ///         selection, and a right-click on a row that was not selected otherwise creates the cube
    ///         under whatever was selected before — which looks like the menu ignoring the row it was
    ///         opened on. A click already inside the selection leaves it alone, so right-clicking one
    ///         of five selected rows still means all five.
    ///     </para>
    /// </remarks>
    static void Contextualise(TreeView tree, ContextMenu menu) {
        tree.AddHandler<PointerEvent>(
            (_, args) => {
                if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Secondary }) {
                    return;
                }

                var node = tree.NodeAt(args.X, args.Y);

                if (node is null || !tree.Selection.Contains(node)) {
                    // Null clears it, which is what a click on empty space should mean: the next
                    // Create Empty then makes a root rather than a child of whatever was last picked.
                    tree.Select(node);
                }
            },

            // ⚠ Registered before the menu's own handler and marked to run regardless, because that
            // one marks the event handled — and this has to happen first either way, or the commands
            // the menu enables are decided from the old selection.
            handledEventsToo: true
        );

        menu.Attach(tree);
    }

    /// <summary>The same menu, over the content browser's tiles.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection is written from the press for <see cref="Contextualise(TreeView,
    ///     ContextMenu)" />'s reason</b> — every line on the menu acts on it — and a press on the
    ///     background clears it, so Create makes a file in the folder being looked at rather than
    ///     beside whatever was clicked last. The grid's own <c>Selected</c> event cannot do this
    ///     job: it fires for a primary press, and a right-click that did not also select would open
    ///     a menu about a different asset.
    /// </remarks>
    void Contextualise(AssetGrid grid, ContextMenu menu) {
        grid.AddHandler<PointerEvent>(
            (_, args) => {
                if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Secondary }) {
                    return;
                }

                if (grid.TileAt(args.X, args.Y)?.Node is { IsIndexed: true } asset) {
                    if (!project.Selection.Contains(asset.Guid)) {
                        project.Selection.Set([asset.Guid]);
                    }

                    return;
                }

                project.Selection.Set([]);
            },

            // Before the menu's own handler and regardless of it having marked the event handled,
            // which is the tree's arrangement and for the same reason: the commands the menu enables
            // are decided from the selection this writes.
            handledEventsToo: true
        );

        menu.Attach(grid);
    }

    /// <summary>What a secondary click in the content browser opens.</summary>
    /// <remarks>
    ///     ⚠ <b>Every line is a registered command, so this menu and the Assets menu on the bar
    ///     cannot disagree.</b> That is the point of the registry rather than a nicety: the two
    ///     menus, the palette and the keymap are four views over one list, and a browser that built
    ///     its own verbs would be the place where Delete means something different.
    /// </remarks>
    ContextMenu AssetMenu() {
        var group = new MenuGroup(new StringId("editor.menu.browser", "Project"));

        group.Add("assets.open");
        group.AddSeparator();

        // ⚠ The same Create submenu the Assets menu on the bar carries, from the same ids — which is
        // what the registry is for and is why this is three lines rather than a second list. A
        // browser whose context menu could make a folder but not a material is one where the seven
        // asset kinds are reachable only from a menu at the top of the window, several inches from
        // the folder somebody has just navigated into and right-clicked in.
        group.AddSubmenu(EditorStrings.MenuCreate)
            .Add("assets.new-folder", "assets.create")
            .AddSeparator()
            .AddDynamic(() => CreatableIds);

        group.Add("assets.import-files");
        group.AddSeparator();
        group.Add("assets.rename", "assets.delete", "assets.move-to");
        group.AddSeparator();
        group.Add("assets.reimport-all", "assets.bake-mesh-maps", "assets.show-in-explorer");

        return MenuPresenter.Context(Shell.Document, group, Shell.Commands, Shell.Keys);
    }

    /// <summary>What is on the outliner's menu.</summary>
    ContextMenu HierarchyMenu() {
        var group = new MenuGroup(new StringId("editor.menu.hierarchy", "Hierarchy"));

        group.Add("scene.create-entity");
        Creatable(group);

        group.AddSeparator();
        group.Add("edit.rename", "edit.delete");
        group.AddSeparator();
        group.Add("entity.toggle-hidden", "entity.toggle-lock");
        group.AddSeparator();
        group.Add("scene.focus");

        return MenuPresenter.Context(Shell.Document, group, Shell.Commands, Shell.Keys);
    }

    /// <summary>Renames whatever the hierarchy has selected, in place.</summary>
    /// <remarks>
    ///     Through the tree's own inline editor rather than a dialog, so that the menu line and F2 do
    ///     the same thing — and so the commit goes through <c>TreeView.Renamed</c>, which is already
    ///     wired to the document's undoable rename.
    /// </remarks>
    void Rename() {
        if (hierarchy is not { } tree || scene.Selection.Count == 0) {
            return;
        }

        var entity = scene.Selection[0];

        foreach (var node in Descendants(tree.Root)) {
            if (node.Tag is Entity tagged && tagged == entity) {
                tree.BeginRename(node);
                return;
            }
        }
    }

    static IEnumerable<TreeNode> Descendants(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }

    /// <summary>Brings the inspector into line with whichever panel the selection changed in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Several selections and one inspector, so something has to arbitrate.</b> There is
    ///         one per open scene — the editor's own, and one more for every scene or prefab opened
    ///         as an asset — and one for the project browser. Only the first was ever read: a click
    ///         in the Project panel, or in the hierarchy of a scene opened from it, moved a highlight
    ///         and ended there. Showing them together is not an option either;
    ///         <c>InspectorRegistry.CommonType</c> draws nothing for a selection with no single type
    ///         in it, which an entity and a texture do not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The one that changed is the one that was clicked in, and it wins.</b> Two
    ///         changing in one frame is this method's own doing — clearing a loser is itself a change
    ///         — and there the one that gained something is the click.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The losers this application owns the views for are cleared rather than left
    ///         highlighted.</b> Two panels showing a selection while the inspector can only show one
    ///         is a picture that lies about which of them the next Delete, the next gizmo drag or the
    ///         next rename will act on. A document opened as an asset is the exception and not an
    ///         oversight: its hierarchy owns its own rows and takes selection outwards only — see
    ///         <c>SceneHierarchyView</c> — so clearing its document's selection from here would leave
    ///         a row highlighted with nothing behind it, which is worse than leaving it be.
    ///     </para>
    /// </remarks>
    void FollowSelection() {
        // ⚠ Not while a gesture in the browser is still running. Pressing an asset row selects it,
        // and handing the inspector over on that frame destroys the field a drag was aimed at long
        // before the drag has begun — see `grabbingAssets`. The hand-over is a click's doing, and a
        // click is not over until the button comes up.
        if (grabbingAssets) {
            return;
        }

        CollectScenes();

        // A document that has been closed cannot be what the inspector is showing, and its snapshot
        // would keep it alive for as long as the editor runs.
        var closed = Forget();

        SceneDocument? changed = null;

        foreach (var document in scenes) {
            if (Differs(Snapshot(document), document.Selection)) {
                changed = document;
                break;
            }
        }

        var assets = Differs(shownAssets, project.Selection);

        if (changed is null && !assets && !closed) {
            return;
        }

        inspectingAssets = changed is null || (assets && project.Selection.Count > 0);

        if (inspectingAssets) {
            DeselectEntities();
        } else {
            inspected = changed;
            DeselectAssets();

            // The editor's own scene keeps the gizmo and the hierarchy pointed at it, so it is only
            // dropped when the click was somewhere else entirely.
            if (!ReferenceEquals(changed, scene)) {
                DeselectEntities();
            }
        }

        // ⚠ Snapshotted after the clears rather than before, so that the changes this method just
        // made to the losing selections are not read as clicks in those panels on the next frame —
        // which would hand the inspector straight back to the panel the user had just left.
        foreach (var document in scenes) {
            var snapshot = Snapshot(document);

            snapshot.Clear();
            snapshot.AddRange(document.Selection);
        }

        shownAssets.Clear();
        shownAssets.AddRange(project.Selection);

        ShowSelection();
    }

    /// <summary>Fills <see cref="scenes" /> with every scene the editor has open, its own first.</summary>
    /// <remarks>
    ///     ⚠ <b>Into a list this object keeps, because this runs every frame.</b> The class's own
    ///     remarks say a comparison of a handful of handles once a frame is not a cost; a pair of
    ///     lists allocated per frame for the rest of the session would be a different claim.
    ///     <para>
    ///         Its own scene is put in explicitly rather than trusted to be somewhere particular in
    ///         <see cref="EditorProject.Documents" />: it is the one that must win a tie, because it
    ///         is the one the scene panel and the gizmo are looking at.
    ///     </para>
    /// </remarks>
    void CollectScenes() {
        scenes.Clear();
        scenes.Add(scene);

        foreach (var document in project.Documents) {
            if (document is SceneDocument open && !ReferenceEquals(open, scene)) {
                scenes.Add(open);
            }
        }
    }

    /// <summary>The snapshot of a document's selection, made on first sight.</summary>
    List<Entity> Snapshot(SceneDocument document) {
        if (!watched.TryGetValue(document, out var snapshot)) {
            watched[document] = snapshot = [];
        }

        return snapshot;
    }

    /// <summary>Drops the snapshots of documents that are no longer open.</summary>
    /// <returns>Whether the one the inspector was showing was among them.</returns>
    /// <remarks>
    ///     The count is checked first so that the ordinary frame — nothing opened, nothing closed —
    ///     does not walk the dictionary at all.
    /// </remarks>
    bool Forget() {
        if (watched.Count <= scenes.Count) {
            return false;
        }

        var lost = false;

        foreach (var document in watched.Keys.Where(document => !scenes.Contains(document)).ToList()) {
            watched.Remove(document);

            if (ReferenceEquals(inspected, document)) {
                inspected = null;
                lost = true;
            }
        }

        return lost;
    }

    /// <summary>Whether a selection differs from the snapshot of it the inspector was built from.</summary>
    static bool Differs<T>(List<T> shown, IReadOnlyList<T> selection) where T : notnull {
        if (shown.Count != selection.Count) {
            return true;
        }

        for (var index = 0; index < shown.Count; index++) {
            if (!EqualityComparer<T>.Default.Equals(shown[index], selection[index])) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops the scene selection, through the hierarchy when it is open.</summary>
    /// <remarks>
    ///     The rows' highlight is the tree's own state and the document's selection is written from
    ///     it, so clearing only the document would leave a row that looks selected. With the panel
    ///     closed there is no tree to clear and the document is all there is.
    /// </remarks>
    void DeselectEntities() {
        if (scene.Selection.Count == 0) {
            return;
        }

        if (hierarchy is { } tree) {
            tree.Select(null);
        } else {
            scene.Selection.Clear();
        }
    }

    /// <inheritdoc cref="DeselectEntities" />
    void DeselectAssets() {
        if (project.Selection.Count == 0) {
            return;
        }

        if (browser is { } open) {
            open.Deselect();
        } else {
            project.Selection.Clear();
        }
    }

    /// <summary>Puts whatever is selected into the inspector, and names it in the status bar.</summary>
    /// <remarks>
    ///     One view object per selected thing, made fresh every time: each holds a handle and the
    ///     model it reads through and nothing else, so keeping a cache of them would be bookkeeping
    ///     in exchange for nothing.
    /// </remarks>
    void ShowSelection() {
        if (inspectingAssets) {
            if (inspector is { } assetView) {
                // ⚠ Dropped rather than left pointing at the last entity. An asset did not come from a
                // prefab, and a stale pairing keyed by reference would answer for whatever boxed value
                // happened to land on the same object.
                assetView.Prefab = null;
            }

            inspector?.Inspect([.. project.Selection.Select(asset => new ProjectAsset(project, asset))]);

            // An asset has no components, and leaving the last entity's foldouts under it would be a
            // panel showing two different things at once.
            components?.Pair(null);
            components?.Show(Entity.Null);

            Shell.Status = project.Selection.Count switch {
                0 => ProductName,
                1 => project.Assets.TryGetByGuid(project.Selection[0], out var entry) ? entry.Name : ProductName,
                _ => $"{project.Selection.Count} selected"
            };

            return;
        }

        var document = inspected ?? scene;

        // ⚠ One per selection rather than one per editor, and this is what feeds doc 47 § 7's row 6.
        // It pairs the objects below with the entities they stand for, so the panel's override marks
        // and its Revert item read the instance's own claim list — `SceneDocument.Prefabs` — instead
        // of comparing values, which is the model doc 47 § 3 rejects. Fresh each time because it
        // caches the prefab files it read, and a prefab saved between two selections must be re-read.
        var sources = new PrefabSource(document, project.Assets);

        if (inspector is { } view) {
            // ⚠ The document whose entities these are, not the editor's own scene. An inspector edit
            // is recorded on the stack of the document it changed, and a scene opened as an asset
            // has one of its own — so an edit made here with the wrong document set would be undone
            // by a Ctrl+Z aimed at something else entirely.
            view.EditedDocument = document;
            view.Prefab = sources;

            List<object> shown = [];

            foreach (var entity in document.Selection) {
                var target = new SceneEntity(document, entity);

                sources.Link(target, entity);
                shown.Add(target);
            }

            // ⚠ Assigned before `Inspect`, because building the rows is what asks the source whether
            // each member is overridden. Setting it afterwards would draw one unmarked panel and only
            // start telling the truth at the next selection.
            // ⚠ Spread rather than passed, because `Inspect` takes `params ReadOnlySpan<object>` and a
            // `List<object>` binds to it as one element — an inspector showing the list.
            view.Inspect([.. shown]);
        }

        // ⚠ Only this editor's own scene. The foldouts write through `scene.Stack`, and a document
        // opened as an asset has a stack of its own — showing its entity's components here would put
        // the edit on the wrong one, which is the hazard the line above guards for the rows.
        components?.Pair(ReferenceEquals(document, scene) ? sources : null);

        components?.Show(
            ReferenceEquals(document, scene) && document.Selection.Count > 0 ? document.Selection[0] : Entity.Null
        );

        Shell.Status = document.Selection.Count switch {
            0 => ProductName,
            1 => document.NameOf(document.Selection[0]),
            _ => $"{document.Selection.Count} selected"
        };
    }

    /// <summary>Says where a drag out of the browser has got to, so the target can light up.</summary>
    /// <remarks>
    ///     The inspector's fields are the only thing that answers: the viewport takes a drop anywhere
    ///     in it and needs no aiming, and an outline round the whole of it during every drag across
    ///     the window would be noise.
    /// </remarks>
    void Dragging(IReadOnlyList<AssetId> assets, float x, float y) =>
        assetDrop.Over(Shell.Document.Root, assets, x, y);

    /// <summary>Resolves a drag released outside the browser: an asset field first, then the scene.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The field is asked first, and a field that <i>refused</i> the drop still consumes
    ///         it.</b> The alternative is falling through to the scene, so that dragging a texture
    ///         onto a mesh field spawns an entity in the middle of the level — a thing the user has to
    ///         notice and then undo, in exchange for an assignment they did not get either way. A drop
    ///         aimed at a field belongs to that field whatever it decides.
    ///     </para>
    ///     <para>
    ///         The panels cannot overlap, so "aimed at a field" and "aimed at the scene" are never
    ///         both true and the order is a formality rather than a policy. It is written down because
    ///         a floating inspector window over the viewport would make it one.
    ///     </para>
    /// </remarks>
    void Dropped(IReadOnlyList<AssetId> assets, float x, float y) {
        var landed = assetDrop.Drop(Shell.Document.Root, assets, x, y);

        if (!landed.IsHandled) {
            DropIntoScene(assets, x, y);
            return;
        }

        // ⚠ The selection the press made is swallowed rather than acted on, and without this the
        // panel jumps to the dropped asset the frame after the drop — hiding the row that just
        // changed, which is the one thing the user was looking at. `FollowSelection` reads a change
        // against these snapshots, so bringing them up to date is how a change is un-noticed; the
        // method does the same thing to its own clears, for the same reason.
        shownAssets.Clear();
        shownAssets.AddRange(project.Selection);

        switch (landed.Outcome) {
            case AssetFieldDropOutcome.Assigned:
                Shell.Notifications.Show($"{landed.Member} is {landed.Asset}", NotificationSeverity.Success);
                break;

            // ⚠ Said out loud rather than silently ignored. A drag that ends on a field and changes
            // nothing looks identical to one the editor dropped on the floor, and the difference —
            // "this is not that kind of asset" — is the one thing the user needs in order to know
            // what to drag instead.
            case AssetFieldDropOutcome.WrongKind:
                Shell.Notifications.Show(
                    $"{landed.Member} does not take {landed.Asset ?? "that"}",
                    NotificationSeverity.Warning,
                    "The field names one kind of asset. Its picker lists what it will take."
                );

                break;

            case AssetFieldDropOutcome.TooMany:
                Shell.Notifications.Show(
                    $"{landed.Member} names one asset",
                    NotificationSeverity.Warning,
                    $"{assets.Count} were dropped on it, and choosing between them is not this gesture's to make."
                );

                break;

            // Already named it, or the member's condition means the edit reaches nothing. Neither is
            // a failure and neither changed anything, so neither is worth a notification.
            case AssetFieldDropOutcome.Unchanged:
            case AssetFieldDropOutcome.NotAField:
            default:
                break;
        }
    }

    /// <summary>Puts assets into the scene, when a drag from the browser was released over it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The drop is refused unless it landed on the viewport or the outliner.</b> A drag
    ///         that ends over the console, the inspector or nothing at all means the user changed
    ///         their mind, and an editor that spawned an entity for it would be one people learn to
    ///         drag carefully in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it makes is an entity carrying an <c>AssetInstance</c>, and that is a
    ///         reference rather than a renderer.</b> Nothing in the runtime turns an asset into
    ///         geometry yet — see <c>PrimitiveShape</c>'s remarks for why it lives in the editor at all —
    ///         so the crate does not appear in the viewport. What is real is everything else: the
    ///         entity is named after the asset, the reference is authored and saved, the inspector's
    ///         asset field shows and can change it, and <c>ReferenceIndex</c> counts it, so deleting
    ///         the asset now warns about the scene.
    ///     </para>
    ///     <para>
    ///         One undo step for the whole drop, however many assets it carried: dropping four things
    ///         is one gesture, and four undos for it is four presses to take back one mistake.
    ///     </para>
    /// </remarks>
    void DropIntoScene(IReadOnlyList<AssetId> assets, float x, float y) {
        if (assets.Count == 0 || !OverScene(x, y)) {
            return;
        }

        List<Entity> created = [];
        List<PrefabUnresolved> refused = [];

        using (scene.Stack.BeginTransaction(assets.Count == 1 ? "Add Asset" : $"Add {assets.Count} Assets")) {
            foreach (var asset in assets) {
                if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
                    continue;
                }

                // ⚠ A prefab is placed rather than referenced, and it is the only kind of asset for
                // which the two differ. Every other drop makes one entity naming the asset; a prefab
                // *is* a subtree, so what a drop of one means is "stamp that subtree out here and
                // remember where each entity came from" — which is what makes the `prefab`, `source`
                // and `overrides` keys have anything to hold. See docs/plan/47 § 7.
                if (EditorPrefab.Claims(entry.Path)) {
                    if (EditorPrefab.TryPlace(scene, project.Assets, asset, Entity.Null, out var root, out var why)) {
                        created.Add(root);
                    } else {
                        refused.Add(why);
                    }

                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(entry.Name);

                created.Add(
                    scene.Create(
                        string.IsNullOrEmpty(name) ? entry.Name : name,
                        LocalTransform.Identity,
                        Entity.Null,
                        entity => AssetInstances.Attach(world, entity, asset)
                    )
                );
            }
        }

        // ⚠ Said out loud rather than silently ignored, for the reason a field drop of the wrong kind
        // is. A prefab whose file has not been written yet, or has gone, drops onto the floor — and
        // "nothing happened" is indistinguishable from a drag the editor lost.
        foreach (var why in refused) {
            Shell.Notifications.Show(
                "That prefab could not be opened",
                NotificationSeverity.Warning,
                $"{why.Kind}: {why.Detail}"
            );
        }

        if (created.Count == 0) {
            return;
        }

        // Selected afterwards, so the next thing the user does — rename it, move it, look at it in
        // the inspector — lands on what they just made.
        scene.Selection.Set(created);
        hierarchyStale = true;

        Shell.Notifications.Show(
            created.Count == 1 ? $"Added {scene.NameOf(created[0])}" : $"Added {created.Count} entities",
            NotificationSeverity.Success,
            "The reference is authored and saved. Nothing draws it yet — no runtime component renders an asset."
        );
    }

    /// <summary>Says what reconciling a freshly-opened scene against its prefabs did.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Silent when nothing moved, and that is most of the time.</b> A level whose prefabs
    ///         have not been touched since it was last saved reconciles to nothing, and a notification
    ///         for that is one people learn to dismiss without reading — which is how the one that
    ///         mattered gets dismissed too.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A warning when something could not be settled, and only then.</b> Values taking
    ///         their template's is the system working: a prefab changed and the level followed it.
    ///         What needs a person is the residue — an entity the template deleted, an override naming
    ///         a member nothing has, a prefab that could not be opened at all — every one of which is
    ///         still in the file, untouched, because a reconcile writes values and removes nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A child added back is counted with the values and not with the residue.</b> It
    ///         travels as a <c>PrefabReport</c> because it is worth saying — it is the one thing a
    ///         reconcile does that a diff of the next save will show as lines nobody typed — but it is
    ///         not something anybody has to settle, and counting it as one would put a warning on every
    ///         level that correctly followed a prefab.
    ///     </para>
    /// </remarks>
    void Announce(SceneDocument document) {
        if (document.Reconciled is not { Changed: true } report) {
            return;
        }

        List<string> lines = [];

        if (report.Written > 0) {
            lines.Add(
                $"{report.Written} members took the template's value across {report.Instances} linked entities."
            );
        }

        var added = 0;

        foreach (var problem in report.Reports) {
            if (problem.Kind == PrefabReportKind.AddedByTemplate) {
                added++;
            }
        }

        if (added > 0) {
            lines.Add($"{added} entities were added from their prefabs.");
        }

        // Named rather than counted, up to a few: "three things could not be resolved" is a sentence
        // nobody can act on, and the names are what say which prefab to go and look at.
        foreach (var unresolved in report.Unresolved.Take(4)) {
            lines.Add($"{unresolved.Kind}: {unresolved.Detail}");
        }

        foreach (var problem in report.Reports.Where(entry => entry.Kind != PrefabReportKind.AddedByTemplate).Take(4)) {
            lines.Add(problem.ToString());
        }

        var outstanding = report.Reports.Count - added + report.Unresolved.Count;

        if (outstanding > 8) {
            lines.Add($"and {outstanding - 8} more.");
        }

        Shell.Notifications.Show(
            outstanding == 0
                ? $"{document.Title.Peek()} caught up with its prefabs"
                : $"{document.Title.Peek()}: {outstanding} left for you to settle",
            outstanding == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning,
            string.Join(" ", lines)
        );
    }

    /// <summary>Whether a point is over a panel that means "the scene".</summary>
    /// <remarks>
    ///     The viewport and the outliner both, because both are the scene: one is what it looks like
    ///     and the other is what is in it, and a person dragging into either means the same thing.
    /// </remarks>
    bool OverScene(float x, float y) {
        foreach (var panel in Panels(Shell.Document.Root)) {
            if (panel.Id is not ("scene" or "hierarchy")) {
                continue;
            }

            var bounds = panel.Bounds;

            if (x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height) {
                return true;
            }
        }

        return false;
    }

    static IEnumerable<DockPanel> Panels(UiElement element) {
        if (element is DockPanel panel) {
            yield return panel;
        }

        foreach (var child in element.Children) {
            foreach (var found in Panels(child)) {
                yield return found;
            }
        }
    }

    /// <summary>What a drag in the outliner meant, as an undoable move of the document.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tree has already moved the node by the time this runs, and the document has
    ///         not.</b> <c>TreeView</c> owns the gesture and its own rows; the scene owns what is
    ///         true. So this reads where the node landed, tells the document, and marks the tree
    ///         stale either way — a move the document refuses (a cycle, an entity already there)
    ///         would otherwise leave the outliner showing a hierarchy the scene does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole selection moves when the dragged row is part of it.</b> The same rule
    ///         the context menu follows, and for the same reason: dragging one of five selected rows
    ///         and having four of them stay behind is the behaviour nobody means.
    ///     </para>
    /// </remarks>
    void Dropped(TreeNode node) {
        if (node.Tag is not Entity moved) {
            return;
        }

        var parent = node.Parent is { Tag: Entity target } ? target : Entity.Null;
        var entities = scene.Selection.Contains(moved) ? scene.Selection.ToList() : [moved];

        scene.Reparent(entities, parent);
        hierarchyStale = true;
    }

    void RebuildHierarchy() {
        if (hierarchy is not { } tree) {
            return;
        }

        while (tree.Root.Children.Count > 0) {
            tree.Root.Remove(tree.Root.Children[^1]);
        }

        // ⚠ One snapshot for the whole rebuild rather than one per row. `IEditorRegistry.All` takes
        // a lock and hands back a fresh array on every call, so asking it inside `Branch` was a
        // locked allocation per entity, and the binder behind each icon was resolved per entity as
        // well. A scene of a few thousand entities paid for both on every rename, every component
        // added and every undo — which is what made having the panel open feel expensive.
        var icons = GlyphSources();

        foreach (var entity in Ordered(scene.Roots)) {
            Branch(tree.Root, entity);
        }

        // ⚠ Expanded by writing the flag and refreshing *once*, rather than by calling `Expand` per
        // root. `TreeView.Expand` refreshes on every call — the right shape for a person clicking a
        // chevron and the wrong one for a loop — so a scene with N roots flattened the whole tree
        // and realised its rows N+1 times. That is the O(n²) this method had.
        tree.Expand(tree.Root.Children);
        ShowSelectionInTree(tree);

        // ⚠ Whether a branch is kept is decided bottom-up: an entity survives the filter if it
        // matches, and a parent survives if any of its descendants did. A filter that dropped a
        // non-matching parent would take the matching child with it, which for a name typed into
        // an outliner is the one row the user was looking for.
        bool Branch(TreeNode parent, Entity entity) {
            var node = parent.Add(scene.NameOf(entity), entity);

            node.Art = GlyphFor(icons, entity);

            var kept = Matches(entity);

            foreach (var child in Ordered(Children(entity))) {
                kept |= Branch(node, child);
            }

            if (!kept) {
                parent.Remove(node);
            }

            return kept;
        }
    }

    /// <summary>Runs the real content planner, for whoever wants to know what a build would say.</summary>
    /// <remarks>
    ///     ⚠ <b>The planner, not a reimplementation of its rules.</b> A panel that worked out for
    ///     itself which assets land in which group would be a second set of rules, and the way that
    ///     drift shows up is a panel saying a project is fine and the build refusing it.
    ///     <para>
    ///         ⚠ <b>Against a workspace of its own.</b> <c>ProjectWorkspace</c> opens the four stores
    ///         that have to agree about which directory they are looking at, and it must not share
    ///         the editor's database — <c>Scan</c> clears and repopulates its dictionaries, which is
    ///         the race <c>ContentTasks</c> already documents.
    ///     </para>
    /// </remarks>
    BuildPlan AnalyseContent() {
        var workspace = new ProjectWorkspace(project.Paths);

        workspace.Database.Scan();
        return ContentPipeline.Analyse(workspace, _ => { });
    }

    /// <summary>The glyph an outliner row draws for what an entity is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What it carries rather than what it is called.</b> An outliner of forty identical
    ///         rows is one you read rather than scan, and a name is the one thing on the row that is
    ///         already text — an icon that repeated it would be decoration. A camera, a light and a
    ///         piece of geometry are the three things a scene is mostly made of, and telling them
    ///         apart at a glance is what the column is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked once per rebuild rather than on every bind.</b> The node keeps the answer,
    ///         and a rebuild is what a component being added or removed already triggers — so a row
    ///         scrolling past does not ask the world a question per registered icon per frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>From the registry rather than from a three-case switch, which is doc 36 § D6.</b>
    ///         The switch could only ever name components this assembly references, so an entity
    ///         carrying a plugin's component was a plain dot whatever it was. What decides now is which
    ///         registered icon's type the entity actually carries — asked through the binder, because
    ///         an archetype knows dense ids and this knows types, and <c>ISceneComponentBinder.Has</c>
    ///         is the only bridge between them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Highest <c>Order</c> first, so "most characteristic" is a declared thing.</b> An
    ///         entity with a camera and a light has to draw one of them, and the alternative to an
    ///         order is the registration sequence — which is whichever assembly happened to load first.
    ///     </para>
    /// </remarks>
    /// <summary>The registered icons that have a binder, most characteristic first.</summary>
    /// <remarks>
    ///     ⚠ <b>Resolved once per rebuild, which is what makes <see cref="GlyphFor" /> cheap.</b> The
    ///     registry copy, the binder lookup and the ordering are all facts about the *session* rather
    ///     than about an entity, and doing them per row turned an outliner refresh into a locked
    ///     allocation and a dictionary walk per entity. Sorted descending here so the first match
    ///     wins and the loop can stop.
    /// </remarks>
    List<(ISceneComponentBinder Binder, IconArt Art)> GlyphSources() {
        List<(ISceneComponentBinder, IconArt)> found = [];

        foreach (var icon in Extensions.All<TypeIcon>().OrderByDescending(entry => entry.Order)) {
            if (SceneComponentRegistry.TryGet(icon.Target, out var binder)) {
                found.Add((binder, icon.Art));
            }
        }

        return found;
    }

    IconArt GlyphFor(List<(ISceneComponentBinder Binder, IconArt Art)> icons, Entity entity) {
        foreach (var (binder, art) in icons) {
            if (binder.Has(world, entity)) {
                return art;
            }
        }

        return EntityArt;
    }

    /// <summary>The subsystems whose components and behaviours this editor draws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One line per subsystem, and a subsystem the editor does not reference is one whose
    ///         components it could not draw anyway.</b> Rendering brings the meshes and the lights,
    ///         Engine the cameras and the transforms, Audio the sources and listeners — and the audio
    ///         one is the reason this list is needed at all, because nothing in a running editor calls
    ///         into <c>Vixen.Audio</c> until somebody adds a source, so its module initializer would
    ///         otherwise never have run at the moment the menu asked what exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Still a list in the application, and doc 36 § D5 said <c>Prime()</c> would "die"
    ///         when the registry was populated by producers.</b> What actually died is its being a
    ///         hardcoded <i>mechanism</i>: it is now a contribution, so a module declares its own and
    ///         a plugin's runtime assembly is declarable by whoever ships it. Eliminating the list
    ///         entirely is not available — a module initializer needs a touch, and the only thing that
    ///         finds an assembly nobody named is a scan, which ADR-002 and
    ///         <c>SceneComponentRegistry</c> both refuse for reasons that have not changed.
    ///     </para>
    /// </remarks>
    static readonly AuthoringAssembly[] BuiltInSubsystems = [
        new(typeof(Camera)),
        new(typeof(Light)),
        new(typeof(AudioSource)),

        // ⚠ The ground, the water and what floats on it — three subsystems this editor references and
        // never calls into before a scene is read, which is exactly the failure the audio line
        // documents. A `Main.vxscene` naming a `!TerrainComponent` or a `!WaterZoneComponent` used to
        // take the whole editor down on the way up, because the alias was unknown at the moment the
        // file was bound.
        new(typeof(Vixen.Rendering.Terrain.TerrainComponent)),
        new(typeof(Vixen.Rendering.Water.WaterZoneComponent)),

        // ⚠ And buoyancy, which is the one on this list the *editor* is the only reason to link.
        // docs/plan/35 § D1 keeps `Vixen.Water.Physics` out of every host that does not float
        // anything — a game opts in — but a scene has to be authorable before it can be opted into,
        // and an Add Component menu with no `BuoyancyBody` in it is a component nobody can place.
        new(typeof(Vixen.Water.Physics.BuoyancyBody))
    ];

    /// <summary>What a row with nothing else to say draws.</summary>
    /// <remarks>
    ///     Not a registration, because nothing keys it: it is the absence of every other answer rather
    ///     than the picture for a type.
    /// </remarks>
    static readonly IconArt EntityArt = IconArt.Of(EditorIcons.Entity);

    /// <summary>An entity's children, as a list a sort can be applied to.</summary>
    /// <remarks>
    ///     <c>Hierarchy.ChildrenOf</c> hands back a struct sequence rather than a list, which is
    ///     right for a walk that does not want an allocation and is the one thing a sort cannot be
    ///     done on. One list per branch, in a rebuild that only happens when the scene's shape
    ///     changed.
    /// </remarks>
    List<Entity> Children(Entity entity) {
        List<Entity> found = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            found.Add(child);
        }

        return found;
    }

    /// <summary>Puts a set of siblings in whatever order the outliner is showing.</summary>
    /// <remarks>
    ///     ⚠ <b>Per level, not over the flattened tree.</b> Sorting an outliner is sorting each
    ///     parent's children — a global sort would put a child above its own parent, which is not a
    ///     tree.
    /// </remarks>
    IEnumerable<Entity> Ordered(IReadOnlyList<Entity> siblings) =>
        hierarchyOrder switch {
            "Name (A–Z)" => siblings.OrderBy(scene.NameOf, StringComparer.CurrentCultureIgnoreCase),
            "Name (Z–A)" => siblings.OrderByDescending(scene.NameOf, StringComparer.CurrentCultureIgnoreCase),
            _ => siblings
        };

    /// <summary>Puts the eye and the padlock on a row, and brings them up to date.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Made once per row and updated on every bind.</b> A virtualised tree rebinds its
    ///         rows as it scrolls, so a handler that added an element per call would add one per
    ///         scrolled row for the life of the panel. The two buttons are found by class on the row
    ///         they already belong to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A row whose parent is hidden shows the eye off but dimmed.</b> It is off because
    ///         the entity is not being drawn, and dimmed because clicking it would do nothing — the
    ///         mark it would clear is on an ancestor. Showing it on would be a lie about what is on
    ///         screen; showing it plainly off would make the click look broken.
    ///     </para>
    /// </remarks>
    void MarkRow(TreeRow row, TreeNode node) {
        var eye = Mark(row, "outliner-hidden", EditorIcons.Eye, EditorIcons.EyeOff, "Hide");
        var padlock = Mark(row, "outliner-locked", ControlIcons.Unlock, ControlIcons.Lock, "Lock");

        if (node.Tag is not Entity entity) {
            eye.AddClass("hidden");
            padlock.AddClass("hidden");

            return;
        }

        eye.RemoveClass("hidden");
        padlock.RemoveClass("hidden");

        Restate(eye, scene.IsHiddenDirectly(entity), scene.IsHidden(entity));
        Restate(padlock, scene.IsLockedDirectly(entity), scene.IsLocked(entity));

        void Restate(ToggleButton button, bool directly, bool inherited) {
            button.IsChecked = directly;

            if (inherited && !directly) {
                button.AddClass("inherited");
            } else {
                button.RemoveClass("inherited");
            }

            // ⚠ Written here and not only from `CheckedChanged`. `IsChecked` above raises nothing
            // when the value it is given is the one it already had, and a pooled row rebound to a
            // different entity is exactly that case — the eye would keep the last entity's shape.
            Wear(button, directly || inherited);
        }
    }

    /// <summary>The toggle for one column, made on the row's first bind and reused after that.</summary>
    /// <remarks>
    ///     ⚠ <b>Two glyphs and no word, which is <c>InspectorView</c>'s padlock argument applied to
    ///     the column beside it.</b> These used to be a cross and a tick with "Hide" and "Lock"
    ///     written next to them — a label four times the button's width on every row of the outliner,
    ///     saying what pressing it <i>does</i> rather than what state it is in, which is the one thing
    ///     a toggle must not do. The shape carries the state now and the label stays for the tooltip
    ///     and the screen reader.
    /// </remarks>
    ToggleButton Mark(TreeRow row, string className, PathBuilder clear, PathBuilder marked, string label) {
        foreach (var child in row.Children) {
            if (child is ToggleButton existing && existing.HasClass(className)) {
                return existing;
            }
        }

        var button = row.Add<ToggleButton>();

        button.AddClass(className);
        button.LeadingIcon.Geometry = clear;
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.Label = label;
        button.TabIndex = -1;

        marks[button] = (clear, marked);

        // ⚠ Reads the row's *current* node rather than the one it was made for. The row outlives the
        // node by design — that is what pooling is — so a closure over the node would toggle
        // whatever entity happened to be in this slot when the panel first scrolled.
        button.CheckedChanged += (control, on) => {
            if (row.Node?.Tag is not Entity entity) {
                return;
            }

            if (className == "outliner-hidden") {
                scene.SetHidden(entity, on);
            } else {
                scene.SetLocked(entity, on);
            }

            // The descendants' rows show an inherited mark, so they have to be restated too — and
            // the selection may now hold something that cannot be picked.
            RefreshMarks();
            _ = control;
        };

        return button;
    }

    /// <summary>Which pair of glyphs a column's toggle draws, off and on.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the button rather than on it, because a <c>ToggleButton</c> has one icon slot
    ///     and no tag.</b> Bounded by the row pool — about thirty rows, two toggles each — for the
    ///     same reason the console's row map is.
    /// </remarks>
    readonly Dictionary<ToggleButton, (PathBuilder Clear, PathBuilder Marked)> marks = [];

    /// <summary>Puts the glyph for a state on a column's toggle.</summary>
    void Wear(ToggleButton button, bool marked) {
        if (marks.TryGetValue(button, out var pair)) {
            button.LeadingIcon.Geometry = marked ? pair.Marked : pair.Clear;
        }
    }

    /// <summary>Brings every realised row's two columns up to date.</summary>
    void RefreshMarks() {
        if (hierarchy is not { } tree) {
            return;
        }

        foreach (var row in tree.Rows) {
            if (row.Node is { } node) {
                MarkRow(row, node);
            }
        }
    }

    bool Matches(Entity entity) =>
        hierarchyFilter is null
        || scene.NameOf(entity).Contains(hierarchyFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Brings the outliner's highlight into line with the document, when they differ.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20's "selection <i>into</i> the tree", which is the half that was missing.</b>
    ///     Clicking a row selected an entity; selecting an entity anywhere else — the viewport, a
    ///     command, an undo — left the outliner showing whatever had been clicked last, because the
    ///     highlight is the tree's own state and nothing wrote it.
    /// </remarks>
    void SyncTreeSelection() {
        if (hierarchy is not { } tree || hierarchyStale) {
            return;
        }

        var shown = 0;
        var agrees = true;

        foreach (var node in tree.Selection) {
            shown++;

            if (node.Tag is not Entity entity || !scene.Selection.Contains(entity)) {
                agrees = false;
                break;
            }
        }

        // Same count and every row selected is selected in the document: nothing to do, which is
        // every frame in which nobody clicked anything.
        if (agrees && shown == scene.Selection.Count) {
            return;
        }

        ShowSelectionInTree(tree);
    }

    /// <inheritdoc cref="SyncTreeSelection" />
    void ShowSelectionInTree(TreeView tree) {
        if (scene.Selection.Count == 0) {
            // ⚠ Guarded as well. Clearing the tree's rows raises `SelectionChanged` too, and letting
            // that through would write an empty selection into whichever document the inspector had
            // just been handed.
            hierarchyEchoing = true;

            try {
                tree.Select(null);
            } finally {
                hierarchyEchoing = false;
            }

            return;
        }

        // ⚠ Guarded, because `Select` raises `SelectionChanged` and the handler writes the tree's
        // selection back into the document. Without this, restoring a selection of three entities
        // would set the document's to one — the first row selected — and the other two would vanish.
        hierarchyEchoing = true;

        try {
            var first = true;

            foreach (var node in Descendants(tree.Root)) {
                if (node.Tag is Entity entity && scene.Selection.Contains(entity)) {
                    tree.Select(node, first ? ModifierKeys.None : ModifierKeys.Control);
                    first = false;
                }
            }
        } finally {
            hierarchyEchoing = false;
        }
    }

    BoundingBox SelectionBounds() {
        if (scene.Selection.Count == 0) {
            return SceneBounds();
        }

        return Around(scene.Selection);
    }

    BoundingBox SceneBounds() {
        var entities = scene.Roots;

        return entities.Count == 0
            ? new BoundingBox(new Vector3(-1f), new Vector3(1f))
            : Around(scene.Entities);
    }

    /// <summary>A box around some entities.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An entity with a shape is measured and one without is padded.</b> Every built-in
    ///         shape fits the unit cube — see <c>MeshPrimitives</c> — so its extent is half its world
    ///         scale, which is what makes focusing a plane scaled twelvefold frame the plane rather
    ///         than a metre of its middle. An empty, a light or a camera still has no size, and half
    ///         a unit either side of its origin is what keeps frame-all from collapsing onto a point.
    ///     </para>
    ///     <para>
    ///         The scale is taken as the length of each of the matrix's three basis vectors, so a
    ///         rotated entity is bounded by a box that is too big rather than one that clips it —
    ///         which is the right way round for something a camera is about to be placed from.
    ///         <c>EditorCamera.Focus</c> floors the radius anyway.
    ///     </para>
    /// </remarks>
    BoundingBox Around(IEnumerable<Entity> entities) {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var any = false;

        foreach (var entity in entities) {
            if (!world.IsAlive(entity) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            var matrix = world.Read<WorldTransform>(entity).Value;
            var position = matrix.Translation;

            var extent = PrimitiveShapes.TryGet(world, entity, out _)
                ? new Vector3(
                    matrix.Right.Length(),
                    matrix.Up.Length(),
                    matrix.Forward.Length()
                ) * 0.5f
                : new Vector3(0.5f);

            low = Vector3.Min(low, position - extent);
            high = Vector3.Max(high, position + extent);
            any = true;
        }

        return any ? new BoundingBox(low, high) : new BoundingBox(new Vector3(-1f), new Vector3(1f));
    }

    /// <summary>Writes the scene, and says so.</summary>
    /// <remarks>
    ///     ⚠ <b>A failed save is a notification and not an exception.</b> A full disk or a read-only
    ///     working tree is an ordinary thing to meet, and an editor that took the process down with
    ///     the unsaved work still in it would be the worst possible response to it.
    /// </remarks>
    void SaveScene() {
        try {
            // ⚠ The sidecars go with it, and they are not named here any more. A scene names a
            // heightfield and a foliage file beside itself; whoever owns those subscribes to
            // `EditorDocument.Saved`, which throws through so a sidecar that could not be written is
            // a failed save rather than a silent half of one.
            scene.Save();

            Shell.Notifications.Success(Path.GetFileName(scenePath));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the scene", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Rescans the project and says what changed.</summary>
    /// <remarks>
    ///     ⚠ <b>The issues are what is worth reporting, not the count.</b> A scan that created eleven
    ///     sidecars, quarantined an orphan and re-GUIDed a duplicate has just modified the working
    ///     tree, and telling somebody only that there are 340 assets would leave them to find that out
    ///     from <c>git status</c>.
    /// </remarks>
    /// <summary>Creates an empty entity under the selection, and selects it.</summary>
    /// <remarks>
    ///     Under the first selected entity rather than at the root, which is what every editor does
    ///     and what somebody who has just clicked a parent means. Selecting the new one is what makes
    ///     the next thing they do — rename it, drag it — land on the thing they just made.
    /// </remarks>
    void CreateEntity() {
        var created = scene.Create("Entity", LocalTransform.Identity, Under());
        scene.Selection.Set([created]);
    }

    /// <summary>Creates one of the built-in shapes under the selection, and selects it.</summary>
    /// <remarks>
    ///     ⚠ <b>At the parent's origin rather than in front of the camera.</b> Placing new geometry
    ///     where the viewport is looking is what Unity does with a preference turned on, and it needs
    ///     the camera's pivot taken back through the parent's inverse — which is a different answer
    ///     for a spawn from the hierarchy, where there may be no viewport open at all. The identity is
    ///     the one placement that means the same thing from both, and it is what Create Empty already
    ///     does.
    /// </remarks>
    void CreateShape(PrimitiveKind kind) {
        var created = scene.CreateShape(kind, LocalTransform.Identity, Under());
        scene.Selection.Set([created]);
    }

    /// <summary>Creates a light under the selection, and selects it.</summary>
    /// <remarks>
    ///     ⚠ <b>Aimed downwards rather than left at the identity, and only this one is.</b> A shape at
    ///     the identity is a shape; a directional or a spot light at the identity points along +Z,
    ///     which for a sun means the horizon and for a spot means a cone lying flat in the floor —
    ///     both of which look like the command having done nothing. Every other kind ignores the
    ///     rotation, so the same placement is right for all five.
    /// </remarks>
    void CreateLight(LightKind kind) {
        var created = scene.CreateLight(kind, Aimed, Under());
        scene.Selection.Set([created]);
    }

    /// <summary>Creates a camera under the selection, and selects it.</summary>
    /// <remarks>
    ///     Level and facing the way a directional light points, for the same reason: a camera looking
    ///     along +Z from the origin is a camera looking at the inside of whatever is at the origin.
    /// </remarks>
    void CreateCamera() {
        var created = scene.CreateCamera(LocalTransform.Identity, Under());
        scene.Selection.Set([created]);
    }

    /// <summary>Pointing down and forward, the way a key light is hung.</summary>
    static LocalTransform Aimed =>
        LocalTransform.Identity with {
            Rotation = Quaternion.FromYawPitchRoll(
                MathUtil.DegreesToRadians(-30f),
                MathUtil.DegreesToRadians(50f),
                0f
            )
        };

    /// <summary>What a newly created entity hangs from.</summary>
    /// <remarks>
    ///     The first selected entity rather than the root, which is what every editor does and what
    ///     somebody who has just clicked a parent means. Selecting the new one afterwards is what
    ///     makes the next thing they do — rename it, drag it — land on the thing they just made.
    /// </remarks>
    Entity Under() => scene.Selection.Count > 0 ? scene.Selection[0] : Entity.Null;

    void RefreshAssets() {
        if (browser is not { } open) {
            return;
        }

        try {
            var report = open.Rescan();

            if (report.Issues.Count == 0) {
                Shell.Notifications.Success($"{report.Assets} assets");
                return;
            }

            Shell.Notifications.Show(
                $"{report.Assets} assets, {report.Issues.Count} repaired",
                NotificationSeverity.Warning,
                string.Join(Environment.NewLine, report.Issues.Take(5).Select(issue => issue.Message))
            );
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not scan the project", NotificationSeverity.Error, exception.Message);
        }
    }

    void SaveLayout() {
        // Named after the preset it came from until there is a dialog to ask. A "Save Layout…" that
        // silently overwrote the arrangement the user is looking at would be worse than one that
        // has an obvious placeholder name.
        var name = Shell.Workspace.DefaultPreset + " (custom)";

        store.SaveLayout(name, Shell.Workspace.Save());
        Shell.Notifications.Success(name);
    }

    void About() =>
        Shell.Notifications.Show(
            "Vixen Editor",
            NotificationSeverity.Info,
            typeof(EditorApplication).Assembly.GetName().Version?.ToString() ?? "development build"
        );
}
