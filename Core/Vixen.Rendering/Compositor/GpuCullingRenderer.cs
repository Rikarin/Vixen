// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Something that knows what its objects would be drawn with, for the pass that turns visibility
///     into draw calls.
/// </summary>
/// <remarks>
///     An interface rather than a reference to the mesh feature, for the reason
///     <see cref="Features.IInstanceSource" /> is one: the argument pass needs five numbers per
///     object and should not depend on the machinery that produces them. A feature that draws
///     something other than meshes fills its own objects' records and nothing else changes.
/// </remarks>
public interface IDrawArgumentSource {
    /// <summary>Fills the records for this feature's objects, leaving every other slot alone.</summary>
    /// <param name="system">The render system, for the per-object data.</param>
    /// <param name="commands">One record per object slot, cleared before the first source sees it.</param>
    void FillArguments(RenderSystem system, Span<DrawCommand> commands);
}

/// <summary>
///     Runs the culling dispatch and the argument pass inside the frame, so neither answer has to come
///     back to the host.
/// </summary>
/// <remarks>
///     <para>
///         The node that makes <see cref="GpuVisibilityGroup.ReadBack" /> false a usable setting.
///         With no readback there is no wait, and the only ordering this RHI can express without a
///         fence or a semaphore is a barrier between two things in the same queue — so the dispatch
///         has to be recorded where the draws that consume it are recorded. That is what this does,
///         at the head of the frame: cull, then turn the bits into draw arguments, then let every
///         later pass draw from them.
///     </para>
///     <para>
///         <strong>The templates are filled here rather than in a feature's <c>Prepare</c>.</strong>
///         A root feature's <c>Prepare</c> runs before its sub-features', so an instancing batch's
///         size and first instance are not known yet when it runs — and those are two of the five
///         numbers a record holds. A compositor node's <c>Build</c> runs after the whole of
///         <see cref="RenderSystem.Draw" />, which is the first moment they all exist.
///     </para>
///     <para>
///         It declares no graph resources and is therefore a side effect: what it writes are two
///         buffers that outlive the graph, and what reads them is a draw call rather than a pass.
///     </para>
/// </remarks>
public sealed class GpuCullingRenderer : SceneRenderer {
    /// <summary>The group whose dispatch to record. Null does nothing at all.</summary>
    public GpuVisibilityGroup? Visibility { get; set; }

    /// <summary>Where the draw arguments go. Null records the cull and stops there.</summary>
    public GpuDrawArguments? Arguments { get; set; }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Visibility is null) {
            return;
        }

        var system = compositor.System;
        var objectCount = system.Objects.Count;
        var viewCount = system.Views.Count;
        var arguments = Arguments;

        if (arguments is not null && objectCount > 0 && viewCount > 0) {
            var commands = arguments.Fill(objectCount);

            foreach (var feature in system.Features) {
                if (feature is IDrawArgumentSource source) {
                    source.FillArguments(system, commands);
                }
            }
        }

        var visibility = Visibility;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;
                pass.SideEffect();

                pass.Execute(
                    context => {
                        // Nothing to record when the group read its answer back — it dispatched and
                        // waited during Cull, and the arguments would be a second copy of a decision
                        // the work list already carries.
                        if (!visibility.Record(context.CommandList)) {
                            return;
                        }

                        arguments?.Update(context.CommandList, visibility.Bits, viewCount, objectCount);
                    }
                );
            }
        );
    }
}
