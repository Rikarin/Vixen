// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>The swapchain a run with no window gets, which is a texture and no presentation.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The point is that the frame stays one frame.</b> A headless run without this had two
///         choices and both were wrong: fall through to the device that draws nothing — which is what
///         <c>--vixen-headless</c> did, so a whole game booted, loaded its level, ran its physics and
///         produced a black picture with every counter reporting success — or grow a second code path
///         around <see cref="ISwapChain" /> that only offscreen callers take, which is how a headless
///         frame stops being the frame anybody ships.
///     </para>
///     <para>
///         Skipped where there is no driver, like the rest of this suite.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class OffscreenSwapChainTests {
    static bool TryOpen(out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(new(), out device, out reason);

    /// <summary>
    ///     A surface of <see cref="SurfaceKind.None" /> is answered rather than refused. Before this,
    ///     the call reached <c>VulkanSwapChain</c>, which asks <c>VulkanSurface.TryCreate</c> for a
    ///     <c>VkSurfaceKHR</c> and is told "a swapchain was asked for on a platform that has nothing
    ///     to present to".
    /// </summary>
    [Fact]
    public void ASurfacelessDeviceStillBuildsAChain() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.None, new(64, 48)));

        Assert.Equal(new Int2(64, 48), chain.Size);
        Assert.Equal(1, chain.ImageCount);
    }

    /// <summary>
    ///     The image is a real texture a copy can read, which is the whole reason a headless picture
    ///     is possible at all: a presented image is created without the transfer-source flag, so
    ///     nothing can copy out of one.
    /// </summary>
    [Fact]
    public void ItsImageIsAnOrdinaryTextureThatCanBeReadBack() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        const int Width = 32;
        const int Height = 32;
        const int Bytes = Width * Height * 4;

        using var chain = owned.CreateSwapChain(
            new(SurfaceHandle.None, new(Width, Height), PixelFormat.Rgba8UNorm)
        );

        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var view));
        Assert.True(view.IsValid);
        Assert.True(chain.CurrentTexture.IsValid);

        var readback = owned.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "offscreen readback")
        );

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Graphics, "offscreen")) {
            list.Barrier(new([], [new(chain.CurrentTexture, ResourceState.Undefined, ResourceState.ColourTarget)]));

            list.BeginRenderPass(new(
                [new(view, LoadAction.Clear, StoreAction.Store, new(0.25f, 0.5f, 0.75f, 1f))],
                name: "offscreen clear"
            ));

            list.EndRenderPass();
            list.Barrier(new([], [new(chain.CurrentTexture, ResourceState.ColourTarget, ResourceState.CopySource)]));
            list.CopyTextureToBuffer(new(chain.CurrentTexture), new(Width, Height, 1), readback, 0);
            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        var pixels = new byte[Bytes];
        owned.Read(readback, 0, pixels);
        owned.Destroy(readback);

        Assert.Equal(SwapChainStatus.Ready, chain.Present());

        // The clear, within a level either way: two conformant drivers may round 0.25 × 255 to 63 or
        // to 64 and both are right — the golden suite's Tolerance.Flat makes the same allowance.
        Assert.InRange(pixels[0], 62, 65);
        Assert.InRange(pixels[1], 126, 129);
        Assert.InRange(pixels[2], 190, 193);
        Assert.Equal(255, pixels[3]);
    }

    /// <summary>
    ///     ⚠ Present is not a no-op that returns whatever: a host reads its status and rebuilds the
    ///     chain on <see cref="SwapChainStatus.OutOfDate" />, so a chain that answered anything else
    ///     here would put the frame loop into a rebuild it can never finish.
    /// </summary>
    [Fact]
    public void PresentingSucceedsAndChangesNothing() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.None, new(16, 16)));

        chain.AcquireNextImage(out _);
        var first = chain.CurrentTexture;

        Assert.Equal(SwapChainStatus.Ready, chain.Present());

        chain.AcquireNextImage(out _);

        // The same image every frame, which is what makes a capture reproducible: with three images
        // in rotation, "the last frame's picture" would depend on how many frames ran.
        Assert.Equal(first, chain.CurrentTexture);
    }

    /// <summary>Resizing gives a chain of the new size, because the host rebuilds rather than recreates.</summary>
    [Fact]
    public void ResizingReportsTheNewSize() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.None, new(16, 16)));
        chain.AcquireNextImage(out _);

        owned.WaitIdle();
        chain.Resize(new(24, 20));

        Assert.Equal(new Int2(24, 20), chain.Size);
        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var view));
        Assert.True(view.IsValid);
    }
}
