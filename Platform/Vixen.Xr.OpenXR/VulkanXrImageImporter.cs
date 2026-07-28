// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Vulkan;

namespace Vixen.Xr.OpenXR;

/// <summary>Adopts the runtime's images into the engine's Vulkan device.</summary>
/// <remarks>
///     <para>
///         Twenty lines, and the only place in this module that knows what a <c>VkImage</c> is for.
///         It exists as a separate type rather than as a method on the session because the seam it
///         implements — <see cref="IXrImageImporter" /> — is what keeps <c>Vixen.Xr</c> free of both
///         Vulkan and OpenXR, and what a second backend or a test plugs into instead.
///     </para>
///     <para>
///         <b>Releasing a handle does not destroy the image.</b> The image belongs to the compositor;
///         what the RHI holds is a table entry pointing at it, and <c>VulkanDevice</c> adopts it
///         exactly as it adopts a window swapchain's images.
///     </para>
/// </remarks>
/// <param name="device">The device to adopt into.</param>
public sealed class VulkanXrImageImporter(VulkanDevice device) : IXrImageImporter {
    /// <inheritdoc />
    public TextureHandle Import(nint nativeImage, in TextureDescription description) =>
        device.ImportImage(nativeImage, in description);

    /// <inheritdoc />
    public TextureViewHandle CreateView(TextureHandle texture) => device.CreateTextureView(texture);

    /// <inheritdoc />
    public void Release(TextureHandle texture) => device.Destroy(texture);

    /// <inheritdoc />
    public void Release(TextureViewHandle view) => device.Destroy(view);
}
