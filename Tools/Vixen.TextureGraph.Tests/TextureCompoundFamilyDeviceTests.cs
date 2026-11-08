// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #167 and #168's compounds, each baked through its published node, against what it says it draws.</summary>
/// <remarks>
///     <para>
///         <b>The patterns have no noise in them, so they are held to positions</b>: a stripe is where the
///         period says, a hexagon's centre is lit and the joint between two is not, every brick is one tone. A
///         hand-written file that wired the wrong port or got a constant wrong moves one of those.
///     </para>
///     <para>
///         ⚠ <b>The noise compounds are held to properties instead, and that is a weaker claim stated as
///         one.</b> Their seeds come from where their nodes sit inside the compound, which no graph in a test
///         can reproduce, so there is no second bake to compare a texel with. What is left is what the file
///         says the picture is — spots that cover π r² of a cell, streaks that vary sixteen times faster down
///         than across, a warp that only moves values it already had — each of which a wrong wire breaks.
///     </para>
/// </remarks>
public class TextureCompoundFamilyDeviceTests(ITestOutputHelper output) {
    const int Side = 256;

    static Lanes Compound(Vixen.Graphics.Vulkan.VulkanDevice device, string path, Action<GraphNode>? numbers = null, int side = Side) =>
        Bake(device, path, side, side, [], numbers);

    static float Fraction(Lanes picture, Func<float, bool> test) {
        var count = 0;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                count += test(picture.At(x, y, 0)) ? 1 : 0;
            }
        }

        return count / (float)(picture.Width * picture.Height);
    }

    /// <summary>The mean of the absolute step to the next texel along x, and along y.</summary>
    static (float AlongX, float AlongY) Steps(Lanes picture) {
        double x = 0;
        double y = 0;

        for (var row = 0; row < picture.Height; row++) {
            for (var column = 0; column < picture.Width; column++) {
                x += MathF.Abs(picture.At((column + 1) % picture.Width, row, 0) - picture.At(column, row, 0));
                y += MathF.Abs(picture.At(column, (row + 1) % picture.Height, 0) - picture.At(column, row, 0));
            }
        }

        var texels = picture.Width * picture.Height;

        return ((float)(x / texels), (float)(y / texels));
    }

    static (float Low, float High) Range(Lanes picture) {
        var low = float.MaxValue;
        var high = float.MinValue;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                low = MathF.Min(low, picture.At(x, y, 0));
                high = MathF.Max(high, picture.At(x, y, 0));
            }
        }

        return (low, high);
    }

    static float At(Lanes picture, float u, float v) =>
        picture.At(Math.Clamp((int)(u * picture.Width), 0, picture.Width - 1), Math.Clamp((int)(v * picture.Height), 0, picture.Height - 1), 0);

    [Fact]
    public void Stripes_are_where_their_period_and_width_put_them() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var stripes = Compound(device, "Patterns/Stripes", node => {
            node.SetValue("Count", 8f);
            node.SetText("width", "0.3");
            node.SetText("softness", "0");
        });

        var checkedTexels = 0;

        for (var x = 0; x < Side; x++) {
            var phase = (x + 0.5f) * 8f / Side % 1f;

            // A texel whose footprint straddles an edge is a mixture, and so is the one at the period's end.
            if (MathF.Abs(phase - 0.7f) < 0.04f || phase < 0.04f || phase > 0.96f) {
                continue;
            }

            for (var y = 0; y < Side; y += 7) {
                Assert.Equal(phase >= 0.7f ? 1f : 0f, stripes.At(x, y, 0), 0.001f);
                checkedTexels++;
            }
        }

        Assert.True(checkedTexels > 5000);
        Assert.Equal(0.3f, Fraction(stripes, value => value > 0.5f), 0.02f);
    }

    [Fact]
    public void A_weave_shows_one_thread_on_top_in_each_square_and_the_other_in_the_next() {
        using var device = TextureKernelHarness.Open();

        var weave = Compound(device, "Patterns/Weave");

        // Eight threads each way: a square is 32 texels, and inside it one thread runs one way, so the
        // height there is constant along the thread and varies across it.
        const int Cell = Side / 8;
        int? previous = null;

        for (var row = 0; row < 8; row++) {
            for (var column = 0; column < 8; column++) {
                var alongX = 0f;
                var alongY = 0f;

                for (var y = 3; y < Cell - 3; y++) {
                    for (var x = 3; x < Cell - 3; x++) {
                        var at = weave.At((column * Cell) + x, (row * Cell) + y, 0);

                        alongX = MathF.Max(alongX, MathF.Abs(weave.At((column * Cell) + x + 1, (row * Cell) + y, 0) - at));
                        alongY = MathF.Max(alongY, MathF.Abs(weave.At((column * Cell) + x, (row * Cell) + y + 1, 0) - at));
                    }
                }

                // One axis is flat to rounding and the other is a thread's profile.
                var axis = alongX < 0.002f ? 0 : 1;

                Assert.True(MathF.Min(alongX, alongY) < 0.002f && MathF.Max(alongX, alongY) > 0.02f, $"square ({column}, {row}) is {alongX} along x and {alongY} along y");

                // The crown of the thread on top is in the middle of the square.
                Assert.True(weave.At((column * Cell) + (Cell / 2), (row * Cell) + (Cell / 2), 0) > 0.95f);

                if (column > 0) {
                    Assert.NotEqual(previous, axis);
                }

                previous = axis;
            }
        }
    }

    [Fact]
    public void Hex_cells_are_lit_at_their_centres_and_dark_in_the_joints() {
        using var device = TextureKernelHarness.Open();

        // Eight columns, so round(8 / √3) = 5 rows of tiles, each two rows of hexagons.
        var hex = Compound(device, "Patterns/Hex Cells", node => node.SetText("bevel", "0.05"), 512);

        for (var j = 0; j < 5; j++) {
            for (var i = 0; i < 8; i++) {
                // The two centres a tile holds, its corner and its middle.
                Assert.True(At(hex, i / 8f, j / 5f) > 0.9f, $"the hexagon at tile ({i}, {j})'s corner is dark");
                Assert.True(At(hex, (i + 0.5f) / 8f, (j + 0.5f) / 5f) > 0.9f, $"the hexagon in tile ({i}, {j}) is dark");

                // And it reaches out to its flank: its apothem is 0.45 of a tile once widened by √3, so
                // four tenths of a tile right of its centre is still inside, and a narrower one ends sooner.
                Assert.True(At(hex, (i + 0.9f) / 8f, (j + 0.5f) / 5f) > 0.9f, $"the hexagon in tile ({i}, {j}) is too narrow");

                // The joint between two side by side, and the slanted one between a hexagon and the one
                // below and to the right of it.
                Assert.True(At(hex, (i + 1f) / 8f, (j + 0.5f) / 5f) < 0.1f, $"the joint right of tile ({i}, {j})'s hexagon is lit");
                Assert.True(At(hex, (i + 0.25f) / 8f, (j + 0.25f) / 5f) < 0.1f, $"the slanted joint in tile ({i}, {j}) is lit");
            }
        }
    }

    [Fact]
    public void A_triangle_grid_lights_every_centroid_and_none_of_the_edges() {
        using var device = TextureKernelHarness.Open();

        // Four bands are eight rows, and round(4√3) = 7 columns.
        // A wide joint, so that a point-up triangle's apex starts a fifth of a row below the row's top while the
        // point-down triangle beside it already has its base there.
        var grid = Compound(device, "Patterns/Triangle Grid", node => {
            node.SetText("bevel", "0.02");
            node.SetText("gap", "0.3");
        }, 512);

        for (var j = 0; j < 8; j++) {
            // Every other row is shifted half a triangle, so its point-up centroids sit on whole columns.
            var up = j % 2 == 0 ? 0.5f : 0f;

            for (var i = 0; i < 7; i++) {
                var upU = (i + up) / 7f;
                var downU = (i + up + 0.5f) / 7f;

                Assert.True(At(grid, upU, (j + (2f / 3f)) / 8f) > 0.9f, $"the point-up triangle at ({i}, {j}) is dark");
                Assert.True(At(grid, downU, (j + (1f / 3f)) / 8f) > 0.9f, $"the point-down triangle at ({i}, {j}) is dark");

                // The slanted edge they share, and the horizontal edge on top of the row.
                Assert.True(At(grid, (upU + downU) / 2f, (j + 0.5f) / 8f) < 0.1f, $"the edge between them at ({i}, {j}) is lit");
                Assert.True(At(grid, upU, j / 8f) < 0.1f, $"the top of row {j} is lit at column {i}");

                // ⚠ Which row is shifted is what makes the rows one lattice, and every point above is the same in a
                // grid whose rows were not shifted at all — each row alone is a valid strip. Just under the row's
                // top, the point-up triangle has not begun and the point-down one is at its widest.
                Assert.True(At(grid, upU, (j + 0.12f) / 8f) < 0.1f, $"above the apex at ({i}, {j}) is lit");
                Assert.True(At(grid, downU, (j + 0.12f) / 8f) > 0.9f, $"under the base at ({i}, {j}) is dark");
            }
        }
    }

    [Fact]
    public void Every_brick_is_one_tone_and_without_variation_every_brick_is_white() {
        using var device = TextureKernelHarness.Open();

        Lanes Bricks(string variation) =>
            Compound(device, "Patterns/Brick", node => {
                node.SetValue("Columns", 4f);
                node.SetValue("Rows", 8f);
                node.SetText("variation", variation);
            });

        var plain = Bricks("0");
        var varied = Bricks("0.5");
        Dictionary<(int Row, int Brick), float> tones = [];

        for (var y = 0; y < Side; y++) {
            var row = y * 8 / Side;
            var down = ((y + 0.5f) * 8f / Side) % 1f;

            for (var x = 0; x < Side; x++) {
                // Courses alternate by half a brick; which way does not matter to which texels share a brick.
                var along = ((x + 0.5f) * 4f / Side) + (row % 2 == 0 ? 0f : 0.5f);
                var across = along % 1f;

                if (MathF.Abs(across - 0.5f) > 0.25f || MathF.Abs(down - 0.5f) > 0.25f) {
                    continue;
                }

                Assert.Equal(1f, plain.At(x, y, 0), 0.0001f);

                var key = (row, (int)MathF.Floor(along) % 4);
                var tone = varied.At(x, y, 0);

                Assert.InRange(tone, 0.5f, 1f);

                if (tones.TryGetValue(key, out var first)) {
                    Assert.Equal(first, tone, 0.0001f);
                } else {
                    tones[key] = tone;
                }
            }
        }

        Assert.Equal(32, tones.Count);
        Assert.True(tones.Values.Distinct().Count() >= 24, "the bricks are not drawn a tone each");
    }

    [Fact]
    public void Clouds_span_the_range_and_roughness_breaks_them_up() {
        using var device = TextureKernelHarness.Open();

        var soft = Compound(device, "Noise/Clouds", node => node.SetText("roughness", "0.2"));
        var rough = Compound(device, "Noise/Clouds", node => node.SetText("roughness", "0.9"));

        Assert.Equal((0f, 1f), Range(soft));

        var softSteps = Steps(soft);
        var roughSteps = Steps(rough);

        output.WriteLine($"soft {softSteps}, rough {roughSteps}");

        Assert.True(softSteps.AlongX < 0.03f, $"soft clouds step {softSteps.AlongX} a texel");
        Assert.True(roughSteps.AlongX > 2f * softSteps.AlongX, "roughness did not add the finer octaves");
    }

    [Fact]
    public void Cell_walls_are_thin_bright_lines_that_widen_with_their_width() {
        using var device = TextureKernelHarness.Open();

        var thin = Compound(device, "Noise/Cell Walls", node => node.SetText("width", "0.03"));
        var wide = Compound(device, "Noise/Cell Walls", node => node.SetText("width", "0.15"));
        var thinShare = Fraction(thin, value => value > 0.5f);
        var wideShare = Fraction(wide, value => value > 0.5f);

        output.WriteLine($"thin {thinShare}, wide {wideShare}");

        Assert.Equal(1f, Range(thin).High, 0.01f);
        Assert.Equal(0f, Range(thin).Low, 0.0001f);
        Assert.InRange(thinShare, 0.01f, 0.2f);
        Assert.True(wideShare > 2.5f * thinShare);
    }

    /// <summary>
    ///     A distance to the nearest point is nought at each point and rises away from it, so the field's pits
    ///     are its cells: four across is sixteen, and every octave that is summed adds four times as many.
    /// </summary>
    [Fact]
    public void Fractal_cells_add_finer_cells_with_each_octave() {
        using var device = TextureKernelHarness.Open();

        var one = Compound(device, "Noise/Fractal Cells", node => node.SetValue("Octaves", 1f));
        var three = Compound(device, "Noise/Fractal Cells", node => node.SetValue("Octaves", 3f));

        static int Pits(Lanes picture) {
            var pits = 0;

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    var lowest = true;

                    for (var dy = -2; dy <= 2 && lowest; dy++) {
                        for (var dx = -2; dx <= 2 && lowest; dx++) {
                            lowest = (dx == 0 && dy == 0)
                                || picture.At((x + dx + Side) % Side, (y + dy + Side) % Side, 0) > picture.At(x, y, 0);
                        }
                    }

                    pits += lowest ? 1 : 0;
                }
            }

            return pits;
        }

        var coarse = Pits(one);
        var fine = Pits(three);

        output.WriteLine($"one octave {coarse} pits, three {fine}");

        Assert.Equal((0f, 1f), Range(one));
        Assert.Equal((0f, 1f), Range(three));
        Assert.InRange(coarse, 12, 20);
        Assert.True(fine > 4 * coarse, $"three octaves have {fine} pits against one octave's {coarse}");
    }

    [Fact]
    public void Streaks_vary_far_faster_down_than_across() {
        using var device = TextureKernelHarness.Open();

        var streaks = Compound(device, "Noise/Streaks");
        var plain = Compound(device, "Noise/Streaks", node => node.SetValue("Stretch", 1f));
        var (acrossStreak, downStreak) = Steps(streaks);
        var (acrossPlain, downPlain) = Steps(plain);

        output.WriteLine($"streaks {acrossStreak}/{downStreak}, plain {acrossPlain}/{downPlain}");

        Assert.True(downStreak > 4f * acrossStreak, $"streaks step {downStreak} down and {acrossStreak} across");
        Assert.InRange(downPlain / acrossPlain, 0.6f, 1.6f);
    }

    /// <summary>One spot per cell, a half at its radius in cells, so the share of the image a spot covers is π r².</summary>
    [Fact]
    public void Spots_cover_pi_r_squared_of_every_cell() {
        using var device = TextureKernelHarness.Open();

        float Covered(string radius) =>
            Fraction(
                Compound(device, "Noise/Spots", node => {
                    node.SetValue("Cells", 16f);
                    node.SetText("radius", radius);
                }, 512),
                value => value > 0.5f
            );

        var small = Covered("0.1");
        var large = Covered("0.2");

        output.WriteLine($"small {small}, large {large}");

        // Two points of the lattice closer than two radii overlap and count once, which costs a few
        // percent of the small spots' area and more of the large ones'.
        Assert.Equal(MathF.PI * 0.01f, small, 0.003f);
        Assert.InRange(large / small, 3.2f, 4.05f);
    }

    [Fact]
    public void A_liquid_only_moves_the_values_its_noise_already_had() {
        using var device = TextureKernelHarness.Open();

        var still = Compound(device, "Noise/Liquid", node => node.SetText("flow", "0"));
        var stirred = Compound(device, "Noise/Liquid");
        var (low, high) = Range(still);
        var moved = 0.0;
        double stillSum = 0;
        double stirredSum = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var value = stirred.At(x, y, 0);

                Assert.InRange(value, low - 0.001f, high + 0.001f);
                moved += MathF.Abs(value - still.At(x, y, 0));
                stillSum += still.At(x, y, 0);
                stirredSum += value;
            }
        }

        var texels = Side * Side;

        Assert.True(moved / texels > 0.02, $"a flow of 48 moved the noise by {moved / texels} on average");
        Assert.Equal(stillSum / texels, stirredSum / texels, 0.05);
    }
}
