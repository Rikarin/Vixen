// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Shaders.Generated;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A thumbnail, uploaded on a real device, asserted to be the picture that went in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A thumbnail that uploaded nothing is indistinguishable from one that has not been
///         decoded yet.</b> The grid draws a type glyph either way, the process exits zero, and there
///         is no validation error because nothing was submitted to validate — which is how
///         <c>ThumbnailSurface.Upload</c> came to record a barrier, a copy and a second barrier into a
///         command list it then dropped on the floor. <c>VulkanCommandList.Dispose</c> returns
///         nothing, so the work was discarded rather than deferred. Every structural claim about that
///         upload was true.
///     </para>
///     <para>
///         ⚠ <b>So what is asserted here is the bytes.</b> How many distinct colours came back, what
///         the mean channel is, and — separately, because a vertically flipped image passes both of
///         the others exactly — which corner is which.
///     </para>
///     <para>
///         ⚠ <b>Skips when there is no Vulkan, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a
///         failure.</b> A gate that silently skips is a gate that passes, which is what makes a device
///         test worth less than nothing on a machine where nobody reads the skip count.
///     </para>
/// </remarks>
public sealed class ThumbnailSurfaceDeviceTests {
    /// <summary>How big the picture under test is.</summary>
    /// <remarks>The size <c>ThumbnailCache</c> reduces to, so this is the shape a real upload has.</remarks>
    const int Size = ThumbnailCache.Size;

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

    /// <summary>The interface's own shader set, read from beside the test binary.</summary>
    /// <remarks>
    ///     A <see cref="ThumbnailSurface" /> needs a <see cref="UiRenderer" /> because an image number
    ///     is only meaningful against one, and a renderer on a real device needs real SPIR-V — the
    ///     four bytes the recording device accepts are what <c>vkCreateShaderModule</c> refuses.
    /// </remarks>
    static UiShaders Shaders(IGraphicsDevice device) =>
        new(
            Load(device, ShaderStage.Vertex, "UiVertex.vert.spv"),
            Load(device, ShaderStage.Fragment, "UiBox.frag.spv"),
            Load(device, ShaderStage.Fragment, "UiText.frag.spv"),
            Load(device, ShaderStage.Fragment, "UiSolid.frag.spv")
        ) {
            Image = Load(device, ShaderStage.Fragment, "UiImage.frag.spv"),

            Locations = new(
                UiVertexKeys.PositionLocation,
                UiVertexKeys.TexcoordLocation,
                UiVertexKeys.VertexColourLocation,
                UiVertexKeys.VertexShapeLocation
            )
        };

    static ShaderHandle Load(IGraphicsDevice device, ShaderStage stage, string name) {
        var path = Path.Combine(AppContext.BaseDirectory, "ToolShaders", name);

        Assert.True(File.Exists(path), $"the upload test needs {name} beside the test binary");

        return device.CreateShader(stage, File.ReadAllBytes(path), name);
    }

    /// <summary>The picture that goes in: red ramps left to right, green top to bottom, blue is full.</summary>
    /// <param name="flip">
    ///     Writes the rows bottom-first, which is the sabotage: it changes neither the colour count
    ///     nor the mean, and only the corner assertions can see it.
    /// </param>
    static byte[] Gradient(bool flip = false) {
        var pixels = new byte[Size * Size * 4];

        for (var y = 0; y < Size; y++) {
            var row = flip ? Size - 1 - y : y;

            for (var x = 0; x < Size; x++) {
                var at = ((row * Size) + x) * 4;

                pixels[at] = (byte)(x * 255 / (Size - 1));
                pixels[at + 1] = (byte)(y * 255 / (Size - 1));
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>Uploads a picture through the surface and reads the texture back.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Upload</c> is called outside the frame and <c>Flush</c> inside it, which is exactly
    ///     where the editor calls each.</b> <c>ThumbnailCache.Pump</c> runs from the application's
    ///     update, before <c>EditorHost.Present</c> opens the frame; a test that uploaded inside the
    ///     frame would be testing an arrangement the editor does not have.
    /// </remarks>
    static byte[] Round(VulkanDevice device, ThumbnailSurface surface, byte[] source) {
        var image = surface.Upload(Size, Size, source);

        Assert.NotEqual(0ul, image);

        const int Bytes = Size * Size * 4;

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "thumbnail readback")
        );

        device.BeginFrame();

        Assert.Equal(1, surface.Flush());

        var texture = surface.TextureOf(image);

        Assert.True(texture.IsValid, "the surface has no texture for the image it just handed out");

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "thumbnail readback")) {
            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(texture), new(Size, Size, 1), readback, 0);

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.CopySource, ResourceState.ShaderRead)])
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[Bytes];

        device.Read(readback, 0, pixels);
        device.Destroy(readback);

        return pixels;
    }

    static int Distinct(byte[] pixels) {
        HashSet<int> seen = [];

        for (var at = 0; at + 3 < pixels.Length; at += 4) {
            seen.Add((pixels[at] << 16) | (pixels[at + 1] << 8) | pixels[at + 2]);
        }

        return seen.Count;
    }

    static double Mean(byte[] pixels) {
        long sum = 0;

        for (var at = 0; at + 3 < pixels.Length; at += 4) {
            sum += pixels[at] + pixels[at + 1] + pixels[at + 2];
        }

        return sum / (double)(pixels.Length / 4 * 3);
    }

    static byte Channel(byte[] pixels, int x, int y, int channel) => pixels[(((y * Size) + x) * 4) + channel];

    /// <summary>The uploaded texture holds the gradient, and holds it the right way up.</summary>
    [Fact]
    public void An_uploaded_thumbnail_is_the_picture_and_not_a_blank_square() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var shaders = Shaders(device);
            var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Bgra8UNorm]));

            using var surface = new ThumbnailSurface(device, renderer);

            var pixels = Round(device, surface, Gradient());

            Assert.Equal(1, surface.Submitted);
            Assert.Equal(0, surface.Waiting);

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                "the upload produced validation errors, so its picture means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );

            var distinct = Distinct(pixels);
            var mean = Mean(pixels);

            // Sixty-four reds by sixty-four greens is 4 096 colours. A thousand is far above what a
            // flat fill, a cleared texture or an undefined one produces, and far below this.
            Assert.True(distinct >= 1000, $"the thumbnail holds {distinct} distinct colour(s), which is not a gradient");

            // Red and green ramp 0…1 and blue is 1, so the mean channel is about (0.5 + 0.5 + 1) / 3
            // of 255 — near 170. A blank texture is 0 and a white one 255.
            Assert.InRange(mean, 140d, 200d);

            const int Last = Size - 1;

            // ⚠ The orientation, asserted separately because a vertically flipped picture has exactly
            // the same colour count and exactly the same mean. Green is the row, and row zero is the
            // top: a thumbnail's first row of bytes is its top row, which is the convention
            // `ThumbnailCache.Reduce` writes and `IThumbnailSurface.Upload` documents.
            Assert.True(
                Channel(pixels, 0, 0, 1) < 32,
                $"the top-left pixel's green is {Channel(pixels, 0, 0, 1)}, so the thumbnail is upside down"
            );

            Assert.True(
                Channel(pixels, 0, Last, 1) > 220,
                $"the bottom-left pixel's green is {Channel(pixels, 0, Last, 1)}, so the thumbnail is upside down"
            );

            // And the other axis, which no vertical flip would disturb: red is the column.
            Assert.True(Channel(pixels, 0, 0, 0) < 32);
            Assert.True(Channel(pixels, Last, 0, 0) > 220);
        }
    }

    /// <summary>A picture uploaded upside down passes the histogram and fails on the corners.</summary>
    /// <remarks>
    ///     ⚠ <b>The corner assertions above have to be shown to have teeth, or they are decoration.</b>
    ///     This uploads the same gradient with its rows reversed and asserts what that costs: the
    ///     distinct-colour count and the mean are <i>identical</i>, and only the corner is different.
    ///     A suite that asserted only a histogram would pass on a flipped thumbnail, which is the
    ///     failure the shader-graph preview work found the hard way.
    /// </remarks>
    [Fact]
    public void A_flipped_picture_has_the_same_histogram_and_different_corners() {
        using (var device = Open()) {
            var shaders = Shaders(device);
            var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Bgra8UNorm]));

            using var surface = new ThumbnailSurface(device, renderer);

            var upright = Round(device, surface, Gradient());
            var flipped = Round(device, surface, Gradient(flip: true));

            Assert.Equal(Distinct(upright), Distinct(flipped));
            Assert.Equal(Mean(upright), Mean(flipped), 6);

            const int Last = Size - 1;

            // The whole difference, and the only assertion that can see it.
            Assert.True(Channel(flipped, 0, 0, 1) > 220);
            Assert.True(Channel(flipped, 0, Last, 1) < 32);
        }
    }

    /// <summary>An image released before the frame drains takes its queued copy with it.</summary>
    /// <remarks>
    ///     ⚠ <b>An eviction can land in the same <c>Pump</c> that made the image</b>, and
    ///     <c>EditorHost.Sync</c> retires — which destroys — before <c>Present</c> flushes. A copy
    ///     left queued would name a destroyed texture, which is a use-after-free the validation layer
    ///     reports somewhere else entirely.
    /// </remarks>
    [Fact]
    public void A_released_image_leaves_no_copy_behind() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var shaders = Shaders(device);
            var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Bgra8UNorm]));

            using var surface = new ThumbnailSurface(device, renderer);

            var image = surface.Upload(Size, Size, Gradient());

            Assert.Equal(1, surface.Waiting);

            surface.Release(image);
            surface.Retire();

            Assert.Equal(0, surface.Waiting);

            device.BeginFrame();

            Assert.Equal(0, surface.Flush());

            device.EndFrame();
            device.WaitIdle();

            Assert.Equal(0, surface.Submitted);
            Assert.Equal(0, VulkanDiagnostics.ErrorCount);
        }
    }

    /// <summary>Retiring a thumbnail the previous frame read does not destroy it under that frame.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The loop below is <c>EditorHost.Run</c>'s, with the calls where the host makes
    ///         them.</b> <c>Sync</c> — which is where <c>ThumbnailSurface.Retire</c> is called —
    ///         runs <i>between</i> <c>EndFrame</c> and the next <c>BeginFrame</c>, and that is the
    ///         one detail the whole test turns on. Moving the release inside the frame makes it pass
    ///         for a reason the editor does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The claim it used to break was the backend's, not this class's.</b>
    ///         <c>ThumbnailSurface.Retire</c> is already deferred and already correct in its own
    ///         terms — it hands the handles to <c>IGraphicsDevice.Destroy</c>, whose contract is that
    ///         the object outlives every frame that could reference it. What was wrong is that
    ///         <c>VulkanDevice.Retire</c> filed an action from outside a frame under the slot the
    ///         next <c>BeginFrame</c> was about to drain, so the deferral was zero frames wide here
    ///         and <c>FramesInFlight</c> frames wide everywhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this does not establish.</b> The frame that reads the thumbnail here copies
    ///         out of it rather than sampling it through a descriptor set, because a real interface
    ///         draw needs a swapchain this test does not have. The object whose lifetime is under
    ///         test is the same <c>VkImage</c> either way and the layers track it the same way, but a
    ///         descriptor-side hazard — a set still naming a destroyed view — is outside what a copy
    ///         can see. It also proves nothing about a driver with no layers, which is the
    ///         configuration the defect was silent in.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_thumbnail_retired_between_frames_outlives_the_frame_that_read_it() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var shaders = Shaders(device);
            var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Bgra8UNorm]));

            using var surface = new ThumbnailSurface(device, renderer);

            const int Bytes = Size * Size * 4;

            var readback = device.CreateBuffer(
                new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "retirement readback")
            );

            // `EditorApplication.Update` — outside the frame, which is where `ThumbnailCache.Pump`
            // runs and where the texture is made.
            var image = surface.Upload(Size, Size, Gradient());

            Assert.NotEqual(0ul, image);

            var texture = surface.TextureOf(image);

            Assert.True(texture.IsValid, "the surface has no texture for the image it just handed out");

            // `EditorHost.Present` — the frame that copies the pixels in and then reads them.
            device.BeginFrame();

            Assert.Equal(1, surface.Flush());

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "reads the thumbnail")) {
                commands.Barrier(
                    new BarrierGroup(
                        [],
                        [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)]
                    )
                );

                commands.CopyTextureToBuffer(new TextureRegion(texture), new(Size, Size, 1), readback, 0);
                commands.Finish();
                device.GraphicsQueue.Submit([commands]);
            }

            device.EndFrame();

            // ⚠ `EditorHost.Sync`, and there is deliberately no wait between it and the frame above.
            // A tile scrolled off screen is evicted from `ThumbnailCache` here, in the update of the
            // frame after the one that drew it.
            surface.Release(image);
            surface.Retire();

            // The next frame. On a backend that filed the retirement under this slot, the destroy
            // ran as this call's first act — with the frame above still on the GPU.
            device.BeginFrame();
            device.EndFrame();

            device.WaitIdle();
            device.Destroy(readback);

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                $"Retiring a thumbnail produced {VulkanDiagnostics.ErrorCount} validation error(s):"
                + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    // ── The registration, which is a second resource with a second lifetime ──────────────────

    /// <summary>How big the pane the interface is drawn into is, in pixels.</summary>
    const int Pane = 128;

    /// <summary>A frame of interface whose only element is one thumbnail.</summary>
    /// <remarks>
    ///     ⚠ <b>Built fresh each frame and always naming the same number</b>, which is a tile that
    ///     has not been rebound since the picture behind it was evicted. The grid does rebind — that
    ///     is <c>ProjectBrowser.Rebind</c> — but it rebinds because a panel subscribes to an event,
    ///     and that is not what a lifetime should rest on.
    /// </remarks>
    static UiGeometry Frame(ulong image, GlyphFieldCache glyphs) {
        var list = new DrawList();
        list.BeginFrame();

        list.Add(
            new Vixen.Ui.DrawCommand(DrawCommandKind.Image, 8, 8, Pane - 16, Pane - 16, Color4.White, 0, 0) {
                Image = image
            }
        );

        list.EndFrame();

        return new UiGeometryBuilder().Build(list, glyphs, new Rectangle(0, 0, Pane, Pane));
    }

    /// <summary>A retired thumbnail is no longer something a draw list can reach a descriptor for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the frame the suite above says it cannot see.</b>
    ///         <see cref="A_thumbnail_retired_between_frames_outlives_the_frame_that_read_it" />
    ///         copies out of the thumbnail rather than sampling it, so it watches the <c>VkImage</c>
    ///         and nothing about the <c>VkImageView</c> a descriptor set holds. Here the interface
    ///         actually draws the tile — one image quad through <c>UiImage.frag</c>, into an
    ///         offscreen colour target rather than a swapchain — so the set is bound and read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The release is between <c>EndFrame</c> and the next <c>BeginFrame</c></b>, which
    ///         is where <c>EditorHost.Sync</c> puts it, and the frames after it still name the
    ///         number. That is the arrangement in which a registration nobody took back is a
    ///         descriptor pointing at a view the device has since freed — the destroy is deferred by
    ///         <c>FramesInFlight</c>, so the frames have to keep coming for it to land at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The answer it settled: this is a use-after-free and not merely a leak.</b> Run
    ///         against a <c>Destroy</c> that skips the unregistration, the layers say
    ///         <c>VUID-vkDestroyImageView-imageView-01026</c> — the view freed while a descriptor set
    ///         in a submitted list still names it — and <c>VUID-vkCmdDrawIndexed-None-08114</c> for
    ///         each set in the ring: <i>"the sampled image descriptor … is using imageView
    ///         VkImageView 0x0 that is invalid or has been destroyed"</i>. What kept the editor out
    ///         of that today is <c>ProjectBrowser.Rebind</c> refreshing every visible tile from the
    ///         same <c>Pump</c> that evicted the picture, so no drawn tile still carries the number
    ///         — which is a coincidence of a panel's event wiring, not a lifetime rule.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the draw counts are for.</b> Zero errors is also what a run that drew
    ///         nothing reports, so the first frame's count is asserted to be one: the set really was
    ///         bound and sampled before anything was released. The frames after it draw nothing, and
    ///         that is the fix rather than a disappointment — an unregistered number is skipped by
    ///         <c>UiRenderer.SubmitDraw</c>, which is a tile with no picture in it, exactly what the
    ///         grid shows while a decode is in flight.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_retired_thumbnail_leaves_no_descriptor_naming_its_destroyed_view() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var shaders = Shaders(device);
            using var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Rgba8UNorm]));
            using var surface = new ThumbnailSurface(device, renderer);

            var target = device.CreateTexture(
                new(
                    PixelFormat.Rgba8UNorm,
                    Pane,
                    Pane,
                    TextureUsage.ColourTarget | TextureUsage.Sampled,
                    Name: "thumbnail pane"
                )
            );

            var view = device.CreateTextureView(target);
            var glyphs = new GlyphFieldCache(new GlyphAtlas(64, 64));

            // `EditorApplication.Update`, outside the frame — where `ThumbnailCache.Pump` uploads.
            var image = surface.Upload(Size, Size, Gradient());

            Assert.NotEqual(0ul, image);

            var drawn = new List<int>();

            // Four, because the destroy is deferred by `FramesInFlight` and a two-frame run would
            // end before the view was actually freed — which is the reading that would pass on the
            // defect.
            for (var pass = 0; pass < 4; pass++) {
                var geometry = Frame(image, glyphs);

                device.BeginFrame();
                surface.Flush();

                using (var commands = device.BeginCommandList(QueueKind.Graphics, "ui")) {
                    // Outside the pass, for the reason `UiRenderer.Upload` gives: the atlas copy is a
                    // transfer and a layout transition, and neither may happen inside one.
                    renderer.Upload(commands, geometry, glyphs.Atlas);

                    commands.Barrier(
                        new(
                            [],
                            [
                                new TextureBarrier(
                                    target,
                                    pass == 0 ? ResourceState.Undefined : ResourceState.ShaderRead,
                                    ResourceState.ColourTarget
                                )
                            ]
                        )
                    );

                    commands.BeginRenderPass(
                        new([new ColourAttachment(view, LoadAction.Clear, StoreAction.Store)], name: "ui")
                    );

                    renderer.Record(commands, geometry, new Int2(Pane, Pane));

                    commands.EndRenderPass();

                    commands.Barrier(
                        new(
                            [],
                            [new TextureBarrier(target, ResourceState.ColourTarget, ResourceState.ShaderRead)]
                        )
                    );

                    commands.Finish();
                    device.GraphicsQueue.Submit([commands]);
                }

                drawn.Add(renderer.Draws);

                device.EndFrame();

                // `EditorHost.Sync`, with no wait between it and the frame above.
                if (pass == 0) {
                    surface.Release(image);
                    surface.Retire();
                }
            }

            device.WaitIdle();
            device.Destroy(view);
            device.Destroy(target);

            // The instrument, checked before the thing it is an instrument for: the first frame
            // really did bind the thumbnail's descriptor set and sample through it. Without this the
            // three assertions below are all true of a run that drew nothing at all.
            Assert.Equal(1, drawn[0]);

            // ⚠ The layers before the draw counts, because this is the assertion that says what the
            // defect *was*, and the count below only says the repair took the shape it was meant to.
            // Without the unregistration this reads three: `VUID-vkDestroyImageView-imageView-01026`
            // for the view being freed while a descriptor set in an in-flight list still names it,
            // and `VUID-vkCmdDrawIndexed-None-08114` twice — "the sampled image descriptor … is
            // using imageView VkImageView 0x0 that is invalid or has been destroyed", once per set
            // in the ring. So it is a use-after-free wherever a draw list still carries the number,
            // and not merely a leak.
            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                $"Drawing a tile that still names a retired thumbnail produced "
                + $"{VulkanDiagnostics.ErrorCount} validation error(s):"
                + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
            );

            // And every frame after the retirement skipped the number, which is what taking the
            // registration back buys. A registration left behind draws all three instead — which is
            // how the errors above are reached.
            Assert.All(drawn.Skip(1), count => Assert.Equal(0, count));
        }
    }

    /// <summary>Decoding and retiring in a loop does not grow the renderer's descriptor set count.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Counting is the only way this one is visible.</b> A registration holds one
    ///         descriptor set per frame in flight, a backend cannot free one — the pools are made
    ///         without <c>FreeDescriptorSetBit</c> on purpose — and <c>ThumbnailSurface</c> hands out
    ///         a number it never reuses. So a browser scrolled through a folder took a fresh ring per
    ///         picture and gave none of them back, and neither the picture nor the validation layers
    ///         said a word. <see cref="UiRenderer.ImageSets" /> is the observer that file documents
    ///         itself for.
    ///     </para>
    ///     <para>
    ///         Before the fix this read 2, 4, 6, 8… on a device with two frames in flight. The shape
    ///         is the assertion rather than the number: every round ends where the first one did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the first round is asserted to have allocated a ring at all</b>, because a
    ///         flat line at zero is what a loop that registered nothing produces, and that would
    ///         satisfy the assertion above for the opposite reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Re_decoding_a_thumbnail_does_not_grow_the_renderers_descriptor_set_count() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var shaders = Shaders(device);
            using var renderer = new UiRenderer(device, shaders, new RenderOutput([PixelFormat.Bgra8UNorm]));
            using var surface = new ThumbnailSurface(device, renderer);

            var counts = new List<int>();

            for (var round = 0; round < 8; round++) {
                // The editor's own order: made in the application's update, copied inside the frame,
                // released and retired between `EndFrame` and the next `BeginFrame`.
                var image = surface.Upload(Size, Size, Gradient());

                Assert.NotEqual(0ul, image);

                device.BeginFrame();

                Assert.Equal(1, surface.Flush());

                device.EndFrame();

                surface.Release(image);
                surface.Retire();

                counts.Add(renderer.ImageSets);
            }

            device.WaitIdle();

            Assert.True(
                counts[0] == device.FramesInFlight,
                $"the first upload should allocate {device.FramesInFlight} descriptor set(s) and "
                + $"allocated {counts[0]}, so the flat line below would not be about registration"
            );

            Assert.All(counts, count => Assert.Equal(counts[0], count));

            Assert.Equal(0, VulkanDiagnostics.ErrorCount);
        }
    }
}
