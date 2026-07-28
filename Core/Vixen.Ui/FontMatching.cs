// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>One registered face and what it claims to be.</summary>
/// <param name="Face">The face.</param>
/// <param name="Weight">100–900.</param>
/// <param name="Style">Upright, italic or oblique.</param>
/// <param name="Stretch">The width.</param>
public readonly record struct FontEntry(FontFace Face, int Weight, FontStyle Style, FontStretch Stretch);

/// <summary>CSS Fonts 4 § 5.2, which decides which face of a family a declaration gets.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The three axes are tried in a fixed order and the order is the whole algorithm:
///         stretch, then style, then weight.</b> Each step narrows the candidates and the next step
///         only ever sees what survived. That is not an implementation convenience — it is what makes
///         a condensed family's bold beat a normal-width bold when the author asked for condensed,
///         and swapping any two steps produces a face that is right about the least important thing.
///     </para>
///     <para>
///         ⚠ <b>The weight rule is asymmetric around 400 and 500, and the asymmetry is deliberate in
///         the specification rather than an oddity to smooth over.</b> Below 400 a missing weight
///         prefers to go lighter, above 500 it prefers to go heavier, and 400 and 500 are special-cased
///         to prefer each other before anything else. A family that ships only 300 and 700 therefore
///         gives 400 the 300 and 500 the 700 — which looks arbitrary until you notice it means "a
///         regular request never accidentally comes back bold".
///     </para>
/// </remarks>
static class FontMatching {
    /// <summary>Picks the face of a family that best answers a query.</summary>
    public static FontFace? Best(List<FontEntry> family, in FontQuery query) {
        if (family.Count == 0) {
            return null;
        }

        Span<int> surviving = family.Count <= 32 ? stackalloc int[family.Count] : new int[family.Count];
        var count = 0;

        for (var i = 0; i < family.Count; i++) {
            surviving[count++] = i;
        }

        count = NarrowStretch(family, surviving, count, query.Stretch);
        count = NarrowStyle(family, surviving, count, query.Style);

        return family[BestWeight(family, surviving, count, query.Weight)].Face;
    }

    /// <summary>
    ///     Keeps the faces whose stretch is nearest the query, condensed-first below normal and
    ///     expanded-first above it.
    /// </summary>
    static int NarrowStretch(List<FontEntry> family, Span<int> surviving, int count, FontStretch wanted) {
        var target = (int) wanted;
        var best = int.MaxValue;

        // ⚠ The tie-break runs the *narrow* way for a condensed request and the *wide* way otherwise,
        // which is CSS's rule and not a coin toss: asking for condensed and being given an expanded
        // face because it happened to be equally far away is the one substitution nobody wants.
        for (var i = 0; i < count; i++) {
            best = Math.Min(best, Distance((int) family[surviving[i]].Stretch, target));
        }

        return Keep(family, surviving, count, entry => Distance((int) entry.Stretch, target) == best);

        int Distance(int candidate, int desired) {
            var gap = Math.Abs(candidate - desired);
            var wrongWay = desired <= (int) FontStretch.Normal ? candidate > desired : candidate < desired;
            return gap * 2 + (wrongWay ? 1 : 0);
        }
    }

    /// <summary>Keeps the faces whose style substitutes best.</summary>
    /// <remarks>
    ///     ⚠ The preference chains are asymmetric. Italic falls back to oblique before upright,
    ///     because a sheared roman still slants; oblique falls back to italic before upright for the
    ///     same reason; and upright falls back to oblique before italic, because a sheared roman is
    ///     closer to a roman than a separately drawn cursive face is.
    /// </remarks>
    static int NarrowStyle(List<FontEntry> family, Span<int> surviving, int count, FontStyle wanted) {
        ReadOnlySpan<FontStyle> order = wanted switch {
            FontStyle.Italic => [FontStyle.Italic, FontStyle.Oblique, FontStyle.Normal],
            FontStyle.Oblique => [FontStyle.Oblique, FontStyle.Italic, FontStyle.Normal],
            _ => [FontStyle.Normal, FontStyle.Oblique, FontStyle.Italic]
        };

        foreach (var style in order) {
            var kept = Keep(family, surviving, count, entry => entry.Style == style);

            if (kept > 0) {
                return kept;
            }
        }

        return count;
    }

    /// <summary>CSS Fonts 4's weight rule, which is three cases rather than "nearest".</summary>
    static int BestWeight(List<FontEntry> family, ReadOnlySpan<int> surviving, int count, int wanted) {
        var best = surviving[0];
        var bestRank = Rank(family[best].Weight);

        for (var i = 1; i < count; i++) {
            var rank = Rank(family[surviving[i]].Weight);

            if (rank < bestRank) {
                bestRank = rank;
                best = surviving[i];
            }
        }

        return best;

        // Lower is better. The first term is which *direction* the specification prefers at this
        // request, the second is how far away the candidate is — so direction always outranks
        // distance, which is what makes 400 take a 300 over a 700 however lopsided the family is.
        long Rank(int candidate) {
            if (candidate == wanted) {
                return 0;
            }

            var preferred = wanted switch {
                // ⚠ **The whole 400–500 band, not the single value 500.** CSS Fonts 4 says a request
                // for 400 checks "weights greater than or equal to 400 and less than or equal to 500,
                // in ascending order" before it looks below 400 — so a family offering 300 and 450
                // gives 400 the *450*. Written as `candidate == 500` this reads plausibly and is
                // wrong for every weight in between, which is most of the interesting ones.
                400 => candidate is > 400 and <= 500 ? 1 : candidate < 400 ? 2 : 3,

                // And 500 checks the same band downwards first.
                500 => candidate is >= 400 and < 500 ? 1 : candidate < 400 ? 2 : 3,

                // Below 400: lighter first.
                < 400 => candidate < wanted ? 1 : 2,

                // Above 500: heavier first.
                _ => candidate > wanted ? 1 : 2
            };

            return (preferred * 1_000L) + Math.Abs(candidate - wanted);
        }
    }

    static int Keep(List<FontEntry> family, Span<int> surviving, int count, Func<FontEntry, bool> wanted) {
        var kept = 0;

        for (var i = 0; i < count; i++) {
            if (wanted(family[surviving[i]])) {
                surviving[kept++] = surviving[i];
            }
        }

        return kept;
    }
}
