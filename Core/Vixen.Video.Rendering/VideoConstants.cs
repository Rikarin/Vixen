// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Video.Gpu;

namespace Vixen.Video.Rendering;

/// <summary>Which shape of picture the shader is looking at.</summary>
/// <remarks>
///     ⚠ <b>Three cases rather than a plane count, because the two one-plane layouts are not the same
///     picture.</b> A greyscale frame has luma and no colour, so its chroma is the neutral 128 the
///     shader supplies for itself; a BGRA frame has been through the conversion already and must not
///     go through it again. Counting planes cannot tell them apart, and a shader that guessed would
///     draw one of them in false colour.
/// </remarks>
public enum VideoSampleMode {
    /// <summary>Three planes: luma, blue-difference, red-difference.</summary>
    Planar = 0,

    /// <summary>One plane of luma. The chroma is neutral.</summary>
    Grey = 1,

    /// <summary>One plane of colour that needs no conversion at all.</summary>
    Packed = 2
}

/// <summary>The sixty-four bytes a video draw is, on their way to both shader stages.</summary>
/// <remarks>
///     <para>
///         <b>A push constant rather than a uniform buffer</b>, for the reason the UI's projection is
///         one: it is sixteen floats that change per draw, and a descriptor set for that would be a
///         set to allocate, bind and invalidate on every video on the screen. Sixty-four bytes is
///         half of the hundred and twenty-eight every Vulkan implementation guarantees.
///     </para>
///     <para>
///         ⚠ <b>Laid out to match the shader's <c>layout(push_constant)</c> block field for field.</b>
///         Nothing checks that at compile time, on any engine — a mismatch is a picture in the wrong
///         place or the wrong colour rather than an error — so the block is one struct here and one
///         block there, and both are commented with the same names.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VideoConstants {
    /// <summary>How many bytes the block is.</summary>
    public const int Size = 64;

    /// <summary>Clip-space scale (xy) and offset (zw), which turn a 0–1 corner into a position.</summary>
    public readonly Vector4 Placement;

    /// <summary>Texture-coordinate scale (xy) and offset (zw), which crop the picture.</summary>
    public readonly Vector4 Crop;

    /// <summary>Luma offset, luma scale, V's contribution to red, U's to blue.</summary>
    public readonly Vector4 Luma;

    /// <summary>U's contribution to green, V's, the tint alpha, and the sample mode.</summary>
    public readonly Vector4 Chroma;

    VideoConstants(Vector4 placement, Vector4 crop, Vector4 luma, Vector4 chroma) {
        Placement = placement;
        Crop = crop;
        Luma = luma;
        Chroma = chroma;
    }

    /// <summary>Works the block out for one draw.</summary>
    /// <param name="draw">What is being drawn and where.</param>
    /// <param name="surface">The target's extent, in the draw's own units.</param>
    /// <returns>The block.</returns>
    /// <remarks>
    ///     ⚠ <b>Y is flipped, and it is the opposite of what "both run downwards" concludes.</b>
    ///     Vulkan's raw clip space does have +y down, and nothing in this engine ever sees it:
    ///     <c>VulkanCommandList.SetViewport</c> submits a negative-height viewport so that the
    ///     engine's +y-up convention holds everywhere. A draw that agreed with the API instead of with
    ///     the engine draws the video upside down, and every unit test passes while it does.
    /// </remarks>
    public static VideoConstants For(in VideoDraw draw, Int2 surface) {
        var scaleX = 2f / surface.X;
        var scaleY = -2f / surface.Y;

        var coefficients = draw.Texture is null
            ? VideoColourCoefficients.For(VideoColourMatrix.Bt709, VideoColourRange.Limited)
            : draw.Texture.Coefficients;

        return new VideoConstants(
            new Vector4(
                draw.Target.Width * scaleX,
                draw.Target.Height * scaleY,
                (draw.Target.X * scaleX) - 1f,
                (draw.Target.Y * scaleY) + 1f
            ),
            new Vector4(
                draw.TextureScale.X,
                draw.TextureScale.Y,
                draw.TextureOffset.X,
                draw.TextureOffset.Y
            ),
            new Vector4(
                coefficients.LumaOffset,
                coefficients.LumaScale,
                coefficients.RedV,
                coefficients.BlueU
            ),
            new Vector4(
                coefficients.GreenU,
                coefficients.GreenV,
                draw.Tint.A,
                (float) ModeOf(draw.Texture)
            )
        );
    }

    /// <summary>Which of the three shapes a texture's planes are.</summary>
    /// <param name="texture">The texture, or null before the first frame has been uploaded.</param>
    /// <returns>The mode.</returns>
    public static VideoSampleMode ModeOf(VideoTexture? texture) =>
        texture?.Format.Layout switch {
            VideoPixelLayout.Bgra8 => VideoSampleMode.Packed,
            VideoPixelLayout.Grey8 => VideoSampleMode.Grey,
            _ => VideoSampleMode.Planar
        };
}
