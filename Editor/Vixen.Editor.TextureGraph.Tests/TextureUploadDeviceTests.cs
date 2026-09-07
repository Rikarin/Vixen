// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary>That the texels a caller uploads are the texels a kernel reads, on a real device.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 48 § 4.1's <c>Text</c> and <c>Svg Path</c> arrive through.</b> Neither can
///         be a compute kernel — a kernel has no rasteriser and cannot reach a font or a path parser
///         — so both are filled on the CPU and enter the plan as an external image, which is what
///         <see cref="TextureUploads" /> makes. <a href="https://github.com/Rikarin/Vixen/issues/687">#687</a>
///         named this as the missing step, and this file is the proof that it is not lossy.
///     </para>
///     <para>
///         ⚠ <b>Every test here names its adapter and skips loudly rather than passing without
///         one.</b> Without a real device a headless run falls back to the Null device, exits 0 and
///         prints identical healthy counters — and a round trip asserted there would have proved that
///         a black image equals a black image, because <c>NullDevice</c> reads back zeroes whatever
///         was written. <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure.
///     </para>
///     <para>
///         ⚠ <b>The kernel is <c>Invert</c> with all four switches off, which is an exact copy.</b>
///         A copy is what makes the assertion an equality over every texel rather than a tolerance:
///         an eight-bit unorm survives the round trip through a float and back exactly, so anything
///         that resamples, offsets or drops a channel between the staging buffer and the storage
///         image shows on the first texel it touches. The pattern is
///         <c>TextureKernelHarness.Unique</c>, where no two texels are alike — a flat fill is the
///         picture a broken upload also produces.
///     </para>
/// </remarks>
public class TextureUploadDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>An op that copies its input, channel for channel.</summary>
    static TextureOp Copy(int output, int input) =>
        new() {
            Kernel = "Invert",
            Output = output,
            Inputs = [input],
            Parameters = [new("invertR", 0f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
        };

    static TexturePlan Plan(TextureFormat source) =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(source, External: true), new(TextureFormat.Rgba8)],
            Ops = [Copy(1, 0)],
            Outputs = [1]
        };

    /// <summary>⚠ An uploaded picture reaches a kernel texel for texel and channel for channel.</summary>
    [Fact]
    public void An_uploaded_picture_is_what_the_kernel_reads() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"upload round trip on {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var plan = Plan(TextureFormat.Rgba8);

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Side, Side, source);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            bake.Read(1),
            4,
            $"uploaded picture on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>⚠ A single-channel mask uploads, and a kernel reads it as <c>(r, 0, 0, 1)</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two claims at once, and both are load-bearing for doc 48 § 4.1's grey sources.</b>
    ///         The first is that <see cref="TextureFormat.R8" /> is uploadable at all, although
    ///         <see cref="TextureFormats.IsStorable" /> is false for it — that predicate is about what
    ///         a kernel may *write*, and a mask is read. The second is the shape a sampled read of one
    ///         has: red carries the mask and green, blue and alpha are the constants the target fills
    ///         in, so a graph that wants the mask in all three channels needs a splat and does not get
    ///         one for free.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ramp is what makes this an equality rather than a shape check.</b> Sixty-four
    ///         distinct levels across the image: an upload that read the buffer with the wrong stride
    ///         — four bytes a texel is the natural mistake, because that is what every other picture
    ///         in these suites is — comes back as a ramp four times as steep, wrapping four times
    ///         across the row, and every texel but the first disagrees.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_single_channel_mask_uploads_and_is_read_as_red_alone() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"R8 upload on {TextureKernelHarness.Adapter(device)}");

        Assert.False(TextureFormats.IsStorable(TextureFormat.R8));

        var mask = new byte[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                mask[(y * Side) + x] = (byte)(x * 4);
            }
        }

        var plan = Plan(TextureFormat.R8);

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Side, Side, mask);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(1);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(mask[(y * Side) + x], TextureKernelHarness.At(picture, x, y, 0));
                Assert.Equal(0, TextureKernelHarness.At(picture, x, y, 1));
                Assert.Equal(0, TextureKernelHarness.At(picture, x, y, 2));
                Assert.Equal(255, TextureKernelHarness.At(picture, x, y, 3));
            }
        }
    }

    /// <summary>⚠ A coverage field arrives as the level it rounds to, end to end.</summary>
    /// <remarks>
    ///     <b>The whole path a rasterised glyph or a filled path takes.</b>
    ///     <c>GlyphRasterizer.Rasterize</c> hands back exactly this — a <c>float[]</c>, row-major, row
    ///     0 at the top, each value in <c>[0, 1]</c> — so what is asserted here is that a coverage of
    ///     one half is 128 in the picture and not 127. Half a step, uniformly, on every anti-aliased
    ///     edge in a shape: it looks like a font weight rather than like arithmetic, which is why the
    ///     rounding is asserted at both ends of the seam.
    /// </remarks>
    [Fact]
    public void A_coverage_field_arrives_as_the_level_it_rounds_to() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"coverage upload on {TextureKernelHarness.Adapter(device)}");

        var coverage = new float[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                coverage[(y * Side) + x] = x / (float)(Side - 1);
            }
        }

        // The three the rounding is actually about: nothing, exactly half, and everything.
        coverage[0] = 0f;
        coverage[1] = 0.5f;
        coverage[2] = 1f;

        var plan = Plan(TextureFormat.R8);

        using var uploads = new TextureUploads(device);

        uploads.AddCoverage(plan, 0, Side, Side, coverage);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(1);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                // ⚠ Written out rather than calling `TextureUploads.Quantize`, which is what
                // produced the bytes: an assertion against the code under test is an assertion that
                // cannot fail, and this loop is 4 096 texels of exactly that shape.
                Assert.Equal(
                    (byte)((coverage[(y * Side) + x] * 255f) + 0.5f),
                    TextureKernelHarness.At(picture, x, y, 0)
                );
            }
        }

        Assert.Equal(0, TextureKernelHarness.At(picture, 0, 0, 0));
        Assert.Equal(128, TextureKernelHarness.At(picture, 1, 0, 0));
        Assert.Equal(255, TextureKernelHarness.At(picture, 2, 0, 0));
    }

    /// <summary>A picture that is not the plan's base resolution is read at its own size.</summary>
    /// <remarks>
    ///     ⚠ <b>An external image is the one place an absolute size enters a plan, and this is what
    ///     that means in practice.</b> The plan's base is 64² and the upload is 16 wide; every kernel
    ///     clamps its taps to the <em>source's</em> dimensions, so the right-hand three quarters of
    ///     the output repeat the source's last column rather than reading outside it. A kernel that
    ///     clamped to the target's dimensions instead — the recurring mistake in this folder — would
    ///     sample far outside a 16-wide texture, and what a Vulkan implementation returns there is not
    ///     the edge.
    /// </remarks>
    [Fact]
    public void An_upload_smaller_than_the_plan_is_clamped_to_its_own_edge() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"undersized upload on {TextureKernelHarness.Adapter(device)}");

        const int Narrow = 16;

        var source = TextureKernelHarness.Ramp(Narrow);
        var plan = Plan(TextureFormat.Rgba8);

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Narrow, Narrow, source);

        Assert.Equal(new Int2(Narrow, Narrow), uploads.SizeOf(0));

        // ⚠ And the plan refuses to give a second answer — #715, #1008. It used to return the
        // nominal 64×64 here, a number no picture produced, read off the level of an image nothing
        // allocates; that answer had one caller and it was `OnCpu`'s read-back (#1000).
        var nominal = Assert.Throws<ArgumentException>(() => plan.SizeOf(0));

        Assert.Contains("external", nominal.Message, StringComparison.Ordinal);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        // ⚠ And the bake says so — #632. This is the picture that issue describes and the guard
        // written for it (#801) could not see: `TexturePlan.Check` skips an external input because
        // the plan has no size for one, so a pointwise kernel over an undersized import was silent
        // everywhere. The declared size is what makes the claim measurable, and it only exists at
        // the evaluation.
        var caution = Assert.Single(bake.Warnings);

        Assert.Contains("Op 0", caution, StringComparison.Ordinal);
        Assert.Contains("Invert", caution, StringComparison.Ordinal);
        Assert.Contains($"{Side}×{Side}", caution, StringComparison.Ordinal);
        Assert.Contains($"{Narrow}×{Narrow}", caution, StringComparison.Ordinal);

        // It is a report and not a refusal: the bake happened, and the picture below is what it drew.
        Assert.Empty(plan.Validate());

        var picture = bake.Read(1);

        // The first sixteen columns are the ramp itself; everything past them is its last column.
        for (var x = 0; x < Narrow; x++) {
            Assert.Equal(source[x * 4], TextureKernelHarness.At(picture, x, 3, 0));
        }

        for (var x = Narrow; x < Side; x++) {
            Assert.Equal(255, TextureKernelHarness.At(picture, x, 3, 0));
        }
    }

    /// <summary>And an op that means to read another extent is silent about the same upload.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that stops the caution being "every import that is not the graph's size is a
    ///     warning".</b> <c>Resample</c>, <c>Crop</c>, <c>Tile</c>, <c>Transform2D</c> and
    ///     <c>Bitmap</c> all read their source's extent on purpose and say so on the op, and the
    ///     evaluator's guard asks <c>TexturePlan.Declared</c> — the same predicate the plan's own
    ///     guard asks — rather than re-spelling it. It is the same plan, the same upload and the same
    ///     kernel as
    ///     <see cref="An_upload_smaller_than_the_plan_is_clamped_to_its_own_edge" />; only the
    ///     declaration differs.
    /// </remarks>
    [Fact]
    public void An_op_that_declares_the_difference_is_not_cautioned_about_the_upload() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"declared extent difference on {TextureKernelHarness.Adapter(device)}");

        const int Narrow = 16;

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [Copy(1, 0) with { ReadsOtherExtents = true }],
            Outputs = [1]
        };

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Narrow, Narrow, TextureKernelHarness.Ramp(Narrow));

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        Assert.Empty(bake.Warnings);
    }

    /// <summary>⚠ A CPU op reading an undersized upload is handed that picture, not the plan's size.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1000">#1000</a>, and the fixture
    ///         is the whole finding.</b> Every other CPU-op case in this assembly uploads at its
    ///         plan's own resolution, where the plan's nominal answer for an external image happens
    ///         to be right — so eight green tests covered a read-back sized from a number no picture
    ///         had. Sixteen texels in a sixty-four-texel plan is the case none of them varied.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the old code did was read memory nobody wrote.</b>
    ///         <c>CopyTextureToBuffer</c> was issued with a 64×64 extent against a 16×16 texture into
    ///         a 16 KB buffer, of which at most 1 KB can have been filled, and the operation was
    ///         handed all of it as picture data — <c>NormalToHeight</c> integrates over exactly that.
    ///         A copy larger than the image is undefined rather than invalid, so MoltenVK raised
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         <b>The bytes are asserted as well as the size</b>, because a size assertion alone
    ///         would pass a read-back that reported 16×16 and copied the wrong region: the ramp is
    ///         unique per texel, so this is 1 024 independent claims that what arrived is what went
    ///         up.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_cpu_op_reading_an_upload_smaller_than_the_plan_is_handed_the_uploads_own_size() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"undersized upload into a cpu op on {TextureKernelHarness.Adapter(device)}");

        const int Narrow = 16;

        var source = TextureKernelHarness.Ramp(Narrow);
        var probe = new RecordTheInput();

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Record", Output = 1, Inputs = [0], Cpu = probe }],
            Outputs = [1]
        };

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Narrow, Narrow, source);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        Assert.Equal(new Int2(Narrow, Narrow), probe.Size);
        Assert.Equal(source, probe.Bytes);

        // And the operation's own output is still the plan's size, which is what it writes into.
        Assert.Equal(new Int2(Side, Side), probe.Wrote);
    }

    /// <summary>An external image a CPU op reads whose size the caller did not declare is refused.</summary>
    /// <remarks>
    ///     ⚠ <b>A refusal rather than a fall-back, and it can be one because the path that cannot
    ///     answer cannot get here.</b> The bare-handle overload of <c>Evaluate</c> declares
    ///     <see cref="TextureUsage.Sampled" /> alone, so a plan whose CPU op reads an external image
    ///     is already refused for the usage before any of this — a caller reaching the size check has
    ///     spelled a <see cref="TextureExternal" /> out, and the size is the same sentence. A default
    ///     meaning "use the plan's number" would have been
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1000">#1000</a> kept alive behind a field
    ///     that looks like a fix.
    /// </remarks>
    [Fact]
    public void A_cpu_op_over_an_external_of_undeclared_size_is_refused() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"undeclared external size on {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Record", Output = 1, Inputs = [0], Cpu = new RecordTheInput() }],
            Outputs = [1]
        };

        using var uploads = new TextureUploads(device);

        var texture = uploads.Add(plan, 0, Side, Side, TextureKernelHarness.Unique(Side));

        using var evaluator = new TexturePlanEvaluator(device);

        var refusal = Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(
                plan,
                new Dictionary<int, TextureExternal> { [0] = new(texture, TextureUploads.UploadUsage) }
            )
        );

        Assert.Contains("declares no size", refusal.Message, StringComparison.Ordinal);

        // The instrument: the same plan and the same texture run the moment the size is declared.
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        Assert.Equal(0, bake.Dispatches);
    }
}

/// <summary>A CPU operation that remembers the picture it was handed and writes nothing.</summary>
/// <remarks>
///     ⚠ <b>It asserts nothing itself.</b> An operation that threw inside <c>Run</c> would surface as
///     whatever the evaluator does with an exception mid-bake rather than as the claim under test, so
///     what it saw is recorded and read back outside.
/// </remarks>
sealed class RecordTheInput : ITextureCpuOperation {
    /// <inheritdoc />
    public string Name => "Record";

    /// <summary>How big the first input was.</summary>
    public Int2 Size { get; private set; }

    /// <summary>Its texels, exactly as they arrived.</summary>
    public byte[] Bytes { get; private set; } = [];

    /// <summary>How big the image it was asked to fill was.</summary>
    public Int2 Wrote { get; private set; }

    /// <inheritdoc />
    public void Run(in TextureCpuInvocation invocation) {
        var source = invocation.Inputs[0];

        Size = new(source.Width, source.Height);
        Bytes = [.. source.Bytes];
        Wrote = new(invocation.Output.Width, invocation.Output.Height);
    }
}
