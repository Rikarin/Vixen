// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>The evaluator, on a real device, asserted against closed forms.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here names its adapter, and skips loudly rather than passing without
///         one.</b> Doc 48 § D3: without a real device a headless run falls back to the Null device,
///         exits 0 and prints identical healthy counters — so a texture-graph test that passed there
///         would have proved that a black image equals a black image. <see cref="Open" /> is the only
///         way into this file, it records the adapter's name into every failure message, and
///         <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure.
///     </para>
///     <para>
///         <b>Closed forms, not goldens and not a CPU twin.</b> Doc 48 § D3 forbids the second
///         implementation; what is asserted is arithmetic that has one answer — a box filter's impulse
///         response is <c>1/(2r+1)</c> over exactly <c>2r+1</c> texels, a levels curve maps three
///         known inputs to three known outputs, and multiply blend of <c>a</c> and <c>b</c> is
///         <c>ab</c>. Those are true of the kernel whoever wrote it.
///     </para>
/// </remarks>
public class TexturePlanDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

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

    /// <summary>What ran, said in every message so a number is never anonymous.</summary>
    static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";

    /// <summary>Uploads a picture as an RGBA8 texture the plan can read.</summary>
    /// <remarks>
    ///     ⚠ <b>On the compute queue, because that is the queue the kernel that reads it runs on.</b>
    ///     The texture is <c>ResourceSharing.Exclusive</c>, so filling it from the graphics family and
    ///     sampling it from a compute family is the same undefined cross-family access
    ///     <see cref="TexturePlanEvaluator" />'s own lists avoid — in reverse, and equally invisible
    ///     on a unified-family adapter like this Mac's.
    /// </remarks>
    static (TextureHandle Texture, BufferHandle Staging) Upload(VulkanDevice device, byte[] pixels, int side) {
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, side, side, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "source")
        );

        var staging = device.CreateBuffer(
            new(pixels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "source staging")
        );

        device.Write(staging, 0, pixels);
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Compute, "upload")) {
            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.Undefined, ResourceState.CopyDestination)])
            );

            commands.CopyBufferToTexture(staging, 0, new(texture), new(side, side, 1));

            commands.Barrier(
                new BarrierGroup(
                    [],
                    [new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]
                )
            );

            commands.Finish();
            device.ComputeQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        return (texture, staging);
    }

    /// <summary>A black image with one white texel, which is what a filter's impulse response is read off.</summary>
    static byte[] Impulse(int side, int x, int y) {
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

    /// <summary>A horizontal ramp from black to white, which a levels curve is read off.</summary>
    static byte[] Ramp(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var value = (byte)(x * 255 / (side - 1));
                var at = (((y * side) + x) * 4);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A vertical edge down the middle: black on the left, white on the right.</summary>
    /// <remarks>
    ///     ⚠ <b>The one source shape that is exactly the same picture at every power-of-two
    ///     resolution</b>, which is what doc 48 § D8's criterion needs of an input. A ramp is not — it
    ///     is <em>invariant</em> under a box blur in its interior, so it would agree at both
    ///     resolutions whether or not the radius scaled. An impulse is not either: one texel at 1K is
    ///     four at 4K, so the two bakes would be given different pictures. A step edge is the same
    ///     continuous image sampled twice, and a box blur turns it into a ramp whose *width* is the
    ///     radius — which is exactly the quantity under test.
    /// </remarks>
    static byte[] Step(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var value = (byte)(x < side / 2 ? 0 : 255);
                var at = ((y * side) + x) * 4;

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    static TextureOp Levels(
        int output,
        int input,
        float inputBlack,
        float inputWhite,
        float gamma = 1f,
        float dither = 0f
    ) =>
        new() {
            Kernel = "Levels",
            Output = output,
            Inputs = [input],
            Parameters = [
                new("inputBlack", inputBlack),
                new("inputWhite", inputWhite),
                new("gamma", gamma),
                new("outputBlack", 0f),
                new("outputWhite", 1f),
                new("dither", dither)
            ]
        };

    static TextureOp Blur(int output, int input, float radiusAtBase, int stepX, int stepY) =>
        new() {
            Kernel = "Blur",
            Output = output,
            Inputs = [input],
            Parameters = [
                new("radius", radiusAtBase, TextureParameterUnit.TexelsAtBase),
                new("stepX", stepX),
                new("stepY", stepY)
            ]
        };

    static TextureOp Mix(int output, int background, int foreground, int mode, float opacity) =>
        new() {
            Kernel = "Blend",
            Output = output,
            Inputs = [background, foreground],
            Parameters = [new("mode", mode), new("opacity", opacity)]
        };

    static byte At(Bitmap picture, int x, int y, int channel) => picture.Pixels[picture.Offset(x, y) + channel];

    /// <summary>A plan runs, the pictures are not black, and the adapter that produced them is named.</summary>
    /// <remarks>
    ///     ⚠ <b>The first assertion in this file is about the device and not about the picture.</b>
    ///     Every structural claim the evaluator makes — a texture was created, a pipeline was built, a
    ///     dispatch was recorded, the counters went up — is equally true of a run that produced
    ///     nothing at all.
    /// </remarks>
    [Fact]
    public void The_adapter_is_named_and_the_plan_produces_a_picture() {
        using var device = Open();

        output.WriteLine($"adapter: {Adapter(device)}");

        Assert.False(
            string.IsNullOrWhiteSpace(device.Adapter.Name),
            "The device did not name its adapter, so nothing measured here can be attributed."
        );

        VulkanDiagnostics.Reset();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [Levels(1, 0, 0f, 1f)],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        Assert.Equal(1, bake.Dispatches);

        var picture = bake.Read(1);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            $"The evaluation on {Adapter(device)} produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        // An identity levels curve reproduces the ramp, which is a picture with sixty-four distinct
        // greys in it — and a black target, an unwritten target and a flat fill are all one.
        HashSet<byte> greys = [];

        for (var x = 0; x < Side; x++) {
            greys.Add(At(picture, x, 0, 0));
        }

        Assert.True(greys.Count >= 60, $"the identity curve produced {greys.Count} distinct values on {Adapter(device)}");
        Assert.True(At(picture, 0, 0, 0) < 8, $"the ramp's left end is {At(picture, 0, 0, 0)} on {Adapter(device)}");
        Assert.True(At(picture, Side - 1, 0, 0) > 247, $"the ramp's right end is {At(picture, Side - 1, 0, 0)}");

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>
    ///     The levels curve at three known points, which is arithmetic with one answer.
    /// </summary>
    [Fact]
    public void A_levels_curve_maps_three_known_inputs_to_three_known_outputs() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            // 0.25 becomes black, 0.75 becomes white, 0.5 becomes the middle.
            Ops = [Levels(1, 0, 0.25f, 0.75f)],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        var picture = bake.Read(1);

        // The ramp is x/63, so these three columns carry about 0.25, 0.5 and 0.75.
        Assert.InRange(At(picture, 16, 8, 0), 0, 6);
        Assert.InRange(At(picture, 32, 8, 0), 118, 138);
        Assert.InRange(At(picture, 47, 8, 0), 249, 255);

        // And below the input black everything is floored rather than wrapped, which is what a
        // saturate that was left out would break.
        Assert.Equal(0, At(picture, 0, 8, 0));

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>
    ///     A box blur's impulse response is <c>2r + 1</c> texels of <c>1/(2r + 1)</c> each.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The closed form doc 48 § D3 names, and it catches three separate mistakes at once:</b>
    ///     a filter that is too wide or too narrow by one, a filter that is not normalised, and a
    ///     filter that ran along the wrong axis.
    /// </remarks>
    [Fact]
    public void A_box_blurs_impulse_response_is_a_normalised_bar_of_the_right_width() {
        using var device = Open();

        const int Radius = 3;
        const int Centre = 32;

        var (source, staging) = Upload(device, Impulse(Side, Centre, Centre), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float)],
            Ops = [Blur(1, 0, Radius, 1, 0)],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        var picture = bake.Read(1);
        var expected = (byte)Math.Round(255f / ((2 * Radius) + 1));

        for (var offset = -Radius; offset <= Radius; offset++) {
            var value = At(picture, Centre + offset, Centre, 0);

            Assert.True(
                Math.Abs(value - expected) <= 2,
                $"texel {offset} of the bar is {value} and a normalised box of radius {Radius} is {expected} "
                + $"({Adapter(device)})"
            );
        }

        // One outside the bar on each side, and the row above, are black — a blur along x must not
        // reach either.
        Assert.True(At(picture, Centre + Radius + 1, Centre, 0) <= 2);
        Assert.True(At(picture, Centre - Radius - 1, Centre, 0) <= 2);
        Assert.True(At(picture, Centre, Centre - 1, 0) <= 2);

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>
    ///     ⚠ Doc 48 § D8 on a device: the same authored radius is half as wide, in texels, on a
    ///     half-resolution image.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two plans writing images of exactly the same size.</b> The first has a base of 64
    ///         and writes at level 0; the second has a base of 128 and writes at level 1 — 64 texels
    ///         either way, reading the same source, dispatching the same number of groups. The only
    ///         thing that differs is what "8 texels at the base" resolves to, and the picture is what
    ///         says whether the evaluator applied it. A radius that reached the kernel unscaled would
    ///         make these two bars identical, which is a perfectly plausible picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the mip difference <em>within</em> one plan, and it is not § D8's
    ///         criterion</b> — the two plans here are two graphs, not one graph baked twice.
    ///         <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a> found that half missing
    ///         and it is
    ///         <see cref="The_same_plan_baked_at_four_times_the_resolution_agrees_with_the_smaller_bake" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radius_in_texels_at_base_is_half_as_wide_on_a_half_resolution_image() {
        using var device = Open();

        const int Centre = 32;
        const float RadiusAtBase = 8f;

        var (source, staging) = Upload(device, Impulse(Side, Centre, Centre), Side);
        using var evaluator = new TexturePlanEvaluator(device);

        var full = Bar(evaluator, device, source, 64, 0);
        var half = Bar(evaluator, device, source, 128, 1);

        output.WriteLine($"adapter: {Adapter(device)}; full {full}, half {half}");

        // 2r + 1 with r = 8, and 2r + 1 with r = 4.
        Assert.Equal(17, full);
        Assert.Equal(9, half);

        device.Destroy(staging);
        device.Destroy(source);

        int Bar(TexturePlanEvaluator evaluator, VulkanDevice device, TextureHandle input, int baseSize, int level) {
            var plan = new TexturePlan {
                BaseWidth = baseSize,
                BaseHeight = baseSize,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float, level)],
                Ops = [Blur(1, 0, RadiusAtBase, 1, 0)],
                Outputs = [1]
            };

            Assert.Equal(Side, plan.SizeOf(1).X);

            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = input });

            var picture = bake.Read(1);
            var lit = 0;

            for (var x = 0; x < Side; x++) {
                if (At(picture, x, Centre, 0) > 2) {
                    lit++;
                }
            }

            return lit;
        }
    }

    /// <summary>
    ///     ⚠ Doc 48 § D8's actual criterion: the same plan baked at 1× and at 4×, the larger
    ///     downsampled, agreeing within a small tolerance.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The test the plan document asks for and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a> found missing.</b> One
    ///         plan, authored at 64, baked twice: once at <c>BakeLevelOffset</c> 0 and once at −2. The
    ///         source is the same continuous picture sampled at both sizes, the radius is authored
    ///         once in texels-at-base, and the 256² result is box-downsampled 4:1 and compared with
    ///         the 64² one texel for texel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Before #619 the plan had no way to express the second bake at all</b>, which is
    ///         why nothing here could be written: moving <c>BaseWidth</c> to 256 moves the unit the
    ///         radius is counted in by the same factor and produces a picture four times sharper.
    ///         That is the two-year fuse § D8 names, and this is the assertion that keeps it out —
    ///         for every kernel added to <c>Shaders/</c> afterwards as much as for this one.
    ///     </para>
    ///     <para>
    ///         <b>The downsample is the criterion, not a CPU kernel.</b> § D3 forbids a C# twin of a
    ///         kernel because a parity test against one proves the two transcriptions agree; a 4×4 box
    ///         average is how the two bakes are brought into one coordinate system, and nothing in a
    ///         graph does it.
    ///     </para>
    ///     <para>
    ///         <b>Why the tolerance is not zero, and why it is nowhere near the bug.</b> A box of
    ///         radius <c>r</c> is <c>2r + 1</c> texels wide, and <c>2(4r) + 1</c> is three texels
    ///         short of four times that — so the 4× ramp is about 3 % shallower and the profiles part
    ///         by a few 255ths at the ramp's ends. An unscaled radius parts them by ninety.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_same_plan_baked_at_four_times_the_resolution_agrees_with_the_smaller_bake() {
        using var device = Open();

        const int Authored = 64;
        const int Large = 256;
        const int Factor = Large / Authored;
        const float RadiusAtBase = 12f;

        var (small, smallStaging) = Upload(device, Step(Authored), Authored);
        var (large, largeStaging) = Upload(device, Step(Large), Large);

        using var evaluator = new TexturePlanEvaluator(device);

        var at1x = Bake(small, 0);
        var at4x = Bake(large, TexturePlan.BakeLevelFor(Authored, Large));

        Assert.Equal(Authored, at1x.Width);
        Assert.Equal(Large, at4x.Width);

        var reduced = Downsample(at4x, Factor);
        var worst = 0;
        var worstAt = 0;
        var total = 0L;

        for (var x = 0; x < Authored; x++) {
            var difference = Math.Abs(At(at1x, x, Authored / 2, 0) - reduced[x]);

            total += difference;

            if (difference > worst) {
                worst = difference;
                worstAt = x;
            }
        }

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"adapter: {Adapter(device)}; worst {worst}/255 at column {worstAt}, "
                + $"mean {total / (double)Authored:F2}/255"
            )
        );

        // A box of radius 12 is 25 texels and one of radius 48 is 97, and 97 is not 100 — so the two
        // ramps differ in slope by 3 % and part by a few 255ths where the ramp meets the flat. A
        // radius that did not scale parts them by about ninety.
        Assert.True(
            worst <= 8,
            $"the 4× bake downsampled differs from the 1× bake by {worst}/255 at column {worstAt} on "
            + $"{Adapter(device)}, and § D8 says the two are the same material"
        );

        device.Destroy(smallStaging);
        device.Destroy(small);
        device.Destroy(largeStaging);
        device.Destroy(large);

        return;

        Bitmap Bake(TextureHandle source, int bake) {
            var plan = new TexturePlan {
                BaseWidth = Authored,
                BaseHeight = Authored,
                BakeLevelOffset = bake,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float)],
                Ops = [Blur(1, 0, RadiusAtBase, 1, 0)],
                Outputs = [1]
            };

            using var baked = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

            return baked.Read(1);
        }

        // A box average of one row, which is what brings the larger bake into the smaller one's
        // coordinates. Averaged over the block in both axes so a per-row artefact cannot hide in it.
        int[] Downsample(Bitmap picture, int factor) {
            var row = new int[picture.Width / factor];

            for (var x = 0; x < row.Length; x++) {
                var sum = 0;

                for (var dy = 0; dy < factor; dy++) {
                    for (var dx = 0; dx < factor; dx++) {
                        sum += At(picture, (x * factor) + dx, ((picture.Height / 2 / factor) * factor) + dy, 0);
                    }
                }

                row[x] = sum / (factor * factor);
            }

            return row;
        }
    }

    /// <summary>
    ///     ⚠ A Levels op writing an image larger than the one it reads clamps to the <em>source's</em>
    ///     edge.
    /// </summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/618">#618</a>, and no test in this
    ///     suite used unequal sizes.</b> <c>Levels.rvn</c> took its extent from <c>target</c> and then
    ///     clamped its <em>source</em> read to it — a no-op after <c>Main</c>'s own bounds guard, so
    ///     it looked like a bounds check and was not one. Everything past the source's width was an
    ///     out-of-bounds <c>Load</c>: undefined, and black on this driver. Its two siblings both ask
    ///     their own source. The closed form is that column 100 of a 128-wide output over a 64-wide
    ///     ramp is the ramp's last column, which is white — and was black.
    /// </remarks>
    [Fact]
    public void A_levels_op_writing_a_larger_image_clamps_to_its_sources_edge() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),

                // Twice the source in each axis, which is a level offset the plan has always allowed
                // and no kernel test had ever asked for.
                new(TextureFormat.Rgba8, -1)
            ],
            Ops = [Levels(1, 0, 0f, 1f)],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        var picture = bake.Read(1);

        Assert.Equal(Side * 2, picture.Width);
        Assert.Equal(Side * 2, picture.Height);

        // Inside the source's extent the identity curve is the ramp, one output texel per source
        // texel — Levels does not resample and is not supposed to.
        Assert.InRange(At(picture, 32, 8, 0), 124, 135);

        // And past it, every column is the ramp's last one. Zero here is the out-of-bounds read.
        foreach (var x in (int[])[Side, Side + 1, 100, (Side * 2) - 1]) {
            Assert.True(
                At(picture, x, 8, 0) > 247,
                $"column {x} of a {Side * 2}-wide output over a {Side}-wide source is "
                + $"{At(picture, x, 8, 0)} and the source's edge is white ({Adapter(device)})"
            );
        }

        // The bottom of the image as well, because the y clamp is a second copy of the same mistake.
        Assert.True(At(picture, 32, (Side * 2) - 1, 0) is > 124 and < 136);

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>Multiply blend of two known images is their product, texel by texel.</summary>
    [Fact]
    public void A_multiply_blend_is_the_product_of_its_two_inputs() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                // A flat 0.6, made by collapsing the ramp's *output* range onto one value — so the
                // second input is a constant whatever the first one was.
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() {
                    Kernel = "Levels",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("inputBlack", 0f),
                        new("inputWhite", 1f),
                        new("gamma", 1f),
                        new("outputBlack", 0.6f),
                        new("outputWhite", 0.6f),
                        new("dither", 0f)
                    ]
                },
                Mix(2, 0, 1, 1, 1f)
            ],
            Outputs = [1, 2]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        var constant = bake.Read(1);
        var picture = bake.Read(2);

        // 0.6 of 255, everywhere, whatever the ramp was under it.
        Assert.InRange(At(constant, 8, 8, 0), 151, 155);
        Assert.InRange(At(constant, 56, 8, 0), 151, 155);

        // And the multiply is a product with one right answer at every column: ramp × 0.6.
        for (var x = 0; x < Side; x += 8) {
            var expected = (byte)Math.Round(x * 255f / (Side - 1) * 0.6f);

            Assert.True(
                Math.Abs(At(picture, x, 8, 0) - expected) <= 3,
                $"column {x} is {At(picture, x, 8, 0)} and the product is {expected} ({Adapter(device)})"
            );
        }

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>
    ///     A chain long enough that the pool has to reuse a texture still produces the right picture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that would go red if an op's output aliased an image it reads.</b>
    ///     <c>TexturePoolTests</c> says the slots do not collide; this says the pictures survive it,
    ///     which is the half no schedule can prove on its own.
    /// </remarks>
    [Fact]
    public void A_chain_that_reuses_the_pool_still_produces_the_right_picture() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var images = ImmutableArray.CreateBuilder<TextureImage>();
        var ops = ImmutableArray.CreateBuilder<TextureOp>();

        images.Add(new(TextureFormat.Rgba8, External: true));

        // Eight identity curves in a row. Whatever the pool does with the seven intermediates, the
        // answer is still the ramp.
        for (var index = 0; index < 8; index++) {
            images.Add(new(TextureFormat.Rgba8));
            ops.Add(Levels(index + 1, index, 0f, 1f));
        }

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = images.ToImmutable(),
            Ops = ops.ToImmutable(),
            Outputs = [8]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        Assert.Equal(8, bake.Dispatches);
        Assert.Equal(2, bake.Schedule.Allocations);

        var picture = bake.Read(8);

        for (var x = 0; x < Side; x += 8) {
            var expected = (byte)(x * 255 / (Side - 1));

            Assert.True(
                Math.Abs(At(picture, x, 4, 0) - expected) <= 3,
                $"column {x} is {At(picture, x, 4, 0)} after eight identity curves and should be {expected} "
                + $"({Adapter(device)})"
            );
        }

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>Doc 48 § M0's deliverable: a plan, evaluated, written to a PNG on disk.</summary>
    [Fact]
    public void A_bake_lands_on_disk_as_a_png() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 41823,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                Blur(1, 0, 4f, 1, 0),
                Blur(2, 1, 4f, 0, 1),
                Levels(3, 2, 0.1f, 0.9f, 0.8f, 1f)
            ],
            Outputs = [3]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        var path = Path.Combine(Path.GetTempPath(), $"vixen-texture-graph-{Guid.NewGuid():N}.png");

        try {
            bake.Save(3, path);

            var written = PngCodec.Load(path);

            Assert.Equal(Side, written.Width);
            Assert.Equal(Side, written.Height);
            Assert.Equal(At(bake.Read(3), 40, 20, 0), At(written, 40, 20, 0));

            output.WriteLine($"adapter: {Adapter(device)}; wrote {new FileInfo(path).Length} bytes to {path}");
        } finally {
            File.Delete(path);
        }

        device.Destroy(staging);
        device.Destroy(source);
    }

    /// <summary>
    ///     Doc 48 § M0's one question: what a forty-op evaluation costs at 1K, 2K and 4K.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A measurement rather than a budget.</b> The only assertion is an absurd ceiling that
    ///         is a hang check and not a bound — a wall-clock budget calibrated on one machine is this
    ///         repository's largest flake source, and the number this exists to produce is one a person
    ///         reads out of the output, attributed to the adapter that produced it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pool's high-water mark is asserted, and it is the half that is not a
    ///         measurement.</b> Forty ops at 4K allocating forty textures is 2.6 GB, which does not
    ///         fail — it swaps, or it is refused by the allocator on somebody else's machine.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_forty_op_evaluation_is_measured_at_one_two_and_four_K() {
        using var device = Open();

        output.WriteLine($"adapter: {Adapter(device)}");

        using var evaluator = new TexturePlanEvaluator(device);

        // ⚠ The kernel is compiled outside the clock, on a plan too small to measure. Leaving it in
        // would put a Raven front-end run and a pipeline creation inside the first resolution's
        // number and nowhere else, which is how a measurement comes out saying that 1K costs more per
        // op than 2K does.
        var (warm, warmStaging) = Upload(device, Ramp(8), 8);

        using (var _ = evaluator.Evaluate(
            new TexturePlan {
                BaseWidth = 8,
                BaseHeight = 8,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                Ops = [Blur(1, 0, 1f, 1, 0)],
                Outputs = [1]
            },
            new Dictionary<int, TextureHandle> { [0] = warm }
        )) {
            Assert.Equal(1, evaluator.Compilations);
        }

        device.Destroy(warmStaging);
        device.Destroy(warm);

        foreach (var side in (int[])[1024, 2048, 4096]) {
            var (source, staging) = Upload(device, Ramp(64), 64);

            var images = ImmutableArray.CreateBuilder<TextureImage>();
            var ops = ImmutableArray.CreateBuilder<TextureOp>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var index = 0; index < 40; index++) {
                images.Add(new(TextureFormat.Rgba8));

                ops.Add(
                    index % 2 == 0
                        ? Blur(index + 1, index, 4f, 1, 0)
                        : Blur(index + 1, index, 4f, 0, 1)
                );
            }

            var plan = new TexturePlan {
                BaseWidth = side,
                BaseHeight = side,
                Images = images.ToImmutable(),
                Ops = ops.ToImmutable(),
                Outputs = [40]
            };

            Assert.Empty(plan.Validate());

            var schedule = TexturePoolSchedule.For(plan);

            Assert.Equal(2, schedule.Allocations);

            var clock = Stopwatch.StartNew();

            using (var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source })) {
                clock.Stop();

                Assert.Equal(40, bake.Dispatches);
            }

            output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{side}²: 40 ops in {clock.Elapsed.TotalMilliseconds:F1} ms "
                    + $"({clock.Elapsed.TotalMilliseconds / 40:F2} ms/op), pool {schedule.Allocations} textures / "
                    + $"{schedule.Bytes / (1024 * 1024)} MB"
                )
            );

            // A hang check and not a bound. Two minutes for forty dispatches is not a slow machine.
            Assert.True(clock.Elapsed.TotalSeconds < 120, $"a 40-op {side}² evaluation took {clock.Elapsed}");

            device.Destroy(staging);
            device.Destroy(source);
        }

        // Three resolutions of one kernel writing one format: three variants would be a cache that
        // does not work, and forty would be no cache at all.
        Assert.Equal(1, evaluator.Compilations);
    }

    /// <summary>An unsound plan is refused before anything is created.</summary>
    [Fact]
    public void An_unsound_plan_is_refused_rather_than_dispatched() {
        using var device = Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.R8)],
            Ops = [Levels(1, 0, 0f, 1f)],
            Outputs = [1]
        };

        var failure = Assert.Throws<ArgumentException>(() => evaluator.Evaluate(plan));

        Assert.Contains("storage image", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, evaluator.Dispatches);
    }

    /// <summary>A plan whose op forgets a parameter is refused rather than given a zero.</summary>
    [Fact]
    public void A_parameter_the_kernel_declares_and_the_op_omits_is_refused() {
        using var device = Open();

        var (source, staging) = Upload(device, Ramp(Side), Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Levels",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("inputBlack", 0f)]
                }
            ],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);

        var failure = Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source })
        );

        Assert.Contains("does not carry it", failure.Message, StringComparison.Ordinal);

        device.Destroy(staging);
        device.Destroy(source);
    }
}
