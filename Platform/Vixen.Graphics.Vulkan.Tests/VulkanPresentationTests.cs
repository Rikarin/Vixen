// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>Acquiring, presenting and rebuilding a real swapchain, with no window anywhere.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The reason this file did not exist was wrong.</b> <c>VulkanSwapChainTests</c> said
///         plainly that "presenting needs a window; AppKit aborts the process when a window is
///         created off the main thread", so the acquire/present path had no automated coverage and
///         <c>Samples/01</c> exercised it by hand. <c>VK_EXT_headless_surface</c> is an ordinary
///         <c>VkSurfaceKHR</c> with no window behind it — MoltenVK carries it, and a swapchain built
///         on one acquires, presents and recycles images through a real presentation engine. The
///         image index alternating 0, 1, 0, 1 across four frames is what settled it.
///     </para>
///     <para>
///         So what runs here is the production <see cref="VulkanSwapChain" />, reached through
///         <c>VulkanDevice.CreateSwapChain</c> exactly as a windowed head reaches it — not a double,
///         and not the offscreen chain, which is a different class that never calls
///         <c>vkQueuePresentKHR</c> at all.
///     </para>
///     <para>
///         ⚠ <b>What still cannot be covered here, stated rather than faked:</b>
///         <c>VK_ERROR_OUT_OF_DATE_KHR</c> and <c>VK_SUBOPTIMAL_KHR</c> come from a window server
///         resizing a surface underneath the swapchain, and a headless surface has no window server
///         to do it. Their <em>handling</em> is covered — the mapping the host loop keys off is
///         asserted directly, in <see cref="TheStatusTheHostLoopKeysOffIsTheOneTheSpecificationMeans" />
///         — but the driver is never made to return them. Saying so is better than a mock that
///         returns whichever one the test wanted.
///     </para>
///     <para>
///         Skipped where the loader has no <c>VK_EXT_headless_surface</c>, and — like the rest of
///         this suite — turned into a failure by <c>VIXEN_REQUIRE_VULKAN=1</c>, so a CI leg whose
///         whole purpose is this path cannot report green having presented nothing.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanPresentationTests {
    /// <summary>The chain a windowed head would get, on a surface with no window.</summary>
    static bool TryOpen(out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(new() { Surface = SurfaceHandle.Windowless }, out device, out reason);

    /// <summary>
    ///     Six frames of acquire, draw, present — and the presentation engine hands the images back
    ///     round rather than repeating one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The cycling is the assertion that a stub could not pass.</b> A swapchain that
    ///     returned image 0 every time would satisfy every status check in this file and be
    ///     completely broken: the frame would overwrite an image the display was still reading. Over
    ///     more frames than there are images, every image has to appear, and no two consecutive
    ///     acquisitions may be the same one while more than one is free.
    /// </remarks>
    [Fact]
    public void TheImagesAreAcquiredPresentedAndHandedBackRound() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(320, 240)));

        Assert.True(chain.ImageCount >= 2, $"a presenting chain has at least two images; this has {chain.ImageCount}.");

        var seen = new HashSet<TextureHandle>();
        var order = new List<TextureHandle>();

        for (var frame = 0; frame < 6; frame++) {
            Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var view));
            Assert.True(view.IsValid);
            Assert.True(chain.CurrentTexture.IsValid);

            seen.Add(chain.CurrentTexture);
            order.Add(chain.CurrentTexture);

            Draw(owned, chain, view);

            Assert.Equal(SwapChainStatus.Ready, chain.Present());
            owned.WaitIdle();
        }

        Assert.Equal(chain.ImageCount, seen.Count);

        for (var frame = 1; frame < order.Count; frame++) {
            Assert.NotEqual(order[frame - 1], order[frame]);
        }
    }

    /// <summary>
    ///     And the whole of it says nothing to the validation layers, which is the only witness the
    ///     presentation protocol has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half of the path that has no other way of being wrong out loud.</b>
    ///         <c>VulkanSwapChain.Present</c> makes an empty submission whose only job is to move the
    ///         frame's completion onto a semaphore belonging to <em>this image</em>, because a
    ///         semaphore handed to <c>vkQueuePresentKHR</c> is consumed asynchronously and is not
    ///         free just because the frame retired. Getting that wrong produces "signaled … but it
    ///         may still be in use" two frames later and nowhere near the cause, on some drivers and
    ///         not others — a picture that looks right on the machine it was written on.
    ///     </para>
    ///     <para>
    ///         Separate from the test above rather than folded into it: the recorder is
    ///         process-wide, so the reset has to be the last thing before the frames it is
    ///         attributing messages to. Same reason <c>ValidationCleanTests</c> is one test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void PresentingRepeatedlySaysNothingToTheValidationLayers() {
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        VulkanRequirement.Available(
            owned.ValidationEnabled,
            "the instance came up without validation, so there is nothing to assert"
        );

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(160, 120)));

        VulkanDiagnostics.Reset();

        // More frames than there are images, and no WaitIdle between them: the semaphore reuse this
        // is watching for only goes wrong once an image comes round for the second time with the
        // first presentation still in flight.
        for (var frame = 0; frame < 8; frame++) {
            Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var view));
            Draw(owned, chain, view);
            Assert.Equal(SwapChainStatus.Ready, chain.Present());
        }

        owned.WaitIdle();

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0 && VulkanDiagnostics.WarningCount == 0,
            $"Presenting reported {VulkanDiagnostics.ErrorCount} error(s) and "
            + $"{VulkanDiagnostics.WarningCount} warning(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>A resize rebuilds the chain at the new size, and it goes on presenting.</summary>
    /// <remarks>
    ///     ⚠ <b>The second half is the one worth having.</b> A rebuild that produced a chain of the
    ///     right size whose images no longer presented would pass a size check and hang a window
    ///     drag — and <c>Build</c> passes the old swapchain as <c>oldSwapchain</c> and then tears it
    ///     down, which is the step that goes wrong.
    /// </remarks>
    [Fact]
    public void AResizeRebuildsTheChainAndItKeepsPresenting() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(128, 128)));

        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var first));
        Draw(owned, chain, first);
        Assert.Equal(SwapChainStatus.Ready, chain.Present());

        chain.Resize(new(256, 192));

        Assert.Equal(new Int2(256, 192), chain.Size);

        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var second));
        Assert.True(second.IsValid);
        Draw(owned, chain, second);
        Assert.Equal(SwapChainStatus.Ready, chain.Present());

        owned.WaitIdle();
    }

    /// <summary>There is nothing to show before an image has been acquired, and it says so.</summary>
    /// <remarks>
    ///     ⚠ <b>A throw rather than a status, and it is the one place in this class that throws.</b>
    ///     <c>OutOfDate</c> is returned because it happens every frame of a window drag; presenting
    ///     an image nobody acquired cannot happen to a correct caller at all, so it is a bug in the
    ///     host rather than a condition to handle — and answering it with a status would let the
    ///     host's own <c>switch</c> swallow it.
    /// </remarks>
    [Fact]
    public void PresentingWithoutAcquiringIsRefusedRatherThanGuessedAt() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(64, 64)));

        Assert.Equal(TextureHandle.Null, chain.CurrentTexture);

        var refusal = Assert.Throws<InvalidOperationException>(() => chain.Present());

        Assert.Contains(nameof(ISwapChain.AcquireNextImage), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And presenting twice off one acquisition is refused by the same check.</summary>
    /// <remarks>
    ///     ⚠ The state is cleared by <c>Present</c> rather than by the next acquire, which is what
    ///     makes this a refusal rather than a second presentation of an image the display already
    ///     owns. A chain that cleared it in <c>AcquireNextImage</c> would present the same image
    ///     twice here and say nothing.
    /// </remarks>
    [Fact]
    public void PresentingTwiceOffOneAcquisitionIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(64, 64)));

        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var view));
        Draw(owned, chain, view);
        Assert.Equal(SwapChainStatus.Ready, chain.Present());
        owned.WaitIdle();

        Assert.Throws<InvalidOperationException>(() => chain.Present());
    }

    /// <summary>
    ///     A disposed chain answers <see cref="SwapChainStatus.DeviceLost" /> rather than using a
    ///     destroyed handle.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It is reachable, which is why it is not an <c>ObjectDisposedException</c>.</b> A
    ///     host that loses its window disposes the chain from the window thread while the frame
    ///     thread is between an acquire and a present; the frame loop already has a branch for
    ///     <c>DeviceLost</c>, and steering into it is a great deal better than an exception crossing
    ///     a thread boundary during teardown.
    /// </remarks>
    [Fact]
    public void ADisposedChainReportsDeviceLostRatherThanTouchingAFreedHandle() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(64, 64)));

        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out _));

        chain.Dispose();

        Assert.Equal(SwapChainStatus.DeviceLost, chain.AcquireNextImage(out var view));
        Assert.False(view.IsValid);
        Assert.Equal(SwapChainStatus.DeviceLost, chain.Present());

        // Idempotent, because the same teardown race disposes it twice as often as not.
        chain.Dispose();
    }

    /// <summary>
    ///     What each Vulkan presentation result means to the frame loop — including the two a
    ///     headless surface will never produce.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>VK_SUBOPTIMAL_KHR</c> is a success and must not be
    ///         <see cref="SwapChainStatus.OutOfDate" />.</b> It means the image was acquired or
    ///         presented and the surface would merely prefer other parameters, which a scaled
    ///         display says on every frame forever; <c>AppGraphics.End</c> puts <c>OutOfDate</c>
    ///         through an unconditional rebuild and <c>Suboptimal</c> through a size check, so
    ///         collapsing the two is a rebuild per frame that never converges.
    ///     </para>
    ///     <para>
    ///         And <c>VK_ERROR_OUT_OF_DATE_KHR</c> must not fall into the default arm with the
    ///         genuinely fatal results: a window drag would then set <c>IsLost</c> and take the
    ///         renderer down. This is the assertion that stands in for the driver-driven test a
    ///         surface with no window server cannot have.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(Result.Success, SwapChainStatus.Ready)]
    [InlineData(Result.SuboptimalKhr, SwapChainStatus.Suboptimal)]
    [InlineData(Result.ErrorOutOfDateKhr, SwapChainStatus.OutOfDate)]
    [InlineData(Result.ErrorDeviceLost, SwapChainStatus.DeviceLost)]
    [InlineData(Result.ErrorSurfaceLostKhr, SwapChainStatus.DeviceLost)]
    [InlineData(Result.ErrorOutOfDeviceMemory, SwapChainStatus.DeviceLost)]
    [InlineData(Result.Timeout, SwapChainStatus.DeviceLost)]
    public void TheStatusTheHostLoopKeysOffIsTheOneTheSpecificationMeans(Result result, SwapChainStatus expected) =>
        Assert.Equal(expected, VulkanSwapChain.Translate(result));

    /// <summary>
    ///     ⚠ A headless surface is a <em>presenting</em> one, which is what separates it from the
    ///     offscreen path rather than making it a second spelling of it.
    /// </summary>
    /// <remarks>
    ///     <c>SurfaceKind.None</c> gives a <c>VulkanOffscreenSwapChain</c> — one image, no
    ///     presentation engine, no <c>vkQueuePresentKHR</c>. If this kind fell into that path the
    ///     whole file above would pass while covering nothing new, which is precisely the shape of
    ///     instrument failure this repository keeps finding.
    /// </remarks>
    [Fact]
    public void AHeadlessSurfaceIsAPresentingOneAndNotTheOffscreenPath() {
        Assert.True(SurfaceHandle.Windowless.CanPresent);
        Assert.False(SurfaceHandle.None.CanPresent);

        Assert.Equal(
            ["VK_KHR_surface", "VK_EXT_headless_surface"],
            VulkanSurface.RequiredExtensions(SurfaceKind.Headless)
        );

        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.Windowless, new Int2(64, 64)));

        Assert.IsType<VulkanSwapChain>(chain);
    }

    /// <summary>One frame: the image is transitioned, cleared, and left ready to be presented.</summary>
    /// <remarks>
    ///     A real render pass rather than a bare barrier, because the acquire semaphore is waited on
    ///     at <c>COLOR_ATTACHMENT_OUTPUT</c> — a frame that never writes colour would never reach the
    ///     stage the wait is placed at, and would prove the wait rather than the presentation.
    /// </remarks>
    static void Draw(VulkanDevice device, ISwapChain chain, TextureViewHandle view) {
        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Graphics, "present")) {
            list.Barrier(new([], [
                new(chain.CurrentTexture, ResourceState.Undefined, ResourceState.ColourTarget)
            ]));

            list.BeginRenderPass(new(
                [new(view, LoadAction.Clear, StoreAction.Store, new(0f, 0.5f, 1f, 1f))],
                name: "present clear"
            ));

            list.EndRenderPass();

            list.Barrier(new([], [
                new(chain.CurrentTexture, ResourceState.ColourTarget, ResourceState.Present)
            ]));

            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        device.EndFrame();
    }
}
