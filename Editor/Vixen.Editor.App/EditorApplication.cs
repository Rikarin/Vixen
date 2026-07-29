// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
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
///         does: nothing here ticks systems, runs a fixed step or updates behaviours. The world is a
///         <i>document</i> — the thing the hierarchy lists, the inspector edits and the gizmo drags —
///         and it starts being a running game only when play mode says so.
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

    /// <summary>What an asset field's button opens.</summary>
    AssetPicker assetPicker = null!;

    /// <summary>Pictures of assets for the browser's grid, decoded off the frame thread.</summary>
    readonly ThumbnailCache thumbnails;

    readonly ContentTasks content;
    readonly PluginHost plugins;

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

    InspectorView? inspector;

    /// <summary>The component foldouts under the inspector, while its panel is open.</summary>
    ComponentsView? components;

    /// <summary>Which components the editor can show, built once because the set is a process fact.</summary>
    /// <remarks>
    ///     A plugin registering a component would want this rebuilt; it is a list rather than a
    ///     snapshot for that reason, and the day a plugin can add one is the day this grows a
    ///     subscription.
    /// </remarks>
    readonly IReadOnlyList<IComponentBridge> bridges = ComponentsView.Default();
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
        EditorServices? services = null
    ) {
        store = new EditorUserStore(directory);
        dataDirectory = directory;
        this.services = services ?? EditorServices.None;

        // ⚠ Before the project, because whether this run is a first one — no history at all — is
        // what decides whether the startup Project Browser has anything to offer, and opening the
        // project is what adds this one to the list.
        Recent = new ProjectHistory(directory);
        IsScratch = projectRoot is null;

        Shell = new EditorShell(width, height);

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
        play = new PlayModeController(world);

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
        };

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
            },

            // And its two buttons are greyed while anything is running, which is a fact about the
            // task rather than about anything the panel did.
            BusyChanged = RefreshBuildPanel
        };

        // ⚠ Before the panels, because the inspector's asset fields are built by drawers that have
        // to be pointed at a project first. `AssetDrawer` has raised `PickRequested` since it was
        // written and nothing ever listened, so the button in an asset field did nothing at all.
        assetPicker = new AssetPicker(project, Shell.Dialogs);

        foreach (var drawer in DrawerRegistry.Default.Drawers.OfType<AssetDrawer>()) {
            drawer.Resolve = assetPicker.NameOf;
            drawer.PickRequested += assetPicker.Open;
        }

        Panels();

        // ⚠ Before `Layouts`, because the Profiling preset names the panels this registers and a
        // preset naming a panel the workspace cannot build is a preset that comes back short.
        DiagnosticsPanels();
        SettingsPanels();
        BuildPanels();

        // And E5's four, for the same reason: the Sequencing preset names the scene list.
        WorldPanels();

        Layouts();
        Commands();

        // ⚠ After the commands and before the keymap, because the undo depth it carries is applied
        // to stacks that exist by now, and because `SavePreferences` is reachable from a panel the
        // line above has just registered.
        LoadPreferences();
        ApplyProjectSettings();

        // ⚠ Plugins go here and not later, and the two reasons are the two lines below. A plugin's
        // commands have to exist before the keymap is read or the user's override for one lands on
        // a command with no default; a plugin's panels have to be registered before the saved
        // layout is applied or an arrangement that had one comes back without it.
        plugins = new PluginHost(Shell, PluginPoints());

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

        if (store.LoadLayout(EditorUserStore.CurrentLayout) is { } layout) {
            Shell.Workspace.Load(layout);
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
    public SceneViewport? Viewport => viewport;

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

    /// <summary>The pane every scene command acts on.</summary>
    SceneViewport? viewport => viewports?.Focused;

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
        DiagnosticsUpdate(delta);

        // ⚠ And E5's two moving surfaces, pulled rather than self-driving. A VFX preview and a
        // sequencer transport both advance with time, and a timer either of them started would
        // outlive the panel it was drawn in — the rule every pulled surface in this editor follows.
        AuthoringUpdate(delta);

        // ⚠ Polled, and it compares before it rebuilds. A command stack is signal-backed and nothing
        // in this loop flushes the reactive scheduler — the same trade the selections make — and the
        // panel is the one that would otherwise rewrite its whole list during a gizmo drag.
        historyView?.Tick();

        ResolveTransforms();
        FollowHistory();
        Retitle();

        if (hierarchyStale) {
            hierarchyStale = false;
            RebuildHierarchy();
        }

        FollowSelection();

        // ⚠ After the arbitration, and every frame rather than only after a rebuild. A selection
        // made anywhere but the tree — a viewport click, a command, an undo — changes nothing
        // structural, so a sync that only ran when the rows were rebuilt would leave the outliner
        // highlighting whatever was clicked in it last. Comparing a handful of handles is the same
        // trade this class already makes for the selections themselves.
        SyncTreeSelection();
        browser?.SyncSelection();

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
        }

        // The inspector follows the gizmo. Reload rather than Inspect, because the rows and their
        // handlers already exist and rebuilding would take the focus out of whatever is being typed.
        if (layout.Panes.Any(static pane => pane.Gizmo.IsDragging)) {
            inspector?.Reload();
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
    ///         joins the two. The editor runs no system graph, so a position typed into the
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

        // Before the shell, because the images it releases are registered with the renderer the
        // shell's document draws through.
        thumbnails.Dispose();

        viewports?.Dispose();

        // Before the shell, because detaching writes a line into the connection log the panel is
        // showing — and after the plugins, because a plugin could in principle be holding a device
        // provider that has just been unloaded.
        DiagnosticsDispose();

        // Before the world, because it holds a snapshot of it: a controller disposed after the world
        // would be releasing chunks into a world that had already released its own.
        play.Dispose();

        Shell.Dispose();
        world.Dispose();

        // After the shell, because disposing it raises no notifications but unloading a plugin can —
        // and a mirror taken down first would lose the last thing the editor had to say.
        log.Dispose();
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

            MeshShapes.Attach(world, entity, kind);
        }
    }

    /// <summary>Makes a pointer press anywhere in a panel say which context the user is in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On press and on the tunnel leg, so it lands before anything acts on it.</b> A
    ///         click in the outliner is what makes Delete mean an entity; recording it from a handler
    ///         that runs after the tree's own would mean the first Delete of a visit to a panel was
    ///         still aimed at the panel before it.
    ///     </para>
    ///     <para>
    ///         <b>The press rather than the focus, because most of these panels do not take one.</b>
    ///         A tree row is focusable and a viewport is not, and "which panel did the user last act
    ///         in" is the question a scoped command is actually asking.
    ///     </para>
    /// </remarks>
    void Contextual(DockPanel panel, string context) =>
        panel.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    Shell.Context = context;
                }
            },
            RoutingStrategy.Capture,
            handledEventsToo: true
        );

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

            // ⚠ Restored, because this runs again every time the panel is reopened and a fresh
            // SceneViewport starts at the origin looking down −Z. Absent for a pane of an
            // arrangement nobody has looked at yet, which is what leaves the quad presets alone.
            if (index < cameras.Length && cameras[index] is { } saved) {
                pane.Camera.Restore(saved);
            }

            var focused = pane;

            pane.Control.AddHandler<FocusEvent>(
                (_, args) => {
                    if (args.Gained) {
                        layout.Focus(focused);
                    }
                },
                handledEventsToo: true
            );

            chrome.Attach(pane, this);
        }
    }

    /// <summary>The toolbar, the stats readout and the rubber-band drawn over each pane.</summary>
    ViewportChrome? chrome;

    void Panels() {
        Shell.RegisterPanel(
            "hierarchy",
            new StringId("editor.panel.hierarchy", "Hierarchy"),
            panel => {
                Contextual(panel, SceneContext);

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
            "project",
            new StringId("editor.panel.project", "Project"),
            panel => {
                Contextual(panel, AssetContext);

                browser = new ProjectBrowser(project, panel);

                browser.Activated += Open;
                browser.Renamed += RenameAsset;
                browser.Moved += MoveAssets;
                browser.DroppedOutside += DropIntoScene;
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

                Contextualise(browser.Tree, assetMenu ??= AssetMenu());
            }
        );

        Shell.RegisterPanel(
            new PanelDescriptor(
                "scene",
                new StringId("editor.panel.scene", "Scene"),
                panel => {
                    Contextual(panel, SceneContext);

                    // ⚠ A layout rather than a control, and every pane in it is a whole
                    // `SceneViewport`. Doc 11 asks for "multiple simultaneous viewports with
                    // independent cameras and render modes", and the second half of that is what
                    // forces it: a view mode is stage state, so a pane that wanted its own would
                    // silently change its neighbour's.
                    viewports = new ViewportLayout(panel, scene.Selection) {
                        Document = scene,
                        TargetsFactory = () => EntityGizmoTarget.For(world, scene.Selection),
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
            "inspector",
            new StringId("editor.panel.inspector", "Inspector"),
            panel => {
                inspector = panel.Add<InspectorView>();
                inspector.EditedDocument = scene;

                // ⚠ Under the inspector's rows rather than inside its model. `InspectorView` draws
                // the members of one described type; which *types* are on an entity is a different
                // question, and one it deliberately cannot ask — see `ComponentsView`. What it does
                // share is the scroll region: an entity with six components is longer than any
                // panel, and two independent scroll regions would leave half the answer off screen
                // whichever one you moved.
                components = inspector.Scroll.Content.Add<ComponentsView>();
                components.Attach(scene, bridges);

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
        );

        Shell.RegisterPanel(
            "console",
            new StringId("editor.panel.console", "Console"),
            panel => {
                Contextual(panel, ConsoleContext);

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
    ///         ⚠ <b>Two of doc 11's extension points are not published, and the reason is
    ///         upstream.</b> Importers are built per run — <c>ContentPipeline</c> calls
    ///         <c>ProjectWorkspace.Importers()</c> inside the background task, deliberately, so the
    ///         editor and the CLI cannot disagree about the set — so there is no registry here for a
    ///         plugin to add to, and giving it one would be the editor building a set the compiler
    ///         workers do not have. Build steps are the same shape. Both want a registry that
    ///         outlives a run, which is a change to <c>Vixen.Editor.Assets</c> rather than to this.
    ///     </para>
    /// </remarks>
    PluginServices PluginPoints() =>
        new PluginServices()
            .Add(project)
            .Add(scene)

            // The static the inspector reads by default, so a plugin's drawer is found by the panel
            // that is already open rather than by one built afterwards.
            .Add(DrawerRegistry.Default);

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

    /// <summary>Unloads every active plugin and loads it again from disk.</summary>
    /// <remarks>
    ///     ⚠ <b>A plugin that does not go away is reported.</b> Its replacement loads either way —
    ///     refusing would make one badly-behaved plugin block the whole reload — but the old copy is
    ///     still in memory with its statics in it, and that is the failure the runtime says nothing
    ///     about. Restarting the editor is the only cure and the notification says so.
    /// </remarks>
    void ReloadPlugins() {
        var reloaded = 0;
        var leaked = new List<string>();

        foreach (var id in plugins.Plugins.Where(plugin => plugin.State == PluginState.Active).Select(plugin => plugin.Id).ToList()) {
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

        Shell.Notifications.Show($"{reloaded} plugin(s) reloaded", NotificationSeverity.Success);
    }

    void Layouts() {
        // The five doc 11 names. They differ in which panels they show and how the middle is split,
        // which is the whole of what a layout preset is — the shapes come from `LayoutPresets` and
        // the panel lists are this application's.
        Shell.RegisterLayout(
            "Default",
            new StringId("editor.layout.default", "Default"),
            () => LayoutPresets.Standard(["hierarchy", "project"], ["scene"], ["inspector"], ["console"])
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
        DiagnosticsCommands();

        // ⚠ And E5's, for the same reason and on the same terms: these are the ids that were
        // declared-and-disabled until this milestone built the panels behind them.
        WorldCommands();

        Shell.Keys.SetDefault("file.exit", new KeyChord(InputKey.Q, ModifierKeys.Control));

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
            new EditorCommand("edit.undo", new StringId("editor.command.undo", "Undo"), () => scene.Stack.Undo()) {
                Category = EditorStrings.CategoryEdit,
                Enablement = () => scene.Stack.CanUndo.Value
            }
        );

        Shell.Commands.Add(
            new EditorCommand("edit.redo", new StringId("editor.command.redo", "Redo"), () => scene.Stack.Redo()) {
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
                        if (viewport is { } pane) {
                            action(pane);
                        }
                    }
                ) {
                    Category = new StringId("editor.category.scene", "Scene"),
                    Enablement = () => viewport is not null,

                    // ⚠ Null when the command is not a toggle, rather than a predicate that answers
                    // false. `MenuPresenter` grows the tick column only for commands that have one,
                    // so a lambda here would indent every line of the Scene menu by an empty tick.
                    Checked = on is null ? null : () => viewport is { } pane && on(pane),

                    // ⚠ And null when the name does not move, which is all but two of these. See
                    // `EditorCommand.Caption`: a delegate asked per button per frame is not free,
                    // and the id has to stay the *same* string whatever the label says — it is what
                    // the keymap, the palette and the menu model all name.
                    Caption = caption is null
                        ? null
                        : () => new StringId(
                            "editor.command." + id,
                            viewport is { } pane ? caption(pane) : label
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
    ///     <see cref="MeshShapes.All" />, so a shape added there appears everywhere without anything
    ///     here being edited.
    /// </remarks>
    void ShapeCommands() {
        foreach (var kind in MeshShapes.All) {
            var shape = kind;
            var name = MeshShapes.NameOf(shape);

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
        "scene.create-" + MeshShapes.NameOf(kind).ToLowerInvariant();

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

        foreach (var kind in MeshShapes.All) {
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
            .Add([.. CreatableIds]);

        group.Add("assets.import-files");
        group.AddSeparator();
        group.Add("assets.rename", "assets.delete", "assets.move-to");
        group.AddSeparator();
        group.Add("assets.reimport-all", "assets.show-in-explorer");

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
            inspector?.Inspect([.. project.Selection.Select(asset => new ProjectAsset(project, asset))]);

            // An asset has no components, and leaving the last entity's foldouts under it would be a
            // panel showing two different things at once.
            components?.Show(Entity.Null);

            Shell.Status = project.Selection.Count switch {
                0 => ProductName,
                1 => project.Assets.TryGetByGuid(project.Selection[0], out var entry) ? entry.Name : ProductName,
                _ => $"{project.Selection.Count} selected"
            };

            return;
        }

        var document = inspected ?? scene;

        if (inspector is { } view) {
            // ⚠ The document whose entities these are, not the editor's own scene. An inspector edit
            // is recorded on the stack of the document it changed, and a scene opened as an asset
            // has one of its own — so an edit made here with the wrong document set would be undone
            // by a Ctrl+Z aimed at something else entirely.
            view.EditedDocument = document;
            view.Inspect([.. document.Selection.Select(entity => new SceneEntity(document, entity))]);
        }

        // ⚠ Only this editor's own scene. The foldouts write through `scene.Stack`, and a document
        // opened as an asset has a stack of its own — showing its entity's components here would put
        // the edit on the wrong one, which is the hazard the line above guards for the rows.
        components?.Show(
            ReferenceEquals(document, scene) && document.Selection.Count > 0 ? document.Selection[0] : Entity.Null
        );

        Shell.Status = document.Selection.Count switch {
            0 => ProductName,
            1 => document.NameOf(document.Selection[0]),
            _ => $"{document.Selection.Count} selected"
        };
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
    ///         geometry yet — see <c>MeshShape</c>'s remarks for why it lives in the editor at all —
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

        using (scene.Stack.BeginTransaction(assets.Count == 1 ? "Add Asset" : $"Add {assets.Count} Assets")) {
            foreach (var asset in assets) {
                if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
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

        foreach (var entity in Ordered(scene.Roots)) {
            Branch(tree.Root, entity);
        }

        tree.Refresh();

        foreach (var node in tree.Root.Children) {
            tree.Expand(node);
        }

        ShowSelectionInTree(tree);

        // ⚠ Whether a branch is kept is decided bottom-up: an entity survives the filter if it
        // matches, and a parent survives if any of its descendants did. A filter that dropped a
        // non-matching parent would take the matching child with it, which for a name typed into
        // an outliner is the one row the user was looking for.
        bool Branch(TreeNode parent, Entity entity) {
            var node = parent.Add(scene.NameOf(entity), entity);

            node.Icon = GlyphFor(entity);

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
    ///         scrolling past does not ask the world three questions per frame.
    ///     </para>
    /// </remarks>
    PathBuilder GlyphFor(Entity entity) {
        if (world.Has<Light>(entity)) {
            return EditorIcons.Light;
        }

        if (world.Has<Camera>(entity)) {
            return EditorIcons.Camera;
        }

        return world.Has<MeshShape>(entity) ? EditorIcons.Cube : EditorIcons.Entity;
    }

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
        var eye = Mark(row, "outliner-hidden", ControlIcons.Close, "Hide");
        var padlock = Mark(row, "outliner-locked", ControlIcons.Check, "Lock");

        if (node.Tag is not Entity entity) {
            eye.AddClass("hidden");
            padlock.AddClass("hidden");

            return;
        }

        eye.RemoveClass("hidden");
        padlock.RemoveClass("hidden");

        Restate(eye, scene.IsHiddenDirectly(entity), scene.IsHidden(entity));
        Restate(padlock, scene.IsLockedDirectly(entity), scene.IsLocked(entity));

        static void Restate(ToggleButton button, bool directly, bool inherited) {
            button.IsChecked = directly;

            if (inherited && !directly) {
                button.AddClass("inherited");
            } else {
                button.RemoveClass("inherited");
            }
        }
    }

    /// <summary>The toggle for one column, made on the row's first bind and reused after that.</summary>
    ToggleButton Mark(TreeRow row, string className, PathBuilder icon, string label) {
        foreach (var child in row.Children) {
            if (child is ToggleButton existing && existing.HasClass(className)) {
                return existing;
            }
        }

        var button = row.Add<ToggleButton>();

        button.AddClass(className);
        button.LeadingIcon.Geometry = icon;
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.Label = label;
        button.TabIndex = -1;

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

            var extent = MeshShapes.TryGet(world, entity, out _)
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
