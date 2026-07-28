// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>A <see cref="PixelFormat" /> as GL's three separate answers.</summary>
/// <param name="Internal">The sized internal format a texture is allocated with.</param>
/// <param name="Format">The channel layout a transfer reads or writes.</param>
/// <param name="Type">The component type a transfer reads or writes.</param>
/// <param name="Renderable">Whether the format may be a colour or depth attachment.</param>
/// <remarks>
///     Three values where Vulkan, D3D12 and WebGPU each have one. That is GL's oldest wart and it is
///     a genuine source of bugs: <c>glTexStorage2D</c> takes the sized internal format,
///     <c>glTexSubImage2D</c> takes the unsized pair, and a mismatched pair is not an error — it is
///     a conversion, so the texture ends up populated with something almost right.
/// </remarks>
readonly record struct GlFormat(uint Internal, uint Format, uint Type, bool Renderable = true);

/// <summary>Pixel formats, in GL's terms.</summary>
static class GlFormats {
    /// <summary>What a format is in GL.</summary>
    /// <param name="format">The RHI format.</param>
    /// <param name="profile">The profile, which decides whether some of them exist.</param>
    /// <exception cref="NotSupportedException">The profile has no such format.</exception>
    public static GlFormat Of(PixelFormat format, GlProfile profile) => format switch {
        PixelFormat.R8UNorm => new(0x8229, GlConstants.Red, GlConstants.UnsignedByte),
        PixelFormat.R8SNorm => new(0x8F94, GlConstants.Red, GlConstants.Byte, false),
        PixelFormat.R8UInt => new(0x8232, GlConstants.RedInteger, GlConstants.UnsignedByte),
        PixelFormat.R8SInt => new(0x8231, GlConstants.RedInteger, GlConstants.Byte),

        PixelFormat.Rg8UNorm => new(0x822B, GlConstants.RedGreen, GlConstants.UnsignedByte),
        PixelFormat.Rg8SNorm => new(0x8F95, GlConstants.RedGreen, GlConstants.Byte, false),
        PixelFormat.R16Float => new(0x822D, GlConstants.Red, GlConstants.HalfFloat),
        PixelFormat.R16UInt => new(0x8234, GlConstants.RedInteger, GlConstants.UnsignedShort),
        PixelFormat.R16UNorm => new(0x822A, GlConstants.Red, GlConstants.UnsignedShort),

        PixelFormat.Rgba8UNorm => new(0x8058, GlConstants.Rgba, GlConstants.UnsignedByte),
        PixelFormat.Rgba8UNormSrgb => new(0x8C43, GlConstants.Rgba, GlConstants.UnsignedByte),
        PixelFormat.Rgba8SNorm => new(0x8F97, GlConstants.Rgba, GlConstants.Byte, false),

        // ⚠ Allocated as RGBA8 and transferred as BGRA. GL has no `GL_BGRA8` sized internal format —
        // the swizzle is a property of the *transfer*, not of the storage — and GLES has no
        // `GL_BGRA` at all without an extension. Naming the format at all is a concession to what
        // a Windows swapchain hands back; on GL it costs a channel swap at upload and nothing at
        // sample time.
        PixelFormat.Bgra8UNorm => profile >= GlProfile.Core45
            ? new GlFormat(0x8058, GlConstants.Bgra, GlConstants.UnsignedByte)
            : throw Unsupported(format, profile, "GLES has no core GL_BGRA transfer format"),
        PixelFormat.Bgra8UNormSrgb => profile >= GlProfile.Core45
            ? new GlFormat(0x8C43, GlConstants.Bgra, GlConstants.UnsignedByte)
            : throw Unsupported(format, profile, "GLES has no core GL_BGRA transfer format"),

        PixelFormat.Rg16Float => new(0x822F, GlConstants.RedGreen, GlConstants.HalfFloat),
        PixelFormat.R32Float => new(0x822E, GlConstants.Red, GlConstants.Float),
        PixelFormat.R32UInt => new(0x8236, GlConstants.RedInteger, GlConstants.UnsignedInt),
        PixelFormat.Rgb10A2UNorm => new(0x8059, GlConstants.Rgba, GlConstants.UnsignedInt2101010Rev),
        PixelFormat.Rg11B10Float => new(0x8C3A, GlConstants.Rgb, GlConstants.UnsignedInt10F11F11FRev),

        PixelFormat.Rgba16Float => new(0x881A, GlConstants.Rgba, GlConstants.HalfFloat),
        PixelFormat.Rg32Float => new(0x8230, GlConstants.RedGreen, GlConstants.Float),
        PixelFormat.Rgba16UNorm => new(0x805B, GlConstants.Rgba, GlConstants.UnsignedShort),

        PixelFormat.Rgba32Float => new(0x8814, GlConstants.Rgba, GlConstants.Float),
        PixelFormat.Rgba32UInt => new(0x8D70, GlConstants.RgbaInteger, GlConstants.UnsignedInt),

        PixelFormat.Depth16UNorm => new(0x81A5, GlConstants.DepthComponent, GlConstants.UnsignedShort),
        PixelFormat.Depth32Float => new(0x8CAC, GlConstants.DepthComponent, GlConstants.Float),
        PixelFormat.Depth24UNormStencil8 => new(0x88F0, GlConstants.DepthStencil, GlConstants.UnsignedInt248),
        PixelFormat.Depth32FloatStencil8 => new(
            0x8CAD,
            GlConstants.DepthStencil,
            GlConstants.Float32UnsignedInt248Rev
        ),

        // Compressed formats never carry a transfer format or type — the data is blocks, and
        // `glCompressedTexSubImage*` takes the internal format twice. Reported separately so a
        // caller that reached for the transfer pair on one gets a diagnostic and not a zero.
        PixelFormat.Bc1RgbaUNorm => Compressed(0x83F1, profile, DesktopOnly),
        PixelFormat.Bc1RgbaUNormSrgb => Compressed(0x8C4D, profile, DesktopOnly),
        PixelFormat.Bc3RgbaUNorm => Compressed(0x83F3, profile, DesktopOnly),
        PixelFormat.Bc3RgbaUNormSrgb => Compressed(0x8C4F, profile, DesktopOnly),
        PixelFormat.Bc4RUNorm => Compressed(0x8DBB, profile, DesktopOnly),
        PixelFormat.Bc5RgUNorm => Compressed(0x8DBD, profile, DesktopOnly),
        PixelFormat.Bc6HRgbUFloat => Compressed(0x8E8F, profile, DesktopOnly),
        PixelFormat.Bc7RgbaUNorm => Compressed(0x8E8C, profile, DesktopOnly),
        PixelFormat.Bc7RgbaUNormSrgb => Compressed(0x8E8D, profile, DesktopOnly),

        PixelFormat.Etc2Rgb8A1UNorm => Compressed(0x9276, profile, MobileOnly),
        PixelFormat.Etc2Rgba8UNorm => Compressed(0x9278, profile, MobileOnly),
        PixelFormat.Astc4X4UNorm => Compressed(0x93B0, profile, MobileOnly),
        PixelFormat.Astc4X4UNormSrgb => Compressed(0x93D0, profile, MobileOnly),
        PixelFormat.Astc8X8UNorm => Compressed(0x93B7, profile, MobileOnly),
        PixelFormat.Astc8X8UNormSrgb => Compressed(0x93D7, profile, MobileOnly),

        _ => throw Unsupported(format, profile, "no OpenGL equivalent")
    };

    /// <summary>Which framebuffer attachment point a format belongs at.</summary>
    /// <param name="format">The format.</param>
    /// <param name="index">Which colour attachment, for a colour format.</param>
    public static uint Attachment(PixelFormat format, int index = 0) {
        if (format.HasDepth() && format.HasStencil()) {
            return GlConstants.DepthStencilAttachment;
        }

        if (format.HasDepth()) {
            return GlConstants.DepthAttachment;
        }

        return format.HasStencil()
            ? GlConstants.StencilAttachment
            : GlConstants.ColourAttachment0 + (uint)index;
    }

    /// <summary>Whether a format's texels are read back as integers rather than floats.</summary>
    /// <remarks>
    ///     Which decides between <c>glClearBufferfv</c> and <c>glClearBufferiv</c>. Clearing an
    ///     integer attachment with the float entry point is undefined and, on most drivers, silently
    ///     writes zero.
    /// </remarks>
    public static bool IsInteger(PixelFormat format) =>
        format is PixelFormat.R8UInt or PixelFormat.R8SInt or PixelFormat.R16UInt
            or PixelFormat.R32UInt or PixelFormat.Rgba32UInt;

    const string DesktopOnly = "BC texture compression is desktop-only";
    const string MobileOnly = "ETC2 and ASTC are GLES-only in core";

    static GlFormat Compressed(uint sized, GlProfile profile, string restriction) {
        var desktop = restriction == DesktopOnly;

        if (desktop == (profile >= GlProfile.Core45)) {
            return new(sized, sized, 0, false);
        }

        throw new NotSupportedException(
            $"This texture's compressed format is unavailable on {profile}: {restriction}. The content "
            + "build produces the format each target wants; a runtime that hit this loaded the wrong one."
        );
    }

    static NotSupportedException Unsupported(PixelFormat format, GlProfile profile, string why) =>
        new($"PixelFormat.{format} is unavailable on {profile}: {why}.");
}
