// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>The default framebuffer, dressed as a swapchain.</summary>
/// <remarks>
///     <para>
///         <b>There is no swapchain in OpenGL.</b> The context owns a default framebuffer, the
///         windowing layer — WGL, GLX, EGL, or the browser — owns the buffers behind it, and
///         presenting is one call outside GL entirely. The RHI's model of acquiring an image,
///         rendering into it and presenting it is Vulkan's, D3D12's and WebGPU's; here it is
///         emulated over something with no images to acquire.
///     </para>
///     <para>
///         So this renders into a texture of its own and blits it to framebuffer zero at present.
///         That costs a full-screen blit per frame, and it buys the thing that matters: a render
///         graph, a compositor and every render feature address the swapchain image as a
///         <see cref="TextureViewHandle" /> exactly as they do on Vulkan, with no branch anywhere
///         for the backend that has no such thing.
///     </para>
///     <para>
///         <b>Why not render into framebuffer zero directly.</b> Because it is not a texture: it
///         cannot be sampled, it cannot be a copy source, and it cannot be attached alongside a depth
///         texture the engine allocated. Every one of those is something a post-processing chain
///         does to the swapchain image on the other backends.
///     </para>
///     <para>
///         ⚠ The blit is deliberately not flipped. The offscreen target holds its first row at the
///         lowest window coordinate and so does the default framebuffer, so a straight copy is
///         right — <c>GlDevice.CopyBufferToTexture</c> sets out why this backend's rows are not
///         upside down despite GL's window origin.
///     </para>
/// </remarks>
sealed class GlSwapChain : ISwapChain {
    readonly GlDevice device;
    readonly Action? present;
    readonly int imageCount;

    TextureHandle texture;
    TextureViewHandle view;
    bool acquired;
    bool disposed;

    public GlSwapChain(GlDevice device, in SwapChainDescription description, Action? present) {
        this.device = device;
        this.present = present;
        Size = description.Size;
        PresentMode = description.PresentMode;

        // GL has no format negotiation: the context was created with a pixel format and that is
        // that. The RHI already tells callers to read Format back rather than assume, and this is
        // the backend that makes that instruction load-bearing.
        Format = description.Format.IsSrgb() ? PixelFormat.Rgba8UNormSrgb : PixelFormat.Rgba8UNorm;

        // Reported as one, because that is the truth. GL's buffer count belongs to the windowing
        // layer, the application cannot choose it, and claiming three would tell a renderer it may
        // have three frames of resources in flight when it has whatever the driver decided.
        imageCount = 1;
        Allocate();
    }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public Int2 Size { get; private set; }

    /// <inheritdoc />
    public PresentMode PresentMode { get; }

    /// <inheritdoc />
    public int ImageCount => imageCount;

    /// <inheritdoc />
    public TextureHandle CurrentTexture => acquired ? texture : TextureHandle.Null;

    /// <summary>How many times <see cref="Present" /> has been called.</summary>
    public int PresentCount { get; private set; }

    /// <inheritdoc />
    public SwapChainStatus AcquireNextImage(out TextureViewHandle image) {
        ObjectDisposedException.ThrowIf(disposed, this);
        acquired = true;
        image = view;
        return SwapChainStatus.Ready;
    }

    /// <inheritdoc />
    public SwapChainStatus Present() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!acquired) {
            throw new InvalidOperationException("Present without a successful AcquireNextImage.");
        }

        device.BlitToDefaultFramebuffer(texture, Size);
        present?.Invoke();
        acquired = false;
        PresentCount++;
        return SwapChainStatus.Ready;
    }

    /// <inheritdoc />
    public void Resize(Int2 size) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (size == Size) {
            return;
        }

        Release();
        Size = size;
        Allocate();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Release();
    }

    void Allocate() {
        texture = device.CreateTexture(new(
            Format,
            Math.Max(1, Size.X),
            Math.Max(1, Size.Y),
            TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
            Name: "SwapChain"
        ));

        view = device.CreateTextureView(texture);
    }

    void Release() {
        if (view.IsValid) {
            device.Destroy(view);
            device.Destroy(texture);
            view = TextureViewHandle.Null;
            texture = TextureHandle.Null;
        }

        acquired = false;
    }
}
