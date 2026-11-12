// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>
///     Patina #170's per-instance outputs, a Tile Sampler's row offset and a Splatter's ring, through the nodes on a
///     device.
/// </summary>
/// <remarks>
///     A white pattern covers its whole square, so every texel an instance covers carries exactly that
///     instance's number, and every number is placed by arithmetic: a 4×4 grid on 64 texels has cells of 16
///     and, at a scale of a half, instances of eight texels about each cell's centre.
/// </remarks>
public class TexturePlacementOutputDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    const int Cell = 16;

    static readonly byte[] White = Solid(Side, Side, 1f, 1f, 1f);

    static Lanes Sampler(Vixen.Graphics.Vulkan.VulkanDevice device, string draws, float rowOffset = 0f) =>
        Bake(
            device,
            "Placement/Tile Sampler",
            Side,
            Side,
            [new("Pattern", White)],
            node => {
                node.SetText("Output", draws);
                node.SetValue("Grid X", 4f);
                node.SetValue("Grid Y", 4f);
                node.SetValue("Scale", 0.5f);
                node.SetValue("Row Offset", rowOffset);
            }
        );

    [Fact]
    public void Each_instance_draws_its_own_number_over_its_coverage() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var random = Sampler(device, "Random");
        var colour = Sampler(device, "Colour");
        var index = Sampler(device, "Index");
        HashSet<float> seen = [];

        for (var j = 0; j < 4; j++) {
            for (var i = 0; i < 4; i++) {
                var (cx, cy) = (i * Cell + 8, j * Cell + 8);
                var value = random.At(cx, cy, 0);

                // One number over the instance's eight texels, nought in the gap round it.
                for (var y = cy - 3; y < cy + 3; y++) {
                    for (var x = cx - 3; x < cx + 3; x++) {
                        Assert.Equal(value, random.At(x, y, 0), 0.0001f);
                        Assert.Equal((i + j * 4 + 0.5f) / 16f, index.At(x, y, 0), 0.0001f);
                    }
                }

                Assert.Equal(0f, random.At(i * Cell, j * Cell, 0));

                // A colour's red is the grey, and its three lanes are three numbers.
                Assert.Equal(value, colour.At(cx, cy, 0), 0.0001f);
                Assert.NotEqual(colour.At(cx, cy, 1), colour.At(cx, cy, 2));

                seen.Add(value);
            }
        }

        // ⚠ The instrument: sixteen instances, sixteen numbers.
        Assert.Equal(16, seen.Count);
    }

    [Fact]
    public void An_instances_uv_runs_across_its_own_square() {
        using var device = TextureKernelHarness.Open();

        var uv = Sampler(device, "Uv");

        // Cell (1, 2): its instance spans x 20..28 and y 36..44, so texel x reads ((x + ½) − 20) / 8.
        for (var y = 37; y < 43; y++) {
            for (var x = 21; x < 27; x++) {
                Assert.Equal((x + 0.5f - 20f) / 8f, uv.At(x, y, 0), 0.002f);
                Assert.Equal((y + 0.5f - 36f) / 8f, uv.At(x, y, 1), 0.002f);
            }
        }
    }

    [Fact]
    public void A_row_offset_shifts_odd_rows_and_wraps_the_last_instance_round() {
        using var device = TextureKernelHarness.Open();

        var plain = Sampler(device, "Index");
        var bond = Sampler(device, "Index", 0.5f);

        // Row 0 is even and does not move.
        Assert.Equal(plain.At(8, 8, 0), bond.At(8, 8, 0));

        // Row 1 moves half a cell: instance 0 of the row is centred on x = 16, and the row's last one on 64,
        // which is the left edge again.
        Assert.Equal(4.5f / 16f, bond.At(16, 24, 0), 0.0001f);
        Assert.Equal(0f, bond.At(8, 24, 0));
        Assert.Equal(7.5f / 16f, bond.At(1, 24, 0), 0.0001f);
        Assert.Equal(4.5f / 16f, plain.At(8, 24, 0), 0.0001f);
    }

    [Fact]
    public void A_ring_puts_its_instances_evenly_round_a_circle() {
        using var device = TextureKernelHarness.Open();

        var ring = Ring(device, false);

        for (var at = 0; at < 8; at++) {
            var angle = at * MathF.Tau / 8f;
            var (x, y) = ((int)MathF.Floor(Side * (0.5f + 0.3f * MathF.Cos(angle))),
                (int)MathF.Floor(Side * (0.5f + 0.3f * MathF.Sin(angle))));

            Assert.Equal((at + 0.5f) / 8f, ring.At(x, y, 0), 0.0001f);
        }

        Assert.Equal(0f, ring.At(Side / 2, Side / 2, 0));
    }

    [Fact]
    public void Following_the_ring_turns_each_instance_by_its_angle_round_it() {
        using var device = TextureKernelHarness.Open();

        // White on the left half of the pattern, black on the right.
        var half = Picture(Side, Side, static (x, _) => x < Side / 2 ? (1f, 1f, 1f, 1f) : (0f, 0f, 0f, 1f));

        var still = Ring(device, false, half, "Pattern");
        var turned = Ring(device, true, half, "Pattern");

        // Instance 0 is at angle nought, right of the centre, and turns by nothing either way.
        Assert.Equal(1f, still.At(49, 32, 0));
        Assert.Equal(1f, turned.At(49, 32, 0));

        // Instance 4 is at π, left of the centre: still, its white half is on the left; turned half a turn,
        // on the right.
        Assert.Equal(1f, still.At(10, 32, 0));
        Assert.Equal(0f, still.At(14, 32, 0));
        Assert.Equal(0f, turned.At(10, 32, 0));
        Assert.Equal(1f, turned.At(14, 32, 0));
    }

    /// <summary>
    ///     Past 256 the node chains batches, each starting from what the last one left, and under <c>Add</c> the
    ///     mean is the count times one instance's area exactly — so a chain that dropped what it carried would
    ///     be its last batch's share and no more.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(600)]
    [InlineData(1500)]
    public void A_splatter_past_one_batch_places_every_instance(int count) {
        using var device = TextureKernelHarness.Open();

        const int Wide = 128;

        var added = Bake(
            device,
            "Placement/Splatter",
            Wide,
            Wide,
            [new("Pattern", Solid(Wide, Wide, 1f, 1f, 1f))],
            node => {
                node.SetText("Accumulation", "Add");
                node.SetValue("Count", count);
                node.SetValue("Scale", 1f / 32f);
            }
        );

        var sum = 0.0;

        for (var y = 0; y < Wide; y++) {
            for (var x = 0; x < Wide; x++) {
                sum += added.At(x, y, 0);
            }
        }

        // Each instance is four texels square: sixteen of the image's 16 384.
        Assert.Equal(count * 16.0 / (Wide * Wide), sum / (Wide * Wide), 0.005);

        // ⚠ And they are different instances. A batch that walked the first 256 again would add the same
        // area, so the total above cannot see it; the last batch's numbers can.
        var numbered = Bake(
            device,
            "Placement/Splatter",
            Wide,
            Wide,
            [new("Pattern", Solid(Wide, Wide, 1f, 1f, 1f))],
            node => {
                node.SetText("Output", "Index");
                node.SetValue("Count", count);
                node.SetValue("Scale", 1f / 32f);
            }
        );

        var highest = 0f;

        for (var y = 0; y < Wide; y++) {
            for (var x = 0; x < Wide; x++) {
                highest = Math.Max(highest, numbered.At(x, y, 0));
            }
        }

        Assert.True(
            highest > 1f - 8f / count,
            $"the highest instance number drawn is {highest * count - 0.5f} of {count}"
        );
    }

    [Fact]
    public void A_splatter_past_its_ceiling_is_refused_and_not_cut_short() {
        var failure = Assert.Throws<ArgumentException>(static () => TexturePlacement.SplatterChain(
                0,
                [],
                TexturePlacement.Splatter(0, 1),
                TexturePlacement.MaxBatchedInstances + 1
            )
        );

        Assert.Contains(
            TexturePlacement.MaxBatchedInstances.ToString(System.Globalization.CultureInfo.InvariantCulture),
            failure.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(
            16,
            TexturePlacement.SplatterChain(
                    0,
                    [..Enumerable.Range(2, 15)],
                    TexturePlacement.Splatter(0, 1),
                    TexturePlacement.MaxBatchedInstances
                )
                .Length
        );
    }

    /// <summary>A 4×4 grid at a scale of a half with maps wired, each instance eight texels about its cell's centre.</summary>
    static Lanes Mapped(
        Vixen.Graphics.Vulkan.VulkanDevice device,
        Wire[] wires,
        Action<Vixen.NodeGraph.GraphNode>? more = null
    ) =>
        Bake(
            device,
            "Placement/Tile Sampler",
            Side,
            Side,
            wires,
            node => {
                node.SetValue("Grid X", 4f);
                node.SetValue("Grid Y", 4f);
                node.SetValue("Scale", 0.5f);
                more?.Invoke(node);
            }
        );

    /// <summary>
    ///     An index map chooses each instance's pattern by its value at the instance's centre, across the further
    ///     pattern inputs, and with no map the seed chooses among the same inputs.
    /// </summary>
    /// <remarks>
    ///     Three patterns of three greys, and an index map that is a third in the left half and five sixths in the
    ///     right: of three choices those are the second and the third. ⚠ With the map unwired every grey must
    ///     still be one of the three and more than one must appear, or the further inputs were never read.
    /// </remarks>
    [Fact]
    public void An_index_map_chooses_among_the_patterns_and_the_seed_does_without_one() {
        using var device = TextureKernelHarness.Open();

        Wire[] patterns = [
            new("Pattern", Solid(Side, Side, 0.2f, 0.2f, 0.2f)),
            new("Pattern 2", Solid(Side, Side, 0.5f, 0.5f, 0.5f)),
            new("Pattern 3", Solid(Side, Side, 0.8f, 0.8f, 0.8f))
        ];

        var chosen = Mapped(
            device,
            [..patterns, new("Index Map", Grey(Side, Side, static (x, _) => x < Side / 2 ? 0.34f : 0.84f), true)]
        );
        var seeded = Mapped(device, patterns);
        HashSet<float> greys = [];

        for (var j = 0; j < 4; j++) {
            for (var i = 0; i < 4; i++) {
                var (cx, cy) = (i * Cell + 8, j * Cell + 8);

                Assert.Equal(i < 2 ? 0.5f : 0.8f, chosen.At(cx, cy, 0), 0.002f);

                var grey = MathF.Round(seeded.At(cx, cy, 0) * 10f) / 10f;

                Assert.Contains(grey, (float[])[0.2f, 0.5f, 0.8f]);
                greys.Add(grey);
            }
        }

        Assert.True(greys.Count > 1, "Sixteen instances drew one pattern of three: the further inputs were not read.");
    }

    /// <summary>A colour map tints a white pattern by its colour at each centre, and the node writes colour for it.</summary>
    [Fact]
    public void A_colour_map_tints_each_instance_by_its_centre() {
        using var device = TextureKernelHarness.Open();

        var tints = Picture(Side, Side, static (x, y) => (x < Side / 2 ? 1f : 0f, y < Side / 2 ? 1f : 0f, 0.5f, 1f));
        var tinted = Mapped(device, [new("Pattern", White), new("Colour Map", tints)]);
        var half = Mapped(
            device,
            [new("Pattern", White), new("Colour Map", tints)],
            static node => node.SetValue("Colour Amount", 0.5f)
        );

        for (var j = 0; j < 4; j++) {
            for (var i = 0; i < 4; i++) {
                var (cx, cy) = (i * Cell + 8, j * Cell + 8);
                var (red, green) = (i < 2 ? 1f : 0f, j < 2 ? 1f : 0f);

                Assert.Equal(red, tinted.At(cx, cy, 0), 0.002f);
                Assert.Equal(green, tinted.At(cx, cy, 1), 0.002f);
                Assert.Equal(0.5f, tinted.At(cx, cy, 2), 0.002f);
                Assert.Equal(0.5f + red * 0.5f, half.At(cx, cy, 0), 0.002f);
            }
        }
    }

    /// <summary>A displacement map moves every instance by its value about a half, in cells, and nought where unwired.</summary>
    /// <remarks>
    ///     A map of three quarters on red and a half on green moves every instance half of <c>Displacement Amount</c>
    ///     cells right: at an amount of a half, four texels. So the texel four left of the cell's centre, inside
    ///     the instance before, is now its left edge's neighbour outside it, and four past the old right edge is in.
    /// </remarks>
    [Fact]
    public void A_displacement_map_moves_each_instance_in_cells() {
        using var device = TextureKernelHarness.Open();

        var push = Solid(Side, Side, 0.75f, 0.5f, 0.5f);
        var still = Mapped(device, [new("Pattern", White)]);
        var moved = Mapped(
            device,
            [new("Pattern", White), new("Displacement Map", push)],
            static node => node.SetValue("Displacement Amount", 0.5f)
        );

        for (var j = 0; j < 4; j++) {
            for (var i = 0; i < 4; i++) {
                var (cx, cy) = (i * Cell + 8, j * Cell + 8);

                // Unmoved, the instance spans cx − 4 … cx + 3. Moved four right, cx … cx + 7.
                Assert.Equal(1f, still.At(cx - 3, cy, 0), 0.002f);
                Assert.Equal(0f, still.At(cx + 5, cy, 0), 0.002f);
                Assert.Equal(0f, moved.At(cx - 3, cy, 0), 0.002f);
                Assert.Equal(1f, moved.At(cx + 5, cy, 0), 0.002f);
            }
        }
    }

    /// <summary>A vector map turns each instance to face along it: a long thin pattern pointing down stands upright.</summary>
    [Fact]
    public void A_vector_map_turns_each_instance_to_face_along_it() {
        using var device = TextureKernelHarness.Open();

        // A bar across the middle of the pattern, the full width and a quarter of the height.
        var bar = Grey(Side, Side, static (_, y) => y is >= 24 and < 40 ? 1f : 0f);
        var down = Solid(Side, Side, 0.5f, 1f, 0.5f);
        var flat = Mapped(device, [new("Pattern", bar)]);
        var turned = Mapped(device, [new("Pattern", bar), new("Vector Map", down)]);

        // An instance is eight texels; its bar is two tall across all eight, and turned a quarter it is two
        // wide down all eight.
        var (cx, cy) = (8, 8);

        Assert.Equal(1f, flat.At(cx - 3, cy, 0), 0.002f);
        Assert.Equal(0f, flat.At(cx, cy - 3, 0), 0.002f);
        Assert.Equal(0f, turned.At(cx - 3, cy, 0), 0.002f);
        Assert.Equal(1f, turned.At(cx, cy - 3, 0), 0.002f);
    }

    static Lanes Ring(
        Vixen.Graphics.Vulkan.VulkanDevice device,
        bool follow,
        byte[]? pattern = null,
        string draws = "Index"
    ) =>
        Bake(
            device,
            "Placement/Splatter",
            Side,
            Side,
            [new("Pattern", pattern ?? White)],
            node => {
                node.SetText("Distribution", "Ring");
                node.SetText("Output", draws);
                node.SetValue("Count", 8f);
                node.SetValue("Scale", 0.125f);
                node.SetValue("Ring Radius", 0.3f);
                node.SetValue("Follow Ring", follow ? 1f : 0f);
            }
        );
}
