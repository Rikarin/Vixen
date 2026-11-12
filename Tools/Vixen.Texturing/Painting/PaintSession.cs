// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Authoring;
using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>What a paint session writes into: one layer's pixels and its atlas.</summary>
/// <param name="Layer">The painted layer's pixels, written in place.</param>
/// <param name="Coverage">Which texels of the atlas belong to an island.</param>
/// <param name="Gutter">
///     How far a stamp is dilated past an island's edge, in texels. ⚠ It should be at least the
///     gutter the atlas was packed with: a dilation shorter than the packer's spacing leaves the
///     outermost gutter texels unpainted, which is the hairline at a higher mip rather than at mip 1.
/// </param>
public sealed record PaintTarget(PaintImage Layer, PaintCoverage Coverage, int Gutter = 4);

/// <summary>
///     One drag, from pointer-down to pointer-up: the seam a viewport drives painting through.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             ⚠ This is the whole of what a surface has to call, and saying so precisely is half of
///             what doc 48 § M9 owes.
///         </b>
///         Doc 48 § D13's two front ends are viewports and this slice is
///         deliberately not one: <c>PaintUvView</c> is the 2D one, and <see cref="PaintProjector" />
///         is what a 3D one drives — though nothing shows a stack's model yet, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a>. What each of them has to
///         do is exactly three things:
///     </para>
///     <list type="number">
///         <item>
///             <b>Turn a pointer position into a position in texels.</b> A 2D UV view already has
///             one: the cursor, in the image control's own coordinates, scaled by the zoom. A 3D
///             view casts a ray at the mesh, takes the hit's barycentric UV, and multiplies by the
///             atlas size — the same pick <c>TerrainPick</c> does for a heightfield, against a mesh.
///         </item>
///         <item>
///             <b>Turn a screen-space brush size into a radius in texels.</b> ⚠ The one conversion
///             that has no counterpart in the terrain tool, because a heightfield's samples per metre
///             is a constant and an atlas's texels per metre is not. A 2D view's is the zoom;
///             <see cref="PaintFootprint" /> is the 3D one. ⚠
///             <b>
///                 This paragraph used to say the
///                 answer was <c>UvDensity</c>'s and it is not
///             </b>
///             — that type answers texels per square
///             unit <em>per chart</em>, so a chart stretched at one end has one number and two
///             answers, and it says nothing at all about the two conversions in front of it: the
///             camera's projection at the hit's depth, and the grazing angle. The density that is
///             wanted is the hit triangle's own Jacobian, <see cref="PaintProjection.Density" />.
///         </item>
///         <item>
///             <b>Supply the mirrors.</b> ⚠
///             <b>
///                 Planar symmetry cannot be computed here and finding
///                 that out is a result rather than a gap.
///             </b>
///             A plane mirrors a point in <em>object</em>
///             space; the mirrored point lands on a different triangle, which is in a different UV
///             island, at an unrelated place in the atlas. There is no transform of the atlas that
///             performs it. So the surface — which is the only thing holding the mesh — picks the
///             mirrored ray's hit and hands over its UV, and <see cref="MoveAll(System.ReadOnlySpan{Vector2})" /> takes a
///             <em>set</em> of positions for that reason.
///         </item>
///     </list>
///     <para>
///         Everything else is here: spacing, jitter, the lazy mouse, the dilation and the single undo
///         entry. A curve or path stroke is <see cref="MoveAll(System.ReadOnlySpan{Vector2})" /> called along the
///         curve, which is why doc 48 § D13 says it does not touch the kernel.
///     </para>
///     <para>
///         ⚠ <b>A session writes a layer and composites nothing.</b> What a host shows under the brush
///         is the plan's own output, repaired over the tiles this dirtied: <c>LayerStackEvaluator</c>
///         watches the image the session writes. There is no arithmetic here to disagree with it.
///     </para>
/// </remarks>
public sealed class PaintSession {
    readonly PaintTarget target;
    readonly List<PaintStroke> strokes = [];

    /// <summary>Each stamp's own rectangle, for the move being processed. Reused, never handed out.</summary>
    readonly List<PaintRect> regions = [];

    readonly PaintBrush brush;
    readonly uint colour;
    readonly float lazy;
    readonly uint seed;

    /// <summary>What the drag found before any of its paths painted.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         One snapshot for every path, because two paths that overlap in the atlas record each
    ///         other's paint.
    ///     </b>
    ///     Undoing per-texel records in any order left some of it behind, and four
    ///     texels of a mirror used to survive their own undo; a table of tiles cannot.
    /// </remarks>
    PaintImageSnapshot? origin;

    PaintSession(PaintTarget target, PaintBrush brush, uint colour, float lazy, uint seed) {
        this.target = target;
        this.brush = brush;
        this.colour = colour;
        this.lazy = lazy;
        this.seed = seed;
    }

    /// <summary>How many strokes the drag has — one, plus one per mirror.</summary>
    public int Strokes => strokes.Count;

    /// <summary>How many stamps the drag has laid down, mirrors included.</summary>
    public int StampCount {
        get {
            var total = 0;

            foreach (var stroke in strokes) {
                total += stroke.StampCount;
            }

            return total;
        }
    }

    /// <summary>How many texel weights the drag has evaluated, mirrors included.</summary>
    /// <remarks>
    ///     ⚠ <b>Half of exit criterion 8's work and not all of it</b> — see
    ///     <see cref="TexelsScanned" />, which is the one to gate on. A weight is evaluated only
    ///     inside a stamp's footprint and only on a covered texel, so this is the number that stays
    ///     comparable across atlas sizes and layer counts, and it is the wrong number to call the
    ///     cost. See <see cref="PaintStroke.WeightsEvaluated" /> for why either is a counter rather
    ///     than a stopwatch.
    /// </remarks>
    public long WeightsEvaluated {
        get {
            var total = 0L;

            foreach (var stroke in strokes) {
                total += stroke.WeightsEvaluated;
            }

            return total;
        }
    }

    /// <summary>How many texels the drag's two loops have looked at, mirrors included.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         What exit criterion 8 should have been gated on, and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a> is why.
    ///     </b>
    ///     <see cref="WeightsEvaluated" /> counts the weight function's calls, which happen only
    ///     inside a stamp's footprint and only where an island covers the texel; the dilation scan
    ///     beside it is the larger of the two and was in no counter at all.
    ///     <see cref="PaintStroke.TexelsScanned" /> says what the closed form is and by how much.
    /// </remarks>
    public long TexelsScanned {
        get {
            var total = 0L;

            foreach (var stroke in strokes) {
                total += stroke.TexelsScanned;
            }

            return total;
        }
    }

    /// <summary>Whether anything has been painted.</summary>
    public bool IsEmpty {
        get {
            foreach (var stroke in strokes) {
                if (!stroke.IsEmpty) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Pointer-down. Evaluates nothing.</summary>
    /// <param name="target">What is being painted.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="colour">What is being painted, packed <c>0xAABBGGRR</c>.</param>
    /// <param name="lazy">How far the brush trails the pointer, in texels.</param>
    /// <param name="seed">What the jitter derives from.</param>
    /// <returns>The session.</returns>
    public static PaintSession Begin(
        PaintTarget target,
        PaintBrush brush,
        uint colour,
        float lazy = 0f,
        uint seed = 0x9E3779B9u
    ) {
        ArgumentNullException.ThrowIfNull(target);

        return new(target, brush, colour, lazy, seed);
    }

    /// <summary>Pointer-move, with symmetry. Paints whatever stamps the movement earned.</summary>
    /// <param name="positions">
    ///     Where the pointer is, in texels — the hit itself first, then one per symmetry mirror. Each
    ///     is its own path, so each gets its own spacing and its own carried distance.
    /// </param>
    /// <returns>What was dirtied, for a view to re-upload.</returns>
    /// <exception cref="ArgumentException">A move supplied a different number of paths than the last one.</exception>
    public PaintRect MoveAll(ReadOnlySpan<Vector2> positions) {
        if (positions.Length == 0) {
            return PaintRect.Empty;
        }

        if (strokes.Count == 0) {
            origin = target.Layer.Snapshot();

            for (var index = 0; index < positions.Length; index++) {
                // ⚠ A different seed per mirror. Sharing one would make a mirrored stroke's jitter
                // identical to its sibling's, so a symmetric drag would paint a picture that is
                // symmetric down to the noise — which is the one thing jitter exists to avoid.
                strokes.Add(
                    new(
                        target.Layer,
                        target.Coverage,
                        brush,
                        colour,
                        target.Gutter,
                        lazy,
                        seed + (uint)index * 0x85EBCA6Bu,
                        origin
                    )
                );
            }
        } else if (strokes.Count != positions.Length) {
            throw new ArgumentException(
                $"This drag began with {strokes.Count} path(s) and this move supplies {positions.Length}. "
                + "Turning symmetry on or off mid-drag would leave a mirror with no record, so the undo "
                + "entry would restore half of what the drag painted.",
                nameof(positions)
            );
        }

        regions.Clear();

        var dirty = PaintRect.Empty;

        for (var index = 0; index < positions.Length; index++) {
            dirty = dirty.Union(strokes[index].MoveTo(positions[index], regions));
        }

        return dirty;
    }

    /// <summary>Pointer-move, with symmetry, telling a caller each stamp's own rectangle.</summary>
    /// <param name="positions">Where the pointer is, in texels — the hit, then one per mirror.</param>
    /// <param name="dirtied">
    ///     Cleared and filled with one rectangle per stamp this move laid down, mirrors included.
    /// </param>
    /// <returns>Their union.</returns>
    /// <remarks>
    ///     ⚠ <b>For the caller that re-uploads.</b> A view given the union alone re-uploads the
    ///     bounding box of both mirrors, and two mirrored paths on opposite sides of the atlas have a
    ///     bounding box spanning it — <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a>.
    ///     The union is still returned, because a small stroke is one rectangle and one upload is
    ///     cheaper than two.
    /// </remarks>
    public PaintRect MoveAll(ReadOnlySpan<Vector2> positions, List<PaintRect> dirtied) {
        ArgumentNullException.ThrowIfNull(dirtied);

        var dirty = MoveAll(positions);

        dirtied.Clear();
        dirtied.AddRange(regions);

        return dirty;
    }

    /// <summary>Pointer-move, without symmetry.</summary>
    /// <param name="position">Where the pointer is, in texels.</param>
    /// <returns>What was dirtied.</returns>
    public PaintRect Move(Vector2 position) {
        Span<Vector2> one = [position];

        return MoveAll(one);
    }

    /// <summary>Pointer-up. Turns the drag into one undo entry.</summary>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="changed">Told which texels moved, on undo and on redo.</param>
    /// <returns>The command, or <see langword="null" /> when the drag painted nothing.</returns>
    /// <remarks>
    ///     ⚠ <b>Null rather than an empty command.</b> A click that missed the mesh is not an undo
    ///     entry, and pushing one would make the artist's next undo do nothing visible — which reads
    ///     as a broken undo stack rather than as a missed click.
    /// </remarks>
    public IAuthoringCommand? End(string name, Action<PaintRect>? changed = null) {
        if (IsEmpty) {
            return null;
        }

        return new PaintStrokeCommand(strokes, name, changed);
    }
}
