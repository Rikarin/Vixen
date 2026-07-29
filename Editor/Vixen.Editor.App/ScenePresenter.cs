// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         ⚠ <b>Three draws into one target, in an order that is not arbitrary.</b> The shapes go
///         first and write depth; the grid and the entity markers follow, depth-tested so that a
///         marker behind a cube is behind it; the gizmo goes last with no depth test at all, because
///         a handle you cannot reach through the thing it moves is a handle you cannot use. The last
///         of the three is what a solid mesh pass made necessary — with only lines in the target
///         there was nothing to be occluded by, which is why the overlay used to share the world
///         list's pipeline.
///     </para>
///     <para>
///         ⚠ <b>This is not the mesh path either.</b> <see cref="MeshRenderer" />'s own remarks say
///         what it is: a tool renderer with no materials and no culling, whose cost is linear in
///         vertices. It is what makes a spawned cube visible today; a viewport driven by
///         <c>RenderSystem</c> through a <c>GraphicsCompositor</c> is what replaces it.
///     </para>
///     <para>
///         ⚠ <b>The target is recreated when the viewport resizes, and the registration is redone
///         with it.</b> A number registered against a destroyed view is a descriptor set pointing at
///         freed memory, and the frame that draws it is undefined behaviour rather than an error —
///         see <c>UiRenderer.RegisterImage</c>. Unregistering first is what keeps that impossible.
///     </para>
/// </remarks>
sealed class ScenePresenter : IDisposable {
    /// <summary>What the interface calls this texture.</summary>
    /// <remarks>
    ///     ⚠ <b>Constant across resizes.</b> The viewport control holds it and the draw list carries
    ///     it; changing it when the target is recreated would mean a frame drawn with a number
    ///     nothing has registered, which is a viewport that blinks empty on every splitter drag.
    /// </remarks>
    public const ulong Image = 1;

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

    readonly MeshRenderer meshes;

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
    /// <param name="meshShaders">The two mesh stages.</param>
    /// <param name="format">What the target's colour format is.</param>
    public ScenePresenter(
        IGraphicsDevice device,
        LineShaders shaders,
        MeshShaders meshShaders,
        PixelFormat format
    ) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        Format = format;

        var output = new RenderOutput([format], DepthFormat);

        lines = new(device, shaders, output);
        overlay = new(device, shaders, output);
        meshes = new(device, meshShaders, output);

        // A few hundred vertices at most: three heads of a dozen segments each. Sized down from the
        // default so a second mesh ring costs kilobytes rather than the megabytes the scene's does.
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

        return true;
    }

    /// <summary>Collects the frame's geometry and writes it.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="viewport">The pane.</param>
    /// <remarks>
    ///     ⚠ <b>Outside the render pass.</b> This writes buffers, and a Vulkan command list may not
    ///     transfer inside one — the same reason the glyph atlas is uploaded before the interface's
    ///     pass rather than in it.
    /// </remarks>
    public void Upload(SceneDocument document, SceneViewport viewport) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewport);

        surfaces.Build(document);
        meshes.Upload(surfaces.Vertices, surfaces.Indices);

        geometry.Build(document, viewport, size.Y);

        Write(lines, geometry.World);
        Write(overlay, geometry.Overlay);

        // Straight across, no copy: `SceneLines` hands the gizmo's solid parts back as spans for
        // exactly this, which the two segment lists cannot do — see its own remarks.
        handles.Upload(geometry.Handles, geometry.HandleIndices);
    }

    /// <summary>Copies a collected list into a renderer's buffer.</summary>
    /// <remarks>
    ///     Through <see cref="pending" /> rather than straight from the source, because
    ///     <c>Upload</c> takes a span and <c>SceneLines</c> hands back an
    ///     <see cref="IReadOnlyList{T}" /> — which is the right shape for something a test reads and
    ///     the wrong one for something a device writes. One reused list, cleared per call.
    /// </remarks>
    void Write(LineRenderer renderer, IReadOnlyList<LineVertex> segments) {
        pending.Clear();

        foreach (var vertex in segments) {
            pending.Add(vertex);
        }

        renderer.Upload(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));
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
        var viewProjection = viewport.Camera.ViewProjection(aspect);

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
                    meshes.Record(context.CommandList, viewProjection);
                    lines.Record(context.CommandList, viewProjection);

                    // Last, and with the depth test off. See the fields' own remarks. The heads go
                    // after the shafts so that an opaque cone covers the end of the line running into
                    // it rather than being drawn over by it.
                    overlay.Record(context.CommandList, viewProjection, depthTested: false);
                    handles.Record(context.CommandList, viewProjection, depthTested: false);
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
