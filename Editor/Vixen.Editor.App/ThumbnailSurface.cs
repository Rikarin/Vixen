// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Ui.Renderer;

namespace Vixen.Editor.App;

/// <summary>Turns decoded thumbnail pixels into something the interface draws.</summary>
/// <remarks>
///     <para>
///         <b>The host's half of <see cref="IThumbnailSurface" />.</b> The application decides which
///         assets are worth a picture and reduces them; this is the part that needs a device, and it
///         is the same three steps the glyph atlas takes — a staging buffer, a copy, and two
///         barriers.
///     </para>
///     <para>
///         ⚠ <b>The first barrier claims <c>Undefined</c> because the texture is new every time.</b>
///         A thumbnail is uploaded once and never rewritten, unlike the atlas, so there is no later
///         state to transition from — and claiming <c>ShaderResource</c> for a texture that has
///         never held anything is a validation error on every single upload.
///     </para>
///     <para>
///         ⚠ <b>Everything is deferred to <see cref="Retire" /> rather than destroyed on the spot.</b>
///         A tile scrolled off screen releases its image, and the frame that drew it may still be in
///         flight — destroying the texture underneath it is a use-after-free the validation layer
///         reports as a crash somewhere else entirely. The host retires them when it knows no frame
///         is using them, which is the same rule <c>EditorPane</c> follows for a resized swapchain.
///     </para>
/// </remarks>
sealed class ThumbnailSurface : IThumbnailSurface, IDisposable {
    readonly IGraphicsDevice device;
    readonly UiRenderer renderer;

    readonly Dictionary<ulong, Uploaded> live = [];
    readonly List<Uploaded> retiring = [];

    ulong next = ThumbnailCache.FirstImage;

    /// <summary>Wires a surface to the device and the renderer that draws the interface.</summary>
    /// <param name="device">The device.</param>
    /// <param name="renderer">The main window's renderer, which is what an <c>Image</c> resolves against.</param>
    public ThumbnailSurface(IGraphicsDevice device, UiRenderer renderer) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderer);

        this.device = device;
        this.renderer = renderer;
    }

    /// <inheritdoc />
    public ulong Upload(int width, int height, ReadOnlySpan<byte> rgba) {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) {
            return 0;
        }

        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, width, height, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "thumbnail")
        );

        var staging = device.CreateBuffer(
            new(rgba.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "thumbnail staging")
        );

        device.Write(staging, 0, rgba);

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "thumbnail")) {
            commands.Barrier(new([], [new(texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
            commands.CopyBufferToTexture(staging, 0, new(texture), new(width, height, 1));
            commands.Barrier(new([], [new(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
        }

        var view = device.CreateTextureView(texture);
        var image = next++;

        renderer.RegisterImage(image, view);
        live[image] = new Uploaded(texture, view, staging);

        return image;
    }

    /// <inheritdoc />
    public void Release(ulong image) {
        if (live.Remove(image, out var uploaded)) {
            retiring.Add(uploaded);
        }
    }

    /// <summary>Destroys what has been released, once no frame can still be using it.</summary>
    /// <remarks>
    ///     Called by the host after it has waited for the device, which is the only moment this is
    ///     safe. A surface that destroyed on <see cref="Release" /> would be freeing a texture the
    ///     frame in flight is sampling.
    /// </remarks>
    public void Retire() {
        foreach (var uploaded in retiring) {
            Destroy(uploaded);
        }

        retiring.Clear();
    }

    /// <inheritdoc />
    public void Dispose() {
        Retire();

        foreach (var uploaded in live.Values) {
            Destroy(uploaded);
        }

        live.Clear();
    }

    void Destroy(Uploaded uploaded) {
        device.Destroy(uploaded.View);
        device.Destroy(uploaded.Texture);
        device.Destroy(uploaded.Staging);
    }

    readonly record struct Uploaded(TextureHandle Texture, TextureViewHandle View, BufferHandle Staging);
}
