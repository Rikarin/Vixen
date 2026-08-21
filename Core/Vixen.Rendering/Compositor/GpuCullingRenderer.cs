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
    /// <summary>
    ///     Where this feature's draws read their arguments, or null to carry their own counts.
    /// </summary>
    /// <remarks>
    ///     Settable, and on the same interface as the filling, because they are two halves of one
    ///     decision: a feature that fills records is the feature that draws from them, and a host
    ///     that set one without the other would have a buffer nothing reads or a draw reading a
    ///     buffer nobody filled. <see cref="CompositorBuilder" /> assigns it when a document asks for
    ///     indirect draws, which is the only place both facts are known at once.
    /// </remarks>
    GpuDrawArguments? Arguments { get; set; }

    /// <summary>Fills the records for this feature's objects, leaving every other slot alone.</summary>
    /// <param name="system">The render system, for the per-object data.</param>
    /// <param name="commands">One record per object slot, cleared before the first source sees it.</param>
    void FillArguments(RenderSystem system, Span<DrawCommand> commands);

    /// <summary>
    ///     Says which batch each of this feature's objects belongs to, for a compacted buffer.
    /// </summary>
    /// <param name="system">The render system.</param>
    /// <param name="batches">One id per object slot, cleared before the first source sees it.</param>
    /// <remarks>
    ///     <para>
    ///         A batch is objects whose draws could be merged: same pipeline, same geometry, same
    ///         material set. Only the feature that would bind those things knows, which is why this
    ///         is here and not derived.
    ///     </para>
    ///     <para>
    ///         Doing nothing is a correct implementation and puts everything in batch zero — which is
    ///         right for a feature whose objects really do bind alike, and safe for one whose do not,
    ///         because the draw side checks that a run is a whole batch before covering it with one
    ///         command.
    ///     </para>
    /// </remarks>
    void FillBatches(RenderSystem system, Span<uint> batches) { }
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
///     <para>
///         <strong>Two-phase culling is two of these nodes.</strong> A second one with
///         <see cref="Phase" /> set to <see cref="CullPhase.Late" />, placed after the main pass's
///         draws and after the <see cref="HiZRenderer" /> that reduced them, asks again about
///         everything the first rejected — this time against depth from this frame rather than the
///         last. Its answer goes into the same two buffers, which is why the draws that follow it
///         need no knowledge of any of this: they read the same argument buffer, whose contents by
///         then are the difference.
///     </para>
/// </remarks>
public sealed class GpuCullingRenderer : SceneRenderer {
    /// <summary>The group whose dispatch to record. Null does nothing at all.</summary>
    public GpuVisibilityGroup? Visibility { get; set; }

    /// <summary>Where the draw arguments go. Null records the cull and stops there.</summary>
    public GpuDrawArguments? Arguments { get; set; }

    /// <summary>Which of a two-phase cull's dispatches this node records.</summary>
    /// <remarks>
    ///     <see cref="CullPhase.Main" /> for an ordinary frame, and for the first of a two-phase one.
    ///     A <see cref="CullPhase.Late" /> node is only meaningful after a main one and after the
    ///     depth its draws produced has been reduced — nothing here can check that, which is why this
    ///     is a node to be placed rather than a flag to be set.
    /// </remarks>
    public CullPhase Phase { get; set; }

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

        // The main node fills the templates; the late node reuses them. They are what the host would
        // have drawn, which the two phases agree about — all that differs is the bits they are
        // filtered by. Filling them again here would clear what the first pass's sources wrote.
        if (Phase == CullPhase.Main && arguments is not null && objectCount > 0 && viewCount > 0) {
            var commands = arguments.Fill(objectCount);

            foreach (var feature in system.Features) {
                if (feature is IDrawArgumentSource source) {
                    source.FillArguments(system, commands);
                }
            }

            // A second pass rather than one, because the two spans come from two arrays and handing
            // out both at once would mean a source could hold one while the other was resized.
            var batches = arguments.Batches(objectCount);

            foreach (var feature in system.Features) {
                if (feature is IDrawArgumentSource source) {
                    source.FillBatches(system, batches);
                }
            }
        }

        var visibility = Visibility;
        var phase = Phase;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // ⚠ **A dispatch that declares itself a graphics pass, deliberately.** What it
                // writes are the visibility bits and the draw-argument buffer, and what reads them
                // are the *draw calls* of later passes — bound into a pipeline rather than declared
                // to the graph, because a graph resource would be aliased out from under a buffer
                // that has to outlive the frame. So there is nothing here for the scheduler to
                // derive a wait edge from, in either direction, and a pass marked Compute would be
                // hoisted into a segment nothing waits for: the rasteriser would read arguments the
                // cull had not finished writing, on hardware with a compute family of its own.
                //
                // Marking it Graphics is the honest declaration, not a workaround. On one queue,
                // declaration order *is* execution order and a barrier orders the dispatch against
                // the draws — which is the ordering this node was built around and says so above.
                // See docs/guide/rendering/async-compute.md for the audit this came out of.
                pass.Kind = PassKind.Graphics;
                pass.SideEffect();

                pass.Execute(
                    context => {
                        // Nothing to record when the group read its answer back — it dispatched and
                        // waited during Cull, and the arguments would be a second copy of a decision
                        // the work list already carries. A late node adds: nothing to record when
                        // there was no pyramid to retest against, which is a frame culled exactly as
                        // a one-phase frame would have been.
                        var recorded = phase == CullPhase.Late
                            ? visibility.RecordLate(context.CommandList)
                            : visibility.Record(context.CommandList);

                        if (!recorded) {
                            return;
                        }

                        arguments?.Update(context.CommandList, visibility.Bits, viewCount, objectCount);
                    }
                );
            }
        );
    }
}
