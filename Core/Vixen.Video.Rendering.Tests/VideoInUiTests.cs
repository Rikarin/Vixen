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
using Vixen.Video.Ui;
using Xunit;

namespace Vixen.Video.Rendering.Tests;

/// <summary>A video inside a user interface, end to end and without a device that draws.</summary>
/// <remarks>
///     <para>
///         <b>What this asserts is that the two ends of one seam meet.</b> A draw list names a picture
///         it knows nothing about; a renderer hands it to a drawer; a drawer recognises a video and
///         draws it. Each half is testable alone and each half passing means nothing on its own —
///         the failure this exists to catch is the interface being wired to nobody, which shows as a
///         hole in a panel and nothing in a log.
///     </para>
///     <para>
///         Driven through <c>UiRenderer</c> rather than a <c>RenderSystem</c>, for the reason that
///         class was split out at all: the part that touches a device can be driven without a scene,
///         a camera or a compositor.
///     </para>
/// </remarks>
public sealed class VideoInUiTests {
    [Fact]
    public void ASurfaceCommandBecomesItsOwnBatchAndItsOwnDraw() {
        var list = new DrawList();
        var source = new object();

        list.BeginFrame();
        list.Add(Rectangle(0, 0, 10, 10));
        list.Add(SurfaceCommand(list, source, new Rectangle(2, 2, 6, 6)));
        list.Add(Rectangle(0, 0, 10, 10));
        list.EndFrame();

        // ⚠ Three batches, not two. A surface never merges with its neighbours — two surfaces are two
        // textures and two descriptor sets — and it must not reorder past them either, because order
        // is the only answer a UI has to what is in front.
        Assert.Equal(3, list.Batches.Count);
        Assert.Equal(BatchKind.Geometry, list.Batches[0].Kind);
        Assert.Equal(BatchKind.Surface, list.Batches[1].Kind);
        Assert.Equal(BatchKind.Geometry, list.Batches[2].Kind);
        Assert.Same(source, list.Surfaces[0]);
    }

    [Fact]
    public void SwappingTheSourceChangesTheFrameEvenThoughTheCommandsMatch() {
        var list = new DrawList();

        Frame(list, new object());
        var first = list.Version;

        Frame(list, new object());

        // ⚠ The failure this prevents: a video element cutting from one clip to another emits
        // byte-identical commands — same rectangle, same index — so a diff over the commands alone
        // reports the frame unchanged and the cached geometry keeps drawing the first clip.
        Assert.NotEqual(first, list.Version);
    }

    [Fact]
    public void DrawingTheSameSourceAgainChangesNothing() {
        var list = new DrawList();
        var source = new object();

        Frame(list, source);
        var first = list.Version;

        Frame(list, source);

        // The ordinary case: a video hands over the same texture every frame while its contents
        // change entirely. A version that moved would throw the cached frame away sixty times a
        // second to redraw the same quad.
        Assert.Equal(first, list.Version);
    }

    [Fact]
    public void AVideoPlayerReachesTheVideoRendererThroughTheDrawList() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var video = VideoRendererTests.Renderer(device);
        using var texture = VideoRendererTests.Uploaded(device, 64, 32);
        using var ui = UiRenderer(device);

        var drawer = new VideoSurfaceDrawer(video, _ => texture);
        ui.SurfaceDrawers.Add(drawer);

        var geometry = Geometry(texture, new Rectangle(10, 20, 100, 50));

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        commands.BeginRenderPass(
            new RenderPassDescription([new ColourAttachment(VideoRendererTests.Target(device))], name: "ui")
        );

        ui.Record(commands, geometry, new Int2(400, 300));

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Equal(1, ui.Draws);
        Assert.Equal(0, ui.SurfacesUnclaimed);
        Assert.Equal(1, video.Draws);
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    [Fact]
    public void AnUnclaimedSurfaceIsCountedRatherThanThrown() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var ui = UiRenderer(device);

        var geometry = Geometry(new object(), new Rectangle(0, 0, 10, 10));

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        commands.BeginRenderPass(
            new RenderPassDescription([new ColourAttachment(VideoRendererTests.Target(device))], name: "ui")
        );

        ui.Record(commands, geometry, new Int2(400, 300));

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // A source and a drawer that were never introduced is a wiring mistake, not a corrupt frame.
        // Throwing here would take down a game over a hole in a panel.
        Assert.Equal(1, ui.SurfacesUnclaimed);
        Assert.Equal(0, ui.Draws);
    }

    [Fact]
    public void TheRectangleAndTheTintSurviveTheRoundTrip() {
        using var device = new NullDevice(new NullDeviceOptions());
        using var video = VideoRendererTests.Renderer(device);
        using var texture = VideoRendererTests.Uploaded(device, 64, 32);
        using var ui = UiRenderer(device);

        var seen = new List<UiSurfaceDraw>();
        ui.SurfaceDrawers.Add(new Watcher(seen));

        var geometry = Geometry(texture, new Rectangle(10, 20, 100, 50), new Color4(1f, 1f, 1f, 0.25f));

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        commands.BeginRenderPass(
            new RenderPassDescription([new ColourAttachment(VideoRendererTests.Target(device))], name: "ui")
        );

        ui.Record(commands, geometry, new Int2(400, 300));
        commands.EndRenderPass();

        // ⚠ Both are read back off the quad rather than carried on the draw, so this is what says the
        // geometry really is the answer. A fade applied by `opacity` arrives here in the alpha.
        var draw = Assert.Single(seen);

        Assert.Equal(10f, draw.Rectangle.X, 3);
        Assert.Equal(20f, draw.Rectangle.Y, 3);
        Assert.Equal(100f, draw.Rectangle.Width, 3);
        Assert.Equal(50f, draw.Rectangle.Height, 3);
        Assert.Equal(0.25f, draw.Tint.A, 3);
        Assert.Equal(new Int2(400, 300), draw.Surface);
    }

    [Fact]
    public void APlayerWithNothingUploadedYetIsCountedRatherThanDrawn() {
        using var device = new NullDevice(new NullDeviceOptions());
        using var video = VideoRendererTests.Renderer(device);

        var drawer = new VideoSurfaceDrawer(video, _ => null);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        var player = new Playback.VideoPlayer(new SilentDecoder());

        try {
            var drawn = drawer.Draw(
                commands,
                new UiSurfaceDraw(player, new Rectangle(0, 0, 10, 10), Color4.White, default, new Int2(100, 100), 1f)
            );

            // The first frame or two of every cutscene, and not an error — but a number that keeps
            // climbing means the uploader is not being run, which otherwise looks exactly like a
            // video that never decoded.
            Assert.False(drawn);
            Assert.Equal(1, drawer.NotReady);
        } finally {
            player.Dispose();
        }
    }

    static UiRenderer UiRenderer(NullDevice device) =>
        new(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui box"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui text"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui solid")
            ),
            new RenderOutput([PixelFormat.Bgra8UNorm])
        );

    static UiGeometry Geometry(object source, Rectangle where, Color4 tint = default) {
        var list = new DrawList();

        list.BeginFrame();
        list.Add(SurfaceCommand(list, source, where, tint));
        list.EndFrame();

        return new UiGeometryBuilder().Build(list, new GlyphFieldCache(new GlyphAtlas(64, 64)), new Rectangle(0, 0, 400, 300));
    }

    static void Frame(DrawList list, object source) {
        list.BeginFrame();
        list.Add(SurfaceCommand(list, source, new Rectangle(0, 0, 10, 10)));
        list.EndFrame();
    }

    static DrawCommand SurfaceCommand(DrawList list, object source, Rectangle where, Color4 tint = default) =>
        new(
            DrawCommandKind.Surface,
            where.X,
            where.Y,
            where.Width,
            where.Height,
            tint == default ? Color4.White : tint,
            0f,
            0f
        ) {
            Surface = list.AddSurface(source)
        };

    static DrawCommand Rectangle(float x, float y, float width, float height) =>
        new(DrawCommandKind.Rectangle, x, y, width, height, Color4.White, 0f, 0f);

    /// <summary>Claims everything and records what it was handed.</summary>
    sealed class Watcher(List<UiSurfaceDraw> seen) : IUiSurfaceDrawer {
        public bool Draw(ICommandList commands, in UiSurfaceDraw draw) {
            seen.Add(draw);

            return true;
        }
    }

    /// <summary>A decoder that produces nothing, so the player never has a frame to upload.</summary>
    sealed class SilentDecoder : IVideoStreamDecoder {
        public VideoFormat Format => new(16, 16, VideoPixelLayout.Yuv420Planar);

        public TimeSpan Duration => TimeSpan.Zero;

        public TimeSpan Position => TimeSpan.Zero;

        public bool CanSeek => false;

        public VideoDecodeStatus DecodeNext(VideoFrame destination) => VideoDecodeStatus.EndOfStream;

        public void Seek(TimeSpan position) { }

        public void Dispose() { }
    }
}
