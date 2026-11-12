// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #167's blue noise: the void-and-cluster tile, and the node that reads it.</summary>
/// <remarks>
///     <para>
///         <b>Blue is a property of every threshold, so that is what is measured.</b> The tile is a rank order:
///         each texel's rank over the count. Cut at any level, the points that remain have to be spread out —
///         no two closer than a white noise of the same density would routinely put them. The nearest pair is
///         the measure, on the torus, because the tile repeats.
///     </para>
///     <para>
///         The node is held to reading that tile and nothing else: one texel of the image per texel of the tile
///         at a scale that makes them equal, every rank once, and a seed that moves the tile without changing
///         what it holds.
///     </para>
/// </remarks>
public class TextureBlueNoiseTests(ITestOutputHelper output) {
    const int Side = TextureBlueNoise.Side;

    /// <summary>The nearest two of the texels at or above a rank, on the torus, in texels.</summary>
    static double Nearest(IReadOnlyList<float> ranks, float level) {
        List<(int X, int Y)> points = [];

        for (var index = 0; index < ranks.Count; index++) {
            if (ranks[index] >= level) {
                points.Add((index % Side, index / Side));
            }
        }

        var nearest = double.MaxValue;

        for (var a = 0; a < points.Count; a++) {
            for (var b = a + 1; b < points.Count; b++) {
                var dx = Math.Abs(points[a].X - points[b].X);
                var dy = Math.Abs(points[a].Y - points[b].Y);

                dx = Math.Min(dx, Side - dx);
                dy = Math.Min(dy, Side - dy);
                nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy));
            }
        }

        return nearest;
    }

    [Fact]
    public void The_tile_is_every_rank_once() {
        var ranks = TextureBlueNoise.Ranks;

        Assert.Equal(Side * Side, ranks.Count);
        Assert.Equal(
            Enumerable.Range(0, Side * Side).Select(static rank => rank / (float)(Side * Side)),
            ranks.Order()
        );
    }

    /// <summary>How unevenly the texels at or above a rank fill the tile: the variance of their share of each 8×8 window.</summary>
    /// <remarks>
    ///     A low-pass of the threshold, read as its variance — which is the threshold's energy at the low frequencies,
    ///     the band a blue noise is empty in. For a white noise at density <c>p</c> it is <c>p(1 − p) / 64</c>.
    /// </remarks>
    static double Uneven(IReadOnlyList<float> ranks, float level) {
        const int Window = 8;

        var (sum, squares) = (0.0, 0.0);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var lit = 0;

                for (var dy = 0; dy < Window; dy++) {
                    for (var dx = 0; dx < Window; dx++) {
                        lit += ranks[(y + dy) % Side * Side + (x + dx) % Side] >= level ? 1 : 0;
                    }
                }

                var share = lit / (double)(Window * Window);

                sum += share;
                squares += share * share;
            }
        }

        var mean = sum / (Side * Side);

        return squares / (Side * Side) - mean * mean;
    }

    /// <summary>Every threshold spreads its points evenly, where a white noise of the same density clumps.</summary>
    /// <remarks>
    ///     ⚠ <b>The bar is the white noise's own, measured beside it</b>: a shuffled copy of the same ranks is the
    ///     same histogram with the order thrown away, so the only thing the two can differ in is the arrangement.
    ///     Every 8×8 window of a blue threshold holds close to its share, so the windows' variance is a small part of
    ///     a white noise's. At a tenth the nearest pair is asked too: no two of void and cluster's points are that
    ///     close there. ⚠ A count of points touching another was tried first and could not tell the two apart at a
    ///     half, where a blue threshold is a maze whose every point has a neighbour.
    /// </remarks>
    [Theory]
    [InlineData(0.9f)]
    [InlineData(0.75f)]
    [InlineData(0.5f)]
    [InlineData(0.1f)]
    public void Every_threshold_spreads_its_points_out(float level) {
        var ranks = TextureBlueNoise.Ranks;
        var random = new Random(4409);
        var shuffled = ranks.OrderBy(_ => random.Next()).ToArray();

        var blue = Uneven(ranks, level);
        var white = Uneven(shuffled, level);
        var expected = level * (1 - level) / 64;

        output.WriteLine(
            $"at {level}: blue's windows vary by {blue:E2}, white's by {white:E2} (expected {expected:E2})"
        );

        Assert.True(white > expected * 0.5, $"The white instrument's {white:E2} is not a white noise's {expected:E2}.");
        Assert.True(blue < white * 0.2, $"Above {level} the windows vary by {blue:E2}, against white's {white:E2}.");

        if (level >= 0.9f) {
            Assert.True(
                Nearest(ranks, level) >= 2.0,
                $"Two of the tenth brightest are {Nearest(ranks, level):0.00} texels apart."
            );
            Assert.True(Nearest(shuffled, level) < 2.0, "The white instrument's tenth has no close pair either.");
        }
    }

    /// <summary>At one tile texel per image texel, the node draws the tile's ranks, each once, under any seed.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void The_node_draws_the_tile_one_texel_to_one(int seed) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var noise = Bake(
            device,
            "Source/Blue Noise",
            Side,
            Side,
            [],
            node => {
                node.SetValue("Scale", 1f);
                node.SetValue("Seed", seed);
            }
        );

        var drawn = new List<float>(Side * Side);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                drawn.Add(noise.At(x, y, 0));
            }
        }

        Assert.Equal(
            TextureBlueNoise.Ranks.Order().Select(static rank => MathF.Round(rank * Side * Side)),
            drawn.Order().Select(static value => MathF.Round(value * Side * Side))
        );
    }

    /// <summary>A seed is another arrangement of the same tile, and nought is the graph's own.</summary>
    [Fact]
    public void A_seed_moves_the_tile() {
        using var device = TextureKernelHarness.Open();

        var first = Bake(device, "Source/Blue Noise", Side, Side, [], static node => node.SetValue("Scale", 1f));
        var again = Bake(device, "Source/Blue Noise", Side, Side, [], static node => node.SetValue("Scale", 1f));
        var other = Bake(
            device,
            "Source/Blue Noise",
            Side,
            Side,
            [],
            static node => {
                node.SetValue("Scale", 1f);
                node.SetValue("Seed", 5f);
            }
        );

        var same = 0;
        var moved = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                same += first.At(x, y, 0) == again.At(x, y, 0) ? 1 : 0;
                moved += first.At(x, y, 0) != other.At(x, y, 0) ? 1 : 0;
            }
        }

        Assert.Equal(Side * Side, same);
        Assert.True(moved > Side * Side / 2, $"Only {moved} texels moved under another seed.");
    }
}
