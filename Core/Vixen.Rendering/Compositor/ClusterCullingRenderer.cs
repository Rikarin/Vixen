// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Runs the cluster traversal inside the frame, and lands the pages it asked for last frame.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="GpuCullingRenderer" />'s sibling, placed for the same reason.</b> The
///         traversal's answer is consumed by a draw, and the only ordering this RHI can express without
///         a fence or a semaphore is a barrier between two things in the same queue — so the dispatch
///         has to be recorded where the draws that read it are recorded. That is what this does, at the
///         head of the frame.
///     </para>
///     <para>
///         <b>The page copies are recorded here too, and the order is the whole of why.</b> A page that
///         arrived on an I/O thread is bytes in a staging buffer;
///         <see cref="MeshletPagePool.Flush" /> turns them into copies into the pool. Those copies have
///         to be recorded <em>before</em> the traversal's dispatch, because the traversal reads a
///         residency bitset that already says the page is there — the bit was set by
///         <see cref="Features.VirtualGeometryRenderFeature.Prepare" />, which ran before this node.
///         Flushing after the dispatch would mean one frame in which a cluster is drawn from a slot
///         whose bytes are still in flight.
///     </para>
///     <para>
///         It declares no graph resources and is therefore a side effect, exactly as
///         <see cref="GpuCullingRenderer" /> is: what it writes are buffers that outlive the graph, and
///         what reads them is a draw call rather than a pass.
///     </para>
/// </remarks>
public sealed class ClusterCullingRenderer : SceneRenderer {
    /// <summary>The traversal whose dispatch to record. Null does nothing at all.</summary>
    public GpuClusterVisibility? Visibility { get; set; }

    /// <summary>The pool whose arrived pages to copy in. Null records the dispatch and stops there.</summary>
    /// <remarks>
    ///     Separate from the traversal because the pool is the residency service's and improvement 6 of
    ///     <c>docs/virtualized-geometry.md</c> means that service will one day hold texture and shadow
    ///     pages too — a node that reached the pool through the traversal would be a node that could only
    ///     ever flush geometry.
    /// </remarks>
    public MeshletPagePool? Pages { get; set; }

    /// <summary>How many page copies the last frame recorded.</summary>
    /// <remarks>
    ///     Zero in a steady state, which is what a streamed scene that has finished streaming looks
    ///     like. Worth a counter because a frame that records none while the traversal keeps asking is
    ///     the signature of a loop that is requesting and never landing — and that draws a correct,
    ///     permanently coarse picture, which nothing else would report.
    /// </remarks>
    public int LastPageCopies { get; private set; }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Visibility is null) {
            return;
        }

        var visibility = Visibility;
        var pages = Pages;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;
                pass.SideEffect();

                pass.Execute(
                    context => {
                        // Pages first — see the class remarks. The bitset the dispatch is about to read
                        // was written on the assumption that this happened.
                        LastPageCopies = pages?.Flush(context.CommandList) ?? 0;

                        visibility.Record(context.CommandList);
                    }
                );
            }
        );
    }
}
