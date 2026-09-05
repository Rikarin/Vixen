// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.5's analysis kernels, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>Closed forms, not goldens and not a CPU twin</b> — doc 48 § D3. The three these hold
///         to are the ones § 4.5's own criteria name: <b>a distance transform from a single lit texel
///         is the Euclidean distance, exactly</b>; <b>a flood fill of two disjoint rectangles is two
///         ids with known sizes</b>; and a Sobel over a flat image is zero everywhere while a Sobel
///         over a ramp is that ramp's own slope.
///     </para>
///     <para>
///         ⚠ <b>The single-seed distance field is exact and is asserted as such rather than with a
///         generous tolerance.</b> A jump flood is only approximate when two seeds compete; with one
///         seed every texel's answer is the true distance, and the only slack left is the read-back's
///         own quantisation to eight bits.
///     </para>
///     <para>
///         ⚠ <b>What this file cannot see, measured rather than guessed: the order of the jump
///         flood's steps.</b> Emitting them ascending left every assertion here green, because with a
///         single seed both orders reach every offset — the binary expansion of the distance is the
///         path. The descending sequence matters where seeds compete, and
///         <c>TextureAnalysisKernelTests</c> is what pins it.
///     </para>
/// </remarks>
public class TextureAnalysisDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>A mask with one texel lit.</summary>
    static byte[] OneTexel(int side, int x, int y) {
        var pixels = new byte[side * side * 4];

        for (var texel = 0; texel < side * side; texel++) {
            pixels[(texel * 4) + 3] = 255;
        }

        var at = ((y * side) + x) * 4;

        pixels[at] = 255;
        pixels[at + 1] = 255;
        pixels[at + 2] = 255;

        return pixels;
    }

    /// <summary>A mask with axis-aligned rectangles lit, inclusive of both corners.</summary>
    static byte[] Rectangles(int side, params (int X0, int Y0, int X1, int Y1)[] rectangles) {
        var pixels = new byte[side * side * 4];

        for (var texel = 0; texel < side * side; texel++) {
            pixels[(texel * 4) + 3] = 255;
        }

        foreach (var rectangle in rectangles) {
            for (var y = rectangle.Y0; y <= rectangle.Y1; y++) {
                for (var x = rectangle.X0; x <= rectangle.X1; x++) {
                    var at = ((y * side) + x) * 4;

                    pixels[at] = 255;
                    pixels[at + 1] = 255;
                    pixels[at + 2] = 255;
                }
            }
        }

        return pixels;
    }

    // --- Distance -------------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ The distance from a single lit texel is the Euclidean distance, everywhere, to within
    ///     the read-back's own eight bits.
    /// </summary>
    /// <remarks>
    ///     <b>§ 4.5's own closed form and the reason the node is a jump flood.</b> The field is
    ///     normalised by the maximum distance, so <c>value × maxDistance</c> is a length in texels and
    ///     the assertion is arithmetic rather than a picture. Every texel of the image is checked, not
    ///     a sample of them: a jump flood that lost the seed in one quadrant would still be right in
    ///     the other three.
    /// </remarks>
    [Fact]
    public void A_distance_field_from_one_lit_texel_is_the_euclidean_distance() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int SeedX = 20;
        const int SeedY = 12;

        var field = Field(device, OneTexel(Side, SeedX, SeedY), TextureDistanceMode.Outside, 1f);
        var worst = 0f;
        var worstAt = (0, 0);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var truth = MathF.Min(MathF.Sqrt(((x - SeedX) * (x - SeedX)) + ((y - SeedY) * (y - SeedY))), Side);
                var measured = TextureKernelHarness.At(field, x, y, 0) / 255f * Side;
                var error = MathF.Abs(measured - truth);

                if (error > worst) {
                    worst = error;
                    worstAt = (x, y);
                }
            }
        }

        // One eight-bit step of a 64-texel field is a quarter of a texel, so half a texel is two
        // steps of slack and nothing else. ⚠ It is not slack enough to catch an *ascending* step
        // chain, which was tried and left this green — see
        // `TextureAnalysisKernelTests.A_distance_chain_halves_its_step_from_the_image_down_to_one`,
        // which is the only instrument for that.
        Assert.True(
            worst <= 0.5f,
            $"the worst texel is ({worstAt.Item1}, {worstAt.Item2}), out by {worst:0.00} texels, on "
            + TextureKernelHarness.Adapter(device)
        );

        Assert.Equal(0, TextureKernelHarness.At(field, SeedX, SeedY, 0));
    }

    /// <summary>The inside mode measures to the nearest texel that is <em>not</em> in the mask.</summary>
    /// <remarks>
    ///     ⚠ <b>Both fields flood in one chain, so this is the same six dispatches reading the other
    ///     two channels.</b> A test of the outside mode alone would pass on a kernel that had lost the
    ///     second field entirely — and the picture would be black, which is what an unwritten storage
    ///     image looks like too.
    /// </remarks>
    [Fact]
    public void The_inside_mode_measures_to_the_edge_of_the_shape() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // A 33×33 square from 16 to 48 inclusive: its centre is 17 texels from the nearest texel
        // outside it, counted as a Euclidean distance to the first background texel at x = 15.
        var field = Field(device, Rectangles(Side, (16, 16, 48, 48)), TextureDistanceMode.Inside, 1f);

        Assert.Equal(0, TextureKernelHarness.At(field, 4, 4, 0));
        Assert.Equal(0, TextureKernelHarness.At(field, 15, 32, 0));

        var centre = TextureKernelHarness.At(field, 32, 32, 0) / 255f * Side;

        Assert.True(MathF.Abs(centre - 17f) <= 0.5f, $"the centre reads {centre:0.00} texels rather than 17");

        // One texel inside the boundary is one texel from the outside.
        var edge = TextureKernelHarness.At(field, 16, 32, 0) / 255f * Side;

        Assert.True(MathF.Abs(edge - 1f) <= 0.5f, $"the boundary texel reads {edge:0.00} texels rather than 1");
    }

    /// <summary>The signed mode crosses a half exactly at the boundary.</summary>
    [Fact]
    public void The_signed_mode_crosses_a_half_at_the_boundary() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var field = Field(device, Rectangles(Side, (16, 16, 48, 48)), TextureDistanceMode.Both, 1f);

        // Inside is above a half and outside is below it, and the two texels either side of the
        // boundary are one step apart in each direction.
        Assert.True(TextureKernelHarness.At(field, 16, 32, 0) > 128);
        Assert.True(TextureKernelHarness.At(field, 15, 32, 0) < 128);
        Assert.True(TextureKernelHarness.At(field, 32, 32, 0) > TextureKernelHarness.At(field, 20, 32, 0));
        Assert.True(TextureKernelHarness.At(field, 0, 32, 0) < TextureKernelHarness.At(field, 12, 32, 0));
    }

    // --- Edge Detect ----------------------------------------------------------------------------

    /// <summary>A Sobel over a flat image is zero, and over a ramp it is the ramp's own slope.</summary>
    /// <remarks>
    ///     ⚠ <b>The flat half alone is the assertion a broken kernel passes</b> — an unwritten image
    ///     is black too. The ramp is what makes the pair a claim: its slope is
    ///     <c>1 / 63</c> per texel, the operator straddles two texels, and the magnitude is therefore
    ///     <c>2 / 63</c> — eight of two hundred and fifty-five, which nothing else produces.
    /// </remarks>
    [Fact]
    public void An_edge_detect_is_black_on_a_flat_image_and_a_ramps_slope_on_a_ramp() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var flat = OneOp(device, TextureKernelHarness.Solid(Side, 128, 128, 128, 255), TextureAnalysis.EdgeDetect(1, 0));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(0, TextureKernelHarness.At(flat, x, y, 0));
            }
        }

        var ramp = OneOp(device, TextureKernelHarness.Ramp(Side), TextureAnalysis.EdgeDetect(1, 0));
        var expected = (int)MathF.Round(255f * 2f / 63f);

        // Away from the clamped border, where the operator straddles the edge texel twice.
        for (var x = 2; x < Side - 2; x++) {
            Assert.InRange(TextureKernelHarness.At(ramp, x, 32, 0), expected - 1, expected + 1);
        }
    }

    /// <summary>The threshold is a floor on the magnitude and not a scale on the answer.</summary>
    [Fact]
    public void An_edge_detect_threshold_is_a_floor() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // The ramp's own magnitude is 2/63 ≈ 0.032, so a threshold above it takes the whole picture
        // to black while a threshold of zero leaves it alone.
        var raised = OneOp(
            device,
            TextureKernelHarness.Ramp(Side),
            TextureAnalysis.EdgeDetect(1, 0, threshold: 0.1f)
        );

        for (var x = 2; x < Side - 2; x++) {
            Assert.Equal(0, TextureKernelHarness.At(raised, x, 32, 0));
        }
    }

    // --- Flood Fill -----------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ Two disjoint rectangles are two islands with two ids, two sizes, and a local UV that
    ///     spans each of them.
    /// </summary>
    /// <remarks>
    ///     <b>§ 4.5's own closed form.</b> Every number here is known before the bake: the small
    ///     rectangle's box begins at (4, 4) and is eight texels across, the large one's begins at
    ///     (40, 20) and is sixteen, and the local UV of a box's own corners is 0 and
    ///     <c>(n − 1) / n</c>. Four pictures are read out of <b>one</b> settled record, which is the
    ///     arrangement that makes § 4.5's five outputs one node.
    /// </remarks>
    [Fact]
    public void A_flood_fill_of_two_rectangles_is_two_islands_with_known_boxes() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var mask = Rectangles(Side, (4, 4, 11, 11), (40, 20, 55, 35));
        var (texture, staging) = TextureKernelHarness.Upload(device, mask, Side, Side);

        try {
            // 32 iterations: bounds travel one texel per dispatch along the island, and the larger
            // rectangle's Manhattan diameter is thirty.
            const int Budget = 32;

            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < Budget; pass++) {
                images.Add(new(TextureFormat.Rgba16Float));
            }

            var idImage = images.Count;

            images.Add(new(TextureFormat.Rgba8));
            images.Add(new(TextureFormat.Rgba8));
            images.Add(new(TextureFormat.Rgba8));
            images.Add(new(TextureFormat.Rgba8));

            ImmutableArray<int> scratch = [.. Enumerable.Range(1, Budget)];

            var ops = TextureAnalysis
                .FloodFill(idImage, 0, scratch, Side, Side, TextureFloodOutput.Id)
                .Add(TextureAnalysis.FloodRead(idImage + 1, scratch[^1], TextureFloodOutput.Size))
                .Add(TextureAnalysis.FloodRead(idImage + 2, scratch[^1], TextureFloodOutput.LocalUv))
                .Add(TextureAnalysis.FloodRead(idImage + 3, scratch[^1], TextureFloodOutput.Random));

            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Seed = 7717u,
                Images = images.ToImmutable(),
                Ops = ops,
                Outputs = [idImage, idImage + 1, idImage + 2, idImage + 3]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var ids = bake.Read(idImage);
            var sizes = bake.Read(idImage + 1);
            var local = bake.Read(idImage + 2);
            var random = bake.Read(idImage + 3);

            // The id is the box's minimum corner over the image's extent, and every texel of one
            // island agrees on it.
            Assert.Equal(Byte(4f / Side), TextureKernelHarness.At(ids, 4, 4, 0));
            Assert.Equal(Byte(4f / Side), TextureKernelHarness.At(ids, 11, 11, 0));
            Assert.Equal(Byte(40f / Side), TextureKernelHarness.At(ids, 55, 35, 0));
            Assert.Equal(Byte(20f / Side), TextureKernelHarness.At(ids, 40, 20, 1));

            // And the two islands do not share it, which is the whole point of the propagation.
            Assert.NotEqual(TextureKernelHarness.At(ids, 4, 4, 0), TextureKernelHarness.At(ids, 40, 20, 0));

            // The size is the box's, in each axis, as a fraction of the image.
            Assert.Equal(Byte(8f / Side), TextureKernelHarness.At(sizes, 7, 7, 0));
            Assert.Equal(Byte(8f / Side), TextureKernelHarness.At(sizes, 7, 7, 1));
            Assert.Equal(Byte(16f / Side), TextureKernelHarness.At(sizes, 48, 28, 0));

            // The local UV spans each island's own box, corner to corner.
            Assert.Equal(0, TextureKernelHarness.At(local, 4, 4, 0));
            Assert.Equal(Byte(7f / 8f), TextureKernelHarness.At(local, 11, 11, 0));
            Assert.Equal(0, TextureKernelHarness.At(local, 40, 20, 1));
            Assert.Equal(Byte(15f / 16f), TextureKernelHarness.At(local, 55, 35, 1));

            // The random value is one per island and it is not the same one.
            Assert.Equal(TextureKernelHarness.At(random, 4, 4, 0), TextureKernelHarness.At(random, 11, 11, 0));
            Assert.Equal(TextureKernelHarness.At(random, 40, 20, 0), TextureKernelHarness.At(random, 55, 35, 0));
            Assert.NotEqual(TextureKernelHarness.At(random, 4, 4, 0), TextureKernelHarness.At(random, 40, 20, 0));

            // Background is black in every one of them.
            Assert.Equal(0, TextureKernelHarness.At(ids, 30, 5, 0));
            Assert.Equal(0, TextureKernelHarness.At(sizes, 30, 5, 0));
            Assert.Equal(0, TextureKernelHarness.At(random, 30, 5, 0));
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ A budget too small to settle the mask is <em>reported</em>, and a budget large enough
    ///     reports that it was.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>§ 4.5 asks for "an iteration ceiling that reports truncation rather than a
    ///         while-loop on the device", and this is the instrument.</b> The residual is one where
    ///         the last two iterations disagree; a <c>MinMaxReduce</c> chain takes its maximum down to
    ///         one texel; the caller reads that texel. ⚠ <b>A line sixty-four texels long needs
    ///         sixty-three iterations</b> — a bound travels one texel per dispatch, so the settling
    ///         time is the island's <em>arc length</em> and has nothing to do with its area, which is
    ///         sixty-four texels either way. Given four it is still moving; given sixty-six it is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because the instrument's failure mode is to report success.</b> A
    ///         residual that always wrote zero would pass the converged case and nothing else in this
    ///         file would notice.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(4, true)]
    [InlineData(66, false)]
    public void A_flood_fill_reports_whether_its_budget_was_enough(int budget, bool expectedTruncation) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // One texel tall and the whole width: bounds crawl along it a texel at a time, which is the
        // shape § 4.5 warns about — its cost is its arc length and not its area.
        var mask = Rectangles(Side, (0, 32, Side - 1, 32));
        var (texture, staging) = TextureKernelHarness.Upload(device, mask, Side, Side);

        try {
            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < budget; pass++) {
                images.Add(new(TextureFormat.Rgba16Float));
            }

            var residual = images.Count;

            images.Add(new(TextureFormat.Rgba8));
            images.Add(new(TextureFormat.Rgba8, LevelOffset: 3));
            images.Add(new(TextureFormat.Rgba8, LevelOffset: 6));

            ImmutableArray<int> scratch = [.. Enumerable.Range(1, budget)];

            var ops = TextureAnalysis
                .FloodFill(residual, 0, scratch, Side, Side)
                .RemoveAt(budget)
                .Add(TextureAnalysis.Residual(residual, scratch[^2], scratch[^1]))
                .Add(Reduce(residual + 1, residual, first: true))
                .Add(Reduce(residual + 2, residual + 1, first: false));

            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = images.ToImmutable(),
                Ops = ops,
                Outputs = [residual + 2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            // Green is the maximum of the reduction: one where any texel is still moving.
            var moving = TextureKernelHarness.At(bake.Read(residual + 2), 0, 0, 1);

            output.WriteLine($"budget {budget}: residual maximum {moving}");

            Assert.Equal(expectedTruncation, moving > 0);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>One level of the min/max reduction, which is <c>AutoLevels</c>' kernel and not this slice's.</summary>
    static TextureOp Reduce(int output, int source, bool first) =>
        new() {
            Kernel = "MinMaxReduce",
            Output = output,
            Inputs = [source],
            Parameters = [new("first", first ? 1f : 0f)]
        };

    /// <summary>What an eight-bit read-back makes of a value in 0..1.</summary>
    static int Byte(float value) => (int)MathF.Round(value * 255f);

    /// <summary>The whole distance chain over one uploaded mask, read back.</summary>
    static Bitmap Field(VulkanDevice device, byte[] mask, TextureDistanceMode mode, float maxDistance) {
        var (texture, staging) = TextureKernelHarness.Upload(device, mask, Side, Side);

        try {
            var dispatches = TextureAnalysis.FloodDispatches(Side, Side);
            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < dispatches; pass++) {
                images.Add(new(TextureFormat.Rgba16Float));
            }

            var result = images.Count;

            images.Add(new(TextureFormat.Rgba8));

            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = images.ToImmutable(),
                Ops = TextureAnalysis.Distance(
                    result,
                    0,
                    [.. Enumerable.Range(1, dispatches)],
                    Side,
                    Side,
                    mode,
                    maxDistance
                ),
                Outputs = [result]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(result);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>Evaluates one op over one uploaded picture and reads the answer back.</summary>
    static Bitmap OneOp(VulkanDevice device, byte[] source, TextureOp op) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                Ops = [op],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(1);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }
}
