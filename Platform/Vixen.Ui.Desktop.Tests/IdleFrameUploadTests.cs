// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>What a window nobody is touching writes to the device, stated as work.</summary>
/// <remarks>
///     <para>
///         <b>The third of the three counters an idle frame is measured in.</b>
///         <c>UiDocument.Diagnostics.DrawListsBuilt</c> says the draw list is rebuilt every frame,
///         <c>UiGeometryBuilder.Tessellations</c> says flattening and tessellating is skipped on a
///         still window, and until <see cref="UiRenderer.GeometryUploads" /> existed the copy to the
///         device — four arrays allocated and every vertex marshalled into host-visible memory — was
///         paid every frame and visible nowhere. This file is that number, on a device that records
///         rather than draws.
///     </para>
///     <para>
///         ⚠ <b>Driven the way <c>UiWindowSurface</c> drives it and not through it</b>, because the
///         surface needs a window and a swapchain and the headless platform stops at the RHI. What
///         is under test is the renderer's answer to the geometry the builder hands back, which is
///         the same object in both hosts — <c>UiApplication.Record</c> and <c>EditorHost.Build</c>
///         both call <c>Upload</c> with the frame <c>TryBuild</c> left them.
///     </para>
///     <para>
///         ⚠ <b>Work and not time, and a counter and not bytes.</b> A <c>NullDevice</c> keeps no
///         memory behind a buffer, so "how many bytes were written" is not a thing it can say; what
///         it can say, through the renderer, is how many frames wrote at all — which is the
///         differential doc 49 § 7.3 asks for, taken on the same machine at the same moment.
///     </para>
/// </remarks>
public class IdleFrameUploadTests {
    const int Frames = 30;

    static UiDocument Still() {
        var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            div { width: 40px; height: 20px; background-color: #345; }
            """);

        for (var index = 0; index < 8; index++) {
            document.Root.Add("div");
        }

        return document;
    }

    /// <summary>A builder, an atlas with room in it, and the extent <see cref="Still" /> lays out in.</summary>
    static (UiGeometryBuilder Builder, GlyphFieldCache Glyphs, Rectangle Extent) Tessellator() =>
        (new UiGeometryBuilder(), new GlyphFieldCache(new GlyphAtlas(256, 256), resolution: 32), new(0f, 0f, 200f, 200f));

    /// <summary>A renderer over a recording device, with the modules every host loads.</summary>
    static (NullDevice Device, UiRenderer Renderer) Renderer() {
        var device = new NullDevice();
        var renderer = new UiRenderer(device, UiShaderLibrary.Load(device), new RenderOutput([PixelFormat.Bgra8UNormSrgb]));

        return (device, renderer);
    }

    /// <summary>One frame, the way a host does it: build or keep, then upload.</summary>
    static void Frame(
        UiDocument document,
        UiGeometryBuilder builder,
        GlyphFieldCache glyphs,
        Rectangle extent,
        NullDevice device,
        UiRenderer renderer,
        ref UiGeometry frame
    ) {
        document.Update();
        document.Draw();
        builder.TryBuild(document.Drawing, glyphs, extent, ref frame);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Upload(commands, frame, glyphs.Atlas);
        commands.Finish();
    }

    /// <summary>
    ///     ⚠ Thirty frames of a still window tessellate one geometry, and used to copy it to the
    ///     device thirty times. Now once: the twenty-nine that skipped hold the region they drew
    ///     from and write nothing.
    /// </summary>
    [Fact]
    public void A_still_window_uploads_its_geometry_once_and_draws_every_later_frame_from_the_device() {
        using var document = Still();

        var (builder, glyphs, extent) = Tessellator();
        var (device, renderer) = Renderer();

        using (device)
        using (renderer) {
            var frame = default(UiGeometry);

            for (var pass = 0; pass < Frames; pass++) {
                Frame(document, builder, glyphs, extent, device, renderer, ref frame);
            }

            Assert.Equal(1, builder.Tessellations);
            Assert.Equal(1, renderer.GeometryUploads);
            Assert.Equal(Frames - 1, renderer.GeometryUploadsSkipped);
        }
    }

    /// <summary>
    ///     The half that makes the first mean something: a window whose drawing changed uploads
    ///     again, and the region it draws from moves on. A skip that never invalidated would draw a
    ///     frozen picture of the window from the device for ever.
    /// </summary>
    [Fact]
    public void A_window_whose_drawing_changed_uploads_again_into_the_next_region() {
        using var document = Still();

        var (builder, glyphs, extent) = Tessellator();
        var (device, renderer) = Renderer();

        using (device)
        using (renderer) {
            var frame = default(UiGeometry);

            Frame(document, builder, glyphs, extent, device, renderer, ref frame);
            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            var region = renderer.Region;

            Assert.Equal(1, renderer.GeometryUploads);

            document.Root.Children[0].AddClass("moved");
            document.Load(".moved { width: 90px; }");

            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            Assert.Equal(2, builder.Tessellations);
            Assert.Equal(2, renderer.GeometryUploads);
            Assert.Equal(1, renderer.GeometryUploadsSkipped);

            // ⚠ The region advanced, which is what keeps a frame in flight from being written over:
            // the skip works by *not* advancing, so the property worth pinning is that an upload
            // still does.
            Assert.NotEqual(region, renderer.Region);
        }
    }

    /// <summary>
    ///     ⚠ <b>A skipped frame stays in the region the device already holds</b>, which is the
    ///     mechanism rather than a detail: the ring exists so a host never writes a region a
    ///     submitted frame is reading, and a frame that writes nothing has no reason to move.
    /// </summary>
    [Fact]
    public void A_skipped_frame_keeps_its_region() {
        using var document = Still();

        var (builder, glyphs, extent) = Tessellator();
        var (device, renderer) = Renderer();

        using (device)
        using (renderer) {
            var frame = default(UiGeometry);

            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            var region = renderer.Region;

            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            Assert.Equal(1, renderer.GeometryUploadsSkipped);
            Assert.Equal(region, renderer.Region);
        }
    }

    /// <summary>
    ///     ⚠ <b>And a re-registered image uploads again although the drawing did not change</b>,
    ///     which is the half of the skip that reads as redundant and is not. A host that replaces
    ///     the texture behind a number — a scene viewport resized, a thumbnail re-rendered — marks
    ///     every frame's descriptor set stale, and the rewrite is deferred to each frame's own turn
    ///     at the region it owns. A frame that skipped would draw with the set it already had,
    ///     pointing at the view the host just destroyed.
    /// </summary>
    [Fact]
    public void A_re_registered_image_uploads_again_although_the_drawing_did_not_change() {
        using var document = Still();

        var (builder, glyphs, extent) = Tessellator();
        var (device, renderer) = Renderer();

        using (device)
        using (renderer) {
            var texture = device.CreateTexture(
                new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: "viewport")
            );

            renderer.RegisterImage(7, device.CreateTextureView(texture));

            var frame = default(UiGeometry);

            Frame(document, builder, glyphs, extent, device, renderer, ref frame);
            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            Assert.Equal(1, renderer.GeometryUploads);
            Assert.Equal(1, renderer.GeometryUploadsSkipped);

            // The same number, a new view: what a viewport does when the pane it is in is resized.
            renderer.RegisterImage(7, device.CreateTextureView(texture));

            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            Assert.Equal(1, builder.Tessellations);
            Assert.Equal(2, renderer.GeometryUploads);
            Assert.Equal(1, renderer.GeometryUploadsSkipped);

            // And once its turn has come round, the still window is still again.
            Frame(document, builder, glyphs, extent, device, renderer, ref frame);

            Assert.Equal(2, renderer.GeometryUploads);
            Assert.Equal(2, renderer.GeometryUploadsSkipped);
        }
    }

    /// <summary>
    ///     Geometry nobody stamped is uploaded every time it is handed over, which is every host and
    ///     fixture that builds a frame by hand — the behaviour they had before the stamp existed, and
    ///     the reason zero is the constructor's default rather than a generation of its own.
    /// </summary>
    [Fact]
    public void Unstamped_geometry_is_uploaded_every_frame() {
        var (device, renderer) = Renderer();

        using (device)
        using (renderer) {
            var geometry = new UiGeometry(
                [
                    new(new(0f, 0f), new(0f, 0f), Color4.White, new(1f, 0f, 0f, 0f)),
                    new(new(10f, 0f), new(1f, 0f), Color4.White, new(1f, 0f, 0f, 0f)),
                    new(new(10f, 10f), new(1f, 1f), Color4.White, new(1f, 0f, 0f, 0f))
                ],
                [0u, 1u, 2u],
                [],
                []
            );

            Assert.Equal(0, geometry.Generation);

            var atlas = new GlyphAtlas(64, 64);

            for (var pass = 0; pass < 3; pass++) {
                using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

                renderer.Upload(commands, geometry, atlas);
                commands.Finish();
            }

            Assert.Equal(3, renderer.GeometryUploads);
            Assert.Equal(0, renderer.GeometryUploadsSkipped);
        }
    }

    /// <summary>
    ///     ⚠ <b>Two builders never share a generation</b>, which is what lets a renderer key on it
    ///     by equality. Per-builder counting would hand a renderer drawing two documents in turn — a
    ///     golden fixture does exactly this — a second frame stamped like the first, and it would
    ///     draw the first document's vertices for the second.
    /// </summary>
    [Fact]
    public void Generations_are_unique_across_builders() {
        using var first = Still();
        using var second = Still();

        var (one, glyphs, extent) = Tessellator();
        var two = new UiGeometryBuilder();

        first.Update();
        first.Draw();
        second.Update();
        second.Draw();

        var a = default(UiGeometry);
        var b = default(UiGeometry);

        Assert.True(one.TryBuild(first.Drawing, glyphs, extent, ref a));
        Assert.True(two.TryBuild(second.Drawing, glyphs, extent, ref b));

        Assert.NotEqual(0, a.Generation);
        Assert.NotEqual(0, b.Generation);
        Assert.NotEqual(a.Generation, b.Generation);

        // And a kept frame keeps its stamp: the whole point is that a skip is recognisable.
        Assert.False(one.TryBuild(first.Drawing, glyphs, extent, ref a));
        Assert.NotEqual(0, a.Generation);
    }
}
