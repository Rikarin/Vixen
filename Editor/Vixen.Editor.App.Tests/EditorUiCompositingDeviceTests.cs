// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The editor's own shader table, asked to blur, filter and mask a group on a real device.</summary>
/// <remarks>
///     <para>
///         <b>The gap this closes: the editor supplied five of the eight stages and nothing said
///         so.</b> <c>EditorHost</c> built its table by hand from a second copy of <c>Ui.rvn</c> under
///         <c>Editor/Vixen.Editor.Host/Shaders</c>, and that copy declared <c>UiVertex</c>,
///         <c>UiBox</c>, <c>UiText</c>, <c>UiSolid</c> and <c>UiImage</c> — not <c>UiBlur</c>,
///         <c>UiColour</c> or <c>UiMask</c>. So in the one application whose stylesheets are this
///         repository's own, <c>filter: blur()</c> drew sharp, <c>filter: grayscale(1)</c> drew in
///         full colour and <c>mask-image</c> did nothing.
///     </para>
///     <para>
///         ⚠ <b>Not one of the three is a failure, which is why it survived a year.</b>
///         <see cref="UiShaders.Blur" />, <see cref="UiShaders.Colour" /> and
///         <see cref="UiShaders.Mask" /> are documented as degrading to a picture: the group is still
///         composited through <see cref="UiShaders.Image" />, the opacities are still right, and the
///         frame that comes out is a perfectly plausible one. No validation error, no log line, no
///         counter out of range — the editor drew a correct picture of the wrong stylesheet.
///     </para>
///     <para>
///         ⚠ <b>So the sabotage is in the test rather than in a commit somebody has to remember to
///         make.</b> Every assertion below is made twice: once against
///         <see cref="UiShaderLibrary.Load" />, which is what <c>EditorHost</c> now hands its
///         renderers, and once against that table with the three optional stages cleared — which
///         reconstructs the editor exactly as it was. The second half is what says the oracles can
///         be false, and it is the half that would have been green before this change.
///     </para>
///     <para>
///         ⚠ <b>Three oracles rather than a reference image, because each is closed form.</b> A
///         Gaussian turns a step edge into a ramp, so a blurred edge has intermediate levels along it
///         and a sharp one has none. A full grayscale matrix makes the three channels equal, and a
///         saturated red's are as far apart as they go. A linear mask ramp from one to zero makes the
///         left of a box brighter than its right, and an unmasked box is flat. None of the three needs
///         a committed picture and none can be satisfied by a frame that drew nothing — the sharp,
///         unfiltered, unmasked arm asserts the opposite of each.
///     </para>
///     <para>
///         ⚠ <b>Skips when there is no Vulkan, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into
///         a failure</b> — <see cref="ThumbnailSurfaceDeviceTests" />'s rule, for its reason.
///     </para>
/// </remarks>
public sealed class EditorUiCompositingDeviceTests {
    /// <summary>The side of the square frame every case below renders.</summary>
    /// <remarks>
    ///     Big enough that a nine-pixel outset at σ=3 lands inside it with room either side, and small
    ///     enough that a readback is a quarter of a megabyte.
    /// </remarks>
    const int Side = 128;

    /// <summary>The sigma of the group blur, in pixels.</summary>
    /// <remarks>
    ///     ⚠ Three, not thirty. <c>UiLayer.KernelRadius</c> is three sigma, so this is a nine-pixel
    ///     outset — wide enough that the ramp below is unmistakably a ramp, and short of the
    ///     truncation at <c>UiLayer.MaximumKernel</c>.
    /// </remarks>
    const float Sigma = 3f;

    /// <summary>What the frame is cleared to, and what "not the box" means to every oracle below.</summary>
    static readonly Color4 Background = new(0.05f, 0.05f, 0.05f, 1f);

    /// <summary>Where the pictures go, or null when nobody asked for any.</summary>
    /// <remarks>
    ///     ⚠ <b>Off unless it is asked for, by <c>VIXEN_UI_CAPTURE=&lt;directory&gt;</c></b> —
    ///     <c>ComposedPaneCaptureTests</c>'s convention, for its reason: writing files does not
    ///     belong in a suite that runs on every push, and a picture is what somebody looking at a
    ///     blurred panel actually wants. The assertions do not depend on it.
    /// </remarks>
    static string? Destination => Environment.GetEnvironmentVariable("VIXEN_UI_CAPTURE");

    /// <summary>A device, or a skip — or, when one was required, a failure.</summary>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>What a rendered frame is worth asserting about.</summary>
    /// <param name="Pixels">RGBA8, row major, <see cref="Side" /> square.</param>
    /// <param name="Blurred">How many composites the renderer convolved.</param>
    /// <param name="Filtered">How many it put a colour matrix through.</param>
    /// <param name="Masked">How many it put a mask through.</param>
    record struct Frame(byte[] Pixels, int Blurred, int Filtered, int Masked) {
        public byte Channel(int x, int y, int channel) => Pixels[(((y * Side) + x) * 4) + channel];

        /// <summary>The green channel, which every box below is bright in and the ground is dark in.</summary>
        public byte Level(int x, int y) => Channel(x, y, 1);
    }

    /// <summary>
    ///     One blurred group, one grayscale group and one masked group, over a dark ground.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Three groups rather than one with all three effects, because a group with a mask and a
    ///     matrix is served by one module.</b> <c>UiMask</c> carries the colour matrix too, so a
    ///     single group would say nothing about <see cref="UiShaders.Colour" /> — that stage is the
    ///     one an unmasked filtered group takes, and it is the arm every <c>filter:</c> in a
    ///     stylesheet without a <c>mask-image</c> beside it goes through.
    /// </remarks>
    static UiGeometry Geometry(GlyphFieldCache glyphs) {
        var list = new DrawList();
        list.BeginFrame();

        // Blurred: a white box whose left edge is the step the Gaussian has to turn into a ramp.
        Push(list, 24, 16, 48, 32, blur: Sigma);
        list.Add(new(DrawCommandKind.Rectangle, 24, 16, 48, 32, Color4.White, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));

        // Filtered: a saturated red box, whose three channels a full grayscale has to equalise.
        Push(list, 24, 56, 48, 24, filter: UiColorMatrix.Grayscale(1f));
        list.Add(new(DrawCommandKind.Rectangle, 24, 56, 48, 24, new Color4(1f, 0f, 0f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));

        // Masked: a white box under a ramp that runs from opaque on the left to clear on the right.
        Push(
            list,
            24,
            88,
            80,
            24,
            mask: [
                new UiMask(
                    new Vector2(24f + 40f, 88f + 12f),
                    new Vector2(40f, 12f),
                    new Vector2(1f, 0f),
                    new Vector3(1f, 0f, 0f),
                    GradientStops.Default,
                    GradientShape.Linear,
                    Via: false
                )
            ]
        );

        list.Add(new(DrawCommandKind.Rectangle, 24, 88, 80, 24, Color4.White, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));

        list.EndFrame();

        return new UiGeometryBuilder().Build(list, glyphs, new Rectangle(0, 0, Side, Side));
    }

    /// <summary>Opens a group carrying one effect and nothing else.</summary>
    static void Push(
        DrawList list,
        float x,
        float y,
        float width,
        float height,
        float blur = 0f,
        UiColorMatrix? filter = null,
        ReadOnlySpan<UiMask> mask = default
    ) =>
        list.Add(
            new Vixen.Ui.DrawCommand(DrawCommandKind.LayerPush, x, y, width, height, Color4.White, 0, 0) {
                Blur = blur,
                Filter = filter,
                Offset = mask.Length > 0 ? list.AddMasks(mask) : 0,
                Length = mask.Length
            }
        );

    /// <summary>Renders <see cref="Geometry" /> through one shader table and reads the pixels back.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Compose</c> after <c>Upload</c> and before the pass, which is the order
    ///     <c>UiWindowSurface</c> uses.</b> A group's pass draws from the buffers <c>Upload</c> writes
    ///     and through the descriptor set it rebinds for this frame, so composing first renders every
    ///     group from the previous frame's geometry — and with one frame that is a group rendered
    ///     from nothing.
    /// </remarks>
    static Frame Render(VulkanDevice device, UiShaders shaders, string? picture = null) {
        using var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Rgba8UNorm]));

        var glyphs = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = Geometry(glyphs);

        // The instrument, before the measurement: a frame that opened no group is one where every
        // oracle below is a comparison between two flat walks.
        Assert.Equal(3, geometry.Layers.Count);

        var target = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                Side,
                Side,
                TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
                Name: "editor ui composite"
            )
        );

        var view = device.CreateTextureView(target);
        var bytes = Side * Side * 4;
        var readback = device.CreateBuffer(new(bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback"));

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "editor ui composite")) {
            renderer.Upload(commands, geometry, glyphs.Atlas);
            renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(Background));

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(target, ResourceState.Undefined, ResourceState.ColourTarget)])
            );

            commands.BeginRenderPass(
                new(
                    [new ColourAttachment(view, LoadAction.Clear, StoreAction.Store, Background)],
                    name: "editor ui composite"
                )
            );

            renderer.Record(commands, geometry, new Int2(Side, Side));

            commands.EndRenderPass();

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(target, ResourceState.ColourTarget, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(target), new(Side, Side, 1), readback, 0);

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[bytes];

        device.Read(readback, 0, pixels);

        device.Destroy(readback);
        device.Destroy(view);
        device.Destroy(target);

        if (picture is { Length: > 0 } && Destination is { Length: > 0 } directory) {
            PngCodec.Save(Path.Combine(directory, picture), new Bitmap(Side, Side, pixels));
        }

        return new Frame(pixels, renderer.Blurred, renderer.Filtered, renderer.Masked);
    }

    /// <summary>The editor's table, and the same table with the three optional stages taken away.</summary>
    /// <remarks>
    ///     ⚠ Cleared rather than replaced: <c>default</c> is the value <see cref="UiShaders" />'s init
    ///     properties hold when a host never sets them, which is precisely the table
    ///     <c>EditorHost</c> built by hand.
    /// </remarks>
    static UiShaders WithoutTheOptionalStages(UiShaders shaders) =>
        shaders with { Blur = default, Colour = default, Mask = default };

    /// <summary>How many distinct levels sit strictly between the ground and the box along a row.</summary>
    /// <remarks>
    ///     The oracle for a Gaussian: a step edge convolved with one is a ramp, and a step edge that
    ///     was not is two levels with nothing in between. Counted over a window that spans the edge
    ///     and three sigma either side of it.
    /// </remarks>
    static int RampWidth(Frame frame, int y, int from, int to) {
        var ramp = 0;

        for (var x = from; x < to; x++) {
            var level = frame.Level(x, y);

            if (level > 24 && level < 232) {
                ramp++;
            }
        }

        return ramp;
    }

    /// <summary>The editor's table blurs a group, and the table it used to build does not.</summary>
    [Fact]
    public void A_blurred_group_is_soft_edged_and_the_old_five_module_table_draws_it_sharp() {
        using var device = Open();

        VulkanDiagnostics.Reset();

        var shaders = UiShaderLibrary.Load(device);

        var blurred = Render(device, shaders, "editor-ui-composited.png");
        var sharp = Render(device, WithoutTheOptionalStages(shaders), "editor-ui-five-modules.png");

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "the frames produced validation errors, so their pixels mean nothing: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        // The instrument. Every one of `Blurred`'s ways of not happening — no stage, no `UiLayer.Blur`,
        // a kernel radius that came out zero — leaves a correct sharp picture behind.
        Assert.Equal(1, blurred.Blurred);
        Assert.Equal(0, sharp.Blurred);

        // The left edge of the box is at x = 24 and the row is through its middle. Nine pixels of
        // outset either side, so the window is wide enough to hold the whole ramp.
        var soft = RampWidth(blurred, 32, 12, 38);
        var hard = RampWidth(sharp, 32, 12, 38);

        Assert.True(
            soft >= 8,
            $"a σ={Sigma} blur left only {soft} intermediate levels across the box's edge, which is not a ramp."
        );

        Assert.True(
            hard <= 2,
            $"the table without a blur stage produced {hard} intermediate levels across the edge, so the "
            + "sabotage arm is not sharp and the assertion above proves nothing."
        );
    }

    /// <summary>The editor's table applies a colour matrix, and the table it used to build does not.</summary>
    [Fact]
    public void A_filtered_group_is_grey_and_the_old_five_module_table_draws_it_red() {
        using var device = Open();

        VulkanDiagnostics.Reset();

        var shaders = UiShaderLibrary.Load(device);

        var filtered = Render(device, shaders);
        var unfiltered = Render(device, WithoutTheOptionalStages(shaders));

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "the frames produced validation errors, so their pixels mean nothing: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        Assert.Equal(1, filtered.Filtered);
        Assert.Equal(0, unfiltered.Filtered);

        // The middle of the red box.
        const int X = 48;
        const int Y = 68;

        var red = filtered.Channel(X, Y, 0);
        var green = filtered.Channel(X, Y, 1);
        var blue = filtered.Channel(X, Y, 2);

        Assert.True(
            Math.Abs(red - green) <= 2 && Math.Abs(green - blue) <= 2,
            $"a full grayscale left the channels at ({red}, {green}, {blue}), which is not grey."
        );

        // ⚠ And the box is still *there*. Three equal channels is also what a group that composited
        // nothing would leave, since the ground is grey too — so the level has to be the filtered
        // red's luminance rather than the background's.
        Assert.True(green > 32, $"the filtered box came out at {green}, which is the dark ground rather than a box.");

        var wasRed = unfiltered.Channel(X, Y, 0);
        var wasGreen = unfiltered.Channel(X, Y, 1);

        Assert.True(
            wasRed - wasGreen > 128,
            $"the table without a colour stage drew ({wasRed}, {wasGreen}), so it is not the red this "
            + "test claims it leaves and the assertion above proves nothing."
        );
    }

    /// <summary>The editor's table applies a mask, and the table it used to build does not.</summary>
    [Fact]
    public void A_masked_group_fades_out_and_the_old_five_module_table_draws_it_flat() {
        using var device = Open();

        VulkanDiagnostics.Reset();

        var shaders = UiShaderLibrary.Load(device);

        var masked = Render(device, shaders);
        var flat = Render(device, WithoutTheOptionalStages(shaders));

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "the frames produced validation errors, so their pixels mean nothing: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        Assert.Equal(1, masked.Masked);
        Assert.Equal(0, flat.Masked);

        // Inside the box, one near each end of the ramp, and both well clear of its edges.
        const int Y = 100;
        const int Near = 30;
        const int Far = 98;

        Assert.True(
            masked.Level(Near, Y) - masked.Level(Far, Y) > 128,
            $"the mask left {masked.Level(Near, Y)} at the opaque end and {masked.Level(Far, Y)} at the "
            + "clear one, which is not a ramp from one to zero."
        );

        Assert.True(
            Math.Abs(flat.Level(Near, Y) - flat.Level(Far, Y)) <= 2,
            $"the table without a mask stage drew {flat.Level(Near, Y)} and {flat.Level(Far, Y)}, so it is "
            + "not the flat box this test claims it leaves and the assertion above proves nothing."
        );
    }
}
