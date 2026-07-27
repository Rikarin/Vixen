// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Memory;
using Vixen.Core.Threading;

namespace Vixen.Rendering;

/// <summary>
///     Which objects each view can see, as one bit per object per view.
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
/// </remarks>
public sealed class VisibilityGroup : IDisposable {
    readonly List<NativeArray<ulong>> perView = [];
    int wordsPerView;
    bool disposed;

    /// <summary>How many views have results.</summary>
    public int ViewCount => perView.Count;

    /// <summary>Whether an object is visible in a view.</summary>
    public bool IsVisible(int viewIndex, RenderObjectId id) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (viewIndex < 0 || viewIndex >= perView.Count || id.Index < 0) {
            return false;
        }

        var word = id.Index >> 6;
        return word < perView[viewIndex].Length && (perView[viewIndex][word] & (1UL << (id.Index & 63))) != 0;
    }

    /// <summary>The raw visibility words for a view, for a consumer that walks them itself.</summary>
    public ReadOnlySpan<ulong> Words(int viewIndex) =>
        viewIndex >= 0 && viewIndex < perView.Count ? perView[viewIndex].AsSpan() : default;

    /// <summary>How many objects are visible in a view.</summary>
    public int VisibleCount(int viewIndex) {
        var total = 0;

        foreach (var word in Words(viewIndex)) {
            total += System.Numerics.BitOperations.PopCount(word);
        }

        return total;
    }

    /// <summary>
    ///     Tests every object against every view, in parallel over the objects.
    /// </summary>
    /// <remarks>
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

        var job = new CullJob(store, views, perView, wordsPerView);

        if (scheduler is null) {
            for (var word = 0; word < wordsPerView; word++) {
                job.Execute(word);
            }

            return;
        }

        // One index per word, so the unit of work is the unit of ownership.
        scheduler.ParallelFor(job, wordsPerView, BatchWords);
    }

    /// <summary>How many 64-object words one job batch covers.</summary>
    /// <remarks>
    ///     Four words is 256 objects — enough that the scheduling overhead is amortised, small enough
    ///     that a scene of a few thousand objects still spreads over every core.
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
