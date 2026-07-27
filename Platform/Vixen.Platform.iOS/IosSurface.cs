// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Ios;

/// <summary>The <c>CAMetalLayer</c> MoltenVK presents to.</summary>
/// <remarks>
///     <para>
///         <see cref="SurfaceKind.Metal" /> carrying the layer pointer, which is exactly what
///         <c>VK_EXT_metal_surface</c>'s <c>VkMetalSurfaceCreateInfoEXT.pLayer</c> wants. The desktop
///         platform arrives at the same handle by a different road — SDL makes a Metal view and hands
///         back its layer — so the Vulkan backend needs no iOS-specific path at all.
///     </para>
///     <para>
///         <b>Read through the view every time rather than cached.</b> The layer object outlives a
///         rotation but its drawable size does not, and a cached size is the bug where the swapchain
///         is built for the orientation the application started in.
///     </para>
/// </remarks>
/// <param name="window">The window this belongs to.</param>
internal sealed class IosSurface(IosWindow window) : ISurface {
    /// <inheritdoc />
    public SurfaceHandle Handle => new(SurfaceKind.Metal, 0, window.View.MetalLayer.Handle.Handle);

    /// <inheritdoc />
    public Int2 PixelSize => window.View.DrawableSize;
}
