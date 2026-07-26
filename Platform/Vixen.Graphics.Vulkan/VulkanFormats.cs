// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Vixen.Graphics.Vulkan;

/// <summary>The RHI's vocabulary in Vulkan's terms.</summary>
/// <remarks>
///     <para>
///         Pure functions with no <c>VkDevice</c> in sight, and deliberately so: this is the half of
///         a backend that can be tested without a driver, and it is also the half where a mistake is
///         silent. A format mapped to the wrong Vulkan enum does not fail — it renders the wrong
///         colours, or samples a normal map as sRGB, and the bug is found by eye weeks later.
///     </para>
///     <para>
///         Every mapping is asserted in both directions by the test suite, which runs on a machine
///         with no Vulkan at all.
///     </para>
/// </remarks>
static class VulkanFormats {
    /// <summary>The Vulkan format for one of ours.</summary>
    /// <param name="format">The engine format.</param>
    /// <returns><see cref="VkFormat.Undefined" /> for a format Vulkan does not have.</returns>
    public static VkFormat ToVulkan(PixelFormat format) => format switch {
        PixelFormat.R8UNorm => VkFormat.R8Unorm,
        PixelFormat.R8SNorm => VkFormat.R8SNorm,
        PixelFormat.R8UInt => VkFormat.R8Uint,
        PixelFormat.R8SInt => VkFormat.R8Sint,

        PixelFormat.Rg8UNorm => VkFormat.R8G8Unorm,
        PixelFormat.Rg8SNorm => VkFormat.R8G8SNorm,
        PixelFormat.R16Float => VkFormat.R16Sfloat,
        PixelFormat.R16UInt => VkFormat.R16Uint,
        PixelFormat.R16UNorm => VkFormat.R16Unorm,

        PixelFormat.Rgba8UNorm => VkFormat.R8G8B8A8Unorm,
        PixelFormat.Rgba8UNormSrgb => VkFormat.R8G8B8A8Srgb,
        PixelFormat.Bgra8UNorm => VkFormat.B8G8R8A8Unorm,
        PixelFormat.Bgra8UNormSrgb => VkFormat.B8G8R8A8Srgb,
        PixelFormat.Rgba8SNorm => VkFormat.R8G8B8A8SNorm,
        PixelFormat.Rg16Float => VkFormat.R16G16Sfloat,
        PixelFormat.R32Float => VkFormat.R32Sfloat,
        PixelFormat.R32UInt => VkFormat.R32Uint,

        // Packed formats read their components in the opposite order from the unpacked ones, which
        // is why this is A2B10G10R10 rather than A2R10G10B10: the engine's channel order is RGBA and
        // Vulkan names a packed format from its most significant bits down.
        PixelFormat.Rgb10A2UNorm => VkFormat.A2B10G10R10UnormPack32,
        PixelFormat.Rg11B10Float => VkFormat.B10G11R11UfloatPack32,

        PixelFormat.Rgba16Float => VkFormat.R16G16B16A16Sfloat,
        PixelFormat.Rg32Float => VkFormat.R32G32Sfloat,
        PixelFormat.Rgba16UNorm => VkFormat.R16G16B16A16Unorm,

        PixelFormat.Rgba32Float => VkFormat.R32G32B32A32Sfloat,
        PixelFormat.Rgba32UInt => VkFormat.R32G32B32A32Uint,

        PixelFormat.Depth16UNorm => VkFormat.D16Unorm,
        PixelFormat.Depth32Float => VkFormat.D32Sfloat,
        PixelFormat.Depth24UNormStencil8 => VkFormat.D24UnormS8Uint,
        PixelFormat.Depth32FloatStencil8 => VkFormat.D32SfloatS8Uint,

        PixelFormat.Bc1RgbaUNorm => VkFormat.BC1RgbaUnormBlock,
        PixelFormat.Bc1RgbaUNormSrgb => VkFormat.BC1RgbaSrgbBlock,
        PixelFormat.Bc3RgbaUNorm => VkFormat.BC3UnormBlock,
        PixelFormat.Bc3RgbaUNormSrgb => VkFormat.BC3SrgbBlock,
        PixelFormat.Bc4RUNorm => VkFormat.BC4UnormBlock,
        PixelFormat.Bc5RgUNorm => VkFormat.BC5UnormBlock,
        PixelFormat.Bc6HRgbUFloat => VkFormat.BC6HUfloatBlock,
        PixelFormat.Bc7RgbaUNorm => VkFormat.BC7UnormBlock,
        PixelFormat.Bc7RgbaUNormSrgb => VkFormat.BC7SrgbBlock,

        PixelFormat.Etc2Rgb8A1UNorm => VkFormat.Etc2R8G8B8A1UnormBlock,
        PixelFormat.Etc2Rgba8UNorm => VkFormat.Etc2R8G8B8A8UnormBlock,

        PixelFormat.Astc4X4UNorm => VkFormat.Astc4x4UnormBlock,
        PixelFormat.Astc4X4UNormSrgb => VkFormat.Astc4x4SrgbBlock,
        PixelFormat.Astc8X8UNorm => VkFormat.Astc8x8UnormBlock,
        PixelFormat.Astc8X8UNormSrgb => VkFormat.Astc8x8SrgbBlock,

        _ => VkFormat.Undefined
    };

    /// <summary>The engine format for one of Vulkan's.</summary>
    /// <param name="format">The Vulkan format.</param>
    /// <returns>
    ///     <see cref="PixelFormat.Undefined" /> for a format the engine does not name — which is most
    ///     of them, and is fine: a swapchain that offers only formats we do not know is a swapchain
    ///     we decline rather than one we guess at.
    /// </returns>
    public static PixelFormat FromVulkan(VkFormat format) => format switch {
        VkFormat.R8Unorm => PixelFormat.R8UNorm,
        VkFormat.R8SNorm => PixelFormat.R8SNorm,
        VkFormat.R8Uint => PixelFormat.R8UInt,
        VkFormat.R8Sint => PixelFormat.R8SInt,

        VkFormat.R8G8Unorm => PixelFormat.Rg8UNorm,
        VkFormat.R8G8SNorm => PixelFormat.Rg8SNorm,
        VkFormat.R16Sfloat => PixelFormat.R16Float,
        VkFormat.R16Uint => PixelFormat.R16UInt,
        VkFormat.R16Unorm => PixelFormat.R16UNorm,

        VkFormat.R8G8B8A8Unorm => PixelFormat.Rgba8UNorm,
        VkFormat.R8G8B8A8Srgb => PixelFormat.Rgba8UNormSrgb,
        VkFormat.B8G8R8A8Unorm => PixelFormat.Bgra8UNorm,
        VkFormat.B8G8R8A8Srgb => PixelFormat.Bgra8UNormSrgb,
        VkFormat.R8G8B8A8SNorm => PixelFormat.Rgba8SNorm,
        VkFormat.R16G16Sfloat => PixelFormat.Rg16Float,
        VkFormat.R32Sfloat => PixelFormat.R32Float,
        VkFormat.R32Uint => PixelFormat.R32UInt,

        VkFormat.A2B10G10R10UnormPack32 => PixelFormat.Rgb10A2UNorm,
        VkFormat.B10G11R11UfloatPack32 => PixelFormat.Rg11B10Float,

        VkFormat.R16G16B16A16Sfloat => PixelFormat.Rgba16Float,
        VkFormat.R32G32Sfloat => PixelFormat.Rg32Float,
        VkFormat.R16G16B16A16Unorm => PixelFormat.Rgba16UNorm,

        VkFormat.R32G32B32A32Sfloat => PixelFormat.Rgba32Float,
        VkFormat.R32G32B32A32Uint => PixelFormat.Rgba32UInt,

        VkFormat.D16Unorm => PixelFormat.Depth16UNorm,
        VkFormat.D32Sfloat => PixelFormat.Depth32Float,
        VkFormat.D24UnormS8Uint => PixelFormat.Depth24UNormStencil8,
        VkFormat.D32SfloatS8Uint => PixelFormat.Depth32FloatStencil8,

        VkFormat.BC1RgbaUnormBlock => PixelFormat.Bc1RgbaUNorm,
        VkFormat.BC1RgbaSrgbBlock => PixelFormat.Bc1RgbaUNormSrgb,
        VkFormat.BC3UnormBlock => PixelFormat.Bc3RgbaUNorm,
        VkFormat.BC3SrgbBlock => PixelFormat.Bc3RgbaUNormSrgb,
        VkFormat.BC4UnormBlock => PixelFormat.Bc4RUNorm,
        VkFormat.BC5UnormBlock => PixelFormat.Bc5RgUNorm,
        VkFormat.BC6HUfloatBlock => PixelFormat.Bc6HRgbUFloat,
        VkFormat.BC7UnormBlock => PixelFormat.Bc7RgbaUNorm,
        VkFormat.BC7SrgbBlock => PixelFormat.Bc7RgbaUNormSrgb,

        VkFormat.Etc2R8G8B8A1UnormBlock => PixelFormat.Etc2Rgb8A1UNorm,
        VkFormat.Etc2R8G8B8A8UnormBlock => PixelFormat.Etc2Rgba8UNorm,

        VkFormat.Astc4x4UnormBlock => PixelFormat.Astc4X4UNorm,
        VkFormat.Astc4x4SrgbBlock => PixelFormat.Astc4X4UNormSrgb,
        VkFormat.Astc8x8UnormBlock => PixelFormat.Astc8X8UNorm,
        VkFormat.Astc8x8SrgbBlock => PixelFormat.Astc8X8UNormSrgb,

        _ => PixelFormat.Undefined
    };

    /// <summary>Which parts of a texture a barrier or view refers to.</summary>
    /// <param name="format">The format, which decides whether it has depth, stencil or colour.</param>
    public static ImageAspectFlags AspectOf(PixelFormat format) {
        if (!format.IsDepthStencil()) {
            return ImageAspectFlags.ColorBit;
        }

        var aspect = format.HasDepth() ? ImageAspectFlags.DepthBit : ImageAspectFlags.None;
        return format.HasStencil() ? aspect | ImageAspectFlags.StencilBit : aspect;
    }

    /// <summary>The sample-count flag for a count.</summary>
    /// <param name="samples">A power of two.</param>
    public static SampleCountFlags ToSampleCount(int samples) => samples switch {
        1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        16 => SampleCountFlags.Count16Bit,
        32 => SampleCountFlags.Count32Bit,
        64 => SampleCountFlags.Count64Bit,
        _ => SampleCountFlags.Count1Bit
    };

    /// <summary>The mask of sample counts a device supports, in the RHI's bit-per-power-of-two
    /// form.</summary>
    /// <param name="flags">What the device reported.</param>
    public static int FromSampleCounts(SampleCountFlags flags) {
        var mask = 0;

        if ((flags & SampleCountFlags.Count1Bit) != 0) {
            mask |= 1 << 0;
        }

        if ((flags & SampleCountFlags.Count2Bit) != 0) {
            mask |= 1 << 1;
        }

        if ((flags & SampleCountFlags.Count4Bit) != 0) {
            mask |= 1 << 2;
        }

        if ((flags & SampleCountFlags.Count8Bit) != 0) {
            mask |= 1 << 3;
        }

        if ((flags & SampleCountFlags.Count16Bit) != 0) {
            mask |= 1 << 4;
        }

        return mask;
    }
}
