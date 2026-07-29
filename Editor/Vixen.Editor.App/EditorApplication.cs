// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Input;
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
///         ⚠ <b>The selection is polled once a frame rather than subscribed to.</b>
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
sealed class EditorApplication : IDisposable {
    readonly EditorUserStore store;
    readonly World world = new("Editor");
    readonly EditorProject project;
    readonly SceneDocument scene;
    readonly ContentTasks content;
    readonly string scenePath;
    readonly List<Entity> shown = [];

    /// <summary>The one system the editor runs, and <see cref="ResolveTransforms" /> says why.</summary>
    readonly TransformSystem transforms = new();

    SceneViewport? viewport;
    InspectorView? inspector;
    TreeView? hierarchy;
    ProjectBrowser? browser;
    ViewBookmark camera;
    bool hierarchyStale = true;

    /// <summary>Builds the editor's interface into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="directory">Where the user's layouts, keymap and preferences live.</param>
    /// <param name="projectRoot">The project to open, or <see langword="null" /> for a scratch one.</param>
    public EditorApplication(float width, float height, string directory, string? projectRoot = null) {
        store = new EditorUserStore(directory);
        Shell = new EditorShell(width, height);

        // ⚠ The fourth user-agent sheet, and it is the application that loads it. `EditorShell` has
        // the three that draw the chrome and cannot have this one: it is deliberately a shell that
        // knows nothing about inspectors, and the panel it hosts is this assembly's choice. Loaded
        // after those three, so a rule of the same specificity here wins — which is what lets it
        // narrow `expander-content`'s indent and a field's background without out-specifying them.
        InspectorTheme.Install(Shell.Document);

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

        camera = new EditorCamera().Bookmark("initial");

        content = new(project, Shell) {
            // The panel's own rescan, so the browser shows what an import repaired rather than what
            // was there before it ran. Assigned rather than called by the tasks directly, because
            // the browser exists only while its panel is open.
            Rescan = () => browser?.Rescan()
        };

        Panels();
        Layouts();
        Commands();

        // ⚠ In this order and no other. The keymap has to be loaded after the commands that own its
        // defaults, or every override in the file lands on a command with no default and the file
        // rewrites itself with the whole map in it. The layout has to be applied after the panels
        // are registered, or a saved arrangement names panels the workspace cannot build.
        if (store.Read(EditorUserStore.KeyMapFile) is { } keymap) {
            Shell.Keys.Load(keymap);
        }

        Shell.Theme.LoadTokens(store.Read("theme.yaml"));

        if (store.LoadLayout(EditorUserStore.CurrentLayout) is { } layout) {
            Shell.Workspace.Load(layout);
        } else {
            Shell.Workspace.Reset();
        }

        Shell.Status = project.Name;
    }

    /// <summary>The interface.</summary>
    public EditorShell Shell { get; }

    /// <summary>The scene being edited.</summary>
    public SceneDocument Scene => scene;

    /// <summary>The pane the scene is drawn in, or <see langword="null" /> while it is closed.</summary>
    /// <remarks>
    ///     Null is the ordinary case rather than an error: a layout without the scene panel in it is
    ///     one the user chose, and the host renders nothing for it.
    /// </remarks>
    public SceneViewport? Viewport => viewport;

    /// <summary>Whether the editor has been asked to close.</summary>
    public bool IsClosing { get; private set; }

    /// <summary>How many render pixels one layout pixel is.</summary>
    /// <remarks>
    ///     Pushed down to the viewport rather than read from it, because the display's scale belongs
    ///     to the window and a panel has no way to ask.
    /// </remarks>
    public float RenderScale {
        get;

        set {
            field = value;

            if (viewport is not null) {
                viewport.Control.RenderScale = value;
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
        // What a finished import or build had to say, on the thread that owns the panels it is about
        // to rebuild. See `ContentTasks` for why nothing crosses back except a queued value.
        content.Pump();

        ResolveTransforms();

        if (hierarchyStale) {
            hierarchyStale = false;
            RebuildHierarchy();
        }

        if (Changed()) {
            ShowSelection();
        }

        if (viewport is not { } pane) {
            return;
        }

        pane.Update(delta);

        // ⚠ Kept every frame, not on the way out. A panel's factory runs again when it is reopened
        // and the SceneViewport goes with the old one, so there is no teardown hook to read the
        // camera in — and a bookmark taken once at startup would restore the origin every time.
        camera = pane.Camera.Bookmark("current");

        // The inspector follows the gizmo. Reload rather than Inspect, because the rows and their
        // handlers already exist and rebuilding would take the focus out of whatever is being typed.
        if (pane.Gizmo.IsDragging) {
            inspector?.Reload();
        }
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
    public void Dispose() {
        viewport?.Dispose();
        Shell.Dispose();
        world.Dispose();
    }

    /// <summary>A scene with something in it, for a project that has none yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Only when there is no file to open.</b> A first run in an empty project opens
    ///     something rather than an empty tree and a viewport with nothing to look at; the moment
    ///     that scene is saved, this never runs again for that project.
    /// </remarks>
    void Seed() {
        var root = scene.Add("Scene Root", LocalTransform.Identity);

        scene.Add("Directional Light", LocalTransform.At(new Vector3(0f, 3f, 0f)), root);
        scene.Add("Main Camera", LocalTransform.At(new Vector3(0f, 1.5f, 6f)), root);

        var ground = scene.Add("Ground", LocalTransform.Identity, root);
        scene.Add("Crate", LocalTransform.At(new Vector3(1.5f, 0.5f, 0f)), ground);
        scene.Add("Barrel", LocalTransform.At(new Vector3(-2f, 0.5f, 1f)), ground);

        // The stack starts empty: seeding is not an edit somebody made, and an editor that opened
        // with five undo steps already on it is one where Ctrl+Z does something inexplicable.
        scene.Stack.Clear();
        scene.Stack.MarkClean();
    }

    void Panels() {
        Shell.RegisterPanel(
            "hierarchy",
            new StringId("editor.panel.hierarchy", "Hierarchy"),
            panel => {
                hierarchy = panel.Add<TreeView>();
                hierarchy.MultiSelect = true;

                hierarchy.SelectionChanged += tree => {
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

                hierarchyStale = true;
            }
        );

        Shell.RegisterPanel(
            "project",
            new StringId("editor.panel.project", "Project"),
            panel => browser = new ProjectBrowser(project, panel)
        );

        Shell.RegisterPanel(
            "scene",
            new StringId("editor.panel.scene", "Scene"),
            panel => {
                var control = panel.Add<ViewportControl>();
                control.RenderScale = RenderScale;

                viewport = new SceneViewport(control, scene.Selection) {
                    Document = scene,
                    TargetsFactory = () => EntityGizmoTarget.For(world, scene.Selection)
                };

                // ⚠ Restored, because this factory runs again every time the panel is reopened and a
                // fresh SceneViewport starts at the origin looking down −Z.
                viewport.Camera.Restore(camera);
            }
        );

        Shell.RegisterPanel(
            "inspector",
            new StringId("editor.panel.inspector", "Inspector"),
            panel => {
                inspector = panel.Add<InspectorView>();
                inspector.EditedDocument = scene;

                // The rows were built against the previous instance of this panel, so what is
                // selected has to be pushed into the new one rather than waited for.
                shown.Clear();
                ShowSelection();
            }
        );

        Shell.RegisterPanel(
            "console",
            new StringId("editor.panel.console", "Console"),
            panel => panel.Add<TextBlock>().Text = "Nothing logged yet."
        );
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

        Shell.Workspace.DefaultPreset = "Default";
    }

    void Commands() {
        Shell.Commands.Add(
            new EditorCommand("file.exit", EditorStrings.CommandExit, () => IsClosing = true) {
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

        EditCommands();
        SceneCommands();

        Shell.Keys.SetDefault("file.exit", new KeyChord(InputKey.Q, ModifierKeys.Control));

        // The three gizmo modes and the two toggles that change what a drag does, which are the
        // things somebody reaches for between one edit and the next. They show their state — a
        // command with a `Checked` predicate draws its button pressed — so the strip also answers
        // "what will dragging do right now" without anything being opened.
        Shell.Toolbar.Show(
            "view.palette",
            null,
            "scene.translate",
            "scene.rotate",
            "scene.scale",
            null,
            "scene.toggle-space",
            "scene.toggle-snap",
            "scene.toggle-grid",
            null,
            "view.reset-layout",
            "view.toggle-theme"
        );

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

        Shell.Commands.Add(
            new EditorCommand(
                "scene.delete-entity",
                new StringId("editor.command.delete-entity", "Delete"),
                () => scene.Delete(scene.Selection.ToList())
            ) {
                Category = EditorStrings.CategoryEdit,
                Enablement = () => scene.Selection.Count > 0
            }
        );

        Shell.Keys.SetDefault("scene.delete-entity", new KeyChord(InputKey.Delete, ModifierKeys.None));

        // ⚠ Ticked, and that is what makes the three modes read as one choice rather than as three
        // buttons. A menu of Translate, Rotate and Scale with nothing saying which is current is one
        // where the only way to find out what a drag will do is to drag — and the tick costs a
        // predicate, which both the menu and the toolbar already ask for.
        Mode("scene.translate", "Translate", GizmoMode.Translate, InputKey.W);
        Mode("scene.rotate", "Rotate", GizmoMode.Rotate, InputKey.E);
        Mode("scene.scale", "Scale", GizmoMode.Scale, InputKey.R);

        Add(
            "scene.toggle-space",
            "Local Space",
            pane => pane.Gizmo.Space = pane.Gizmo.Space == GizmoSpace.World ? GizmoSpace.Local : GizmoSpace.World,
            on: pane => pane.Gizmo.Space != GizmoSpace.World
        );

        Add(
            "scene.toggle-pivot",
            "Pivot at Centre",
            pane => pane.Gizmo.Pivot = pane.Gizmo.Pivot == PivotMode.Pivot ? PivotMode.Center : PivotMode.Pivot,
            on: pane => pane.Gizmo.Pivot == PivotMode.Center
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

        Add("scene.toggle-grid", "Grid", pane => pane.Grid.Enabled = !pane.Grid.Enabled, on: pane => pane.Grid.Enabled);

        Add(
            "scene.toggle-projection",
            "Orthographic",
            pane => pane.Camera.IsOrthographic = !pane.Camera.IsOrthographic,
            on: pane => pane.Camera.IsOrthographic
        );

        Add("scene.focus", "Focus Selection", pane => pane.FocusSelection(SelectionBounds()), key: InputKey.F);
        Add("scene.frame-all", "Frame All", pane => pane.Camera.Focus(SceneBounds()), key: InputKey.A);

        View("scene.view-front", "Front View", ViewDirection.Front, InputKey.Keypad1);
        View("scene.view-right", "Right View", ViewDirection.Right, InputKey.Keypad3);
        View("scene.view-top", "Top View", ViewDirection.Top, InputKey.Keypad7);

        void Mode(string id, string label, GizmoMode mode, InputKey key) =>
            Add(id, label, pane => pane.Gizmo.Mode = mode, pane => pane.Gizmo.Mode == mode, key);

        void View(string id, string label, ViewDirection direction, InputKey key) =>
            Add(id, label, pane => pane.Camera.LookFrom(direction), key: key);

        void Add(
            string id,
            string label,
            Action<SceneViewport> action,
            Func<SceneViewport, bool>? on = null,
            InputKey key = InputKey.Unknown
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
                    Checked = on is null ? null : () => viewport is { } pane && on(pane)
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
        var menu = Shell.Menus.InsertMenu(2, new StringId("editor.menu.scene", "Scene"));

        menu.Add("scene.create-entity", "scene.delete-entity").AddSeparator();

        menu.AddSubmenu(new StringId("editor.menu.gizmo", "Gizmo"))
            .Add("scene.translate", "scene.rotate", "scene.scale")
            .AddSeparator()
            .Add("scene.toggle-space", "scene.toggle-pivot", "scene.toggle-snap");

        menu.AddSubmenu(new StringId("editor.menu.camera", "Camera"))
            .Add("scene.view-front", "scene.view-right", "scene.view-top")
            .AddSeparator()
            .Add("scene.toggle-projection");

        menu.AddSeparator().Add("scene.focus", "scene.frame-all");
        menu.AddSeparator().Add("scene.toggle-grid");

        // The View menu is the shell's, and this is the one thing the application has to put on it:
        // saving an arrangement needs somewhere to save it, which is the user store this owns.
        Shell.View.AddSeparator().Add("view.save-layout");

        // ⚠ Rebuilt here rather than left to the one a registration triggers, which is why this runs
        // last. Every `Commands.Add` and every `Keys.SetDefault` above rebuilt the bar against a
        // model that did not yet have this menu in it, and nothing after this point registers either.
        Shell.MenuBar.Rebuild();
    }

    /// <summary>Whether the selection differs from what the inspector is showing.</summary>
    bool Changed() {
        if (shown.Count != scene.Selection.Count) {
            return true;
        }

        for (var index = 0; index < shown.Count; index++) {
            if (shown[index] != scene.Selection[index]) {
                return true;
            }
        }

        return false;
    }

    void ShowSelection() {
        shown.Clear();
        shown.AddRange(scene.Selection);

        // One SceneEntity per selected entity, made fresh: it holds a handle and a document and
        // nothing else, so keeping a cache of them would be bookkeeping in exchange for nothing.
        inspector?.Inspect([.. shown.Select(entity => new SceneEntity(scene, entity))]);

        Shell.Status = shown.Count switch {
            0 => project.Name,
            1 => scene.NameOf(shown[0]),
            _ => $"{shown.Count} selected"
        };
    }

    void RebuildHierarchy() {
        if (hierarchy is not { } tree) {
            return;
        }

        while (tree.Root.Children.Count > 0) {
            tree.Root.Remove(tree.Root.Children[^1]);
        }

        foreach (var entity in scene.Roots) {
            Branch(tree.Root, entity);
        }

        tree.Refresh();

        foreach (var node in tree.Root.Children) {
            tree.Expand(node);
        }

        void Branch(TreeNode parent, Entity entity) {
            var node = parent.Add(scene.NameOf(entity), entity);

            foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
                Branch(node, child);
            }
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

    /// <summary>A box around some entities' origins.</summary>
    /// <remarks>
    ///     ⚠ <b>Origins, not render bounds.</b> Nothing here has a mesh yet, so "how big is it" has
    ///     no answer — a padded box around the points is what makes focus and frame-all land
    ///     somewhere sensible until there is geometry to measure. <c>EditorCamera.Focus</c> floors
    ///     the radius anyway, so a single entity still gets a usable distance.
    /// </remarks>
    BoundingBox Around(IEnumerable<Entity> entities) {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var any = false;

        foreach (var entity in entities) {
            if (!world.IsAlive(entity) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            var position = new Transform(world, entity).Position;

            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
            any = true;
        }

        return any
            ? new BoundingBox(low - new Vector3(0.5f), high + new Vector3(0.5f))
            : new BoundingBox(new Vector3(-1f), new Vector3(1f));
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
        var parent = scene.Selection.Count > 0 ? scene.Selection[0] : Entity.Null;
        var created = scene.Create("Entity", LocalTransform.Identity, parent);

        scene.Selection.Set([created]);
    }

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
