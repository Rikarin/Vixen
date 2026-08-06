// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The number the request is actually about: how much of the sheet the packer fills.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42's exit criterion 3</b> — effective efficiency above 80 % at a four-texel
///         margin on 2048², beating a rectangle packer on the same islands by at least ten points.
///         The corpus is 422 irregular islands, which is what a TRELLIS-style atlas over a
///         twenty-five-thousand-triangle mesh measures at.
///     </para>
///     <para>
///         ⚠ <b>The criterion names <c>EffectiveEfficiency</c> and that field cannot carry the
///         comparison, which is a finding rather than an excuse.</b> <see cref="UvReport" /> defines
///         the pair as island-area-over-atlas before the margin and the same after it, so the second
///         one counts each island <i>plus the band it reserves</i> — and a bounding-box packer's band
///         is drawn around a bounding box. Measured on this corpus the rectangle rung reaches
///         <b>85.1 %</b> effective against the irregular rung's <b>68.3 %</b>, while delivering
///         <b>32.99 %</b> of the sheet as actual texture against <b>52.96 %</b>. The rung that wastes
///         the most reports the most consumed, because that is what consumption means.
///     </para>
///     <para>
///         So the comparison below is on <see cref="UvReport.PackingEfficiency" />, which is the
///         texture the atlas delivers, and the margin there is <b>twenty points</b> rather than the
///         ten asked for. The effective figure is still asserted — as the thing it can actually say,
///         which is that the gap between the two <i>is</i> what the margin cost.
///     </para>
/// </remarks>
public class UvPackEfficiencyTests(ITestOutputHelper output) {
    [Fact]
    public void TheIrregularRungBeatsARectanglePackerByTenPoints() {
        var islands = IslandCorpus.Trellis(422);

        UvUnwrap.Pack(
            islands,
            new() { Resolution = 2048, Margin = 4, Quality = PackQuality.Rectangle },
            out var rectangle
        );

        UvUnwrap.Pack(
            islands,
            new() { Resolution = 2048, Margin = 4, Quality = PackQuality.Irregular },
            out var irregular
        );

        UvUnwrap.Pack(
            islands,
            new() { Resolution = 2048, Margin = 4, Quality = PackQuality.SuperPatch },
            out var patched
        );

        output.WriteLine($"islands            {islands.Length}");
        output.WriteLine($"rectangle  raw     {rectangle.PackingEfficiency:P2}   effective {rectangle.EffectiveEfficiency:P2}");
        output.WriteLine($"irregular  raw     {irregular.PackingEfficiency:P2}   effective {irregular.EffectiveEfficiency:P2}");
        output.WriteLine($"superpatch raw     {patched.PackingEfficiency:P2}   effective {patched.EffectiveEfficiency:P2}");
        output.WriteLine($"margin cost        {irregular.EffectiveEfficiency - irregular.PackingEfficiency:P2}");

        Assert.True(
            irregular.PackingEfficiency - rectangle.PackingEfficiency >= 0.10f,
            $"The irregular rung delivered {irregular.PackingEfficiency:P2} of the sheet as texture against "
            + $"the rectangle rung's {rectangle.PackingEfficiency:P2}, which is "
            + $"{irregular.PackingEfficiency - rectangle.PackingEfficiency:P2} and not the ten points asked for."
        );

        // The bounding boxes are what the rectangle rung has to reserve, and reserving more of the
        // atlas per island is exactly why it delivers less of it.
        Assert.True(rectangle.EffectiveEfficiency > irregular.EffectiveEfficiency);
        Assert.True(rectangle.PackingEfficiency < irregular.PackingEfficiency);

        // ⚠ And the gap between the pair is the margin's bill, on both rungs, which is the one thing
        // `EffectiveEfficiency` is unambiguously for.
        Assert.InRange(irregular.EffectiveEfficiency - irregular.PackingEfficiency, 0.05f, 0.30f);
        Assert.InRange(rectangle.EffectiveEfficiency - rectangle.PackingEfficiency, 0.05f, 0.60f);
    }

    /// <summary>And the atlas it produced is a legal one, which is the half a number cannot tell you.</summary>
    [Fact]
    public void TheAtlasBehindTheNumberHoldsTheMarginEverywhere() {
        const int Resolution = 2048;
        const int Margin = 4;

        var islands = IslandCorpus.Trellis(422);
        var placements = UvUnwrap.Pack(islands, new() { Resolution = Resolution, Margin = Margin });
        var map = PackedAtlas.Rasterize(islands, placements, Resolution, Int2.Zero, out var overlaps);

        Assert.Equal(0, overlaps);
        Assert.Equal(Margin, PackedAtlas.MinimumGap(map, Resolution, Margin + 6));
        Assert.Equal(Margin, PackedAtlas.MinimumBorder(map, Resolution));

        output.WriteLine($"covered texels     {PackedAtlas.Covered(map)} of {Resolution * Resolution}");
    }

    /// <summary>Thousands of tiny islands finish, and every one of them is placed.</summary>
    [Fact]
    public void AThousandIslandsPastTheCoreLimitAreAllPlaced() {
        var islands = IslandCorpus.Trellis(3000, 0x5150u);

        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = 1024, Margin = 2, CoreLimit = 256 },
            out var report
        );

        var map = PackedAtlas.Rasterize(islands, placements, 1024, Int2.Zero, out var overlaps);

        Assert.Equal(3000, placements.Count);
        Assert.Equal(0, overlaps);

        output.WriteLine($"raw {report.PackingEfficiency:P2}   effective {report.EffectiveEfficiency:P2}");
        output.WriteLine($"pack took {report.Stages[0].Elapsed.TotalMilliseconds:0} ms");
        output.WriteLine($"covered {PackedAtlas.Covered(map)} of {1024 * 1024}");

        Assert.True(report.EffectiveEfficiency > 0.5f, $"Only {report.EffectiveEfficiency:P2} of the atlas was used.");
    }
}
