// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.OpenXR;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Xr.OpenXR;

/// <summary>Eye buffers the runtime owns and the engine renders into.</summary>
/// <remarks>
///     <para>
///         <b>The images are the compositor's.</b> They are allocated by the runtime — possibly in
///         memory it can reproject without a copy — and handed over as <c>VkImage</c> handles, which
///         the graphics backend adopts through <see cref="IXrImageImporter" />. Destroying this
///         releases the adoption and leaves the images alone; destroying the OpenXR swapchain is what
///         frees them.
///     </para>
///     <para>
///         <b>Acquire and wait are one call here, and that is deliberate.</b> The specification
///         separates them so that an application can do other work in between, and in practice
///         nothing does: a renderer acquires an image at the moment it is about to write to it.
///         Merging them removes an ordering that is easy to get wrong and whose only symptom is
///         tearing that appears on the headset and nowhere else.
///     </para>
/// </remarks>
public sealed unsafe class OpenXrSwapchain : IXrSwapchain {
    readonly XR api;
    readonly TextureHandle[] images;
    readonly IXrImageImporter? importer;
    readonly TextureViewHandle[] views;

    bool disposed;
    Swapchain handle;

    internal OpenXrSwapchain(OpenXrSession session, IXrImageImporter? importer, in XrSwapchainDescription description) {
        this.importer = importer;
        api = session.Api;

        var formats = EnumerateFormats(session);

        if (!OpenXrFormats.TryPick(description.Format, formats, out var format)
            && session.BackendLogger is { } logger) {
            OpenXrLog.FormatFallback(
                logger,
                description.Format.ToString(),
                OpenXrFormats.FromVulkan(format).ToString()
            );
        }

        var create = new SwapchainCreateInfo {
            Type = StructureType.SwapchainCreateInfo,
            UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit,
            Format = format,
            SampleCount = (uint)Math.Max(1, description.SampleCount),
            Width = (uint)description.Size.X,
            Height = (uint)description.Size.Y,
            FaceCount = 1,
            ArraySize = (uint)Math.Max(1, description.ArrayLayers),
            MipCount = 1
        };

        Swapchain created;

        OpenXrResult.Check(api.CreateSwapchain(session.Handle, &create, &created), "xrCreateSwapchain");
        handle = created;

        Size = description.Size;
        Format = OpenXrFormats.FromVulkan(format);
        ArrayLayers = (int)create.ArraySize;

        var raw = EnumerateImages();

        images = new TextureHandle[raw.Length];
        views = new TextureViewHandle[raw.Length];

        if (importer is null) {
            return;
        }

        var texture = new TextureDescription(
            Format,
            description.Size.X,
            description.Size.Y,
            description.Usage,
            ArrayLayers: ArrayLayers,
            SampleCount: (int)create.SampleCount,
            Name: string.IsNullOrEmpty(description.Name) ? "xr swapchain" : description.Name
        );

        for (var index = 0; index < raw.Length; index++) {
            images[index] = importer.Import((nint)raw[index], in texture);
            views[index] = importer.CreateView(images[index]);
        }
    }

    /// <inheritdoc />
    public Int2 Size { get; }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public int ArrayLayers { get; }

    /// <inheritdoc />
    public int ImageCount => images.Length;

    /// <inheritdoc />
    public int AcquiredIndex { get; private set; } = -1;

    internal Swapchain Handle => handle;

    /// <inheritdoc />
    public TextureHandle Image(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, images.Length);

        return images[index];
    }

    /// <inheritdoc />
    public TextureViewHandle View(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, views.Length);

        return views[index];
    }

    /// <inheritdoc />
    public int AcquireImage() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (AcquiredIndex >= 0) {
            throw new InvalidOperationException("An image is already acquired from this swapchain.");
        }

        var acquire = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        var index = 0u;

        OpenXrResult.Check(api.AcquireSwapchainImage(handle, &acquire, &index), "xrAcquireSwapchainImage");

        var wait = new SwapchainImageWaitInfo {
            Type = StructureType.SwapchainImageWaitInfo,

            // The specification's own "wait as long as it takes". A timeout here would mean rendering
            // into an image the compositor is still reading, which is tearing rather than a dropped
            // frame — and a dropped frame is what a runtime that cannot free an image is already
            // doing.
            Timeout = long.MaxValue
        };

        OpenXrResult.Check(api.WaitSwapchainImage(handle, &wait), "xrWaitSwapchainImage");
        AcquiredIndex = (int)index;

        return AcquiredIndex;
    }

    /// <inheritdoc />
    public void ReleaseImage() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (AcquiredIndex < 0) {
            throw new InvalidOperationException("No image is acquired from this swapchain.");
        }

        var release = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };

        OpenXrResult.Check(api.ReleaseSwapchainImage(handle, &release), "xrReleaseSwapchainImage");
        AcquiredIndex = -1;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (importer is not null) {
            for (var index = 0; index < images.Length; index++) {
                if (views[index].IsValid) {
                    importer.Release(views[index]);
                }

                if (images[index].IsValid) {
                    importer.Release(images[index]);
                }
            }
        }

        if (handle.Handle != 0) {
            api.DestroySwapchain(handle);
            handle = default;
        }
    }

    long[] EnumerateFormats(OpenXrSession session) {
        var count = 0u;

        OpenXrResult.Check(
            api.EnumerateSwapchainFormats(session.Handle, 0, &count, null),
            "xrEnumerateSwapchainFormats"
        );

        if (count == 0) {
            return [];
        }

        var formats = new long[count];

        fixed (long* first = formats) {
            OpenXrResult.Check(
                api.EnumerateSwapchainFormats(session.Handle, count, &count, first),
                "xrEnumerateSwapchainFormats"
            );
        }

        return formats;
    }

    ulong[] EnumerateImages() {
        var count = 0u;

        OpenXrResult.Check(
            api.EnumerateSwapchainImages(handle, 0, &count, null),
            "xrEnumerateSwapchainImages"
        );

        if (count == 0) {
            return [];
        }

        var native = new SwapchainImageVulkanKHR[count];

        for (var index = 0; index < count; index++) {
            native[index].Type = StructureType.SwapchainImageVulkanKhr;
        }

        fixed (SwapchainImageVulkanKHR* first = native) {
            OpenXrResult.Check(
                api.EnumerateSwapchainImages(handle, count, &count, (SwapchainImageBaseHeader*)first),
                "xrEnumerateSwapchainImages"
            );
        }

        var result = new ulong[count];

        for (var index = 0; index < count; index++) {
            result[index] = native[index].Image;
        }

        return result;
    }
}
