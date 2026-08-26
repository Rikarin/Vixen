// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Lighting;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Keeps the clipmap over the camera, on the device, and named in the frame's set.
/// </summary>
/// <remarks>
///     <para>
///         The node that joins the three halves that already existed and never met: a
///         <see cref="GlobalDistanceField" /> that knows how to composite itself, a
///         <see cref="GlobalDistanceFieldTexture" /> that knows how to copy one up, and a shader that
///         knows how to read one. Everything here is sequencing.
///     </para>
///     <para>
///         <b>It recomposites when the answer would change, not every frame.</b> A composite is every
///         cell of every level against every instance — the most expensive thing in the frame by a
///         wide margin — and a camera that has not crossed a cell boundary would get the same numbers
///         back. The levels are snapped to their own grids precisely so that "has anything moved" is
///         a comparison rather than a guess, and this is what cashes that in: a still camera
///         composites once and then never again.
///     </para>
///     <para>
///         <b>The names are written every frame regardless.</b> They are cheap, they are the frame's
///         answer to "where is the clipmap now", and a set rebuilt for another reason would otherwise
///         bind whatever the last frame left. Re-asserting a value that has not changed does not
///         bump <c>ParameterCollection.Version</c>, so it does not cost an upload either.
///     </para>
///     <para>
///         <b>The view position is the host's, not the camera's.</b> It is usually the camera and does
///         not have to be: a clipmap centred slightly ahead of a fast-moving camera has the geometry
///         it is about to need rather than the geometry behind it. Taking it from
///         <see cref="RenderView" /> would make that impossible to express, so the default is to
///         follow the view and the property is there to override it.
///     </para>
/// </remarks>
public sealed class GlobalDistanceFieldRenderer : SceneRenderer, IDisposable {
    readonly List<DistanceFieldInstance> instances = [];

    Vector3 lastCentre;
    int lastVersion;
    bool composited;
    bool disposed;

    ClipmapRefresh? refresh;
    JobHandle refreshHandle;
    Vector3 refreshCentre;

    /// <summary>The clipmap to keep. Null does nothing at all.</summary>
    public GlobalDistanceField? Field { get; set; }

    /// <summary>Its mirror on the device, made on the first record.</summary>
    public GlobalDistanceFieldTexture? Texture { get; private set; }

    /// <summary>Where the names go — the frame's set 0.</summary>
    /// <remarks>Null writes nothing, which is what a node kept for its clipmap alone wants.</remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>The compose-slot prefix the clipmap's names are written under.</summary>
    /// <remarks>
    ///     A slot's bindings are named for the <i>slot</i> rather than for the shader that declared
    ///     them, so this is <c>DistanceFieldAo.GlobalDistanceField</c> and not <c>ForwardPlus</c> —
    ///     the pass, then the slot, then the name. Get it wrong and every binding resolves to nothing,
    ///     silently, which is why the default is the one consumer that exists rather than a guess.
    /// </remarks>
    public string ShaderName { get; set; } = "DistanceFieldAo.GlobalDistanceField";

    /// <summary>Any further prefixes the same clipmap is written under.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>One clipmap, more than one consumer.</b> <see cref="ShaderName" /> is the pass that
    ///         has always read it; this is every other one, and the entry that matters is
    ///         <c>ForwardPlus.GlobalDistanceField</c> — the shading pass marching the field for
    ///         ambient occlusion, which is the only place occlusion can be applied to indirect light
    ///         and not to direct.
    ///     </para>
    ///     <para>
    ///         ⚠ Qualified, exactly like <see cref="ShaderName" />: the pass, then the slot's shader.
    ///         A binding written under a prefix no variant declares resolves to nothing and says
    ///         nothing — and a variant that declares bindings nothing writes is a set the writer fills
    ///         partially, which is every draw in that pass refused. The two failures look nothing
    ///         alike and both come from one string.
    ///     </para>
    /// </remarks>
    public IList<string> Passes { get; } = [];

    /// <summary>What the clipmap covers.</summary>
    /// <remarks>
    ///     Changing the contents does not itself trigger a recomposite — see
    ///     <see cref="InstancesVersion" />, which is what says the list means something different
    ///     now. Comparing the instances themselves every frame would cost more than the comparison
    ///     saves.
    /// </remarks>
    public IList<DistanceFieldInstance> Instances => instances;

    /// <summary>
    ///     Bumped by whoever changes <see cref="Instances" />, to say the composite is out of date.
    /// </summary>
    public int InstancesVersion { get; set; }

    /// <summary>Where to centre the clipmap, or null to follow the view.</summary>
    public Vector3? ViewPosition { get; set; }

    /// <summary>Whose position to follow when <see cref="ViewPosition" /> says nothing.</summary>
    /// <remarks>
    ///     Named rather than taken from a draw context, because <see cref="Build" /> is where this node
    ///     belongs and a graph pass has no view — see the remarks there. A frame with several views
    ///     has to say which one the clipmap follows anyway, and there is no defensible default.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>The device its texture is made on, or null to take the frame's.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many cells the last recomposite kept rather than recomputed.</summary>
    /// <remarks>
    ///     Zero on the first frame and after anything moves; nearly the whole clipmap for a camera
    ///     that crossed one cell. What says the scroll is happening rather than merely available.
    /// </remarks>
    public long Reused => Field?.Reused ?? 0;

    /// <summary>How many times the clipmap has actually been recomposited.</summary>
    /// <remarks>
    ///     What makes "it does not recomposite a still frame" checkable rather than claimed. A frame
    ///     that moved the camera a centimetre and this number going up is the defect.
    /// </remarks>
    public int Composites { get; private set; }

    /// <summary>Whether the composite may use more than one thread.</summary>
    /// <remarks>
    ///     What this governs is the fallback, because <see cref="Jobs" /> is the better answer to the
    ///     same question and a node that has one uses it. Without a scheduler the composite is
    ///     <c>Parallel.For</c> on the thread pool and the frame waits for it either way; with one it
    ///     is the job system, and every recomposite after the first does not make the frame wait at
    ///     all.
    /// </remarks>
    public bool Parallel { get; set; } = true;

    /// <summary>The job system the composite runs on, or null to run it inline.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what makes the recomposite deferrable, and it is the only thing that
    ///         does.</b> A composite is every cell of every level against every instance — the most
    ///         expensive thing in the frame — and the levels are snapped so that a camera crossing one
    ///         cell keeps about 97 per cent of what it already had. That means the answer this frame
    ///         and the answer next frame differ over a slab, and a frame that draws last refresh's
    ///         clipmap is drawing something very nearly right. Given a scheduler, that is what it
    ///         does: the refresh is scheduled one slice at a time into
    ///         <see cref="JobPriority.Background" />, the handle is <i>kept</i> rather than completed,
    ///         and the frame carries on with the clipmap it already has on the device.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first composite is not deferred, and is not scheduled into that tier.</b>
    ///         Before there is a clipmap there is nothing to draw instead: the volumes do not exist, so
    ///         <c>GlobalDistanceFieldTexture.Apply</c> names no handles, so the pass's set is filled
    ///         partially and every draw in it is refused. The frame genuinely is blocked on that one —
    ///         and <see cref="JobPriority.Background" /> on work the caller is blocked on is not a
    ///         no-op but a pessimisation, because the waiting thread drains every unrelated frame item
    ///         it can reach first. So the blocking composite goes in as
    ///         <see cref="JobPriority.Frame" /> and is completed at once, and only the refreshes
    ///         nobody is waiting for go in as background. The tier follows whether the caller waits,
    ///         which is the whole rule.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is not "no threads", it is "no deferral".</b> A node without one recomposites
    ///         inside <see cref="Record" /> exactly as it always did — which is what every test and
    ///         every tool that stands this node up on its own wants, and what the editor gets.
    ///     </para>
    /// </remarks>
    public JobScheduler? Jobs { get; set; }

    /// <summary>Whether a refresh has been scheduled and has not been swapped in yet.</summary>
    public bool IsRefreshing => refresh is not null;

    /// <summary>How many frames have been drawn against a clipmap a refresh was already out to replace.</summary>
    /// <remarks>
    ///     The number the deferral is actually worth, and the number that says how stale the picture
    ///     is allowed to get. Zero on a node with no <see cref="Jobs" />, always — that node's frames
    ///     wait instead.
    /// </remarks>
    public int Deferred { get; private set; }

    /// <summary>Declares the pass that composites the clipmap and copies it up.</summary>
    /// <param name="compositor">The compositor.</param>
    /// <param name="frame">The frame being built.</param>
    /// <exception cref="ArgumentNullException">There is no frame.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>A pass of its own, and it has to be one.</b> Uploading a clipmap is a
    ///         buffer-to-texture copy, and a copy cannot be recorded inside a render pass — which is
    ///         what <see cref="SceneRenderer.Record" /> runs inside, because the only thing that calls
    ///         it is a <see cref="RenderPassRenderer" /> that has already opened one. This node spent
    ///         its whole life so far unable to run in a real frame for that reason, and nothing
    ///         noticed until a frame tried. What buys the copy its place outside a render pass is
    ///         declaring no attachments; the <see cref="PassKind" /> has nothing to do with it.
    ///     </para>
    ///     <para>
    ///         <b>Marked as having a side effect, because the graph cannot see what it produces.</b> The
    ///         volumes are not graph resources — they belong to
    ///         <see cref="GlobalDistanceFieldTexture" /> and are named into a descriptor set rather than
    ///         read through the graph — so a pass that writes no graph resource reads as a pass nothing
    ///         needs, and would be culled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <see cref="PassKind.Graphics" /> rather than <see cref="PassKind.Transfer" />,
    ///         which it used to say.</b> Two reasons, either of which is enough. The body is not a
    ///         copy: <see cref="GlobalDistanceFieldTexture.Upload" /> brackets its copies with
    ///         barriers into <see cref="ResourceState.ShaderRead" />, and that state names the vertex,
    ///         fragment and compute stages — none of which a Vulkan transfer family supports, so the
    ///         barrier is invalid usage on a transfer queue rather than merely slow. And the volumes
    ///         being invisible to the graph means no wait edge could be derived either way, so a
    ///         hoisted pass would be one nothing waits for. See
    ///         <c>docs/guide/rendering/async-compute.md</c>.
    ///     </para>
    /// </remarks>
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Field is null) {
            return;
        }

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Graphics;
                pass.SideEffect();

                pass.Execute(
                    context => {
                        var draw = frame.Context(context.CommandList);

                        draw.View = View;
                        Record(compositor, draw);
                    }
                );
            }
        );
    }

    /// <inheritdoc />
    protected internal override void Record(GraphicsCompositor compositor, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Field is not { } field || (Device ?? context.Device) is not { } device) {
            return;
        }

        var centre = ViewPosition ?? context.View?.Position ?? Vector3.Zero;

        if (refresh is { } pending) {
            // ⚠ Asked, never waited on. `IsCompleted` is the whole difference between a consumer of
            // the background tier and a `ParallelFor` wearing its name: `Complete` here would block
            // the frame on work the tier had just told every worker to prefer nothing about, which
            // is slower than never having deferred it.
            if (Jobs is not { } asked || asked.IsCompleted(refreshHandle)) {
                refresh = null;

                try {
                    // Rethrows a slice that threw, on the frame thread, where the pass can report it —
                    // and is a no-op on a handle that has already finished.
                    Jobs?.Complete(refreshHandle);
                } catch {
                    // ⚠ Given back rather than left outstanding. There is one spare buffer per level,
                    // so a refresh nobody published or abandoned is a clipmap that can never start
                    // another one — the throw would arrive once and the refusal every frame after,
                    // which is a much worse failure than the one that happened.
                    pending.Abandon();

                    throw;
                }

                pending.Publish();
                Finish(field, device, context);
            } else {
                // This frame draws the clipmap the refresh is out to replace. That is the deferral,
                // and this is the only place it is visible from outside.
                Deferred++;
            }
        } else if (ShouldComposite(field, centre, out var moveOnly)) {
            // The snapped centre of the finest level, which is what decides whether a later frame has
            // anything to redo. Held rather than recorded, so that a composite that threw — or one
            // still running — does not leave the node believing it happened.
            refreshCentre = GlobalDistanceField.Snap(centre, field.CellSizeOf(0));

            if (Jobs is not { } jobs) {
                // Scrolling only where the camera moved and nothing else did. This node is the one
                // thing that knows the difference — it is what watches the instance version — and the
                // clipmap cannot work it out for itself without comparing every instance every frame.
                field.Update(centre, CollectionsMarshal.AsSpan(instances), Parallel, moveOnly);
                Finish(field, device, context);
            } else {
                var started = field.BeginUpdate(centre, CollectionsMarshal.AsSpan(instances), moveOnly);
                var slices = new CompositeSliceJob(started);

                // One slice per work item, because the tier defers work rather than interrupting it:
                // a refresh handed over as a single item would hold a worker for the whole composite
                // and the frame behind it would wait exactly as long as if it had never been
                // deferred.
                if (CanDefer) {
                    try {
                        refreshHandle = jobs.ScheduleParallel(
                            slices,
                            started.SliceCount,
                            batchSize: 1,
                            priority: JobPriority.Background
                        );
                    } catch {
                        started.Abandon();

                        throw;
                    }

                    // ⚠ After the schedule, never before. A refresh recorded against a handle the
                    // scheduling call never returned is one the next frame polls with the null
                    // handle — which reads as complete — and publishes a composite in which not one
                    // slice has run.
                    refresh = started;

                    // This frame, and every frame until it lands, draws the clipmap it replaces. No
                    // upload here for the same reason: the device holds the previous composite and
                    // the names written below describe that one. Both move together, at Publish.
                    Deferred++;
                } else {
                    // Nothing to draw instead, so the frame is blocked on this one — which is exactly
                    // the case the background tier must not be asked for. `Frame`, and completed at
                    // once. See the remarks on `Jobs`.
                    try {
                        jobs.ParallelFor(slices, started.SliceCount, batchSize: 1);
                    } catch {
                        started.Abandon();

                        throw;
                    }

                    started.Publish();
                    Finish(field, device, context);
                }
            }
        }

        if (SceneConstants is { } scene && Texture is not null) {
            Texture.Apply(scene.Parameters, ShaderName);

            foreach (var pass in Passes) {
                Texture.Apply(scene.Parameters, pass);
            }
        }
    }

    /// <summary>Waits for an outstanding refresh, running its slices on this thread while it waits.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>For the frame that cannot accept a stale clipmap</b> — a teleport, a level load, a
    ///         cut to a camera somewhere else. The deferral is worth having because a camera that
    ///         walks keeps 97 per cent of what it had; a camera that jumps keeps none of it, and the
    ///         previous composite is not nearly right, it is somewhere else entirely.
    ///     </para>
    ///     <para>
    ///         The refresh is swapped in and uploaded by the next <see cref="Record" />, not here:
    ///         this has no command list to copy with, and inventing one is how a node ends up
    ///         uploading outside the pass that declared it could.
    ///     </para>
    /// </remarks>
    public void WaitForRefresh() {
        if (refresh is null) {
            return;
        }

        Jobs?.Complete(refreshHandle);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // ⚠ Drained rather than abandoned, and before anything else goes. The slices write into the
        // clipmap's spare buffers, which belong to a field this node does not own and which whatever
        // outlives it may composite into again — so a node that walked away from work still running
        // would leave a second composite writing the buffer the first one is in the middle of.
        if (refresh is { } pending) {
            refresh = null;

            try {
                Jobs?.Complete(refreshHandle);
            } catch (JobExecutionException) {
                // A slice that threw has nowhere to be reported to from here, and Dispose that
                // throws loses whatever the caller was disposing after this. It has already been
                // recorded in the scheduler's failure log.
            }

            pending.Abandon();
        }

        Texture?.Dispose();
        Texture = null;
    }

    /// <summary>Whether there is something on the device good enough to draw one more frame with.</summary>
    /// <remarks>
    ///     ⚠ All three, and the third is the one that is easy to leave out: a node that has
    ///     composited and whose field has content can still have nothing <i>on the device</i>, and
    ///     <c>GlobalDistanceFieldTexture.Apply</c> then names no volume — which the set writer counts
    ///     as an unfilled binding and refuses every draw in the pass for, silently.
    /// </remarks>
    bool CanDefer => composited && Field is { HasContent: true } && Texture is { Uploads: > 0 };

    /// <summary>Records that a composite landed, and copies it up.</summary>
    /// <param name="field">The clipmap.</param>
    /// <param name="device">The device to upload to.</param>
    /// <param name="context">The frame, for its command list.</param>
    void Finish(GlobalDistanceField field, IGraphicsDevice device, RenderDrawContext context) {
        lastCentre = refreshCentre;
        composited = true;
        Composites++;

        Texture ??= new(field);
        Texture.Upload(device, context.CommandList);
    }

    /// <summary>One Z slice of one level of one refresh.</summary>
    /// <param name="refresh">The refresh.</param>
    /// <remarks>
    ///     A struct holding one reference, so the scheduler's copy of it costs a pointer and nothing
    ///     on the path from scheduling to running allocates. The indices are the refresh's own —
    ///     <c>index / Resolution</c> is the level and <c>index % Resolution</c> the slice — and every
    ///     one of them writes only its own cells of its own level's spare buffer.
    /// </remarks>
    readonly struct CompositeSliceJob(ClipmapRefresh refresh) : IJobParallelFor {
        /// <inheritdoc />
        public void Execute(int index) => refresh.Composite(index);
    }

    /// <summary>Whether anything about the clipmap would come out different this frame.</summary>
    /// <param name="field">The clipmap.</param>
    /// <param name="centre">Where it would be centred.</param>
    /// <param name="moveOnly">Whether the camera is the only thing that changed, so a scroll is safe.</param>
    /// <returns>Whether to redo it.</returns>
    /// <remarks>
    ///     The finest level's snap is the test, because it is the one with the smallest cell: a
    ///     movement too small to move level zero is too small to move any of them.
    /// </remarks>
    bool ShouldComposite(GlobalDistanceField field, Vector3 centre, out bool moveOnly) {
        moveOnly = false;

        if (!composited || !field.HasContent) {
            return true;
        }

        if (InstancesVersion != lastVersion) {
            lastVersion = InstancesVersion;

            return true;
        }

        moveOnly = true;

        return GlobalDistanceField.Snap(centre, field.CellSizeOf(0)) != lastCentre;
    }
}
