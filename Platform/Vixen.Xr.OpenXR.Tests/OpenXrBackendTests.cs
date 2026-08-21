// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Xunit;

namespace Vixen.Xr.OpenXR.Tests;

/// <summary>
///     What can be asserted about a backend that talks to a headset. The session model, the
///     projections and the action system are tested in <c>Vixen.Xr.Tests</c> against the simulated
///     device; this is about whether the runtime is found, whether the refusal is graceful, and
///     whether the pieces that do not need hardware are right.
/// </summary>
/// <remarks>
///     Every test that needs a real runtime skips itself when there is none, rather than failing. A
///     CI runner with no OpenXR loader is the ordinary case — and so is a developer's laptop — and a
///     suite that goes red on it is a suite people learn to ignore. This is the same bargain
///     <c>OpenALBackendTests</c> makes.
/// </remarks>
public sealed class OpenXrBackendTests {
    [Fact]
    public void ConstructingIsSafeWithNoRuntimeOnTheMachine() {
        using var backend = new OpenXrBackend();

        // No assertion about which: a machine may genuinely have no OpenXR. What must hold is that
        // constructing it is safe either way, because backend selection constructs every candidate.
        Assert.Equal("OpenXR", backend.Name);

        if (!backend.IsAvailable) {
            Assert.NotEmpty(backend.UnavailableReason);
        }
    }

    [Fact]
    public void AnUnavailableBackendRefusesWorkWithTheReasonRatherThanACrash() {
        using var backend = new OpenXrBackend();

        // The skip runs the other way round from its neighbours, and is a skip for the same reason:
        // this case is about the refusal, so a machine that *has* a runtime has nothing to say
        // about it. A bare `return` here reported a pass on exactly those machines — green without
        // having exercised the path the test is named for.
        Assert.SkipWhen(backend.IsAvailable, "An OpenXR runtime is installed, so there is no refusal to observe.");

        var thrown = Assert.Throws<InvalidOperationException>(() => backend.GetVulkanRequirements());

        Assert.Equal(backend.UnavailableReason, thrown.Message);
    }

    [Fact]
    public void TheSystemIsDescribedWhenThereIsOne() {
        using var backend = new OpenXrBackend();

        Assert.SkipWhen(!backend.IsAvailable, backend.UnavailableReason);
        Assert.True(backend.TryGetSystem(out var system));
        Assert.NotEmpty(system.Name);
        Assert.InRange(system.ViewCount, 1, 4);
        Assert.True(system.RecommendedImageSize.X > 0);
        Assert.True(system.RecommendedImageSize.Y > 0);
        Assert.True(system.MaximumImageSize.X >= system.RecommendedImageSize.X);
    }

    [Fact]
    public void TheRuntimeNamesTheExtensionsTheDeviceMustCarry() {
        using var backend = new OpenXrBackend();

        Assert.SkipWhen(!backend.IsAvailable, backend.UnavailableReason);

        var requirements = backend.GetVulkanRequirements();

        // Not "is not empty": a runtime that shares a device inside one process may genuinely need
        // nothing. What must hold is that the versions bracket something usable, because a device
        // created outside them is one the runtime will refuse a session on.
        Assert.True(requirements.MinimumApiVersion <= requirements.MaximumApiVersion);
        Assert.True(requirements.MinimumApiVersion >= new Version(1, 0));
        Assert.NotNull(requirements.InstanceExtensions);
        Assert.NotNull(requirements.DeviceExtensions);
    }

    [Fact]
    public void DisposingTwiceIsHarmless() {
        var backend = new OpenXrBackend();

        backend.Dispose();
        backend.Dispose();
    }

    [Theory]
    [InlineData(PixelFormat.Rgba8UNormSrgb)]
    [InlineData(PixelFormat.Bgra8UNormSrgb)]
    [InlineData(PixelFormat.Rgba16Float)]
    public void EveryFormatTheSwapchainOffersRoundTrips(PixelFormat format) {
        // Needs no runtime: it is the table that turns an engine format into the VkFormat number a
        // runtime enumerates, and getting it wrong is a swapchain that will not create.
        var vulkan = OpenXrFormats.ToVulkan(format);

        Assert.NotEqual(0, vulkan);
        Assert.Equal(format, OpenXrFormats.FromVulkan(vulkan));
    }

    [Fact]
    public void TheFormatAskedForIsTakenWhenTheRuntimeOffersIt() {
        long[] offered = [
            OpenXrFormats.ToVulkan(PixelFormat.Bgra8UNormSrgb),
            OpenXrFormats.ToVulkan(PixelFormat.Rgba8UNormSrgb)
        ];

        Assert.True(OpenXrFormats.TryPick(PixelFormat.Rgba8UNormSrgb, offered, out var chosen));
        Assert.Equal(PixelFormat.Rgba8UNormSrgb, OpenXrFormats.FromVulkan(chosen));
    }

    [Fact]
    public void TheRuntimesOwnFirstChoiceIsTakenWhenItCannotOfferWhatWasAsked() {
        long[] offered = [OpenXrFormats.ToVulkan(PixelFormat.Bgra8UNormSrgb)];

        Assert.False(OpenXrFormats.TryPick(PixelFormat.Rgba16Float, offered, out var chosen));
        Assert.Equal(PixelFormat.Bgra8UNormSrgb, OpenXrFormats.FromVulkan(chosen));
    }

    [Fact]
    public void ADepthFormatIsNeverTakenAsAFallbackForAColourOne() {
        // It would be accepted, render nothing visible, and take a while to work out.
        long[] offered = [
            OpenXrFormats.ToVulkan(PixelFormat.Depth32Float),
            OpenXrFormats.ToVulkan(PixelFormat.Bgra8UNorm)
        ];

        Assert.False(OpenXrFormats.TryPick(PixelFormat.Rgba16Float, offered, out var chosen));
        Assert.Equal(PixelFormat.Bgra8UNorm, OpenXrFormats.FromVulkan(chosen));
    }

    [Fact]
    public void AVersionIsPackedTheWayOpenXrPacksIt() {
        // Major in the top sixteen bits, minor in the next, patch in the bottom thirty-two — which is
        // not how Vulkan packs one, and mixing the two is a refused instance with no explanation.
        Assert.Equal(0x0001_0000_0000_0022UL, OpenXrBackend.Pack(1, 0, 34));
        Assert.Equal(0x0001_0001_0000_0000UL, OpenXrBackend.Pack(1, 1, 0));
    }
}
