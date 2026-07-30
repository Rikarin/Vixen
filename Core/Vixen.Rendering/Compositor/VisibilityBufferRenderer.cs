// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Draws every visible cluster into a visibility buffer: phase 4, placed in the frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>A graph pass, unlike its two siblings.</b>
///         <see cref="GpuCullingRenderer" /> and <see cref="ClusterCullingRenderer" /> declare no
///         resources and are side effects, because what they write are buffers that outlive the graph.
///         This one writes a texture the rest of the frame reads, so it declares it — and the graph then
///         aliases it, transitions it and hands it to whatever names it, exactly as it does for a depth
///         target or a bloom chain.
///     </para>
///     <para>
///         <b>The identity target clears to all ones, not to zero.</b> Zero is a real identity — the
///         first cluster of the frame, its first triangle — so a pixel nothing covered would read as
///         geometry. <see cref="GpuClusterRaster.Nothing" /> is what the resolve tests against, and this
///         is the only place it gets written.
///     </para>
///     <para>
///         <b>It needs a depth target and does not own one.</b> The whole reason phase 4 needs no
///         atomics is that the hardware's depth test resolves which cluster is nearest, so a pass without
///         depth would write whichever identity the rasterizer happened to reach last. Naming an existing
///         depth resource rather than creating one is deliberate: a frame that also draws classic
///         geometry wants both in <em>one</em> depth buffer, or the two occlude each other not at all.
///     </para>
/// </remarks>
public sealed class VisibilityBufferRenderer : SceneRenderer {
    /// <summary>The raster that records the draw. Null does nothing at all.</summary>
    public GpuClusterRaster? Raster { get; set; }

    /// <summary>
    ///     The binning pass that sorts this buffer's tiles by material. Null draws it and stops there.
    /// </summary>
    /// <remarks>
    ///     Recorded by this node rather than by one of its own, because the ordering is not something a
    ///     document should be able to get wrong: the binning reads the identity buffer, so it has to be
    ///     after the draw that writes it and before anything that resolves it. A separate node would be a
    ///     separate placement decision with no way to check it — and it would have to reopen the graph's
    ///     pass to name the same texture.
    /// </remarks>
    public GpuVisibilityTiles? Tiles { get; set; }

    /// <summary>What the identity target is called in the graph, for whatever resolves it.</summary>
    public string Output { get; set; } = "VisibilityBuffer";

    /// <summary>Which depth resource to test and write against.</summary>
    /// <remarks>
    ///     Required, for the reason in the class remarks: without a depth test the nearest cluster is
    ///     whichever one the rasterizer reached last. A name rather than a texture so that the frame's
    ///     one depth buffer serves this pass and the classic draws alike.
    /// </remarks>
    public string Depth { get; set; } = "SceneDepth";

    /// <summary>Which view's camera the clusters are projected with.</summary>
    /// <remarks>
    ///     One view, because a visibility buffer is a screen and a screen belongs to a camera. A shadow
    ///     view wants its own buffer at its own resolution, which is phase 7 rather than a second index
    ///     here.
    /// </remarks>
    public int ViewIndex { get; set; }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Raster is not { Visibility.MeshCount: > 0 }) {
            return;
        }

        var system = compositor.System;

        if (ViewIndex < 0 || ViewIndex >= system.Views.Count) {
            return;
        }

        var raster = Raster;
        var tiles = Tiles;
        var viewProjection = system.Views[ViewIndex].ViewProjection;
        var size = frame.Size;

        var identity = frame.Graph.CreateTexture(
            new(
                GpuClusterRaster.Format,
                size.X,
                size.Y,
                TextureUsage.ColourTarget | TextureUsage.Sampled,
                Name: Output
            )
        );

        // Registered under its name so whatever resolves it can ask for it the way every other node asks
        // for a target — which is what makes phase 5 a node placed in a document rather than a reference
        // to this class.
        frame.Add(Output, identity, GpuClusterRaster.Format);

        var depth = frame.Texture(ToString(), Depth);
        var format = frame.FormatOf(ToString(), Depth);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // Cleared, and to zero — which GpuClusterRaster.Nothing is, and which is why the packed
                // slot is biased by one. A pixel nothing drew reads as nothing rather than as the frame's
                // first cluster.
                pass.ColourAttachment(identity, LoadAction.Clear);
                pass.DepthAttachment(depth, LoadAction.Load);

                pass.Execute(
                    context => {
                        // Outside the render pass and before it: the argument copy that turns the
                        // traversal's count into an instance count is a buffer copy, and a copy between
                        // BeginRenderPass and EndRenderPass is not legal. The graph opens the pass around
                        // Execute, so this has to be the pass's first act rather than the node's.
                        raster.Record(context.CommandList, viewProjection, GpuClusterRaster.Format, format);
                    }
                );
            }
        );

        if (tiles is null) {
            return;
        }

        // A second pass, and not a second node. The binning samples what the draw wrote, so it cannot be
        // inside the render pass that writes it — an attachment being written is not a texture being read
        // — and it must not be somewhere a document could place it wrongly. Two passes from one node is
        // the arrangement that makes both true.
        frame.Graph.AddPass(
            $"{this}.Tiles",
            pass => {
                pass.Kind = PassKind.Compute;
                pass.Reads(identity);
                pass.SideEffect();

                pass.Execute(
                    context => {
                        // The counters the previous frame left, before this frame overwrites them. Read
                        // here rather than by the caller because this is the one place that knows the
                        // dispatch has been submitted and the next one has not started.
                        tiles.ReadCounts();
                        tiles.Record(context.CommandList, context.View(identity), size);
                    }
                );
            }
        );
    }
}
