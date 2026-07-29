// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Video.Gpu;
using Xunit;

namespace Vixen.Video.Rendering.Tests;

/// <summary>The sixty-four bytes, which nothing else can check.</summary>
/// <remarks>
///     ⚠ <b>The one part of a renderer whose mistakes are silent.</b> A push block that disagrees with
///     the shader's is a picture in the wrong place or the wrong colour — never an error, on any API —
///     so the arithmetic is asserted here and the field order is asserted by the size.
/// </remarks>
public sealed class VideoConstantsTests {
    [Fact]
    public void TheBlockIsTheSizeTheLayoutDeclares() {
        // Four vec4s. If this ever disagrees, the pipeline layout's push range and the struct
        // disagree, and Vulkan rejects the range rather than the struct — which points at the wrong
        // file.
        Assert.Equal(VideoConstants.Size, Unsafe.SizeOf<VideoConstants>());
        Assert.Equal(64, VideoConstants.Size);
    }

    [Fact]
    public void AFullSurfaceRectangleCoversClipSpace() {
        var constants = VideoConstants.For(Draw(new Rectangle(0, 0, 200, 100)), new Int2(200, 100));

        // uv 0,0 → (-1, +1) and uv 1,1 → (+1, -1): the whole of clip space, with y running up.
        Assert.Equal(2f, constants.Placement.X, 5);
        Assert.Equal(-2f, constants.Placement.Y, 5);
        Assert.Equal(-1f, constants.Placement.Z, 5);
        Assert.Equal(1f, constants.Placement.W, 5);
    }

    [Fact]
    public void YRunsUpwards() {
        // ⚠ The trap this exists for. Vulkan's raw clip space has +y down and nothing in this engine
        // ever sees it — the Vulkan backend submits a negative-height viewport so +y is up
        // everywhere. A block that agreed with the API rather than with the engine draws the video
        // upside down, and every other test passes while it does.
        var top = VideoConstants.For(Draw(new Rectangle(0, 0, 100, 10)), new Int2(100, 100));

        // A rectangle at the top of the surface must be at the top of clip space, which is +1.
        Assert.Equal(1f, top.Placement.W, 5);
        Assert.True(top.Placement.Y < 0f);
    }

    [Fact]
    public void ARectangleInACornerLandsInThatCorner() {
        var constants = VideoConstants.For(Draw(new Rectangle(150, 150, 50, 50)), new Int2(200, 200));

        // Bottom-right quarter-ish: x starts at +0.5, y starts at -0.5, and each spans half a unit.
        Assert.Equal(0.5f, constants.Placement.X, 5);
        Assert.Equal(-0.5f, constants.Placement.Y, 5);
        Assert.Equal(0.5f, constants.Placement.Z, 5);
        Assert.Equal(-0.5f, constants.Placement.W, 5);
    }

    [Fact]
    public void TheCoefficientsAreTheTexturesOwn() {
        using var device = new NullDevice(new NullDeviceOptions());
        using var texture = VideoRendererTests.Uploaded(device, 32, 16);

        var constants = VideoConstants.For(VideoDraw.Filling(texture, new Rectangle(0, 0, 10, 10)), new Int2(10, 10));
        var expected = texture.Coefficients;

        // The six numbers, in the order the shader reads them. A transposition here is a picture with
        // its red and blue swapped, which reads as a decoder bug.
        Assert.Equal(expected.LumaOffset, constants.Luma.X, 5);
        Assert.Equal(expected.LumaScale, constants.Luma.Y, 5);
        Assert.Equal(expected.RedV, constants.Luma.Z, 5);
        Assert.Equal(expected.BlueU, constants.Luma.W, 5);
        Assert.Equal(expected.GreenU, constants.Chroma.X, 5);
        Assert.Equal(expected.GreenV, constants.Chroma.Y, 5);
    }

    [Theory]
    [InlineData(VideoPixelLayout.Yuv420Planar, VideoSampleMode.Planar)]
    [InlineData(VideoPixelLayout.Yuv444Planar, VideoSampleMode.Planar)]
    [InlineData(VideoPixelLayout.Grey8, VideoSampleMode.Grey)]
    [InlineData(VideoPixelLayout.Bgra8, VideoSampleMode.Packed)]
    public void TheModeDistinguishesTheTwoOnePlaneLayouts(VideoPixelLayout layout, VideoSampleMode expected) {
        using var device = new NullDevice(new NullDeviceOptions());
        using var texture = new VideoTexture(device, "test");

        var frame = new VideoFrame();
        frame.Reset(new VideoFormat(32, 16, layout));

        VideoRendererTests.Upload(device, texture, frame);

        // ⚠ Counting planes cannot tell grey from packed — both are one — and drawing one as the
        // other is a greyscale picture in false colour or a colour picture converted twice.
        Assert.Equal(expected, VideoConstants.ModeOf(texture));
    }

    static VideoDraw Draw(Rectangle target) =>
        new(null!, target, Vector2.One, Vector2.Zero, Color4.White);
}
