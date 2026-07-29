// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Reduces the frame's depth into a <see cref="HiZPyramid" />, for the next frame's culling to
///     test against.
/// </summary>
/// <remarks>
///     <para>
///         The node exists because of one word in its declaration: <em>reads</em>. Depth is a graph
///         resource — written by a prepass or by the opaque pass, aliased with whatever else fits its
///         memory — and a dispatch that sampled it without saying so would run before the pass that
///         filled it, or read it in the wrong layout, on whichever driver reordered them. Declaring
///         the read is what orders the two and places the barrier, and it is the reason doc 06 says
///         GPU occlusion culling needs the culler to be part of the compositor.
///     </para>
///     <para>
///         <strong>Placed after the pass that fills depth, not before the frame.</strong> What it
///         reduces is <em>this</em> frame's depth, and what consumes it is <em>next</em> frame's
///         <see cref="GpuVisibilityGroup.Cull" /> — which runs before anything is drawn and so can
///         never have anything newer. That one frame of staleness is the whole cost of one-phase
///         occlusion culling: something that was hidden last frame and is not now appears a frame
///         late.
///     </para>
///     <para>
///         The pyramid outlives the graph, so the node does not declare it as a resource: a
///         transient the graph aliased would be somebody else's pixels by the time the culler
///         sampled it. See <see cref="HiZPyramid" />.
///     </para>
/// </remarks>
public sealed class HiZRenderer : SceneRenderer {
    /// <summary>The name of the depth texture to reduce.</summary>
    public required string Depth { get; init; }

    /// <summary>The pyramid to build. Null does nothing at all.</summary>
    /// <remarks>
    ///     Owned by whatever also hands it to <see cref="GpuVisibilityGroup.Occluders" />, because
    ///     those two are the same object seen from either end of a frame boundary and nothing else
    ///     would keep them the same one.
    /// </remarks>
    public HiZPyramid? Pyramid { get; set; }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Pyramid is null) {
            return;
        }

        var depth = frame.Texture(ToString(), Depth);
        var size = frame.Size;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;
                pass.Reads(depth);

                // What it writes is not a graph resource, so as far as the graph can see this pass
                // produces nothing and is dead. Saying so is the price of the pyramid outliving the
                // frame — and it is the honest way round: a transient the graph could track would be
                // aliased away before the next frame's culler ever sampled it.
                pass.SideEffect();

                pass.Execute(context => Pyramid.Build(context.CommandList, context.View(depth), size));
            }
        );
    }
}
