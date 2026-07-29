// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Vixen.Video.Gpu;
using Xunit;

namespace Vixen.Video.Rendering.Tests;

/// <summary>A video inside a user interface, and there is no seam between them.</summary>
/// <remarks>
///     <para>
///         <b>What this asserts is that a video is an ordinary picture by the time a UI sees one.</b>
///         <c>UiRenderer</c> already draws a texture nobody there wrote: an element puts a number in
///         an image command and the host registers a view against that number. A video's planes
///         cannot be that view — there are three of them and they are not colour — so
///         <see cref="VideoRenderTarget" /> runs the conversion into a target of its own, and what
///         comes out is a view like any other.
///     </para>
///     <para>
///         ⚠ <b>Nothing shipped joins these two assemblies.</b> The join is one line in a game —
///         <c>ui.RegisterImage(handle, target.View)</c> — which is exactly the property worth having,
///         and exactly the property a test has to check on purpose, because there is no code anywhere
///         that would fail to compile if it stopped being true.
///     </para>
/// </remarks>
public sealed class VideoInUiTests {
    const ulong Handle = 7;

    [Fact]
    public void AVideosTargetIsAViewAUiRendererWillDraw() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var planes = VideoRendererTests.Uploaded(device, 64, 32);
        using var target = Target(device);
        using var ui = UiRenderer(device);

        Convert(device, target, planes, new Int2(64, 32));
        ui.RegisterImage(Handle, target.View);

        Record(device, ui, Geometry(new Rectangle(10, 20, 100, 50)));

        // The whole claim: one draw, through the interface's own image pipeline, of a picture that
        // was three R8 planes a moment ago.
        Assert.Equal(1, ui.Draws);
    }

    [Fact]
    public void AnUnregisteredNumberDrawsNothing() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var ui = UiRenderer(device);

        Record(device, ui, Geometry(new Rectangle(0, 0, 10, 10)));

        // ⚠ Nothing, and specifically not the atlas. Drawing an unregistered number through the image
        // shader would sample the glyph atlas — a rectangle of scrambled letters where the video
        // should be — which is why `UiRenderer` skips rather than falls back.
        Assert.Equal(0, ui.Draws);
    }

    [Fact]
    public void ResizingTheVideoMakesANewViewAndSaysSo() {
        using var device = new NullDevice(new NullDeviceOptions());
        using var planes = new VideoTexture(device, "test");
        using var target = Target(device);

        VideoRendererTests.Upload(device, planes, VideoRendererTests.Frame(64, 32));
        Convert(device, target, planes, new Int2(64, 32));

        var first = target.Revision;
        var view = target.View;

        VideoRendererTests.Upload(device, planes, VideoRendererTests.Frame(128, 64));
        Convert(device, target, planes, new Int2(128, 64));

        // ⚠ The revision is what a consumer holding the view has to watch. A resize destroys the
        // texture, so a descriptor set still naming the old view names freed memory — undefined
        // rather than an error, and it shows as a picture that is fine until the window is dragged.
        Assert.NotEqual(first, target.Revision);
        Assert.NotEqual(view, target.View);
        Assert.Equal(new Int2(128, 64), target.Size);
    }

    [Fact]
    public void RedrawingAtTheSameSizeKeepsTheView() {
        using var device = new NullDevice(new NullDeviceOptions());
        using var planes = VideoRendererTests.Uploaded(device, 64, 32);
        using var target = Target(device);

        Convert(device, target, planes, new Int2(64, 32));

        var revision = target.Revision;
        var view = target.View;

        Convert(device, target, planes, new Int2(64, 32));

        // The ordinary case, twenty-five times a second: the contents change and the handle does not,
        // so nothing has to be registered again.
        Assert.Equal(revision, target.Revision);
        Assert.Equal(view, target.View);
    }

    [Fact]
    public void TheConversionIsAPassOfItsOwnWithABarrierEitherSide() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var planes = VideoRendererTests.Uploaded(device, 64, 32);
        using var target = Target(device);

        // The recorder is cumulative across submissions and the upload above already put two barriers
        // in it, so what is asserted is the difference this call makes.
        var before = device.Recorder!.CountOf(RecordedCommandKind.Barrier);

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "convert")) {
            target.Draw(commands, planes, new Int2(64, 32));
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        // ⚠ Into the attachment state and back out. A pass declares what its attachments need; the
        // transition *out* is the caller's, and a target left in ColourTarget is one the consumer's
        // shader reads as undefined.
        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.BeginRenderPass));
        Assert.Equal(before + 2, device.Recorder.CountOf(RecordedCommandKind.Barrier));
    }

    [Fact]
    public void ADegenerateSizeDrawsNothingRatherThanCreatingAZeroTexture() {
        using var device = new NullDevice(new NullDeviceOptions());
        using var planes = VideoRendererTests.Uploaded(device, 64, 32);
        using var target = Target(device);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "convert");

        // A player whose first frame has not arrived reports nothing for a size, and a zero-extent
        // texture is a validation error on every backend.
        Assert.False(target.Draw(commands, planes, new Int2(0, 32)));
        Assert.False(target.View.IsValid);
    }

    static VideoRenderTarget Target(NullDevice device) =>
        new(
            device,
            new VideoShaders(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "video vertex"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "video fragment")
            )
        );

    static void Convert(NullDevice device, VideoRenderTarget target, VideoTexture planes, Int2 size) {
        using var commands = device.BeginCommandList(QueueKind.Graphics, "convert");

        target.Draw(commands, planes, size);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);
    }

    static void Record(NullDevice device, UiRenderer ui, UiGeometry geometry) {
        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        ui.Upload(commands, geometry, new GlyphAtlas(64, 64));

        commands.BeginRenderPass(
            new RenderPassDescription([new ColourAttachment(VideoRendererTests.Target(device))], name: "ui")
        );

        ui.Record(commands, geometry, new Int2(400, 300));

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);
    }

    static UiRenderer UiRenderer(NullDevice device) =>
        new(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui box"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui text"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui solid")
            ) {
                Image = device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui image")
            },
            new RenderOutput([PixelFormat.Bgra8UNorm])
        );

    static UiGeometry Geometry(Rectangle where) {
        var list = new DrawList();

        list.BeginFrame();

        list.Add(
            new Vixen.Ui.DrawCommand(
                DrawCommandKind.Image,
                where.X,
                where.Y,
                where.Width,
                where.Height,
                Color4.White,
                0f,
                0f
            ) {
                Image = Handle
            }
        );

        list.EndFrame();

        return new UiGeometryBuilder().Build(
            list,
            new GlyphFieldCache(new GlyphAtlas(64, 64)),
            new Rectangle(0, 0, 400, 300)
        );
    }
}
