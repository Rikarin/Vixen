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

    /// <summary>
    ///     ⚠ <b>Eight bits across a wider gamut is a worse picture, not a better one.</b> The same
    ///     256 steps stretched over P3 are coarser per step than over sRGB, so a gradient that was
    ///     clean bands. A surface that offers a wide space only at eight bits is offering nothing
    ///     worth taking, and the sRGB path wins.
    /// </summary>
    [Fact]
    public void AWideColorSpaceIsRefusedAtEightBits() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.B8G8R8A8Unorm, ColorSpace = ColorSpaceKHR.SpaceDisplayP3NonlinearExt },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceDisplayP3NonlinearExt },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var chosen = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb, ColorGamut.DisplayP3);

        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, chosen.ColorSpace);
        Assert.Equal(ColorGamut.Srgb, VulkanSwapChain.GamutOf(chosen.ColorSpace));
    }

    /// <summary>
    ///     ⚠ <b>A linear colour space with a unorm format is worse than banding.</b> Extended sRGB
    ///     carries the extra gamut as values below zero and above one; a unorm cannot store either,
    ///     so the pairing would clamp away precisely what it was chosen for. MoltenVK really does
    ///     offer this combination, so refusing it has to be deliberate.
    /// </summary>
    [Fact]
    public void ExtendedSrgbIsRefusedWithoutFloatStorage() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.B8G8R8A8Unorm, ColorSpace = ColorSpaceKHR.SpaceExtendedSrgbLinearExt },
            new() {
                Format = VkFormat.A2B10G10R10UnormPack32, ColorSpace = ColorSpaceKHR.SpaceExtendedSrgbLinearExt
            },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var chosen = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb, ColorGamut.DisplayP3);

        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, chosen.ColorSpace);
    }

    /// <summary>
    ///     Extended sRGB in half-float is the pairing that needs no conversion anywhere: the engine's
    ///     own primaries, its own linear encoding, and room for the out-of-gamut values it already
    ///     carries unclamped.
    /// </summary>
    [Fact]
    public void ExtendedSrgbInHalfFloatIsPreferredToDisplayP3() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.A2B10G10R10UnormPack32, ColorSpace = ColorSpaceKHR.SpaceDisplayP3NonlinearExt },
            new() { Format = VkFormat.R16G16B16A16Sfloat, ColorSpace = ColorSpaceKHR.SpaceExtendedSrgbLinearExt },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var chosen = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb, ColorGamut.DisplayP3);

        Assert.Equal(ColorSpaceKHR.SpaceExtendedSrgbLinearExt, chosen.ColorSpace);
        Assert.Equal(VkFormat.R16G16B16A16Sfloat, chosen.Format);
    }

    /// <summary>Ten bits is enough for P3's own primaries, where the encoding is not linear.</summary>
    [Fact]
    public void DisplayP3IsTakenAtTenBits() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.B8G8R8A8Unorm, ColorSpace = ColorSpaceKHR.SpaceDisplayP3NonlinearExt },
            new() { Format = VkFormat.A2B10G10R10UnormPack32, ColorSpace = ColorSpaceKHR.SpaceDisplayP3NonlinearExt },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var chosen = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb, ColorGamut.DisplayP3);

        Assert.Equal(ColorSpaceKHR.SpaceDisplayP3NonlinearExt, chosen.ColorSpace);
        Assert.Equal(VkFormat.A2B10G10R10UnormPack32, chosen.Format);
        Assert.Equal(ColorGamut.DisplayP3, VulkanSwapChain.GamutOf(chosen.ColorSpace));
    }

    /// <summary>
    ///     ⚠ <b>The case that decides whether this feature is honest.</b> A surface with nothing but
    ///     sRGB — an older driver, a Linux compositor without the extension, or any instance created
    ///     without <c>VK_EXT_swapchain_colorspace</c> — must come out exactly where it came out
    ///     before the wide-gamut path existed, and must say so rather than claim a gamut it did not
    ///     get.
    /// </summary>
    [Fact]
    public void ASurfaceWithNoWideSpaceFallsBackUnchanged() {
        SurfaceFormatKHR[] available = [
            new() { Format = VkFormat.B8G8R8A8Unorm, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr },
            new() { Format = VkFormat.B8G8R8A8Srgb, ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr }
        ];

        var wide = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb, ColorGamut.Rec2020);
        var plain = VulkanSwapChain.ChooseFormat(available, PixelFormat.Bgra8UNormSrgb);

        Assert.Equal(plain, wide);
        Assert.Equal(ColorGamut.Srgb, VulkanSwapChain.GamutOf(wide.ColorSpace));
    }

    /// <summary>A caller that did not ask for a wide gamut does not get one, however much is on offer.</summary>
    [Fact]
    public void NotAskingForAWideGamutChangesNothing() {
        var chosen = VulkanSwapChain.ChooseFormat(MoltenVkFormats(), PixelFormat.Bgra8UNormSrgb, ColorGamut.Srgb);

        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, chosen.ColorSpace);
        Assert.Equal(VkFormat.B8G8R8A8Srgb, chosen.Format);
    }

    /// <summary>
    ///     ⚠ <b>What this machine actually reports</b>, taken from <c>vulkaninfo</c> on an M1 Max
    ///     under MoltenVK with <c>VK_EXT_swapchain_colorspace</c> enabled: five formats offered
    ///     against every one of twelve colour spaces, with no filtering whatsoever — including the
    ///     eight-bit unorm paired with a linear space that needs to store negatives. The driver's
    ///     enumeration is not a recommendation, which is the whole reason the pairing rule lives
    ///     here.
    /// </summary>
    static SurfaceFormatKHR[] MoltenVkFormats() {
        VkFormat[] formats = [
            VkFormat.B8G8R8A8Unorm,
            VkFormat.B8G8R8A8Srgb,
            VkFormat.R16G16B16A16Sfloat,
            VkFormat.A2B10G10R10UnormPack32,
            VkFormat.A2R10G10B10UnormPack32
        ];

        ColorSpaceKHR[] spaces = [
            ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ColorSpaceKHR.SpaceDisplayP3NonlinearExt,
            ColorSpaceKHR.SpaceDciP3NonlinearExt,
            ColorSpaceKHR.SpaceBT709NonlinearExt,
            ColorSpaceKHR.SpaceAdobergbNonlinearExt,
            ColorSpaceKHR.SpacePassThroughExt,
            ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
            ColorSpaceKHR.SpaceExtendedSrgbNonlinearExt,
            ColorSpaceKHR.SpaceBT2020LinearExt,
            ColorSpaceKHR.SpaceDisplayP3LinearExt,
            ColorSpaceKHR.SpaceHdr10ST2084Ext,
            ColorSpaceKHR.SpaceHdr10HlgExt
        ];

        return [.. from space in spaces from format in formats select new SurfaceFormatKHR {
            Format = format, ColorSpace = space
        }];
    }

    /// <summary>
    ///     Against that real enumeration, a P3 request lands on extended sRGB in half-float — the one
    ///     pairing that carries the engine's linear values through untouched.
    /// </summary>
    [Fact]
    public void AgainstThisMachinesRealEnumerationTheChoiceIsHalfFloatExtendedSrgb() {
        var chosen = VulkanSwapChain.ChooseFormat(
            MoltenVkFormats(),
            PixelFormat.Bgra8UNormSrgb,
            ColorGamut.DisplayP3
        );

        Assert.Equal(ColorSpaceKHR.SpaceExtendedSrgbLinearExt, chosen.ColorSpace);
        Assert.Equal(VkFormat.R16G16B16A16Sfloat, chosen.Format);

        // And the renderer is told it may send anything, because extended sRGB is unbounded.
        Assert.Equal(ColorGamut.Rec2020, VulkanSwapChain.GamutOf(chosen.ColorSpace));
    }

    /// <summary>
    ///     HDR's colour spaces carry a PQ or HLG transfer function rather than a gamut widening, so
    ///     they are not something a request for a wider gamut may silently be answered with.
    /// </summary>
    [Theory]
    [InlineData(ColorSpaceKHR.SpaceHdr10ST2084Ext)]
    [InlineData(ColorSpaceKHR.SpaceHdr10HlgExt)]
    [InlineData(ColorSpaceKHR.SpacePassThroughExt)]
    public void AnHdrOrPassThroughSpaceIsNotAGamut(ColorSpaceKHR space) =>
        Assert.Equal(ColorGamut.Srgb, VulkanSwapChain.GamutOf(space));

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
    ///     A swapchain asked for on a device with no surface is answered offscreen, not refused.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test asserted the opposite, and the claim it was defending has been
    ///         withdrawn.</b> It said a surfaceless request was "a caller error worth naming, because
    ///         the cause is upstream: the device was created without the window's handle" — which is
    ///         one thing it can mean and not the only one. The other is a run that has no window on
    ///         purpose and wants its frame anyway, and that run is the majority of them: every
    ///         golden fixture, every dedicated server, and now every <c>--vixen-capture</c>.
    ///     </para>
    ///     <para>
    ///         The mistake the old message was guarding against is still caught, one level up:
    ///         <c>GraphicsHost</c> declines Vulkan for a surface it cannot present to unless
    ///         something asked for a picture, so a head that meant to have a window and did not still
    ///         hears about it — with the log line that can say so in a sentence, rather than an
    ///         exception from a constructor.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASwapChainOnAHeadlessDeviceIsOffscreenRatherThanRefused() {
        VulkanRequirement.Available(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var chain = owned.CreateSwapChain(new(SurfaceHandle.None, new Int2(640, 480)));

        Assert.Equal(new Int2(640, 480), chain.Size);
        Assert.Equal(SwapChainStatus.Ready, chain.AcquireNextImage(out var view));
        Assert.True(view.IsValid);
    }
}
