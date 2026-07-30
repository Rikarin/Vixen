// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Ui.Renderer;

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
///         a transform, which is what <c>docs/blockout-tools.md</c> § B1 asked for and what makes the
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
    readonly Dictionary<PrimitiveKind, MeshShapeGeometry> registered = [];

    /// <summary>The batches of <see cref="surfaces" />, resolved against <see cref="registered" />.</summary>
    readonly List<MeshInstanceBatch> draws = [];

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
        // `docs/blockout-tools.md` is about, and are written once rather than per frame.
        meshes = new(device, instanceShaders, output);

        // A few hundred vertices at most: three heads of a dozen segments each. Sized down from the
        // default so a second mesh ring costs kilobytes rather than megabytes.
        handles = new(device, meshShaders, output, 4096, 8192);
    }

    /// <summary>What the shapes in the scene are drawn as.</summary>
    public SceneMeshes Surfaces => surfaces;

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

        surfaces.Build(document, viewport);

        // ⚠ Resolved after the collect and before the upload, because this is where a shape first seen
        // this frame gets put on the device. The batches name kinds; the renderer needs slices.
        Resolve(commands);

        meshes.Upload(surfaces.Instances, CollectionsMarshal.AsSpan(draws));

        geometry.Build(document, viewport, size.Y);

        Write(lines, geometry.World);
        Write(overlay, geometry.Overlay);

        // Straight across, no copy: `SceneLines` hands the gizmo's solid parts back as spans for
        // exactly this, which the two segment lists cannot do — see its own remarks.
        handles.Upload(geometry.Handles, geometry.HandleIndices);

        var stats = viewport.Stats;

        stats.Entities = surfaces.Count;

        // ⚠ The renderer's counts rather than the collector's, and the difference is the truncation. A
        // frame that produced more entities than the instance ring holds drops whole batches, and a
        // stats overlay whose triangle count silently stops rising is the one readout that would hide
        // the overflow it exists to make visible.
        stats.Triangles = meshes.Triangles;
        stats.Segments = ((geometry.World.Count + geometry.Overlay.Count) / 2) + meshes.Segments;
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

        foreach (var batch in surfaces.Batches) {
            if (!registered.TryGetValue(batch.Kind, out var shape)) {
                if (!meshes.TryRegister(surfaces.Shape(batch.Kind), out shape)) {
                    continue;
                }

                registered[batch.Kind] = shape;
            }

            draws.Add(new(shape, batch.First, batch.Count, batch.Edges));
        }

        meshes.Flush(commands);
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
                    // ⚠ The shapes first, because they are the only thing here that writes depth —
                    // the two line pipelines test it and neither fills it. Drawn after the grid they
                    // would be correct and pointless; drawn after nothing they are what the grid and
                    // the markers are then tested against.
                    meshes.Record(context.CommandList, view);
                    lines.Record(context.CommandList, viewProjection);

                    // Last, and with the depth test off. See the fields' own remarks. The heads go
                    // after the shafts so that an opaque cone covers the end of the line running into
                    // it rather than being drawn over by it.
                    overlay.Record(context.CommandList, viewProjection, depthTested: false);
                    handles.Record(context.CommandList, viewProjection, depthTested: false);

                    viewport.Stats.Draws = meshes.Draws + lines.Draws + overlay.Draws + handles.Draws;
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
