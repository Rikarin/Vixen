// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Foliage;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>What binds the terrain and foliage tools to the Scene in front of them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The hole every one of these tools was falling through.</b> <c>TerrainMode</c> sculpts
///         whatever <c>TerrainEdit.Terrain</c> holds and <c>FoliageMode</c> paints whatever
///         <c>FoliageEdit.Volume</c> holds — and both are properties the mode's own remarks say "the
///         application sets". Nothing set either. The consequence was not subtle: creating a terrain
///         worked once, per session, and reopening the Scene it was saved into left every tool in the
///         mode greyed out with "no terrain" beside them. That reads exactly as a half-finished
///         feature, because from the outside it is indistinguishable from one.
///     </para>
///     <para>
///         ⚠ <b>The selection first, then the only one in the Scene.</b> Requiring the terrain entity
///         to be selected before the brush works is technically defensible and is a trap: a person
///         clicks the ground to aim, which selects nothing, and the tool they were holding turns off.
///         A Scene with exactly one terrain has no ambiguity to resolve, so it is bound whatever is
///         selected; a Scene with several needs the selection, and says so in the panel.
///     </para>
///     <para>
///         ⚠ <b>Written back on save rather than on every stroke.</b> A <c>.vxterrain</c> for a
///         2 km² World is tens of megabytes, and a brush drag is dozens of strokes a second. What
///         makes that safe is that the Scene and the terrain are saved together — see
///         <see cref="SaveTerrains" /> — so there is no state in which the Scene names a heightfield
///         that does not have the edits the Scene was saved with.
///     </para>
/// </remarks>
public sealed partial class TerrainModule {
    /// <summary>The terrains this session has read, by the reference the Scene named.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed by reference rather than by entity.</b> Two entities naming one
    ///     <c>.vxterrain</c> are two placements of one heightfield, and reading it twice would let a
    ///     stroke on one be silently discarded by a save from the other.
    /// </remarks>
    readonly Dictionary<string, TerrainMap> terrains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which of them have edits that are not on disk.</summary>
    readonly HashSet<string> terrainEdits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which references failed to read, so the failure is reported once rather than per frame.</summary>
    readonly HashSet<string> terrainRefusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the tools are pointed at right now.</summary>
    string boundTerrain = string.Empty;

    /// <summary>And the entity it was reached through, for its transform.</summary>
    Entity boundEntity = Entity.Null;

    /// <summary>How many terrains the Scene has, which is what the panel says when it is not one.</summary>
    internal int TerrainsInScene { get; private set; }

    /// <summary>Brings the terrain and foliage tools into line with the Scene, once a frame.</summary>
    internal void FollowTerrainSelection() {
        var (entity, reference) = Bound();

        TerrainsInScene = CountTerrains();

        BindColliders();

        // ⚠ Bound whether or not there is a terrain, because foliage paints onto *any* surface —
        // which is `TerrainMode`'s own reason for the two modes being two. A volume that only
        // appeared once a heightfield existed would make the tree brush depend on a feature it has
        // nothing to do with.
        // ⚠ The null case is the whole point, and the obvious guard misses it: both are null on the
        // first frame, so a plain reference comparison decides they already agree and the volume is
        // never made. The mode then has none for the whole session, which is exactly the state this
        // file exists to end.
        if (volume is null || !ReferenceEquals(foliage.Editing.Volume, volume)) {
            foliage.Editing.Volume = Volume();
            RefreshPalette();
        }

        if (reference.Length == 0) {
            Unbind();
            return;
        }

        if (!string.Equals(reference, boundTerrain, StringComparison.OrdinalIgnoreCase)) {
            if (Load(reference) is not { } map) {
                Unbind();
                return;
            }

            boundTerrain = reference;
            terrain.Editing.Terrain = map;
            terrain.Editing.Layer = map.Layers.Count > 0 ? map.Layers[^1] : null;

            RefreshTerrainLayers();
            RefreshTerrainFacts();
        }

        boundEntity = entity;

        // ⚠ Re-read every frame rather than at bind time, because moving the terrain entity with the
        // gizmo moves where the brush thinks the ground is. A stale origin puts every stroke at an
        // offset from the pointer — which reads as the brush being inaccurate rather than as the
        // transform not having been noticed.
        var origin = entity != Entity.Null && Scene.World.IsAlive(entity) && Scene.World.Has<LocalTransform>(entity)
            ? Scene.World.Read<LocalTransform>(entity).Position
            : Vector3.Zero;

        terrain.Origin = origin;

        // The growth simulation and the foliage brush both need ground. A surface rebuilt per frame
        // would rebuild its layer-name cache per frame; one is kept until the terrain or the origin
        // changes.
        if (foliage.Editing.Surface is not TerrainSurface surface
            || !ReferenceEquals(surface.Terrain, terrain.Editing.Terrain)
            || surface.Origin != origin) {
            foliage.Editing.Surface = terrain.Editing.Terrain is { } ground ? new TerrainSurface(ground, origin) : null;
        }
    }

    /// <summary>Points the sculpt tools at whatever rebuilds collision, if the host has one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The thing that was missing, and it was not the adapter.</b>
    ///         <see cref="ITerrainColliders" /> is called by <c>TerrainEdit.Commit</c> after every
    ///         stroke that moved a height, and until this line nothing in the tree ever assigned
    ///         <c>TerrainEdit.Colliders</c> outside a test — so the seam was fed by a double in the
    ///         suite and by nothing at all in the product. Writing an implementation without writing
    ///         this would have been the same defect one layer in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked for rather than required, which is <c>PluginServices.TryGet</c>'s own
    ///         stated case.</b> A scene being sculpted before anybody has pressed play has no physics
    ///         world, and <see cref="ITerrainColliders" />' remarks make null "a terrain with no
    ///         collision, not an error". A module that refused a host without one would be a toolset
    ///         that cannot be used until the game runs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And that is why this module still references no physics.</b> The contract is
    ///         declared here and the implementation is <c>Vixen.Editor.Terrain.Physics</c>' —
    ///         a third assembly, because the layer rules forbid <c>Core/Vixen.Terrain.Physics</c>
    ///         from referencing <c>Editor/</c> and this toolset has no business linking Jolt.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Once, and then never again.</b> Re-reading every frame would overwrite a
    ///         <c>Colliders</c> a host or a test set directly, which is the assignment
    ///         <c>TerrainMode.Editing</c> being public exists to allow.
    ///     </para>
    /// </remarks>
    void BindColliders() {
        if (terrain.Editing.Colliders is null && services.TryGet<ITerrainColliders>(out var colliders)) {
            terrain.Editing.Colliders = colliders;
        }
    }

    /// <summary>Which terrain the tools should be pointed at, and through which entity.</summary>
    (Entity Entity, string Reference) Bound() {
        foreach (var selected in Scene.Selection) {
            // ⚠ Liveness first, and `World.Has` throws rather than answering false for a destroyed
            // entity. A selection outlives the thing it names for exactly one frame after an undo
            // that removed it — the selection is a list of handles and nothing prunes it — so a
            // membership test that assumed the handle was good took the editor down on Ctrl-Z.
            if (!Scene.World.IsAlive(selected)) {
                continue;
            }

            if (Scene.World.Has<TerrainComponent>(selected)
                && Scene.World.Read<TerrainComponent>(selected).Terrain is { Length: > 0 } named) {
                return (selected, named);
            }
        }

        // Nothing selected carries one. A Scene with exactly one terrain has no ambiguity, so the
        // brush keeps working while somebody clicks around in the viewport.
        var only = Entity.Null;
        var reference = string.Empty;
        var found = 0;

        foreach (var (entity, component) in Terrains()) {
            found++;

            if (found > 1) {
                return (Entity.Null, string.Empty);
            }

            only = entity;
            reference = component.Terrain ?? string.Empty;
        }

        return (only, reference);
    }

    /// <summary>Every entity in the Scene that names a terrain.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TerrainComponent</c> holds a string, so it lives in the World's managed store and
    ///     <c>chunk.Values&lt;T&gt;()</c> throws for it.</b> Reading one entity at a time is what the
    ///     store supports, and a forest of terrains is a handful of entities rather than a hot loop.
    ///
    ///     ⚠ <b>Collected into a list rather than yielded, and the compiler is right to insist.</b>
    ///     <c>chunk.Entities</c> is a <see cref="ReadOnlySpan{T}" /> over the chunk's storage, which
    ///     cannot cross a <c>yield</c> — and the reason it cannot is the reason this would be a bug if
    ///     it could: the caller may add an entity between two iterations, and the span would then be
    ///     over memory the World has moved.
    /// </remarks>
    List<(Entity Entity, TerrainComponent Component)> Terrains() {
        var found = new List<(Entity, TerrainComponent)>();
        var query = new QueryDescription().WithAll<TerrainComponent>();

        foreach (var chunk in Scene.World.Query(query).Chunks()) {
            foreach (var entity in chunk.Entities) {
                found.Add((entity, Scene.World.Read<TerrainComponent>(entity)));
            }
        }

        return found;
    }

    int CountTerrains() => Terrains().Count;

    /// <summary>Reads a <c>.vxterrain</c>, or reports why it could not be.</summary>
    TerrainMap? Load(string reference) {
        if (terrains.TryGetValue(reference, out var cached)) {
            return cached;
        }

        if (terrainRefusals.Contains(reference)) {
            return null;
        }

        try {
            var path = Path.Combine(Project.Paths.Assets, reference.Replace('/', Path.DirectorySeparatorChar));
            var map = TerrainStore.Read(File.ReadAllBytes(path));

            map.Resolve();
            terrains[reference] = map;

            return map;
        } catch (Exception failure) when (failure is IOException or ArgumentException or UnauthorizedAccessException) {
            // ⚠ Remembered, so the notification is one rather than sixty a second. A missing terrain
            // is a Scene referring to a file somebody deleted, which is a state to report and go on
            // from rather than to re-report every frame.
            terrainRefusals.Add(reference);

            Shell.Notifications.Show(
                "The terrain could not be read",
                NotificationSeverity.Error,
                $"{reference} — {failure.Message}"
            );

            return null;
        }
    }

    void Unbind() {
        if (boundTerrain.Length == 0) {
            return;
        }

        boundTerrain = string.Empty;
        boundEntity = Entity.Null;

        terrain.Editing.Terrain = null;
        terrain.Editing.Layer = null;
        foliage.Editing.Surface = null;

        RefreshTerrainLayers();
    }

    /// <summary>What the viewport draws the ground from.</summary>
    /// <remarks>
    ///     Built on first use rather than in a field initialiser, because it takes the application
    ///     and <c>this</c> is not available in one.
    /// </remarks>
    internal ITerrainScene TerrainScene => terrainScene ??= new(this);

    SceneTerrains? terrainScene;

    /// <summary>The application, seen as the thing that answers "what ground is in this Scene".</summary>
    /// <remarks>
    ///     ⚠ <b>A nested type rather than <see cref="TerrainModule" /> implementing the interface
    ///     itself.</b> The module is already several partials wide; adding a presenter-facing
    ///     interface to it would put a member called <c>Terrains</c> on the class that already has a
    ///     private <c>Terrains()</c> meaning something different — the entity walk rather than the
    ///     draw list.
    /// </remarks>
    sealed class SceneTerrains(TerrainModule editor) : ITerrainScene {
        readonly List<(TerrainMap, Vector3)> drawn = [];

        /// <inheritdoc />
        public IReadOnlyList<(TerrainMap Terrain, Vector3 Origin)> Terrains() {
            drawn.Clear();

            foreach (var (entity, component) in editor.Terrains()) {
                if (component.Terrain is not { Length: > 0 } reference || editor.Load(reference) is not { } map) {
                    continue;
                }

                var origin = editor.Scene.World.IsAlive(entity) && editor.Scene.World.Has<LocalTransform>(entity)
                    ? editor.Scene.World.Read<LocalTransform>(entity).Position
                    : Vector3.Zero;

                drawn.Add((map, origin));
            }

            return drawn;
        }
    }

    /// <summary>What the viewport draws the painted foliage from.</summary>
    /// <remarks>Built on first use, for <see cref="TerrainScene" />'s reason.</remarks>
    internal IVegetationScene VegetationScene => vegetationScene ??= new(this);

    SceneVegetation? vegetationScene;

    /// <summary>The session, seen as the thing that answers "what is painted in this Scene".</summary>
    /// <remarks>
    ///     A nested type for <see cref="SceneTerrains" />'s reason, and thin for the same one: the
    ///     volume it hands out is the very object the brush writes into, so a stroke shows in the
    ///     viewport on the frame it lands with nothing copied and nothing notified.
    /// </remarks>
    sealed class SceneVegetation(TerrainModule editor) : IVegetationScene {
        /// <inheritdoc />
        public FoliageVolume Foliage() => editor.Volume();

        /// <inheritdoc />
        public AssetReference MeshOf(string reference) => editor.ResolveAsset(reference);
    }

    /// <summary>Says a terrain has edits that the file does not.</summary>
    /// <remarks>
    ///     Raised from <c>TerrainMode.Committed</c>, which is every stroke and every layer verb — the
    ///     complete set of things that change a heightfield through the editor.
    /// </remarks>
    void TerrainEdited() {
        if (boundTerrain.Length > 0) {
            terrainEdits.Add(boundTerrain);
        }
    }

    /// <summary>Writes back every terrain this session has edited.</summary>
    /// <returns>How many files were written.</returns>
    /// <remarks>
    ///     ⚠ <b>Called from the Scene's save, so the two cannot disagree.</b> A Scene saved without
    ///     its heightfields names a terrain whose edits are only in memory, and the person who finds
    ///     that out is the one who reopens the file tomorrow.
    /// </remarks>
    internal int SaveTerrains() {
        var written = 0;

        foreach (var reference in terrainEdits.ToArray()) {
            if (!terrains.TryGetValue(reference, out var map)) {
                terrainEdits.Remove(reference);
                continue;
            }

            try {
                var path = Path.Combine(Project.Paths.Assets, reference.Replace('/', Path.DirectorySeparatorChar));

                map.Resolve();
                File.WriteAllBytes(path, TerrainStore.Write(map));

                terrainEdits.Remove(reference);
                written++;
            } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
                Shell.Notifications.Show(
                    "The terrain could not be saved",
                    NotificationSeverity.Error,
                    $"{reference} — {failure.Message}"
                );
            }
        }

        return written;
    }

    /// <summary>Takes a terrain the create form just built into the session, without re-reading it.</summary>
    /// <remarks>
    ///     ⚠ <b>Otherwise the next frame reads the file back and replaces the object the mode is
    ///     already holding.</b> Everything would still work — the bytes are the same — right up until
    ///     somebody sculpts between the create and the first save, at which point the stroke is on an
    ///     object nothing points at any more.
    /// </remarks>
    void Adopt(string reference, TerrainMap map) {
        terrains[reference] = map;
        terrainRefusals.Remove(reference);

        boundTerrain = reference;
        terrain.Editing.Terrain = map;
    }

    // ── The foliage volume ──────────────────────────────────────────────────────────────────────

    /// <summary>The volume the Scene's foliage is painted into, made on demand.</summary>
    /// <remarks>
    ///     ⚠ <b>One per Scene and stored beside it, which is [docs/plan/31 § T5c]'s own phrase.</b> A
    ///     volume is millions of transforms; a <c>.vxscene</c> is the file two people touch every day.
    ///     What the Scene carries is the palette — names and asset references, which a review has to
    ///     be able to read — and what sits beside it is the instances.
    /// </remarks>
    FoliageVolume? volume;

    /// <summary>Whether the volume has instances the file beside the Scene does not.</summary>
    bool volumeEdited;

    /// <summary>Where a Scene's foliage instances live.</summary>
    /// <param name="scenePath">The Scene's own path. Empty for a Scene that has never been saved.</param>
    /// <returns>The path, or empty when there is nowhere to put it yet.</returns>
    internal static string FoliagePathOf(string scenePath) =>
        string.IsNullOrEmpty(scenePath) ? string.Empty : Path.ChangeExtension(scenePath, ".vxfol");

    /// <summary>The volume, reading what is beside the Scene the first time it is asked for.</summary>
    internal FoliageVolume Volume() {
        if (volume is { } existing) {
            return existing;
        }

        volume = new FoliageVolume(new(32f));

        var path = FoliagePathOf(ScenePath);

        if (path.Length > 0 && File.Exists(path)) {
            try {
                FoliageStore.Read(volume, File.ReadAllBytes(path));
            } catch (Exception failure) when (failure is IOException or ArgumentException) {
                Shell.Notifications.Show(
                    "The Scene's foliage could not be read",
                    NotificationSeverity.Error,
                    failure.Message
                );
            }
        }

        return volume;
    }

    /// <summary>Writes the Scene's instances beside it.</summary>
    /// <returns>Whether anything was written.</returns>
    /// <remarks>
    ///     ⚠ <b>A volume with nothing stored in it deletes the file rather than writing an empty
    ///     one.</b> Every type in the palette may be derived — [§ D8]: grass is a rule, and nothing
    ///     about a derived type is in any file — so "no bytes" is the normal steady state, and a
    ///     zero-instance <c>.vxfol</c> beside every Scene is noise in every diff for ever.
    /// </remarks>
    internal bool SaveFoliage() {
        if (volume is not { } painted || !volumeEdited) {
            return false;
        }

        var path = FoliagePathOf(ScenePath);

        if (path.Length == 0) {
            return false;
        }

        try {
            if (!FoliageStore.Persisted(painted).Any()) {
                if (File.Exists(path)) {
                    File.Delete(path);
                }

                volumeEdited = false;

                return true;
            }

            var bytes = new byte[FoliageStore.ByteCount(painted)];

            FoliageStore.Write(painted, bytes);
            File.WriteAllBytes(path, bytes);

            volumeEdited = false;

            return true;
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("The foliage could not be saved", NotificationSeverity.Error, failure.Message);

            return false;
        }
    }

    /// <summary>Says the volume has instances the file beside the Scene does not.</summary>
    void FoliageEditedHere() => volumeEdited = true;

    // ── Reading the four content assets back out of the Project ─────────────────────────────────

    /// <summary>Reads a Project asset as one of doc 31's four YAML types.</summary>
    /// <typeparam name="T">Which type.</typeparam>
    /// <param name="relative">Its path under <c>Assets</c>.</param>
    /// <param name="value">What it said.</param>
    /// <returns>Whether it could be read.</returns>
    /// <remarks>
    ///     ⚠ <b>The source text rather than the built artefact, and that is deliberate for a
    ///     toolset.</b> <c>TerrainAssetImporter</c> writes the same bytes into the content build, so
    ///     reading the build would be one more thing that has to be up to date before a brush works —
    ///     and the file an author has just edited is the one they mean.
    /// </remarks>
    bool ReadAsset<T>(string relative, out T value) {
        value = default!;

        try {
            var path = Path.Combine(Project.Paths.Assets, relative.Replace('/', Path.DirectorySeparatorChar));

            if (YamlReader.Read(File.ReadAllText(path)) is not YamlMapping root) {
                return false;
            }

            value = YamlSerializer.Deserialize<T>(root);

            return true;
        } catch (Exception failure) when (failure is IOException or YamlParseException or ArgumentException
                                              or InvalidOperationException or FormatException) {
            Shell.Notifications.Show($"{relative} could not be read", NotificationSeverity.Warning, failure.Message);

            return false;
        }
    }

    /// <summary>Every asset in the Project whose path ends one of these ways, under <c>Assets</c>.</summary>
    IEnumerable<string> AssetsOfKind(params string[] extensions) {
        foreach (var entry in Project.Assets.Entries) {
            if (entry.IsFolder) {
                continue;
            }

            foreach (var extension in extensions) {
                if (entry.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) {
                    yield return Relative(entry.Path);

                    break;
                }
            }
        }
    }

    /// <summary>An asset's path with the <c>Assets</c> prefix taken off, which is what a reference is.</summary>
    static string Relative(string path) {
        var normalised = path.Replace('\\', '/');

        return normalised.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? normalised[7..] : normalised;
    }

    /// <summary>What the Project browser has selected, as paths.</summary>
    IEnumerable<string> SelectedAssets() {
        foreach (var guid in Project.Selection) {
            if (Project.Assets.TryGetByGuid(guid, out var entry) && !entry.IsFolder) {
                yield return Relative(entry.Path);
            }
        }
    }

    /// <summary>Adds whatever foliage or grass assets the Project browser has selected.</summary>
    /// <returns>How many were added.</returns>
    /// <remarks>
    ///     ⚠ <b>From the browser's selection rather than a file dialog.</b> A palette entry is an
    ///     asset in this Project, and the panel that already lists them is three inches away — a
    ///     native picker would open on the file system, where the same file is reachable and its
    ///     GUID is not.
    /// </remarks>
    internal int AddSelectedToPalette() {
        var added = 0;

        foreach (var path in SelectedAssets().ToArray()) {
            if (path.EndsWith(".vxfoliage", StringComparison.OrdinalIgnoreCase)) {
                if (ReadAsset<FoliageType>(path, out var type)) {
                    AddToPalette(type);
                    added++;
                }
            } else if (path.EndsWith(".vxgrass", StringComparison.OrdinalIgnoreCase)) {
                if (ReadAsset<GrassType>(path, out var grassType)) {
                    // ⚠ Through the grass type's own conversion rather than by copying fields. Grass
                    // is derived and foliage is stored — [§ D8] — and a palette entry that said
                    // "stored" for a grass rule would put a million blades into the .vxfol beside the
                    // Scene, which is precisely the file FoliageStore refuses to write.
                    AddToPalette(grassType.ToFoliageType());
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>Every spline in the Project, built.</summary>
    /// <remarks>
    ///     ⚠ <b>The Project rather than the Scene, which is a simplification and is named as one.</b>
    ///     A road is a <c>.vxspline</c> asset and nothing in a Scene points at one yet — there is no
    ///     spline component and no curve editor in the viewport — so "the roads on this terrain" is
    ///     "every road in the Project". A Project with two terrains and roads for both would lay all
    ///     of them onto whichever terrain is selected, which is wrong and is visible immediately,
    ///     where the alternative — <see cref="Roads" /> answering nothing — is a button that silently
    ///     does nothing.
    /// </remarks>
    internal List<(string Name, Spline Curve)> ProjectRoads() {
        var roads = new List<(string, Spline)>();

        foreach (var path in AssetsOfKind(".vxspline")) {
            if (!ReadAsset<SplineAsset>(path, out var asset) || !asset.CanBuild) {
                continue;
            }

            try {
                roads.Add((asset.Name.Length > 0 ? asset.Name : Path.GetFileNameWithoutExtension(path), asset.Build()));
            } catch (ArgumentException) {
                // A spline that cannot be built is reported by the importer beside the file; a
                // regeneration that threw would take the rest of the roads with it.
            }
        }

        return roads;
    }

    /// <summary>Turns a mesh name in a spline or a foliage type into a reference.</summary>
    /// <remarks>
    ///     ⚠ <b>The seam <c>TerrainSplineSpawner.Spawn</c> takes as a parameter, filled in.</b> It
    ///     accepts a path under <c>Assets</c> as well as a <c>vx:</c> reference, because a name typed
    ///     into a YAML file by hand is a path far more often than it is a GUID.
    /// </remarks>
    internal AssetReference ResolveAsset(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return AssetReference.Null;
        }

        if (AssetReference.TryParse(name, out var parsed)) {
            return parsed;
        }

        var normalised = name.Replace('\\', '/');
        var candidate = normalised.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? normalised
            : "Assets/" + normalised;

        return Project.Assets.TryGetByPath(candidate, out var entry) ? new AssetReference(entry.Guid) : AssetReference.Null;
    }

    /// <summary>Adds a <c>.vxfoliage</c> or <c>.vxgrass</c> to the Scene's palette.</summary>
    /// <param name="type">What the asset said.</param>
    /// <returns>Its index in the palette.</returns>
    internal int AddToPalette(FoliageType type) {
        var painted = Volume();
        var index = painted.AddType(type);

        foliage.Editing.Volume = null;
        foliage.Editing.Volume = painted;
        foliage.Editing.Choose(index);

        RefreshPalette();

        return index;
    }
}
