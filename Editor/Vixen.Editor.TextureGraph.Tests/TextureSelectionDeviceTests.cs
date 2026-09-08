// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Geometry.Remeshing;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § M8's colour / ID selection mask, on a real device, over the bake's own palette.</summary>
/// <remarks>
///     <para>
///         <b>The fixture is <c>MapBaker.IdColour</c> and never an invented triple</b>, and that is
///         what makes this file a test about the <c>id</c> bake rather than about three numbers
///         somebody chose. The palette is what the map contains — <c>MeshMapBake.Identifiers</c>
///         applies it and nothing decodes it afterwards — so a colour selection is only usable if it
///         can tell the palette's own entries apart.
///     </para>
///     <para>
///         ⚠ <b>The discriminating property, and it is an inversion rather than a tolerance.</b> The
///         palette is a golden-angle hue sweep, so <em>index distance and colour distance are
///         unrelated</em>: ids 0 and 1 are a third of the hue circle apart, and ids 0 and 89 are five
///         thousandths of it apart. <see cref="An_id_is_nominal_so_the_neighbouring_index_is_the_far_colour" />
///         puts both in one picture at one tolerance, so an implementation that decoded the hue to an
///         index and compared <c>id ± tolerance</c> gets <b>both halves backwards</b> — it would keep
///         island 1 and drop island 89. A fixture that only ever matched exactly could not see that
///         at all, which is the trap
///         <a href="https://github.com/Rikarin/Vixen/issues/1010">#1010</a> names.
///     </para>
///     <para>
///         ⚠ <b>And the ceiling that inversion implies is asserted rather than described.</b> Because
///         the nearest colour to an island belongs to a Fibonacci-distant island, a tolerance is also
///         a bound on how many islands the node can separate — the node's own remarks put the number
///         at eighty-nine for the 0.02 default, and
///         <see cref="The_palettes_own_resolution_is_what_bounds_the_tolerance" /> is where that
///         number comes from a measurement instead of from a paragraph.
///     </para>
/// </remarks>
public class TextureSelectionDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>What island <paramref name="id" /> looks like in a baked map, quantized as the file is.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>MeshMapBake</c>'s own rounding, because the kernel compares what the PNG
    ///     holds.</b> Matching the unquantized <see cref="Vector3" /> would leave every "exact" match
    ///     up to half a byte away from the texel and would make an assertion about a hard step depend
    ///     on a rounding this file does not own.
    /// </remarks>
    static Vector3 Palette(int id) {
        var colour = MapBaker.IdColour(id);

        return new(Quantize(colour.X), Quantize(colour.Y), Quantize(colour.Z));
    }

    static float Quantize(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f) / 255f;

    static float Distance(Vector3 first, Vector3 second) => (first - second).Length();

    /// <summary>Two islands, left and right, each one flat colour.</summary>
    static byte[] Islands(Vector3 left, Vector3 right) {
        var pixels = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = ((y * Side) + x) * 4;
                var colour = x < Side / 2 ? left : right;

                pixels[at] = (byte)MathF.Round(colour.X * 255f);
                pixels[at + 1] = (byte)MathF.Round(colour.Y * 255f);
                pixels[at + 2] = (byte)MathF.Round(colour.Z * 255f);
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A grey ramp across the image, every channel equal, so the distance from black is exact.</summary>
    static byte[] GreyRamp() {
        var pixels = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = ((y * Side) + x) * 4;
                var value = (byte)(x * 4);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    ///     ⚠ The island one index away is the one furthest in colour, and the island eighty-nine away
    ///     is the one nearest — so one tolerance keeps the second and drops the first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the whole of #1010's warning, made into two pictures at one setting.</b> An
    ///         implementation that compared decoded indices with a <c>±</c> answers the opposite way
    ///         on both halves, and no assertion that only ever matched a colour exactly could tell the
    ///         two implementations apart.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fixture asserts its own discriminating power first.</b> If the two distances
    ///         did not straddle the tolerance — if the palette were, say, sequential rather than
    ///         golden-angle — both halves below would pass for reasons that had nothing to do with the
    ///         kernel.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_id_is_nominal_so_the_neighbouring_index_is_the_far_colour() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const float Tolerance = 0.2f;

        var wanted = Palette(0);
        var next = Distance(wanted, Palette(1));
        var fibonacci = Distance(wanted, Palette(89));

        output.WriteLine($"id 0 to id 1 is {next}; id 0 to id 89 is {fibonacci}; tolerance {Tolerance}");

        Assert.True(next > Tolerance, $"id 1 is {next} from id 0, so this tolerance proves nothing about it");
        Assert.True(fibonacci < Tolerance, $"id 89 is {fibonacci} from id 0, so this tolerance proves nothing");

        var neighbour = OneOp(device, Islands(wanted, Palette(1)), Select(wanted, Tolerance));

        Assert.Equal(255, TextureKernelHarness.At(neighbour, 16, 32, 0));
        Assert.Equal(0, TextureKernelHarness.At(neighbour, 48, 32, 0));

        var distant = OneOp(device, Islands(wanted, Palette(89)), Select(wanted, Tolerance));

        Assert.Equal(255, TextureKernelHarness.At(distant, 16, 32, 0));
        Assert.Equal(255, TextureKernelHarness.At(distant, 48, 32, 0));
    }

    /// <summary>The two islands the palette puts together are separated by a small enough tolerance.</summary>
    /// <remarks>
    ///     ⚠ <b>Verify the instrument: without this, the test above would be satisfied by a kernel
    ///     that selected everything.</b> "Both islands are white" is what a comparison that always
    ///     matches produces, so the same picture at a tolerance under their separation has to come
    ///     back with one of them black — and the number it separates at is the number the node's own
    ///     remarks quote.
    /// </remarks>
    [Fact]
    public void The_palettes_own_resolution_is_what_bounds_the_tolerance() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var wanted = Palette(0);
        var fibonacci = Distance(wanted, Palette(89));

        output.WriteLine($"id 0 to id 89 is {fibonacci}, and the node's default tolerance is 0.02");

        // The claim the node's remarks make: the default separates the first eighty-nine islands and
        // stops there. Both halves, because "0.02 is enough" and "0.02 is barely enough" are
        // different statements and only the pair of them is a bound.
        Assert.True(fibonacci < 0.05f, $"id 89 is {fibonacci} away, which is not the near case at all");

        var loose = OneOp(device, Islands(wanted, Palette(89)), Select(wanted, fibonacci * 1.5f));

        Assert.Equal(255, TextureKernelHarness.At(loose, 48, 32, 0));

        var tight = OneOp(device, Islands(wanted, Palette(89)), Select(wanted, fibonacci * 0.5f));

        Assert.Equal(255, TextureKernelHarness.At(tight, 16, 32, 0));
        Assert.Equal(0, TextureKernelHarness.At(tight, 48, 32, 0));
    }

    /// <summary>With no softness the edge is a step, which is what an index map needs.</summary>
    /// <remarks>
    ///     ⚠ <b>A ramp rather than two flat fills, because a step is a claim about the texels either
    ///     side of one distance and a flat fill cannot make it.</b> Every channel of the ramp is
    ///     equal, so a grey of <c>g</c> is exactly <c>√3 · g</c> from black — arithmetic rather than
    ///     opinion — and the assertion is that the mask is 255 up to that distance and 0 immediately
    ///     after it, with nothing between.
    /// </remarks>
    [Fact]
    public void With_no_softness_the_edge_is_a_step() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // Eight texels in: grey 32, which is 32/255 per channel and √3 times that away from black.
        var inside = MathF.Sqrt(3f) * (8f * 4f / 255f);
        var beyond = MathF.Sqrt(3f) * (9f * 4f / 255f);

        var picture = OneOp(device, GreyRamp(), Select(Vector3.Zero, (inside + beyond) / 2f));

        Assert.Equal(255, TextureKernelHarness.At(picture, 8, 32, 0));
        Assert.Equal(0, TextureKernelHarness.At(picture, 9, 32, 0));

        // ⚠ And nothing anywhere is between, which is the property a hard step has and a ramp with a
        // very small softness does not. A kernel that had smoothed the edge over one texel would pass
        // both assertions above.
        for (var x = 0; x < Side; x++) {
            var value = TextureKernelHarness.At(picture, x, 32, 0);

            Assert.True(value is 0 or 255, $"texel {x} reads {value}, so the edge is not a step");
        }
    }

    /// <summary>Softness is a second radius: the mask falls from one to zero between the two.</summary>
    /// <remarks>
    ///     ⚠ <b>The midpoint is asserted, not just the ends.</b> A kernel that returned one inside and
    ///     zero outside — the hard step again — has the right answer at both ends of the ramp and the
    ///     wrong one everywhere between, so the ends alone cannot tell softness from no softness.
    /// </remarks>
    [Fact]
    public void Softness_is_the_distance_the_mask_takes_to_reach_zero() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const float Tolerance = 0f;

        // Black to the ramp's own far end: grey 128 is 32 texels in, so the half-way texel is 16.
        var softness = MathF.Sqrt(3f) * (32f * 4f / 255f);
        var picture = OneOp(device, GreyRamp(), Select(Vector3.Zero, Tolerance, softness));

        var start = TextureKernelHarness.At(picture, 0, 32, 0);
        var middle = TextureKernelHarness.At(picture, 16, 32, 0);
        var end = TextureKernelHarness.At(picture, 32, 32, 0);

        output.WriteLine($"ramp reads {start} at black, {middle} half way and {end} at the softness");

        Assert.Equal(255, start);
        Assert.InRange(middle, 118, 138);
        Assert.Equal(0, end);
    }

    /// <summary>⚠ The source is read nearest, so a mask over an id map holds no colour no island has.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>§ D12's own rule one node further along.</b> <c>Source/Mesh Map</c> samples the
    ///         <c>id</c> map nearest because the average of two labels is a third label; a selection
    ///         that then interpolated would put that third label back along every chart border, and
    ///         the mask would grow a hairline of a partial coverage no island asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A source smaller than the target is what makes the question askable.</b> At equal
    ///         extents a bilinear read lands exactly on a texel centre and agrees with a nearest one
    ///         everywhere, so the two implementations are indistinguishable — this evaluates a 32²
    ///         source into a 64² target, where a bilinear read would average across the island border
    ///         and produce mask values strictly between 0 and 255.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_source_is_read_nearest_so_no_texel_is_a_blend_of_two_islands() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Small = Side / 2;

        var wanted = Palette(0);
        var pixels = new byte[Small * Small * 4];

        for (var y = 0; y < Small; y++) {
            for (var x = 0; x < Small; x++) {
                var at = ((y * Small) + x) * 4;
                var colour = x < Small / 2 ? wanted : Palette(1);

                pixels[at] = (byte)MathF.Round(colour.X * 255f);
                pixels[at + 1] = (byte)MathF.Round(colour.Y * 255f);
                pixels[at + 2] = (byte)MathF.Round(colour.Z * 255f);
                pixels[at + 3] = 255;
            }
        }

        // ⚠ A softness of exactly the two islands' separation, and a hard step would hide the whole
        // question: an averaged texel half way between the colours is 0.3 from the one selected and a
        // step returns 0 for it, which is what an unaveraged island-1 texel returns too. Under this
        // ramp a blend is the only thing that can read strictly between the ends, so every texel being
        // one end or the other *is* the claim that nothing was interpolated.
        var separation = Distance(wanted, Palette(1));
        var picture = Sized(device, pixels, Small, Small, Side, Side, Select(wanted, 0f, separation));

        for (var x = 0; x < Side; x++) {
            var value = TextureKernelHarness.At(picture, x, 32, 0);

            Assert.True(value is 0 or 255, $"texel {x} reads {value}, so two islands were averaged");
        }
    }

    static TextureOp Select(Vector3 colour, float tolerance, float softness = 0f) =>
        TextureSelections.ColourSelect(1, 0, colour.X, colour.Y, colour.Z, tolerance, softness);

    /// <summary>Evaluates one op over one uploaded picture and reads the answer back.</summary>
    static Bitmap OneOp(VulkanDevice device, byte[] source, TextureOp op) =>
        Sized(device, source, Side, Side, Side, Side, op);

    /// <summary>The same, over a source whose extent is not the plan's.</summary>
    static Bitmap Sized(
        VulkanDevice device,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        TextureOp op
    ) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, sourceWidth, sourceHeight);

        try {
            var plan = new TexturePlan {
                BaseWidth = width,
                BaseHeight = height,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                Ops = [op],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);

            // ⚠ The sized overload, because the source is not always the plan's own extent — the
            // nearest-sampling assertion turns entirely on a 32² source in a 64² bake, and an
            // external with no size is one the read-back cannot name an extent for.
            using var bake = evaluator.Evaluate(
                plan,
                TextureKernelHarness.Externals(0, texture, sourceWidth, sourceHeight)
            );

            return bake.Read(1);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }
}
