// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina wave 8's compounds over a mask or a placement, each against a closed form.</summary>
/// <remarks>
///     <para>
///         <b>Patina #174's distance and flood compounds and #170's placement helpers take a picture</b>, so unlike the
///         noise compounds they can be held to texels: a band of a stated width round a disc, an island filled to one
///         at its middle, a range of instances by number, four pictures each in its own quarter.
///     </para>
///     <para>
///         And two of #168's patterns, whose geometry is arithmetic: a mesh's wire takes the share of the pitch it
///         says, and a waveform's bars stand symmetric about the middle row with a gap between each.
///     </para>
/// </remarks>
public class TextureDistanceCompoundDeviceTests(ITestOutputHelper output) {
    const int Side = 128;

    /// <summary>One disc of radius 24 at the centre.</summary>
    static readonly byte[] Disc = Grey(
        Side,
        Side,
        static (x, y) => (x + 0.5f - 64) * (x + 0.5f - 64) + (y + 0.5f - 64) * (y + 0.5f - 64) < 24 * 24 ? 1f : 0f
    );

    static float Distance(int x, int y) =>
        MathF.Sqrt((x + 0.5f - 64) * (x + 0.5f - 64) + (y + 0.5f - 64) * (y + 0.5f - 64));

    /// <summary>An outline is one on a band outside the shape as wide as it says, and nought inside and beyond.</summary>
    [Fact]
    public void An_outline_is_a_band_of_its_width_outside_the_shape() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // A width of a sixteenth of 128 is eight texels, and no softness keeps the band hard.
        var outline = Bake(
            device,
            "Adjust/Outline",
            Side,
            Side,
            [new("Mask", Disc, true)],
            static node => {
                node.SetText("width", "0.0625");
                node.SetText("softness", "0");
            }
        );

        var checkedBand = 0;

        for (var y = 0; y < Side; y += 3) {
            for (var x = 0; x < Side; x += 3) {
                var d = Distance(x, y);

                if (d < 23f || d > 34f) {
                    Assert.Equal(0f, outline.At(x, y, 0), 0.01f);
                } else if (d is > 25.5f and < 30.5f) {
                    Assert.Equal(1f, outline.At(x, y, 0), 0.01f);
                    checkedBand++;
                }
            }
        }

        Assert.True(checkedBand > 20, $"Only {checkedBand} texels of the band were examined.");
    }

    /// <summary>Fill from Edges is nought at an island's edge and one half its narrower side in, whatever its size.</summary>
    /// <remarks>
    ///     ⚠ Two discs of different sizes, so a fill scaled by the image rather than by the island would reach one in
    ///     the large disc and only a part of it in the small one.
    /// </remarks>
    [Fact]
    public void Fill_from_edges_reaches_one_at_every_islands_middle() {
        using var device = TextureKernelHarness.Open();

        var discs = Grey(
            Side,
            Side,
            static (x, y) => {
                var big = (x + 0.5f - 40) * (x + 0.5f - 40) + (y + 0.5f - 40) * (y + 0.5f - 40) < 24 * 24;
                var small = (x + 0.5f - 100) * (x + 0.5f - 100) + (y + 0.5f - 100) * (y + 0.5f - 100) < 10 * 10;

                // ⚠ And a bar 48 by 16, so a fill scaled by the island's wider side reaches a third at its middle.
                var bar = x is >= 70 and < 118 && y is >= 10 and < 26;

                return big || small || bar ? 1f : 0f;
            }
        );

        var fill = Bake(device, "Utility/Fill from Edges", Side, Side, [new("Mask", discs, true)]);

        Assert.Equal(1f, fill.At(94, 17, 0), 0.15f);

        Assert.Equal(1f, fill.At(39, 39, 0), 0.1f);
        Assert.Equal(1f, fill.At(99, 99, 0), 0.15f);
        Assert.Equal(0.5f, fill.At(40 + 12, 39, 0), 0.1f);
        Assert.Equal(0.5f, fill.At(100 + 5, 99, 0), 0.15f);
        Assert.Equal(0f, fill.At(70, 70, 0), 0.001f);
    }

    /// <summary>Instance Range selects the instances numbered first to last of a placement's Index, and no other.</summary>
    [Fact]
    public void Instance_range_selects_its_numbers_and_no_others() {
        using var device = TextureKernelHarness.Open();

        const int Cells = 8;
        const int Cell = Side / Cells;

        // A Tile Sampler's Index at an eight by eight grid: instance i + 8j is (i + 8j + ½) / 64 over its cell.
        var index = Grey(Side, Side, (x, y) => (x / Cell + y / Cell * Cells + 0.5f) / 64f);

        // ⚠ The index arrives as a byte here, which holds 1/255 steps against a 1/64 spacing — every number still
        // rounds inside its own share, which is what lets this read an eight-bit picture at all.
        var range = Bake(
            device,
            "Utility/Instance Range",
            Side,
            Side,
            [new("Index", index, true)],
            static node => {
                node.SetText("first", "10");
                node.SetText("last", "21");
            }
        );

        for (var number = 0; number < 64; number++) {
            var (x, y) = (number % Cells * Cell + Cell / 2, number / Cells * Cell + Cell / 2);

            Assert.Equal(number is >= 10 and <= 21 ? 1f : 0f, range.At(x, y, 0));
        }
    }

    /// <summary>Atlas Pack puts each picture in its own quarter of the width, squeezed to it.</summary>
    [Fact]
    public void Atlas_pack_puts_each_picture_in_its_own_column() {
        using var device = TextureKernelHarness.Open();

        var atlas = Bake(
            device,
            "Utility/Atlas Pack",
            Side,
            Side,
            [
                new("First", Solid(Side, Side, 1f, 0f, 0f)),
                new("Second", Solid(Side, Side, 0f, 1f, 0f)),
                new("Third", Solid(Side, Side, 0f, 0f, 1f)),
                new("Fourth", Grey(Side, Side, static (x, _) => x < Side / 2 ? 1f : 0f))
            ]
        );

        (float R, float G, float B)[] expected = [(1, 0, 0), (0, 1, 0), (0, 0, 1)];

        for (var column = 0; column < 3; column++) {
            var x = column * 32 + 16;

            Assert.Equal(expected[column].R, atlas.At(x, 64, 0), 0.01f);
            Assert.Equal(expected[column].G, atlas.At(x, 64, 1), 0.01f);
            Assert.Equal(expected[column].B, atlas.At(x, 64, 2), 0.01f);
        }

        // The fourth picture's left half lit, squeezed into the left half of the fourth column.
        Assert.Equal(1f, atlas.At(96 + 8, 64, 0), 0.01f);
        Assert.Equal(0f, atlas.At(96 + 24, 64, 0), 0.01f);
    }

    /// <summary>Island Remap lays the whole pattern into each island's own box, and nought between islands.</summary>
    [Fact]
    public void Island_remap_lays_the_pattern_into_each_islands_box() {
        using var device = TextureKernelHarness.Open();

        // Two squares, 16 and 32 texels, and a pattern lit only in its left half.
        var islands = Grey(
            Side,
            Side,
            static (x, y) => (x is >= 8 and < 24 && y is >= 8 and < 24) || (x is >= 64 and < 96 && y is >= 64 and < 96)
                ? 1f
                : 0f
        );
        var pattern = Grey(Side, Side, static (x, _) => x < Side / 2 ? 1f : 0f);

        var remapped = Bake(
            device,
            "Utility/Island Remap",
            Side,
            Side,
            [new("Mask", islands, true), new("Pattern", pattern)]
        );

        Assert.Equal(1f, remapped.At(10, 16, 0), 0.01f);
        Assert.Equal(0f, remapped.At(21, 16, 0), 0.01f);
        Assert.Equal(1f, remapped.At(70, 80, 0), 0.01f);
        Assert.Equal(0f, remapped.At(90, 80, 0), 0.01f);
        Assert.Equal(0f, remapped.At(40, 40, 0), 0.001f);
    }

    /// <summary>A cast shadow darkens the picture beside the shape along its angle, and not the other side.</summary>
    [Fact]
    public void A_cast_shadow_falls_one_way() {
        using var device = TextureKernelHarness.Open();

        var light = Grey(Side, Side, static (_, _) => 1f);
        var shadowed = Bake(
            device,
            "Adjust/Cast Shadow",
            Side,
            Side,
            [new("Input", light), new("Shape", Disc, true)],
            static node => node.SetValue("Angle", 0f)
        );

        // Three texels right of the disc's edge is in its shadow; three left of it is lit.
        Assert.True(shadowed.At(64 + 27, 64, 0) < 0.7f, $"the shadow side reads {shadowed.At(64 + 27, 64, 0)}");
        Assert.Equal(1f, shadowed.At(64 - 28, 64, 0), 0.01f);
        Assert.Equal(1f, shadowed.At(64, 64, 0), 0.01f);
    }

    /// <summary>A mesh's wire covers the share of the pitch it says each way, so the openings are the rest squared.</summary>
    [Fact]
    public void A_mesh_grids_wire_takes_its_share_of_the_pitch() {
        using var device = TextureKernelHarness.Open();

        var mesh = Bake(
            device,
            "Patterns/Mesh Grid",
            Side,
            Side,
            [],
            static node => {
                node.SetValue("Count", 8f);
                node.SetText("wire", "0.25");
                node.SetText("softness", "0");
            }
        );

        var lit = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                lit += mesh.At(x, y, 0) > 0.5f ? 1 : 0;
            }
        }

        // Open is (1 − ¼)² of the area.
        Assert.Equal(1f - 0.75f * 0.75f, lit / (float)(Side * Side), 0.03f);
    }

    /// <summary>A waveform's bars stand symmetric about the middle row, with the column's gap dark from top to bottom.</summary>
    [Fact]
    public void Waveform_bars_stand_about_the_middle_with_gaps_between() {
        using var device = TextureKernelHarness.Open();

        var bars = Bake(device, "Noise/Waveform Bars", Side, Side, [], static node => node.SetValue("Bars", 8f));
        var heights = new HashSet<int>();

        for (var bar = 0; bar < 8; bar++) {
            // The right part of each sixteen-texel column is the bar, and its left part the gap.
            var (inBar, inGap) = (bar * 16 + 12, bar * 16 + 1);
            var top = Enumerable.Range(0, Side).First(y => bars.At(inBar, y, 0) > 0.5f);
            var bottom = Enumerable.Range(0, Side).Last(y => bars.At(inBar, y, 0) > 0.5f);

            Assert.True(
                Math.Abs(Side - 1 - bottom - top) <= 1,
                $"bar {bar} runs {top} to {bottom}, not about the middle"
            );
            Assert.All(Enumerable.Range(0, Side), y => Assert.Equal(0f, bars.At(inGap, y, 0), 0.01f));
            heights.Add(bottom - top);
        }

        Assert.True(heights.Count > 3, "Eight bars came out at so few heights that the height was not drawn per bar.");
    }
}
