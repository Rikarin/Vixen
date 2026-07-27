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
    public float CaretOffset(int index) {
        var clamped = Math.Clamp(index, 0, Text.Length);

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

    /// <summary>The caret position nearest a point.</summary>
    /// <param name="x">A distance in design units from the start of the line.</param>
    /// <returns>A UTF-16 index on a grapheme boundary.</returns>
    /// <remarks>
    ///     <para>
    ///         Nearly the inverse of <see cref="CaretOffset" />, and tested as one: for every
    ///         grapheme boundary in a string, hit-testing the caret's own offset lands back on a
    ///         caret at the same place. That round trip is worth more than any hand-written
    ///         expectation about where a caret goes, because it holds for scripts nobody thought to
    ///         write a case for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not an inverse, and it cannot be.</b> Where a left-to-right run meets a
    ///         right-to-left one, two different logical positions occupy the <i>same</i> x — in
    ///         <c>abcلسان</c> the caret after the <c>c</c> and the caret at the end of the text are
    ///         the same point on the screen. No function from a point to an index can return both,
    ///         so this returns the one belonging to the run drawn first. Telling them apart needs a
    ///         caret <i>affinity</i> carried alongside the index, which is an editor's concern and
    ///         is owed with the rest of <c>TextEditor</c>.
    ///     </para>
    /// </remarks>
    public int CaretIndexAt(float x) {
        if (Clusters.Count == 0) {
            return 0;
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
            return best.Start;
        }

        var fraction = Math.Clamp((x - best.X) / best.Advance, 0, 1);
        if (best.IsRightToLeft) {
            fraction = 1 - fraction;
        }

        var step = (int)Math.Round(fraction * steps, MidpointRounding.AwayFromZero);
        return BoundaryAfter(best.Start, step);
    }

    static float Interpolate(ClusterSpan span, float fraction) =>
        span.IsRightToLeft ? span.Right - (span.Advance * fraction) : span.X + (span.Advance * fraction);

    float Fraction(ClusterSpan span, int index) {
        var total = Steps(span.Start, span.End);
        return total == 0 ? 0 : (float)Steps(span.Start, index) / total;
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
