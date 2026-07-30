// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>One band of a refinement policy: how near counts, and how fine to go there.</summary>
/// <param name="Margin">How far outside a renderer's bounds this band reaches, in world units.</param>
/// <param name="Size">The brick size to refine to inside it, in finest cells.</param>
/// <remarks>
///     A margin rather than a distance from the camera, because what wants resolution is the
///     neighbourhood of <i>geometry</i> — a wall leaks at its own surface whether the camera is a
///     metre away or fifty. Camera distance is a budget question and belongs to whoever picks the
///     bands, not to the bands themselves.
/// </remarks>
public readonly record struct IrradianceRefinementBand(float Margin, int Size);

/// <summary>Decides where an irradiance field should be fine — doc 19 § 3's "from renderer bounds".</summary>
/// <remarks>
///     <para>
///         <b><see cref="IrradianceField.Refine" /> could always do this and nothing ever called it.</b>
///         A field allocated at one size everywhere spends its pool on empty air and is too coarse
///         exactly where it matters, which is the neighbourhood of a surface: doc 19's risk G3 is that
///         light leaks through walls, and the knob is how thick a wall is <i>in probes</i>. Refinement
///         is what makes a wall thicker in probes without making the whole field finer.
///     </para>
///     <para>
///         <b>Bands, coarsest first, and the order is what grades the field.</b>
///         <see cref="IrradianceField.Refine" /> only splits bricks <i>larger</i> than its target, so a
///         wide band at size four followed by a narrow one at size one leaves three resolutions
///         standing: fine against the surface, medium around it, coarse in the air. Applying them the
///         other way round would refine everything to one and the wide band would find nothing to do.
///     </para>
///     <para>
///         <b>It coarsens what nothing protects, when told to.</b> Refinement only ever adds detail,
///         so a field driven by bands alone over a long session ratchets toward its finest everywhere
///         the geometry has ever been — wrong for a streamed scene, whose pool never gets slots back.
///         With <see cref="CoarsenTo" /> set, every aligned group of bricks finer than it whose merged
///         extent no band still claims at a finer size is merged back, probes subsampled rather than
///         discarded. A region a band <i>does</i> claim is protected — including the regions this very
///         call refined, which is what keeps one <see cref="Apply" /> from undoing itself — and
///         <see cref="CoarsenMargin" /> widens every claim, so a box teetering on a brick boundary
///         does not split and merge the same octet on alternating frames.
///     </para>
///     <para>
///         <b>Running out of pool is not an error.</b> <see cref="IrradianceField.Split" /> skips a
///         child it cannot allocate, so a policy that asks for more than the pool holds produces a
///         field that is fine where it got to and coarse after — a quality reduction rather than a
///         failure, which is what doc 19 § 7 says a fixed pool is for.
///     </para>
/// </remarks>
public sealed class IrradianceRefinementPolicy {
    /// <summary>The bands, applied coarsest first whatever order they are given in.</summary>
    /// <remarks>
    ///     Sorted on use rather than on insert, so a caller can build the list in whatever order reads
    ///     best and cannot get the grading backwards by doing so.
    /// </remarks>
    public IList<IrradianceRefinementBand> Bands { get; } = [];

    /// <summary>How many bricks the last <see cref="Apply" /> produced.</summary>
    /// <remarks>
    ///     Zero on a field already fine enough, which is the steady state — a policy that keeps
    ///     producing bricks every frame is one whose bands disagree with the pool, and that shows up
    ///     here rather than as a stutter nobody can attribute.
    /// </remarks>
    public int Refined { get; private set; }

    /// <summary>The size bricks relax back to where no band claims them finer. Zero never coarsens.</summary>
    /// <remarks>
    ///     The field's own baseline — what <see cref="IrradianceField.AllocateAll" /> was called with —
    ///     is the number that belongs here: coarser would merge past the allocation nothing refined,
    ///     and finer leaves part of the ratchet in place. A power of two, like every brick size.
    /// </remarks>
    public int CoarsenTo { get; set; }

    /// <summary>Extra width every band keeps while deciding what may coarsen, in world units.</summary>
    /// <remarks>
    ///     The hysteresis: a box sitting exactly on a brick boundary refines the brick when it edges
    ///     in and would merge it back the frame it edges out, splitting and merging the same octet
    ///     forever. Any width above the largest per-frame movement of a bound ends that; the cost is
    ///     detail lingering that much farther from departed geometry.
    /// </remarks>
    public float CoarsenMargin { get; set; }

    /// <summary>How many merges the last <see cref="Apply" /> performed.</summary>
    /// <remarks>
    ///     The other side of <see cref="Refined" />'s steady state: a number that never settles while
    ///     the scene stands still means a band and the coarsening disagree about a region, and the
    ///     field is thrashing between two answers instead of holding either.
    /// </remarks>
    public int Coarsened { get; private set; }

    /// <summary>Refines a field around a set of renderer bounds.</summary>
    /// <param name="field">The field to refine.</param>
    /// <param name="bounds">Where the geometry is. Empty refines nothing.</param>
    /// <returns>How many bricks were made.</returns>
    /// <exception cref="ArgumentNullException">There is no field or no bounds.</exception>
    /// <remarks>
    ///     Idempotent: a second call over the same bounds makes nothing, because every brick that
    ///     overlaps a band is already at or below that band's size and <c>Refine</c> splits only what
    ///     is larger.
    /// </remarks>
    public int Apply(IrradianceField field, IReadOnlyList<BoundingBox> bounds) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(bounds);

        Refined = 0;
        Coarsened = 0;

        if (Bands.Count > 0 && bounds.Count > 0) {
            var ordered = new List<IrradianceRefinementBand>(Bands);

            ordered.Sort(static (left, right) => right.Size.CompareTo(left.Size));

            foreach (var band in ordered) {
                if (band.Size <= 0) {
                    continue;
                }

                foreach (var box in bounds) {
                    Refined += field.Refine(Expanded(box, band.Margin), band.Size);
                }
            }
        }

        // After refinement, never instead of it: what was just claimed is what must not merge.
        // And deliberately not gated on `bounds` being empty — a scene whose geometry left
        // entirely is exactly the scene that owes every slot back.
        if (CoarsenTo > 1) {
            Coarsen(field, bounds);
        }

        return Refined;
    }

    /// <summary>Merges every unclaimed group finer than <see cref="CoarsenTo" />, finest first.</summary>
    /// <remarks>
    ///     Rounds, because a merge enables the next: four singles become a two this round and a two
    ///     becomes a four the next. The snapshot per round is deliberate — <see cref="IrradianceField.TryMerge" />
    ///     revalidates everything, so an entry the previous merge invalidated just refuses.
    /// </remarks>
    void Coarsen(IrradianceField field, IReadOnlyList<BoundingBox> bounds) {
        var candidates = new List<IrradianceBrick>();

        while (true) {
            candidates.Clear();

            foreach (var brick in field.Bricks) {
                if (brick.Size * 2 <= CoarsenTo) {
                    candidates.Add(brick);
                }
            }

            candidates.Sort(static (left, right) => left.Size.CompareTo(right.Size));

            var merges = 0;

            foreach (var brick in candidates) {
                // A merge earlier in this pass may have consumed the candidate. Skipping the stale
                // entry is load-bearing, not tidy: the brick standing at its cell now is coarser,
                // and calling through it would merge one level past what the snapshot vetted —
                // stale singles once escalated a corner octet into a brick the size of the field.
                if (!field.Indirection.TryBrick(brick.Cell, out var live)
                    || live.Cell != brick.Cell
                    || live.Size != brick.Size) {
                    continue;
                }

                var merged = brick.Size * 2;
                var origin = new Int3(
                    brick.Cell.X / merged * merged,
                    brick.Cell.Y / merged * merged,
                    brick.Cell.Z / merged * merged
                );

                if (Claimed(field, origin, merged, bounds) || !field.TryMerge(origin)) {
                    continue;
                }

                merges++;
            }

            if (merges == 0) {
                return;
            }

            Coarsened += merges;
        }
    }

    /// <summary>Whether any band still wants somewhere in a would-be brick finer than it would be.</summary>
    bool Claimed(IrradianceField field, Int3 origin, int size, IReadOnlyList<BoundingBox> bounds) {
        var extent = field.BrickBounds(new(0, origin, size));

        foreach (var band in Bands) {
            if (band.Size <= 0 || band.Size >= size) {
                continue;
            }

            foreach (var box in bounds) {
                if (Expanded(box, band.Margin + CoarsenMargin).Intersects(extent)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One box grown by a margin on every side.</summary>
    /// <remarks>
    ///     Not clamped to the field. <see cref="IrradianceField.Refine" /> asks each brick whether it
    ///     intersects, and a brick outside the region is not split however far past the field the
    ///     region reaches — so clamping would be arithmetic with no effect on the answer.
    /// </remarks>
    static BoundingBox Expanded(BoundingBox box, float margin) {
        var grown = new Vector3(Math.Max(0f, margin));

        return new(box.Minimum - grown, box.Maximum + grown);
    }
}
