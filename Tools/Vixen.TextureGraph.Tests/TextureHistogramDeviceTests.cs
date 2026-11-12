// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #172's histogram and summed-area nodes, each through its node on a device, against counts.</summary>
/// <remarks>
///     <para>
///         <b>The pictures are written as bytes, and a byte is a bin</b>: the bench reads an RGBA8 texel as
///         <c>b / 255</c>, and <c>floor(256 · b / 255)</c> is <c>b</c> for every byte but 255, which shares
///         the top bin with 254 — the clamp the kernel documents. So a picture spelled in bytes has a
///         histogram the test can count on the CPU, exactly, and every expected number below is a count.
///     </para>
///     <para>
///         ⚠ <b>Sizes that are not the counting kernel's own.</b> The counts are allocated at a level between
///         256 and 511 rows tall, so a 256-texel picture is counted at its own size, a 64-texel one at four
///         times it and a 1024-texel one at a quarter — three different partitions of the source's columns
///         into the counts' texels, each of which has to count every texel once.
///     </para>
/// </remarks>
public class TextureHistogramDeviceTests(ITestOutputHelper output) {
    /// <summary>A picture whose byte histogram is easy to write down: the top half is <c>x</c>, the bottom half <c>x / 2</c>.</summary>
    static byte[] Halves(int side) =>
        Grey(side, side, (x, y) => (y < side / 2 ? x * 256 / side : x * 128 / side) / 255f);

    /// <summary>The byte each texel of <see cref="Halves" /> holds.</summary>
    static int HalvesByte(int side, int x, int y) => y < side / 2 ? x * 256 / side : x * 128 / side;

    /// <summary>The share of each bin, counted on the CPU.</summary>
    static double[] Shares(int side, Func<int, int, int> bin) {
        var counts = new double[256];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                counts[Math.Min(bin(x, y), 255)]++;
            }
        }

        return [..counts.Select(count => count / (side * side))];
    }

    /// <summary>
    ///     A picture whose every column differs from its neighbour: a counter that skipped columns would count
    ///     another histogram. The top half is a scramble of every byte, the bottom half the lower 128 only.
    /// </summary>
    static int MixedByte(int x, int y, int side) => y < side / 2 ? (x * 37 + y * y) % 256 : x * 5 % 128;

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1024)]
    public void The_density_strip_is_each_bins_share_times_256_at_any_size(int side) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = Grey(side, side, (x, y) => MixedByte(x, y, side) / 255f);
        var shares = Shares(side, (x, y) => MixedByte(x, y, side));
        var density = Bake(device, "Analysis/Histogram", side, side, [new("Input", picture, true)]);
        var cumulative = Bake(
            device,
            "Analysis/Histogram",
            side,
            side,
            [new("Input", picture, true)],
            static node => node.SetText("Mode", "Cumulative")
        );

        for (var x = 0; x < side; x++) {
            var u = (x + 0.5) / side;
            var bin = Math.Min((int)Math.Floor(u * 256), 255);
            var below = shares.Take(bin).Sum();
            var expected = below + shares[bin] * (u * 256 - bin);

            foreach (var y in (int[])[0, side / 3, side - 1]) {
                Assert.Equal((float)(shares[bin] * 256), density.At(x, y, 0), 0.002f);
                Assert.Equal((float)expected, cumulative.At(x, y, 0), 0.001f);
            }
        }

        // ⚠ The instrument: a histogram that came back flat would be the uniform answer to every question.
        Assert.True(shares.Max() * 256 > 1.2 && shares.Min() * 256 < 0.8);
    }

    /// <summary>
    ///     A sweep is exactly <c>(x + ½) / w</c>, so at 256 texels each texel is the middle of its own bin and the
    ///     histogram is flat — finer than a byte can say, which is where the bin's width is visible at all.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    public void A_sweep_has_a_flat_histogram_and_its_distribution_is_the_identity(int side) {
        using var device = TextureKernelHarness.Open();

        Wire[] sweep = [new("Input", [], true, "Source/Gradient")];

        var density = Bake(device, "Analysis/Histogram", side, side, sweep);
        var cumulative = Bake(
            device,
            "Analysis/Histogram",
            side,
            side,
            sweep,
            static node => node.SetText("Mode", "Cumulative")
        );

        for (var x = 0; x < side; x++) {
            Assert.Equal(1f, density.At(x, side / 2, 0), 0.0001f);
            Assert.Equal((x + 0.5f) / side, cumulative.At(x, side / 2, 0), 0.0001f);
        }
    }

    [Fact]
    public void The_render_is_bars_scaled_to_the_tallest_bin() {
        using var device = TextureKernelHarness.Open();

        const int Side = 256;

        // Every byte below 128 is held by 384 texels and every byte above by 128: bars of one and a third.
        var bars = Bake(device, "Analysis/Histogram Render", Side, Side, [new("Input", Halves(Side), true)]);

        for (var x = 0; x < Side; x++) {
            var height = x < 128 ? 1f : 1f / 3f;

            for (var y = 0; y < Side; y++) {
                var up = 1f - (y + 0.5f) / Side;

                if (MathF.Abs(up - height) > 0.01f) {
                    Assert.Equal(up <= height ? 1f : 0f, bars.At(x, y, 0), 0.0001f);
                }
            }
        }

        var cumulative = Bake(
            device,
            "Analysis/Histogram Render",
            Side,
            Side,
            [new("Input", Halves(Side), true)],
            static node => node.SetValue("Cumulative", 1f)
        );

        // The distribution function: three quarters of the image is below byte 128.
        Assert.Equal(1f, cumulative.At(127, Side - 1 - (int)(0.73f * Side), 0));
        Assert.Equal(0f, cumulative.At(127, Side - 1 - (int)(0.77f * Side), 0));
    }

    [Fact]
    public void Equalising_spreads_a_bunched_grey_evenly_and_leaves_an_even_one_alone() {
        using var device = TextureKernelHarness.Open();

        const int Side = 256;

        // Every byte, 256 times: already even. Byte b is b/255, which is b/255 of the way into bin b, and the
        // line through a bin of share 1/256 starting at b/256 reads it at b/256 + b/(256·255) = b/255.
        var even = Bake(
            device,
            "Colour/Histogram Equalise",
            Side,
            Side,
            [new("Input", Grey(Side, Side, static (x, _) => x / 255f), true)]
        );

        for (var x = 0; x < Side; x++) {
            Assert.Equal(x / 255f, even.At(x, 9, 0), 0.0005f);
        }

        // A square of a ramp is bunched at the dark end: a quarter of it is below 1/16. Equalised, the rank
        // comes back to within a bin's share — and the bins are bytes, so the darkest holds every value
        // under 1/510, about one texel in twenty-two, all read at nought.
        static float Ramp(int x, int y) => (y * Side + x + 0.5f) / (Side * Side);

        var bunched = Grey(Side, Side, (x, y) => Ramp(x, y) * Ramp(x, y));
        var spread = Bake(device, "Colour/Histogram Equalise", Side, Side, [new("Input", bunched, true)]);
        var worst = 0f;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                worst = MathF.Max(worst, MathF.Abs(spread.At(x, y, 0) - Ramp(x, y)));
            }
        }

        output.WriteLine($"worst rank error {worst}");

        // That darkest byte bounds the error: 0.044 of rank, read at nought. A kernel that did not equalise
        // would leave the input, which is up to a quarter from its rank.
        Assert.True(worst < 0.05f, $"an equalised ramp-squared is {worst} from its rank.");

        // Monotonic: a brighter input is never a darker output.
        for (var at = 1; at < Side * Side; at++) {
            Assert.True(spread.At(at % Side, at / Side, 0) >= spread.At((at - 1) % Side, (at - 1) / Side, 0) - 0.0001f);
        }
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(100, 37)]
    [InlineData(512, 512)]
    public void A_summed_area_table_is_the_sum_to_each_texel_over_the_texel_count(int width, int height) {
        using var device = TextureKernelHarness.Open();

        static float Value(int x, int y) => (x * 7 + y * 13) % 256 / 255f;

        var picture = Grey(width, height, Value);
        var table = Bake(device, "Analysis/Summed Area Table", width, height, [new("Input", picture, true)]);

        // The prefix sums, on the CPU, in double.
        var sums = new double[width * height];

        for (var y = 0; y < height; y++) {
            double row = 0;

            for (var x = 0; x < width; x++) {
                row += Unit(Value(x, y));
                sums[y * width + x] = row + (y > 0 ? sums[(y - 1) * width + x] : 0);
            }
        }

        double count = width * height;

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var expected = (float)(sums[y * width + x] / count);

                Assert.True(
                    MathF.Abs(expected - table.At(x, y, 0)) < 1e-5f + expected * 1e-5f,
                    $"({x}, {y}) is {table.At(x, y, 0)} where the sum to it is {expected}"
                );
            }
        }

        // The far corner is the mean.
        Assert.Equal((float)(sums[^1] / count), table.At(width - 1, height - 1, 0), 0.00001f);
    }
}
