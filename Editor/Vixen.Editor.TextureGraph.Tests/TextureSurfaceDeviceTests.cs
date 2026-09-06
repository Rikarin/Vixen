// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.6's surface kernels, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>The four closed forms § 4.6 names:</b> height → normal of a plane is the flat normal;
///         ambient occlusion of a plane is one everywhere; a normal combine with a flat input returns
///         the other one; and — the one this file exists for — <b>a height that rises downwards has
///         green below a half</b>.
///     </para>
///     <para>
///         ⚠ <b>The green convention is asserted and not claimed.</b> § 4.6 says a flipped green "is
///         the defect that survives every review because it looks like lighting", and it is right: a
///         normal map with the wrong sign lights a surface plausibly and only looks wrong beside a
///         correct one. <c>HeightToNormal.rvn</c> derives the sign from
///         <c>MaterialSurface.rvn</c>'s <c>Normals.Decode</c>, <c>Normals.Frame</c>'s bitangent and
///         the engine's v-down UV; <see cref="A_height_that_rises_downwards_has_green_below_a_half" />
///         is where that derivation becomes a number.
///     </para>
///     <para>
///         ⚠ <b>The normal-combine test tilts <em>both</em> inputs, deliberately.</b> Reorienting and
///         whiteout agree exactly whenever either input is flat, which is the case a lazy test
///         reaches for first — so the discriminating assertion is that the answer's red and green
///         differ, which whiteout's cannot when the two inputs are tilted equally in opposite axes.
///     </para>
/// </remarks>
public class TextureSurfaceDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>A tangent-space normal map of one constant direction, encoded.</summary>
    static byte[] Normal(int side, byte r, byte g, byte b) => TextureKernelHarness.Solid(side, r, g, b, 255);

    /// <summary>A height field rising by exactly four steps a texel, along one axis.</summary>
    /// <remarks>
    ///     ⚠ <b>Four a texel rather than the full range, so that every difference is exact in eight
    ///     bits.</b> A ramp built as <c>x × 255 / 63</c> truncates unevenly — some texel pairs differ
    ///     by eight and some by nine — and a test of a gradient built on one would carry that
    ///     unevenness into its tolerance and hide a real error of the same size.
    /// </remarks>
    static byte[] Ramp(int side, bool downwards) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = (byte)((downwards ? y : x) * 4);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A height field that is flat and low to the left of a wall and flat and high to its right.</summary>
    static byte[] Step(int side, int at) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var offset = ((y * side) + x) * 4;
                var value = (byte)(x >= at ? 255 : 0);

                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A normal map whose green channel ramps by four a texel down the image, red flat.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="RedRamp" /> turned ninety degrees, and it takes a width and a height</b>
    ///     rather than a side: the curvature kernel scales the two axes by two different extents, and
    ///     a square image is the one shape on which that is unobservable — #947.
    /// </remarks>
    static byte[] GreenRamp(int width, int height) {
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = ((y * width) + x) * 4;

                pixels[at] = 128;
                pixels[at + 1] = (byte)(y * 4);
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A normal map whose red rises three steps a texel away from the middle column.</summary>
    /// <remarks>
    ///     ⚠ <b>Three a texel and not four, so the far column stays inside eight bits</b>: at a side
    ///     of 64 the apex is 32 texels from the edge, and four a texel would wrap the byte there and
    ///     put a second, invented kink in the field.
    /// </remarks>
    static byte[] Kink(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;

                pixels[at] = (byte)(128 + (3 * Math.Abs(x - (side / 2))));
                pixels[at + 1] = 128;
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A normal map whose red channel ramps by four a texel and whose green is flat.</summary>
    static byte[] RedRamp(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;

                pixels[at] = (byte)(x * 4);
                pixels[at + 1] = 128;
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    // --- Height → Normal ------------------------------------------------------------------------

    /// <summary>A plane's normal map is the flat normal, everywhere.</summary>
    [Fact]
    public void A_height_to_normal_of_a_plane_is_the_flat_normal() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(device, TextureKernelHarness.Solid(Side, 96, 96, 96, 255), TextureSurfaces.HeightToNormal(1, 0));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 0), 127, 129);
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 1), 127, 129);
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 2), 254, 255);
            }
        }
    }

    /// <summary>
    ///     ⚠ The green convention: a height that rises as you move <em>down</em> the image is green
    ///     below a half.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Derived rather than chosen</b> — see <c>Shaders/HeightToNormal.rvn</c>. A tangent
    ///         frame's bitangent is the direction <c>v</c> increases in
    ///         (<c>MapBaker.Frame</c>, Lengyel), <c>v</c> increases downwards
    ///         (a sampled texture's <c>v = 0</c> is its top row), and the surface normal of a height
    ///         field is <c>(−∂h/∂u, −∂h/∂v, 1)</c>. Nothing in
    ///         <c>MaterialSurface.rvn</c>'s sampling path flips it back.
    ///     </para>
    ///     <para>
    ///         The number is exact enough to assert as one: the ramp rises four eight-bit steps a
    ///         texel, so the slope per unit of UV is <c>4 × 64 / 255</c>, and the encoded green is
    ///         thirty-seven. ⚠ <b>The flipped kernel would produce two hundred and eighteen</b>, which
    ///         is what makes the inequality worth spelling out beside the range.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_height_that_rises_downwards_has_green_below_a_half() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var down = OneOp(device, Ramp(Side, downwards: true), TextureSurfaces.HeightToNormal(1, 0));

        // Away from the clamped border, where the operator straddles the edge texel twice.
        for (var y = 2; y < Side - 2; y++) {
            Assert.True(
                TextureKernelHarness.At(down, 32, y, 1) < 128,
                $"green is {TextureKernelHarness.At(down, 32, y, 1)} at row {y} — the convention is flipped"
            );

            Assert.InRange(TextureKernelHarness.At(down, 32, y, 1), 35, 39);
            Assert.InRange(TextureKernelHarness.At(down, 32, y, 0), 127, 129);
        }

        // The same slope along x puts the tilt in red and leaves green alone, which is what says the
        // two axes were not transposed.
        var across = OneOp(device, Ramp(Side, downwards: false), TextureSurfaces.HeightToNormal(1, 0));

        for (var x = 2; x < Side - 2; x++) {
            Assert.InRange(TextureKernelHarness.At(across, x, 32, 0), 35, 39);
            Assert.InRange(TextureKernelHarness.At(across, x, 32, 1), 127, 129);
        }
    }

    /// <summary>Intensity bends the normal and nothing else, and zero leaves it flat.</summary>
    [Fact]
    public void A_height_to_normal_at_zero_intensity_is_flat() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(device, Ramp(Side, downwards: true), TextureSurfaces.HeightToNormal(1, 0, intensity: 0f));

        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 1), 127, 129);
        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 2), 254, 255);
    }

    // --- Normal Transform -----------------------------------------------------------------------

    /// <summary>Flipping green twice is the identity, and once is the escape hatch.</summary>
    [Fact]
    public void A_flipped_green_flipped_again_is_the_source() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // ⚠ A *unit* normal, and that is not a detail: (191, 64, 218) decodes to (0.5, −0.5, 0.707),
        // whose length is one, so renormalising is the identity and the round trip is exact. An
        // encoded triple picked by eye is not unit, and this test would then be asserting what the
        // renormalisation did to it rather than what the flip did.
        var source = Normal(Side, 191, 64, 218);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)
                ],
                Ops = [
                    TextureSurfaces.NormalTransform(1, 0, flipGreen: true),
                    TextureSurfaces.NormalTransform(2, 1, flipGreen: true)
                ],
                Outputs = [1, 2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var once = bake.Read(1);
            var twice = bake.Read(2);

            // 64 is below a half, so once flipped it is the same distance above it.
            Assert.InRange(TextureKernelHarness.At(once, 8, 8, 1), 190, 192);
            Assert.InRange(TextureKernelHarness.At(twice, 8, 8, 1), 63, 65);
            Assert.InRange(TextureKernelHarness.At(twice, 8, 8, 0), 190, 192);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>A quarter turn takes a tilt in red to a tilt in green.</summary>
    /// <remarks>
    ///     ⚠ <b>Clockwise on screen, which is the direction every other angle in the folder is
    ///     measured in</b> — green points down the image, so the xy pair lives in a y-down frame and
    ///     the ordinary rotation matrix turns it that way. A kernel that turned it the other way would
    ///     put the tilt in green too, at <c>255 − 218</c>, which is why the sign is asserted rather
    ///     than the magnitude alone.
    /// </remarks>
    [Fact]
    public void A_quarter_turn_takes_a_tilt_in_red_to_a_tilt_in_green() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            Normal(Side, 218, 128, 218),
            TextureSurfaces.NormalTransform(1, 0, rotation: MathF.PI / 2f)
        );

        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 0), 126, 130);
        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 1), 216, 220);
    }

    // --- Normal Combine -------------------------------------------------------------------------

    /// <summary>A flat detail returns the base, exactly — the identity both formulations share.</summary>
    [Fact]
    public void Combining_with_a_flat_detail_returns_the_base() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // The base is a unit normal — (0.5, −0.5, 0.707) — so "returns the base" is an equality on
        // the encoded bytes rather than a claim about what a renormalisation did to a short vector.
        var picture = TwoOp(device, Normal(Side, 191, 64, 218), Normal(Side, 128, 128, 255));

        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 0), 190, 192);
        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 1), 63, 65);
        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 2), 217, 219);
    }

    /// <summary>
    ///     ⚠ Two tilted inputs are <em>reoriented</em>, which whiteout cannot produce: the answer's
    ///     red and green differ.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The discriminating case § 4.6 asks for.</b> With a base tilted 45° in x and a detail
    ///         tilted 45° in y, reorienting gives <c>(0.5, 0.707, 0.5)</c> — the detail rotated into
    ///         the base's frame — and whiteout gives <c>(0.64, 0.64, 0.45)</c>, whose red and green are
    ///         <em>equal</em> because it adds the two xy pairs and the two inputs are symmetric.
    ///     </para>
    ///     <para>
    ///         So the assertion is in two parts and the second is the one that cannot be satisfied by
    ///         the cheaper formula: the numbers, and then the inequality.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Combining_two_tilted_normals_reorients_rather_than_whitening_out() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = TwoOp(device, Normal(Side, 218, 128, 218), Normal(Side, 128, 218, 218));

        var red = TextureKernelHarness.At(picture, 32, 32, 0);
        var green = TextureKernelHarness.At(picture, 32, 32, 1);
        var blue = TextureKernelHarness.At(picture, 32, 32, 2);

        output.WriteLine($"reoriented: ({red}, {green}, {blue}); whiteout would be (209, 209, 185)");

        Assert.InRange(red, 189, 194);
        Assert.InRange(green, 216, 220);
        Assert.InRange(blue, 188, 193);

        // Whiteout is symmetric in these two inputs and reorienting is not, which is the whole of the
        // difference § 4.6 insists on.
        Assert.True(green - red > 15, $"red {red} and green {green} are too close to be a reorientation");
    }

    // --- Curvature ------------------------------------------------------------------------------

    /// <summary>A flat normal map has no curvature, which is a half and not a zero.</summary>
    [Fact]
    public void Curvature_of_a_flat_normal_map_is_a_half() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(device, Normal(Side, 128, 128, 255), TextureSurfaces.Curvature(1, 0));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 0), 127, 128);
            }
        }
    }

    /// <summary>A normal whose red rises linearly has a constant divergence, and it is not a half.</summary>
    /// <remarks>
    ///     ⚠ <b>A ramp is the case that separates a divergence from a gradient magnitude.</b> A
    ///     kernel that measured <c>|∇n|</c> would also be constant here, and positive — so the number
    ///     is asserted rather than the sign: red rising by four eight-bit steps a texel is a
    ///     divergence of <c>8 × 64 / 255 ≈ 2.008</c>, which at the default intensity is
    ///     <c>0.5 + 0.251</c>.
    /// </remarks>
    [Fact]
    public void Curvature_of_a_linear_normal_ramp_is_constant_and_off_the_half() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(device, RedRamp(Side), TextureSurfaces.Curvature(1, 0));

        for (var x = 2; x < Side - 2; x++) {
            Assert.InRange(TextureKernelHarness.At(picture, x, 32, 0), 189, 194);
        }
    }

    /// <summary>
    ///     ⚠ The green half of the divergence is read, and it is scaled by the image's <em>height</em>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What the two oracles above cannot see</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/947">#947</a>. A flat field has no
    ///         divergence in either axis, and <see cref="RedRamp" />'s green is constant, so a kernel
    ///         that dropped <c>(below.y − above.y)</c> altogether passes both of them. This is the
    ///         same closed form turned ninety degrees: green rising by four eight-bit steps a texel
    ///         down the image is a divergence of <c>8 × height / 255</c>, which at the default
    ///         intensity puts the answer the same distance off the half as the red ramp does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the image is deliberately not square, which is the half that pins the
    ///         pairing.</b> <c>Main</c> scales the x difference by <c>size.x</c> and the y difference
    ///         by <c>size.y</c> — "per unit UV" — and on a square image those are the same number, so
    ///         a kernel that multiplied both by the width would be indistinguishable. At 64 × 32 the
    ///         two answers are 40 apart: 159 for the height and 191 for the width, which is the number
    ///         the red ramp above produces and therefore the one a swap would look right as.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Curvature_reads_green_down_the_image_and_scales_it_by_the_height() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Width = 64;
        const int Height = 32;

        var picture = Sized(device, GreenRamp(Width, Height), Width, Height, TextureSurfaces.Curvature(1, 0));

        for (var y = 2; y < Height - 2; y++) {
            var value = TextureKernelHarness.At(picture, 17, y, 0);

            Assert.True(
                value is >= 157 and <= 162,
                $"green rising four a texel down a {Width} × {Height} image came out {value} at (17, {y}). "
                + "159 is the divergence scaled by the height; 128 is the y difference dropped, and 191 is it "
                + $"scaled by the width ({TextureKernelHarness.Adapter(device)})"
            );
        }
    }

    /// <summary>
    ///     ⚠ Doubling the radius across a symmetric kink halves the curvature, exactly — which is what
    ///     makes the radius a radius.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one thing a linear ramp cannot calibrate</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/947">#947</a>, whose leading example is
    ///         a radius read as a diameter. A central difference over a straight line is
    ///         <em>exactly</em> radius-invariant: <c>(f(x+r) − f(x−r)) / 2r</c> is the slope for every
    ///         <c>r</c>, so <see cref="Curvature_of_a_linear_normal_ramp_is_constant_and_off_the_half" />
    ///         pins the scale and says nothing whatever about the reach. A field with a kink in it is
    ///         the smallest thing that can.
    ///     </para>
    ///     <para>
    ///         <b>The closed form.</b> Red is <c>128 + 3·|x − 32|</c>, so column 33 sits one texel
    ///         right of the apex. At radius 1 both taps are on the right arm and the answer is that
    ///         arm's own slope — 175. At radius 2 the left tap crosses onto the other arm, whose rise
    ///         is symmetric, so the difference over twice the span is the same six eight-bit steps and
    ///         the divergence is exactly half — 151. ⚠ The halving is the assertion that survives a
    ///         change to <c>intensity</c>, and it is the one that cannot be met by reading the
    ///         neighbourhood at any single wrong radius: a reach of 2 where 1 was asked for makes the
    ///         first number the second.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Doubling_the_curvature_radius_across_a_kink_halves_the_answer() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var near = OneOp(device, Kink(Side), TextureSurfaces.Curvature(1, 0));
        var far = OneOp(device, Kink(Side), TextureSurfaces.Curvature(1, 0, radius: 2f));

        var tight = TextureKernelHarness.At(near, 33, 32, 0);
        var wide = TextureKernelHarness.At(far, 33, 32, 0);

        output.WriteLine($"radius 1: {tight}; radius 2: {wide}");

        Assert.True(
            tight is >= 173 and <= 178,
            $"a radius of one across a kink of three steps a texel came out {tight} and the arm's own slope "
            + $"is 175 ({TextureKernelHarness.Adapter(device)})"
        );

        Assert.True(
            wide is >= 149 and <= 154,
            $"a radius of two came out {wide}, and 151 is the same rise over twice the span. {tight} would be "
            + $"a radius that never widened ({TextureKernelHarness.Adapter(device)})"
        );

        Assert.True(
            Math.Abs(tight - 128 - (2 * (wide - 128))) <= 3,
            $"{tight} and {wide} are not two and one of the same divergence, so the reach is not what divides "
            + $"it ({TextureKernelHarness.Adapter(device)})"
        );
    }

    // --- Ambient Occlusion ----------------------------------------------------------------------

    /// <summary>A plane occludes nothing, which is one everywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument's own failure mode is to report exactly this.</b> An occlusion kernel
    ///     that never ran leaves black, not white, so a flat plane coming back white is a real
    ///     assertion — but it is only half of one, which is why the step below it is in the same file.
    /// </remarks>
    [Fact]
    public void Ambient_occlusion_of_a_plane_is_one_everywhere() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            TextureKernelHarness.Solid(Side, 96, 96, 96, 255),
            TextureSurfaces.AmbientOcclusion(1, 0)
        );

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(255, TextureKernelHarness.At(picture, x, y, 0));
            }
        }
    }

    /// <summary>A wall darkens the low ground beside it, out to the radius and no further.</summary>
    /// <remarks>
    ///     ⚠ <b>"Out to the radius and no further" is the half that makes this a test of a horizon
    ///     search</b> rather than of any function that is dark near a step. The wall is at texel 32
    ///     and the radius is sixteen, so the ground at texel 2 cannot see it and has to come back
    ///     exactly white.
    /// </remarks>
    [Fact]
    public void Ambient_occlusion_darkens_the_low_side_of_a_wall_and_nothing_beyond_the_radius() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            Step(Side, 32),
            TextureSurfaces.AmbientOcclusion(1, 0, radius: 16f, samples: 8, height01: 0.2f)
        );

        var beside = TextureKernelHarness.At(picture, 31, 32, 0);
        var far = TextureKernelHarness.At(picture, 2, 32, 0);
        var above = TextureKernelHarness.At(picture, 48, 32, 0);

        output.WriteLine($"beside the wall {beside}, out of reach {far}, on top {above}");

        Assert.True(beside < 220, $"the ground beside the wall reads {beside} and is not occluded");
        Assert.Equal(255, far);
        Assert.Equal(255, above);
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

    /// <summary>Runs one op over an uploaded image of a stated shape, and reads the answer back.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="OneOp" /> with the square taken out</b>, and it exists for one assertion:
    ///     a kernel that scales two axes by two extents is only testable where the two differ — #947.
    ///     Everything else in this file is 64², and should stay so.
    /// </remarks>
    static Bitmap Sized(VulkanDevice device, byte[] source, int width, int height, TextureOp op) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, width, height);

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
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(1);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>Combines two uploaded normal maps and reads the answer back.</summary>
    static Bitmap TwoOp(VulkanDevice device, byte[] baseMap, byte[] detailMap) {
        var (first, firstStaging) = TextureKernelHarness.Upload(device, baseMap, Side, Side);
        var (second, secondStaging) = TextureKernelHarness.Upload(device, detailMap, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [TextureSurfaces.NormalCombine(2, 0, 1)],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);

            using var bake = evaluator.Evaluate(
                plan,
                new Dictionary<int, TextureHandle> { [0] = first, [1] = second }
            );

            return bake.Read(2);
        } finally {
            device.Destroy(firstStaging);
            device.Destroy(first);
            device.Destroy(secondStaging);
            device.Destroy(second);
        }
    }
}
