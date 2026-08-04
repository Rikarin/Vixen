// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     The compositor node that draws the world's terrains: the surface, and each terrain's grass.
/// </summary>
/// <remarks>
///     <para>
///         <b>What <c>!Terrain</c> builds to — the runtime reachability the whole stack was
///         missing.</b> <see cref="TerrainRenderer" />, <see cref="GrassDispatch" /> and
///         <see cref="GrassDrawPass" /> are complete and tested and had no caller outside the
///         editor; this is the caller. It reads the frame's terrains from a
///         <see cref="TerrainSceneSource" /> the extraction system fills, owns one draw set per
///         heightfield, and declares two passes: an attachment-less pass for the uploads and the
///         scatter dispatch — a render graph pass with no attachments runs outside a render pass,
///         which is where a transfer and a compute dispatch are legal — and a raster pass that
///         loads the frame's colour and depth and draws.
///     </para>
///     <para>
///         ⚠ <b>The depth is the scene's, loaded and written.</b> Terrain is opaque geometry that
///         happens to be drawn after the Main pass, so it must occlude and be occluded — reverse-Z,
///         <c>CompareFunction.Greater</c>, which is <c>DepthStencilState.Default</c> and what every
///         pipeline in this stack already builds. A read-only depth here would draw the ground over
///         every wall that should hide it.
///     </para>
///     <para>
///         ⚠ <b>Preview-grade shading, stated rather than discovered.</b> <c>Terrain.rvn</c> lights
///         with its own hard-coded sun and the default permutation covers four weight-blended
///         layers; frame-lit shading, shadow casting and motion vectors are tracked work. What this
///         node settles is placement and reachability, which is the part a document can say.
///     </para>
///     <para>
///         ⚠ <b>A heightfield placed twice draws once.</b> The renderer's constants are per frame
///         and per placement, so a second entity naming the same terrain would overwrite the
///         first's placement before either draws. The editor shares the atlas and accepts the same
///         limit; <see cref="SharedPlacements" /> is where the second placement is counted rather
///         than silently lost.
///     </para>
/// </remarks>
public sealed class TerrainSceneRenderer : SceneRenderer, IDisposable {
    /// <summary>The cell grid every grass field scatters over, in metres.</summary>
    /// <remarks>The editor's foliage volume uses the same 32 m cell, and the two must agree the day
    ///     painted foliage and derived grass share a residency.</remarks>
    public const float GrassCellSize = 32f;

    /// <summary>How far grass cells stay resident when neither component nor document says.</summary>
    public const float DefaultGrassRange = 160f;

    readonly Dictionary<TerrainMap, TerrainDrawSet?> sets = [];
    readonly List<(TerrainDrawSet Set, TerrainSceneEntry Entry)> drawn = [];
    readonly HashSet<TerrainMap> placed = [];
    readonly Stopwatch clock = Stopwatch.StartNew();

    TerrainShaders terrainShaders;
    bool disposed;

    /// <summary>The colour target the ground draws into.</summary>
    public required string Output { get; init; }

    /// <summary>The depth target it tests and writes.</summary>
    public required string Depth { get; init; }

    /// <summary>The view whose camera places, culls and streams the ground.</summary>
    public RenderView? View { get; set; }

    /// <summary>Where the frame's terrains come from, or null while nothing supplies them.</summary>
    /// <remarks>
    ///     The stable object between this node — rebuilt per document load — and the extraction
    ///     system, registered once. Null draws nothing quietly, which is a document opened before
    ///     its host finished wiring.
    /// </remarks>
    public TerrainSceneSource? Scene { get; set; }

    /// <summary>The resolved vegetation numbers this node runs at.</summary>
    public TerrainVegetationQuality Vegetation { get; set; } = new();

    /// <summary>Whether grass fields scatter and draw at all.</summary>
    public bool Grass { get; set; } = true;

    /// <summary>The device, or null for a node that declines to draw.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>How many terrains the last frame drew.</summary>
    public int TerrainsDrawn { get; private set; }

    /// <summary>How many grass fields the last frame dispatched.</summary>
    public int GrassFieldsDrawn { get; private set; }

    /// <summary>How many placements were dropped because their heightfield was already placed.</summary>
    public int SharedPlacements { get; private set; }

    /// <summary>How many terrains the renderer refused — a shape problem, not a frame problem.</summary>
    public int RefusedTerrains { get; private set; }

    /// <summary>How many grass fields named a weight layer their terrain does not have.</summary>
    public int GrassLayersMissing { get; private set; }

    /// <summary>Whether the last build was still waiting for a shader to resolve.</summary>
    /// <remarks>
    ///     True for the first frames of a development run while the compiler works, and for ever on
    ///     a shipped build whose bundle is missing the terrain variants — which is the state this
    ///     property exists to make legible, because both look like ground that is not there.
    /// </remarks>
    public bool WaitingForShaders { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        // The names first and loudly, before any early-out: a document that names a target nothing
        // declared is wrong however empty the world is, and a refusal that only fired once a
        // terrain existed would land a long way from the typo.
        var colour = frame.Texture(ToString(), Output);
        var colourFormat = frame.FormatOf(ToString(), Output);
        var depth = frame.Texture(ToString(), Depth);
        var depthFormat = frame.FormatOf(ToString(), Depth);

        TerrainsDrawn = 0;
        GrassFieldsDrawn = 0;
        SharedPlacements = 0;
        WaitingForShaders = false;

        // A node built without a device declines to draw — an editor opening a document before it
        // has a window, and CompositorBuilder's own contract for every post node.
        if (Device is null || Modules is null || View is not { } view || Scene is not { } scene) {
            return;
        }

        if (scene.Terrains.Count == 0) {
            // No terrain in the world is the ordinary case for most projects, and it costs nothing.
            return;
        }

        if (!ResolveTerrainShaders(frame)) {
            WaitingForShaders = true;
            return;
        }

        var output = new RenderOutput([colourFormat], depthFormat);

        drawn.Clear();
        placed.Clear();

        foreach (var entry in scene.Terrains) {
            if (!placed.Add(entry.Terrain)) {
                SharedPlacements++;
                continue;
            }

            if (SetFor(entry, output, frame) is not { } set) {
                continue;
            }

            drawn.Add((set, entry));
        }

        if (drawn.Count == 0) {
            return;
        }

        var time = (float)clock.Elapsed.TotalSeconds;

        // The uploads and the scatter, outside any render pass: buffer writes, texture copies and
        // the two compute dispatches are what a Vulkan render pass refuses. SideEffect because the
        // graph cannot see this pass's edges — the terrain's atlas and the grass ring are the
        // node's own resources, not the graph's.
        frame.Graph.AddPass(
            $"{this}.Upload",
            pass => {
                pass.SideEffect();

                pass.Execute(
                    context => {
                        foreach (var (set, entry) in drawn) {
                            Upload(context.CommandList, set, entry, view, time);
                        }
                    }
                );
            }
        );

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // Loaded, never cleared: the Main pass's picture is what the ground joins. The
                // depth is read-write on purpose — see the class remarks.
                pass.ColourAttachment(colour, LoadAction.Load, default);
                pass.DepthAttachment(depth, LoadAction.Load, 0f);

                pass.Execute(
                    context => {
                        foreach (var (set, _) in drawn) {
                            set.Surface.Record(context.CommandList);
                            TerrainsDrawn++;

                            if (set.Field is { } field) {
                                field.Draw.Record(context.CommandList, field.Dispatch);
                                GrassFieldsDrawn++;
                            }
                        }
                    }
                );
            }
        );
    }

    /// <summary>Stages one terrain's frame: the surface, then its grass field.</summary>
    void Upload(ICommandList commands, TerrainDrawSet set, in TerrainSceneEntry entry, RenderView view, float time) {
        // The placement rides the view matrix, because the shader has no field for one — the
        // editor's UploadTerrain states the whole argument. The frustum comes from the combined
        // matrix so culling happens in the terrain's own space.
        var placedMatrix = Matrix4x4.FromTranslation(entry.Origin) * view.ViewProjection;
        var terrainView = new TerrainView(placedMatrix, view.Position - entry.Origin, new(placedMatrix));

        // Re-assigned per frame rather than at construction, because the source appears when
        // content mounts — which may be after this node first built the set.
        set.Surface.Textures = Scene?.Textures;
        set.Surface.Upload(commands, terrainView);

        if (set.Field is not { } field) {
            return;
        }

        var range = entry.GrassRange > 0f ? entry.GrassRange : DefaultGrassRange;

        field.Residency.Update(view.Position, range);

        field.Resident.Clear();
        field.Resident.AddRange(field.Residency.Resident);

        var scale = MathF.Max(Vegetation.GrassCullDistanceScale, 0f);
        var source = set.Surface.GrassSource(field.Layer, entry.Origin);

        field.Dispatch.Prepare(
            field.Type,
            new(GrassCellSize),
            field.Resident,
            in source,
            field.Draw.MeshTemplate,
            Math.Clamp(Vegetation.GrassDensityScale, 0f, 1f)
        );

        // The blades are scattered in world space, so the grass draws with the camera's own view
        // rather than the terrain's origin-shifted one.
        var worldView = new TerrainView(view.ViewProjection, view.Position, new(view.ViewProjection));

        field.Draw.Prepare(
            commands,
            field.Dispatch,
            in worldView,
            field.Type.Wind,
            time,
            fade: new(field.Type.StartCullDistance * scale, field.Type.EndCullDistance * scale)
        );

        field.Dispatch.Record(commands);
    }

    /// <summary>The draw set for one terrain, made on the frame its heightfield first appears.</summary>
    TerrainDrawSet? SetFor(in TerrainSceneEntry entry, in RenderOutput output, CompositorFrame frame) {
        var nearRange = entry.NearRange > 0f ? entry.NearRange : Vegetation.TerrainNearRange;

        if (sets.TryGetValue(entry.Terrain, out var existing)) {
            if (existing is null) {
                // Refused once is refused always — the refusal was about the terrain's shape, and
                // retrying per frame would re-throw sixty times a second.
                return null;
            }

            if (Math.Abs(existing.NearRange - nearRange) > float.Epsilon) {
                // A LOD range is baked into the quadtree at construction, so a changed range is a
                // new renderer. Rare — a scene edit — and the atlas re-upload it costs is the same
                // one the first frame paid.
                existing.Dispose();
                sets.Remove(entry.Terrain);
            } else {
                SyncGrass(existing, entry, output, frame);
                return existing;
            }
        }

        TerrainDrawSet set;

        try {
            var surface = new TerrainRenderer(
                Device!,
                entry.Terrain,
                terrainShaders,
                output,
                Vixen.Terrain.TerrainLodRanges.Default with { NearRange = nearRange }
            );

            var description = entry.Terrain.Description;

            // A streamer only changes the first frame of a large terrain — a small one fits the
            // atlas by construction and the machinery would be a decision with one outcome. Sixteen
            // tiles is TerrainRenderer's own line for "fits by construction". Attached before the
            // first Upload, which is the streamer's stated requirement.
            if (description.TilesX * description.TilesZ > 16) {
                surface.Streaming = new(description, new TerrainTileSource(entry.Terrain));
            }

            set = new(surface, nearRange);
        } catch (ArgumentException) {
            // A terrain the renderer refuses is one shape of asset, not the whole frame — the
            // editor's viewport makes the same trade.
            sets[entry.Terrain] = null;
            RefusedTerrains++;

            return null;
        }

        sets[entry.Terrain] = set;
        SyncGrass(set, entry, output, frame);

        return set;
    }

    /// <summary>Brings a set's grass field into line with what the entry says this frame.</summary>
    void SyncGrass(TerrainDrawSet set, in TerrainSceneEntry entry, in RenderOutput output, CompositorFrame frame) {
        if (!Grass || entry.Grass is not { } type) {
            set.Field?.Dispose();
            set.Field = null;

            return;
        }

        if (set.Field is { } existing) {
            if (existing.Type == type) {
                return;
            }

            // The rule changed — grass is derived, so the whole field re-grows. The ring's buffers
            // are sized by quality rather than by the type, so a rebuild is the descriptor work
            // only.
            existing.Dispose();
            set.Field = null;
        }

        var layer = LayerOf(entry.Terrain, type.Layer);

        if (type.NeedsSurfaceWeight && layer < 0) {
            // A rule bound to a layer the terrain does not have grows nothing wherever it is
            // asked, so the field is not built — and the counter is where a person finds out.
            GrassLayersMissing++;

            return;
        }

        if (GrassShaders(frame, type) is not { } shaders) {
            WaitingForShaders = true;

            return;
        }

        var dispatch = new GrassDispatch(
            Device!,
            shaders.Scatter,
            shaders.Arguments,
            Math.Max(1, Vegetation.GrassResidentCells),
            Math.Max(1, Vegetation.GrassBladesPerCell)
        );

        var draw = new GrassDrawPass(Device!, shaders.Draw, output);

        set.Field = new(
            type,
            layer,
            new(new(GrassCellSize), Math.Max(1, Vegetation.GrassResidentCells)),
            dispatch,
            draw
        );
    }

    /// <summary>The weight layer a name refers to, or negative for none.</summary>
    static int LayerOf(TerrainMap terrain, string? name) {
        if (name is not { Length: > 0 }) {
            return -1;
        }

        for (var layer = 0; layer < terrain.Weights.LayerCount; layer++) {
            if (string.Equals(terrain.Weights.LayerOf(layer).Name, name, StringComparison.Ordinal)) {
                return layer;
            }
        }

        return -1;
    }

    /// <summary>Resolves the surface's two stages, once, through the frame's effect system.</summary>
    /// <remarks>
    ///     The default permutation — four layer slots, no height blend — which is the same
    ///     preview-grade variant the editor embeds. A terrain whose splat wants more compiles the
    ///     same shader at other values, and routing that through here is part of the frame-lit
    ///     shading task.
    /// </remarks>
    bool ResolveTerrainShaders(CompositorFrame frame) {
        if (terrainShaders.IsValid) {
            return true;
        }

        if (frame.Effects.Resolve(EffectKey.Of("Terrain")) is not { } effect) {
            return false;
        }

        terrainShaders = new(
            Modules!.ModuleOf(effect, ShaderStage.Vertex),
            Modules.ModuleOf(effect, ShaderStage.Fragment)
        );

        return terrainShaders.IsValid;
    }

    /// <summary>Resolves one grass rule's three shaders and the output its pipeline draws into.</summary>
    /// <remarks>
    ///     Two compute variants — the scatter and its <c>Arguments</c> phase — keyed by the rule's
    ///     own flags, because <c>LayerBound</c> and <c>RandomYaw</c> compile the sampling in or
    ///     out. The draw is one variant for every field.
    /// </remarks>
    GrassFieldShaders? GrassShaders(CompositorFrame frame, in GrassType type) {
        var bound = type.NeedsSurfaceWeight ? "true" : "false";
        var yaw = type.RandomYaw ? "true" : "false";

        var scatterKey = EffectKey.Of(
            "GrassScatter",
            [
                KeyValuePair.Create("Arguments", "false"),
                KeyValuePair.Create("LayerBound", bound),
                KeyValuePair.Create("RandomYaw", yaw)
            ]
        );

        var argumentKey = EffectKey.Of(
            "GrassScatter",
            [
                KeyValuePair.Create("Arguments", "true"),
                KeyValuePair.Create("LayerBound", bound),
                KeyValuePair.Create("RandomYaw", yaw)
            ]
        );

        if (frame.Effects.Resolve(scatterKey) is not { } scatterEffect
            || frame.Effects.Resolve(argumentKey) is not { } argumentEffect
            || frame.Effects.Resolve(EffectKey.Of("Grass")) is not { } drawEffect) {
            return null;
        }

        var scatter = Modules!.ModuleOf(scatterEffect, ShaderStage.Compute);
        var arguments = Modules.ModuleOf(argumentEffect, ShaderStage.Compute);

        var draw = new TerrainShaders(
            Modules.ModuleOf(drawEffect, ShaderStage.Vertex),
            Modules.ModuleOf(drawEffect, ShaderStage.Fragment)
        );

        if (!scatter.IsValid || !arguments.IsValid || !draw.IsValid) {
            return null;
        }

        return new(scatter, arguments, draw);
    }

    readonly record struct GrassFieldShaders(ShaderHandle Scatter, ShaderHandle Arguments, TerrainShaders Draw);

    /// <summary>Everything one heightfield draws with, kept across frames.</summary>
    /// <remarks>
    ///     Per terrain rather than per frame because a <see cref="TerrainRenderer" /> owns an atlas
    ///     — the editor's <c>ScenePresenter</c> states the cost of rebuilding one.
    /// </remarks>
    sealed class TerrainDrawSet : IDisposable {
        public TerrainDrawSet(TerrainRenderer surface, float nearRange) {
            Surface = surface;
            NearRange = nearRange;
        }

        public TerrainRenderer Surface { get; }

        public float NearRange { get; }

        public GrassField? Field { get; set; }

        public void Dispose() {
            Field?.Dispose();
            Surface.Streaming?.Dispose();
            Surface.Dispose();
        }
    }

    /// <summary>One grass rule over one terrain: the ring, the dispatch and the draw.</summary>
    sealed class GrassField : IDisposable {
        public GrassField(GrassType type, int layer, GrassResidency residency, GrassDispatch dispatch, GrassDrawPass draw) {
            Type = type;
            Layer = layer;
            Residency = residency;
            Dispatch = dispatch;
            Draw = draw;
        }

        public GrassType Type { get; }

        public int Layer { get; }

        public GrassResidency Residency { get; }

        public GrassDispatch Dispatch { get; }

        public GrassDrawPass Draw { get; }

        /// <summary>This frame's resident cells, materialised once for the dispatch.</summary>
        public List<GrassSlot> Resident { get; } = [];

        public void Dispose() {
            Draw.Dispose();
            Dispatch.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in sets.Values) {
            set?.Dispose();
        }

        sets.Clear();
        drawn.Clear();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "Terrain" : Name;
}
