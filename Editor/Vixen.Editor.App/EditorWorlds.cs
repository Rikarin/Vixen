// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Navigation;
using Vixen.Navigation.Baking;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Styling;

namespace Vixen.Editor.App;

/// <summary>Doc 20's B6: the four world-building surfaces, and multi-scene under them.</summary>
/// <remarks>
///     <para>
///         <b>Three panels over one document, and the document is a sidecar beside the scene.</b>
///         World Settings, Lighting and Navigation are three <c>InspectorView</c>s over three
///         properties of <see cref="WorldSettings" /> — doc 11's "adding a setting is declaring a
///         type", applied to the scene rather than to the project. What each panel adds beside its
///         rows is the verb that makes the numbers mean something: a bake, a debug draw, a budget
///         readout.
///     </para>
///     <para>
///         ⚠ <b>Layers and tags are deliberately not here.</b> Doc 20's own row says they "need an
///         ECS-side concept first", and a panel that maintained a list of layer names nothing in the
///         runtime reads would fail this document's second bar — a promise the editor breaks the
///         second time it is used.
///     </para>
///     <para>
///         ⚠ <b>Multi-scene is additive into one world, which is what the runtime already does.</b>
///         <c>SceneManager</c> loads scenes into a world additively and unloads each on its own, and
///         <c>SceneDocument</c> is a document over one of its handles. So "open a second scene" is a
///         second document over the same world rather than a second world — which is what makes an
///         entity handle mean one thing across the editor, and what lets a sequence in one scene
///         drive an actor in another.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>What the world settings panel is called in an arrangement.</summary>
    internal const string WorldPanel = "world-settings";

    /// <summary>And the lighting one.</summary>
    internal const string LightingPanel = "lighting";

    /// <summary>And the navigation one.</summary>
    internal const string NavigationPanel = "navigation";

    /// <summary>And the list of open scenes.</summary>
    internal const string ScenesPanel = "scenes";

    /// <summary>The context id of the world-building panels.</summary>
    internal const string WorldContext = "world";

    /// <summary>The active scene's settings, read when the scene is loaded and written on save.</summary>
    WorldSettings world0 = new();

    /// <summary>Every scene open in this editor, the first being the one it started with.</summary>
    /// <remarks>
    ///     ⚠ <b>A list beside <c>scene</c> rather than instead of it.</b> Half the editor holds the
    ///     active scene — the viewport, the gizmo, the inspector, the picker — and replacing that
    ///     field with an index would be a change to every one of them. What multi-scene adds is the
    ///     <i>others</i>, and making one of them active is an assignment to the field they all
    ///     already read.
    /// </remarks>
    readonly List<EditorScene> openScenes = [];

    /// <summary>One scene open in the editor: its document, its file, and its settings.</summary>
    /// <param name="Document">The document.</param>
    /// <param name="Path">Where it is written, absolute.</param>
    /// <remarks>
    ///     ⚠ <b>The path is here rather than on the document, because a document does not know where
    ///     it is written</b> — it has an <c>ISceneWriter</c>, which is deliberately an interface so
    ///     that a test can write into memory. The editor is what knows about files.
    /// </remarks>
    internal sealed record EditorScene(SceneDocument Document, string Path) {
        /// <summary>Its world settings.</summary>
        public WorldSettings Settings { get; set; } = new();

        /// <summary>Whether its entities are drawn.</summary>
        /// <remarks>
        ///     ⚠ <b>Editor state, like an entity's own visibility.</b> Hiding a scene to work on the
        ///     one in front of it must not change what ships, so it is not written and it is not
        ///     undoable — the rule <c>entity.toggle-hidden</c> already follows.
        /// </remarks>
        public bool IsVisible { get; set; } = true;

        /// <summary>Whether its entities can be selected or edited.</summary>
        public bool IsLocked { get; set; }
    }

    /// <summary>The scene commands and the outliner act on: the one new entities go into.</summary>
    internal EditorScene ActiveScene => openScenes.Count > 0 ? openScenes[activeScene] : new(scene, scenePath);

    int activeScene;

    /// <summary>Every scene open in this editor.</summary>
    internal IReadOnlyList<EditorScene> Scenes => openScenes;

    /// <summary>The active scene's world settings.</summary>
    internal WorldSettings World => world0;

    /// <summary>The four panels doc 20's B6 names that this milestone builds.</summary>
    void WorldPanels() {
        Shell.RegisterPanel(
            WorldPanel,
            new StringId("editor.panel.world", "World Settings"),
            panel => {
                Contextual(panel, WorldContext);

                Settings(panel, () => world0.Environment, "Environment");
                Settings(panel, () => world0.Physics, "Physics");
            }
        );

        Shell.RegisterPanel(
            LightingPanel,
            new StringId("editor.panel.lighting", "Lighting"),
            panel => {
                Contextual(panel, WorldContext);

                Settings(panel, () => world0.Lighting, "Global Illumination");

                // ⚠ The budget readout is arithmetic over the settings rather than a measurement,
                // and it says so. What a probe spacing costs in probes is a division; what it costs
                // in milliseconds is the renderer's answer and the renderer does not have a GI path
                // yet — doc 19's is Phase 7's neighbourhood. A number presented as measured when it
                // was derived is the failure this panel most easily could have.
                var facts = panel.Add("world-facts");

                var refresh = panel.Add<Button>();
                refresh.Label = "Recompute budgets";
                refresh.Clicked += _ => Budgets(facts);

                Budgets(facts);

                foreach (var view in LightingDebugViews) {
                    Fact(panel, view, "needs the GI path — doc 14 Phase 7");
                }
            }
        );

        Shell.RegisterPanel(
            NavigationPanel,
            new StringId("editor.panel.navigation", "Navigation"),
            panel => {
                Contextual(panel, WorldContext);

                Settings(panel, () => world0.Navigation, "Agent");

                var report = panel.Add("world-facts");

                var bake = panel.Add<Button>();
                bake.Label = "Bake navigation mesh";
                bake.Clicked += _ => Bake(report);

                var draw = panel.Add<CheckBox>();
                draw.Label = "Draw the navigation mesh";
                draw.IsChecked = navigationDrawn;
                draw.CheckedChanged += (_, on) => navigationDrawn = on;

                Report(report, navigation);
            }
        );

        Shell.RegisterPanel(
            ScenesPanel,
            new StringId("editor.panel.scenes", "Scenes"),
            panel => {
                Contextual(panel, SceneContext);

                sceneList = panel.Add("scene-list");
                RefreshScenes();
            }
        );
    }

    /// <summary>The debug views a GI solution owes, named as absent rather than left out.</summary>
    /// <remarks>
    ///     Doc 20's first bar: "a verb that is not implemented is <i>visibly</i> not implemented
    ///     rather than absent". Four rows saying what they will show reads as a renderer that will;
    ///     four missing rows read as one that cannot.
    /// </remarks>
    static readonly string[] LightingDebugViews = [
        "Distance-field coverage",
        "Irradiance probe placement",
        "Surface-cache residency",
        "Indirect-only"
    ];

    UiElement? sceneList;
    bool navigationDrawn;
    NavMeshTileData? navigation;

    /// <summary>Draws one settings group as inspector rows, and writes the sidecar when it changes.</summary>
    /// <remarks>
    ///     ⚠ <b>Saved on change rather than behind an Apply, which is the opposite of the settings
    ///     window's rule and is right here for the reason that rule gives.</b> Doc 20 asks for an
    ///     explicit Apply where a setting <i>costs</i> something to change — lowering the undo depth
    ///     drops history, changing the content target invalidates an import. Nothing on this panel
    ///     costs anything: the fog colour is read by the next frame and the agent radius by the next
    ///     bake, so a person who changes one wants to see it.
    /// </remarks>
    void Settings(DockPanel panel, Func<object> group, string title) {
        panel.Add("world-title").Text = title;

        var inspector = panel.Add<InspectorView>();

        inspector.EditedDocument = null;
        inspector.Inspect(group());

        inspector.ValueChanged += (_, _) => SaveWorld();
    }

    /// <summary>Writes the active scene's settings beside it.</summary>
    /// <remarks>
    ///     ⚠ <b>Failures are a notification and not an exception.</b> A read-only working copy is an
    ///     ordinary thing to meet, and this runs from a field's own change handler — where an
    ///     exception would take the frame down with the scene still unsaved.
    /// </remarks>
    void SaveWorld() {
        try {
            world0.Save(ActiveScene.Path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the world settings", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Reads the active scene's settings, which is what loading a scene does.</summary>
    void LoadWorld() {
        world0 = WorldSettings.Load(ActiveScene.Path);
        if (openScenes.Count > activeScene) {
            openScenes[activeScene].Settings = world0;
        }
    }

    /// <summary>What the lighting settings cost, derived and labelled as derived.</summary>
    void Budgets(UiElement facts) {
        while (facts.Children.Count > 0) {
            facts.Children[^1].Remove();
        }

        var lighting = world0.Lighting;

        // The probe count over the distance-field range, which is the volume GI actually covers.
        var side = Math.Max(1, (int) (2f * lighting.DistanceFieldRange / Math.Max(lighting.ProbeSpacing, 0.01f)));
        var probes = (long) side * side * Math.Max(1, side / 4);

        var voxels = Math.Max(1, (int) (2f * lighting.DistanceFieldRange / Math.Max(lighting.DistanceFieldVoxel, 0.01f)));

        Fact(facts, "Probes in range", string.Create(CultureInfo.InvariantCulture, $"{probes:N0} (derived)"));

        Fact(
            facts,
            "Frames to refresh them",
            string.Create(CultureInfo.InvariantCulture, $"{probes / Math.Max(1, lighting.ProbeBudget):N0} (derived)")
        );

        Fact(
            facts,
            "Distance field",
            string.Create(CultureInfo.InvariantCulture, $"{voxels:N0}³ voxels at the finest level (derived)")
        );

        Fact(
            facts,
            "Surface cache",
            string.Create(CultureInfo.InvariantCulture, $"{lighting.SurfaceCacheBudget:N0} cards a frame")
        );
    }

    /// <summary>One "label: value" row in a derived-facts readout.</summary>
    /// <remarks>
    ///     <see cref="FactRow" />, the shared part — this was the original of the four lines
    ///     <c>TerrainModule</c> and <c>WaterModule</c> each kept a copy of.
    /// </remarks>
    static void Fact(UiElement into, string label, string value) {
        var row = into.Add<FactRow>();

        row.Name = label;
        row.Value = value;
    }

    /// <summary>Bakes a navigation mesh over the scene's geometry and reports what came out.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Over the primitives' boxes, because the viewport draws primitives and not
    ///         meshes.</b> <c>NavMeshBaker</c> takes vertices and indices; what the editor's world has
    ///         is <c>PrimitiveShape</c> primitives and world matrices, so what is voxelised is the unit box
    ///         each primitive occupies, transformed. That is a real navigation mesh over a real
    ///         blockout — which is what a level designer bakes at this stage anyway — and it becomes
    ///         the true geometry the day the renderer has meshes, with nothing here changing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On the calling thread, and it says how long it took.</b> A bake of a blockout is
    ///         tens of milliseconds; a bake of a level is not, and moving it onto <c>ContentTasks</c>
    ///         is the same piece of work the content build already models. The readout is what makes
    ///         the moment that stops being acceptable visible daily rather than asserted here.
    ///     </para>
    /// </remarks>
    void Bake(UiElement report) {
        var settings = world0.Navigation;

        List<Vector3> vertices = [];
        List<int> indices = [];

        foreach (var entity in ActiveScene.Document.Entities) {
            if (!PrimitiveShapes.TryGet(ActiveScene.Document.World, entity, out _)
                || !ActiveScene.Document.World.Has<WorldTransform>(entity)) {
                continue;
            }

            Box(vertices, indices, ActiveScene.Document.World.Get<WorldTransform>(entity).Value);
        }

        if (indices.Count == 0) {
            navigation = null;
            Report(report, null, "Nothing in this scene has geometry to walk on.");

            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try {
            navigation = NavMeshBaker.Bake(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices),
                new NavMeshBuildSettings {
                    CellSize = settings.CellSize,
                    CellHeight = settings.CellHeight,
                    AgentRadius = settings.AgentRadius,
                    AgentHeight = settings.AgentHeight,
                    AgentMaxClimb = settings.AgentClimb,
                    AgentMaxSlope = settings.AgentSlope
                }
            );

            Report(
                report,
                navigation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Baked in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms "
                    + $"over {indices.Count / 3:N0} triangle(s)."
                )
            );
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
            navigation = null;
            Report(report, null, exception.Message);
        }
    }

    /// <summary>Adds a unit box's twelve triangles, transformed, to a bake's geometry.</summary>
    static void Box(List<Vector3> vertices, List<int> indices, Matrix4x4 transform) {
        var first = vertices.Count;

        for (var corner = 0; corner < 8; corner++) {
            vertices.Add(
                Matrix4x4.TransformPosition(
                    new(
                        (corner & 1) == 0 ? -0.5f : 0.5f,
                        (corner & 2) == 0 ? -0.5f : 0.5f,
                        (corner & 4) == 0 ? -0.5f : 0.5f
                    ),
                    transform
                )
            );
        }

        // The six faces, two triangles each, wound so that an up-facing one faces up — which is what
        // decides whether the baker calls it walkable.
        ReadOnlySpan<int> faces = [
            0, 2, 3, 0, 3, 1,
            4, 5, 7, 4, 7, 6,
            0, 1, 5, 0, 5, 4,
            2, 6, 7, 2, 7, 3,
            0, 4, 6, 0, 6, 2,
            1, 3, 7, 1, 7, 5
        ];

        foreach (var index in faces) {
            indices.Add(first + index);
        }
    }

    static void Report(UiElement report, NavMeshTileData? tile, string? message = null) {
        while (report.Children.Count > 0) {
            report.Children[^1].Remove();
        }

        if (message is { Length: > 0 }) {
            Fact(report, "Bake", message);
        }

        if (tile is null) {
            Fact(report, "Navigation mesh", "not baked");

            return;
        }

        Fact(report, "Polygons", tile.Polys.Length.ToString("N0", CultureInfo.InvariantCulture));
        Fact(report, "Vertices", tile.Vertices.Length.ToString("N0", CultureInfo.InvariantCulture));
    }

    // ── Multi-scene ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a scene beside the ones already open, rather than over them.</summary>
    /// <param name="path">Where the scene file is, absolute.</param>
    /// <returns>The document, or <see langword="null" /> when it would not load.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Into the same world and its own <c>SceneHandle</c>.</b> That is what
    ///         <c>SceneManager</c> is for and it is what makes an entity handle mean one thing across
    ///         the editor — a second world would mean the outliner, the gizmo and the picker each
    ///         needing to know which world an entity came from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A scene already open is activated rather than loaded twice.</b> Two documents over
    ///         one file are two undo histories over one set of bytes, and whichever saved last wins —
    ///         which is <c>AssetEditorRegistry.TryOpen</c>'s argument, restated for scenes.
    ///     </para>
    /// </remarks>
    internal SceneDocument? OpenSceneAdditively(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (openScenes.FindIndex(open => string.Equals(open.Path, path, StringComparison.Ordinal)) is var found and >= 0) {
            Activate(found);

            return openScenes[found].Document;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var document = new SceneDocument(project, world, AssetId.Empty, name) { Writer = new SceneFileWriter(path) };

        try {
            SceneSerializer.Load(document, path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            document.Close();
            Shell.Notifications.Show("Could not open the scene", NotificationSeverity.Error, exception.Message);

            return null;
        }

        document.Stack.Clear();
        document.Stack.MarkClean();
        document.StructureChanged += _ => hierarchyStale = true;
        document.Renamed += (_, _) => hierarchyStale = true;

        openScenes.Add(new(document, path) { Settings = WorldSettings.Load(path) });

        hierarchyStale = true;
        RefreshScenes();

        Shell.Notifications.Success(Path.GetFileName(path) + " opened additively");
        return document;
    }

    /// <summary>Makes one of the open scenes the one commands act on.</summary>
    /// <param name="index">Which one.</param>
    internal void Activate(int index) {
        if (index < 0 || index >= openScenes.Count || index == activeScene) {
            return;
        }

        activeScene = index;

        var open = openScenes[index];

        SetActiveScene(open.Document, open.Path);
        world0 = open.Settings;

        project.Activate(open.Document);

        // Every panel that follows the active scene is pointed at it here rather than each of them
        // watching a signal, because "which scene is active" is this class's arbitration in exactly
        // the way "which selection the inspector follows" already is.
        if (viewports is { } layout) {
            layout.Document = open.Document;
        }

        hierarchyStale = true;
        RefreshScenes();
        Retitle();
    }

    /// <summary>Points every field that holds the active scene at another one.</summary>
    /// <remarks>
    ///     ⚠ <b>The picker and the probe are rebuilt rather than retargeted.</b> Both cache a mesh
    ///     per shape kind against the document they were made for — which is what
    ///     <see cref="EditorApplication" />'s own remarks say they are held for — so pointing one at
    ///     a different scene would answer clicks with the previous scene's geometry.
    /// </remarks>
    void SetActiveScene(SceneDocument document, string path) {
        scene = document;
        scenePath = path;

        picker = new ScenePicker(document);
        probe = new SceneProbe(document);
    }

    /// <summary>Closes an additively-opened scene, asking first if it has changes.</summary>
    /// <param name="index">Which one.</param>
    /// <remarks>
    ///     ⚠ <b>The first scene cannot be closed.</b> An editor with no scene at all has no viewport
    ///     subject, no outliner and nowhere to put a new entity — every one of which is a panel that
    ///     would have to grow an empty state for a condition nobody wants to be in. Closing the last
    ///     one is New Scene.
    /// </remarks>
    internal void CloseScene(int index) {
        if (index <= 0 || index >= openScenes.Count) {
            return;
        }

        var open = openScenes[index];

        Confirm(
            open.Document.IsDirty.Value,
            "Discard unsaved changes?",
            Path.GetFileName(open.Path) + " has changes that have not been written.",
            () => {
                // ⚠ The entities go with the scene. The world is shared, so a document closed without
                // unloading its handle would leave its entities in the level with nothing naming
                // them — which is the multi-scene version of a leak.
                open.Document.Delete([.. open.Document.Roots]);
                open.Document.Close();

                openScenes.RemoveAt(index);

                if (activeScene >= index) {
                    activeScene = Math.Max(0, activeScene - 1);
                }

                Activate(activeScene);
                hierarchyStale = true;
                RefreshScenes();
            },
            confirm: "Discard"
        );
    }

    /// <summary>Rebuilds the Scenes panel's rows.</summary>
    void RefreshScenes() {
        if (sceneList is not { } list) {
            return;
        }

        while (list.Children.Count > 0) {
            list.Children[^1].Remove();
        }

        for (var index = 0; index < openScenes.Count; index++) {
            var position = index;
            var open = openScenes[index];

            var row = list.Add("scene-row");
            if (index == activeScene) {
                row.AddClass("selected");
            }

            var name = row.Add("scene-name");
            name.Text = Path.GetFileNameWithoutExtension(open.Path) + (open.Document.IsDirty.Value ? " *" : string.Empty);

            var visible = row.Add<CheckBox>();
            visible.IsChecked = open.IsVisible;

            visible.CheckedChanged += (_, on) => {
                open.IsVisible = on;

                foreach (var entity in open.Document.Roots) {
                    open.Document.SetHidden(entity, !on);
                }

                RefreshMarks();
            };

            var locked = row.Add<CheckBox>();
            locked.IsChecked = open.IsLocked;

            locked.CheckedChanged += (_, on) => {
                open.IsLocked = on;

                foreach (var entity in open.Document.Roots) {
                    open.Document.SetLocked(entity, on);
                }

                RefreshMarks();
            };

            row.AddHandler<PointerEvent>(
                (_, args) => {
                    if (args.Action == PointerAction.Pressed) {
                        Activate(position);
                    }
                }
            );

            if (index == 0) {
                continue;
            }

            var close = row.Add<Button>();

            close.Label = "Close";
            close.Size = ControlSize.Small;
            close.Variant = ControlVariant.Subtle;
            close.Clicked += _ => CloseScene(position);
        }
    }

    /// <summary>What the Scene menu's category is called, matching the viewport commands' own.</summary>
    static readonly StringId SceneCategory = new("editor.category.scene", "Scene");

    /// <summary>The verbs doc 20's B6 and its multi-scene row name.</summary>
    void WorldCommands() {
        CreateAssetCommands();

        Panel("scene.world-settings", new StringId("editor.command.scene.world-settings", "World Settings…"), WorldPanel, SceneCategory);
        Panel("scene.lighting", new StringId("editor.command.scene.lighting", "Lighting…"), LightingPanel, SceneCategory);
        Panel("scene.navigation", new StringId("editor.command.scene.navigation", "Navigation…"), NavigationPanel, SceneCategory);
        Panel("scene.scenes", new StringId("editor.command.scene.scenes", "Scenes"), ScenesPanel, SceneCategory);

        Verb(
            "scene.open-additive",
            new StringId("editor.command.scene.open-additive", "Open Scene Additively…"),
            SceneCategory,
            OpenSceneAdditive,
            enabled: () => services.CanPick
        );

        Verb(
            "scene.save-all-scenes",
            new StringId("editor.command.scene.save-all-scenes", "Save All Scenes"),
            SceneCategory,
            SaveAllScenes,
            enabled: () => openScenes.Exists(open => open.Document.IsDirty.Value)
        );

        Planned(
            "scene.layers",
            new StringId("editor.command.scene.layers", "Layers and Tags…"),
            SceneCategory,
            "Layers need an ECS-side concept first; a list of names nothing reads would be a promise the editor breaks."
        );
    }

    void OpenSceneAdditive() {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        deferred.When(
            dialogs.OpenFileAsync(
                new Platform.FileDialogOptions {
                    Title = "Open Scene Additively",
                    InitialDirectory = project.Paths.Assets,
                    Filters = [new Platform.FileFilter("Vixen scene", SceneSerializer.Extension.TrimStart('.'))]
                }
            ),
            path => {
                if (path is not null) {
                    OpenSceneAdditively(path);
                }
            },
            failure => Shell.Notifications.Show("Could not open the scene", NotificationSeverity.Error, failure.Message)
        );
    }

    void SaveAllScenes() {
        var written = 0;

        foreach (var open in openScenes) {
            if (!open.Document.IsDirty.Value) {
                continue;
            }

            try {
                open.Document.Save();
                open.Settings.Save(open.Path);

                written++;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                Shell.Notifications.Show(
                    "Could not save " + Path.GetFileName(open.Path),
                    NotificationSeverity.Error,
                    exception.Message
                );
            }
        }

        Shell.Notifications.Success($"{written} scene(s) saved");
        RefreshScenes();
    }

    // ── Making one of the new assets ────────────────────────────────────────────────────────────

    /// <summary>The asset kinds the authoring surfaces add, and what an empty one of each is called.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An empty file, not a template.</b> Every one of these documents opens a zero-byte
    ///         file as a sensible new one — a VFX graph with a spawner and an output, a shader graph
    ///         with a colour property and a master, an animation graph with one layer and one state,
    ///         an input asset with <c>Player/Move</c> — which is <c>AssetFile.Read</c>'s own bargain
    ///         and is why creating one is a <c>File.Create</c> rather than a copy of something in a
    ///         templates folder that would then be a second place the defaults live.
    ///     </para>
    ///     <para>
    ///         Seven of these are E5's and the shader graph is the eighth, added when its panel was:
    ///         doc 20's Create ▸ names it, and until this line existed the only way to reach the
    ///         editor was to make the file outside the editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Doc 31's four are the exception to the empty-file bargain, and they have to
    ///         be.</b> Those documents open a zero-byte file as a sensible new one; a
    ///         <c>.vxlayer</c> is read by <c>TerrainAssetImporter</c>, which deserialises it and runs
    ///         the type's own <c>Validate()</c> — so an empty one imports as a layer with no name and
    ///         reports itself as incomplete beside the file. Starter text costs four literals and is
    ///         the difference between "created" and "created, with a warning".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And they do not open, because nothing claims them.</b> There is no asset editor
    ///         for a layer or a foliage type — they are edited in the inspector and in the terrain
    ///         panels — so opening one would put "No editor claims that file" on screen every time
    ///         somebody made one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>.vxterrain</c> is deliberately not on this list.</b> A terrain is a size, a
    ///         tile count and a height range before it is a file, and an empty one of those is eight
    ///         gigabytes as easily as eight megabytes — which is what the Terrain panel's create form
    ///         and its derived-cost readout exist for. A Create ▸ line making a default-sized one
    ///         would be the one asset in the menu whose default is a guess about the size of
    ///         somebody's world.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>These are registered, not read.</b> F3 reported this as a literal array in the
    ///         application, which is why a plugin introducing an asset type could not put it in
    ///         Create ▸ at all. It is still a literal — the application is where the application's own
    ///         kinds belong — but it goes into <c>EditorRegistry</c> at start-up and the menu is built
    ///         from the registry, so a contributed kind and a built-in one are the same thing to
    ///         everything downstream.
    ///     </para>
    /// </remarks>
    static readonly NewAssetKind[] BuiltInAssetKinds = [
        new("assets.create-shader-graph", "Shader Graph", ".vxshadergraph", "New Shader Graph"),
        new("assets.create-vfx", "VFX Graph", ".vxvfx", "New Effect"),
        new("assets.create-animation", "Animation Clip", ".vxanim", "New Clip"),
        new("assets.create-animation-graph", "Animation Graph", ".vxanimgraph", "New Animation Graph"),
        new("assets.create-sequence", "Sequence", ".vxseq", "New Sequence"),
        new("assets.create-input", "Input Actions", ".vxinput", "New Input Actions"),
        new("assets.create-mixer", "Audio Mixer", ".vxmixer", "New Mixer"),
        new("assets.create-font", "Font", ".vxfont", "New Font"),

        new("assets.create-terrain-layer", "Terrain Layer", ".vxlayer", "New Terrain Layer", NewLayer, false),
        new("assets.create-foliage", "Foliage Type", ".vxfoliage", "New Foliage Type", NewFoliage, false),
        new("assets.create-grass", "Grass Type", ".vxgrass", "New Grass Type", NewGrass, false),
        new("assets.create-spline", "Spline", ".vxspline", "New Spline", NewSpline, false),

        new("assets.create-move-set", "Move Set", ".vxmoveset", "New Move Set", NewMoveSet),
        new("assets.create-proxy-shapes", "Proxy Shapes", ".vxproxyshapes", "New Proxy Shapes", NewProxyShapes),
        new("assets.create-shape-vocabulary", "Shape Vocabulary", ".vxshapevocab", "New Shape Vocabulary", NewVocabulary),
        new("assets.create-priorities", "Priority Ladder", ".vxpriorities", "New Priority Ladder", NewPriorities, false),
        new("assets.create-constraint-template", "Constraint Template", ".vxconstraints", "New Constraint Template", NewTemplate, false),
        new("assets.create-harness", "Variation Harness", ".vxharness", "New Harness", NewHarness)
    ];

    /// <summary>An empty movement vocabulary.</summary>
    /// <remarks>
    ///     No rows, because a row with no clip is an import error and a row naming a clip that does
    ///     not exist is a worse one: the first says "fill this in" beside the file, and the second
    ///     says nothing at all until somebody plays it.
    /// </remarks>
    const string NewMoveSet = """
        name: New Move Set
        entries: []
        rules: []
        """;

    /// <summary>An empty shape set that names neither a vocabulary nor a rig yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Both reference lines are absent rather than pointing at a conventional path.</b> A set
    ///     naming a file that does not exist fails its import — the importer declares the dependency
    ///     and refuses — where one naming none imports clean and unchecked, which is the right state
    ///     for a file somebody made ten seconds ago. The panel has a row for each, so the first thing
    ///     it says about a new set is which one to fill in.
    /// </remarks>
    const string NewProxyShapes = """
        name: New Proxy Shapes
        shapes: []
        """;

    /// <summary>A vocabulary with the two terms almost every humanoid set uses.</summary>
    /// <remarks>
    ///     Two rather than none, because the file's whole purpose is a list somebody adds to — and an
    ///     empty list gives no clue what a term looks like, or that the meaning is the useful half.
    /// </remarks>
    const string NewVocabulary = """
        name: New Vocabulary
        shapes:
          - name: belly
            meaning: The front of the torso, where a hand rests or leans.
          - name: right-palm
            meaning: The gripping face of the right hand.
        tags: []
        classes: []
        """;

    /// <summary>The conventional ladder, spelled out in the file.</summary>
    /// <remarks>
    ///     ⚠ <b>The rungs are in the file and not defaulted by the reader.</b> A priority is a name a
    ///     project agrees on, and the agreement has to be somewhere two people can read and edit it;
    ///     a ladder that came from a constant would be one nobody knew they were allowed to change.
    ///     A hundred apart, because a sub-step is clamped to ±99 and the importer warns about any two
    ///     rungs closer than that.
    /// </remarks>
    const string NewPriorities = """
        name: New Priority Ladder
        step: 100
        rungs:
          - name: flourish
            value: 0
            meaning: A secondary motion. Anything may override it.
          - name: look
            value: 100
            meaning: Where the head is pointed.
          - name: aim
            value: 200
            meaning: Where a weapon is pointed.
          - name: balance
            value: 300
            meaning: Keeping the body over its feet.
          - name: interaction
            value: 400
            meaning: A deliberate reach for something.
          - name: contact
            value: 500
            meaning: A hand or a foot that must not slide.
        """;

    /// <summary>A named template with no tags in it yet.</summary>
    /// <remarks>
    ///     ⚠ <b>The name is why this is not an empty file.</b> The importer refuses a nameless
    ///     template: the name is written into every tag it produces and is how a re-apply finds them
    ///     again, so a nameless one can be applied once and never maintained.
    /// </remarks>
    const string NewTemplate = """
        name: New Template
        revision: 1
        meaning: What this bundle of constraints is for.
        tags: []
        """;

    /// <summary>A run that names nothing yet, and one threshold so that it is a gate.</summary>
    /// <remarks>
    ///     ⚠ <b>This one imports with an error on purpose, and the error is the instructions.</b> A
    ///     harness refuses a plan with no clip and no rig, because a build step that always passes is
    ///     worse than one that says what it is missing — so the two lines an author has to fill in are
    ///     the two the importer complains about, beside the file, the moment it is created.
    ///     <para>
    ///         ⚠ <b><c>''</c> rather than a bare <c>clip:</c>, and the quotes are load-bearing.</b> An
    ///         empty scalar is the document's null, and <see cref="HarnessPlanContent.Clip" /> is
    ///         declared <c>string</c> — so a bare key is refused by the binder before the importer can
    ///         reach it, and the author gets a schema complaint instead of the sentence written for
    ///         them. The error this file is *for* is the importer's, which needs the plan to bind
    ///         first.
    ///     </para>
    /// </remarks>
    const string NewHarness = """
        name: New Harness
        clip: ''
        rig: ''
        samples: 32
        bodies: [0.85, 1.0, 1.2]
        thresholds:
          residual: 0.02
        """;

    /// <summary>A paint layer with a tiling somebody can see, and no texture yet.</summary>
    /// <remarks>
    ///     Four metres, because a ground texture tiled at one metre reads as noise from standing
    ///     height and one tiled at sixteen reads as a blur — and the number an author changes first is
    ///     the one they can already see the effect of.
    /// </remarks>
    const string NewLayer = """
        name: New Layer
        tilingMetres: 4
        """;

    /// <summary>A stored foliage type with a spacing, and no mesh yet.</summary>
    const string NewFoliage = """
        name: New Foliage
        radius: 2
        storage: Stored
        """;

    /// <summary>A derived grass type over a layer that has to be renamed to one that exists.</summary>
    /// <remarks>
    ///     ⚠ <b>The layer name is the one field with no defensible default</b>, so it names the
    ///     conventional one and the importer's own <c>Validate()</c> is what says when it does not
    ///     match the terrain — which is a message beside the file rather than an empty hillside.
    /// </remarks>
    const string NewGrass = """
        name: New Grass
        layer: Grass
        density: 8
        jitter: 0.8
        """;

    /// <summary>Two control points, which is the fewest a curve can be built from.</summary>
    /// <remarks>
    ///     ⚠ <b>One point is an error rather than a warning</b> — <c>SplineAsset.Build</c> throws for
    ///     it, and <c>TerrainAssetImporter</c> fails the import on that. So a created spline has two,
    ///     ten metres apart, and is a road somebody can drag rather than a file that arrives broken.
    /// </remarks>
    const string NewSpline = """
        name: New Spline
        points:
          - position: {x: 0, y: 0, z: 0}
          - position: {x: 10, y: 0, z: 0}
        """;

    /// <summary>Every asset kind Create ▸ offers, in the order it offers them.</summary>
    internal IReadOnlyList<NewAssetKind> AssetKinds =>
        [.. Extensions.All<NewAssetKind>().OrderBy(static kind => kind.Order)];

    /// <summary>The ids of the Create submenu's lines, in the order they are shown.</summary>
    internal IEnumerable<string> CreatableIds => AssetKinds.Select(static kind => kind.Id);

    /// <summary>One command per asset kind, whoever contributed it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One command per kind rather than one that takes a kind</b>, for the reason
    ///         <c>ShapeCommands</c> gives: the registry's unit is a command with an id, a title and an
    ///         enablement, so "Create Animation Graph" being findable in the palette and bindable to a
    ///         key means it has to be its own entry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it listens, because a plugin activates after this has run.</b> A menu built
    ///         once from a registry read once is a menu a plugin cannot reach — F3's problem with an
    ///         extra step rather than F3 fixed. The Create submenu is rebuilt from the registry each
    ///         time it changes, which is also what takes a line back out when a plugin is unloaded.
    ///     </para>
    /// </remarks>
    void CreateAssetCommands() {
        foreach (var kind in BuiltInAssetKinds) {
            contributions.Add(Extensions.Add(kind));
        }

        // Producer 1, on the same terms and for the same reason. See `StandardIcons`.
        foreach (var icon in StandardIcons.Assets) {
            contributions.Add(Extensions.Add(icon));
        }

        foreach (var icon in StandardIcons.Types) {
            contributions.Add(Extensions.Add(icon));
        }

        foreach (var kind in AssetKinds) {
            CreateAssetCommand(kind);
        }

        Extensions.Changed += RefreshAssetKinds;
        Extensions.Changed += RefreshOverlays;
    }

    /// <summary>Follows a contributed or withdrawn scene overlay onto the panes.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="RefreshAssetKinds" /> because it is the same failure.</b> Chrome
    ///     built when the panes were arranged reads the registry once, so a contribution arriving
    ///     later — a project script's first build, a plugin enabled from the manager, a reload —
    ///     registers and never appears. That is one tier working and another silently not, from the
    ///     same declaration in the same words.
    /// </remarks>
    void RefreshOverlays(Type kind) => chrome?.Refreshed(kind);

    /// <summary>Puts a kind's command in the registry, if it is not already there.</summary>
    void CreateAssetCommand(NewAssetKind kind) {
        if (Shell.Commands[kind.Id] is not null) {
            return;
        }

        Verb(
            kind.Id,
            new StringId("editor.command." + kind.Id, kind.Title),
            CategoryAssets,
            () => CreateAsset(kind.Extension, kind.DefaultName, kind.NewContents(), kind.Opens)
        );
    }

    /// <summary>Follows a contributed or withdrawn asset kind into the commands and the menu.</summary>
    /// <remarks>
    ///     ⚠ <b>The command for a withdrawn kind is left alone.</b> A plugin's own registration scope
    ///     is what removes its command — see <c>PluginContext.AddCommand</c> — and removing it from
    ///     here as well would mean a kind and a command withdrawn in either order sometimes threw and
    ///     sometimes did not. What this owns is the menu.
    /// </remarks>
    void RefreshAssetKinds(Type kind) {
        if (kind != typeof(NewAssetKind)) {
            return;
        }

        foreach (var contributed in AssetKinds) {
            CreateAssetCommand(contributed);
        }

        // The submenus themselves are `MenuGroup.AddDynamic` over `CreatableIds`, so their contents
        // are decided when the bar is built rather than when it was described. This is what makes it
        // build again.
        Shell.MenuBar.Rebuild();
    }

    /// <summary>Makes an empty asset in the browser's folder and opens it.</summary>
    /// <remarks>
    ///     ⚠ <b>Scanned before it is opened, because a document is found by its GUID.</b> The file
    ///     has to be in the asset database for <c>AssetEditorRegistry.TryOpen</c> to have an identity
    ///     to open it under — and without the sidecar the scan mints, a second scan would give it a
    ///     new one and every reference to it would dangle.
    /// </remarks>
    /// <param name="extension">What the file is called after the dot.</param>
    /// <param name="name">What it is called before it.</param>
    /// <param name="contents">
    ///     What to write into it. Empty is the zero-byte file the remark above describes; anything
    ///     else is a starter document, which the four kinds that are read by an importer rather than
    ///     by a document need.
    /// </param>
    /// <param name="opens">Whether to open it, which needs an asset editor that claims the extension.</param>
    void CreateAsset(string extension, string name, string contents = "", bool opens = true) {
        var folder = "Assets";

        if (project.Selection.Count > 0 && project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            folder = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "Assets";
        }

        var directory = project.Paths.Absolute(folder);
        var path = Path.Combine(directory, name + extension);
        var attempt = 2;

        while (File.Exists(path)) {
            path = Path.Combine(directory, $"{name} {attempt++}{extension}");
        }

        try {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, contents);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not create the asset", NotificationSeverity.Error, exception.Message);

            return;
        }

        project.Assets.Scan();
        browser?.Rescan();

        var relative = project.Paths.Relative(path);

        if (opens && project.Assets.TryGetByPath(relative, out var created)) {
            Open(created.Guid);
        }

        Shell.Notifications.Success(Path.GetFileName(path) + " created");
    }

    /// <summary>Steps the authoring surfaces that move by themselves, once a frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Found by walking the document tree rather than by holding a reference.</b> An asset
    ///     editor's panel is registered on demand and its factory runs again on every reopen, so a
    ///     field pointing at the view would be a field pointing at a control that has left the
    ///     document — which is the shape of mistake that took the editor down when the Scene tab was
    ///     first closed. Walking is a few dozen elements once a frame and cannot go stale.
    /// </remarks>
    void AuthoringUpdate(TimeSpan delta) {
        foreach (var element in Moving(Shell.Document.Root)) {
            switch (element) {
                case Vixen.Editor.AssetEditors.Vfx.VfxGraphView effect:
                    effect.Tick(delta);
                    break;

                case Vixen.Editor.AssetEditors.Sequencing.SequenceView sequence:
                    sequence.Tick(delta);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Every element under one, depth first.</summary>
    static IEnumerable<UiElement> Moving(UiElement element) {
        yield return element;

        foreach (var child in element.Children) {
            foreach (var found in Moving(child)) {
                yield return found;
            }
        }
    }

    /// <summary>The four panels' own elements, as a sheet.</summary>
    /// <remarks>
    ///     A sixth user-agent sheet, after the five the constructor already loads, and written
    ///     against the tokens they declare — the same arrangement <c>BrowserTheme</c> has, and for
    ///     the same reason: these elements are this assembly's panels and nobody else's business.
    /// </remarks>
    internal static class WorldTheme {
        /// <summary>Adds the sheet to a document.</summary>
        /// <param name="document">The document.</param>
        /// <returns>The sheet's index, for a hot reload.</returns>
        public static int Install(UiDocument document) {
            ArgumentNullException.ThrowIfNull(document);

            return document.Load(Css, StyleOrigin.UserAgent);
        }

        /// <summary>The stylesheet's text.</summary>
        /// <remarks>
        ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>.</b> Sixteen
        ///     lines rather than the hundreds its siblings carry, but it moved for the same reasons
        ///     and one of its own: a stylesheet buried in the middle of a thousand-line
        ///     <c>.cs</c> is the one nobody finds. It is <c>WorldTheme.vcss</c> at the project root
        ///     now, embedded by the glob in <c>Core/Vixen.Ui/build/Vixen.Ui.targets</c>, which this
        ///     project already imported.
        ///     <para>
        ///         Cached for the same reason the others are: the resource is immutable, so the
        ///         cache cannot go stale — a hot reload replaces the sheet through
        ///         <c>UiDocument</c>, not through here.
        ///     </para>
        /// </remarks>
        public static string Css => sheet ??= Read("Vixen.Editor.App.WorldTheme.vcss");

        static string? sheet;

        static string Read(string name) {
            var assembly = typeof(WorldTheme).Assembly;

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is "
                    + "added by the .vcss glob in Vixen.Ui.targets, which this project imports at "
                    + "the bottom of its .csproj.");

            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
    }
}