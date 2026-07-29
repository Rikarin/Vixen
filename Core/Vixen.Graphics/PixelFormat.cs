// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>A texture or attachment format.</summary>
/// <remarks>
///     <para>
///         The set every backend can express, named the way Vulkan and WebGPU name them: channels,
///         then bit depth, then interpretation. <c>Rgba8UNorm</c> is four eight-bit channels read as
///         <c>[0, 1]</c>; <c>Rgba8UNormSrgb</c> is the same bits with the hardware doing the sRGB
///         conversion on read and write.
///     </para>
///     <para>
///         Deliberately not exhaustive. Every format here is one the engine uses or the swapchain
///         may hand us; a backend that supports more is not obliged to expose them, and a format
///         nobody has a use for is a format nobody has tested. <see cref="PixelFormats" /> answers
///         the questions the RHI actually asks — how big is a block, is it depth, is it sRGB — and
///         a backend translates by table lookup.
///     </para>
/// </remarks>
public enum PixelFormat : ushort {
    /// <summary>No format. Not a valid attachment or texture format.</summary>
    Undefined = 0,

    // ── 8-bit ───────────────────────────────────────────────────────────────────────────────

    /// <summary>One 8-bit channel, <c>[0, 1]</c>.</summary>
    R8UNorm = 1,

    /// <summary>One 8-bit channel, <c>[-1, 1]</c>.</summary>
    R8SNorm = 2,

    /// <summary>One 8-bit channel as an unsigned integer.</summary>
    R8UInt = 3,

    /// <summary>One 8-bit channel as a signed integer.</summary>
    R8SInt = 4,

    // ── 16-bit ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Two 8-bit channels, <c>[0, 1]</c>.</summary>
    Rg8UNorm = 10,

    /// <summary>Two 8-bit channels, <c>[-1, 1]</c>.</summary>
    Rg8SNorm = 11,

    /// <summary>One 16-bit channel as a half float.</summary>
    R16Float = 12,

    /// <summary>One 16-bit channel as an unsigned integer.</summary>
    R16UInt = 13,

    /// <summary>One 16-bit channel, <c>[0, 1]</c>.</summary>
    R16UNorm = 14,

    // ── 32-bit ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Four 8-bit channels, <c>[0, 1]</c>.</summary>
    Rgba8UNorm = 20,

    /// <summary>Four 8-bit channels, <c>[0, 1]</c>, sRGB-encoded.</summary>
    Rgba8UNormSrgb = 21,

    /// <summary>Four 8-bit channels in blue-green-red-alpha order, <c>[0, 1]</c>.</summary>
    /// <remarks>What a Windows swapchain usually hands back, which is why it is named rather than
    /// swizzled at upload.</remarks>
    Bgra8UNorm = 22,

    /// <summary>Four 8-bit channels in blue-green-red-alpha order, sRGB-encoded.</summary>
    Bgra8UNormSrgb = 23,

    /// <summary>Four 8-bit channels, <c>[-1, 1]</c>.</summary>
    Rgba8SNorm = 24,

    /// <summary>Two 16-bit channels as half floats.</summary>
    Rg16Float = 25,

    /// <summary>One 32-bit channel as a float.</summary>
    R32Float = 26,

    /// <summary>One 32-bit channel as an unsigned integer.</summary>
    R32UInt = 27,

    /// <summary>Ten bits each for red, green and blue and two for alpha, <c>[0, 1]</c>.</summary>
    /// <remarks>The HDR swapchain format on every desktop, and the usual choice for a normal buffer
    /// where the extra precision earns its place.</remarks>
    Rgb10A2UNorm = 28,

    /// <summary>Eleven bits each for red and green and ten for blue, as small floats.</summary>
    /// <remarks>The standard HDR colour target: no alpha, no sign, and half the memory of
    /// <see cref="Rgba16Float" />.</remarks>
    Rg11B10Float = 29,

    // ── 64-bit ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Four 16-bit channels as half floats.</summary>
    Rgba16Float = 40,

    /// <summary>Two 32-bit channels as floats.</summary>
    Rg32Float = 41,

    /// <summary>Four 16-bit channels, <c>[0, 1]</c>.</summary>
    Rgba16UNorm = 42,

    // ── 128-bit ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Four 32-bit channels as floats.</summary>
    Rgba32Float = 50,

    /// <summary>Four 32-bit channels as unsigned integers.</summary>
    Rgba32UInt = 51,

    // ── Depth and stencil ───────────────────────────────────────────────────────────────────

    /// <summary>16-bit unsigned normalised depth.</summary>
    Depth16UNorm = 60,

    /// <summary>32-bit float depth.</summary>
    /// <remarks>
    ///     The engine's default, with a reversed-Z projection — see
    ///     <c>Vixen.Core.Mathematics/Conventions.md</c>. A float32 depth buffer with near and far
    ///     swapped distributes precision far better than the 24-bit fixed-point alternative, and the
    ///     cost is that the depth comparison runs the other way round, which is a convention rather
    ///     than a per-site decision.
    /// </remarks>
    Depth32Float = 61,

    /// <summary>24-bit unsigned normalised depth with an 8-bit stencil.</summary>
    Depth24UNormStencil8 = 62,

    /// <summary>32-bit float depth with an 8-bit stencil.</summary>
    Depth32FloatStencil8 = 63,

    // ── Block compression ───────────────────────────────────────────────────────────────────

    /// <summary>BC1 — colour with one bit of alpha, 0.5 bytes per pixel.</summary>
    Bc1RgbaUNorm = 80,

    /// <summary>BC1, sRGB-encoded.</summary>
    Bc1RgbaUNormSrgb = 81,

    /// <summary>BC3 — colour with interpolated alpha, 1 byte per pixel.</summary>
    Bc3RgbaUNorm = 82,

    /// <summary>BC3, sRGB-encoded.</summary>
    Bc3RgbaUNormSrgb = 83,

    /// <summary>BC4 — one channel, 0.5 bytes per pixel.</summary>
    Bc4RUNorm = 84,

    /// <summary>BC5 — two channels, 1 byte per pixel. The normal-map format.</summary>
    Bc5RgUNorm = 85,

    /// <summary>BC6H — three HDR channels, 1 byte per pixel.</summary>
    Bc6HRgbUFloat = 86,

    /// <summary>BC7 — high-quality colour and alpha, 1 byte per pixel.</summary>
    Bc7RgbaUNorm = 87,

    /// <summary>BC7, sRGB-encoded.</summary>
    Bc7RgbaUNormSrgb = 88,

    /// <summary>ETC2 — colour with one bit of alpha, 0.5 bytes per pixel. Mobile.</summary>
    Etc2Rgb8A1UNorm = 100,

    /// <summary>ETC2 with full alpha, 1 byte per pixel. Mobile.</summary>
    Etc2Rgba8UNorm = 101,

    /// <summary>ASTC with a 4×4 block, 1 byte per pixel. Mobile.</summary>
    Astc4X4UNorm = 110,

    /// <summary>ASTC with a 4×4 block, sRGB-encoded.</summary>
    Astc4X4UNormSrgb = 111,

    /// <summary>ASTC with an 8×8 block, 0.25 bytes per pixel.</summary>
    Astc8X8UNorm = 112,

    /// <summary>ASTC with an 8×8 block, sRGB-encoded.</summary>
    Astc8X8UNormSrgb = 113
}

/// <summary>What the RHI needs to know about a <see cref="PixelFormat" />.</summary>
/// <remarks>
///     A lookup rather than a switch at every call site. Every one of these questions is asked while
///     validating a copy, sizing a staging buffer or choosing an attachment layout, and getting
///     block size wrong is the bug where a compressed texture uploads as a quarter of an image.
/// </remarks>
public static class PixelFormats {
    /// <summary>Bytes per block — per pixel for uncompressed formats.</summary>
    /// <param name="format">The format.</param>
    /// <returns><c>0</c> for <see cref="PixelFormat.Undefined" />.</returns>
    public static int BlockSize(this PixelFormat format) => format switch {
        PixelFormat.R8UNorm or PixelFormat.R8SNorm or PixelFormat.R8UInt or PixelFormat.R8SInt => 1,

        PixelFormat.Rg8UNorm or PixelFormat.Rg8SNorm or PixelFormat.R16Float or PixelFormat.R16UInt
            or PixelFormat.R16UNorm or PixelFormat.Depth16UNorm => 2,

        PixelFormat.Depth24UNormStencil8 => 4,

        PixelFormat.Rgba8UNorm or PixelFormat.Rgba8UNormSrgb or PixelFormat.Bgra8UNorm
            or PixelFormat.Bgra8UNormSrgb or PixelFormat.Rgba8SNorm or PixelFormat.Rg16Float
            or PixelFormat.R32Float or PixelFormat.R32UInt or PixelFormat.Rgb10A2UNorm
            or PixelFormat.Rg11B10Float or PixelFormat.Depth32Float => 4,

        // Five bytes of data in an eight-byte slot on every real implementation, and it is the
        // allocation size that matters here rather than the useful bits.
        PixelFormat.Depth32FloatStencil8 => 8,

        PixelFormat.Rgba16Float or PixelFormat.Rg32Float or PixelFormat.Rgba16UNorm => 8,

        PixelFormat.Rgba32Float or PixelFormat.Rgba32UInt => 16,

        // 4×4 blocks at half a byte per pixel.
        PixelFormat.Bc1RgbaUNorm or PixelFormat.Bc1RgbaUNormSrgb or PixelFormat.Bc4RUNorm
            or PixelFormat.Etc2Rgb8A1UNorm => 8,

        // 4×4 blocks at a byte per pixel.
        PixelFormat.Bc3RgbaUNorm or PixelFormat.Bc3RgbaUNormSrgb or PixelFormat.Bc5RgUNorm
            or PixelFormat.Bc6HRgbUFloat or PixelFormat.Bc7RgbaUNorm or PixelFormat.Bc7RgbaUNormSrgb
            or PixelFormat.Etc2Rgba8UNorm or PixelFormat.Astc4X4UNorm or PixelFormat.Astc4X4UNormSrgb
            or PixelFormat.Astc8X8UNorm or PixelFormat.Astc8X8UNormSrgb => 16,

        _ => 0
    };

    /// <summary>How many pixels wide and tall one block covers.</summary>
    /// <param name="format">The format.</param>
    /// <returns><c>(1, 1)</c> for uncompressed formats.</returns>
    public static (int Width, int Height) BlockExtent(this PixelFormat format) => format switch {
        PixelFormat.Astc8X8UNorm or PixelFormat.Astc8X8UNormSrgb => (8, 8),
        _ when format.IsCompressed() => (4, 4),
        _ => (1, 1)
    };

    /// <summary>Whether the format is block-compressed.</summary>
    public static bool IsCompressed(this PixelFormat format) => format >= PixelFormat.Bc1RgbaUNorm;

    /// <summary>Whether the format carries depth.</summary>
    public static bool HasDepth(this PixelFormat format) =>
        format is PixelFormat.Depth16UNorm or PixelFormat.Depth32Float
            or PixelFormat.Depth24UNormStencil8 or PixelFormat.Depth32FloatStencil8;

    /// <summary>Whether the format carries stencil.</summary>
    public static bool HasStencil(this PixelFormat format) =>
        format is PixelFormat.Depth24UNormStencil8 or PixelFormat.Depth32FloatStencil8;

    /// <summary>Whether the format is a depth or stencil format, and therefore not a colour one.</summary>
    public static bool IsDepthStencil(this PixelFormat format) => format.HasDepth() || format.HasStencil();

    /// <summary>How a shader reads a texture in this format, for the layout that declares it.</summary>
    /// <param name="format">The format.</param>
    /// <remarks>
    ///     <para>
    ///         What to put in a <see cref="DescriptorBinding.SampleType" /> when the texture is one
    ///         the caller already has — a render-graph resource, a shadow atlas — rather than one it
    ///         is describing from a shader's declaration.
    ///     </para>
    ///     <para>
    ///         Never <see cref="DescriptorSampleType.UnfilterableFloat" />, because that is not a
    ///         property of the format: a 32-bit float texture is filterable on a device that has the
    ///         feature and not on one that does not. So this answers with what the format is, and a
    ///         caller targeting a device without the feature declares the narrower type itself.
    ///     </para>
    /// </remarks>
    public static DescriptorSampleType SampleTypeOf(this PixelFormat format) {
        if (format.HasDepth()) {
            return DescriptorSampleType.Depth;
        }

        return format switch {
            PixelFormat.R8UInt or PixelFormat.R16UInt or PixelFormat.R32UInt or PixelFormat.Rgba32UInt =>
                DescriptorSampleType.UInt,
            PixelFormat.R8SInt => DescriptorSampleType.SInt,
            _ => DescriptorSampleType.Float
        };
    }

    /// <summary>Whether a texture in this format may be read through a binding declared that way.</summary>
    /// <param name="declared">What the descriptor layout says the binding holds.</param>
    /// <param name="format">The format of the view being bound.</param>
    /// <remarks>
    ///     Float and unfilterable float accept the same formats and differ only in what a sampler may
    ///     do with them, which is the sampler binding's business rather than the texture's — so this
    ///     answers the question a backend has at bind time, and no more of it than that.
    /// </remarks>
    public static bool Accepts(this DescriptorSampleType declared, PixelFormat format) => declared switch {
        DescriptorSampleType.Float or DescriptorSampleType.UnfilterableFloat =>
            format.SampleTypeOf() == DescriptorSampleType.Float,
        _ => format.SampleTypeOf() == declared
    };

    /// <summary>Whether the hardware converts to and from sRGB on access.</summary>
    /// <remarks>
    ///     The whole of colour correctness in one flag: shading happens in linear space, so a colour
    ///     texture must be sRGB and a normal map, roughness map or mask must not be. Getting it
    ///     wrong produces an image that looks washed out or crushed and is famously hard to diagnose
    ///     by eye.
    /// </remarks>
    public static bool IsSrgb(this PixelFormat format) =>
        format is PixelFormat.Rgba8UNormSrgb or PixelFormat.Bgra8UNormSrgb
            or PixelFormat.Bc1RgbaUNormSrgb or PixelFormat.Bc3RgbaUNormSrgb
            or PixelFormat.Bc7RgbaUNormSrgb or PixelFormat.Astc4X4UNormSrgb
            or PixelFormat.Astc8X8UNormSrgb;

    /// <summary>The sRGB form of a format, or the format itself if it has none.</summary>
    public static PixelFormat ToSrgb(this PixelFormat format) => format switch {
        PixelFormat.Rgba8UNorm => PixelFormat.Rgba8UNormSrgb,
        PixelFormat.Bgra8UNorm => PixelFormat.Bgra8UNormSrgb,
        PixelFormat.Bc1RgbaUNorm => PixelFormat.Bc1RgbaUNormSrgb,
        PixelFormat.Bc3RgbaUNorm => PixelFormat.Bc3RgbaUNormSrgb,
        PixelFormat.Bc7RgbaUNorm => PixelFormat.Bc7RgbaUNormSrgb,
        PixelFormat.Astc4X4UNorm => PixelFormat.Astc4X4UNormSrgb,
        PixelFormat.Astc8X8UNorm => PixelFormat.Astc8X8UNormSrgb,
        _ => format
    };

    /// <summary>The linear form of a format, or the format itself if it is already linear.</summary>
    public static PixelFormat ToLinear(this PixelFormat format) => format switch {
        PixelFormat.Rgba8UNormSrgb => PixelFormat.Rgba8UNorm,
        PixelFormat.Bgra8UNormSrgb => PixelFormat.Bgra8UNorm,
        PixelFormat.Bc1RgbaUNormSrgb => PixelFormat.Bc1RgbaUNorm,
        PixelFormat.Bc3RgbaUNormSrgb => PixelFormat.Bc3RgbaUNorm,
        PixelFormat.Bc7RgbaUNormSrgb => PixelFormat.Bc7RgbaUNorm,
        PixelFormat.Astc4X4UNormSrgb => PixelFormat.Astc4X4UNorm,
        PixelFormat.Astc8X8UNormSrgb => PixelFormat.Astc8X8UNorm,
        _ => format
    };

    /// <summary>How many bytes one mip level of a texture occupies, tightly packed.</summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The level's width in pixels.</param>
    /// <param name="height">The level's height in pixels.</param>
    /// <param name="depth">The level's depth in slices.</param>
    /// <returns>The size in bytes.</returns>
    /// <remarks>
    ///     Rounds up to whole blocks, which is the step everyone forgets: a 5×5 BC7 mip is two
    ///     blocks by two blocks, not 25 pixels' worth, and treating it as the latter truncates the
    ///     bottom-right corner of every non-multiple-of-four texture.
    /// </remarks>
    public static long LevelSize(this PixelFormat format, int width, int height, int depth = 1) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        var (blockWidth, blockHeight) = format.BlockExtent();
        var columns = (width + blockWidth - 1) / blockWidth;
        var rows = (height + blockHeight - 1) / blockHeight;

        return (long)columns * rows * depth * format.BlockSize();
    }

    /// <summary>How many mip levels a texture of this size can have, down to 1×1.</summary>
    /// <param name="width">The base width.</param>
    /// <param name="height">The base height.</param>
    /// <param name="depth">The base depth.</param>
    public static int MipLevelCount(int width, int height, int depth = 1) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        var largest = Math.Max(width, Math.Max(height, depth));
        return (int)Math.Floor(Math.Log2(largest)) + 1;
    }
}
