// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.1's source kernels, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here names its adapter and skips loudly rather than passing without
///         one.</b> Without a real device a headless run falls back to the Null device, exits 0 and
///         prints identical healthy counters — and a source kernel asserted there would have proved
///         that a black image equals a black image. <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into
///         a failure.
///     </para>
///     <para>
///         <b>Closed forms, and where possible an area rather than a texel.</b> A shape's covered
///         fraction is the strongest oracle in this file: a disc inscribed in its image covers exactly
///         π/4 of it and an equilateral triangle inscribed in the same circle covers 3√3/16, and
///         neither number can be reached by a kernel that drew a different shape, drew it at the wrong
///         size, or drew nothing. Halving the scale quarters the area, which is the same oracle read
///         as a ratio and is immune to any constant factor.
///     </para>
///     <para>
///         ⚠ <b>Doc 48's exit criterion 3 — a 1K bake and a downsampled 4K bake agreeing within
///         2/255 — is false as written for a hard-edged source, and that is a property of the picture
///         rather than a defect in the kernel.</b> The 4K bake is anti-aliased by the downsample and
///         the 1K one is not, so a falloff-zero disc and a checkerboard disagree by a full step all
///         the way round every boundary while agreeing exactly everywhere else. The comparison is
///         meaningful for a field that is band-limited at the *lower* resolution, and that is where
///         <see cref="A_source_kernel_bakes_the_same_picture_at_both_resolutions" /> makes it — a
///         soft-edged shape, a gradient and a noise, all continuous.
///     </para>
/// </remarks>
public class TextureSourceDeviceTests(ITestOutputHelper output) {
    /// <summary>The coarse bake, standing in for doc 48 § D8's 1K.</summary>
    const int Side = 64;

    /// <summary>The fine bake. Four times the coarse one, which is the 1K-to-4K ratio.</summary>
    const int Fine = 256;

    /// <summary>Doc 48's exit criterion 3, in eight-bit steps.</summary>
    const int Tolerance = 2;

    public static TheoryData<int, double> ShapeAreas =>
        new() {
            // A disc inscribed in the image covers π/4 of it.
            { (int)TextureShapeKind.Disc, Math.PI / 4 },
            // An equilateral triangle inscribed in that same circle covers 3√3/16.
            { (int)TextureShapeKind.Triangle, 3 * Math.Sqrt(3) / 16 }
        };

    /// <summary>A device, or a loud skip — or, when one was required, a failure.</summary>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";

    /// <summary>Uploads a picture as an RGBA8 texture a plan can read as an external image.</summary>
    static (TextureHandle Texture, BufferHandle Staging) Upload(
        VulkanDevice device,
        byte[] pixels,
        int width,
        int height
    ) {
        var texture = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                width,
                height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "source"
            )
        );

        var staging = device.CreateBuffer(
            new(pixels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "source staging")
        );

        device.Write(staging, 0, pixels);
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "upload")) {
            commands.Barrier(
                new BarrierGroup(
                    [],
                    [new TextureBarrier(texture, ResourceState.Undefined, ResourceState.CopyDestination)]
                )
            );

            commands.CopyBufferToTexture(staging, 0, new(texture), new(width, height, 1));

            commands.Barrier(
                new BarrierGroup(
                    [],
                    [new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]
                )
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        return (texture, staging);
    }

    /// <summary>The identity ramp: texel <c>i</c> of 256 holds <c>i/255</c> in every colour channel.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes <c>Gradient</c>'s closed form exact.</b> Through it the kernel's
    ///     output *is* its ramp position, so what a gradient test asserts is the sweep and nothing
    ///     about how a ramp is evaluated — which is the part
    ///     <c>Vixen.Ui.Controls.Advanced.Gradient</c> owns and this kernel deliberately does not.
    /// </remarks>
    static byte[] IdentityRamp() {
        var pixels = new byte[256 * 4];

        for (var i = 0; i < 256; i++) {
            pixels[i * 4] = (byte)i;
            pixels[(i * 4) + 1] = (byte)i;
            pixels[(i * 4) + 2] = (byte)i;
            pixels[(i * 4) + 3] = 255;
        }

        return pixels;
    }

    /// <summary>Two columns, black and white, for the sRGB filter-order question.</summary>
    static byte[] Edge() {
        var pixels = new byte[2 * 2 * 4];

        for (var y = 0; y < 2; y++) {
            for (var x = 0; x < 2; x++) {
                var at = ((y * 2) + x) * 4;
                var value = (byte)(x == 0 ? 0 : 255);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A horizontal ramp, the picture a resampler reproduces exactly.</summary>
    static byte[] Ramp(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var value = (byte)(x * 255 / (side - 1));
                var at = ((y * side) + x) * 4;

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    static byte At(Bitmap picture, int x, int y, int channel = 0) => picture.Pixels[picture.Offset(x, y) + channel];

    /// <summary>The x coordinate of a texel centre in the shape frame at scale 1 — −1 to 1 across the image.</summary>
    static float Axis(int index, int side) => (((index + 0.5f) / side) - 0.5f) * 2f;

    /// <summary>A box downsample, on the CPU, of a bake that has already been read back as bytes.</summary>
    /// <remarks>
    ///     Not a kernel and nothing in a graph does it — doc 48 § D3 forbids a CPU twin of a
    ///     <em>kernel</em>, and this is the measuring instrument the resolution comparison is made
    ///     with. A mean over a square block is what "downsample the 4K one" means.
    /// </remarks>
    static Bitmap Reduce(Bitmap picture, int factor) {
        var width = picture.Width / factor;
        var height = picture.Height / factor;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                for (var channel = 0; channel < 4; channel++) {
                    var total = 0;

                    for (var dy = 0; dy < factor; dy++) {
                        for (var dx = 0; dx < factor; dx++) {
                            total += At(picture, (x * factor) + dx, (y * factor) + dy, channel);
                        }
                    }

                    pixels[(((y * width) + x) * 4) + channel] = (byte)((total + (factor * factor / 2)) / (factor * factor));
                }
            }
        }

        return new(width, height, pixels);
    }

    /// <summary>One procedural op, its own image, at a given side.</summary>
    static TexturePlan Procedural(TextureOp op, int side) =>
        new() {
            BaseWidth = side,
            BaseHeight = side,
            Seed = 41823,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [op],
            Outputs = [0]
        };

    /// <summary>One op that reads one external image.</summary>
    static TexturePlan OverExternal(TextureOp op, int side) =>
        new() {
            BaseWidth = side,
            BaseHeight = side,
            Seed = 41823,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [op],
            Outputs = [1]
        };

    /// <summary>How many texels of a grey field are above the halfway mark.</summary>
    static int Covered(Bitmap picture) {
        var lit = 0;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                if (At(picture, x, y) > 127) {
                    lit++;
                }
            }
        }

        return lit;
    }

    /// <summary>A constant is the constant, everywhere.</summary>
    /// <remarks>
    ///     The one kernel whose closed form is its parameters, and the picture every bisection starts
    ///     from. It is also the cheapest place to see that the evaluator writes uniform members under
    ///     the kernel's own names: three different numbers in three channels cannot be produced by a
    ///     block that was filled in the wrong order.
    /// </remarks>
    [Fact]
    public void A_uniform_is_the_colour_it_was_given_in_every_texel() {
        using var device = Open();

        output.WriteLine($"adapter: {Adapter(device)}");

        VulkanDiagnostics.Reset();

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(Procedural(TextureSources.Uniform(0, 0.25f, 0.5f, 0.75f), Side));

        var picture = bake.Read(0);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            $"the evaluation on {Adapter(device)} produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        foreach (var (x, y) in new[] { (0, 0), (Side - 1, 0), (0, Side - 1), (Side - 1, Side - 1), (31, 17) }) {
            Assert.InRange(At(picture, x, y, 0), 62, 66);
            Assert.InRange(At(picture, x, y, 1), 126, 130);
            Assert.InRange(At(picture, x, y, 2), 189, 193);
            Assert.InRange(At(picture, x, y, 3), 253, 255);
        }
    }

    /// <summary>
    ///     ⚠ A shape's covered area is a number the shape cannot be wrong about: π/4 for the inscribed
    ///     disc, 3√3/16 for the inscribed equilateral triangle.
    /// </summary>
    /// <remarks>
    ///     This is doc 48's preferred oracle — "a shape whose covered area must halve" — and it
    ///     catches the three things a per-texel probe does not: a shape drawn at the wrong scale, a
    ///     shape drawn as a different kind, and a kernel that wrote only the first workgroup of the
    ///     image and left the rest at whatever the allocator had.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShapeAreas))]
    public void A_shapes_covered_area_is_the_fraction_geometry_says(int kind, double fraction) {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var op = TextureSources.Shape(0, (TextureShapeKind)kind, falloff: 0.005f);

        using var bake = evaluator.Evaluate(Procedural(op, Side));

        var lit = Covered(bake.Read(0));
        var expected = fraction * Side * Side;

        output.WriteLine($"adapter: {Adapter(device)}; kind {kind} covered {lit}, geometry says {expected:F1}");

        Assert.InRange(lit, expected * 0.95, expected * 1.05);
    }

    /// <summary>Halving a shape's scale quarters its area, which no constant factor can fake.</summary>
    [Fact]
    public void Halving_a_shapes_scale_quarters_its_area() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        using var whole = evaluator.Evaluate(Procedural(TextureSources.Shape(0, falloff: 0.005f), Side));
        using var half = evaluator.Evaluate(
            Procedural(TextureSources.Shape(0, scale: 0.5f, falloff: 0.005f), Side)
        );

        var big = Covered(whole.Read(0));
        var small = Covered(half.Read(0));

        output.WriteLine($"adapter: {Adapter(device)}; disc {big} at scale 1, {small} at scale 0.5");

        Assert.InRange(big / (double)small, 3.8, 4.2);

        // And the half-scale square is exact rather than approximate: |p| ≤ 1 at scale 0.5 is
        // uv within a quarter of the centre, which is texels 16 to 47 in each axis on a 64.
        using var square = evaluator.Evaluate(
            Procedural(TextureSources.Shape(0, TextureShapeKind.Square, 0.5f, falloff: 0.005f), Side)
        );

        Assert.InRange(Covered(square.Read(0)), 1020, 1028);
    }

    /// <summary>Each of the five profile kinds at a point, which is what tells them apart.</summary>
    /// <remarks>
    ///     ⚠ <b>The five are what pin <see cref="TextureShapeKind" />'s numbers to the kernel's
    ///     comparisons.</b> Nothing in the C# would notice a renumbering — a paraboloid drawn where a
    ///     gaussian was asked for is a perfectly plausible picture — so each kind is identified here
    ///     by the value only it produces at one place.
    /// </remarks>
    [Fact]
    public void Each_shape_profile_has_the_value_its_formula_gives() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        const int Column = 48;
        const int Row = 32;

        var px = Axis(Column, Side);
        var py = Axis(Row, Side);
        var r = MathF.Sqrt((px * px) + (py * py));

        output.WriteLine($"adapter: {Adapter(device)}; probing r = {r:F5} at ({Column}, {Row})");

        Expect(TextureShapeKind.Paraboloid, 1f - (r * r));
        Expect(TextureShapeKind.Gaussian, MathF.Exp(-r * r * 6.907755f));
        Expect(TextureShapeKind.Cone, 1f - r);
        Expect(TextureShapeKind.HalfBell, 1f - (r * r * (3f - (2f * r))));
        Expect(TextureShapeKind.Gradation, (px * 0.5f) + 0.5f);

        void Expect(TextureShapeKind kind, float value) {
            using var bake = evaluator.Evaluate(Procedural(TextureSources.Shape(0, kind), Side));

            var measured = At(bake.Read(0), Column, Row);
            var wanted = value * 255f;

            Assert.True(
                Math.Abs(measured - wanted) <= 2,
                $"{kind} at r = {r:F5} is {measured} and its formula says {wanted:F1} ({Adapter(device)})"
            );
        }
    }

    /// <summary>
    ///     ⚠ <c>falloff</c> is read by the three boundary kinds and by none of the five profiles, and
    ///     that exception is written down here rather than discovered.
    /// </summary>
    [Fact]
    public void A_profile_shape_ignores_the_falloff_and_a_boundary_shape_does_not() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        using var sharp = evaluator.Evaluate(
            Procedural(TextureSources.Shape(0, TextureShapeKind.Paraboloid, falloff: 0.01f), Side)
        );

        using var soft = evaluator.Evaluate(
            Procedural(TextureSources.Shape(0, TextureShapeKind.Paraboloid, falloff: 0.9f), Side)
        );

        Assert.Equal(sharp.Read(0).Pixels, soft.Read(0).Pixels);

        // The same two falloffs on a disc are two different pictures, so the test above is a claim
        // about the profile and not about a parameter nothing reads.
        using var hard = evaluator.Evaluate(Procedural(TextureSources.Shape(0, falloff: 0.01f), Side));
        using var blurred = evaluator.Evaluate(Procedural(TextureSources.Shape(0, falloff: 0.9f), Side));

        Assert.NotEqual(hard.Read(0).Pixels, blurred.Read(0).Pixels);
    }

    /// <summary>A two-by-two checker is four quadrants, and an offset of one cell inverts it.</summary>
    [Fact]
    public void A_checker_alternates_by_cell_and_an_odd_offset_inverts_it() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        using var plain = evaluator.Evaluate(Procedural(TextureSources.Checker(0, 2f, 2f), Side));
        using var shifted = evaluator.Evaluate(Procedural(TextureSources.Checker(0, 2f, 2f, offsetX: 1f), Side));

        var picture = plain.Read(0);

        output.WriteLine($"adapter: {Adapter(device)}");

        Assert.True(At(picture, 16, 16) < 4, $"the top-left cell is {At(picture, 16, 16)}");
        Assert.True(At(picture, 48, 16) > 251, $"the top-right cell is {At(picture, 48, 16)}");
        Assert.True(At(picture, 16, 48) > 251, $"the bottom-left cell is {At(picture, 16, 48)}");
        Assert.True(At(picture, 48, 48) < 4, $"the bottom-right cell is {At(picture, 48, 48)}");

        var other = shifted.Read(0);

        Assert.True(At(other, 16, 16) > 251);
        Assert.True(At(other, 48, 16) < 4);
    }

    /// <summary>
    ///     Through the identity ramp, a linear gradient's output <em>is</em> its position: exactly
    ///     <c>(x + 0.5) / width</c>.
    /// </summary>
    [Fact]
    public void A_linear_gradient_through_the_identity_ramp_is_its_own_position() {
        using var device = Open();

        var (ramp, staging) = Upload(device, IdentityRamp(), 256, 1);
        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            OverExternal(TextureSources.Gradient(1, 0), Side),
            new Dictionary<int, TextureHandle> { [0] = ramp }
        );

        var picture = bake.Read(1);

        output.WriteLine($"adapter: {Adapter(device)}; ends {At(picture, 0, 32)} and {At(picture, Side - 1, 32)}");

        for (var x = 0; x < Side; x++) {
            var wanted = (x + 0.5f) / Side * 255f;
            var measured = At(picture, x, 32);

            Assert.True(
                Math.Abs(measured - wanted) <= Tolerance,
                $"column {x} is {measured} and (x + 0.5)/{Side} is {wanted:F1} ({Adapter(device)})"
            );
        }

        device.Destroy(staging);
        device.Destroy(ramp);
    }

    /// <summary>The other three sweeps, each by the relation that only it satisfies.</summary>
    /// <remarks>
    ///     ⚠ <b>Reflected is asserted as <c>2·linear(|d|) − 1</c> rather than against a number</b>,
    ///     because that relation is what "mirrored about the centre" means and it holds whatever the
    ///     ramp is. The angular sweep's quarter turn is what says the rotation runs clockwise on
    ///     screen — a texel's y grows downwards, so the direction a quarter of the way round from
    ///     screen-right is screen-*down*.
    /// </remarks>
    [Fact]
    public void The_radial_reflected_and_angular_sweeps_are_the_shapes_their_names_claim() {
        using var device = Open();

        var (ramp, staging) = Upload(device, IdentityRamp(), 256, 1);
        using var evaluator = new TexturePlanEvaluator(device);
        var externals = new Dictionary<int, TextureHandle> { [0] = ramp };

        using var linear = evaluator.Evaluate(OverExternal(TextureSources.Gradient(1, 0), Side), externals);

        using var radial = evaluator.Evaluate(
            OverExternal(TextureSources.Gradient(1, 0, TextureGradientKind.Radial), Side),
            externals
        );

        using var reflected = evaluator.Evaluate(
            OverExternal(TextureSources.Gradient(1, 0, TextureGradientKind.Reflected), Side),
            externals
        );

        using var angular = evaluator.Evaluate(
            OverExternal(TextureSources.Gradient(1, 0, TextureGradientKind.Angular), Side),
            externals
        );

        var straight = linear.Read(1);
        var round = radial.Read(1);
        var mirrored = reflected.Read(1);
        var swept = angular.Read(1);

        output.WriteLine($"adapter: {Adapter(device)}");

        // Radial: nothing at the centre, and the middle of an edge is one radius away.
        Assert.True(At(round, 32, 32) < 12, $"the radial centre is {At(round, 32, 32)}");
        Assert.True(At(round, Side - 1, 32) > 243, $"the radial edge is {At(round, Side - 1, 32)}");

        // Reflected is the linear sweep folded: 2·linear − 1 on the far side of the centre.
        for (var x = 34; x < Side; x++) {
            var folded = (2 * At(straight, x, 32)) - 255;

            Assert.True(
                Math.Abs(At(mirrored, x, 32) - folded) <= 3,
                $"reflected at {x} is {At(mirrored, x, 32)} and 2·linear − 1 is {folded} ({Adapter(device)})"
            );
        }

        // Angular: screen-right is the start, screen-down is a quarter of the way round.
        Assert.True(At(swept, Side - 1, 32) < 6, $"the angular start is {At(swept, Side - 1, 32)}");
        Assert.InRange(At(swept, 32, Side - 1), 58, 70);
    }

    /// <summary>
    ///     ⚠ An sRGB bitmap is decoded <em>before</em> it is filtered, and the midpoint of a
    ///     black-to-white edge is the one texel that can tell.
    /// </summary>
    /// <remarks>
    ///     Decode-then-filter puts the midpoint at 0.5 in linear light — 131 of 255. Filter-then-decode
    ///     puts it at 0.214, which is 58, and reads as an edge that is too dark: the second half of
    ///     § 4.1's "commonest wrong-looking graph", and the half that survives a code review because
    ///     the decode <em>is</em> there, one line too late.
    /// </remarks>
    [Fact]
    public void An_srgb_bitmap_is_decoded_before_it_is_filtered() {
        using var device = Open();

        var (source, staging) = Upload(device, Edge(), 2, 2);
        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            OverExternal(TextureSources.Bitmap(1, 0, srgb: true), Side),
            new Dictionary<int, TextureHandle> { [0] = source }
        );

        var midpoint = At(bake.Read(1), 32, 32);

        output.WriteLine($"adapter: {Adapter(device)}; the edge's midpoint is {midpoint}");

        Assert.True(
            midpoint is > 120 and < 145,
            $"the midpoint of a decoded black-to-white edge is {midpoint}; 131 is decode-then-filter and 58 is "
            + $"filter-then-decode ({Adapter(device)})"
        );

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>A bitmap resampled into its own resolution is itself, texel for texel.</summary>
    [Fact]
    public void A_bitmap_resampled_into_its_own_resolution_is_unchanged() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side, Side);
        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            OverExternal(TextureSources.Bitmap(1, 0, srgb: false), Side),
            new Dictionary<int, TextureHandle> { [0] = source }
        );

        var picture = bake.Read(1);

        output.WriteLine($"adapter: {Adapter(device)}");

        for (var x = 0; x < Side; x++) {
            var wanted = (byte)(x * 255 / (Side - 1));

            Assert.True(
                Math.Abs(At(picture, x, 17) - wanted) <= 1,
                $"column {x} came back as {At(picture, x, 17)} and went in as {wanted} ({Adapter(device)})"
            );
        }

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>Every basis draws a field, in range, that is not one value.</summary>
    /// <remarks>
    ///     ⚠ <b>The first thing to know about a noise kernel is that it ran.</b> A dispatch that wrote
    ///     nothing, a hash that collapsed to a constant and a normalisation that divided the field
    ///     away all produce a flat image, and a flat image is what an unwritten storage texture looks
    ///     like on a fresh device.
    /// </remarks>
    [Theory]
    [InlineData((int)TextureNoiseBasis.Value)]
    [InlineData((int)TextureNoiseBasis.Gradient)]
    [InlineData((int)TextureNoiseBasis.Worley)]
    [InlineData((int)TextureNoiseBasis.White)]
    public void Every_noise_basis_draws_a_field_that_is_not_one_value(int basis) {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            Procedural(TextureSources.Noise(0, (TextureNoiseBasis)basis, 8f), Side)
        );

        var picture = bake.Read(0);
        HashSet<byte> values = [];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                values.Add(At(picture, x, y));
            }
        }

        output.WriteLine($"adapter: {Adapter(device)}; basis {basis} drew {values.Count} distinct values");

        Assert.True(values.Count > 24, $"basis {basis} drew {values.Count} distinct values on {Adapter(device)}");
    }

    /// <summary>
    ///     Worley's F1 never exceeds its F2, and its cell index is a field of its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>F1 ≤ F2</c> is the closed form of a two-nearest search</b> and it is false for every
    ///     way of getting the bookkeeping wrong — a swap, an update that forgets to demote the old F1,
    ///     a second loop that starts from the same initial value. The cell index is asserted
    ///     separately because it is what doc 48 § 4.1 says this basis exists for; a constant blue
    ///     channel would satisfy the inequality perfectly.
    /// </remarks>
    [Fact]
    public void Worley_answers_with_f1_below_f2_and_a_cell_index() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            Procedural(TextureSources.Noise(0, TextureNoiseBasis.Worley, 6f), Side)
        );

        var picture = bake.Read(0);
        HashSet<byte> cells = [];
        var nearest = 255;
        var furthest = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var f1 = At(picture, x, y, 0);
                var f2 = At(picture, x, y, 1);

                Assert.True(f1 <= f2 + 1, $"F1 is {f1} and F2 is {f2} at ({x}, {y}) on {Adapter(device)}");

                cells.Add(At(picture, x, y, 2));
                nearest = Math.Min(nearest, f1);
                furthest = Math.Max(furthest, f2);
            }
        }

        output.WriteLine(
            $"adapter: {Adapter(device)}; {cells.Count} cell indices, nearest F1 {nearest}, furthest F2 {furthest}"
        );

        Assert.True(
            furthest < 250,
            $"the furthest F2 is {furthest}, which is a second-nearest that never got written "
            + $"({Adapter(device)})"
        );

        // ⚠ **Neither assertion above catches the mistake this basis is actually prone to**, and that
        // is worth writing down rather than discovering. Delete the demotion — the `f2 = f1` that runs
        // before `f1 = reach` — and F2 becomes the smallest distance among the cells the loop happened
        // to visit *after* the nearest one. That is still ordered, still bounded, and still a plausible
        // grey field. Both tests stay green.
        //
        // **What it is not is a distance.** F1 and F2 are distances to points that do not move, so
        // each is 1-Lipschitz in position: two adjacent texels are one texel apart, so their values
        // differ by at most one texel's worth of distance. A per-cell bookkeeping bug is
        // discontinuous at every cell border, where the visit order changes — so the largest step
        // between neighbours is the measurement that separates them.
        var step = Math.Max(Roughness(picture, 1, 0, 1), Roughness(picture, 0, 1, 1));

        output.WriteLine($"the largest step between adjacent F2 texels is {step} of 255");

        // One texel is scale/Side of a cell, F2 is reported in units of two cells, and a byte is
        // 1/255 of that — so a 1-Lipschitz field cannot step by more than about twelve, and the two
        // added are the read-back's own rounding at each end.
        var bound = (int)Math.Ceiling(6.0 / Side / 2 * 255) + 2;

        Assert.True(
            step <= bound,
            $"F2 steps by {step} of 255 between two adjacent texels and a distance field cannot step "
            + $"by more than {bound} ({Adapter(device)})"
        );

        static int Roughness(Bitmap picture, int dx, int dy, int channel) {
            var worst = 0;

            for (var y = 0; y + dy < picture.Height; y++) {
                for (var x = 0; x + dx < picture.Width; x++) {
                    worst = Math.Max(
                        worst,
                        Math.Abs(At(picture, x, y, channel) - At(picture, x + dx, y + dy, channel))
                    );
                }
            }

            return worst;
        }

        // Six cells across means thirty-six feature points on this image, so some texel is nearly on
        // one — and thirty-six independent hashes do not collide into a handful of bytes.
        Assert.True(nearest < 12, $"the nearest F1 anywhere is {nearest} on {Adapter(device)}");
        Assert.True(cells.Count >= 20, $"the cell index took {cells.Count} values on {Adapter(device)}");
    }

    /// <summary>
    ///     Two identical noise ops in one plan draw different fields, which is what
    ///     <see cref="TexturePlan.SeedFor" /> is for.
    /// </summary>
    [Fact]
    public void Two_noise_ops_with_the_same_parameters_draw_different_fields() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 41823,
            Images = [new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)],
            Ops = [TextureSources.Noise(0), TextureSources.Noise(1)],
            Outputs = [0, 1]
        };

        using var bake = evaluator.Evaluate(plan);

        output.WriteLine($"adapter: {Adapter(device)}");

        Assert.NotEqual(bake.Read(0).Pixels, bake.Read(1).Pixels);
    }

    /// <summary>
    ///     ⚠ A tiling noise's lattice wraps, so the two ends of the image continue into each other.
    /// </summary>
    /// <remarks>
    ///     <b>Measured as a difference against the same difference with tiling off</b>, rather than
    ///     against a number. With the lattice folded, the first and last columns sit either side of
    ///     one shared corner and differ by a fraction of a cell's variation; without it, the corner at
    ///     the right-hand end is an independent hash and the two columns are unrelated. A kernel whose
    ///     <c>Fold</c> did nothing would make the two runs identical, which is precisely what the
    ///     second assertion refuses.
    /// </remarks>
    [Fact]
    public void A_tiling_noise_joins_its_own_edges() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        using var seamless = evaluator.Evaluate(
            Procedural(TextureSources.Noise(0, TextureNoiseBasis.Value, 4f, tiling: true), Side)
        );

        using var open = evaluator.Evaluate(
            Procedural(TextureSources.Noise(0, TextureNoiseBasis.Value, 4f), Side)
        );

        var joined = Seam(seamless.Read(0));
        var cut = Seam(open.Read(0));

        output.WriteLine($"adapter: {Adapter(device)}; seam {joined:F1} tiling, {cut:F1} not");

        Assert.True(joined < 10, $"a tiling noise's seam differs by {joined:F1} of 255 on {Adapter(device)}");
        Assert.True(cut > 30, $"an untiled noise's seam differs by only {cut:F1} on {Adapter(device)}");

        static double Seam(Bitmap picture) {
            var total = 0;

            for (var y = 0; y < picture.Height; y++) {
                total += Math.Abs(At(picture, 0, y) - At(picture, picture.Width - 1, y));
            }

            return total / (double)picture.Height;
        }
    }

    /// <summary>
    ///     Doc 48 § D8's exit criterion 3: a coarse bake and a downsampled fine one are the same
    ///     picture, for every source whose field is continuous.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The hard-edged kinds are deliberately not in this list, and their absence is a finding
    ///     rather than an omission.</b> A falloff-zero disc, a checkerboard and white noise are
    ///     discontinuous, so the fine bake carries anti-aliasing the coarse one cannot and the two
    ///     disagree by up to a full step along every edge. That is a property of the picture. What
    ///     doc 48 § D8 is actually asking — does a length parameter reach the kernel in the units of
    ///     the image it writes — is answered here for the fields where the question is meaningful, and
    ///     it is answered for the filters by <c>TexturePlanDeviceTests</c>'s impulse bar.
    /// </remarks>
    [Theory]
    [InlineData("a soft disc")]
    [InlineData("a linear gradient")]
    [InlineData("value noise")]
    public void A_source_kernel_bakes_the_same_picture_at_both_resolutions(string what) {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var ramp = default(TextureHandle);
        var staging = default(BufferHandle);
        var uploaded = what == "a linear gradient";

        if (uploaded) {
            (ramp, staging) = Upload(device, IdentityRamp(), 256, 1);
        }

        var coarse = Bake(Side);
        var fine = Reduce(Bake(Fine), Fine / Side);
        var worst = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                worst = Math.Max(worst, Math.Abs(At(coarse, x, y) - At(fine, x, y)));
            }
        }

        output.WriteLine($"adapter: {Adapter(device)}; {what} differs by at most {worst} of 255");

        Assert.True(
            worst <= Tolerance,
            $"{what} at {Side} and at {Fine} downsampled differ by {worst} of 255, and doc 48 allows "
            + $"{Tolerance} ({Adapter(device)})"
        );

        if (uploaded) {
            device.Destroy(staging);
            device.Destroy(ramp);
        }

        Bitmap Bake(int side) {
            if (uploaded) {
                using var over = evaluator.Evaluate(
                    OverExternal(TextureSources.Gradient(1, 0), side),
                    new Dictionary<int, TextureHandle> { [0] = ramp }
                );

                return over.Read(1);
            }

            var op = what == "value noise"
                ? TextureSources.Noise(0, TextureNoiseBasis.Value, 4f)
                : TextureSources.Shape(0, TextureShapeKind.Disc, falloff: 0.5f);

            using var bake = evaluator.Evaluate(Procedural(op, side));

            return bake.Read(0);
        }
    }

    /// <summary>
    ///     The whole of § 4.1 that is implemented, in one plan, producing six pictures nobody has to
    ///     read one at a time.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Six kernels compiled and six dispatches recorded is equally true of a run that
    ///     produced nothing</b>, so what is asserted is that no two of the six images are the same
    ///     bytes — which a plan of six black images would fail.
    /// </remarks>
    [Fact]
    public void The_six_source_kernels_run_in_one_plan_and_draw_six_different_pictures() {
        using var device = Open();

        var (ramp, staging) = Upload(device, IdentityRamp(), 256, 1);
        var (source, sourceStaging) = Upload(device, Ramp(Side), Side, Side);

        VulkanDiagnostics.Reset();

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 41823,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8, External: true),
                .. Enumerable.Repeat(new TextureImage(TextureFormat.Rgba8), 6)
            ],
            Ops = [
                TextureSources.Uniform(2, 0.3f),
                TextureSources.Bitmap(3, 0, srgb: false),
                TextureSources.Gradient(4, 1),
                TextureSources.Shape(5),
                TextureSources.Noise(6),
                TextureSources.Checker(7, 4f, 4f)
            ],
            Outputs = [2, 3, 4, 5, 6, 7]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            plan,
            new Dictionary<int, TextureHandle> { [0] = source, [1] = ramp }
        );

        output.WriteLine($"adapter: {Adapter(device)}; {evaluator.Compilations} variants, {bake.Dispatches} dispatches");

        Assert.Equal(6, bake.Dispatches);
        Assert.Equal(6, evaluator.Compilations);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            $"the evaluation on {Adapter(device)} produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        var pictures = new List<ImmutableArray<byte>>();

        for (var image = 2; image < 8; image++) {
            pictures.Add([.. bake.Read(image).Pixels]);
        }

        for (var a = 0; a < pictures.Count; a++) {
            for (var b = a + 1; b < pictures.Count; b++) {
                Assert.False(
                    pictures[a].SequenceEqual(pictures[b]),
                    $"images {a + 2} and {b + 2} are the same bytes on {Adapter(device)}"
                );
            }
        }

        device.Destroy(staging);
        device.Destroy(ramp);
        device.Destroy(sourceStaging);
        device.Destroy(source);
    }
}
