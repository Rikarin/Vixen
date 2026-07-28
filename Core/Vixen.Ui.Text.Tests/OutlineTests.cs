// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text.Outlines;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     The contours HarfBuzz will not give us, checked against the one thing it will.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gate is an external oracle, because nothing else can be one at this scale.</b>
///         HarfBuzz computes a glyph's extents from the same font by a completely separate
///         implementation, so agreeing with it over every glyph of every embedded font says the
///         contours are the font's own. A hand-written expectation could cover a dozen glyphs and
///         would say nothing about the eleven thousand behind them.
///     </para>
///     <para>
///         The same oracle over 242 system fonts and 259,298 glyphs is what the spike ran —
///         <c>docs/plan/spikes/text-glyph-outlines/RESULT.md</c>. This is the part of it that can
///         live in CI, where there are fourteen fonts and no system font directory to lean on.
///     </para>
/// </remarks>
public class OutlineTests {
    /// <summary>Every embedded font, by file name.</summary>
    public static TheoryData<string> Fonts {
        get {
            var data = new TheoryData<string>();
            foreach (var name in FontNames()) {
                data.Add(name);
            }

            return data;
        }
    }

    static IEnumerable<string> FontNames() {
        const string Prefix = "Vixen.Ui.Text.Tests.Fonts.";

        return Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(resource => resource.StartsWith(Prefix, StringComparison.Ordinal)
                               && (resource.EndsWith(".ttf", StringComparison.Ordinal)
                                   || resource.EndsWith(".otf", StringComparison.Ordinal)))
            .Select(resource => resource[Prefix.Length..])
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Every glyph of one font: the outline's own bounds against the extents HarfBuzz reports.
    /// </summary>
    /// <param name="font">Which font.</param>
    /// <remarks>
    ///     ⚠ <b>Control-point bounds, not curve bounds, and the difference is the oracle rather than
    ///     the parser.</b> For a <c>glyf</c> font HarfBuzz returns the box the font *stores*, and a
    ///     TrueType font stores the box of its control points — so comparing sampled curve bounds
    ///     measures the font's own convention and reads as a 5 % failure rate. What this checks is
    ///     that the points decoded are the points the font holds, which is the question a table
    ///     parser has to answer.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Fonts))]
    public void Every_glyph_of_every_font_has_the_bounds_HarfBuzz_reports_for_it(string font) {
        var face = TestFonts.Load(font);
        Assert.True(face.HasOutlines, $"{font} has no readable outlines");

        var checkedGlyphs = 0;
        var failures = new List<string>();

        for (ushort glyph = 0; glyph < face.GlyphCount; glyph++) {
            var outline = face.GetOutline(glyph);
            if (outline.IsEmpty || !face.TryGetGlyphExtents(glyph, out var expected)) {
                continue;
            }

            checkedGlyphs++;
            var actual = ControlPointBounds(outline);

            if (Off(actual, expected) > 1f) {
                failures.Add($"glyph {glyph}: expected {expected}, got {actual}");
            }
        }

        Assert.True(checkedGlyphs > 0, $"{font} produced no outlines to check");
        Assert.Empty(failures);
    }

    /// <summary>
    ///     ⚠ The oracle is only worth what it covers, so this asserts it covered something. A reader
    ///     that returned an empty outline for every glyph would pass the comparison above vacuously
    ///     — every glyph skipped, every assertion unreached.
    /// </summary>
    [Fact]
    public void The_corpus_is_large_enough_for_the_comparison_to_mean_something() {
        var total = 0;
        var fonts = 0;

        foreach (var font in FontNames()) {
            var face = TestFonts.Load(font);
            fonts++;

            for (ushort glyph = 0; glyph < face.GlyphCount; glyph++) {
                if (!face.GetOutline(glyph).IsEmpty) {
                    total++;
                }
            }
        }

        // A floor over the embedded fonts, not a tuned figure: they draw well over two thousand
        // glyphs between them, and the number only matters as a guard against the comparison above
        // going quiet. Eight of the twenty-two are the Consortium's variable faces, read here at
        // their default instance — which is worth having, because a `gvar` reader that damaged the
        // stored outline would show up in this comparison rather than in the variation suite.
        Assert.Equal(22, fonts);
        Assert.True(total > 2000, $"only {total} glyphs produced an outline");
    }

    // ------------------------------------------------------------ The two formats

    [Fact]
    public void A_truetype_glyph_is_drawn_in_quadratics() {
        var verbs = Verbs(TestFonts.Load(TestFonts.Kannada), 'ಕ');

        Assert.Contains(OutlineVerb.Quadratic, verbs);
        Assert.DoesNotContain(OutlineVerb.Cubic, verbs);
    }

    /// <summary>
    ///     And a CFF one in cubics — neither converted to the other, because only one of those two
    ///     directions is exact and neither is free.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The embedded CFF fonts are tiny, and measurably thinner than they look.</b> Fifteen
    ///     drawn glyphs between the four of them, two of which are rectangles — and counted rather
    ///     than guessed at, <b>zero stem operators and zero hintmasks in the whole corpus</b>. So the
    ///     width-parity rule that decides how many bytes a hintmask skips is never executed here, and
    ///     a sabotage inverting it passes every test in this file. The charstring interpreter's real
    ///     gate was the spike's 17,934 CFF glyphs, which cannot live in CI because the fonts belong
    ///     to the operating system. The flex operators are unreached for the same reason.
    /// </remarks>
    [Fact]
    public void A_cff_glyph_is_drawn_in_cubics() {
        var verbs = AllVerbs(TestFonts.Load(TestFonts.Cff));

        Assert.Contains(OutlineVerb.Cubic, verbs);
        Assert.DoesNotContain(OutlineVerb.Quadratic, verbs);
    }

    /// <summary>
    ///     ⚠ <b>A golden path, because the bounds oracle provably cannot see one.</b> Both of the
    ///     rules that turn TrueType's points into a path — an implied on-curve point midway between
    ///     two off-curve ones, and a contour that begins off-curve — move points that already lie
    ///     inside the hull of the points around them. Breaking either changes the shape and not the
    ///     bounding box, so every comparison against HarfBuzz stays green: found by two sabotages
    ///     that failed to fail, and closed here rather than explained away.
    /// </summary>
    /// <remarks>
    ///     HarfBuzz has no drawing API to compare against — checked against the pinned 14.2.1.1
    ///     assembly, which exposes no <c>Draw</c>, <c>Outline</c>, <c>Path</c> or <c>Paint</c> type
    ///     at all — so a written-down expectation is the only oracle left for path structure. It is
    ///     one glyph, and its job is to fail when the reconstruction rules change.
    /// </remarks>
    [Fact]
    public void The_path_through_a_glyph_s_points_is_the_one_TrueType_describes() {
        const string Expected =
            "M674,749 L1060,749 L1060,601 L1018,601 Q875,601 827,605 Q907,568 953,493 Q999,418 999,323 Q999,160 889,68 Q779,-25 584,-25 Q401,-25 292,70 Q183,165 183,323 Q183,509 355,605 Q302,601 175,601 L119,601 L129,749 L514,749 Q537,758 554,784 Q572,810 572,845 Q572,902 544,939 L116,939 L131,1130 L683,1130 Q808,1130 858,1147 Q909,1164 932,1200 Q956,1237 956,1292 Q956,1404 843,1529 L920,1588 Q1070,1430 1070,1238 Q1070,1121 1020,1054 Q971,988 892,964 Q813,939 676,939 Q694,893 694,843 Q694,789 674,749 Z M590,603 Q465,603 393,542 Q321,482 321,379 Q321,284 390,229 Q458,174 589,174 Q728,174 794,232 Q861,290 861,379 Q861,481 788,542 Q714,603 590,603 Z";

        var face = TestFonts.Load(TestFonts.Kannada);
        Assert.Equal(Expected, Describe(face.GetOutline(face.GlyphFor('\u0C95'))));
    }

    static string Describe(GlyphOutline outline) =>
        string.Join(
            " ",
            outline.Segments.Select(segment => segment.Verb switch {
                OutlineVerb.Move => $"M{segment.X0:F0},{segment.Y0:F0}",
                OutlineVerb.Line => $"L{segment.X0:F0},{segment.Y0:F0}",
                OutlineVerb.Quadratic => $"Q{segment.X0:F0},{segment.Y0:F0} {segment.X1:F0},{segment.Y1:F0}",
                OutlineVerb.Cubic =>
                    $"C{segment.X0:F0},{segment.Y0:F0} {segment.X1:F0},{segment.Y1:F0} {segment.X2:F0},{segment.Y2:F0}",
                _ => "Z"
            })
        );

    /// <summary>
    ///     ⚠ <b>And the two glyphs where a contour does not begin on the curve.</b> The Kannada
    ///     golden above reaches neither branch — all its contours start on-curve — so the sabotage
    ///     that always starts at point zero passed it. These two were found by counting which
    ///     branch each of the corpus's 2,066 glyphs took: the Balinese one starts at the contour's
    ///     <i>last</i> point, and the Tai Tham one at the midpoint between the last and the first,
    ///     because neither end is on the curve.
    /// </summary>
    [Theory]
    [InlineData("NotoSansBalinese-Regular.ttf", 4, "M-553,1650 Q-507,1650 -472,1616 Q-438,1583 -438,1533 Q-438,1486 -471,1452 Q-504,1417 -553,1417 Q-603,1417 -636,1452 Q-670,1487 -670,1533 Q-670,1581 -636,1616 Q-601,1650 -553,1650 Z M-350,1320 Q-284,1415 -267,1467 Q-250,1519 -250,1593 Q-250,1713 -330,1776 Q-410,1840 -553,1840 Q-706,1840 -792,1776 Q-877,1711 -877,1593 Q-877,1517 -860,1466 Q-843,1414 -777,1320 L-827,1270 Q-957,1430 -957,1602 Q-957,1790 -854,1890 Q-751,1990 -560,1990 Q-373,1990 -272,1889 Q-170,1788 -170,1602 Q-170,1432 -300,1270 L-350,1320 Z")]
    [InlineData("TestShapeLana.ttf", 8, "M190,0 L940,1493 L1124,1493 L374,0 L190,0 Z M190,1309 Q190,1493 374,1493 Q558,1493 558,1309 Q558,1125 374,1125 Q190,1125 190,1309 Z M756,184 Q756,368 940,368 Q1124,368 1124,184 Q1124,0 940,0 Q756,0 756,184 Z")]
    public void A_contour_that_starts_off_the_curve_begins_where_TrueType_says(
        string font,
        int glyph,
        string expected
    ) => Assert.Equal(expected, Describe(TestFonts.Load(font).GetOutline((ushort)glyph)));

    [Fact]
    public void Every_contour_opens_with_a_move_and_ends_with_a_close() {
        var outline = TestFonts.Load(TestFonts.Kannada).GetOutline(Glyph(TestFonts.Load(TestFonts.Kannada), 'ಕ'));

        Assert.Equal(OutlineVerb.Move, outline.Segments[0].Verb);
        Assert.Equal(OutlineVerb.Close, outline.Segments[^1].Verb);

        // ...and nothing draws between a close and the next move.
        for (var i = 0; i < outline.Segments.Length - 1; i++) {
            if (outline.Segments[i].Verb == OutlineVerb.Close) {
                Assert.Equal(OutlineVerb.Move, outline.Segments[i + 1].Verb);
            }
        }
    }

    [Fact]
    public void A_glyph_that_draws_nothing_has_an_empty_outline() {
        var face = TestFonts.Load(TestFonts.Kannada);
        Assert.True(face.GetOutline(Glyph(face, ' ')).IsEmpty);
    }

    [Fact]
    public void An_empty_outline_has_no_bounds_rather_than_infinite_ones() {
        Assert.Equal(default, GlyphOutline.Empty.Bounds());
    }

    // ------------------------------------------------------------ Helpers

    static ushort Glyph(FontFace face, char character) => face.GlyphFor(character);

    static HashSet<OutlineVerb> Verbs(FontFace face, char character) =>
        [.. face.GetOutline(Glyph(face, character)).Segments.Select(segment => segment.Verb)];

    static HashSet<OutlineVerb> AllVerbs(FontFace face) {
        var verbs = new HashSet<OutlineVerb>();
        for (ushort glyph = 0; glyph < face.GlyphCount; glyph++) {
            foreach (var segment in face.GetOutline(glyph).Segments) {
                verbs.Add(segment.Verb);
            }
        }

        return verbs;
    }

    /// <summary>The bounds of the control points, which is what a stored font bbox holds.</summary>
    static GlyphBounds ControlPointBounds(GlyphOutline outline) {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        void Hit(float x, float y) {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var segment in outline.Segments) {
            switch (segment.Verb) {
                case OutlineVerb.Move or OutlineVerb.Line:
                    Hit(segment.X0, segment.Y0);
                    break;

                case OutlineVerb.Quadratic:
                    Hit(segment.X0, segment.Y0);
                    Hit(segment.X1, segment.Y1);
                    break;

                case OutlineVerb.Cubic:
                    Hit(segment.X0, segment.Y0);
                    Hit(segment.X1, segment.Y1);
                    Hit(segment.X2, segment.Y2);
                    break;

                default:
                    break;
            }
        }

        return new GlyphBounds(minX, minY, maxX, maxY);
    }

    static float Off(GlyphBounds actual, GlyphBounds expected) =>
        Math.Max(
            Math.Max(Math.Abs(actual.MinX - expected.MinX), Math.Abs(actual.MaxX - expected.MaxX)),
            Math.Max(Math.Abs(actual.MinY - expected.MinY), Math.Abs(actual.MaxY - expected.MaxY))
        );
}
