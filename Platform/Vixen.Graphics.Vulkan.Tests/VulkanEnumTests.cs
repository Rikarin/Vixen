// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Enum translation, over every member of every enum. A wrong mapping here does not throw — it
///     renders something, and finding out which of thirteen blend factors is wrong from a screenshot
///     is an afternoon.
/// </summary>
public sealed class VulkanEnumTests {
    /// <summary>
    ///     No enum member may fall through to a switch's default. Every one of these mappings has a
    ///     fallback arm, which is right for forwards compatibility and is also exactly what would hide
    ///     a member somebody forgot to map — so the fallback value has to be unreachable for real
    ///     members.
    /// </summary>
    [Fact]
    public void EveryBlendFactorMapsToADistinctVulkanFactor() =>
        AssertInjective<BlendFactor, Silk.NET.Vulkan.BlendFactor>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryCompareFunctionMapsToADistinctVulkanOp() =>
        AssertInjective<CompareFunction, CompareOp>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryStencilOperationMapsToADistinctVulkanOp() =>
        AssertInjective<StencilOperation, StencilOp>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryBlendOperationMapsToADistinctVulkanOp() =>
        AssertInjective<BlendOperation, BlendOp>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryTopologyMapsToADistinctVulkanTopology() =>
        AssertInjective<PrimitiveTopology, Silk.NET.Vulkan.PrimitiveTopology>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryAddressModeMapsToADistinctVulkanMode() =>
        AssertInjective<AddressMode, SamplerAddressMode>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryBorderColourMapsToADistinctVulkanColour() =>
        AssertInjective<BorderColour, BorderColor>(VulkanEnums.ToVulkan);

    [Fact]
    public void EveryDescriptorKindMapsToADistinctVulkanType() =>
        AssertInjective<DescriptorKind, DescriptorType>(VulkanEnums.ToVulkan);

    /// <summary>
    ///     Pinned by value as well as by injectivity, because this is the arm the fallback would
    ///     silently eat: an unmapped kind falls through to <c>UniformBuffer</c>, and a structure
    ///     written as a uniform buffer is a layout the driver accepts and a query that opens nothing.
    /// </summary>
    [Fact]
    public void AnAccelerationStructureIsItsOwnDescriptorType() =>
        Assert.Equal(
            DescriptorType.AccelerationStructureKhr,
            VulkanEnums.ToVulkan(DescriptorKind.AccelerationStructure)
        );

    [Fact]
    public void EveryAccelerationStructureKindMapsToADistinctVulkanType() =>
        AssertInjective<AccelerationStructureKind, AccelerationStructureTypeKHR>(VulkanEnums.ToVulkan);

    /// <summary>
    ///     ⚠ <b>The identity, and pinned because it has been "fixed" once.</b> The tempting argument
    ///     runs: <c>VulkanCommandList.SetViewport</c> submits a negative-height viewport, a mirror
    ///     flips winding, so the mapping must invert. It counts one mirror and there are two —
    ///     Vulkan's own +Y-down clip convention is the other — and they cancel: a triangle wound
    ///     counter-clockwise in the engine's +Y-up clip space is counter-clockwise again in the
    ///     framebuffer coordinates Vulkan decides facing in. Inverted, every closed mesh in the repo
    ///     is classified inside-out, <c>CullMode.Back</c> culls the surface, and golden fixtures
    ///     draw nothing with no validation error anywhere. The <c>cull-back</c> and
    ///     <c>cull-front</c> golden references are the on-device record of the same fact.
    /// </summary>
    [Theory]
    [InlineData(FrontFace.CounterClockwise, Silk.NET.Vulkan.FrontFace.CounterClockwise)]
    [InlineData(FrontFace.Clockwise, Silk.NET.Vulkan.FrontFace.Clockwise)]
    public void TheWindingMapsStraightThroughBecauseTwoMirrorsCancel(
        FrontFace face,
        Silk.NET.Vulkan.FrontFace expected
    ) =>
        Assert.Equal(expected, VulkanEnums.ToVulkan(face));

    /// <summary>
    ///     Every vertex format has to exist. <c>Undefined</c> is what an unmapped one produces, and a
    ///     vertex attribute with an undefined format is a pipeline that fails to compile with a
    ///     message about a format rather than about the attribute.
    /// </summary>
    [Fact]
    public void EveryVertexFormatHasAVulkanFormat() {
        foreach (var format in Enum.GetValues<VertexFormat>()) {
            Assert.NotEqual(VkFormat.Undefined, VulkanEnums.ToVulkan(format));
        }
    }

    [Fact]
    public void EveryPresentModeRoundTrips() {
        foreach (var mode in Enum.GetValues<PresentMode>()) {
            Assert.Equal(mode, VulkanEnums.FromVulkan(VulkanEnums.ToVulkan(mode)));
        }
    }

    /// <summary>
    ///     A resolve stores. Mapping it to <c>DontCare</c> would discard the multisampled image the
    ///     resolve reads from, which produces a black resolve target and no error at all.
    /// </summary>
    [Fact]
    public void ResolveStores() {
        Assert.Equal(AttachmentStoreOp.Store, VulkanEnums.ToVulkan(StoreAction.Resolve));
        Assert.Equal(AttachmentStoreOp.Store, VulkanEnums.ToVulkan(StoreAction.Store));
        Assert.Equal(AttachmentStoreOp.DontCare, VulkanEnums.ToVulkan(StoreAction.DontCare));
    }

    /// <summary>
    ///     ⚠ <b>The reversed-Z boundary.</b> The engine's near plane is depth 1 and its far plane is
    ///     0, so keeping the sample nearest the camera means <c>VK_RESOLVE_MODE_MAX_BIT</c>. Swapping
    ///     these two arms is invisible: every frame still renders, and only what reads the resolved
    ///     depth is wrong.
    /// </summary>
    [Fact]
    public void MaxIsTheNearestSampleUnderReversedZ() {
        Assert.Equal(ResolveModeFlags.MaxBit, VulkanEnums.ToVulkan(DepthResolveMode.Max));
        Assert.Equal(ResolveModeFlags.MinBit, VulkanEnums.ToVulkan(DepthResolveMode.Min));
        Assert.Equal(ResolveModeFlags.SampleZeroBit, VulkanEnums.ToVulkan(DepthResolveMode.SampleZero));
    }

    /// <summary>
    ///     No depth resolve mode maps to an average. Vulkan forbids it for depth, and the reason it
    ///     forbids it is the reason the engine's enum has no such member: the mean of several depths
    ///     describes no surface.
    /// </summary>
    [Fact]
    public void NoDepthResolveModeAverages() {
        foreach (var mode in Enum.GetValues<DepthResolveMode>()) {
            Assert.NotEqual(ResolveModeFlags.AverageBit, VulkanEnums.ToVulkan(mode));
            Assert.NotEqual(ResolveModeFlags.None, VulkanEnums.ToVulkan(mode));
        }
    }

    [Fact]
    public void EveryDepthResolveModeMapsToADistinctVulkanFlag() =>
        AssertInjective<DepthResolveMode, ResolveModeFlags>(VulkanEnums.ToVulkan);

    /// <summary>
    ///     ⚠ <b>A device that answered nothing still resolves by sample zero</b>, because Vulkan
    ///     requires that one of every implementation.
    /// </summary>
    /// <remarks>
    ///     The arm that matters is the all-zero one. <c>VulkanAdapter</c> only chains
    ///     <c>VkPhysicalDeviceDepthStencilResolveProperties</c> where the version or the extension
    ///     says the question exists, so <c>Translate</c> is handed a zeroed structure on the devices
    ///     that were never asked — and a mask of zero would report a device that cannot resolve depth
    ///     at all, which is not a device Vulkan permits and would leave
    ///     <c>ClampDepthResolveMode</c> with nothing to clamp to.
    /// </remarks>
    [Fact]
    public void SampleZeroSurvivesADeviceThatSaidNothing() {
        var features = GraphicsDeviceFeatures.Minimum with {
            SupportedDepthResolveModes = VulkanFeatures.FromDepthResolveModes(default)
        };

        Assert.True(features.SupportsDepthResolveMode(DepthResolveMode.SampleZero));
        Assert.False(features.SupportsDepthResolveMode(DepthResolveMode.Min));
        Assert.False(features.SupportsDepthResolveMode(DepthResolveMode.Max));

        // And that is what a pass asking for Min gets on such a device, rather than Min itself.
        Assert.Equal(DepthResolveMode.SampleZero, features.ClampDepthResolveMode(DepthResolveMode.Min));
        Assert.Equal(DepthResolveMode.SampleZero, features.ClampDepthResolveMode(DepthResolveMode.Max));
    }

    /// <summary>
    ///     ⚠ <b>lavapipe's answer, written down as a fixture</b>: <c>SAMPLE_ZERO</c> alone, which is
    ///     what the golden images' device reports and what made
    ///     <c>DepthResolveImageTests.MinAndMaxResolveTheSameDepthBufferDifferently</c> impossible
    ///     there rather than wrong.
    /// </summary>
    [Fact]
    public void ABitTheDeviceAdvertisesIsTheModeItKeeps() {
        var lavapipe = GraphicsDeviceFeatures.Minimum with {
            SupportedDepthResolveModes = VulkanFeatures.FromDepthResolveModes(ResolveModeFlags.SampleZeroBit)
        };

        Assert.Equal(DepthResolveMode.SampleZero, lavapipe.ClampDepthResolveMode(DepthResolveMode.Max));

        var desktop = GraphicsDeviceFeatures.Minimum with {
            SupportedDepthResolveModes = VulkanFeatures.FromDepthResolveModes(
                ResolveModeFlags.SampleZeroBit | ResolveModeFlags.MinBit | ResolveModeFlags.MaxBit
            )
        };

        Assert.Equal(DepthResolveMode.Max, desktop.ClampDepthResolveMode(DepthResolveMode.Max));
        Assert.Equal(DepthResolveMode.Min, desktop.ClampDepthResolveMode(DepthResolveMode.Min));

        // ⚠ One bit at a time, because a mask built by OR-ing the wrong shifts would still pass the
        // two cases above. Min advertised alone must not make Max legal.
        var minimumOnly = GraphicsDeviceFeatures.Minimum with {
            SupportedDepthResolveModes = VulkanFeatures.FromDepthResolveModes(
                ResolveModeFlags.SampleZeroBit | ResolveModeFlags.MinBit
            )
        };

        Assert.Equal(DepthResolveMode.Min, minimumOnly.ClampDepthResolveMode(DepthResolveMode.Min));
        Assert.Equal(DepthResolveMode.SampleZero, minimumOnly.ClampDepthResolveMode(DepthResolveMode.Max));
    }

    [Fact]
    public void EveryLoadActionMapsToADistinctVulkanOp() =>
        AssertInjective<LoadAction, AttachmentLoadOp>(VulkanEnums.ToVulkan);

    /// <summary>
    ///     Layer count decides array-ness, not the declared dimension: a one-layer view of an array
    ///     texture is a plain 2D view, and a cube with twelve layers is a cube array. Getting it wrong
    ///     gives the shader a view of the wrong type, which validation catches and release does not.
    /// </summary>
    [Theory]
    [InlineData(TextureDimension.Texture2D, 1, ImageViewType.Type2D)]
    [InlineData(TextureDimension.Texture2D, 4, ImageViewType.Type2DArray)]
    [InlineData(TextureDimension.Texture1D, 1, ImageViewType.Type1D)]
    [InlineData(TextureDimension.Texture1D, 3, ImageViewType.Type1DArray)]
    [InlineData(TextureDimension.Texture3D, 1, ImageViewType.Type3D)]
    [InlineData(TextureDimension.TextureCube, 6, ImageViewType.TypeCube)]
    [InlineData(TextureDimension.TextureCube, 12, ImageViewType.TypeCubeArray)]
    public void ViewTypeFollowsDimensionAndLayerCount(
        TextureDimension dimension,
        int layers,
        ImageViewType expected
    ) =>
        Assert.Equal(expected, VulkanEnums.ToViewType(dimension, layers));

    [Fact]
    public void ShaderStageFlagsAreUnionedNotOverwritten() {
        var flags = VulkanEnums.ToVulkan(ShaderStage.Vertex | ShaderStage.Fragment);

        Assert.True((flags & ShaderStageFlags.VertexBit) != 0);
        Assert.True((flags & ShaderStageFlags.FragmentBit) != 0);
        Assert.True((flags & ShaderStageFlags.ComputeBit) == 0);
    }

    [Fact]
    public void EveryGraphicsStageIsInAllGraphics() {
        var flags = VulkanEnums.ToVulkan(ShaderStage.AllGraphics);

        Assert.True((flags & ShaderStageFlags.VertexBit) != 0);
        Assert.True((flags & ShaderStageFlags.FragmentBit) != 0);
        Assert.True((flags & ShaderStageFlags.GeometryBit) != 0);
        Assert.True((flags & ShaderStageFlags.TessellationControlBit) != 0);
        Assert.True((flags & ShaderStageFlags.TessellationEvaluationBit) != 0);
        Assert.True((flags & ShaderStageFlags.ComputeBit) == 0);
    }

    [Fact]
    public void ColourWriteMaskMapsEachChannel() {
        Assert.Equal(ColorComponentFlags.None, VulkanEnums.ToVulkan(ColourWriteMask.None));
        Assert.Equal(ColorComponentFlags.RBit, VulkanEnums.ToVulkan(ColourWriteMask.Red));
        Assert.Equal(ColorComponentFlags.GBit, VulkanEnums.ToVulkan(ColourWriteMask.Green));
        Assert.Equal(ColorComponentFlags.BBit, VulkanEnums.ToVulkan(ColourWriteMask.Blue));
        Assert.Equal(ColorComponentFlags.ABit, VulkanEnums.ToVulkan(ColourWriteMask.Alpha));

        Assert.Equal(
            ColorComponentFlags.RBit | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            VulkanEnums.ToVulkan(ColourWriteMask.All)
        );
    }

    [Fact]
    public void EveryBufferUsageBitMapsToOne() {
        foreach (var usage in Enum.GetValues<BufferUsage>()) {
            if (usage == BufferUsage.None) {
                Assert.Equal(BufferUsageFlags.None, VulkanEnums.ToVulkan(usage));
                continue;
            }

            Assert.NotEqual(BufferUsageFlags.None, VulkanEnums.ToVulkan(usage));
        }
    }

    /// <summary>
    ///     The ray-tracing usages map to the KHR bits and not to each other. Input and storage are
    ///     different permissions — a build <em>reads</em> geometry and <em>lives in</em> storage —
    ///     and the address bit is a third thing both of them merely require.
    /// </summary>
    [Fact]
    public void RayTracingBufferUsagesMapToTheKhrBits() {
        Assert.Equal(
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            VulkanEnums.ToVulkan(BufferUsage.AccelerationStructureInput)
        );

        Assert.Equal(
            BufferUsageFlags.AccelerationStructureStorageBitKhr,
            VulkanEnums.ToVulkan(BufferUsage.AccelerationStructureStorage)
        );

        Assert.Equal(
            BufferUsageFlags.ShaderDeviceAddressBit,
            VulkanEnums.ToVulkan(BufferUsage.ShaderDeviceAddress)
        );
    }

    [Fact]
    public void EveryTextureUsageBitMapsToOne() {
        foreach (var usage in Enum.GetValues<TextureUsage>()) {
            if (usage == TextureUsage.None) {
                Assert.Equal(ImageUsageFlags.None, VulkanEnums.ToVulkan(usage));
                continue;
            }

            Assert.NotEqual(ImageUsageFlags.None, VulkanEnums.ToVulkan(usage));
        }
    }

    static void AssertInjective<TFrom, TTo>(Func<TFrom, TTo> map)
        where TFrom : struct, Enum
        where TTo : struct, Enum {
        var seen = new Dictionary<TTo, TFrom>();

        foreach (var value in Enum.GetValues<TFrom>()) {
            var mapped = map(value);

            Assert.False(
                seen.TryGetValue(mapped, out var collision),
                $"{typeof(TFrom).Name}.{value} and {typeof(TFrom).Name}.{collision} both map to "
                + $"{typeof(TTo).Name}.{mapped}, so at least one of them is wrong."
            );

            seen[mapped] = value;
        }
    }
}
