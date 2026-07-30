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
///         <b>It only ever refines.</b> Nothing here merges bricks back when geometry moves away, so a
///         field driven by this over a long session ratchets toward its finest everywhere the geometry
///         has ever been. That is honest for a static or slowly-changing scene and wrong for a streamed
///         one; coarsening needs the pool to give slots back and a policy for when, and neither exists.
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

        if (Bands.Count == 0 || bounds.Count == 0) {
            return 0;
        }

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

        return Refined;
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
