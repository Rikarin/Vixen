// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>The <c>VkFormat</c> numbers a KTX2 header carries, and what they mean here.</summary>
/// <remarks>
///     <para>
///         A table in this assembly rather than a reference to the Vulkan backend, because
///         <c>Vixen.Core.Imaging</c> is a Core assembly and <c>Vixen.Graphics.Vulkan</c> is a
///         Platform one — and because these numbers are part of the <i>file format</i>, not of the
///         graphics API. A KTX2 file written on a machine with no Vulkan at all still names its
///         format this way.
///     </para>
///     <para>
///         Only the formats the engine actually ships textures in are listed. A number nobody writes
///         is a number nobody has tested, and the alternative — hundreds of entries transcribed from
///         a header — is hundreds of chances to transcribe one wrongly.
///     </para>
/// </remarks>
public static class VkFormats {
    static readonly (PixelFormat Format, uint VkFormat)[] Table = [
        (PixelFormat.R8UNorm, 9),                  // VK_FORMAT_R8_UNORM
        (PixelFormat.Rg8UNorm, 16),                // VK_FORMAT_R8G8_UNORM
        (PixelFormat.Rgba8UNorm, 37),              // VK_FORMAT_R8G8B8A8_UNORM
        (PixelFormat.Rgba8UNormSrgb, 43),          // VK_FORMAT_R8G8B8A8_SRGB
        (PixelFormat.Bgra8UNorm, 44),              // VK_FORMAT_B8G8R8A8_UNORM
        (PixelFormat.Bgra8UNormSrgb, 50),          // VK_FORMAT_B8G8R8A8_SRGB
        (PixelFormat.Rgba16Float, 97),             // VK_FORMAT_R16G16B16A16_SFLOAT
        (PixelFormat.R32Float, 100),               // VK_FORMAT_R32_SFLOAT
        (PixelFormat.Rgba32Float, 109),            // VK_FORMAT_R32G32B32A32_SFLOAT
        (PixelFormat.Bc1RgbaUNorm, 133),           // VK_FORMAT_BC1_RGBA_UNORM_BLOCK
        (PixelFormat.Bc1RgbaUNormSrgb, 134),       // VK_FORMAT_BC1_RGBA_SRGB_BLOCK
        (PixelFormat.Bc3RgbaUNorm, 137),           // VK_FORMAT_BC3_UNORM_BLOCK
        (PixelFormat.Bc3RgbaUNormSrgb, 138),       // VK_FORMAT_BC3_SRGB_BLOCK
        (PixelFormat.Bc4RUNorm, 139),              // VK_FORMAT_BC4_UNORM_BLOCK
        (PixelFormat.Bc5RgUNorm, 141),             // VK_FORMAT_BC5_UNORM_BLOCK
        (PixelFormat.Bc6HRgbUFloat, 144),          // VK_FORMAT_BC6H_UFLOAT_BLOCK
        (PixelFormat.Bc7RgbaUNorm, 145),           // VK_FORMAT_BC7_UNORM_BLOCK
        (PixelFormat.Bc7RgbaUNormSrgb, 146),       // VK_FORMAT_BC7_SRGB_BLOCK
        (PixelFormat.Etc2Rgb8A1UNorm, 151),        // VK_FORMAT_ETC2_R8G8B8A1_UNORM_BLOCK
        (PixelFormat.Etc2Rgba8UNorm, 153),         // VK_FORMAT_ETC2_R8G8B8A8_UNORM_BLOCK
        (PixelFormat.Astc4X4UNorm, 157),           // VK_FORMAT_ASTC_4x4_UNORM_BLOCK
        (PixelFormat.Astc4X4UNormSrgb, 158),       // VK_FORMAT_ASTC_4x4_SRGB_BLOCK
        (PixelFormat.Astc8X8UNorm, 171),           // VK_FORMAT_ASTC_8x8_UNORM_BLOCK
        (PixelFormat.Astc8X8UNormSrgb, 172)        // VK_FORMAT_ASTC_8x8_SRGB_BLOCK
    ];

    /// <summary>The Vulkan number for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Its number.</returns>
    /// <exception cref="Ktx2Exception">Nothing here writes that format.</exception>
    public static uint From(PixelFormat format) {
        foreach (var (candidate, vkFormat) in Table) {
            if (candidate == format) {
                return vkFormat;
            }
        }

        throw new Ktx2Exception(
            $"{format} has no VkFormat number here. Add it to VkFormats.Table if a build should be able to "
            + "ship textures in it."
        );
    }

    /// <summary>What a Vulkan number means.</summary>
    /// <param name="vkFormat">The number.</param>
    /// <returns>The format.</returns>
    /// <exception cref="Ktx2Exception">Nothing here reads that number.</exception>
    public static PixelFormat To(uint vkFormat) {
        foreach (var (format, candidate) in Table) {
            if (candidate == vkFormat) {
                return format;
            }
        }

        throw new Ktx2Exception(
            $"VkFormat {vkFormat} is not a format this engine reads. The file was probably written for another "
            + "renderer."
        );
    }

    /// <summary>Every format that can be written.</summary>
    public static IEnumerable<PixelFormat> Supported => Table.Select(entry => entry.Format);
}
