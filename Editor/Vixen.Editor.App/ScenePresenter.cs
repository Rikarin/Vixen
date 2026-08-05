// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using Vixen.Ui.Renderer;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App;

/// <summary>Renders the scene into a texture and hands the texture to the interface.</summary>
/// <remarks>
///     <para>
///         <b>The last link, and the one that makes the viewport a viewport.</b> The draw list can
///         carry a texture and <c>Viewport</c> draws one; this is what puts something in it. The scene
///         goes into an offscreen colour target, the target is registered with the UI renderer under
///         a number, and the viewport control carries that number — so the scene arrives in the
///         interface as an ordinary element that other elements can be drawn over.
///     </para>
///     <para>
///         ⚠ <b>Four draws into one target, in an order that is not arbitrary.</b> The shapes go
///         first and are the only thing here that writes depth; the grid and the entity markers
///         follow, depth-tested so that a marker behind a cube is behind it; then the gizmo's shafts,
///         rings and plane quads with no depth test at all, because a handle you cannot reach through
///         the thing it moves is a handle you cannot use; and last the gizmo's solid handles — the
///         arm heads and the box in the middle — which are triangles rather than segments and so
///         cannot share the buffer in front of them. The last two are what a solid mesh pass made
///         necessary: with only lines in the target there was nothing to be occluded by, which is why
///         the overlay used to share the world list's pipeline.
///     </para>
///     <para>
///         ⚠ <b>The shapes come out of device-resident buffers and the overlay does not.</b>
///         <see cref="MeshInstanceRenderer" /> holds each shape once and draws it once per entity from
///         a transform, which is what <c>docs/plan/24-blockout-tools.md</c> § B1 asked for and what makes the
///         viewport's cost linear in entities rather than in vertices. The gizmo's heads stay on
///         <see cref="MeshRenderer" />, because they are geometry that is genuinely rebuilt every
///         frame — a handle is a different size and a different colour at every camera position, and
///         there is one of them.
///     </para>
///     <para>
///         ⚠ <b>What is still missing is materials, not geometry.</b> There is one key direction, one
///         ambient term and a colour per instance here; a per-face material, a texture or the blockout
///         checker needs the viewport driven by <c>RenderSystem</c> through a
///         <c>GraphicsCompositor</c>, which is Phase 7's material-system wiring.
///     </para>
///     <para>
///         ⚠ <b>The target is recreated when the viewport resizes, and the registration is redone
///         with it.</b> A number registered against a destroyed view is a descriptor set pointing at
///         freed memory, and the frame that draws it is undefined behaviour rather than an error —
///         see <c>UiRenderer.RegisterImage</c>. Unregistering first is what keeps that impossible.
///     </para>
/// </remarks>
sealed class ScenePresenter : IDisposable {
    /// <summary>What the interface calls this presenter's texture.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Constant across resizes.</b> The viewport control holds it and the draw list
    ///         carries it; changing it when the target is recreated would mean a frame drawn with a
    ///         number nothing has registered, which is a viewport that blinks empty on every splitter
    ///         drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per presenter rather than a constant, which is what a four-pane layout needed.</b>
    ///         One number for every pane means every pane's control names the same texture, so all
    ///         four show whichever presenter registered last — four identical views of the
    ///         perspective camera, which reads as the other three cameras not working. The host hands
    ///         out the numbers, because it is the thing that knows how many panes there are.
    ///     </para>
    /// </remarks>
    public ulong Image { get; }

    readonly IGraphicsDevice device;

    /// <summary>The depth-tested lines: the grid, the markers, the parent lines.</summary>
    readonly LineRenderer lines;

    /// <summary>The gizmo, in a renderer of its own so it can be drawn without the depth test.</summary>
    /// <remarks>
    ///     ⚠ <b>A second instance rather than a second range in the first one.</b> One
    ///     <see cref="LineRenderer" /> holds one buffer and draws all of it with one pipeline, so
    ///     splitting the frame's lines across its two pipelines needs either a range on
    ///     <c>Record</c> — public API in a shipping assembly, for a distinction only this file makes
    ///     — or two of them. Two costs a second buffer of a few hundred vertices.
    /// </remarks>
    readonly LineRenderer overlay;

    /// <summary>The scene's shapes: one copy of each on the device, one instance per entity.</summary>
    readonly MeshInstanceRenderer meshes;

    /// <summary>Which device geometry each shape kind was registered as.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed by kind and rebuilt when the collector's <c>Revision</c> moves.</b> A shape is
    ///     uploaded on the frame the first entity wanting it appears, and the only thing that changes
    ///     what a kind's geometry *is* is <c>SceneMeshes.Segments</c> — which is what the revision
    ///     counts. Without that check a sphere rebuilt at a new tessellation would keep being drawn at
    ///     the old one, because the buffers are the only place the vertices exist now.
    /// </remarks>
    readonly Dictionary<SceneShape, MeshShapeGeometry> registered = [];

    /// <summary>The batches of <see cref="surfaces" />, resolved against <see cref="registered" />.</summary>
    readonly List<MeshInstanceBatch> draws = [];

    /// <summary>Which edited meshes this frame drew, so the revisions it did not can be given back.</summary>
    readonly HashSet<SceneShape> drawn = [];

    /// <summary>Registrations waiting out the frames that could still be reading them.</summary>
    readonly List<(MeshShapeGeometry Geometry, int Frames)> retiring = [];

    /// <summary>The gizmo's solid heads, in a renderer of their own so they escape the depth test.</summary>
    /// <remarks>
    ///     ⚠ <b>A second instance for the same reason <see cref="overlay" /> is one</b>, and the need
    ///     is sharper here: a wire handle behind a cube still shows a few pixels through it, and a
    ///     solid one is simply gone. One <see cref="MeshRenderer" /> holds one buffer and draws all of
    ///     it with one pipeline, so the world's shapes and the gizmo's heads cannot share it.
    /// </remarks>
    readonly MeshRenderer handles;

    readonly SceneLines geometry = new();
    readonly SceneMeshes surfaces = new();
    readonly List<LineVertex> pending = [];

    /// <summary>One renderer per terrain the pane has drawn, kept across frames.</summary>
    /// <remarks>
    ///     ⚠ <b>Per terrain rather than per pane-frame, because a <c>TerrainRenderer</c> owns an
    ///     atlas.</b> Building one per frame would upload the whole heightfield every frame and
    ///     allocate a texture per frame to do it — and keying it by the terrain object rather than by
    ///     the entity is what makes two entities placing one heightfield share the upload.
    /// </remarks>
    readonly Dictionary<TerrainMap, TerrainRenderer> terrains = [];

    /// <summary>What this frame chose to draw, in the order it will be recorded.</summary>
    /// <remarks>
    ///     With each renderer's placement, because the vegetation needs it: a grass scatter samples
    ///     the terrain's maps in local space and puts its blades back in world space, so "which
    ///     terrain" and "where it stands" travel together.
    /// </remarks>
    readonly List<(TerrainRenderer Renderer, Vector3 Origin)> terrainDraws = [];

    /// <summary>The painted foliage and grass, drawn into the same pass as the ground they stand on.</summary>
    readonly VegetationPresenter vegetation;

    /// <summary>The scene's water, drawn over the ground it covers.</summary>
    /// <remarks>
    ///     ⚠ <b>A third presenter rather than a range in this one</b>, for
    ///     <see cref="vegetation" />'s reason and one more: the water fold is stateful across frames —
    ///     a zone's field is re-rasterised only when it has to be — and that state belongs to
    ///     something with a lifetime rather than to a frame's collect.
    /// </remarks>
    readonly WaterPresenter water;

    TextureHandle colour;
    TextureViewHandle colourView;
    TextureHandle depth;
    TextureViewHandle depthView;
    Int2 size;

    /// <summary>Which of the collector's shape revisions <see cref="registered" /> was built from.</summary>
    int revision;

    bool disposed;

    /// <summary>How wide the target is, in render pixels.</summary>
    public int Width => size.X;

    /// <summary>How tall it is.</summary>
    public int Height => size.Y;

    /// <summary>Whether there is a target to draw into.</summary>
    public bool IsReady => colourView.IsValid;

    /// <summary>Builds the pipelines the scene is drawn with.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two line stages.</param>
    /// <param name="meshShaders">The two mesh stages, for the gizmo's solid handles.</param>
    /// <param name="instanceShaders">The two instanced stages, for the scene's shapes.</param>
    /// <param name="format">What the target's colour format is.</param>
    /// <param name="image">What the interface calls this presenter's texture. Unique per pane.</param>
    public ScenePresenter(
        IGraphicsDevice device,
        LineShaders shaders,
        MeshShaders meshShaders,
        MeshInstanceShaders instanceShaders,
        PixelFormat format,
        ulong image = 1
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfZero(image);

        this.device = device;
        Format = format;
        Image = image;

        var output = new RenderOutput([format], DepthFormat);

        lines = new(device, shaders, output);
        overlay = new(device, shaders, output);

        // The instance ring is what a frame costs and the geometry is what a scene costs. Sixteen
        // thousand entities at a hundred and sixty bytes is two and a half megabytes a region; the
        // shape buffers are sized for the eight primitives with room for the edited meshes
        // `docs/plan/24-blockout-tools.md` is about, and are written once rather than per frame.
        meshes = new(device, instanceShaders, output);

        // A few hundred vertices at most: three heads of a dozen segments each. Sized down from the
        // default so a second mesh ring costs kilobytes rather than megabytes.
        handles = new(device, meshShaders, output, 4096, 8192);

        // The painted vegetation, drawn with the same instanced stages and into the same output as
        // the scene's shapes — which is what puts a tree behind a wall behind the wall.
        vegetation = new(device, instanceShaders, output);

        // The water, on the gizmo heads' pipeline rather than the instanced one: a surface is
        // geometry genuinely rebuilt every frame — the waves move — which is what `MeshRenderer` is
        // for and what `MeshInstanceRenderer` deliberately is not.
        water = new(device, meshShaders, output);
    }

    /// <summary>What the shapes in the scene are drawn as.</summary>
    public SceneMeshes Surfaces => surfaces;

    /// <summary>The two stages a terrain draws with, or default while there are none.</summary>
    /// <remarks>
    ///     ⚠ <b>Default is a viewport that draws no terrain and is not broken.</b> The modules come
    ///     from <c>Shaders/Terrain.*.spv</c>, which <c>./build.sh CheckShaders</c> produces from the
    ///     library — a build that has not run it has no terrain module to load, and a presenter that
    ///     threw would take the whole viewport with it over a feature the scene may not use.
    /// </remarks>
    public TerrainShaders TerrainStages { get; set; }

    /// <summary>Where the terrains in the scene come from, or null while nothing supplies them.</summary>
    /// <remarks>
    ///     ⚠ <b>A seam rather than a walk over the world, for <see cref="Surfaces" />'s reason.</b>
    ///     A <c>TerrainComponent</c> names an asset and turning that name into a heightfield is the
    ///     application's — it owns the asset database and the read-back cache. What a presenter can
    ///     do is draw what it is handed.
    /// </remarks>
    public ITerrainScene? TerrainScene { get; set; }

    /// <summary>Where the painted foliage comes from, or null while nothing supplies it.</summary>
    /// <remarks>
    ///     <see cref="TerrainScene" />'s sibling seam, filled the same way: the terrain module
    ///     contributes an implementation and the host hands it over. Null is a viewport with no
    ///     vegetation in it and nothing else different.
    /// </remarks>
    public IVegetationScene? VegetationScene { get; set; }

    /// <summary>The grass modules, or default while <c>CheckShaders</c> has not produced them.</summary>
    /// <remarks><see cref="TerrainStages" />'s rule: absent draws no grass and breaks nothing.</remarks>
    internal GrassShaderSet GrassStages {
        get => vegetation.GrassStages;
        set => vegetation.GrassStages = value;
    }

    /// <summary>The vegetation half, for the tests that assert what a frame recorded.</summary>
    internal VegetationPresenter Vegetation => vegetation;

    /// <summary>Where the scene's splines, sea states and ground come from, or null while nothing supplies them.</summary>
    /// <remarks>
    ///     <see cref="VegetationScene" />'s seam exactly, filled the same way: the water module
    ///     contributes an implementation and the host hands it over. Null is a viewport with no water
    ///     in it and nothing else different.
    /// </remarks>
    public IWaterScene? WaterScene {
        get => water.Scene;
        set => water.Scene = value;
    }

    /// <summary>The water half, for the panel that shows the fold's counts and the tests that assert them.</summary>
    internal WaterPresenter Water => water;

    /// <summary>How many terrains the last <see cref="Upload" /> chose to draw.</summary>
    public int TerrainsDrawn => terrainDraws.Count;

    /// <summary>The colour format the target and the pipeline agree on.</summary>
    public PixelFormat Format { get; }

    /// <summary>The depth format, which is the engine's reversed-Z one.</summary>
    public const PixelFormat DepthFormat = PixelFormat.Depth32Float;

    /// <summary>Makes the target match the viewport, and re-registers it if it changed.</summary>
    /// <param name="viewport">The pane.</param>
    /// <param name="renderer">The interface's renderer, which holds the registration.</param>
    /// <returns>Whether there is a target to draw into.</returns>
    public bool Resize(SceneViewport viewport, UiRenderer renderer) {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(renderer);
        ObjectDisposedException.ThrowIf(disposed, this);

        var wanted = new Int2(viewport.Control.RenderWidth, viewport.Control.RenderHeight);

        if (wanted.X <= 0 || wanted.Y <= 0) {
            // A collapsed dock panel, a hidden tab, the frame before the first layout. A zero-sized
            // texture is what a device refuses to make, so there is nothing to do but wait.
            return IsReady;
        }

        if (wanted == size && IsReady) {
            return true;
        }

        // ⚠ Released and re-registered, not unregistered and registered afresh. Unregistering
        // destroys the number's descriptor sets, and this runs once a frame for as long as a splitter
        // is being dragged — a set per resize is a leak the backend cannot reclaim, because its pools
        // are deliberately created without `FreeDescriptorSetBit`. Re-registration keeps the sets and
        // repoints them, and it is safe across the destroy below because both are deferred to the
        // frame that owns them: the view outlives every frame in flight, and each frame's set is
        // rewritten before that frame binds it.
        Release();

        size = wanted;

        colour = device.CreateTexture(
            new(Format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "scene colour")
        );

        colourView = device.CreateTextureView(colour);

        depth = device.CreateTexture(
            new(DepthFormat, size.X, size.Y, TextureUsage.DepthStencilTarget, Name: "scene depth")
        );

        depthView = device.CreateTextureView(depth);

        renderer.RegisterImage(Image, colourView);
        viewport.Control.RenderTarget = Image;

        // The answers in flight are about a picture that no longer exists: the target was recreated
        // at a new size, so a pick resolved against the old one would name whatever id happens to sit
        // at those pixels now.
        viewport.Picking?.Abandon();

        return true;
    }

    /// <summary>Collects the frame's geometry and writes it.</summary>
    /// <param name="commands">
    ///     The frame's command list, open and outside a render pass, for the copies a newly registered
    ///     shape needs.
    /// </param>
    /// <param name="document">The scene.</param>
    /// <param name="viewport">The pane.</param>
    /// <remarks>
    ///     ⚠ <b>Outside the render pass.</b> This writes buffers, and a Vulkan command list may not
    ///     transfer inside one — the same reason the glyph atlas is uploaded before the interface's
    ///     pass rather than in it. The shape geometry is device-local, so its writes are a staged copy
    ///     recorded here rather than a map, which is the same rule with one more step.
    /// </remarks>
    public void Upload(ICommandList commands, SceneDocument document, SceneViewport viewport) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewport);

        // ⚠ The ambient term is the view mode's and the shaded default is the renderer's. Unlit,
        // albedo and normal are modes about a surface's own value rather than about its shape, and a
        // lambert term over any of the three is the picture the mode exists to take apart.
        meshes.Ambient = ViewShading.AmbientFor(viewport.Modes.Current, Ambient);

        // ⚠ Doc 24's D5 and P5 meeting: the block-out checker's squares are the work plane's step, so
        // "the grid I can see", "the grid I snap to" and "the squares on the surfaces I am snapping"
        // are one number. A plane that has not been given a step draws its adaptive spacing, and the
        // checker follows that too — which is what keeps the squares countable at every zoom.
        surfaces.Checker = viewport.Grid.Plane.Effective(viewport.Grid.Spacing(viewport.Camera, size.Y));

        // ⚠ Before the shapes, and outside the render pass like everything else here: a terrain's
        // upload is a buffer write and a texture copy, and Vulkan refuses a transfer inside a pass.
        var ratio = size.Y <= 0 ? 1f : (float) size.X / size.Y;

        UploadTerrain(commands, viewport.Camera, ratio);

        // ⚠ After the terrain and before the shapes, because the water fold asks the terrain seam how
        // high the ground is — and because a lake is drawn *over* the ground it covers, which only
        // works if the ground is already in the target when the blend happens. The world is the
        // document's rather than a copy: the fold reads `WorldTransform`, which the application has
        // resolved by the time a frame is uploaded.
        water.Upload(document.World, viewport.Camera, ratio);

        surfaces.Build(document, viewport);

        // ⚠ Resolved after the collect and before the upload, because this is where a shape first seen
        // this frame gets put on the device. The batches name kinds; the renderer needs slices.
        Resolve(commands);

        meshes.Upload(surfaces.Instances, CollectionsMarshal.AsSpan(draws));

        geometry.Build(document, viewport, size.Y);

        // ⚠ The water's diagnostics join the depth-tested list rather than the overlay's, because a
        // flow arrow behind a hill is behind the hill — these describe places in the world, not
        // handles you have to be able to reach through it.
        Write(lines, geometry.World, water.DebugLines);
        Write(overlay, geometry.Overlay);

        // ⚠ The gizmo's key light follows the camera, and this is the half of it that reaches the
        // GPU. `GizmoGeometry` shades the arm shafts on the CPU from the same call and the same
        // ambient; a frame that set one and not the other draws a cone lit from the side its own arm
        // is dark on, or a ball a shade lighter than the cones beside it. See `GizmoGeometry.KeyLight`
        // for why a handle is not lit by a direction in the world, and its `Ambient` for why a handle
        // is shaded harder than a shape in the scene.
        var key = GizmoGeometry.KeyLight(viewport.Camera);

        handles.LightDirection = key;
        handles.Ambient = GizmoGeometry.Ambient;

        // Straight across, no copy: `SceneLines` hands the gizmo's solid parts back as spans for
        // exactly this, which the two segment lists cannot do — see its own remarks.
        handles.Upload(geometry.Handles, geometry.HandleIndices);

        var stats = viewport.Stats;

        stats.Entities = surfaces.Count;

        // ⚠ The renderer's counts rather than the collector's, and the difference is the truncation. A
        // frame that produced more entities than the instance ring holds drops whole batches, and a
        // stats overlay whose triangle count silently stops rising is the one readout that would hide
        // the overflow it exists to make visible.
        // ⚠ The water's are added rather than left out, and the reason is the same one the note above
        // gives about truncation: the surface is the largest single thing a pane can draw, and a
        // readout that ignored it would say "0 tris" over a lake. `MeshRenderer.Dropped` is what says
        // the ring overflowed; this is what says how much it was asked for.
        stats.Triangles = meshes.Triangles + water.Triangles;
        stats.Segments = ((geometry.World.Count + geometry.Overlay.Count) / 2) + meshes.Segments;
    }

    /// <summary>Chooses this frame's terrain patches and stages what the device needs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The placement rides the view matrix rather than a per-terrain world transform,
    ///         because the shader has no field for one.</b> <c>TerrainConstants</c> carries the sample
    ///         grid and the height range and nothing about where the terrain is; a terrain's samples
    ///         <em>are</em> its space — <c>TerrainComponent</c>'s own constraint. Pre-multiplying the
    ///         camera's matrix by the entity's translation puts it where the entity is without giving
    ///         the shader a concept it deliberately does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The frustum is built from the <em>combined</em> matrix, so culling happens in the
    ///         terrain's own space.</b> Selecting against the camera's world frustum while the patches
    ///         are placed in sample space would cull the wrong nodes for any terrain not at the
    ///         origin — visible as ground that disappears when you look at it from one side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A terrain the frame did not see keeps its renderer.</b> Disposing one because a
    ///         camera turned away would destroy the atlas and re-upload the whole heightfield when it
    ///         turned back; they are released together in <see cref="Dispose" />, which is the frame
    ///         boundary a device resource can safely be freed on.
    ///     </para>
    /// </remarks>
    internal void UploadTerrain(ICommandList commands, EditorCamera camera, float aspect) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(camera);

        terrainDraws.Clear();

        if (TerrainStages.IsValid && TerrainScene is { } scene) {
            var viewProjection = camera.ViewProjection(aspect);

            foreach (var (terrain, origin) in scene.Terrains()) {
                if (!terrains.TryGetValue(terrain, out var renderer)) {
                    try {
                        renderer = new(
                            device,
                            terrain,
                            TerrainStages,
                            new([Format], DepthFormat),
                            TerrainLodRanges.Default
                        );
                    } catch (ArgumentException) {
                        // A terrain the renderer refuses is one shape of asset, not the whole viewport.
                        continue;
                    }

                    terrains[terrain] = renderer;
                }

                var placed = Matrix4x4.FromTranslation(origin) * viewProjection;

                renderer.Upload(commands, new(placed, camera.Position - origin, new(placed)));
                terrainDraws.Add((renderer, origin));
            }
        }

        // ⚠ After the terrains and outside the `IsValid` guard, on purpose twice over: the grass
        // scatters over this frame's terrain draws, so they have to be chosen first — and foliage
        // paints onto *any* surface, so a build with no terrain modules still shows the forest.
        vegetation.Upload(commands, VegetationScene, surfaces.Meshes, terrainDraws, camera, aspect, size.Y);
    }

    /// <summary>Records this frame's terrain draws.</summary>
    /// <param name="commands">Where to record. Must be inside the scene's render pass.</param>
    /// <returns>How many draws were issued, which is one per terrain that selected any patch.</returns>
    /// <remarks>
    ///     Its own method rather than a loop inside the pass, so a test can drive the recording
    ///     without building a render graph — the graph is what schedules it, not what it does.
    /// </remarks>
    internal int RecordTerrain(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);

        var draws = 0;

        foreach (var (terrain, _) in terrainDraws) {
            terrain.Record(commands);
            draws += terrain.Draws;
        }

        return draws;
    }

    /// <summary>How much light a surface facing away from the key gets, in the shaded modes.</summary>
    /// <remarks>
    ///     <see cref="MeshInstanceRenderer.Ambient" />'s own default, held here because the renderer's
    ///     copy is overwritten every frame by the view mode and there has to be somewhere the shaded
    ///     value comes back from.
    /// </remarks>
    public float Ambient { get; set; } = 0.35f;

    /// <summary>Copies a collected list into a renderer's buffer.</summary>
    /// <remarks>
    ///     Through <see cref="pending" /> rather than straight from the source, because
    ///     <c>Upload</c> takes a span and <c>SceneLines</c> hands back an
    ///     <see cref="IReadOnlyList{T}" /> — which is the right shape for something a test reads and
    ///     the wrong one for something a device writes. One reused list, cleared per call.
    /// </remarks>
    void Write(LineRenderer renderer, params IReadOnlyList<LineVertex>[] lists) {
        pending.Clear();

        foreach (var segments in lists) {
            foreach (var vertex in segments) {
                pending.Add(vertex);
            }
        }

        renderer.Upload(CollectionsMarshal.AsSpan(pending));
    }

    /// <summary>Turns the collector's batches into draws, registering any shape it has not seen.</summary>
    /// <param name="commands">The frame's command list, for the copies a registration stages.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The seam between a collector with no device and a renderer with no scene.</b> A
    ///         <c>ShapeBatch</c> says "these entities are cubes"; this is where a cube becomes a range
    ///         in a vertex buffer, once, on the frame the first one appears.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A shape the buffers cannot hold is dropped rather than thrown for.</b> That is a
    ///         scene larger than this path was sized for, and one shape missing from the picture is
    ///         something a designer can see and recover from where a frame that threw is not.
    ///     </para>
    /// </remarks>
    void Resolve(ICommandList commands) {
        if (revision != surfaces.Revision) {
            // ⚠ Idled before the space is reused, and this is the one place that needs it: freeing a
            // slice hands its bytes to the next registration, which would then overwrite geometry a
            // frame still in flight is drawing from. A tessellation change is a menu item rather than
            // a frame, so the stall costs nothing anybody can see.
            device.WaitIdle();

            foreach (var shape in registered.Values) {
                meshes.Release(shape);
            }

            registered.Clear();
            revision = surfaces.Revision;
        }

        draws.Clear();

        // ⚠ Asked for once, before anything is staged. A frame that meets a scene of block-out meshes
        // for the first time wants all of them at once, and the staging region can only be grown while
        // nothing refers to it — which is true here and stops being true after the first write. Without
        // this a large scene appears one buffer-full per frame, in visible steps; with it the frame
        // that could not fit everything is followed by one that can.
        meshes.Reserve(Wanted());

        Deferred = 0;

        foreach (var batch in surfaces.Batches) {
            if (!registered.TryGetValue(batch.Shape, out var shape)) {
                // Null for a mesh the collector could not resolve, which cannot happen for a batch it
                // emitted — a batch exists because some entity drew it. Checked anyway, because the
                // alternative is a null dereference in a viewport's draw path.
                if (surfaces.Shape(batch.Shape) is not { } geometry) {
                    continue;
                }

                // ⚠ A shape that would not fit in what is left of the staging region is deferred
                // rather than thrown for, which is what this method's own remarks have always claimed
                // and what the buffer could not honour until it grew a way to be asked. It is not
                // drawn this frame and is registered on the next, when the region is empty again.
                if (!meshes.TryRegister(geometry, out shape)) {
                    Deferred++;
                    continue;
                }

                registered[batch.Shape] = shape;
            }

            draws.Add(new(shape, batch.First, batch.Count, batch.Edges));

            if (batch.Shape.IsEdit) {
                drawn.Add(batch.Shape);
            }
        }

        Retire();
        meshes.Flush(commands);
    }

    /// <summary>How many shapes the last frame could not find staging room for.</summary>
    /// <remarks>
    ///     ⚠ <b>A number that stays up is a scene whose geometry is bigger than one staging region and
    ///     is not converging</b> — which would otherwise look like a viewport that draws most of a
    ///     level. It falls to zero on the frame after a reservation has taken effect.
    /// </remarks>
    public int Deferred { get; private set; }

    /// <summary>How many bytes this frame's shapes would stage if none of them were registered yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted over the batches rather than tracked as the registrations happen.</b> The
    ///     reservation has to be made before the first write and the answer has to include the shapes
    ///     that write would make room impossible for — so it is a pass over the frame's own batch list,
    ///     which is tens of entries and already in hand.
    /// </remarks>
    long Wanted() {
        long total = 0;

        foreach (var batch in surfaces.Batches) {
            if (registered.ContainsKey(batch.Shape) || surfaces.Shape(batch.Shape) is not { } geometry) {
                continue;
            }

            total += meshes.StagingCost(geometry);
        }

        return total;
    }

    /// <summary>Gives back the space an edited mesh's previous revision was registered under.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Deferred by the ring's depth rather than freed on the spot, and this is the one
    ///         place a <c>WaitIdle</c> would be wrong.</b> Freeing a slice hands its bytes to the next
    ///         registration — see <see cref="Resolve" />'s own note — and an edited mesh is registered
    ///         again on every frame of a drag. Idling the device per frame of a drag is precisely the
    ///         four-frames-a-second tool <c>docs/plan/24-blockout-tools.md</c> § B1 called a blocker;
    ///         waiting the number of frames the renderer can have in flight costs nothing and is
    ///         correct for the same reason the instance ring is a ring.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keyed on what the frame drew, not on what changed.</b> An entity deleted mid-drag,
    ///         an undo that put a mesh back to a revision still registered, a pane that stopped drawing
    ///         a wall — all of them are "this key was not named this frame", and none of them has an
    ///         event that would have said so.
    ///     </para>
    /// </remarks>
    void Retire() {
        foreach (var shape in registered.Keys.Where(shape => shape.IsEdit && !drawn.Contains(shape)).ToArray()) {
            retiring.Add((registered[shape], meshes.Regions));
            registered.Remove(shape);
        }

        for (var index = retiring.Count - 1; index >= 0; index--) {
            var (geometry, left) = retiring[index];

            if (left > 0) {
                retiring[index] = (geometry, left - 1);
                continue;
            }

            meshes.Release(geometry);
            retiring.RemoveAt(index);
        }

        drawn.Clear();
    }

    /// <summary>Declares the pass that draws the scene.</summary>
    /// <param name="graph">The frame's graph.</param>
    /// <param name="viewport">The pane, for its camera.</param>
    /// <param name="texture">What the interface's pass has to declare that it reads.</param>
    /// <returns>Whether a pass was declared.</returns>
    /// <remarks>
    ///     ⚠ <b>The caller has to declare the read, and this returning the texture is how.</b> The
    ///     interface samples this target through a descriptor set, and a descriptor set is invisible
    ///     to the render graph — it orders passes and places barriers from what they <i>say</i> they
    ///     touch. Without the declaration the target is still a colour attachment when the interface
    ///     samples it, which Vulkan reports as a layout mismatch and which on a driver that does not
    ///     check would be a scene drawn from memory nothing had finished writing.
    /// </remarks>
    public bool Declare(RenderGraph graph, SceneViewport viewport, out GraphTexture texture) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(viewport);

        texture = default;

        if (!IsReady) {
            return false;
        }

        var target = graph.ImportTexture(
            colour,
            colourView,
            new(Format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "scene colour"),
            ResourceState.Undefined,

            // Left ready to be sampled, which is what the interface's pass does with it in the same
            // frame. The graph places the barrier from this rather than from anyone remembering to.
            ResourceState.ShaderRead
        );

        var depthTarget = graph.ImportTexture(
            depth,
            depthView,
            new(DepthFormat, size.X, size.Y, TextureUsage.DepthStencilTarget, Name: "scene depth"),
            ResourceState.Undefined,
            ResourceState.DepthStencilWrite
        );

        var aspect = size.Y <= 0 ? 1f : (float) size.X / size.Y;
        var camera = viewport.Camera;
        var viewProjection = camera.ViewProjection(aspect);

        // ⚠ The camera goes to the shapes as numbers rather than as a matrix, because the selection
        // outline is expanded per vertex by a width in *pixels* — see `MeshInstanceView`, and
        // `EditorCamera.PixelScale` for the half of `WorldPerPixel` a vertex stage can be given.
        var view = new MeshInstanceView(
            viewProjection,
            camera.Position,
            camera.Forward,
            camera.NearPlane,
            camera.IsOrthographic,
            camera.PixelScale(size.Y)
        );

        graph.AddPass(
            "scene",
            pass => {
                pass.ColourAttachment(target, LoadAction.Clear, new Color4(0.10f, 0.11f, 0.13f, 1f));

                // Zero is *far* under the engine's reversed-Z convention, which the mesh and line
                // pipelines' GREATER comparison agrees with.
                pass.DepthAttachment(depthTarget, LoadAction.Clear, 0f);
                pass.SideEffect();

                pass.Execute(context => {
                    // ⚠ The terrain before the shapes, and it writes depth like them. It is the
                    // ground everything else stands on, so a marker or a grid line below it has to be
                    // hidden by it — which only happens if it is in the buffer before they are tested.
                    var ground = RecordTerrain(context.CommandList);

                    // ⚠ The vegetation right after the ground and before everything else, because it
                    // is the other depth-writing geometry here: a marker behind a tree has to lose
                    // the depth test to it, and the grass draws read arguments a compute pass wrote
                    // during the upload — which is the ordering the dispatch's own barriers close.
                    ground += vegetation.Record(context.CommandList);

                    // ⚠ The shapes next, because they are the only other thing here that writes depth
                    // — the two line pipelines test it and neither fills it. Drawn after the grid they
                    // would be correct and pointless; drawn after nothing they are what the grid and
                    // the markers are then tested against.
                    meshes.Record(context.CommandList, view);

                    // ⚠ The water after every opaque thing and before the lines, and both halves
                    // matter. It blends premultiplied, so whatever it covers has to be in the target
                    // already — a lake recorded before the ground would be composited against the
                    // clear colour and read as a flat blue card. And it writes depth, so the grid
                    // and the markers under it are hidden by it, which is what makes a submerged
                    // object read as submerged rather than as floating over the water.
                    var wet = water.Record(context.CommandList, viewProjection);

                    lines.Record(context.CommandList, viewProjection);

                    // Last, and with the depth test off. See the fields' own remarks. The solid
                    // parts go after the shafts so that an opaque cone covers the end of the line
                    // running into it rather than being drawn over by it, and so that the ball in
                    // the middle covers the inner end of all three — the arms are built from the
                    // origin outwards and it is what hides that.
                    overlay.Record(context.CommandList, viewProjection, depthTested: false);
                    handles.Record(context.CommandList, viewProjection, depthTested: false);

                    viewport.Stats.Draws =
                        ground + wet + meshes.Draws + lines.Draws + overlay.Draws + handles.Draws;
                });
            }
        );

        texture = target;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        meshes.Dispose();
        handles.Dispose();
        lines.Dispose();
        overlay.Dispose();
        vegetation.Dispose();
        water.Dispose();

        foreach (var terrain in terrains.Values) {
            terrain.Dispose();
        }

        terrains.Clear();
        terrainDraws.Clear();

        Release();
    }

    void Release() {
        if (colourView.IsValid) {
            device.Destroy(colourView);
            device.Destroy(colour);
        }

        if (depthView.IsValid) {
            device.Destroy(depthView);
            device.Destroy(depth);
        }

        colourView = default;
        colour = default;
        depthView = default;
        depth = default;
        size = default;
    }
}
