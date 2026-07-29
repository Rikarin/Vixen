// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>Axis normalisation, <c>avar</c>, and the two cache keys a variable font would break.</summary>
/// <remarks>
///     The arithmetic and the keying, both decidable without a file, and both of which have to be
///     right before <c>gvar</c>'s deltas are worth reading. What the deltas then do is
///     <see cref="VariationConformanceTests" />, against the Consortium's hundred cases; the two
///     tests at the end here read real <c>fvar</c> tables because the padding in a font's tags is not
///     something a hand-written axis would ever have.
/// </remarks>
public class FontVariationTests {
    static readonly FontAxis Weight = new("wght", 100f, 400f, 900f);

    [Fact]
    public void The_two_halves_of_an_axis_are_scaled_separately() {
        // ⚠ The whole subtlety. 250 is half way from the default down to the minimum, so it is −0.5 —
        // *not* its position on the 100…900 line, which would be −0.1875. An axis is two segments
        // joined at the default, and a font's named instances are placed against that reading.
        Assert.Equal(-0.5f, Weight.Normalize(250f), 0.0001f);
        Assert.Equal(0f, Weight.Normalize(400f), 0.0001f);
        Assert.Equal(0.6f, Weight.Normalize(700f), 0.0001f);

        Assert.Equal(-1f, Weight.Normalize(100f), 0.0001f);
        Assert.Equal(1f, Weight.Normalize(900f), 0.0001f);
    }

    [Fact]
    public void A_value_outside_the_axis_is_clamped_rather_than_extrapolated() {
        Assert.Equal(-1f, Weight.Normalize(50f), 0.0001f);
        Assert.Equal(1f, Weight.Normalize(2_000f), 0.0001f);
    }

    [Fact]
    public void A_degenerate_axis_answers_zero_rather_than_dividing_by_it() {
        // Fonts really do ship these — an axis pinned at one end, usually because a family was built
        // from a template.
        var pinned = new FontAxis("wght", 400f, 400f, 900f);

        Assert.Equal(0f, pinned.Normalize(400f), 0.0001f);
        Assert.Equal(0f, pinned.Normalize(100f), 0.0001f);
        Assert.Equal(1f, pinned.Normalize(900f), 0.0001f);
    }

    [Fact]
    public void An_avar_map_warps_between_its_pairs() {
        // The identity ends plus one designer's opinion in the middle: normalised 0.5 behaves as 0.8.
        var map = new AxisSegmentMap([(-1f, -1f), (0f, 0f), (0.5f, 0.8f), (1f, 1f)]);

        Assert.Equal(0f, map.Apply(0f), 0.0001f);
        Assert.Equal(0.8f, map.Apply(0.5f), 0.0001f);

        // Half way along the first segment: 0.25 → 0.4.
        Assert.Equal(0.4f, map.Apply(0.25f), 0.0001f);

        // And half way along the second: 0.75 → 0.9.
        Assert.Equal(0.9f, map.Apply(0.75f), 0.0001f);
    }

    [Fact]
    public void An_empty_avar_map_is_the_identity() {
        var map = default(AxisSegmentMap);

        Assert.Equal(0.37f, map.Apply(0.37f), 0.0001f);
    }

    [Fact]
    public void Creating_a_position_normalises_and_then_warps() {
        var axes = ImmutableArray.Create(Weight);
        var maps = ImmutableArray.Create(new AxisSegmentMap([(-1f, -1f), (0f, 0f), (0.6f, 0.9f), (1f, 1f)]));

        // 700 normalises to 0.6, which the map sends to 0.9. Doing them in the other order, or
        // skipping the map, leaves both ends of the axis exactly right and everything between them
        // subtly wrong — which is the hardest kind of wrong to notice.
        var position = FontVariation.Create(axes, maps, new Dictionary<string, float> { ["wght"] = 700f });

        Assert.Equal(0.9f, Assert.Single(position.Coordinates), 0.0001f);
    }

    [Fact]
    public void An_axis_nobody_named_keeps_its_default() {
        var axes = ImmutableArray.Create(Weight, new FontAxis("wdth", 50f, 100f, 200f));

        var position = FontVariation.Create(axes, [], new Dictionary<string, float> { ["wdth"] = 150f });

        Assert.Equal(2, position.Coordinates.Length);
        Assert.Equal(0f, position.Coordinates[0], 0.0001f);
        Assert.Equal(0.5f, position.Coordinates[1], 0.0001f);
    }

    [Fact]
    public void A_font_with_no_axes_gives_the_none_position() {
        Assert.Same(FontVariation.None, FontVariation.Create([], [], new Dictionary<string, float>()));
        Assert.True(FontVariation.None.IsNone);
    }

    [Fact]
    public void Two_positions_are_equal_only_when_every_coordinate_is() {
        var first = new FontVariation([0.5f, -0.25f]);
        var same = new FontVariation([0.5f, -0.25f]);
        var different = new FontVariation([0.5f, -0.24f]);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, different);

        // ⚠ Bitwise rather than tolerant, because these are keys. A tolerant comparison makes a cache
        // that returns the outline for a *nearby* instance, and there is no bound on how wrong that
        // looks — an animation would visibly stick at whichever weight got there first.
        Assert.NotEqual(new FontVariation([0.5f]), new FontVariation([0.5000001f]));
    }

    [Fact]
    public void Different_axis_positions_do_not_share_a_shaping_entry() {
        // The reason the shaping key had to grow. It deliberately omits the *size*, because shaping
        // is done at design-unit scale and one entry serves every pixel size. An axis position is not
        // a scale: moving `wght` changes advances in design units, so leaving it out gives a cache
        // that answers the first weight asked for and keeps answering it.
        var cache = new ShapingCache();
        var font = TestFonts.Load("TestShapeLana.ttf");

        var light = cache.Shape(font, "a", ParagraphDirection.Auto, new FontVariation([-1f]));
        var bold = cache.Shape(font, "a", ParagraphDirection.Auto, new FontVariation([1f]));

        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);

        // And the same position twice is a hit, so the key has not simply been made useless.
        cache.Shape(font, "a", ParagraphDirection.Auto, new FontVariation([-1f]));
        Assert.Equal(1, cache.Hits);

        Assert.NotNull(light);
        Assert.NotNull(bold);
    }

    [Fact]
    public void Different_axis_positions_do_not_share_an_atlas_entry() {
        // The same argument for the glyph atlas: same font, same glyph, different axis position,
        // different outline — and a key without the position hands the second instance the first
        // one's distance field, reporting a hit while drawing a bold as a regular.
        var regular = new GlyphKey(0, 42, 64, new FontVariation([0f]));
        var bold = new GlyphKey(0, 42, 64, new FontVariation([1f]));

        Assert.NotEqual(regular, bold);
        Assert.Equal(regular, new GlyphKey(0, 42, 64, new FontVariation([0f])));

        // A static face keeps the old shape of the key and is distinct from a variable font sitting
        // at its defaults, which is not pedantry: one has a single instance for ever, the other is
        // one assignment away from being something else.
        Assert.NotEqual(new GlyphKey(0, 42, 64), regular);
    }

    [Fact]
    public void A_fonts_axes_come_back_in_the_fonts_own_order() {
        var font = TestFonts.Load("Zycon.ttf");

        Assert.True(font.IsVariable);
        Assert.Equal(["T1  ", "T2  ", "T3  ", "T4  ", "M1  ", "M2  "], font.Axes.Select(axis => axis.Tag));

        // ⚠ Order, not sorted order, and it is load-bearing: every variation table names an axis by
        // its index. Sorting these would read `M1`'s deltas against `T1`'s coordinate.
        Assert.Equal(0f, font.Axes[0].Minimum);
        Assert.Equal(-1f, font.Axes[4].Minimum);

        // A static face answers empty rather than throwing, which is what makes `IsVariable` a
        // question a caller can ask about any font.
        Assert.False(TestFonts.Load("TestShapeLana.ttf").IsVariable);
    }

    [Fact]
    public void An_axis_asked_for_without_its_padding_is_still_found() {
        // ⚠ **This was a real bug and the conformance suite found it.** A tag is four bytes, so
        // Zycon's axes are `M1  ` and `T1  ` on disk, and every caller that names one — CSS, the
        // Consortium's case files, a person — writes `M1`. Matching only the stored form left all
        // six axes at their defaults, which is indistinguishable on screen from a font with no
        // variation data at all: 32 of the suite's cases drew the same glyph nine times.
        var font = TestFonts.Load("Zycon.ttf");

        var trimmed = font.Variation(new Dictionary<string, float> { ["M1"] = 1f });
        var padded = font.Variation(new Dictionary<string, float> { ["M1  "] = 1f });

        Assert.Equal(padded, trimmed);
        Assert.Equal(1f, trimmed.Coordinates[4]);

        // And nothing else moved, so the lookup is not matching on a prefix.
        Assert.Equal(0f, trimmed.Coordinates[0]);
    }

    [Fact]
    public void Shaping_an_instance_moves_the_advances_too() {
        // ⚠ **The outline is not the whole of an instance.** A bold is wider as well as heavier, and
        // the width comes from `HVAR` through the shaper rather than from `gvar`. Before this was
        // wired, `ShapingCache` had a key that told two instances apart and a shaper that gave them
        // the same answer — a cache that was correct about a distinction nothing downstream made.
        var font = TestFonts.Load("TestGVARFour.ttf");

        Assert.Equal(["cntr", "wght"], font.Axes.Select(axis => axis.Tag));

        var light = TextShaper.Shape(font, "OIO", ParagraphDirection.Auto, font.Variation(Weights(0f)));
        var heavy = TextShaper.Shape(font, "OIO", ParagraphDirection.Auto, font.Variation(Weights(1000f)));

        Assert.NotEqual(light.Advance, heavy.Advance);

        // And it goes back: the face carries the instance, so a paragraph that asks for nothing must
        // not inherit whatever the last one set. `wght` defaults to its own maximum in this font, so
        // shaping with no instance has to agree with shaping at 1000 and disagree with 0.
        var plain = TextShaper.Shape(font, "OIO");

        Assert.Equal(FontVariation.None, font.Instance);
        Assert.Equal(heavy.Advance, plain.Advance);
        Assert.NotEqual(light.Advance, plain.Advance);
    }

    static Dictionary<string, float> Weights(float weight) => new(StringComparer.Ordinal) { ["wght"] = weight };
}
