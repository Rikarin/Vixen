// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The half of a backend that can be tested without a driver, and the half where a mistake is
///     silent: a format mapped to the wrong Vulkan enum does not fail, it renders the wrong colours.
/// </summary>
public class VulkanFormatTests {
    [Fact]
    public void EveryFormatTheEngineNamesHasAVulkanEquivalent() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            if (format == PixelFormat.Undefined) {
                continue;
            }

            Assert.NotEqual(VkFormat.Undefined, VulkanFormats.ToVulkan(format));
        }
    }

    [Fact]
    public void EveryMappingSurvivesTheRoundTrip() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            if (format == PixelFormat.Undefined) {
                continue;
            }

            Assert.Equal(format, VulkanFormats.FromVulkan(VulkanFormats.ToVulkan(format)));
        }
    }

    [Fact]
    public void NoTwoFormatsMapOntoTheSameVulkanOne() {
        var seen = new Dictionary<VkFormat, PixelFormat>();

        foreach (var format in Enum.GetValues<PixelFormat>()) {
            if (format == PixelFormat.Undefined) {
                continue;
            }

            var vulkan = VulkanFormats.ToVulkan(format);
            Assert.False(seen.TryGetValue(vulkan, out var other), $"{format} and {other} both map to {vulkan}.");
            seen[vulkan] = format;
        }
    }

    /// <summary>
    ///     Vulkan names a packed format from its most significant bits down, so the engine's RGBA
    ///     order becomes A2B10G10R10 rather than A2R10G10B10. Getting it the other way round swaps
    ///     red and blue in every HDR target, which looks like a colour-grading bug.
    /// </summary>
    [Fact]
    public void PackedFormatsAreNamedInTheOppositeOrder() {
        Assert.Equal(VkFormat.A2B10G10R10UnormPack32, VulkanFormats.ToVulkan(PixelFormat.Rgb10A2UNorm));
        Assert.Equal(VkFormat.B10G11R11UfloatPack32, VulkanFormats.ToVulkan(PixelFormat.Rg11B10Float));
    }

    /// <summary>An sRGB format must map to an sRGB Vulkan format and never to its linear twin —
    /// the conversion is the hardware's, and losing it is invisible until the image is wrong.</summary>
    [Fact]
    public void SrgbStaysSrgbAcrossTheBoundary() {
        foreach (var format in Enum.GetValues<PixelFormat>()) {
            if (!format.IsSrgb()) {
                continue;
            }

            var name = VulkanFormats.ToVulkan(format).ToString();
            Assert.Contains("Srgb", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AspectsFollowTheFormat() {
        Assert.Equal(ImageAspectFlags.ColorBit, VulkanFormats.AspectOf(PixelFormat.Rgba8UNorm));
        Assert.Equal(ImageAspectFlags.DepthBit, VulkanFormats.AspectOf(PixelFormat.Depth32Float));

        Assert.Equal(
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            VulkanFormats.AspectOf(PixelFormat.Depth24UNormStencil8)
        );
    }

    [Fact]
    public void SampleCountsSurviveTheRoundTrip() {
        foreach (var samples in (int[])[1, 2, 4, 8, 16]) {
            var flags = VulkanFormats.ToSampleCount(samples);
            var mask = VulkanFormats.FromSampleCounts(flags);
            var features = GraphicsDeviceFeatures.Minimum with { SupportedSampleCounts = mask };

            Assert.True(features.SupportsSampleCount(samples));
        }
    }
}
