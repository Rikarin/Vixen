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
    static (TextureHandle Texture, BufferHandle Staging) Upload(VulkanDevice device, byte[] pixels, int side) {
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, side, side, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "source")
        );

        var staging = device.CreateBuffer(
            new(pixels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "source staging")
        );

        device.Write(staging, 0, pixels);
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "upload")) {
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
            device.GraphicsQueue.Submit([commands]);
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
    ///     <b>Two plans that differ only in the base resolution, writing images of exactly the same
    ///     size.</b> The first has a base of 64 and writes at level 0; the second has a base of 128
    ///     and writes at level 1 — 64 texels either way, reading the same source, dispatching the same
    ///     number of groups. The only thing that differs is what "8 texels at the base" resolves to,
    ///     and the picture is what says whether the evaluator applied it. A radius that reached the
    ///     kernel unscaled would make these two bars identical, which is a perfectly plausible picture
    ///     and the reason nobody notices this bug for two years.
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
                // A flat half-grey, made by collapsing the ramp's input range onto one value.
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                Levels(1, 0, 0.5f, 0.5f),
                Mix(2, 0, 1, 1, 1f)
            ],
            Outputs = [2]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        var mask = bake.Read(1);
        var picture = bake.Read(2);

        // The degenerate levels is a step: black below 0.5, white at and above it. So the multiply is
        // the ramp masked off on its left half and untouched on its right.
        Assert.Equal(0, At(mask, 8, 8, 0));
        Assert.Equal(255, At(mask, 56, 8, 0));

        Assert.Equal(0, At(picture, 8, 8, 0));
        Assert.InRange(At(picture, 56, 8, 0), At(mask, 56, 8, 0) - 250 + 220, 255);
        Assert.InRange(At(picture, 56, 8, 0), 220, 232);

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
