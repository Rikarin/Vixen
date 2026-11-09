// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #167's noise options, each through <c>Source/Noise</c> on a device, against identities.</summary>
/// <remarks>
///     <para>
///         <b>Identities rather than pictures</b>, because a noise has no closed form a test can spell, and it
///         does not need one: a lattice shifted by whole texels is the same picture rolled, a sum that starts
///         an octave later is the sum's own tail, and a fold is a function of the plain value at the same
///         texel. Each holds to rounding, and each is false for the kernel that ignores the option.
///     </para>
///     <para>
///         Every bake here is one graph holding one <c>Source/Noise</c>, placed first, so every bake of one
///         test draws from the same seed and two bakes differ only in the numbers the test sets.
///     </para>
/// </remarks>
public class TextureNoiseFamilyDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    static Lanes Noise(
        Vixen.Graphics.Vulkan.VulkanDevice device,
        string basis,
        Action<GraphNode> numbers,
        int side = Side,
        bool tiling = true
    ) =>
        Bake(device, "Source/Noise", side, side, [], node => {
            node.SetText("Basis", basis);
            numbers(node);
        }, tiling: tiling);

    static void Each(int side, Action<int, int> check) {
        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                check(x, y);
            }
        }
    }

    /// <summary>
    ///     The default eight cells are eight texels each on a 64-texel image, so five eighths of a cell is five
    ///     texels, and a shift of the whole scale is the whole image.
    /// </summary>
    [Fact]
    public void A_whole_texel_offset_rolls_the_picture_and_an_offset_of_the_scale_is_the_identity() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        foreach (var basis in (string[])["Gradient", "Value", "Gaussian"]) {
            var still = Noise(device, basis, node => node.SetValue("Octaves", 3f));
            var rolled = Noise(device, basis, node => {
                node.SetValue("Octaves", 3f);
                node.SetValue("Offset X", 5f / 8f);
                node.SetValue("Offset Y", 9f / 8f);
            });
            var round = Noise(device, basis, node => {
                node.SetValue("Octaves", 3f);
                node.SetValue("Offset X", 8f);
                node.SetValue("Offset Y", -16f);
            });

            Each(Side, (x, y) => {
                Assert.Equal(still.At((x + 5) % Side, (y + 9) % Side, 0), rolled.At(x, y, 0), 0.0005f);
                Assert.Equal(still.At(x, y, 0), round.At(x, y, 0), 0.0005f);
            });

            // ⚠ And the offset moved something: a roll of a field that ignored it is the field.
            Assert.True(MathF.Abs(still.At(3, 3, 0) - rolled.At(3, 3, 0)) > 0.0001f || MathF.Abs(still.At(40, 17, 0) - rolled.At(40, 17, 0)) > 0.0001f);
        }
    }

    /// <summary>
    ///     Patina #120's seed: nought is the picture the node always drew, the same number draws the same picture, and
    ///     another number draws a field that agrees with the first nowhere but by chance.
    /// </summary>
    [Fact]
    public void A_seed_of_nought_is_the_old_picture_and_another_seed_is_another_draw() {
        using var device = TextureKernelHarness.Open();

        foreach (var basis in (string[])["Gradient", "Value", "Worley", "White"]) {
            var plain = Noise(device, basis, static node => node.SetValue("Octaves", 3f));
            var nought = Noise(device, basis, static node => {
                node.SetValue("Octaves", 3f);
                node.SetValue("Seed", 0f);
            });
            var seven = Noise(device, basis, static node => {
                node.SetValue("Octaves", 3f);
                node.SetValue("Seed", 7f);
            });
            var again = Noise(device, basis, static node => {
                node.SetValue("Octaves", 3f);
                node.SetValue("Seed", 7f);
            });
            var moved = 0;

            Each(Side, (x, y) => {
                Assert.Equal(plain.At(x, y, 0), nought.At(x, y, 0), 0.0001f);
                Assert.Equal(seven.At(x, y, 0), again.At(x, y, 0), 0.0001f);

                if (MathF.Abs(plain.At(x, y, 0) - seven.At(x, y, 0)) > 0.01f) {
                    moved++;
                }
            });

            // Most texels of an independent draw disagree; a seed the kernel ignored would move none.
            Assert.True(moved > Side * Side / 2, $"{basis}: a seed of 7 moved {moved} of {Side * Side} texels.");
        }
    }

    [Fact]
    public void Scale_x_and_y_stretch_the_lattice_along_one_axis_each() {
        using var device = TextureKernelHarness.Open();

        // Sixteen white cells across and eight down: a 64-texel image is constant on blocks four wide and
        // eight tall, and no taller — whether the axis rounds to a period because it wraps or takes the
        // number as it is because it does not.
        foreach (var tiling in (bool[])[true, false]) {
            Blocks(Noise(device, "White", node => {
                node.SetValue("Scale", 16f);
                node.SetValue("Scale Y", 0.5f);
            }, tiling: tiling));
        }

        // And x alone: thirty-two across and eight down.
        var stretched = Noise(device, "White", node => {
            node.SetValue("Scale", 8f);
            node.SetValue("Scale X", 4f);
        });

        Each(Side, (x, y) => Assert.Equal(stretched.At(x - (x % 2), y - (y % 8), 0), stretched.At(x, y, 0), 0.0001f));
        Assert.NotEqual(stretched.At(0, 0, 0), stretched.At(2, 0, 0), 0.0001f);
    }

    static void Blocks(Lanes white) {
        HashSet<float> blocks = [];

        for (var by = 0; by < 8; by++) {
            for (var bx = 0; bx < 16; bx++) {
                var value = white.At(bx * 4, by * 8, 0);

                for (var y = 0; y < 8; y++) {
                    for (var x = 0; x < 4; x++) {
                        Assert.Equal(value, white.At((bx * 4) + x, (by * 8) + y, 0), 0.0001f);
                    }
                }

                blocks.Add(MathF.Round(value, 4));
            }
        }

        Assert.True(blocks.Count >= 120, $"{blocks.Count} distinct blocks of 128: the cells are not one value each.");
    }

    /// <summary>A two-octave sum is its first octave plus <c>gain</c> times the sum that starts one octave on.</summary>
    [Fact]
    public void A_sum_that_starts_an_octave_later_is_the_tail_of_the_whole_sum() {
        using var device = TextureKernelHarness.Open();

        const float Gain = 0.6f;

        foreach (var (basis, profile) in (ReadOnlySpan<(string, string)>)[("Value", "Plain"), ("Gradient", "Ridged"), ("Worley", "Plain")]) {
            Lanes Sum(int first, int count) =>
                Noise(device, basis, node => {
                    node.SetText("Profile", profile);
                    node.SetValue("Scale", 4f);
                    node.SetValue("Gain", Gain);
                    node.SetValue("Min Octave", first);
                    node.SetValue("Octaves", count);
                });

            var whole = Sum(0, 2);
            var head = Sum(0, 1);
            var tail = Sum(1, 1);
            var differs = 0;

            Each(Side, (x, y) => {
                // Worley sums F1 in red and F2 in green; every other basis is grey.
                for (var lane = 0; lane < (basis == "Worley" ? 2 : 1); lane++) {
                    var expected = (head.At(x, y, lane) + (Gain * tail.At(x, y, lane))) / (1f + Gain);

                    Assert.Equal(expected, whole.At(x, y, lane), 0.0005f);
                }

                differs += MathF.Abs(head.At(x, y, 0) - tail.At(x, y, 0)) > 0.05f ? 1 : 0;
            });

            // ⚠ The tail is its own octave: a kernel that ignored the first octave would make the identity
            // hold between three copies of one picture.
            Assert.True(differs > (Side * Side) / 4, $"{basis}: the octave a sum starts at made no difference.");

            if (basis == "Worley") {
                // The cell index is the first summed octave's, and the sum leaves it alone.
                Each(Side, (x, y) => Assert.Equal(head.At(x, y, 2), whole.At(x, y, 2), 0.0001f));
            }
        }
    }

    [Fact]
    public void Ridged_and_billow_fold_each_octave_about_a_half() {
        using var device = TextureKernelHarness.Open();

        Lanes Folded(string profile, int octaves) =>
            Noise(device, "Gradient", node => {
                node.SetText("Profile", profile);
                node.SetValue("Octaves", octaves);
            });

        var plain = Folded("Plain", 1);
        var ridged = Folded("Ridged", 1);
        var billow = Folded("Billow", 1);

        Each(Side, (x, y) => {
            var v = plain.At(x, y, 0);

            Assert.Equal(1f - MathF.Abs((2f * v) - 1f), ridged.At(x, y, 0), 0.0005f);
            Assert.Equal(MathF.Abs((2f * v) - 1f), billow.At(x, y, 0), 0.0005f);
        });

        // ⚠ Per octave and not on the sum: folding a three-octave sum once is a contour line, and it is not
        // what the kernel draws.
        var sum = Folded("Plain", 3);
        var foldedSum = Folded("Ridged", 3);
        var largest = 0f;

        Each(Side, (x, y) => largest = MathF.Max(largest, MathF.Abs(1f - MathF.Abs((2f * sum.At(x, y, 0)) - 1f) - foldedSum.At(x, y, 0))));

        Assert.True(largest > 0.05f, $"A ridged sum is within {largest} of the folded plain sum.");
    }

    /// <summary>
    ///     With every option turned at once, at numbers that are not whole, the field still meets itself across
    ///     both edges: each axis rounds its own period.
    /// </summary>
    [Fact]
    public void Every_option_at_once_still_tiles() {
        using var device = TextureKernelHarness.Open();

        foreach (var basis in (string[])["Value", "Gradient", "Worley", "White", "Gaussian"]) {
            var field = Noise(device, basis, node => {
                node.SetText("Profile", "Ridged");
                node.SetValue("Scale", 3.3f);
                node.SetValue("Scale X", 1.7f);
                node.SetValue("Scale Y", 0.6f);
                node.SetValue("Offset X", 0.31f);
                node.SetValue("Offset Y", 0.77f);
                node.SetValue("Min Octave", 1f);
                node.SetValue("Octaves", 3f);
                node.SetValue("Lacunarity", 2.3f);
            }, 128);

            for (var lane = 0; lane < (basis == "Worley" ? 2 : 1); lane++) {
                float Mean(Func<int, float> difference) {
                    var sum = 0f;

                    for (var line = 0; line < 128; line++) {
                        sum += difference(line);
                    }

                    return sum / 128f;
                }

                var acrossX = Mean(y => MathF.Abs(field.At(127, y, lane) - field.At(0, y, lane)));
                var acrossY = Mean(x => MathF.Abs(field.At(x, 127, lane) - field.At(x, 0, lane)));
                var stepX = 0f;
                var stepY = 0f;

                for (var at = 0; at < 127; at++) {
                    stepX = MathF.Max(stepX, Mean(y => MathF.Abs(field.At(at + 1, y, lane) - field.At(at, y, lane))));
                    stepY = MathF.Max(stepY, Mean(x => MathF.Abs(field.At(x, at + 1, lane) - field.At(x, at, lane))));
                }

                Assert.True(acrossX <= (1.2f * stepX) + 0.001f, $"{basis} lane {lane}: x seam {acrossX} against steps of {stepX}");
                Assert.True(acrossY <= (1.2f * stepY) + 0.001f, $"{basis} lane {lane}: y seam {acrossY} against steps of {stepY}");
            }
        }
    }

    /// <summary>Sampled on its lattice points, a gaussian lattice's histogram is a bell about a half.</summary>
    /// <remarks>
    ///     An offset of half a texel against a lattice of one cell per texel puts every texel centre on a
    ///     corner, where value noise is exactly the corner's value — so the picture is the lattice itself,
    ///     and its moments are the distribution's. White noise at the same place is the uniform control.
    /// </remarks>
    [Fact]
    public void A_gaussian_lattice_has_a_bell_shaped_histogram() {
        using var device = TextureKernelHarness.Open();

        const int Wide = 128;

        (float Mean, float Deviation, float WithinOne) Moments(string basis) {
            var lattice = Noise(device, basis, node => {
                node.SetValue("Scale", Wide);
                node.SetValue("Offset X", -0.5f);
                node.SetValue("Offset Y", -0.5f);
            }, Wide);

            double sum = 0;
            double squares = 0;

            Each(Wide, (x, y) => {
                sum += lattice.At(x, y, 0);
                squares += lattice.At(x, y, 0) * lattice.At(x, y, 0);
            });

            var mean = sum / (Wide * Wide);
            var deviation = Math.Sqrt((squares / (Wide * Wide)) - (mean * mean));
            var within = 0;

            Each(Wide, (x, y) => within += Math.Abs(lattice.At(x, y, 0) - mean) < deviation ? 1 : 0);

            return ((float)mean, (float)deviation, within / (float)(Wide * Wide));
        }

        var gaussian = Moments("Gaussian");
        var white = Moments("White");

        output.WriteLine($"gaussian {gaussian}, white {white}");

        Assert.Equal(0.5f, gaussian.Mean, 0.01f);
        Assert.Equal(1f / 6f, gaussian.Deviation, 0.01f);
        Assert.Equal(0.683f, gaussian.WithinOne, 0.02f);

        // The control: the same sampling of a uniform lattice is a plateau, one in √12 wide.
        Assert.Equal(0.2887f, white.Deviation, 0.01f);
        Assert.Equal(0.577f, white.WithinOne, 0.02f);
    }
}
