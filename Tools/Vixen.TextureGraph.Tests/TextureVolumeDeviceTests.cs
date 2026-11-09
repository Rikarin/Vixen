// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #179's nodes driven by a position map, each through its node on a device.</summary>
/// <remarks>
///     The position map is spelled in bytes, so every position is exactly <c>b / 255</c> and a test can say
///     what each texel stands for. The property that matters for a seam is that the output is a function of
///     the position alone, wherever on the image that position is; the closed forms are a ramp's projection
///     and a box's or ellipsoid's extent.
/// </remarks>
public class TextureVolumeDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>Red runs across, green down, blue is fixed: every texel a different point of a plane.</summary>
    static readonly byte[] Plane = Picture(Side, Side, (x, y) => (x * 3 / 255f, y * 3 / 255f, 128 / 255f, 1f));

    /// <summary>The same points, the image mirrored left to right.</summary>
    static readonly byte[] Mirrored = Picture(Side, Side, (x, y) => ((Side - 1 - x) * 3 / 255f, y * 3 / 255f, 128 / 255f, 1f));

    [Theory]
    [InlineData("Gradient")]
    [InlineData("Simplex")]
    [InlineData("Worley")]
    [InlineData("Value")]
    public void A_volume_noise_is_a_function_of_the_position_and_not_of_the_texel(string basis) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        void Configure(Vixen.NodeGraph.GraphNode node) {
            node.SetText("Basis", basis);
            node.SetValue("Scale", 5f);
        }

        var straight = Bake(device, "Source/Volume Noise", Side, Side, [new("Position", Plane)], Configure);
        var mirrored = Bake(device, "Source/Volume Noise", Side, Side, [new("Position", Mirrored)], Configure);

        var lowest = 1f;
        var highest = 0f;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(straight.At(x, y, 0), mirrored.At(Side - 1 - x, y, 0), 0.0001f);

                lowest = Math.Min(lowest, straight.At(x, y, 0));
                highest = Math.Max(highest, straight.At(x, y, 0));
            }
        }

        // ⚠ The instrument: a flat picture is a function of anything.
        Assert.True(highest - lowest > 0.25f, $"{basis} spans only {lowest}..{highest}");
    }

    [Fact]
    public void An_offset_of_one_cell_is_the_position_moved_by_one_cell() {
        using var device = TextureKernelHarness.Open();

        // At a scale of five a cell is a fifth of the bounds, which is 51 bytes exactly.
        var moved = Picture(Side, Side, (x, y) => ((x * 3 + 51) / 255f, y * 3 / 255f, 128 / 255f, 1f));

        var shifted = Bake(
            device,
            "Source/Volume Noise",
            Side,
            Side,
            [new("Position", Plane)],
            node => {
                node.SetValue("Scale", 5f);
                node.SetValue("Offset X", 1f);
            }
        );

        var placed = Bake(device, "Source/Volume Noise", Side, Side, [new("Position", moved)], node => node.SetValue("Scale", 5f));
        var unshifted = Bake(device, "Source/Volume Noise", Side, Side, [new("Position", Plane)], node => node.SetValue("Scale", 5f));

        var same = 0.0;
        var apart = 0.0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                same += Math.Abs(shifted.At(x, y, 0) - placed.At(x, y, 0));
                apart += Math.Abs(shifted.At(x, y, 0) - unshifted.At(x, y, 0));
            }
        }

        Assert.True(same / (Side * Side) < 0.001, $"mean difference {same / (Side * Side)}");
        Assert.True(apart / (Side * Side) > 0.02, "the offset moved nothing");
    }

    [Fact]
    public void More_octaves_add_detail_and_keep_the_mean() {
        using var device = TextureKernelHarness.Open();

        static double Mean(TextureNodeBench.Lanes lanes) {
            var sum = 0.0;

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    sum += lanes.At(x, y, 0);
                }
            }

            return sum / (Side * Side);
        }

        // ⚠ The second difference and not the first. Each octave is half as strong and twice as fine, so its
        // slope is the same as the first octave's and a sum of first differences barely moves — measured, five
        // per cent. Its curvature doubles per octave, which is what detail is.
        static double Roughness(TextureNodeBench.Lanes lanes) {
            var sum = 0.0;

            for (var y = 0; y < Side; y++) {
                for (var x = 1; x < Side - 1; x++) {
                    sum += Math.Abs(lanes.At(x + 1, y, 0) - (2f * lanes.At(x, y, 0)) + lanes.At(x - 1, y, 0));
                }
            }

            return sum;
        }

        // One cell across the bounds, so four octaves are eight cells over the 64 texels the plane spans and
        // none of them is finer than a texel.
        var one = Bake(device, "Source/Volume Noise", Side, Side, [new("Position", Plane)], node => {
            node.SetValue("Scale", 1f);
            node.SetValue("Octaves", 1f);
        });

        var four = Bake(device, "Source/Volume Noise", Side, Side, [new("Position", Plane)], node => {
            node.SetValue("Scale", 1f);
            node.SetValue("Octaves", 4f);
        });

        Assert.True(Roughness(four) > Roughness(one) * 2, $"{Roughness(four)} against {Roughness(one)}");
        Assert.Equal(Mean(one), Mean(four), 0.08);
    }

    [Fact]
    public void A_two_point_gradient_is_the_position_projected_onto_the_line_between_them() {
        using var device = TextureKernelHarness.Open();

        void Ends(Vixen.NodeGraph.GraphNode node, (float X, float Y, float Z) start, (float X, float Y, float Z) end) {
            node.SetValue("Start X", start.X);
            node.SetValue("Start Y", start.Y);
            node.SetValue("Start Z", start.Z);
            node.SetValue("End X", end.X);
            node.SetValue("End Y", end.Y);
            node.SetValue("End Z", end.Z);
        }

        var across = Bake(device, "Source/Volume Gradient", Side, Side, [new("Position", Plane)], node => Ends(node, (0.2f, 0.3f, 0f), (0.8f, 0.3f, 0f)));
        var diagonal = Bake(device, "Source/Volume Gradient", Side, Side, [new("Position", Plane)], node => Ends(node, (0f, 0f, 0.5f), (0.5f, 0.5f, 0.5f)));

        for (var y = 0; y < Side; y += 7) {
            for (var x = 0; x < Side; x += 5) {
                var (r, g) = (x * 3 / 255f, y * 3 / 255f);

                // ⚠ A start away from the origin, so a projection of the position rather than of its offset from the
                // start is a different number.
                Assert.Equal(Math.Clamp((r - 0.2f) / 0.6f, 0f, 1f), across.At(x, y, 0), 0.0005f);
                Assert.Equal(Math.Clamp(r + g, 0f, 1f), diagonal.At(x, y, 0), 0.0005f);
            }
        }
    }

    [Theory]
    [InlineData("Box")]
    [InlineData("Ellipsoid")]
    public void A_volume_mask_is_one_inside_its_extent_and_fades_over_its_falloff_outside(string shape) {
        using var device = TextureKernelHarness.Open();

        var mask = Bake(
            device,
            "Source/Volume Mask",
            Side,
            Side,
            [new("Position", Plane)],
            node => {
                node.SetText("Shape", shape);
                node.SetValue("Centre X", 0.25f);
                node.SetValue("Centre Y", 0.25f);
                node.SetValue("Centre Z", 128 / 255f);
                node.SetValue("Size X", 0.2f);
                node.SetValue("Size Y", 0.2f);
                node.SetValue("Size Z", 0.2f);
                node.SetValue("Falloff", 0.1f);
            }
        );

        var inside = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var (dx, dy) = (Math.Abs((x * 3 / 255f) - 0.25f), Math.Abs((y * 3 / 255f) - 0.25f));
                var outside = shape == "Box"
                    ? MathF.Sqrt(MathF.Pow(Math.Max(dx - 0.1f, 0f), 2) + MathF.Pow(Math.Max(dy - 0.1f, 0f), 2))
                    : MathF.Max(MathF.Sqrt((dx * dx) + (dy * dy)) - 0.1f, 0f);

                Assert.Equal(1f - Math.Clamp(outside / 0.1f, 0f, 1f), mask.At(x, y, 0), 0.002f);

                inside += outside == 0f ? 1 : 0;
            }
        }

        // The instrument: both halves of the shape were on the picture.
        Assert.InRange(inside, 20, Side * Side / 2);
        Assert.Equal(0f, mask.At(Side - 1, Side - 1, 0));
    }
}
