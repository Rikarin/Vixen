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
}
