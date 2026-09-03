// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>One shaping cluster: the characters it covers and the space it occupies.</summary>
/// <param name="Start">The first character it covers, as a UTF-16 index.</param>
/// <param name="End">One past the last. Clusters tile the text without gaps or overlaps.</param>
/// <param name="X">Its left edge, in design units from the start of the line.</param>
/// <param name="Advance">Its width. Zero if every glyph in it was a deleted default-ignorable.</param>
/// <param name="IsRightToLeft">
///     Whether the characters run right to left inside it — which decides which edge the first
///     character is at, not which edge is <see cref="X" />.
/// </param>
public readonly record struct ClusterSpan(int Start, int End, float X, float Advance, bool IsRightToLeft) {
    /// <summary>Its right edge.</summary>
    public float Right => X + Advance;
}

/// <summary>Which of the two characters either side of an index a caret belongs to.</summary>
/// <remarks>
///     <para>
///         <b>An index names a boundary between two characters, and a boundary is not a place.</b>
///         Where both neighbours run the same way it may as well be — the character before ends
///         exactly where the character after begins, so the two readings are the same point and
///         nothing can tell them apart. At a <i>direction</i> boundary they are at opposite ends of
///         a run, and often at opposite ends of the line.
///     </para>
///     <para>
///         ⚠ <b>This is what makes <see cref="ShapedText.CaretOffset(int, CaretAffinity)" /> and
///         <see cref="ShapedText.CaretPositionAt" /> inverses of each other.</b> Without it the
///         first is a function from one index to two places and has to pick one, so hit-testing a
///         caret's own offset can return a caret drawn somewhere else — which on screen is a caret
///         that teleports across the line when the user clicks exactly where it already is.
///     </para>
///     <para>
///         ⚠ <b>It does not make the relation a bijection, and no bit could.</b> Two <i>different</i>
///         indices can share one point: in <c>abcلسان</c> the caret after the <c>c</c> and the caret
///         at the end of the text are both at the left edge of the Arabic run. That direction of the
///         ambiguity is irreducible — a point genuinely names two positions — and is resolved by an
///         editor remembering where its caret was, never by asking the text.
///     </para>
/// </remarks>
public enum CaretAffinity : byte {
    /// <summary>
    ///     The character <i>after</i> the index — the caret sits at that character's leading edge.
    ///     The reading a caret arriving from the left, or from a click on the character itself, has.
    /// </summary>
    Downstream,

    /// <summary>
    ///     The character <i>before</i> the index — the caret sits at that character's trailing edge.
    ///     The reading a caret that has just typed, or walked forward off the end of a run, has.
    /// </summary>
    Upstream
}

public sealed partial class ShapedText {
    List<ClusterSpan>? clusters;
    int[]? graphemes;

    /// <summary>The shaping clusters, tiling the text, ordered as they are drawn.</summary>
    public IReadOnlyList<ClusterSpan> Clusters => clusters ??= BuildClusters();

    /// <summary>Where a caret sits when it is in front of a character.</summary>
    /// <param name="index">A UTF-16 index into the text, ideally on a grapheme boundary.</param>
    /// <returns>The caret's x, in design units from the start of the line.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A shaping cluster is not a grapheme cluster, and the difference is the whole
    ///         problem.</b> A cluster is what the shaper could not subdivide — a ligature, a
    ///         reordered Indic syllable — and it can hold several user-perceived characters behind
    ///         one glyph. A caret moves in graphemes and has to land <i>inside</i> such a glyph, so
    ///         its position is interpolated across the cluster by grapheme count. Snapping to the
    ///         cluster edge instead makes the caret skip a character in Devanagari and jump the
    ///         whole of an <c>ffi</c> in Latin, both of which look like the arrow key is broken.
    ///     </para>
    ///     <para>
    ///         ⚠ In a right-to-left cluster the fraction runs the other way: the first character is
    ///         at the cluster's <i>right</i> edge. <see cref="ClusterSpan.X" /> is always the left
    ///         edge in line coordinates, because the pen always moves left to right; it is the
    ///         characters inside that reverse.
    ///     </para>
    /// </remarks>
    public float CaretOffset(int index) => CaretOffset(index, CaretAffinity.Downstream);

    /// <summary>Where a caret sits, given which side of the index it belongs to.</summary>
    /// <param name="index">A UTF-16 index into the text, ideally on a grapheme boundary.</param>
    /// <param name="affinity">Which of the two characters either side of the index it belongs to.</param>
    /// <returns>The caret's x, in design units from the start of the line.</returns>
    /// <remarks>
    ///     <para>
    ///         Everything <see cref="CaretOffset(int)" />'s remarks say about clusters and graphemes
    ///         holds here; the affinity decides only <i>which cluster</i> is interpolated across.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two answers differ only at a direction boundary, and that is not a weakness
    ///         of this design but the whole of the problem.</b> Clusters tile the text without gaps,
    ///         so inside a run the trailing edge of one and the leading edge of the next are the same
    ///         number. Where the direction changes the two clusters are at opposite ends of a run,
    ///         and one index therefore names two places that can be most of a line apart.
    ///     </para>
    /// </remarks>
    public float CaretOffset(int index, CaretAffinity affinity) {
        var clamped = Math.Clamp(index, 0, Text.Length);

        // ⚠ Upstream is tried first and only for Upstream, rather than being folded into one loop
        // with a flipped comparison: an index strictly inside a cluster is matched by both windows
        // and must give the same answer either way, or every caret in the middle of a ligature
        // would move when an editor changed its mind about which character it trails.
        if (affinity == CaretAffinity.Upstream) {
            foreach (var span in Clusters) {
                if (clamped > span.Start && clamped <= span.End) {
                    return Interpolate(span, Fraction(span, clamped, whenIndivisible: 1));
                }
            }
        }

        foreach (var span in Clusters) {
            if (clamped >= span.Start && clamped < span.End) {
                return Interpolate(span, Fraction(span, clamped));
            }
        }

        // Past the last character: the trailing edge of whichever cluster ends the text.
        foreach (var span in Clusters) {
            if (span.End == Text.Length) {
                return Interpolate(span, 1);
            }
        }

        return Advance;
    }

    /// <summary>The caret position nearest a point, without which side of it the caret is on.</summary>
    /// <param name="x">A distance in design units from the start of the line.</param>
    /// <returns>A UTF-16 index on a grapheme boundary.</returns>
    /// <remarks>
    ///     <para>
    ///         Nearly the inverse of <see cref="CaretOffset(int)" />, and for a paragraph that runs
    ///         one way it is one: for every grapheme boundary, hit-testing the caret's own offset
    ///         lands back on the same index.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Where the direction changes it is not, and the missing half is exactly the
    ///         affinity this drops.</b> <see cref="CaretPositionAt" /> returns both and round-trips
    ///         at a direction boundary; this returns the index alone, so feeding it back to
    ///         <see cref="CaretOffset(int)" /> can answer with the other end of the line. Prefer the
    ///         pair anywhere a caret is being <i>placed</i> rather than merely counted — this
    ///         overload is for callers that only want to know which character was clicked.
    ///     </para>
    /// </remarks>
    public int CaretIndexAt(float x) => CaretPositionAt(x).Index;

    /// <summary>The caret nearest a point: where it is in the text, and which side it belongs to.</summary>
    /// <param name="x">A distance in design units from the start of the line.</param>
    /// <returns>A UTF-16 index on a grapheme boundary, and the affinity that puts it back here.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The inverse of <see cref="CaretOffset(int, CaretAffinity)" />, which
    ///         <see cref="CaretIndexAt" /> could not be.</b> The affinity is read off which end of
    ///         the landed-on cluster the point fell at, so feeding the pair back gives the same x —
    ///         at a direction boundary as well as inside a run. That round trip is the gate, and it
    ///         is worth more than any hand-written expectation about where a caret goes, because it
    ///         holds for scripts nobody thought to write a case for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The index alone is still not recoverable, and that is a property of bidi text
    ///         rather than a gap here.</b> In <c>abcلسان</c> the caret after the <c>c</c> and the
    ///         caret at the end of the text are the same point on the screen: a point names two
    ///         positions, and no return value can be both. This answers with the one belonging to
    ///         the cluster drawn first, so an editor that needs the other must carry it rather than
    ///         re-derive it from a click.
    ///     </para>
    /// </remarks>
    public (int Index, CaretAffinity Affinity) CaretPositionAt(float x) {
        if (Clusters.Count == 0) {
            return (0, CaretAffinity.Downstream);
        }

        var best = Clusters[0];
        var distance = float.PositiveInfinity;

        foreach (var span in Clusters) {
            // Clusters are walked in drawing order, and the first match wins. That is what decides
            // the direction-boundary ambiguity above, so it is a choice rather than an accident.
            if (x >= span.X && x <= span.Right) {
                best = span;
                distance = 0;
                break;
            }

            var gap = x < span.X ? span.X - x : x - span.Right;
            if (gap < distance) {
                best = span;
                distance = gap;
            }
        }

        var steps = Steps(best.Start, best.End);
        if (steps == 0 || best.Advance == 0) {
            return (best.Start, CaretAffinity.Downstream);
        }

        var fraction = Math.Clamp((x - best.X) / best.Advance, 0, 1);
        if (best.IsRightToLeft) {
            fraction = 1 - fraction;
        }

        var step = (int)Math.Round(fraction * steps, MidpointRounding.AwayFromZero);

        // ⚠ The affinity is *which end of this cluster* the point landed at, not which direction the
        // cluster runs. Landing on the trailing edge means the caret belongs to the character before
        // the index — which is the reading that puts it back on this cluster rather than on whatever
        // is drawn after the index, and at a direction boundary those are at opposite ends of a run.
        return (BoundaryAfter(best.Start, step), step == steps ? CaretAffinity.Upstream : CaretAffinity.Downstream);
    }

    static float Interpolate(ClusterSpan span, float fraction) =>
        span.IsRightToLeft ? span.Right - (span.Advance * fraction) : span.X + (span.Advance * fraction);

    /// <summary>How far across a cluster an index sits, counted in graphemes.</summary>
    /// <param name="span">The cluster.</param>
    /// <param name="index">A UTF-16 index inside it.</param>
    /// <param name="whenIndivisible">
    ///     What to answer for a cluster with no grapheme boundary inside it to interpolate against —
    ///     the leading edge for a caret in front of it, the <i>trailing</i> edge for one behind it.
    ///     <para>
    ///         ⚠ <b>This parameter is insurance and is labelled as such: answering 0 for both fails
    ///         nothing.</b> The only span reached with no grapheme step in it is the one a glyphless
    ///         run gets — a string of deleted joiners — and that span's <c>Advance</c> is zero, so
    ///         both edges are the same number and no assertion about an x can tell them apart. It is
    ///         kept because the two callers genuinely want opposite ends and a future span that is
    ///         indivisible *and* wide would be silently wrong at one of them.
    ///     </para>
    /// </param>
    float Fraction(ClusterSpan span, int index, float whenIndivisible = 0) {
        var total = Steps(span.Start, span.End);
        return total == 0 ? whenIndivisible : (float)Steps(span.Start, index) / total;
    }

    /// <summary>How many grapheme boundaries lie in <c>(from, to]</c>.</summary>
    int Steps(int from, int to) {
        var boundaries = graphemes ??= BuildGraphemes();
        var count = 0;

        foreach (var boundary in boundaries) {
            if (boundary > from && boundary <= to) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The grapheme boundary <paramref name="step" /> steps after <paramref name="from" />.</summary>
    /// <remarks>
    ///     ⚠ Zero steps is <paramref name="from" /> itself, not the next boundary. Getting that
    ///     wrong is invisible in a hand-written expectation and fatal to the round trip: every
    ///     caret hit-tests one grapheme further along than where it was drawn, including the one at
    ///     the start of the line.
    /// </remarks>
    int BoundaryAfter(int from, int step) {
        if (step <= 0) {
            return from;
        }

        var boundaries = graphemes ??= BuildGraphemes();

        foreach (var boundary in boundaries) {
            if (boundary <= from) {
                continue;
            }

            if (--step == 0) {
                return boundary;
            }
        }

        return Text.Length;
    }

    int[] BuildGraphemes() {
        var found = new List<int>();
        GraphemeBreaker.Collect(Text, found);
        return [.. found];
    }

    /// <summary>Groups the glyphs into clusters and gives each one its place on the line.</summary>
    /// <remarks>
    ///     <para>
    ///         The glyphs of a cluster are contiguous, which is what makes this a single pass. Their
    ///         <i>order</i> is not logical, though: within a right-to-left run the clusters come
    ///         back visually, so they are reversed before the character range of each is worked out
    ///         from where the next one begins.
    ///     </para>
    ///     <para>
    ///         ⚠ A run can produce no glyphs at all — a string of zero-width joiners, all deleted.
    ///         It still gets a cluster, of zero width, so that its characters have somewhere for a
    ///         caret to be rather than falling into the neighbour's range.
    ///     </para>
    /// </remarks>
    List<ClusterSpan> BuildClusters() {
        var spans = new List<ClusterSpan>();
        var penX = 0f;

        foreach (var run in Runs) {
            if (run.Glyphs.Count == 0) {
                spans.Add(new ClusterSpan(
                    run.Item.Start,
                    run.Item.Start + run.Item.Length,
                    penX,
                    0,
                    run.Item.IsRightToLeft
                ));

                continue;
            }

            var groups = new List<(int Cluster, float X, float Advance)>();
            var i = 0;

            while (i < run.Glyphs.Count) {
                var cluster = run.Glyphs[i].Cluster;
                var x = penX;
                var advance = 0f;

                while (i < run.Glyphs.Count && run.Glyphs[i].Cluster == cluster) {
                    advance += run.Glyphs[i].XAdvance;
                    i++;
                }

                penX += advance;
                groups.Add((cluster, x, advance));
            }

            if (run.Item.IsRightToLeft) {
                groups.Reverse();
            }

            for (var k = 0; k < groups.Count; k++) {
                spans.Add(new ClusterSpan(
                    groups[k].Cluster,
                    k + 1 < groups.Count ? groups[k + 1].Cluster : run.Item.Start + run.Item.Length,
                    groups[k].X,
                    groups[k].Advance,
                    run.Item.IsRightToLeft
                ));
            }
        }

        return spans;
    }
}
