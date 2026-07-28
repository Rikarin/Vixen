// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.WebGPU;

/// <summary>The images a window is presented from, over a configured WebGPU surface.</summary>
/// <remarks>
///     <para>
///         WebGPU has no swapchain object. A surface is <em>configured</em> — format, size, usage,
///         present mode — and then hands out one texture at a time, and how many it cycles through is
///         its own business. So <see cref="ImageCount" /> reports what was asked for rather than what
///         was granted, which is the only honest answer available and is stated here so nobody builds
///         a per-image resource array on it. Frame pacing goes through
///         <see cref="IGraphicsDevice.FramesInFlight" />, which is a number this engine chose.
///     </para>
///     <para>
///         The texture a surface hands out belongs to the surface: it is invalidated by the next
///         present whether or not anyone released it. So each acquire wraps it in a fresh handle
///         pair, and the present that follows retires them — a renderer that held one across a frame
///         would be holding a texture the implementation has taken back.
///     </para>
/// </remarks>
sealed class WebGpuSwapChain : ISwapChain {
    readonly WebGpuDevice device;
    readonly WgpuTextureUsage usage;

    TextureHandle currentTexture;
    TextureViewHandle currentView;
    bool disposed;

    internal WebGpuSwapChain(WebGpuDevice device, in SwapChainDescription description) {
        this.device = device;

        var binding = device.Binding;
        var preferred = binding.PreferredSurfaceFormat;
        var wanted = description.Format.ToWebGpu();

        // The implementation's preference wins when what was asked for is not expressible, which is
        // what ISwapChain.Format documents: read it back rather than assuming. On a browser the
        // preference is bgra8unorm or rgba8unorm depending on the platform, and picking the other one
        // costs a full-screen swizzle every frame.
        var chosen = wanted == WgpuTextureFormat.Undefined ? preferred : wanted;
        Format = chosen.ToEngine();
        SurfaceFormat = chosen;

        Size = new(Math.Max(1, description.Size.X), Math.Max(1, description.Size.Y));
        PresentMode = description.PresentMode;
        ImageCount = Math.Max(1, description.ImageCount);
        usage = WgpuTextureUsage.RenderAttachment | WgpuTextureUsage.CopySrc;

        Configure();
    }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public Int2 Size { get; private set; }

    /// <inheritdoc />
    public PresentMode PresentMode { get; }

    /// <inheritdoc />
    /// <remarks>What was asked for. WebGPU does not say what it granted.</remarks>
    public int ImageCount { get; }

    /// <inheritdoc />
    public TextureHandle CurrentTexture => currentTexture;

    /// <summary>The format the surface was configured with.</summary>
    internal WgpuTextureFormat SurfaceFormat { get; }

    /// <inheritdoc />
    public SwapChainStatus AcquireNextImage(out TextureViewHandle view) {
        ObjectDisposedException.ThrowIf(disposed, this);

        // A previous image nobody presented. Releasing it before asking for another keeps the handle
        // tables from growing by one pair per skipped frame, which a resize loop would otherwise do
        // for as long as the user held the window edge.
        ReleaseCurrent();

        var status = device.Binding.AcquireSurfaceTexture(out var texture);

        if (status is not WgpuSurfaceStatus.Success || !texture.IsValid) {
            view = TextureViewHandle.Null;
            return WebGpuConversions.ToEngine(status, false);
        }

        (currentTexture, currentView) = device.AdoptSurfaceTexture(texture, Format, Size);
        view = currentView;

        return SwapChainStatus.Ready;
    }

    /// <inheritdoc />
    public SwapChainStatus Present() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!currentView.IsValid) {
            throw new InvalidOperationException(
                "Present without a successful AcquireNextImage. There is no surface texture to show, and "
                + "WebGPU's present takes no argument to say which one — the surface knows."
            );
        }

        device.Binding.PresentSurface();
        ReleaseCurrent();

        return SwapChainStatus.Ready;
    }

    /// <inheritdoc />
    public void Resize(Int2 size) {
        ObjectDisposedException.ThrowIf(disposed, this);

        ReleaseCurrent();
        Size = new(Math.Max(1, size.X), Math.Max(1, size.Y));
        Configure();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        ReleaseCurrent();
    }

    void Configure() =>
        device.Binding.ConfigureSurface(
            new(
                SurfaceFormat,
                usage,
                Size.X,
                Size.Y,
                WebGpuConversions.ToWebGpu(PresentMode),

                // Opaque rather than Auto. A canvas configured for premultiplied alpha composites
                // with the page behind it, so anything the renderer leaves with alpha below one shows
                // the document through it — which is a surprise on the web and impossible anywhere
                // else, and is not what a game window means.
                WgpuCompositeAlphaMode.Opaque
            )
        );

    void ReleaseCurrent() {
        if (currentView.IsValid) {
            device.Destroy(currentView);
            device.Destroy(currentTexture);
        }

        currentView = TextureViewHandle.Null;
        currentTexture = TextureHandle.Null;
    }
}
