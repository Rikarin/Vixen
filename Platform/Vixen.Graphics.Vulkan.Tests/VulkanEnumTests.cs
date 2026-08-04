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
