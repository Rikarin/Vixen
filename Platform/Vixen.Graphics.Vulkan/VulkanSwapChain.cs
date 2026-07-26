// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Vixen.Core.Mathematics;
using VkFormat = Silk.NET.Vulkan.Format;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Vixen.Graphics.Vulkan;

/// <summary>The images a window is presented from.</summary>
/// <remarks>
///     <para>
///         Presentation is where a Vulkan backend's synchronisation is either right or subtly wrong.
///         The rule this implements: acquiring signals a semaphore, the first submission after an
///         acquire waits on it, and presenting waits on whatever the last submission signalled. That
///         is enough for a frame recorded in submission order, which is what a frame is.
///     </para>
///     <para>
///         <c>OutOfDate</c> is returned, never thrown. It happens every time a user drags a window
///         edge, and a renderer that treated it as exceptional would pay an exception per frame for
///         the duration of the drag ([05](../../docs/plan/05-graphics-rhi.md)).
///     </para>
/// </remarks>
sealed unsafe class VulkanSwapChain : ISwapChain {
    readonly VulkanDevice device;
    readonly KhrSwapchain extension;
    readonly SurfaceKHR surface;
    readonly VkSemaphore[] acquired;
    VkSemaphore[] presentable = [];

    SwapchainKHR handle;
    TextureHandle[] images = [];
    TextureViewHandle[] imageViews = [];
    uint currentImage;
    int acquireCursor;
    bool imageAcquired;
    bool disposed;

    internal VulkanSwapChain(VulkanDevice device, in SwapChainDescription description) {
        this.device = device;

        extension = device.Swapchains
            ?? throw new InvalidOperationException(
                "A swapchain was asked for on a device created without VK_KHR_swapchain, which means it "
                + "was created without a surface. Pass the window's SurfaceHandle to VulkanDeviceOptions."
            );

        surface = device.Surface;

        if (surface.Handle == 0) {
            throw new InvalidOperationException("This device has no surface, so it cannot present.");
        }

        // One per frame in flight: the semaphore an acquire signals is not safe to reuse until that
        // frame's work has retired, and reusing it a frame early is a hang that reproduces on one
        // driver in ten.
        acquired = new VkSemaphore[device.FramesInFlight + 1];

        for (var index = 0; index < acquired.Length; index++) {
            var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

            fixed (VkSemaphore* target = &acquired[index]) {
                VulkanDevice.Check(
                    device.Api.CreateSemaphore(device.Handle, &info, null, target),
                    "vkCreateSemaphore"
                );
            }
        }

        Build(description.Size, description.Format, description.PresentMode, description.ImageCount);
    }

    /// <inheritdoc />
    public PixelFormat Format { get; private set; }

    /// <inheritdoc />
    public Int2 Size { get; private set; }

    /// <inheritdoc />
    public PresentMode PresentMode { get; private set; }

    /// <inheritdoc />
    public int ImageCount => images.Length;

    /// <inheritdoc />
    public TextureHandle CurrentTexture =>
        imageAcquired && currentImage < images.Length ? images[currentImage] : TextureHandle.Null;

    /// <inheritdoc />
    public SwapChainStatus AcquireNextImage(out TextureViewHandle view) {
        view = TextureViewHandle.Null;

        if (disposed || handle.Handle == 0) {
            return SwapChainStatus.DeviceLost;
        }

        var semaphore = acquired[acquireCursor];
        acquireCursor = (acquireCursor + 1) % acquired.Length;

        uint index = 0;

        var result = extension.AcquireNextImage(
            device.Handle,
            handle,
            ulong.MaxValue,
            semaphore,
            default,
            &index
        );

        var status = Translate(result);

        if (status is SwapChainStatus.OutOfDate or SwapChainStatus.DeviceLost) {
            return status;
        }

        currentImage = index;
        imageAcquired = true;
        view = imageViews[index];

        // The colour-attachment-output stage, not top-of-pipe: everything before writing colour —
        // vertex work, depth, the whole geometry pipeline — may legally run before the image is
        // available, and waiting at the top serialises all of it against the display.
        device.WaitBeforeNextSubmit(semaphore, PipelineStageFlags.ColorAttachmentOutputBit);
        return status;
    }

    /// <inheritdoc />
    public SwapChainStatus Present() {
        if (disposed || handle.Handle == 0) {
            return SwapChainStatus.DeviceLost;
        }

        if (!imageAcquired) {
            throw new InvalidOperationException(
                "Present() was called without a successful AcquireNextImage(). There is no image to show."
            );
        }

        imageAcquired = false;
        var swapchain = handle;
        var index = currentImage;
        var wait = presentable[index];

        // One empty submission whose only job is to move the frame's completion onto a semaphore
        // that belongs to *this image*.
        //
        // The frame's own signal semaphore cannot serve: it comes from a ring the device recycles on
        // its frame fence, and that fence knows when the submission finished, not when the
        // presentation engine finished reading. A semaphore handed to vkQueuePresentKHR is consumed
        // asynchronously and is not free just because our frame retired — which validation reports
        // as "signaled ... but it may still be in use", two frames later and nowhere near the cause.
        //
        // A per-image semaphore has the property that matters: vkAcquireNextImage returning this
        // image is itself proof that its previous present completed, so reuse is safe by
        // construction rather than by argument.
        var previous = device.TakePresentWait();
        var queue = device.QueueFor(QueueKind.Graphics).Handle;
        var stage = PipelineStageFlags.AllCommandsBit;

        var handover = new SubmitInfo {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = previous.Handle == 0 ? 0u : 1u,
            PWaitSemaphores = previous.Handle == 0 ? null : &previous,
            PWaitDstStageMask = previous.Handle == 0 ? null : &stage,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &wait
        };

        VulkanDevice.Check(device.Api.QueueSubmit(queue, 1, &handover, default), "vkQueueSubmit");

        var info = new PresentInfoKHR {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &index
        };

        return Translate(extension.QueuePresent(queue, &info));
    }

    /// <inheritdoc />
    public void Resize(Int2 size) {
        if (disposed) {
            return;
        }

        // Every image may still be in flight, and recreating underneath them is undefined on every
        // API. The contract says the caller is responsible for having waited; this waits anyway,
        // because a resize is not a hot path and a hang here is very expensive to diagnose.
        device.WaitIdle();
        Build(size, Format, PresentMode, images.Length);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        device.WaitIdle();
        Teardown();

        foreach (var semaphore in acquired) {
            device.Api.DestroySemaphore(device.Handle, semaphore, null);
        }
    }

    static SwapChainStatus Translate(Result result) => result switch {
        Result.Success => SwapChainStatus.Ready,
        Result.SuboptimalKhr => SwapChainStatus.Suboptimal,
        Result.ErrorOutOfDateKhr => SwapChainStatus.OutOfDate,
        _ => SwapChainStatus.DeviceLost
    };

    /// <summary>Picks a surface format, preferring the one asked for.</summary>
    /// <remarks>
    ///     The preference is honoured only if the surface offers it; otherwise the first format the
    ///     surface names that the RHI also names. Declining an unknown format rather than guessing is
    ///     deliberate: a swapchain in a format the engine cannot describe is one whose contents no
    ///     part of the renderer can reason about.
    /// </remarks>
    internal static SurfaceFormatKHR ChooseFormat(ReadOnlySpan<SurfaceFormatKHR> available, PixelFormat wanted) {
        var target = VulkanFormats.ToVulkan(wanted);

        foreach (var format in available) {
            if (format.Format == target && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr) {
                return format;
            }
        }

        foreach (var format in available) {
            if (format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr
                && VulkanFormats.FromVulkan(format.Format) != PixelFormat.Undefined) {
                return format;
            }
        }

        return available.IsEmpty ? new() { Format = VkFormat.Undefined } : available[0];
    }

    /// <summary>Picks a present mode, falling back to the one every driver must support.</summary>
    internal static PresentModeKHR ChoosePresentMode(
        ReadOnlySpan<PresentModeKHR> available,
        PresentMode wanted
    ) {
        var target = VulkanEnums.ToVulkan(wanted);

        foreach (var mode in available) {
            if (mode == target) {
                return mode;
            }
        }

        // FIFO is the only mode the specification requires, so it is the only safe fallback. Silently
        // choosing Immediate instead would give a caller who asked for vsync a tearing window.
        return PresentModeKHR.FifoKhr;
    }

    /// <summary>Clamps a requested image count into what the surface allows.</summary>
    /// <remarks>
    ///     <c>maxImageCount</c> of zero means "no maximum", which is the one value a naive
    ///     <c>Math.Min</c> gets exactly backwards — it would clamp every request to zero and create a
    ///     swapchain with no images.
    /// </remarks>
    internal static uint ChooseImageCount(in SurfaceCapabilitiesKHR capabilities, int wanted) {
        var count = (uint)Math.Max(1, wanted);
        count = Math.Max(count, capabilities.MinImageCount);

        if (capabilities.MaxImageCount > 0) {
            count = Math.Min(count, capabilities.MaxImageCount);
        }

        return count;
    }

    /// <summary>Clamps a size into what the surface allows.</summary>
    /// <remarks>
    ///     A current extent of <c>0xFFFFFFFF</c> means the surface takes its size from the swapchain
    ///     rather than the other way round, which is what Wayland does; anything else is the compositor
    ///     stating the size, and asking for a different one fails.
    /// </remarks>
    internal static Extent2D ChooseExtent(in SurfaceCapabilitiesKHR capabilities, Int2 size) {
        if (capabilities.CurrentExtent.Width != uint.MaxValue) {
            return capabilities.CurrentExtent;
        }

        return new(
            Math.Clamp(
                (uint)Math.Max(0, size.X),
                capabilities.MinImageExtent.Width,
                capabilities.MaxImageExtent.Width
            ),
            Math.Clamp(
                (uint)Math.Max(0, size.Y),
                capabilities.MinImageExtent.Height,
                capabilities.MaxImageExtent.Height
            )
        );
    }

    void Build(Int2 size, PixelFormat preferredFormat, PresentMode preferredMode, int preferredCount) {
        var api = device.Api;
        var surfaces = device.Surfaces
            ?? throw new InvalidOperationException("VK_KHR_surface is not loaded on this instance.");

        var physical = device.Adapters.Handle;

        SurfaceCapabilitiesKHR capabilities;

        VulkanDevice.Check(
            surfaces.GetPhysicalDeviceSurfaceCapabilities(physical, surface, &capabilities),
            "vkGetPhysicalDeviceSurfaceCapabilities"
        );

        uint formatCount = 0;
        surfaces.GetPhysicalDeviceSurfaceFormats(physical, surface, ref formatCount, null);
        var formats = new SurfaceFormatKHR[Math.Max(1, formatCount)];

        fixed (SurfaceFormatKHR* first = formats) {
            surfaces.GetPhysicalDeviceSurfaceFormats(physical, surface, &formatCount, first);
        }

        uint modeCount = 0;
        surfaces.GetPhysicalDeviceSurfacePresentModes(physical, surface, ref modeCount, null);
        var modes = new PresentModeKHR[Math.Max(1, modeCount)];

        fixed (PresentModeKHR* first = modes) {
            surfaces.GetPhysicalDeviceSurfacePresentModes(physical, surface, &modeCount, first);
        }

        var chosen = ChooseFormat(formats.AsSpan(0, (int)formatCount), preferredFormat);
        var mode = ChoosePresentMode(modes.AsSpan(0, (int)modeCount), preferredMode);
        var extent = ChooseExtent(capabilities, size);
        var count = ChooseImageCount(capabilities, preferredCount);
        var previous = handle;

        var create = new SwapchainCreateInfoKHR {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = count,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,

            // Transfer-destination as well as colour-attachment: a blit into the swapchain image is
            // how a post-processing chain that ends in a full-resolution image finishes, and a
            // swapchain that cannot be copied into forces a redundant fullscreen draw.
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = previous
        };

        SwapchainKHR created;

        VulkanDevice.Check(
            extension.CreateSwapchain(device.Handle, &create, null, &created),
            "vkCreateSwapchainKHR"
        );

        Teardown();
        handle = created;
        Format = VulkanFormats.FromVulkan(chosen.Format);
        PresentMode = VulkanEnums.FromVulkan(mode);
        Size = new((int)extent.Width, (int)extent.Height);

        uint imageCount = 0;
        extension.GetSwapchainImages(device.Handle, handle, ref imageCount, null);
        var raw = new Image[imageCount];

        fixed (Image* first = raw) {
            extension.GetSwapchainImages(device.Handle, handle, &imageCount, first);
        }

        images = new TextureHandle[imageCount];
        imageViews = new TextureViewHandle[imageCount];
        presentable = new VkSemaphore[imageCount];

        for (var index = 0u; index < imageCount; index++) {
            var semaphore = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

            fixed (VkSemaphore* target = &presentable[index]) {
                VulkanDevice.Check(
                    api.CreateSemaphore(device.Handle, &semaphore, null, target),
                    "vkCreateSemaphore"
                );
            }
        }

        var description = new TextureDescription(
            Format,
            (int)extent.Width,
            (int)extent.Height,
            TextureUsage.ColourTarget | TextureUsage.CopyDestination,
            Name: "SwapChain image"
        );

        for (var index = 0u; index < imageCount; index++) {
            images[index] = device.AdoptSwapChainImage(raw[index], description);
            imageViews[index] = device.CreateTextureView(images[index]);
        }
    }

    void Teardown() {
        foreach (var view in imageViews) {
            device.Destroy(view);
        }

        foreach (var image in images) {
            device.Destroy(image);
        }

        imageViews = [];
        images = [];

        foreach (var semaphore in presentable) {
            device.Api.DestroySemaphore(device.Handle, semaphore, null);
        }

        presentable = [];

        if (handle.Handle != 0) {
            extension.DestroySwapchain(device.Handle, handle, null);
            handle = default;
        }
    }
}
