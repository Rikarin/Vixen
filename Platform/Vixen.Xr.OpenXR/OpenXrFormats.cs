// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Xr.OpenXR;

/// <summary>The formats an XR swapchain can be, as Vulkan numbers.</summary>
/// <remarks>
///     <para>
///         <b>An XR swapchain's format is a <c>VkFormat</c> as an <c>int64</c>.</b> OpenXR does not
///         have formats of its own: it enumerates whatever the graphics API's are, so the numbers
///         here are Vulkan's enumerators written out. They are constants rather than a reference to
///         <c>Silk.NET.Vulkan</c> because this module is deliberately not a Vulkan module — it hands
///         raw handles about and never touches the API.
///     </para>
///     <para>
///         <b>sRGB, and this is the whole colour-management story.</b> A compositor blends in linear
///         space and expects the images it is given to say what they are, so an eye buffer in a
///         UNORM format is one the runtime will treat as linear and display too dark. The engine
///         renders to an sRGB target for exactly this reason everywhere else too.
///     </para>
/// </remarks>
static class OpenXrFormats {
    // VkFormat, from vulkan_core.h. Written out rather than referenced, see above.
    const long R8G8B8A8Unorm = 37;
    const long R8G8B8A8Srgb = 43;
    const long B8G8R8A8Unorm = 44;
    const long B8G8R8A8Srgb = 50;
    const long R16G16B16A16Sfloat = 97;
    const long D16Unorm = 124;
    const long D32Sfloat = 126;
    const long D24UnormS8Uint = 129;

    /// <summary>Which Vulkan format a pixel format is, or <c>0</c> if it has no direct equivalent.</summary>
    /// <param name="format">The engine's format.</param>
    /// <returns>The Vulkan enumerator.</returns>
    public static long ToVulkan(PixelFormat format) => format switch {
        PixelFormat.Rgba8UNorm => R8G8B8A8Unorm,
        PixelFormat.Rgba8UNormSrgb => R8G8B8A8Srgb,
        PixelFormat.Bgra8UNorm => B8G8R8A8Unorm,
        PixelFormat.Bgra8UNormSrgb => B8G8R8A8Srgb,
        PixelFormat.Rgba16Float => R16G16B16A16Sfloat,
        PixelFormat.Depth16UNorm => D16Unorm,
        PixelFormat.Depth32Float => D32Sfloat,
        PixelFormat.Depth24UNormStencil8 => D24UnormS8Uint,
        _ => 0
    };

    /// <summary>Which pixel format a Vulkan enumerator is, or <see cref="PixelFormat.Undefined" />.</summary>
    /// <param name="format">The Vulkan enumerator.</param>
    /// <returns>The engine's format.</returns>
    public static PixelFormat FromVulkan(long format) => format switch {
        R8G8B8A8Unorm => PixelFormat.Rgba8UNorm,
        R8G8B8A8Srgb => PixelFormat.Rgba8UNormSrgb,
        B8G8R8A8Unorm => PixelFormat.Bgra8UNorm,
        B8G8R8A8Srgb => PixelFormat.Bgra8UNormSrgb,
        R16G16B16A16Sfloat => PixelFormat.Rgba16Float,
        D16Unorm => PixelFormat.Depth16UNorm,
        D32Sfloat => PixelFormat.Depth32Float,
        D24UnormS8Uint => PixelFormat.Depth24UNormStencil8,
        _ => PixelFormat.Undefined
    };

    /// <summary>Picks a format for a swapchain out of what the runtime offers.</summary>
    /// <param name="wanted">What the caller asked for.</param>
    /// <param name="offered">Every format the runtime will accept, in its own order of preference.</param>
    /// <param name="chosen">What was picked.</param>
    /// <returns>Whether what was picked is what was asked for.</returns>
    /// <remarks>
    ///     <para>
    ///         The runtime's list is ordered by <em>its</em> preference, and that order is worth
    ///         respecting: it is the compositor telling you which format it can present without a
    ///         conversion pass. So the fallback is the first offered format this engine understands,
    ///         rather than the first one this engine would have chosen.
    ///     </para>
    ///     <para>
    ///         A depth format is never picked as a fallback for a colour request. It would be
    ///         accepted, render nothing visible, and take a while to work out.
    ///     </para>
    /// </remarks>
    public static bool TryPick(PixelFormat wanted, ReadOnlySpan<long> offered, out long chosen) {
        var target = ToVulkan(wanted);

        foreach (var format in offered) {
            if (format == target && target != 0) {
                chosen = format;

                return true;
            }
        }

        foreach (var format in offered) {
            var known = FromVulkan(format);

            if (known != PixelFormat.Undefined && !known.IsDepthStencil()) {
                chosen = format;

                return false;
            }
        }

        chosen = offered.IsEmpty ? 0 : offered[0];

        return false;
    }
}
