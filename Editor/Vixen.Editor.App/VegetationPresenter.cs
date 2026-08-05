// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App;

/// <summary>The grass modules a pane draws with, or default while the build has not produced them.</summary>
/// <param name="Draw">The two stages <c>Grass.rvn</c> compiles to.</param>
/// <param name="Scatter">The <c>GrassScatter</c> compute stage, layer-bound.</param>
/// <param name="ScatterUnbound">The <c>LayerBound = false</c> permutation, for a type with no layer.</param>
/// <param name="Arguments">The <c>Arguments = true</c> permutation that fills the indirect draws.</param>
/// <remarks>
///     ⚠ <b>Default is a viewport that draws no grass and is not broken</b> — <c>TerrainStages</c>'s
///     own rule, and for the same reason: the modules come from the shader library through
///     <c>./build.sh CheckShaders</c>, and a build that has never run it should still start.
/// </remarks>
readonly record struct GrassShaderSet(
    TerrainShaders Draw,
    ShaderHandle Scatter,
    ShaderHandle ScatterUnbound,
    ShaderHandle Arguments
) {
    /// <summary>Whether every module is here.</summary>
    public bool IsValid => Draw.IsValid && Scatter.IsValid && ScatterUnbound.IsValid && Arguments.IsValid;
}

/// <summary>Draws the painted vegetation — foliage instances and derived grass — into a pane's scene pass.</summary>
/// <remarks>
///     <para>
///         <b>The half of the terrain toolset the viewport was not showing.</b> The foliage brush
///         writes instances into a <see cref="FoliageVolume" /> and the grass types are rules over the
///         terrain's painted weights, and the presenter drew neither — so painting a forest was an act
///         of faith. This is the drawing half, and it deliberately takes two different routes:
///     </para>
///     <para>
///         ⚠ <b>Foliage goes through the CPU cull and the viewport's own instanced mesh pipeline.</b>
///         <c>FoliageCullPass</c> — the device cull — exists, but its output is indirect commands over
///         a survivors buffer, and no graphics shader anywhere reads that arrangement yet; the runtime
///         compositor's foliage draw is still owed. <see cref="FoliageRenderer" /> is the same
///         arithmetic on the CPU, its compacted transforms are exactly what
///         <see cref="MeshInstanceRenderer" /> uploads, and the painted volumes an editor session
///         holds are thousands of instances rather than the fifty thousand the dispatch exists for.
///         What that buys is the viewport's existing preview shading on the type's real mesh, resolved
///         through the same <see cref="IMeshSource" /> the scene's mesh components use.
///     </para>
///     <para>
///         ⚠ <b>Grass goes through the device scatter, because there is no honest CPU preview of a
///         hundred thousand blades.</b> <see cref="GrassDispatch" /> re-scatters the resident cells
///         from the terrain renderer's own atlas every frame and <see cref="GrassDrawPass" /> draws
///         the survivors indirectly — the same pair the runtime uses, so what the editor shows is what
///         the game will grow. The dispatches are recorded in the upload, outside the render pass,
///         which is where a compute dispatch is legal; only the draws land in the pass.
///     </para>
///     <para>
///         ⚠ <b>A grass lane is a preview of a rule the palette only remembers half of.</b>
///         [docs/plan/31 § D8]: grass is derived, and <c>GrassType.ToFoliageType</c> projects the
///         placement rules into the palette entry and drops the rest — so the lane rebuilds a
///         <see cref="GrassType" /> from the shared fields and takes the defaults for jitter and wind.
///         A preview a knob's turn away from the runtime is the honest best until the palette carries
///         the whole rule.
///     </para>
/// </remarks>
sealed class VegetationPresenter : IDisposable {
    /// <summary>How many cells one terrain's grass may hold resident, per pane.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixty-four rather than the dispatch's default two hundred and fifty-six, and the
    ///     difference is megabytes per pane.</b> A lane's instance ring is
    ///     <c>capacity × bladesPerCell × 48</c> bytes, and a four-pane layout multiplies whatever this
    ///     costs by four. Cells past it are refused loudly — <see cref="GrassDispatch.Refused" /> — and
    ///     a field that stops short in a preview is visible where a hidden allocation is not.
    /// </remarks>
    const int CellCapacity = 64;

    /// <summary>How many blades one cell's run holds — <c>TerrainGrassSettings</c>'s own default.</summary>
    const int BladesPerCell = 4096;

    /// <summary>What a foliage instance is tinted while the viewport has no materials.</summary>
    /// <remarks>
    ///     A muted green rather than <c>SceneMeshes.ShapeColour</c>'s grey, so a painted stand reads
    ///     as vegetation beside the block-out it grows around. The shading itself is the same
    ///     preview BRDF everything else in the pane gets.
    /// </remarks>
    static readonly Color4 PlantColour = new(0.42f, 0.52f, 0.36f, 1f);

    readonly IGraphicsDevice device;
    readonly RenderOutput output;

    /// <summary>The foliage instances, drawn by the same pipeline as the scene's shapes.</summary>
    /// <remarks>
    ///     ⚠ <b>A second <see cref="MeshInstanceRenderer" /> rather than a second range in the
    ///     presenter's</b>, for the reason the gizmo overlay is a second <c>LineRenderer</c>: one
    ///     renderer holds one instance ring rebuilt per frame from the scene's entities, and foliage
    ///     instances are not entities. Sized down — a painted stand is thousands of instances, not the
    ///     scene's sixteen thousand entities.
    /// </remarks>
    readonly MeshInstanceRenderer plants;

    /// <summary>The CPU cull — <c>FoliageCullPass</c>'s reference arithmetic, which needs no shader.</summary>
    readonly FoliageRenderer culler = new();

    /// <summary>Which device geometry each palette type's mesh was registered as.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed by the resolved reference rather than the palette index</b>, so two types naming
    ///     one mesh — a birch and its sapling — share the upload, and a palette reorder does not
    ///     re-register anything.
    /// </remarks>
    readonly Dictionary<AssetReference, MeshShapeGeometry> registered = [];

    readonly List<FoliageDraw> foliageDraws = [];
    readonly List<MeshInstance> instances = [];
    readonly List<MeshInstanceBatch> batches = [];
    readonly Dictionary<int, MeshShapeGeometry> shapes = [];

    /// <summary>One terrain's grass: the residency ring and a lane per derived type.</summary>
    readonly Dictionary<TerrainMap, GrassField> fields = [];

    /// <summary>The lanes this frame prepared, in the order their draws are recorded.</summary>
    readonly List<(GrassDrawPass Pass, GrassDispatch Dispatch)> grassDraws = [];

    readonly List<(int Index, GrassType Type)> grassTypes = [];
    readonly List<GrassSlot> resident = [];

    /// <summary>The wind's clock. A pane-local stopwatch is the presenter's only frame clock.</summary>
    readonly Stopwatch clock = Stopwatch.StartNew();

    MeshInstanceView view;

    /// <summary>What the current grass rules are, as a value the rebuild check compares.</summary>
    /// <remarks>
    ///     The layer <em>bindings</em> rather than the whole types: a rule whose density changed needs
    ///     new constants, which <c>Prepare</c> writes every frame anyway, but one whose binding
    ///     changed needs the other scatter permutation — a different pipeline, which only a rebuild
    ///     can swap in.
    /// </remarks>
    string signature = string.Empty;

    bool disposed;

    public VegetationPresenter(IGraphicsDevice device, MeshInstanceShaders shaders, RenderOutput output) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        this.output = output;

        // An eighth of the scene renderer's ring: a painted stand is thousands of instances. The
        // geometry buffers hold the palette's meshes — real assets rather than eight primitives, so
        // roomier per shape and far fewer shapes.
        plants = new(device, shaders, output, 1 << 13, 1 << 17, 1 << 18);
    }

    /// <summary>The grass modules, or default while the build has not produced them.</summary>
    public GrassShaderSet GrassStages { get; set; }

    /// <summary>How many foliage instances the last upload chose to draw.</summary>
    public int FoliageDrawn { get; private set; }

    /// <summary>How many instances were skipped because their type's mesh is not resident yet.</summary>
    /// <remarks>
    ///     <see cref="IMeshSource" />'s contract makes this the ordinary first answer: asking is what
    ///     starts the load, and the type draws on the frame the mesh lands.
    /// </remarks>
    public int FoliageWaiting { get; private set; }

    /// <summary>How many cells this frame's grass dispatches cover, across every terrain and type.</summary>
    public int GrassCells { get; private set; }

    /// <summary>How many draws the last <see cref="Record" /> issued.</summary>
    public int Draws { get; private set; }

    /// <summary>Culls the painted vegetation and stages what the device needs.</summary>
    /// <param name="commands">The frame's command list, open and outside a render pass.</param>
    /// <param name="scene">What answers "what is painted", or null while nothing supplies it.</param>
    /// <param name="meshes">Where a palette entry's mesh comes from, or null while nothing does.</param>
    /// <param name="terrains">This frame's terrains, for the grass to scatter over.</param>
    /// <param name="camera">The pane's camera.</param>
    /// <param name="aspect">Its aspect ratio.</param>
    /// <param name="height">The target's height in pixels, for the instanced pipeline's view.</param>
    /// <remarks>
    ///     ⚠ <b>The grass dispatches are recorded here, not in the pass.</b> A compute dispatch is
    ///     illegal inside a render pass, and the scatter has to have retired before the draw reads its
    ///     indirect arguments — <see cref="GrassDispatch.Record" /> carries the barriers, and running
    ///     it beside the terrain's own uploads puts it on the right side of the pass for free.
    /// </remarks>
    public void Upload(
        ICommandList commands,
        IVegetationScene? scene,
        IMeshSource? meshes,
        IReadOnlyList<(TerrainRenderer Renderer, Vector3 Origin)> terrains,
        EditorCamera camera,
        float aspect,
        int height
    ) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(disposed, this);

        grassDraws.Clear();
        instances.Clear();
        batches.Clear();

        FoliageDrawn = 0;
        FoliageWaiting = 0;
        GrassCells = 0;

        if (scene?.Foliage() is not { } volume) {
            // ⚠ Uploaded empty rather than left alone: the renderer draws what its last upload wrote,
            // and a module that went away would otherwise leave its forest standing in every pane.
            plants.Upload([], []);
            return;
        }

        var viewProjection = camera.ViewProjection(aspect);
        var frustum = new BoundingFrustum(viewProjection);

        view = new(
            viewProjection,
            camera.Position,
            camera.Forward,
            camera.NearPlane,
            camera.IsOrthographic,
            camera.PixelScale(height)
        );

        UploadFoliage(commands, scene, meshes, volume, in frustum, camera.Position);
        UploadGrass(commands, volume, terrains, camera, viewProjection, in frustum);
    }

    /// <summary>Records this frame's vegetation draws. Must be inside the scene's render pass.</summary>
    /// <returns>How many draws were issued.</returns>
    public int Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        plants.Record(commands, in view);
        Draws += plants.Draws;

        foreach (var (pass, dispatch) in grassDraws) {
            Draws += pass.Record(commands, dispatch);
        }

        return Draws;
    }

    /// <summary>The foliage half: resolve each stored type's mesh, cull, and fill the instance ring.</summary>
    void UploadFoliage(
        ICommandList commands,
        IVegetationScene scene,
        IMeshSource? meshes,
        FoliageVolume volume,
        in BoundingFrustum frustum,
        Vector3 position
    ) {
        foliageDraws.Clear();
        shapes.Clear();

        for (var index = 0; index < volume.Palette.Count; index++) {
            var type = volume.Palette[index];

            // Derived types are rules, not instances — they are the grass half's business. And a
            // stored type nobody has painted yet has nothing to cull.
            if (type.Storage != FoliageStorage.Stored || volume.CountOf(index) == 0) {
                continue;
            }

            if (Resolve(commands, scene, meshes, type.Mesh) is not { } shape) {
                FoliageWaiting += volume.CountOf(index);
                continue;
            }

            shapes[index] = shape;

            // One level and no distances: the viewport draws the mesh at every range the cull keeps.
            // The command template is unused — the CPU path's indirect commands are discarded, because
            // the instanced renderer draws from ranges rather than from argument buffers.
            foliageDraws.Add(new(index, [default], []));
        }

        if (foliageDraws.Count == 0) {
            plants.Upload([], []);
            plants.Flush(commands);
            return;
        }

        FoliageDrawn = culler.Cull(volume, foliageDraws, in frustum, position);

        // The culler compacts every surviving transform into one run and its batches name ranges of
        // it, so the instance list is a straight copy and the batch list a reslicing — the exact
        // shape `MeshInstanceRenderer.Upload` wants.
        foreach (var transform in culler.Transforms) {
            instances.Add(MeshInstance.Of(in transform, PlantColour));
        }

        foreach (var batch in culler.Batches) {
            if (shapes.TryGetValue(batch.Type, out var shape)) {
                batches.Add(new(shape, batch.First, batch.Count, Edges: false));
            }
        }

        plants.Upload(CollectionsMarshal.AsSpan(instances), CollectionsMarshal.AsSpan(batches));
        plants.Flush(commands);
    }

    /// <summary>A palette mesh, registered on the frame it first resolves.</summary>
    MeshShapeGeometry? Resolve(ICommandList commands, IVegetationScene scene, IMeshSource? meshes, string name) {
        if (meshes is null || string.IsNullOrEmpty(name)) {
            return null;
        }

        var reference = scene.MeshOf(name);

        if (reference.IsNull) {
            return null;
        }

        if (registered.TryGetValue(reference, out var shape)) {
            return shape;
        }

        // ⚠ Asking is what starts the load — `IMeshSource`'s contract — so a miss here is "next
        // frame", not "never". The staging reservation mirrors `ScenePresenter.Resolve`: grown before
        // the write, because it cannot be grown after one.
        if (!meshes.TryGet(reference, out var mesh)) {
            return null;
        }

        plants.Reserve(plants.StagingCost(mesh));

        if (!plants.TryRegister(mesh, out shape)) {
            return null;
        }

        registered[reference] = shape;

        return shape;
    }

    /// <summary>The grass half: bring each terrain's residency up to date and record the scatters.</summary>
    void UploadGrass(
        ICommandList commands,
        FoliageVolume volume,
        IReadOnlyList<(TerrainRenderer Renderer, Vector3 Origin)> terrains,
        EditorCamera camera,
        in Matrix4x4 viewProjection,
        in BoundingFrustum frustum
    ) {
        if (!GrassStages.IsValid || terrains.Count == 0) {
            return;
        }

        Reconstruct(volume);

        if (grassTypes.Count == 0) {
            return;
        }

        var range = 0f;

        foreach (var (_, type) in grassTypes) {
            range = MathF.Max(range, type.EndCullDistance);
        }

        var time = (float)clock.Elapsed.TotalSeconds;

        foreach (var (renderer, origin) in terrains) {
            var field = FieldOf(renderer.Terrain, volume);

            field.Residency.Update(camera.Position, range);

            resident.Clear();
            resident.AddRange(field.Residency.Resident);

            if (resident.Count == 0) {
                continue;
            }

            foreach (var lane in field.Lanes) {
                // A layer-bound type on a terrain that has no such layer grows nothing there — the
                // honest answer, since nothing is painted. The unbound lane covers the whole ground.
                var layer = LayerOf(renderer.Terrain, lane.Type.Layer);

                if (lane.Bound && layer < 0) {
                    continue;
                }

                lane.Dispatch.Prepare(
                    lane.Type,
                    volume.Grid,
                    resident,
                    renderer.GrassSource(layer, origin),
                    lane.Pass.MeshTemplate
                );

                // The blades land in world space — the scatter bakes the origin in — so the draw
                // takes the camera's own matrix rather than the terrain's placed one.
                lane.Pass.Prepare(commands, lane.Dispatch, new(viewProjection, camera.Position, frustum), lane.Type.Wind, time);
                lane.Dispatch.Record(commands);

                GrassCells += lane.Dispatch.CellCount;
                grassDraws.Add((lane.Pass, lane.Dispatch));
            }
        }
    }

    /// <summary>The grass rules the palette's derived entries describe, rebuilt for this frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Invalid rules are skipped rather than thrown for.</b> <c>GrassDispatch.Prepare</c>
    ///     refuses an unusable type loudly, and it is right to — but a palette is content somebody is
    ///     editing, and one half-filled entry must not take the pane down.
    /// </remarks>
    void Reconstruct(FoliageVolume volume) {
        grassTypes.Clear();

        for (var index = 0; index < volume.Palette.Count; index++) {
            var entry = volume.Palette[index];

            if (entry.Storage != FoliageStorage.Derived) {
                continue;
            }

            // The projection `GrassType.ToFoliageType` makes, inverted where it can be. Jitter and
            // wind did not survive the trip, so the preview takes a new rule's defaults for both.
            var type = GrassType.Of(entry.Name) with {
                Mesh = entry.Mesh,
                Albedo = entry.Albedo,
                Layer = entry.LayerFilter,
                MinWeight = entry.LayerThreshold,
                MaxWeight = MathF.Max(entry.LayerThreshold, 1f),
                Density = entry.Density,
                MinScale = entry.MinScale,
                MaxScale = entry.MaxScale,
                RandomYaw = entry.RandomYaw,
                AlignToNormal = entry.AlignToNormal,
                MaxSlope = entry.MaxSlope,
                StartCullDistance = entry.StartCullDistance,
                EndCullDistance = entry.EndCullDistance
            };

            if (type.Validate() is null) {
                grassTypes.Add((index, type));
            }
        }

        var parts = new System.Text.StringBuilder();

        foreach (var (index, type) in grassTypes) {
            parts.Append(index).Append(':').Append(type.Layer.Length == 0 ? '·' : '#').Append('|');
        }

        signature = parts.ToString();
    }

    /// <summary>One terrain's field, rebuilt when the palette's grass rules changed.</summary>
    /// <remarks>
    ///     ⚠ <b>The device is idled before lanes are torn down</b>, for the reason
    ///     <c>ScenePresenter.Resolve</c> idles before releasing shapes: a lane's buffers may be what a
    ///     frame in flight is drawing from. A palette edit is a panel action rather than a frame, so
    ///     the stall costs nothing anybody can see.
    /// </remarks>
    GrassField FieldOf(TerrainMap terrain, FoliageVolume volume) {
        if (fields.TryGetValue(terrain, out var field)) {
            if (field.Signature == signature && field.Grid.Equals(volume.Grid)) {
                return field;
            }

            device.WaitIdle();
            field.Dispose();
            fields.Remove(terrain);
        }

        field = new(volume.Grid, signature);

        foreach (var (index, type) in grassTypes) {
            var bound = !string.IsNullOrEmpty(type.Layer);

            field.Lanes.Add(
                new(
                    index,
                    type,
                    bound,
                    new(
                        device,
                        bound ? GrassStages.Scatter : GrassStages.ScatterUnbound,
                        GrassStages.Arguments,
                        CellCapacity,
                        BladesPerCell
                    ),
                    new(device, GrassStages.Draw, output)
                )
            );
        }

        fields[terrain] = field;

        return field;
    }

    static int LayerOf(TerrainMap terrain, string name) {
        if (string.IsNullOrEmpty(name)) {
            return -1;
        }

        var weights = terrain.Weights;

        for (var index = 0; index < weights.LayerCount; index++) {
            if (string.Equals(weights.LayerOf(index).Name, name, StringComparison.OrdinalIgnoreCase)) {
                return index;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var field in fields.Values) {
            field.Dispose();
        }

        fields.Clear();
        plants.Dispose();
    }

    /// <summary>One derived type's device pair on one terrain.</summary>
    /// <param name="Index">Which palette entry.</param>
    /// <param name="Type">The reconstructed rule.</param>
    /// <param name="Bound">Whether the rule names a layer, which picked the scatter permutation.</param>
    /// <param name="Dispatch">The scatter.</param>
    /// <param name="Pass">The draw.</param>
    sealed record GrassLane(int Index, GrassType Type, bool Bound, GrassDispatch Dispatch, GrassDrawPass Pass);

    /// <summary>One terrain's residency ring and its lanes.</summary>
    sealed class GrassField(FoliageCellGrid grid, string signature) : IDisposable {
        public FoliageCellGrid Grid { get; } = grid;

        /// <summary>Which rules the lanes were built from, for the rebuild check.</summary>
        public string Signature { get; } = signature;

        /// <summary>Which cells hold blades. One ring per terrain, shared by every lane.</summary>
        public GrassResidency Residency { get; } = new(grid, CellCapacity);

        public List<GrassLane> Lanes { get; } = [];

        public void Dispose() {
            foreach (var lane in Lanes) {
                lane.Dispatch.Dispose();
                lane.Pass.Dispose();
            }

            Lanes.Clear();
        }
    }
}
