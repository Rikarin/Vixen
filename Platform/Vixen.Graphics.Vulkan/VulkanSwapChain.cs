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

    /// <summary>The surface this swapchain made for itself, if it made one.</summary>
    /// <remarks>Destroyed with the swapchain. Zero when presenting to the device's own window.</remarks>
    readonly SurfaceKHR owned;

    readonly VkSemaphore[] acquired;
    VkSemaphore[] presentable = [];

    SwapchainKHR handle;
    TextureHandle[] images = [];
    TextureViewHandle[] imageViews = [];
    uint currentImage;
    int acquireCursor;
    bool imageAcquired;
    bool disposed;
    readonly ColorGamut requestedGamut;

    internal VulkanSwapChain(VulkanDevice device, in SwapChainDescription description) {
        this.device = device;

        // ⚠ The *request*, kept for the lifetime of the swapchain rather than derived from what was
        // chosen. `Resize` rebuilds against a surface that may by then be on a different display —
        // dragging a window from a P3 laptop panel to an sRGB monitor is the ordinary case — and
        // re-asking for the gamut that was granted last time would pin the swapchain to whichever
        // display it happened to be created on.
        requestedGamut = description.Gamut;

        extension = device.Swapchains
            ?? throw new InvalidOperationException(
                "A swapchain was asked for on a device created without VK_KHR_swapchain, which means it "
                + "was created without a surface. Pass the window's SurfaceHandle to VulkanDeviceOptions."
            );

        // ⚠ A surface of its own for any window that is not the one the device was created for, and
        // the device's own for the one that is. A `VkSurfaceKHR` may have exactly one swapchain at a
        // time — a second swapchain built on the same surface fails with
        // `VK_ERROR_NATIVE_WINDOW_IN_USE_KHR` — so an editor tearing a panel out onto the desktop
        // needs one per window. The device's is still the device's: the queue families were selected
        // against it, and destroying it here would take the device's ability to present with it.
        if (description.Surface.CanPresent && description.Surface != device.Presenting) {
            if (!VulkanSurface.TryCreate(device.Instance, description.Surface, out owned, out var refused)) {
                throw new VulkanException($"A surface could not be created for a second window: {refused}");
            }

            surface = owned;
            Supported();
        } else {
            surface = device.Surface;
        }

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
    public ColorGamut Gamut { get; private set; }

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

        // Only the one this made. The device's own outlives every swapchain built on it, and
        // destroying it here would leave a device that can no longer present to its own window.
        if (owned.Handle != 0) {
            device.Surfaces?.DestroySurface(device.Instance.Handle, owned, null);
        }
    }

    /// <summary>Refuses a window the graphics queue cannot present to, rather than finding out later.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked per surface, because the answer is per surface.</b> The queue family was chosen
    ///     against the device's <i>first</i> window; a second window on another display, or driven by
    ///     another GPU, can legitimately not be presentable from it. Every desktop driver in practice
    ///     says yes, which is exactly why finding out by way of undefined behaviour on the one that
    ///     does not would be finding out the hard way.
    /// </remarks>
    void Supported() {
        var surfaces = device.Surfaces
            ?? throw new InvalidOperationException("VK_KHR_surface is not loaded on this instance.");

        var supported = new Silk.NET.Core.Bool32(false);

        VulkanDevice.Check(
            surfaces.GetPhysicalDeviceSurfaceSupport(
                device.Adapters.Handle,
                ((VulkanQueue) device.GraphicsQueue).Family,
                surface,
                &supported
            ),
            "vkGetPhysicalDeviceSurfaceSupport"
        );

        if (!supported) {
            throw new VulkanException(
                "This device's graphics queue cannot present to that window. It is on a display or a "
                + "GPU the device was not created against, and presenting to it needs a device of its own."
            );
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
    internal static SurfaceFormatKHR ChooseFormat(ReadOnlySpan<SurfaceFormatKHR> available, PixelFormat wanted) =>
        ChooseFormat(available, wanted, ColorGamut.Srgb);

    /// <summary>Picks a surface format and colour space, for a requested display gamut.</summary>
    /// <param name="available">What the surface reported.</param>
    /// <param name="wanted">The preferred pixel format.</param>
    /// <param name="gamut">The gamut to ask the display for.</param>
    /// <returns>The chosen pairing, which may be narrower than the one requested.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A wide colour space is only ever accepted together with enough precision to use
    ///         it, and that pairing is a rule here rather than a coincidence.</b> Eight bits spread
    ///         across P3 are coarser per step than eight bits across sRGB, so
    ///         <c>B8G8R8A8_UNORM</c> on a P3 surface bands visibly in gradients that were clean in
    ///         sRGB — a strictly worse picture in exchange for the wider primaries. MoltenVK offers
    ///         every format with every colour space, including
    ///         <c>EXTENDED_SRGB_LINEAR</c> paired with an eight-bit unorm that cannot represent a
    ///         single one of the out-of-range values that colour space exists to carry, so the
    ///         filtering cannot be left to the driver's enumeration.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Extended sRGB is preferred over P3 for a reason that is not quality.</b> Its
    ///         primaries are the engine's own and its encoding is linear, so a wide colour reaches
    ///         the image as the exact number the parser produced, with no rebasing step anywhere.
    ///         Display P3 is a different set of primaries: the same numbers sent to a P3 surface are
    ///         a <i>more saturated picture</i>, not a wider one, until something converts them. That
    ///         is why the chosen gamut is reported back out rather than assumed.
    ///     </para>
    ///     <para>
    ///         Asking for <see cref="ColorGamut.Srgb" /> — the default — takes exactly the path this
    ///         function has always taken, so nothing changes for a caller that has not opted in.
    ///     </para>
    /// </remarks>
    internal static SurfaceFormatKHR ChooseFormat(
        ReadOnlySpan<SurfaceFormatKHR> available,
        PixelFormat wanted,
        ColorGamut gamut
    ) {
        var target = VulkanFormats.ToVulkan(wanted);

        if (gamut != ColorGamut.Srgb) {
            // Ordered best-first: linear half-float in the engine's own primaries, then the same
            // primaries at ten bits, then the destination's actual primaries. Each candidate is only
            // taken if the surface offers it with a format that can hold what it promises.
            foreach (var space in WideSpaces(gamut)) {
                var found = new SurfaceFormatKHR();
                var best = 0;

                foreach (var format in available) {
                    if (format.ColorSpace != space) {
                        continue;
                    }

                    var rank = Precision(format.Format, space);

                    // The requested format wins ties, so a caller who asked for fp16 and a surface
                    // that offers both fp16 and ten-bit does not silently get the narrower one.
                    if (rank > best || (rank == best && rank > 0 && format.Format == target)) {
                        found = format;
                        best = rank;
                    }
                }

                if (best > 0) {
                    return found;
                }
            }
        }

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

    /// <summary>The colour spaces that would satisfy a request for this gamut, best first.</summary>
    static ColorSpaceKHR[] WideSpaces(ColorGamut gamut) => gamut switch {
        ColorGamut.DisplayP3 => [
            ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
            ColorSpaceKHR.SpaceDisplayP3NonlinearExt
        ],

        // Nothing narrower than Rec. 2020 can stand in for it, but extended sRGB is unbounded and so
        // covers it: the primaries are sRGB's, and the values simply run past them.
        ColorGamut.Rec2020 => [
            ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
            ColorSpaceKHR.SpaceBT2020LinearExt
        ],

        _ => []
    };

    /// <summary>How well a format serves a colour space, or zero if it must not be paired with it.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is a refusal, not a low score.</b> Eight bits is the banding case for every wide
    ///     space, and for the linear ones it is worse than banding: a unorm cannot store a negative
    ///     or above-one value at all, so the entire extra gamut would be silently clamped away by the
    ///     image the renderer just went to the trouble of filling correctly.
    /// </remarks>
    static int Precision(VkFormat format, ColorSpaceKHR space) {
        var linear = space is ColorSpaceKHR.SpaceExtendedSrgbLinearExt
            or ColorSpaceKHR.SpaceDisplayP3LinearExt
            or ColorSpaceKHR.SpaceBT2020LinearExt;

        return format switch {
            VkFormat.R16G16B16A16Sfloat => 3,
            VkFormat.A2B10G10R10UnormPack32 or VkFormat.A2R10G10B10UnormPack32 when !linear => 2,
            _ => 0
        };
    }

    /// <summary>What gamut a chosen colour space actually delivers.</summary>
    /// <remarks>
    ///     Read back rather than assumed, because the request is a preference: a surface that offers
    ///     no usable wide pairing leaves the swapchain in sRGB, and a caller that carried on mapping
    ///     colours to P3 anyway would be showing over-saturated ones on an sRGB display.
    /// </remarks>
    internal static ColorGamut GamutOf(ColorSpaceKHR space) => space switch {
        // Unbounded sRGB primaries. Anything representable is representable here, so the honest
        // answer for "what may I send" is the widest gamut the engine names.
        ColorSpaceKHR.SpaceExtendedSrgbLinearExt => ColorGamut.Rec2020,
        ColorSpaceKHR.SpaceDisplayP3NonlinearExt or ColorSpaceKHR.SpaceDisplayP3LinearExt => ColorGamut.DisplayP3,
        ColorSpaceKHR.SpaceBT2020LinearExt => ColorGamut.Rec2020,
        _ => ColorGamut.Srgb
    };

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

        var chosen = ChooseFormat(formats.AsSpan(0, (int)formatCount), preferredFormat, requestedGamut);
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
        Gamut = GamutOf(chosen.ColorSpace);
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
