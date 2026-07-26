// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The swapchain's choices, which are pure functions of what a surface reported.
/// </summary>
/// <remarks>
///     <para>
///         <b>There is no end-to-end swapchain test, and that is a real gap.</b> Presenting needs a
///         window; AppKit aborts the process when a window is created off the main thread, which is
///         why <c>DesktopPlatformTests</c> forces SDL's dummy video driver on macOS
///         ([10](../../docs/plan/10-platforms.md)). So the code that turns a <c>CAMetalLayer</c> into
///         a <c>VkSurfaceKHR</c> and drives acquire/present has no automated coverage on this
///         platform; <c>Samples/01</c> is what exercises it, by hand.
///     </para>
///     <para>
///         What <em>is</em> testable is every decision made from the surface's report, and those are
///         where the traps are — a <c>maxImageCount</c> of zero meaning "no maximum", a
///         <c>currentExtent</c> of <c>0xFFFFFFFF</c> meaning "you choose". Both read backwards to the
///         obvious implementation.
///     </para>
/// </remarks>
public sealed class VulkanSwapChainTests {
    [Fact]
    public void ThePreferredFormatIsChosenWhereTheSurfaceOffersIt() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.B8G8R8A8Unorm, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var chosen = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb);
        Assert.Equal(VkFormat.B8G8R8A8Srgb, chosen.Format);
    }

    /// <summary>
    ///     A format the engine cannot name is one whose contents no part of the renderer could reason
    ///     about, so a known format is preferred even when it is not the one asked for.
    /// </summary>
    [Fact]
    public void AKnownFormatIsPreferredToTheSurfacesFirstOffer() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.A2B10G10R10UnormPack32, ColorSpace = ColorSpaceKHR.SpaceHdr10ST2084Ext },
            new() { Format = VkFormat.B8G8R8A8Unorm, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var chosen = VulkanSwapChain.ChooseFormat(available, PixelFormat.Rgba32Float);

        Assert.Equal(VkFormat.B8G8R8A8Unorm, chosen.Format);
        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, chosen.ColorSpace);
    }

    [Fact]
    public void TheRequestedPresentModeIsChosenWhereItExists() {
        PresentModeKHR[] available = [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr];

        Assert.Equal(
            PresentModeKHR.MailboxKhr,
            VulkanSwapChain.ChoosePresentMode(available, PresentMode.Mailbox)
        );
    }

    /// <summary>
    ///     FIFO is the only mode the specification requires, so it is the only safe fallback. Quietly
    ///     choosing Immediate instead would hand a caller who asked for vsync a tearing window.
    /// </summary>
    [Fact]
    public void AnUnsupportedPresentModeFallsBackToFifo() {
        PresentModeKHR[] available = [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr];

        Assert.Equal(
            PresentModeKHR.FifoKhr,
            VulkanSwapChain.ChoosePresentMode(available, PresentMode.Mailbox)
        );
    }

    /// <summary>
    ///     <c>maxImageCount == 0</c> means "no maximum", which a plain <c>Math.Min</c> gets exactly
    ///     backwards — it would clamp every request to zero and build a swapchain with no images.
    /// </summary>
    [Fact]
    public void AMaximumOfZeroMeansNoMaximum() {
        var capabilities = new SurfaceCapabilitiesKHR { MinImageCount = 2, MaxImageCount = 0 };

        Assert.Equal(3u, VulkanSwapChain.ChooseImageCount(capabilities, 3));
        Assert.Equal(8u, VulkanSwapChain.ChooseImageCount(capabilities, 8));
    }

    [Fact]
    public void TheImageCountIsClampedIntoTheSurfacesRange() {
        var capabilities = new SurfaceCapabilitiesKHR { MinImageCount = 2, MaxImageCount = 3 };

        Assert.Equal(2u, VulkanSwapChain.ChooseImageCount(capabilities, 1));
        Assert.Equal(3u, VulkanSwapChain.ChooseImageCount(capabilities, 3));
        Assert.Equal(3u, VulkanSwapChain.ChooseImageCount(capabilities, 9));
    }

    /// <summary>
    ///     A compositor that states the size gets its way; asking for a different one fails.
    /// </summary>
    [Fact]
    public void AStatedExtentWins() {
        var capabilities = new SurfaceCapabilitiesKHR {
            CurrentExtent = new(1280, 720),
            MinImageExtent = new(1, 1),
            MaxImageExtent = new(4096, 4096)
        };

        var extent = VulkanSwapChain.ChooseExtent(capabilities, new(640, 480));

        Assert.Equal(1280u, extent.Width);
        Assert.Equal(720u, extent.Height);
    }

    /// <summary>
    ///     <c>0xFFFFFFFF</c> means the surface takes its size from the swapchain rather than the other
    ///     way round, which is what Wayland does.
    /// </summary>
    [Fact]
    public void AnUnsetExtentTakesTheRequestedSizeClamped() {
        var capabilities = new SurfaceCapabilitiesKHR {
            CurrentExtent = new(uint.MaxValue, uint.MaxValue),
            MinImageExtent = new(16, 16),
            MaxImageExtent = new(1024, 1024)
        };

        var exact = VulkanSwapChain.ChooseExtent(capabilities, new(640, 480));
        Assert.Equal(640u, exact.Width);
        Assert.Equal(480u, exact.Height);

        var clamped = VulkanSwapChain.ChooseExtent(capabilities, new(4000, 2));
        Assert.Equal(1024u, clamped.Width);
        Assert.Equal(16u, clamped.Height);

        // A minimised window reports zero, and a swapchain extent below the minimum is invalid.
        var minimised = VulkanSwapChain.ChooseExtent(capabilities, new(0, 0));
        Assert.Equal(16u, minimised.Width);
        Assert.Equal(16u, minimised.Height);
    }

    /// <summary>
    ///     Which instance extensions a surface needs is asked before the instance exists, so it has to
    ///     be answerable from the surface kind alone — and it is asserted here, on a machine that may
    ///     have no Vulkan at all.
    /// </summary>
    [Theory]
    [InlineData(SurfaceKind.Win32, "VK_KHR_win32_surface")]
    [InlineData(SurfaceKind.Xlib, "VK_KHR_xlib_surface")]
    [InlineData(SurfaceKind.Wayland, "VK_KHR_wayland_surface")]
    [InlineData(SurfaceKind.Metal, "VK_EXT_metal_surface")]
    [InlineData(SurfaceKind.Android, "VK_KHR_android_surface")]
    public void EachSurfaceKindNamesItsOwnExtensionAndTheCommonOne(SurfaceKind kind, string expected) {
        var extensions = VulkanSurface.RequiredExtensions(kind);

        Assert.Contains("VK_KHR_surface", extensions);
        Assert.Contains(expected, extensions);
    }

    /// <summary>A headless platform has nothing to present to and needs no surface extension.</summary>
    [Fact]
    public void AHeadlessPlatformNeedsNoSurfaceExtensions() =>
        Assert.Empty(VulkanSurface.RequiredExtensions(SurfaceKind.None));

    /// <summary>
    ///     A swapchain asked for on a device with no surface is a caller error worth naming, because
    ///     the cause is upstream: the device was created without the window's handle.
    /// </summary>
    [Fact]
    public void ASwapChainOnAHeadlessDeviceIsRefused() {
        Assert.SkipUnless(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var thrown = Assert.Throws<InvalidOperationException>(
            () => owned.CreateSwapChain(new(SurfaceHandle.None, new Int2(640, 480)))
        );

        Assert.Contains("surface", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }
}
