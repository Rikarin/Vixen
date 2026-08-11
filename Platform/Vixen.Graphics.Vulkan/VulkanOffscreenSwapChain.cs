// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.Vulkan;

/// <summary>A swapchain with nothing to present to: one image, on the real device.</summary>
/// <remarks>
///     <para>
///         <b>What a frame draws into when there is no window.</b> Vulkan opens perfectly happily
///         without a surface — <c>VulkanSurface.RequiredExtensions</c> answers
///         <see cref="Core.SurfaceKind.None" /> with no extensions at all, which is how
///         <c>Vixen.Graphics.Golden.Tests</c> has rendered on this repository's real MoltenVK device
///         for a hundred and seventy-odd fixtures. What it cannot do is build a
///         <c>VkSwapchainKHR</c>, because there is no <c>VkSurfaceKHR</c> to build one on.
///     </para>
///     <para>
///         So the frame's final colour target becomes an ordinary texture and everything upstream of
///         it is unchanged: the host acquires, the compositor imports under
///         <c>GraphicsOptions.Output</c>, the graph writes it, and <see cref="Present" /> is where
///         the picture stops instead of where it is handed to a compositor. That is the whole of the
///         difference, and keeping it behind <see cref="ISwapChain" /> is what makes a headless run
///         run the <em>same</em> frame rather than a second code path that drifts.
///     </para>
///     <para>
///         ⚠ <b>One image, deliberately, where a presenting chain has three.</b> The reason a
///         swapchain has several is that the display holds one while the GPU draws the next; nothing
///         holds this one. More images would mean a capture had to say which of them it read, and
///         two runs of the same seed could answer differently — which is the property the whole
///         headless-picture exercise exists to get rid of.
///     </para>
///     <para>
///         ⚠ <b>It is not the windowed frame, byte for byte.</b> The format is whatever was asked
///         for rather than whatever the surface offered, there is no present and therefore no
///         compositor colour management, and the size is
///         <c>GraphicsOptions.WindowlessSize</c> rather than a display's backing scale. See
///         <a href="../../docs/guide/rendering/capturing-a-frame.md">capturing a frame</a>.
///     </para>
/// </remarks>
sealed class VulkanOffscreenSwapChain : ISwapChain {
    readonly VulkanDevice device;

    TextureHandle texture;
    TextureViewHandle view;
    bool disposed;

    public VulkanOffscreenSwapChain(VulkanDevice device, in SwapChainDescription description) {
        this.device = device;
        Format = description.Format;
        Size = new(Math.Max(description.Size.X, 1), Math.Max(description.Size.Y, 1));
        PresentMode = description.PresentMode;
    }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public Int2 Size { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Reported as asked for rather than corrected to <see cref="Graphics.PresentMode.Fifo" />.
    ///     Nothing here waits for a display, so no mode is more true than another, and answering
    ///     something the caller did not ask for would read as a fallback that happened.
    /// </remarks>
    public PresentMode PresentMode { get; }

    /// <inheritdoc />
    public int ImageCount => 1;

    /// <inheritdoc />
    public TextureHandle CurrentTexture => texture;

    /// <summary>How many times <see cref="Present" /> has been called.</summary>
    /// <remarks>
    ///     The one thing a test can ask that distinguishes "the frame ran and finished" from "the
    ///     frame was abandoned", now that there is no display to look at.
    /// </remarks>
    public int PresentCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Created on the first acquire, not in the constructor.</b> A swapchain is built
    ///     before the host knows whether the frame will run at all, and a texture allocated by a
    ///     constructor that then throws is one nobody has a handle to free.
    /// </remarks>
    public SwapChainStatus AcquireNextImage(out TextureViewHandle imageView) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!texture.IsValid) {
            var description = new TextureDescription(
                Format,
                Size.X,
                Size.Y,

                // CopySource is what a capture reads through, and it is unconditional rather than
                // asked for: a texture's usage is fixed at creation, so a flag added only when
                // --vixen-capture was given would make the two runs different frames — and the
                // capture would then be a picture of a target nobody else renders into.
                //
                // CopyDestination for the same reason the presenting chain has TransferDst: a post
                // chain that ends in a full-resolution image blits into the final target rather than
                // drawing a redundant fullscreen triangle.
                TextureUsage.ColourTarget | TextureUsage.CopySource | TextureUsage.CopyDestination,
                Name: "offscreen swapchain image"
            );

            texture = device.CreateTexture(description);
            view = device.CreateTextureView(texture);
        }

        imageView = view;
        return SwapChainStatus.Ready;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing to present to and nothing to wait for. The frame is complete when the queue has
    ///     retired it, which is what a capture waits on — see <c>AppGraphics.End</c>.
    /// </remarks>
    public SwapChainStatus Present() {
        ObjectDisposedException.ThrowIf(disposed, this);
        PresentCount++;

        return SwapChainStatus.Ready;
    }

    /// <inheritdoc />
    public void Resize(Int2 size) {
        ObjectDisposedException.ThrowIf(disposed, this);

        Size = new(Math.Max(size.X, 1), Math.Max(size.Y, 1));
        Release();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Release();
    }

    void Release() {
        if (!texture.IsValid) {
            return;
        }

        device.Destroy(view);
        device.Destroy(texture);

        view = TextureViewHandle.Null;
        texture = TextureHandle.Null;
    }
}
