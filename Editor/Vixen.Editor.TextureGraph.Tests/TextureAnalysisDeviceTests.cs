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
    ///     ⚠ Two islands that share the minimum corner of their boxes still get two ids — and the
    ///     picture that proves it is the one <a href="https://github.com/Rikarin/Vixen/issues/691">
    ///     #691</a> asked for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#691 reported the collision one level too high.</b> It said two islands with the
    ///         same bounding <em>box</em> read as one, and offered "an L-shape and a small square
    ///         tucked into its corner". ⚠ <b>No such pair exists</b> —
    ///         <c>TextureAnalysisKernelTests.No_two_islands_ever_share_a_bounding_box_but_a_minimum_corner_is_not_a_name</c>
    ///         walks all 65 536 four-by-four masks under both connectivities and finds none, for the
    ///         topological reason written there. The L in the example does not share its box with
    ///         anything: whatever sits in its notch has a smaller one.
    ///     </para>
    ///     <para>
    ///         What <em>is</em> real is that the <c>Id</c> picture published half the record. This
    ///         mask is a five-texel bar and a hook that starts under its left end and reaches past its
    ///         right: two islands, nowhere adjacent, whose boxes are <c>(10, 10)–(14, 10)</c> and
    ///         <c>(10, 10)–(18, 12)</c> — <b>different boxes, one shared minimum corner</b>. Red and
    ///         green therefore agree by construction, which is the assertion that says the fixture is
    ///         the fixture; blue is the whole record hashed, and it is what has to differ.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sizes are read as the instrument check.</b> Two ids differing would also be
    ///         satisfied by a flood that had simply failed to join each island up — so the same bake
    ///         is asked how big each island's box is, and both answers are the ones a settled flood
    ///         owes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_islands_that_share_a_minimum_corner_still_get_two_ids() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var mask = Rectangles(Side, (10, 10, 14, 10), (10, 12, 18, 12), (18, 10, 18, 12));
        var (ids, sizes) = TwoFloods(device, mask, TextureFloodOutput.Id, TextureFloodOutput.Size, false);

        // Both islands begin at (10, 10), so the addressable half of the id is the same one.
        Assert.Equal(Byte(10f / Side), TextureKernelHarness.At(ids, 12, 10, 0));
        Assert.Equal(Byte(10f / Side), TextureKernelHarness.At(ids, 12, 10, 1));
        Assert.Equal(Byte(10f / Side), TextureKernelHarness.At(ids, 14, 12, 0));
        Assert.Equal(Byte(10f / Side), TextureKernelHarness.At(ids, 14, 12, 1));

        // And they are two islands, which the boxes say: 5×1 against 9×3.
        Assert.Equal(Byte(5f / Side), TextureKernelHarness.At(sizes, 12, 10, 0));
        Assert.Equal(Byte(1f / Side), TextureKernelHarness.At(sizes, 12, 10, 1));
        Assert.Equal(Byte(9f / Side), TextureKernelHarness.At(sizes, 14, 12, 0));
        Assert.Equal(Byte(3f / Side), TextureKernelHarness.At(sizes, 14, 12, 1));

        // So the id has to separate them, and only blue can.
        Assert.NotEqual(TextureKernelHarness.At(ids, 12, 10, 2), TextureKernelHarness.At(ids, 14, 12, 2));

        // Every texel of one island still agrees with every other, which is what "settled" means and
        // what a name hashed per texel rather than per record would break.
        Assert.Equal(TextureKernelHarness.At(ids, 10, 10, 2), TextureKernelHarness.At(ids, 14, 10, 2));
        Assert.Equal(TextureKernelHarness.At(ids, 10, 12, 2), TextureKernelHarness.At(ids, 18, 10, 2));
    }

    /// <summary>
    ///     ⚠ Two squares touching only at a corner are <b>one</b> island under eight-connectivity and
    ///     <b>two</b> under four.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/705">#705</a>.</b>
    ///         <c>FloodBounds.rvn</c> branches on <c>diagonal</c> —
    ///         <c>val skip = dx * dy != 0 &amp;&amp; diagonal == 0</c> — and nothing in the suite set
    ///         it: every flood above took the builder's default, so ⚠ <b>a kernel that ignored the
    ///         uniform, or inverted its sense, was green across the whole file</b>.
    ///     </para>
    ///     <para>
    ///         The closed form is the definition of the two connectivities, and it needs no tolerance
    ///         at all. Two 4×4 squares meeting at (13, 13)–(14, 14) are, under four-connectivity, two
    ///         islands whose boxes are their own squares; under eight, one island whose box is the
    ///         pair's. So the ids either differ or agree, and the sizes are either 4/64 in each axis
    ///         or 8/64 — four numbers that no other reading of the uniform produces.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false, 4, 4)]
    [InlineData(true, 8, 8)]
    public void Two_squares_touching_at_a_corner_are_one_island_only_under_eight_connectivity(
        bool diagonal,
        int firstSpan,
        int secondSpan
    ) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var mask = Rectangles(Side, (10, 10, 13, 13), (14, 14, 17, 17));
        var (ids, sizes) = TwoFloods(device, mask, TextureFloodOutput.Id, TextureFloodOutput.Size, diagonal);

        // The minimum corner is (10, 10) for the first square either way; what the connectivity
        // decides is whether the second square carries it too.
        Assert.Equal(Byte(10f / Side), TextureKernelHarness.At(ids, 11, 11, 0));
        Assert.Equal(Byte(diagonal ? 10f / Side : 14f / Side), TextureKernelHarness.At(ids, 16, 16, 0));

        if (diagonal) {
            Assert.Equal(TextureKernelHarness.At(ids, 11, 11, 0), TextureKernelHarness.At(ids, 16, 16, 0));
        } else {
            Assert.NotEqual(TextureKernelHarness.At(ids, 11, 11, 0), TextureKernelHarness.At(ids, 16, 16, 0));
        }

        // And the box each texel settled on is the one island it belongs to, in both axes.
        Assert.Equal(Byte(firstSpan / (float)Side), TextureKernelHarness.At(sizes, 11, 11, 0));
        Assert.Equal(Byte(firstSpan / (float)Side), TextureKernelHarness.At(sizes, 11, 11, 1));
        Assert.Equal(Byte(secondSpan / (float)Side), TextureKernelHarness.At(sizes, 16, 16, 0));
        Assert.Equal(Byte(secondSpan / (float)Side), TextureKernelHarness.At(sizes, 16, 16, 1));
    }

    /// <summary>
    ///     ⚠ The <c>BoundingBox</c> picture is the settled record itself — minimum in red and green,
    ///     <em>maximum</em> in blue and alpha — and its alpha is what tells background from a dark
    ///     island.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fifth of § 4.5's five outputs, and the one nothing read.</b>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/705">#705</a>: the two-rectangle test
    ///         above says in its own docstring that "four pictures are read out of one settled
    ///         record", and <c>kind == 3</c> is the fifth — ⚠ <b>swapping the minimum and the maximum
    ///         inside that branch was invisible to the entire suite</b>, and a swapped box is a
    ///         perfectly plausible picture.
    ///     </para>
    ///     <para>
    ///         It is also the only branch that stores four meaningful channels rather than an explicit
    ///         alpha of one, which is what <c>FloodFill.rvn</c>'s header rests the background
    ///         discriminator on: a texel in no island stores <c>(0, 0, 0, 1)</c>, and an island's alpha
    ///         is <c>maxY / height</c>, which is at most <c>(h − 1) / h</c>. So <b>alpha of one is
    ///         background and nothing else can be</b> — asserted here, because the header is the only
    ///         other place it is written down.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bounding_box_picture_is_the_record_and_its_alpha_names_the_background() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var mask = Rectangles(Side, (4, 4, 11, 11), (40, 20, 55, 35));
        var (boxes, _) = TwoFloods(device, mask, TextureFloodOutput.BoundingBox, TextureFloodOutput.Id, false);

        foreach (var (x, y, minX, minY, maxX, maxY) in new[] {
            (7, 7, 4, 4, 11, 11),
            (11, 11, 4, 4, 11, 11),
            (48, 28, 40, 20, 55, 35)
        }) {
            Assert.Equal(Byte(minX / (float)Side), TextureKernelHarness.At(boxes, x, y, 0));
            Assert.Equal(Byte(minY / (float)Side), TextureKernelHarness.At(boxes, x, y, 1));
            Assert.Equal(Byte(maxX / (float)Side), TextureKernelHarness.At(boxes, x, y, 2));
            Assert.Equal(Byte(maxY / (float)Side), TextureKernelHarness.At(boxes, x, y, 3));

            // The ordering, said separately: a swapped branch reads as a box and this is what says
            // which corner is which.
            Assert.True(
                TextureKernelHarness.At(boxes, x, y, 0) < TextureKernelHarness.At(boxes, x, y, 2),
                $"the box at ({x}, {y}) has its red above its blue, so the minimum and the maximum are the "
                + $"wrong way round on {TextureKernelHarness.Adapter(device)}"
            );
        }

        // Background is opaque and every island's alpha is its own maxY over the image, which cannot
        // reach one.
        Assert.Equal(255, TextureKernelHarness.At(boxes, 30, 5, 3));
        Assert.Equal(0, TextureKernelHarness.At(boxes, 30, 5, 0));
        Assert.True(TextureKernelHarness.At(boxes, 48, 28, 3) < 255);
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

    /// <summary>One flood over one mask, read out twice.</summary>
    /// <remarks>
    ///     ⚠ <b>Two reads of <em>one</em> settled record</b>, which is the arrangement § 4.5's five
    ///     outputs rest on — a second flood would prove the same numbers and cost the settling time
    ///     again. The budget is 24, which is past the arc length of every mask this file builds.
    /// </remarks>
    static (Bitmap First, Bitmap Second) TwoFloods(
        VulkanDevice device,
        byte[] mask,
        TextureFloodOutput first,
        TextureFloodOutput second,
        bool diagonal
    ) {
        const int Budget = 24;

        var (texture, staging) = TextureKernelHarness.Upload(device, mask, Side, Side);

        try {
            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < Budget; pass++) {
                images.Add(new(TextureFormat.Rgba16Float));
            }

            var read = images.Count;

            images.Add(new(TextureFormat.Rgba8));
            images.Add(new(TextureFormat.Rgba8));

            ImmutableArray<int> scratch = [.. Enumerable.Range(1, Budget)];

            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Seed = 7717u,
                Images = images.ToImmutable(),
                Ops = TextureAnalysis
                    .FloodFill(read, 0, scratch, Side, Side, first, diagonal)
                    .Add(TextureAnalysis.FloodRead(read + 1, scratch[^1], second)),
                Outputs = [read, read + 1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return (bake.Read(read), bake.Read(read + 1));
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

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
