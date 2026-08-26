// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>Where one level of a refresh is going, and out of which buffer it may copy.</summary>
/// <param name="Slot">The half of the double buffer being written.</param>
/// <param name="Previous">The half being read — the level a frame is still uploading and sampling.</param>
/// <param name="Origin">The level's first cell, in whole cells of the global grid.</param>
/// <param name="Shift">How far the level moved since <paramref name="Previous" />, in cells.</param>
/// <param name="Reuse">Whether cells inside the overlap may be copied rather than recomputed.</param>
/// <param name="Maximum">The furthest this level is willing to claim anything is.</param>
readonly record struct LevelPlan(
    MeshDistanceField Slot,
    MeshDistanceField Previous,
    Vector3 Origin,
    Int3 Shift,
    bool Reuse,
    float Maximum
);

/// <summary>A recomposite of a <see cref="GlobalDistanceField" />, in pieces somebody else runs.</summary>
/// <remarks>
///     <para>
///         <b>The composite is the most expensive thing in the frame and about 97 per cent of it is
///         usually stale.</b> A camera that crossed one cell of a 32³ level keeps all but a slab of
///         it, and the levels are snapped to their own grids precisely so that is true. What that
///         buys is only realisable by a caller that does not have to wait: this is the shape that
///         lets one exist.
///     </para>
///     <para>
///         <b>One slice per index, and that is the point rather than an implementation detail.</b>
///         The job scheduler's background tier defers work; it cannot interrupt it. A refresh handed
///         over as one item would occupy a worker for the whole composite whatever tier it was in,
///         and the frame behind it would wait exactly as long. <see cref="SliceCount" /> short items
///         is what makes "later" mean anything — see
///         <c>docs/guide/core/job-priorities.md</c>.
///     </para>
///     <para>
///         ⚠ <b>Nothing a reader of the clipmap can see moves until <see cref="Publish" />.</b> The
///         slices write the other half of each level's double buffer, so a frame may upload, sample
///         and name the previous composite for as many frames as the refresh takes. That is the
///         staleness the caller is buying, and it is a whole refresh of it rather than a torn one.
///     </para>
/// </remarks>
public sealed class ClipmapRefresh {
    readonly GlobalDistanceField field;
    readonly DistanceFieldInstance[] instances;
    readonly BoundingBox[] bounds;
    readonly LevelPlan[] plans;
    readonly Vector3 viewPosition;
    readonly long reused;

    internal ClipmapRefresh(
        GlobalDistanceField field,
        Vector3 viewPosition,
        DistanceFieldInstance[] instances,
        BoundingBox[] bounds,
        bool scroll
    ) {
        this.field = field;
        this.viewPosition = viewPosition;
        this.instances = instances;
        this.bounds = bounds;

        plans = new LevelPlan[field.LevelCount];

        for (var level = 0; level < plans.Length; level++) {
            plans[level] = field.PlanLevel(level, viewPosition, scroll);

            if (plans[level].Reuse) {
                reused += field.Kept(plans[level].Shift);
            }
        }

        SliceCount = plans.Length * field.Resolution;
    }

    /// <summary>How many pieces this is in — one Z slice of one level each.</summary>
    public int SliceCount { get; }

    /// <summary>How many cells this refresh will keep rather than recompute.</summary>
    /// <remarks>
    ///     Known before a single slice has run, because it follows from the shifts alone. It does not
    ///     reach <see cref="GlobalDistanceField.Reused" /> until <see cref="Publish" />, for the same
    ///     reason nothing else does.
    /// </remarks>
    public long Reused => reused;

    /// <summary>Whether this refresh has been swapped in.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>Runs one slice.</summary>
    /// <param name="index">Which, in <c>[0, SliceCount)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is not one of this refresh's.</exception>
    /// <remarks>
    ///     Safe to call from as many threads at once as there are indices: every slice writes its own
    ///     cells of its own level's spare buffer and reads only the previous composite, the instance
    ///     copy and the plan.
    /// </remarks>
    public void Composite(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SliceCount);

        var resolution = field.Resolution;

        field.CompositeSlice(in plans[index / resolution], index % resolution, instances, bounds);
    }

    /// <summary>Makes this refresh the clipmap, in one step.</summary>
    /// <exception cref="InvalidOperationException">It has already been published, or is not the clipmap's.</exception>
    /// <remarks>
    ///     ⚠ <b>Only when every slice has run.</b> Nothing here checks, because the check would be a
    ///     counter every slice contended over for a mistake the caller cannot make by accident — the
    ///     handle that says the work is done is what a scheduler already hands back. Publishing early
    ///     swaps in a level that is half this composite and half the one two frames ago.
    /// </remarks>
    public void Publish() {
        if (IsPublished) {
            throw new InvalidOperationException("This refresh has already been published.");
        }

        IsPublished = true;
        field.PublishRefresh(this, plans, viewPosition, reused);
    }

    /// <summary>Throws this refresh away, leaving the clipmap as it was.</summary>
    /// <remarks>
    ///     <para>
    ///         For a caller that has decided the answer is no longer wanted — a teleport, a level
    ///         unload. The clipmap keeps the composite it already had; the spare buffers keep whatever
    ///         the abandoned slices wrote, which costs nothing because nothing reads a buffer it did
    ///         not fill.
    /// </para>
    ///     <para>
    ///         ⚠ <b>Only once the slices have stopped running.</b> This does not wait for them, and
    ///         the next refresh plans into the same spare buffers — so abandoning work that is still
    ///         in flight is two composites writing one buffer, which is the one thing the double
    ///         buffer exists to prevent. Complete the handle first.
    ///     </para>
    /// </remarks>
    public void Abandon() {
        IsPublished = true;
        field.AbandonRefresh(this);
    }
}
