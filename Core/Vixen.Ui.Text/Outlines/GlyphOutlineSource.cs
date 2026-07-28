// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.Text.Outlines;

/// <summary>
///     A font's outlines, in whichever of the two formats it stores them.
/// </summary>
/// <remarks>
///     <para>
///         <b>The tables come from HarfBuzz and the reading is Vixen's.</b> HarfBuzzSharp exposes no
///         outline API at all — <c>TryGetGlyphExtents</c> is a bounding box, and there is no draw,
///         paint or outline surface — so the only way to a contour is to read the tables it will
///         hand over. Verified before it was built on:
///         <c>docs/plan/spikes/text-glyph-outlines/RESULT.md</c>.
///     </para>
///     <para>
///         ⚠ <b>The outline is positioned, and the font's own coordinates are not.</b> HarfBuzz
///         reports a <c>glyf</c> glyph's extents shifted so that its <c>xMin</c> lands on the left
///         side bearing from <c>hmtx</c>, and where a font's stored <c>xMin</c> disagrees with its
///         <c>lsb</c> — common, and universal in italics — the two spaces differ by that much. Since
///         every position in this assembly comes from HarfBuzz, the outline is put in HarfBuzz's
///         space rather than the other way round: a glyph drawn from the raw table would sit
///         <c>lsb − xMin</c> units off, on exactly the fonts nobody tests with.
///     </para>
///     <para>
///         CFF is left alone. HarfBuzz runs the charstring itself for those, and the spike measured
///         no shift.
///     </para>
/// </remarks>
internal sealed class GlyphOutlineSource {
    readonly GlyfOutlines? glyf;
    readonly CffOutlines? cff;
    readonly byte[] hmtx;
    readonly int longMetrics;

    GlyphOutlineSource(GlyfOutlines? glyf, CffOutlines? cff, byte[] hmtx, int longMetrics) {
        this.glyf = glyf;
        this.cff = cff;
        this.hmtx = hmtx;
        this.longMetrics = longMetrics;
    }

    /// <summary>Whether the font stores outlines this can read at all.</summary>
    public bool HasOutlines => glyf is not null || cff is not null;

    /// <summary>Builds a source from a face's tables.</summary>
    /// <param name="table">Reads one table by tag, returning an empty array when it is absent.</param>
    /// <param name="unitsPerEm">The font's design grid.</param>
    /// <returns>The source. It reads nothing further from the face.</returns>
    public static GlyphOutlineSource Create(Func<string, byte[]> table, int unitsPerEm) {
        var hmtx = table("hmtx");
        var hhea = table("hhea");
        var longMetrics = hhea.Length >= 36 ? new SfntReader(hhea) { Position = 34 }.U16() : 0;

        var glyfTable = table("glyf");
        if (glyfTable.Length > 0) {
            var head = table("head");
            var maxp = table("maxp");
            var loca = table("loca");

            if (head.Length >= 52 && maxp.Length >= 6 && loca.Length > 0) {
                return new GlyphOutlineSource(new GlyfOutlines(head, maxp, loca, glyfTable), null, hmtx, longMetrics);
            }
        }

        var cffTable = table("CFF ");
        return cffTable.Length > 0
            ? new GlyphOutlineSource(null, new CffOutlines(cffTable, unitsPerEm), hmtx, longMetrics)
            : new GlyphOutlineSource(null, null, hmtx, longMetrics);
    }

    /// <summary>The glyph's contours, positioned the way the shaper's numbers assume.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The outline, or an empty one for a glyph that draws nothing.</returns>
    public GlyphOutline Read(int glyph) {
        if (cff is not null) {
            return cff.Read(glyph);
        }

        if (glyf is null) {
            return GlyphOutline.Empty;
        }

        var outline = glyf.Read(glyph);
        var shift = Shift(glyph);
        return shift == 0 ? outline : Translate(outline, shift);
    }

    /// <summary>How far the rasterised glyph sits from where the table stores it.</summary>
    float Shift(int glyph) {
        if (glyf?.StoredBounds(glyph) is not { } stored || LeftSideBearing(glyph) is not { } lsb) {
            return 0;
        }

        return lsb - stored.MinX;
    }

    /// <summary>The glyph's left side bearing, from the run-length-encoded tail of <c>hmtx</c>.</summary>
    int? LeftSideBearing(int glyph) {
        if (hmtx.Length == 0 || longMetrics == 0 || glyph < 0) {
            return null;
        }

        // Up to numberOfHMetrics the table holds (advance, lsb) pairs; past it, bare lsbs for the
        // monospaced tail — which is how a CJK font stores twenty thousand identical advances.
        var at = glyph < longMetrics ? (glyph * 4) + 2 : (longMetrics * 4) + ((glyph - longMetrics) * 2);
        if (at + 2 > hmtx.Length) {
            return null;
        }

        return new SfntReader(hmtx) { Position = at }.S16();
    }

    static GlyphOutline Translate(GlyphOutline outline, float dx) {
        if (outline.IsEmpty) {
            return outline;
        }

        var moved = ImmutableArray.CreateBuilder<OutlineSegment>(outline.Segments.Length);

        // Per verb rather than all three x fields: the unused ones are zero, and moving them would
        // leave a shape that reads correctly and dumps wrong.
        foreach (var segment in outline.Segments) {
            moved.Add(segment.Verb switch {
                OutlineVerb.Move or OutlineVerb.Line => segment with { X0 = segment.X0 + dx },
                OutlineVerb.Quadratic => segment with { X0 = segment.X0 + dx, X1 = segment.X1 + dx },
                OutlineVerb.Cubic => segment with {
                    X0 = segment.X0 + dx, X1 = segment.X1 + dx, X2 = segment.X2 + dx
                },
                _ => segment
            });
        }

        return new GlyphOutline(moved.ToImmutable());
    }
}
