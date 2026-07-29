// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Video.Gpu;
using Xunit;

namespace Vixen.Video.Rendering.Tests;

/// <summary>The device half, against the backend that records what it was asked to do.</summary>
public sealed class VideoRendererTests {
    [Fact]
    public void OneVideoIsOnePipelineOneSetAndOneDraw() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = Uploaded(device, 64, 32);
        using var renderer = Renderer(device);

        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));

        Assert.Equal(1, renderer.Draws);
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.BindPipeline));
        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.Draw));
    }

    [Fact]
    public void DrawingTheSameVideoAgainWritesNoDescriptors() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = Uploaded(device, 64, 32);
        using var renderer = Renderer(device);

        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));
        var first = renderer.DescriptorWrites;

        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));
        Record(device, renderer, texture, new Rectangle(0, 0, 50, 50));

        // ⚠ The claim: a video drawn every frame, and twice a frame, costs one descriptor write for
        // its life. A number that climbs is invisible in the picture and is a set rewritten while a
        // frame in flight is reading it.
        Assert.Equal(1, first);
        Assert.Equal(1, renderer.DescriptorWrites);
    }

    [Fact]
    public void ChangingShapeRewritesTheSet() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = new VideoTexture(device, "test");
        using var renderer = Renderer(device);

        Upload(device, texture, Frame(64, 32));
        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));

        // A WebM may change resolution mid-stream. VideoTexture destroys and recreates its planes,
        // so a set still naming the old views is a set naming destroyed resources.
        Upload(device, texture, Frame(128, 64));
        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));

        Assert.Equal(2, renderer.DescriptorWrites);
    }

    [Fact]
    public void ADegenerateDrawRecordsNothing() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = Uploaded(device, 64, 32);
        using var renderer = Renderer(device);

        // A zero-width rectangle and a zero-sized surface are both ordinary — a collapsed panel, a
        // minimised window — and neither is worth a draw call that covers nothing.
        Assert.False(Record(device, renderer, texture, new Rectangle(0, 0, 0, 100)));
        Assert.False(Record(device, renderer, texture, new Rectangle(0, 0, 10, 10), new Int2(0, 0)));
        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    [Fact]
    public void ForgettingATextureReleasesItsSet() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = Uploaded(device, 64, 32);
        using var renderer = Renderer(device);

        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));

        Assert.True(renderer.Forget(texture));
        Assert.False(renderer.Forget(texture));

        Record(device, renderer, texture, new Rectangle(0, 0, 100, 100));

        // Forgotten and asked for again: a new set. Which is the point — a long-running game that
        // opened a hundred cutscenes should not keep a hundred sets alive for the one it is playing.
        Assert.Equal(2, renderer.DescriptorWrites);
    }

    internal static VideoRenderer Renderer(NullDevice device) =>
        new(
            device,
            new VideoShaders(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "video vertex"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "video fragment")
            ),
            new RenderOutput([PixelFormat.Bgra8UNorm])
        );

    internal static VideoTexture Uploaded(NullDevice device, int width, int height) {
        var texture = new VideoTexture(device, "test");

        Upload(device, texture, Frame(width, height));

        return texture;
    }

    internal static VideoFrame Frame(int width, int height) {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(width, height, VideoPixelLayout.Yuv420Planar));

        return frame;
    }

    /// <summary>
    ///     ⚠ Finished and submitted, because the null backend flushes to its recorder at submission
    ///     rather than on each call — a helper that only recorded would assert against an empty list.
    /// </summary>
    internal static void Upload(NullDevice device, VideoTexture texture, VideoFrame frame) {
        using var commands = device.BeginCommandList(QueueKind.Graphics, "upload");

        texture.Upload(commands, frame);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);
    }

    internal static TextureViewHandle Target(NullDevice device) =>
        device.CreateTextureView(
            device.CreateTexture(
                new TextureDescription(PixelFormat.Bgra8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "target")
            )
        );

    static bool Record(
        NullDevice device,
        VideoRenderer renderer,
        VideoTexture texture,
        Rectangle target,
        Int2? surface = null
    ) {
        using var commands = device.BeginCommandList(QueueKind.Graphics, "draw");

        // ⚠ Inside a pass, because a draw outside one is what the null backend refuses and what every
        // real API refuses. The pass is what the renderer would be recorded into by a compositor.
        commands.BeginRenderPass(new RenderPassDescription([new ColourAttachment(Target(device))], name: "video"));

        renderer.Begin();

        var drawn = renderer.Record(
            commands,
            VideoDraw.Filling(texture, target),
            surface ?? new Int2(200, 200)
        );

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        return drawn;
    }
}
