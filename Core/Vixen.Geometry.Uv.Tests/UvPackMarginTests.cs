// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The margin is in texels, so the gap is the same at every resolution. Measured, not asserted.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42's exit criterion 4, and § B4's two-year fuse.</b> A margin expressed as a
///         fraction of UV space looks right at the resolution it was tuned at and bleeds across islands
///         at mip 3 when the same asset ships at half of it — in a build nobody associates with the
///         packing change, misdiagnosed as a sampler problem roughly always.
///     </para>
///     <para>
///         ⚠ <b>The verification is a rasterization.</b> The criterion says so in as many words, and
///         the reason is that a packer asserting its own occupancy grid would prove only that it agrees
///         with itself. What has to hold is that the placements — offset, scale, quarter turn — put the
///         <i>coordinates</i> the same number of texels apart at 512² as at 4096².
///     </para>
/// </remarks>
public class UvPackMarginTests {
    [Fact]
    public void TheTexelGapIsTheSameAtEveryResolution() {
        const int Margin = 4;

        var islands = IslandCorpus.Trellis(24, 0x3C1Fu);
        var gaps = new Dictionary<int, int>();
        var borders = new Dictionary<int, int>();

        foreach (var resolution in (int[])[512, 1024, 2048, 4096]) {
            var placements = UvUnwrap.Pack(islands, new() { Resolution = resolution, Margin = Margin });
            var map = PackedAtlas.Rasterize(islands, placements, resolution, Int2.Zero, out var overlaps);

            Assert.Equal(0, overlaps);

            gaps[resolution] = PackedAtlas.MinimumGap(map, resolution, Margin + 6);
            borders[resolution] = PackedAtlas.MinimumBorder(map, resolution);
        }

        Assert.All(gaps, entry => Assert.Equal(Margin, entry.Value));
        Assert.All(borders, entry => Assert.Equal(Margin, entry.Value));
    }

    /// <summary>The same claim the other way round: change the margin, and the gap changes with it.</summary>
    /// <remarks>
    ///     ⚠ Without this, a packer that ignored <see cref="PackSettings.Margin" /> entirely and left
    ///     four texels because of some other accident would pass the sweep above at every resolution.
    /// </remarks>
    [Theory]
    [InlineData(512, 2)]
    [InlineData(512, 6)]
    [InlineData(1024, 2)]
    [InlineData(1024, 6)]
    [InlineData(1024, 12)]
    public void TheGapIsTheMarginTheCallerAskedFor(int resolution, int margin) {
        var islands = IslandCorpus.Trellis(24, 0x3C1Fu);
        var placements = UvUnwrap.Pack(islands, new() { Resolution = resolution, Margin = margin });
        var map = PackedAtlas.Rasterize(islands, placements, resolution, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);
        Assert.Equal(margin, PackedAtlas.MinimumGap(map, resolution, margin + 6));
        Assert.Equal(margin, PackedAtlas.MinimumBorder(map, resolution));
    }

    /// <summary>A margin in UV units would shrink with the resolution. This is what that would look like.</summary>
    /// <remarks>
    ///     The islands are scaled to a fixed texel density, so the *content* is identical at both
    ///     resolutions and only the sheet grew. A UV-unit margin would then double its texel gap at
    ///     twice the resolution; a texel margin does not move.
    /// </remarks>
    [Fact]
    public void DoublingTheResolutionAtAFixedDensityDoesNotMoveTheGap() {
        const int Margin = 5;

        var islands = IslandCorpus.Trellis(20, 0x77A1u);
        var small = UvUnwrap.Pack(islands, new() { Resolution = 512, Margin = Margin, TexelDensity = 96f });
        var large = UvUnwrap.Pack(islands, new() { Resolution = 1024, Margin = Margin, TexelDensity = 96f });

        var smallMap = PackedAtlas.Rasterize(islands, small, 512, Int2.Zero, out _);
        var largeMap = PackedAtlas.Rasterize(islands, large, 1024, Int2.Zero, out _);

        Assert.Equal(Margin, PackedAtlas.MinimumGap(smallMap, 512, Margin + 6));
        Assert.Equal(Margin, PackedAtlas.MinimumGap(largeMap, 1024, Margin + 6));

        // And the islands themselves are the same size in texels, which is what makes the comparison
        // above about the margin rather than about the scale.
        var smallCover = PackedAtlas.Covered(smallMap);
        var largeCover = PackedAtlas.Covered(largeMap);

        Assert.InRange(largeCover, (int)(smallCover * 0.97f), (int)(smallCover * 1.03f));
    }

    /// <summary>The 61-island set that read a gap of zero across a pair 1.967 texels apart.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is a nightly failure of
    ///         <c>UvPackPropertyTests.The_gap_is_exactly_the_margin_when_the_atlas_is_packed_tightly</c>,
    ///         pinned.</b> The <c>properties (uv)</c> leg failed on it once in six nights; CsCheck seed
    ///         <c>1XF0jvSmVt13</c> reproduces it, at the per-commit gate's own eighty cases rather than
    ///         only at the nightly multiplier. The seed is <i>not</i> what is pinned here — a seed is a
    ///         claim about a generator and a library version, and both move. The recipes are written
    ///         out, so this set is this set for as long as the file exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The packer was right and the measurement was wrong, which is why the fix is in
    ///         <see cref="PackedAtlas" /> and this test is here rather than a placement being
    ///         corrected.</b> Islands 26 and 32 are <b>1.967 texels</b> apart — very nearly twice the
    ///         one-texel margin asked for. What read zero was a single texel, <c>(107, 151)</c>, which
    ///         island 32's geometry enters by <b>1.4 × 10⁻⁵ of a texel</b>: below the precision at
    ///         which this rasterizer, working in the atlas, and the packer's, working in the island's
    ///         own frame, can agree at all. That phantom texel sat diagonally against a texel of island
    ///         26, and a Chebyshev gap of one is a gap of zero. Of the 35,613 texels the set covers it
    ///         was the only one the two rasterizations disagreed about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted, and the second is the one that would catch a tolerance
    ///         grown until the test passed.</b> <c>PackedAtlas.Grazing</c> drops texels an island only
    ///         grazes, which can only <i>widen</i> a measured gap — so a gap that is exactly the margin
    ///         is the statement that the tolerance moved the phantom texel and nothing else. A
    ///         tolerance large enough to eat real coverage would push this above the margin and fail.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AGrazedTexelDoesNotCloseAGapTheIslandsNeverCrossed() {
        const int Resolution = 256;
        const int Margin = 1;

        var islands = IslandSpace.Build(GrazingSet);
        var settings = new PackSettings { Resolution = Resolution, Margin = Margin, CoreLimit = 64 };
        var placements = UvUnwrap.Pack(islands, settings);

        PackedAtlas.Rasterize(islands, placements, Resolution, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);

        // The same subset the property measures over: an island the scale search shrank below a texel
        // rasterizes to a texel it covers a few per cent of, and where that lands is decided twice.
        var thick = new List<UvPlacement>();

        foreach (var placement in placements) {
            var size = islands[placement.Island].Size * placement.Scale * Resolution;

            if (size.X >= 1f && size.Y >= 1f) {
                thick.Add(placement);
            }
        }

        var map = PackedAtlas.Rasterize(islands, thick, Resolution, Int2.Zero, out _);

        Assert.True(thick.Count >= 2, $"only {thick.Count} islands survived the sub-texel filter.");
        Assert.True(PackedAtlas.Covered(map) > 0, "the filtered set rasterized to nothing.");

        Assert.Equal(Margin, PackedAtlas.MinimumBorder(map, Resolution));
        Assert.Equal(Margin, PackedAtlas.MinimumGap(map, Resolution, Margin + 6));
    }

    /// <summary>The recipes CsCheck seed <c>1XF0jvSmVt13</c> drew, written out so they cannot drift.</summary>
    static readonly IslandRecipe[] GrazingSet = [
        new(IslandShape.Sliver, 4, 0.06556703f, 3.967721f, 1.7229552f),
        new(IslandShape.Rectangle, 15, 0.020131536f, 0.49642515f, 1f),
        new(IslandShape.Star, 16, 0.06434888f, 2f, 1.2123733f),
        new(IslandShape.Rectangle, 11, 0.03488314f, 4f, 4f),
        new(IslandShape.Star, 10, 0.020585429f, 4f, 4f),
        new(IslandShape.Star, 16, 0.02f, 4f, 2.765335f),
        new(IslandShape.Sliver, 11, 0.02f, 2.4848485f, 2f),
        new(IslandShape.Star, 12, 0.02000145f, 1f, 2.6153846f),
        new(IslandShape.Degenerate, 8, 0.36f, 0.7199445f, 2.463622f),
        new(IslandShape.Rectangle, 11, 0.02f, 2f, 0.43243244f),
        new(IslandShape.Convex, 9, 0.02f, 1.0344827f, 0.8611111f),
        new(IslandShape.Rectangle, 10, 0.02f, 0.33333334f, 0.31666493f),
        new(IslandShape.Star, 4, 0.02f, 2.7232404f, 2f),
        new(IslandShape.Sliver, 18, 0.02f, 2f, 2.5114422f),
        new(IslandShape.Star, 9, 0.02003208f, 1.7967691f, 2f),
        new(IslandShape.Convex, 10, 0.02f, 4f, 2.1734693f),
        new(IslandShape.Star, 7, 0.02f, 4f, 2f),
        new(IslandShape.Convex, 12, 0.03759621f, 2.4410696f, 2.5869584f),
        new(IslandShape.Convex, 3, 0.02f, 0.8779707f, 1.1561484f),
        new(IslandShape.Star, 18, 0.02f, 3.1549296f, 2.329678f),
        new(IslandShape.Star, 8, 0.02f, 3.896449f, 1f),
        new(IslandShape.Sliver, 14, 0.020290624f, 3f, 3f),
        new(IslandShape.Star, 11, 0.36f, 0.31641293f, 1.0647144f),
        new(IslandShape.Star, 20, 0.02111426f, 2.8444445f, 1.4874296f),
        new(IslandShape.Star, 7, 0.020000203f, 2.0495853f, 2f),
        new(IslandShape.Sliver, 16, 0.0871605f, 1f, 1.6153846f),
        new(IslandShape.Convex, 14, 0.27800378f, 1.939868f, 1.9150038f),
        new(IslandShape.Star, 19, 0.020024309f, 2.773241f, 3.868421f),
        new(IslandShape.Star, 7, 0.02f, 0.7691498f, 2.8818064f),
        new(IslandShape.Star, 5, 0.06406401f, 4f, 3.250784f),
        new(IslandShape.Star, 13, 0.02f, 4f, 0.6818182f),
        new(IslandShape.Convex, 6, 0.12375349f, 3.2615652f, 4f),
        new(IslandShape.Star, 7, 0.36f, 3.113914f, 0.5866232f),
        new(IslandShape.Star, 16, 0.36f, 1.9552239f, 3.0072827f),
        new(IslandShape.Rectangle, 4, 0.02f, 3f, 4f),
        new(IslandShape.Convex, 11, 0.14188454f, 0.299078f, 2f),
        new(IslandShape.Sliver, 11, 0.27414587f, 0.15701342f, 3.25f),
        new(IslandShape.Rectangle, 11, 0.020000054f, 4f, 0.65196896f),
        new(IslandShape.Star, 7, 0.02f, 4f, 1.147541f),
        new(IslandShape.Sliver, 16, 0.36f, 2f, 1.75f),
        new(IslandShape.Star, 14, 0.02f, 0.54f, 3.841535f),
        new(IslandShape.Rectangle, 9, 0.02f, 4f, 0.278481f),
        new(IslandShape.Star, 19, 0.020961424f, 3.8924818f, 2.1992288f),
        new(IslandShape.Star, 20, 0.02f, 0.3508772f, 2f),
        new(IslandShape.Rectangle, 3, 0.02f, 4f, 0.48394537f),
        new(IslandShape.Sliver, 5, 0.030593786f, 2.1532536f, 3.3161063f),
        new(IslandShape.Convex, 8, 0.02f, 2.5751057f, 2f),
        new(IslandShape.Rectangle, 4, 0.02f, 3.854467f, 1f),
        new(IslandShape.Sliver, 17, 0.02f, 1f, 2.2914767f),
        new(IslandShape.Convex, 17, 0.36f, 2.5450177f, 3.494382f),
        new(IslandShape.Star, 19, 0.027621089f, 2.1995559f, 2.5844157f),
        new(IslandShape.Convex, 8, 0.02f, 2f, 2.1153846f),
        new(IslandShape.Star, 8, 0.021786395f, 1.826087f, 3.53125f),
        new(IslandShape.Convex, 14, 0.02f, 0.90909094f, 3.0020266f),
        new(IslandShape.Convex, 6, 0.35731044f, 3f, 3.2083333f),
        new(IslandShape.Star, 10, 0.02f, 0.5125551f, 1f),
        new(IslandShape.Star, 20, 0.02f, 3.575f, 1f),
        new(IslandShape.Sliver, 18, 0.3224284f, 4f, 2f),
        new(IslandShape.Degenerate, 16, 0.020146875f, 3f, 1.1910112f),
        new(IslandShape.Convex, 11, 0.020544f, 1.6296296f, 2.5185523f),
        new(IslandShape.Star, 8, 0.17320006f, 1.4571066f, 0.3409091f)
    ];
}
