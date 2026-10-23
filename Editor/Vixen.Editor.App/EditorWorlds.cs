// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
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

    /// <summary>The context id of the world-building panels.</summary>
    internal const string WorldContext = "world";

    /// <summary>The active scene's settings, read when the scene is loaded and written on save.</summary>
    WorldSettings world0 = new();

    /// <summary>What the editor knows about each open scene's file, keyed by its document.</summary>
    /// <remarks>
    ///     ⚠ <b>Which scenes are open, in what order, and which is active are
    ///     <see cref="ISceneEditing" />'s (#1290)</b> — <see cref="SceneEditing.Scenes" /> is the list
    ///     and this is only the file beside each entry: its path and its world settings. The view marks
    ///     the Scenes panel toggles are the panel's, <see cref="ScenesPanel" />. The application follows
    ///     the service's <see cref="ISceneEditing.ActiveChanged" /> rather than assigning <c>scene</c>
    ///     itself, and its <see cref="ISceneEditing.ScenesChanged" /> to forget a closed scene's file.
    /// </remarks>
    readonly Dictionary<SceneDocument, EditorScene> sceneFiles = [];

    /// <summary>One scene open in the editor: its document, its file, and its settings.</summary>
    /// <param name="Document">The document.</param>
    /// <param name="Path">Where it is written, absolute.</param>
    /// <remarks>
    ///     ⚠ <b>The path is here rather than on the document, because a document does not know where
    ///     it is written</b> — it has an <c>ISceneWriter</c>, which is deliberately an interface so
    ///     that a test can write into memory. The editor is what knows about files.
    /// </remarks>
    internal sealed record EditorScene(SceneDocument Document, string Path) {
        /// <summary>The file the scene writes now, which is not always <see cref="Path" />.</summary>
        /// <remarks>
        ///     ⚠ <b>Save As and Open Scene move the writer and leave <see cref="Path" /> where it
        ///     was</b>, so anything asking "is this file open" asks this and not that (#1416).
        /// </remarks>
        public string Writes => Document.Writer is SceneFileWriter writer ? writer.Path : Path;

        /// <summary>Its world settings.</summary>
        public WorldSettings Settings { get; set; } = new();
    }

    /// <summary>The scene commands and the outliner act on: the one new entities go into.</summary>
    internal EditorScene ActiveScene => sceneFiles.TryGetValue(scene, out var open) ? open : new(scene, scenePath);

    /// <summary>Every scene open in this editor, in the service's order.</summary>
    internal IReadOnlyList<EditorScene> Scenes => [.. sceneEditing.Scenes.Select(document => sceneFiles[document])];

    /// <summary>The active scene's world settings.</summary>
    internal WorldSettings World => world0;

    /// <summary>Three of the four panels doc 20's B6 names; the Scenes panel is <c>SceneModule</c>'s (#1290).</summary>
    void WorldPanels() {
        Shell.RegisterPanel(
            WorldPanel,
            EditorStrings.PanelWorld,
            panel => {
                panel.WhenPressedIn(() => Shell.Context = WorldContext);

                Settings(panel, () => world0.Environment, "Environment");
                Settings(panel, () => world0.Physics, "Physics");
            }
        );

        Shell.RegisterPanel(
            LightingPanel,
            EditorStrings.PanelLighting,
            panel => {
                panel.WhenPressedIn(() => Shell.Context = WorldContext);

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
            EditorStrings.PanelNavigation,
            panel => {
                panel.WhenPressedIn(() => Shell.Context = WorldContext);

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
        if (sceneFiles.TryGetValue(scene, out var open)) {
            open.Settings = world0;
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
    ///         ⚠ <b>The read behind <see cref="ISceneEditing.OpenAdditively" /> (#1290)</b>, handed to the
    ///         service as <see cref="SceneEditing.Load" /> because refusing a file another document edits
    ///         is this class's arbitration. Scene ▸ Open Scene Additively is <c>SceneModule</c>'s.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Into the same world and its own <c>SceneHandle</c>.</b> That is what
    ///         <c>SceneManager</c> is for and it is what makes an entity handle mean one thing across
    ///         the editor — a second world would mean the outliner, the gizmo and the picker each
    ///         needing to know which world an entity came from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A scene already open is activated rather than loaded twice.</b> Two documents over
    ///         one file are two undo histories over one set of bytes, and whichever saved last wins —
    ///         which is <c>IDocumentHost.TryOpen</c>'s argument, restated for scenes.
    ///     </para>
    /// </remarks>
    internal SceneDocument? OpenSceneAdditively(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // ⚠ By the file each scene writes now, not the one it was opened from — after Save As the
        // main scene's recorded path names a file it no longer writes, so adding that file activated
        // the main scene and adding the one it moved to loaded a second document over it (#1416).
        if (Scenes.FirstOrDefault(open => SameFile(open.Writes, path)) is { } found) {
            sceneEditing.Activate(found.Document);

            return found.Document;
        }

        if (EditedElsewhere(path, scene: null) is { } holder) {
            RefuseSecondDocument(path, holder, "Could not open the scene");

            return null;
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

        sceneFiles[document] = new(document, path) { Settings = WorldSettings.Load(path) };
        sceneEditing.Open(document);

        sceneEditing.Restructured();

        Shell.Notifications.Success(Path.GetFileName(path) + " opened additively");
        return document;
    }

    /// <summary>Points the editor at the scene <see cref="ISceneEditing" /> made active.</summary>
    /// <remarks>
    ///     ⚠ <b>The one writer of <c>scene</c> after start-up (#1290).</b> The service owns which scene
    ///     is active and raises <see cref="ISceneEditing.ActiveChanged" />; what only the application can
    ///     do — the picker, the probe, the world settings, the document focus and the panes — happens
    ///     here, so a plugin activating a scene moves the editor exactly as a click in the Scenes panel
    ///     does.
    /// </remarks>
    void Followed() {
        var open = sceneFiles[sceneEditing.Active];

        SetActiveScene(open.Document, open.Path);
        world0 = open.Settings;

        project.Activate(open.Document);

        // Every panel that follows the active scene is pointed at it here rather than each of them
        // watching a signal, because "which scene is active" is this class's arbitration in exactly
        // the way "which selection the inspector follows" already is.
        if (scenePanes.Layout is { } layout) {
            layout.Document = open.Document;
        }

        sceneEditing.Restructured();
        Retitle();
    }

    /// <summary>Forgets the file of every scene the service no longer has open.</summary>
    /// <remarks>
    ///     ⚠ <b>Following <see cref="ISceneEditing.ScenesChanged" />, so a plugin's
    ///     <see cref="ISceneEditing.Close" /> and the Scenes panel's are one path (#1290).</b> The
    ///     service deletes the closed scene's entities and closes the document; the path and the world
    ///     settings beside it are what only this class held.
    /// </remarks>
    void ForgetClosedScenes() {
        foreach (var closed in sceneFiles.Keys.Where(document => !sceneEditing.Scenes.Contains(document)).ToList()) {
            sceneFiles.Remove(closed);
        }
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

    /// <summary>The verbs doc 20's B6 and its multi-scene row name.</summary>
    void WorldCommands() {
        CreateAssetCommands();

        Panel("scene.world-settings", EditorStrings.CommandSceneWorldSettings, WorldPanel, EditorStrings.CategoryScene);
        Panel("scene.lighting", EditorStrings.CommandSceneLighting, LightingPanel, EditorStrings.CategoryScene);
        Panel("scene.navigation", EditorStrings.CommandSceneNavigation, NavigationPanel, EditorStrings.CategoryScene);

        // ⚠ The Scenes panel, Open Scene Additively and Close Scene are `SceneModule`'s (#1290), over
        // `ISceneEditing`. Save All Scenes stays: it writes each scene's world settings beside it.
        Verb(
            "scene.save-all-scenes",
            EditorStrings.CommandSceneSaveAllScenes,
            EditorStrings.CategoryScene,
            SaveAllScenes,
            enabled: () => sceneEditing.Scenes.Any(document => document.HasUnsavedChanges.Value)
        );

        Planned("scene.layers", EditorStrings.CommandSceneLayers, EditorStrings.CategoryScene);
    }

    void SaveAllScenes() {
        var written = 0;

        foreach (var open in Scenes) {
            if (!open.Document.HasUnsavedChanges.Value) {
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
    }

    // ── Making one of the new assets ────────────────────────────────────────────────────────────

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
        // ⚠ The editor's own kinds are not added here any more: `LibraryModule` registers them through
        // `PluginContext` (doc 50 § D3, #1292), so they reach this menu by the door a plugin's do.

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
    void RefreshOverlays(Type kind) => scenePanes.Refreshed(kind);

    /// <summary>Puts a kind's command in the registry, if it is not already there.</summary>
    /// <remarks>
    ///     The making is <c>IContentLibrary.Create</c>'s; the opening is this editor's, because a
    ///     document is the Documents context's and needs an asset editor that claims the extension.
    /// </remarks>
    void CreateAssetCommand(NewAssetKind kind) {
        if (Shell.Commands[kind.Id] is not null) {
            return;
        }

        Verb(
            kind.Id,
            kind.Title,
            EditorStrings.CategoryAssets,
            () => {
                if (library.Create(kind) is { } created && kind.Opens) {
                    Open(created);
                }
            }
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
            // ⚠ By the contract and not by name (#1297): the VFX graph and the sequence were two
            // `case` patterns on the asset editors' types, which doc 36 § P3 counted as the two names
            // the compiler does not report when the reference goes.
            if (element is ITickingView ticking) {
                ticking.Tick(delta);
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

            var loaded = document.Load(Css, StyleOrigin.UserAgent);

            document.Load(Utilities, StyleOrigin.UserAgent);

            return loaded;
        }

        /// <summary>This assembly's utility rules, in <c>@layer utilities</c>.</summary>
        /// <remarks>
        ///     ⚠ <b>A sheet of its own rather than a share of the editor's, and that is shape C working
        ///     rather than a duplication of it.</b> What
        ///     <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c> shares is the <i>tokens</i>;
        ///     the scan and the output stay this project's, so the build stays incremental and this
        ///     assembly is not rebuilt because a panel somewhere else started using <c>gap-3</c>.
        ///     Everything here is inside <c>@layer utilities</c>, where document order decides nothing,
        ///     so a dozen assemblies loading a dozen of these behaves as one sheet.
        ///     <para>
        ///         ⚠ Loaded at the same origin as the sheet above, which is what keeps the ladder
        ///         meaningful: origin is the cascade's first question and the layer only its second, so
        ///         loading these as <c>Author</c> would stop them being ordered against the sheet at all
        ///         and start them beating a user's accessibility overrides.
        ///     </para>
        /// </remarks>
        public static string Utilities => VixenUtilityStyles.Utilities;

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