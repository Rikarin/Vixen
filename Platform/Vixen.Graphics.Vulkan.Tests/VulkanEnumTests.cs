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
    ///     ⚠ <b>The one mapping that is deliberately not the identity, and the reason is
    ///     <c>VulkanCommandList.SetViewport</c>.</b> It submits a negative-height viewport so that
    ///     the engine's +Y-up clip space reaches the screen the right way up; Vulkan then decides
    ///     facing from the signed area in framebuffer coordinates, which that flip mirrors. So a
    ///     mesh wound counter-clockwise seen from outside — which is what <c>MeshPrimitives</c>
    ///     builds and what <see cref="FrontFace.CounterClockwise" /> names — arrives clockwise.
    ///     Mapped straight through, <c>CullMode.Back</c> keeps the inside of every closed mesh and
    ///     a shader reading <c>SV_IsFrontFace</c> flips exactly the normals that were already right.
    /// </summary>
    [Theory]
    [InlineData(FrontFace.CounterClockwise, Silk.NET.Vulkan.FrontFace.Clockwise)]
    [InlineData(FrontFace.Clockwise, Silk.NET.Vulkan.FrontFace.CounterClockwise)]
    public void TheWindingIsInvertedToPayForTheFlippedViewport(
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
