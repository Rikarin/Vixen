// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.SceneView;

/// <summary>Which object each pixel belongs to, drawn into a target and read back.</summary>
/// <remarks>
///     <para>
///         <b>A render stage, not a raycast.</b> Raycasting works for a box and stops working for a
///         skinned mesh, an instanced forest, an alpha-cut leaf, a mesh the GPU deformed and anything
///         drawn by a shader that moved its vertices. Drawing the ids with the same vertex path as
///         the picture means what you click is what you see, by construction, for every kind of
///         geometry there will ever be. Stride's editor picks the same way for the same reason.
///     </para>
///     <para>
///         <b>Two passes, and they have to be two.</b> A copy cannot be recorded inside a render
///         pass, so the ids are drawn in a graphics pass and the one pixel under the pointer is
///         copied in a transfer pass that reads what the first one wrote. The graph derives the order
///         and the barrier from that read, which is the point of declaring it.
///     </para>
///     <para>
///         <b>The drawing half is a <c>RenderPassRenderer</c> this owns rather than a pass this
///         declares.</b> Building the draw pass by hand would need <c>CompositorFrame.Context</c>,
///         which is internal to <c>Vixen.Rendering</c> — and rightly so, since a draw context is the
///         renderer's own plumbing. Composition is the seam <c>SceneRenderer.BuildChild</c> exists
///         for, and it means the picking pass gets every fix the ordinary pass node gets.
///     </para>
///     <para>
///         ⚠ <b>Nothing is drawn on a frame nobody clicked.</b> The pass is skipped when there is no
///         request, so a viewport sitting still costs nothing — and because the render graph culls
///         passes whose output nobody reads, forgetting to skip it would have been merely wasteful
///         rather than wrong.
///     </para>
/// </remarks>
public sealed class PickingRenderer : SceneRenderer {
    /// <summary>The view to draw from.</summary>
    public required RenderView View { get; init; }

    /// <summary>The stage that writes ids.</summary>
    public required RenderStage Stage { get; init; }

    /// <summary>Where the answers are collected.</summary>
    public required PickingBuffer Buffer { get; init; }

    /// <summary>What the frame calls the id target.</summary>
    /// <remarks>
    ///     A name rather than a texture, because the render graph decides what a name is — how big,
    ///     whether it aliases something else, whether it needs to reach memory at all.
    /// </remarks>
    public string IdTarget { get; set; } = "PickingId";

    /// <summary>What the frame calls the depth buffer the ids are tested against.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own depth, not the scene's.</b> Sharing the scene's would mean the picking pass
    ///     had to run after everything that writes depth and would tie the two together for no gain;
    ///     the picking pass draws the same geometry and can fill its own.
    /// </remarks>
    public string DepthTarget { get; set; } = "PickingDepth";

    /// <summary>The per-view block to bind before drawing.</summary>
    public ViewConstants? Constants { get; set; }

    /// <summary>The texture declaration a compositor has to carry for <see cref="IdTarget" />.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="PixelFormat.R32UInt" />, not a colour format.</b> An id packed into eight-bit
    ///     channels runs out at sixteen million objects and, worse, is subject to whatever the
    ///     swapchain's colour space does to it; a 32-bit unsigned integer target is exact and is what
    ///     the value is. <see cref="TextureUsage.CopySource" /> is what lets the one pixel be read.
    /// </remarks>
    public RenderResourceAsset IdResource => new() {
        Name = IdTarget,
        Format = PixelFormat.R32UInt,
        Usage = TextureUsage.ColourTarget | TextureUsage.CopySource,
        Scale = 1f
    };

    /// <summary>The depth declaration a compositor has to carry for <see cref="DepthTarget" />.</summary>
    public RenderResourceAsset DepthResource => new() {
        Name = DepthTarget,
        Format = PixelFormat.Depth32Float,
        Usage = TextureUsage.DepthStencilTarget,
        Scale = 1f
    };

    /// <inheritdoc />
    protected override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        // Only when somebody clicked: a stage nothing draws costs no culling, which is the whole
        // point of the collect phase deciding the view's mask rather than a host setting it.
        if (Buffer.Requested is not null) {
            CollectChild(Draw(), compositor);
        }
    }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (!Buffer.TryTakeRequest(out var destination, out var request)) {
            return;
        }

        BuildChild(Draw(), compositor, frame);

        var ids = frame.Texture(ToString(), IdTarget);

        frame.Graph.AddPass(
            ToString() + ".Readback",
            pass => {
                pass.Kind = PassKind.Transfer;
                pass.Reads(ids, ResourceState.CopySource);

                // The graph culls a pass whose outputs nobody reads, and the readback buffer is not
                // one of its resources — it belongs to the ring and outlives the frame. Without this
                // the whole picking chain would be culled and picking would silently never answer.
                pass.SideEffect();

                pass.Execute(graphContext => {
                    var region = new TextureRegion(
                        graphContext.Texture(ids),
                        Origin: new Int3(request.X, request.Y, 0)
                    );

                    graphContext.CommandList.CopyTextureToBuffer(region, PickingBuffer.CopySize, destination, 0);
                });
            }
        );
    }

    /// <summary>The pass that draws the ids, made fresh each phase from the current settings.</summary>
    /// <remarks>
    ///     ⚠ <b>Both phases have to be handed the same shape</b>, and the target names are settable,
    ///     so it is built from them rather than cached — a node whose collect and build disagreed
    ///     about which stage it draws would cull for one list and record from another.
    /// </remarks>
    RenderPassRenderer Draw() {
        var pass = new RenderPassRenderer {
            Name = ToString(),
            DepthTarget = DepthTarget,

            // Cleared to zero, which is "nothing" — see PickResult.IsHit for why an object's id is
            // its index plus one rather than its index.
            Load = LoadAction.Clear,
            ClearColour = default,
            DepthLoad = LoadAction.Clear,
            ClearDepth = 0f
        };

        pass.ColourTargets.Add(IdTarget);
        pass.Children.Add(new SingleStageRenderer { View = View, Stage = Stage, Constants = Constants });

        return pass;
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "Picking" : Name;
}
