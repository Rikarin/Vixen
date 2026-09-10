// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Memory;
using Vixen.Core.Threading;

namespace Vixen.Rendering;

/// <summary>
///     Which objects each view can see, as one bit per object per view, decided on the CPU.
/// </summary>
/// <remarks>
///     <para>
///         Bits rather than a list of visible ids, and the reason is what happens next: every stage
///         of every view walks this, and a bitset lets that walk be a word at a time while a list
///         would be a pointer chase per object. It also makes the result a fixed size, so the
///         culling job writes into memory it did not have to allocate.
///     </para>
///     <para>
///         <strong>Culling is per view, not per view per stage.</strong> An object either is or is
///         not inside a frustum, and asking again for each stage would be the same arithmetic
///         repeated — the stage mask filters afterwards, when the work list is built, where it is an
///         <c>and</c> on a value already loaded.
///     </para>
///     <para>
///         The default implementation of <see cref="IVisibilityGroup" />, and the one that runs
///         everywhere: it needs no device, no compute support and no shader, which is why it is what
///         a GL or WebGL target uses and what <see cref="GpuVisibilityGroup" /> falls back to.
///     </para>
/// </remarks>
public sealed class VisibilityGroup : IVisibilityGroup {
    readonly List<NativeArray<ulong>> perView = [];
    int wordsPerView;
    bool disposed;

    /// <inheritdoc />
    public int ViewCount => perView.Count;

    /// <inheritdoc />
    public bool IsVisible(int viewIndex, RenderObjectId id) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (viewIndex < 0 || viewIndex >= perView.Count || id.Index < 0) {
            return false;
        }

        var word = id.Index >> 6;
        return word < perView[viewIndex].Length && (perView[viewIndex][word] & (1UL << (id.Index & 63))) != 0;
    }

    /// <inheritdoc />
    public ReadOnlySpan<ulong> Words(int viewIndex) =>
        viewIndex >= 0 && viewIndex < perView.Count ? perView[viewIndex].AsSpan() : default;

    /// <inheritdoc />
    public void Hide(int viewIndex, RenderObjectId id) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (viewIndex < 0 || viewIndex >= perView.Count || id.Index < 0) {
            return;
        }

        var word = id.Index >> 6;

        if (word < perView[viewIndex].Length) {
            perView[viewIndex][word] &= ~(1UL << (id.Index & 63));
        }
    }

    /// <inheritdoc />
    public int VisibleCount(int viewIndex) {
        var total = 0;

        foreach (var word in Words(viewIndex)) {
            total += System.Numerics.BitOperations.PopCount(word);
        }

        return total;
    }

    /// <inheritdoc cref="IVisibilityGroup.Cull" />
    /// <remarks>
    ///     <para>
    ///         Tests every object against every view, in parallel over the objects.
    ///     </para>
    ///     <para>
    ///         Parallel over objects rather than over views, which is the partitioning that stays
    ///         balanced: a frame typically has a handful of views and tens of thousands of objects,
    ///         so splitting by view leaves most threads idle and splitting by object does not.
    ///     </para>
    ///     <para>
    ///         Each object owns one bit in each view's set, and threads take disjoint object ranges —
    ///         but a <c>ulong</c> holds 64 objects' bits, so two threads writing neighbouring objects
    ///         would race on one word. The batch size is therefore a multiple of 64: every thread
    ///         owns whole words, and no lock or atomic is needed for any of it.
    ///     </para>
    /// </remarks>
    public void Cull(RenderObjectStore store, IReadOnlyList<RenderView> views, JobScheduler? scheduler = null) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(views);
        ObjectDisposedException.ThrowIf(disposed, this);

        EnsureCapacity(views.Count, store.Count);

        foreach (var set in perView) {
            set.AsSpan().Clear();
        }

        if (store.Count == 0 || views.Count == 0) {
            return;
        }

        // ⚠ The live words, not the allocated ones. `wordsPerView` is a high-water mark that doubles
        // and never shrinks, so a scene of ten objects used to cull over eight words — 512 object
        // slots — and a scene that once held 40 000 and now holds 100 kept paying for 40 000 for the
        // rest of the process. Every word past this one writes zero into an array `Cull` has already
        // cleared, so narrowing to it changes no bit and never has.
        var words = (store.Count + 63) >> 6;
        var job = new CullJob(store, views, perView, words);

        LastCulledWords = words;
        LastCullWasParallel = scheduler is not null && words > BatchWords;

        if (!LastCullWasParallel) {
            for (var word = 0; word < words; word++) {
                job.Execute(word);
            }

            return;
        }

        // One index per word, so the unit of work is the unit of ownership.
        scheduler!.ParallelFor(job, words, BatchWords);
    }

    /// <summary>How many 64-object words the last <see cref="Cull" /> covered.</summary>
    /// <remarks>
    ///     The live count rather than the allocated one, which is the whole of what
    ///     <see cref="Cull" /> narrowed: the two differ by the high-water mark of every scene this
    ///     group has ever held. Internal because it exists to be asserted on — nothing about the
    ///     frame reads it — and a diagnostic overlay that wanted it would be the reason to promote it.
    /// </remarks>
    internal int LastCulledWords { get; private set; }

    /// <summary>Whether the last <see cref="Cull" /> went to the job system.</summary>
    /// <remarks>
    ///     <c>VfxSystem.LastStepWasParallel</c> and <c>GoapPlanQueue.LastLanes</c> in the same shape,
    ///     and for the same reason: which side of a threshold a call fell on is otherwise invisible,
    ///     and a threshold nothing can observe is one nobody can measure.
    /// </remarks>
    internal bool LastCullWasParallel { get; private set; }

    /// <summary>
    ///     Makes room for a frame and clears it, for a producer that is not <see cref="Cull" />.
    /// </summary>
    /// <param name="viewCount">How many views the frame has.</param>
    /// <param name="objectCount">How many object slots the store holds.</param>
    /// <remarks>
    ///     This and <see cref="Fill" /> are the two halves of one seam, and it exists for exactly one
    ///     caller: <see cref="GpuVisibilityGroup" /> computes the same bits on the device and needs
    ///     somewhere to put them that every consumer already knows how to read. Sharing the storage
    ///     rather than duplicating it is also what makes <see cref="Hide" />, <see cref="Words" /> and
    ///     the rest identical between the two paths instead of merely similar.
    /// </remarks>
    internal void Reset(int viewCount, int objectCount) {
        ObjectDisposedException.ThrowIf(disposed, this);

        EnsureCapacity(viewCount, objectCount);

        foreach (var set in perView) {
            set.AsSpan().Clear();
        }
    }

    /// <summary>One view's words, to be written into. Valid until the next <see cref="Reset" />.</summary>
    /// <param name="viewIndex">Which view.</param>
    internal Span<ulong> Fill(int viewIndex) {
        ObjectDisposedException.ThrowIf(disposed, this);
        return viewIndex >= 0 && viewIndex < perView.Count ? perView[viewIndex].AsSpan() : default;
    }

    /// <summary>How many 64-object words one job batch covers.</summary>
    /// <remarks>
    ///     <para>
    ///         Four words is 256 objects — enough that the scheduling overhead is amortised, small
    ///         enough that a scene of a few thousand objects still spreads over every core.
    ///     </para>
    ///     <para>
    ///         It is also the threshold: a frame whose live words fit in one batch has nothing to
    ///         spread, so it runs inline rather than renting a slot, publishing a handle and
    ///         completing it to run the one batch. <c>GoapPlanQueue</c>'s <c>lanes > 1</c> is the
    ///         same rule with a different unit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is not the measured crossover, and it deliberately does not claim to be.</b>
    ///         What it rules out is the case that cannot pay off under any measurement — one batch of
    ///         work put through the scheduler — and #1206 asks for the real figure, which depends on
    ///         the cost of sixty-four frustum tests against every view in the frame and has to be
    ///         taken on an idle machine. ⚠ Note also what the original report got wrong: the calling
    ///         thread does <em>not</em> sit idle while one worker culls. <c>JobScheduler.Complete</c>
    ///         executes ready work while it waits, so a single-batch dispatch is scheduling overhead
    ///         rather than a serialised frame — smaller than it looked, and still pure loss.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And what that measurement must not conclude.</b> One index is sixty-four objects
    ///         against <em>every</em> view, so the frame's work is words × views while the threshold
    ///         above counts words alone — which reads like an omission and is not one: views multiply
    ///         the work inside an index without producing a second batch to spread, so a frame of one
    ///         batch has nothing to spread however many views it has, and the rule is view-independent
    ///         exactly as written. What views do reach is the <i>batch size</i> they share a constant
    ///         with: at eight views a single word is already 512 frustum tests, so the batch that
    ///         amortises a dispatch is smaller than the batch that does at one view, and a scene of
    ///         four words could then spread where today it cannot. So the figure #1206 asks for is a
    ///         crossover in object-view tests and a batch derived from the view count — not one
    ///         number in words — and neither half is inventable without the machine.
    ///     </para>
    /// </remarks>
    const int BatchWords = 4;

    void EnsureCapacity(int viewCount, int objectCount) {
        var words = (objectCount + 63) >> 6;

        if (words > wordsPerView) {
            wordsPerView = Math.Max(words, Math.Max(wordsPerView * 2, 8));

            for (var i = 0; i < perView.Count; i++) {
                perView[i].Dispose();
                perView[i] = NativeArray<ulong>.Zeroed(wordsPerView, name: "Visibility");
            }
        }

        while (perView.Count < viewCount) {
            perView.Add(NativeArray<ulong>.Zeroed(Math.Max(wordsPerView, 8), name: "Visibility"));
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in perView) {
            set.Dispose();
        }

        perView.Clear();
        wordsPerView = 0;
    }

    /// <summary>Tests the 64 objects of one word against every view.</summary>
    readonly struct CullJob(
        RenderObjectStore store,
        IReadOnlyList<RenderView> views,
        List<NativeArray<ulong>> results,
        int wordCount
    ) : IJobParallelFor {
        public void Execute(int word) {
            if (word >= wordCount) {
                return;
            }

            var first = word << 6;
            var last = Math.Min(first + 64, store.Count);
            var objects = store.All;

            for (var viewIndex = 0; viewIndex < views.Count; viewIndex++) {
                var view = views[viewIndex];
                var frustum = view.Frustum;
                var stages = view.Stages;
                var maximum = view.MaximumDistance;
                var position = view.Position;
                var bits = 0UL;

                for (var i = first; i < last; i++) {
                    ref readonly var candidate = ref objects[i];

                    // Three rejections before any geometry: dead, in no stage this view draws, and
                    // beyond the view's own distance. Each is cheaper than the frustum test and each
                    // removes objects the frustum would have accepted.
                    if (!candidate.IsAlive || !candidate.Stages.Intersects(stages)) {
                        continue;
                    }

                    if (maximum > 0f && !WithinDistance(position, candidate.Bounds, maximum)) {
                        continue;
                    }

                    if (frustum.Contains(candidate.Bounds) == ContainmentType.Disjoint) {
                        continue;
                    }

                    bits |= 1UL << (i - first);
                }

                results[viewIndex][word] = bits;
            }
        }

        /// <summary>Whether a sphere reaches within <paramref name="maximum" /> of a point.</summary>
        /// <remarks>
        ///     Measured to the sphere's <em>surface</em>, not its centre, so a large object does not
        ///     pop out while it is still visibly on screen. Squared, because a square root here is
        ///     paid once per object per view.
        /// </remarks>
        static bool WithinDistance(Vector3 from, in BoundingSphere bounds, float maximum) {
            var reach = maximum + bounds.Radius;
            var offset = bounds.Center - from;
            return Vector3.Dot(offset, offset) <= reach * reach;
        }
    }
}
