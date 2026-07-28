// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// Aliased, and the reason is the very thing this file exists to guard. Inside a namespace under
// Vixen.Graphics, an unqualified `BlendFactor` resolves to the RHI's own enum rather than Silk's —
// enclosing namespaces are searched before using directives — and `Assert.Equal((uint)theirs,
// (uint)ours)` would then be comparing the RHI's numbering with itself and passing for nothing.
using SilkAddressMode = Silk.NET.WebGPU.AddressMode;
using SilkBlendFactor = Silk.NET.WebGPU.BlendFactor;
using SilkBufferBindingType = Silk.NET.WebGPU.BufferBindingType;
using SilkBufferUsage = Silk.NET.WebGPU.BufferUsage;
using SilkCompareFunction = Silk.NET.WebGPU.CompareFunction;
using SilkFeatureName = Silk.NET.WebGPU.FeatureName;
using SilkLoadOp = Silk.NET.WebGPU.LoadOp;
using SilkPresentMode = Silk.NET.WebGPU.PresentMode;
using SilkPrimitiveTopology = Silk.NET.WebGPU.PrimitiveTopology;
using SilkShaderStage = Silk.NET.WebGPU.ShaderStage;
using SilkStencilOperation = Silk.NET.WebGPU.StencilOperation;
using SilkStoreOp = Silk.NET.WebGPU.StoreOp;
using SilkSurfaceStatus = Silk.NET.WebGPU.SurfaceGetCurrentTextureStatus;
using SilkTextureFormat = Silk.NET.WebGPU.TextureFormat;
using SilkTextureSampleType = Silk.NET.WebGPU.TextureSampleType;
using SilkTextureUsage = Silk.NET.WebGPU.TextureUsage;
using SilkTextureViewDimension = Silk.NET.WebGPU.TextureViewDimension;
using SilkVertexFormat = Silk.NET.WebGPU.VertexFormat;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>The backend's own WebGPU enums against the binding's.</summary>
/// <remarks>
///     <para>
///         The backend spells WebGPU's vocabulary in its own enums, because the browser surface
///         cannot reference Silk.NET at all — and it gives them <c>webgpu.h</c>'s numbers so the
///         native surface can cast rather than switch, and the browser surface can index a string
///         table by the same number.
///     </para>
///     <para>
///         <b>That cast is only safe while the numbers agree, and nothing else checks it.</b> A
///         binding upgrade that inserted one texture format would silently shift every format above
///         it: every pipeline would still compile, every draw would still run, and the picture would
///         be wrong. So every member is asserted here, by name, one <c>InlineData</c> per value —
///         the same shape as <c>Vixen.Net.Tests/Wire</c>'s committed bytes, and for the same reason:
///         a hash carries one bit, and what you then need is which member and which value.
///     </para>
/// </remarks>
public class WebGpuEnumAgreementTests {
    [Theory]
    [InlineData(WgpuTextureFormat.Undefined, SilkTextureFormat.Undefined)]
    [InlineData(WgpuTextureFormat.R8Unorm, SilkTextureFormat.R8Unorm)]
    [InlineData(WgpuTextureFormat.R8Snorm, SilkTextureFormat.R8Snorm)]
    [InlineData(WgpuTextureFormat.R8Uint, SilkTextureFormat.R8Uint)]
    [InlineData(WgpuTextureFormat.R8Sint, SilkTextureFormat.R8Sint)]
    [InlineData(WgpuTextureFormat.R16Uint, SilkTextureFormat.R16Uint)]
    [InlineData(WgpuTextureFormat.R16Float, SilkTextureFormat.R16float)]
    [InlineData(WgpuTextureFormat.Rg8Unorm, SilkTextureFormat.RG8Unorm)]
    [InlineData(WgpuTextureFormat.Rg8Snorm, SilkTextureFormat.RG8Snorm)]
    [InlineData(WgpuTextureFormat.R32Float, SilkTextureFormat.R32float)]
    [InlineData(WgpuTextureFormat.R32Uint, SilkTextureFormat.R32Uint)]
    [InlineData(WgpuTextureFormat.Rg16Float, SilkTextureFormat.RG16float)]
    [InlineData(WgpuTextureFormat.Rgba8Unorm, SilkTextureFormat.Rgba8Unorm)]
    [InlineData(WgpuTextureFormat.Rgba8UnormSrgb, SilkTextureFormat.Rgba8UnormSrgb)]
    [InlineData(WgpuTextureFormat.Rgba8Snorm, SilkTextureFormat.Rgba8Snorm)]
    [InlineData(WgpuTextureFormat.Bgra8Unorm, SilkTextureFormat.Bgra8Unorm)]
    [InlineData(WgpuTextureFormat.Bgra8UnormSrgb, SilkTextureFormat.Bgra8UnormSrgb)]
    [InlineData(WgpuTextureFormat.Rgb10A2Unorm, SilkTextureFormat.Rgb10A2Unorm)]
    [InlineData(WgpuTextureFormat.Rg11B10Ufloat, SilkTextureFormat.RG11B10Ufloat)]
    [InlineData(WgpuTextureFormat.Rg32Float, SilkTextureFormat.RG32float)]
    [InlineData(WgpuTextureFormat.Rgba16Float, SilkTextureFormat.Rgba16float)]
    [InlineData(WgpuTextureFormat.Rgba32Float, SilkTextureFormat.Rgba32float)]
    [InlineData(WgpuTextureFormat.Rgba32Uint, SilkTextureFormat.Rgba32Uint)]
    [InlineData(WgpuTextureFormat.Depth16Unorm, SilkTextureFormat.Depth16Unorm)]
    [InlineData(WgpuTextureFormat.Depth24PlusStencil8, SilkTextureFormat.Depth24PlusStencil8)]
    [InlineData(WgpuTextureFormat.Depth32Float, SilkTextureFormat.Depth32float)]
    [InlineData(WgpuTextureFormat.Depth32FloatStencil8, SilkTextureFormat.Depth32floatStencil8)]
    [InlineData(WgpuTextureFormat.Bc1RgbaUnorm, SilkTextureFormat.BC1RgbaUnorm)]
    [InlineData(WgpuTextureFormat.Bc1RgbaUnormSrgb, SilkTextureFormat.BC1RgbaUnormSrgb)]
    [InlineData(WgpuTextureFormat.Bc3RgbaUnorm, SilkTextureFormat.BC3RgbaUnorm)]
    [InlineData(WgpuTextureFormat.Bc3RgbaUnormSrgb, SilkTextureFormat.BC3RgbaUnormSrgb)]
    [InlineData(WgpuTextureFormat.Bc4RUnorm, SilkTextureFormat.BC4RUnorm)]
    [InlineData(WgpuTextureFormat.Bc5RgUnorm, SilkTextureFormat.BC5RGUnorm)]
    [InlineData(WgpuTextureFormat.Bc6HRgbUfloat, SilkTextureFormat.BC6HrgbUfloat)]
    [InlineData(WgpuTextureFormat.Bc7RgbaUnorm, SilkTextureFormat.BC7RgbaUnorm)]
    [InlineData(WgpuTextureFormat.Bc7RgbaUnormSrgb, SilkTextureFormat.BC7RgbaUnormSrgb)]
    [InlineData(WgpuTextureFormat.Etc2Rgb8A1Unorm, SilkTextureFormat.Etc2Rgb8A1Unorm)]
    [InlineData(WgpuTextureFormat.Etc2Rgba8Unorm, SilkTextureFormat.Etc2Rgba8Unorm)]
    [InlineData(WgpuTextureFormat.Astc4X4Unorm, SilkTextureFormat.Astc4x4Unorm)]
    [InlineData(WgpuTextureFormat.Astc4X4UnormSrgb, SilkTextureFormat.Astc4x4UnormSrgb)]
    [InlineData(WgpuTextureFormat.Astc8X8Unorm, SilkTextureFormat.Astc8x8Unorm)]
    [InlineData(WgpuTextureFormat.Astc8X8UnormSrgb, SilkTextureFormat.Astc8x8UnormSrgb)]
    public void TextureFormatsAgree(WgpuTextureFormat ours, SilkTextureFormat theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuBufferUsage.MapRead, SilkBufferUsage.MapRead)]
    [InlineData(WgpuBufferUsage.MapWrite, SilkBufferUsage.MapWrite)]
    [InlineData(WgpuBufferUsage.CopySrc, SilkBufferUsage.CopySrc)]
    [InlineData(WgpuBufferUsage.CopyDst, SilkBufferUsage.CopyDst)]
    [InlineData(WgpuBufferUsage.Index, SilkBufferUsage.Index)]
    [InlineData(WgpuBufferUsage.Vertex, SilkBufferUsage.Vertex)]
    [InlineData(WgpuBufferUsage.Uniform, SilkBufferUsage.Uniform)]
    [InlineData(WgpuBufferUsage.Storage, SilkBufferUsage.Storage)]
    [InlineData(WgpuBufferUsage.Indirect, SilkBufferUsage.Indirect)]
    public void BufferUsagesAgree(WgpuBufferUsage ours, SilkBufferUsage theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuTextureUsage.CopySrc, SilkTextureUsage.CopySrc)]
    [InlineData(WgpuTextureUsage.CopyDst, SilkTextureUsage.CopyDst)]
    [InlineData(WgpuTextureUsage.TextureBinding, SilkTextureUsage.TextureBinding)]
    [InlineData(WgpuTextureUsage.StorageBinding, SilkTextureUsage.StorageBinding)]
    [InlineData(WgpuTextureUsage.RenderAttachment, SilkTextureUsage.RenderAttachment)]
    public void TextureUsagesAgree(WgpuTextureUsage ours, SilkTextureUsage theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuShaderStage.Vertex, SilkShaderStage.Vertex)]
    [InlineData(WgpuShaderStage.Fragment, SilkShaderStage.Fragment)]
    [InlineData(WgpuShaderStage.Compute, SilkShaderStage.Compute)]
    public void ShaderStagesAgree(WgpuShaderStage ours, SilkShaderStage theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuCompareFunction.Never, SilkCompareFunction.Never)]
    [InlineData(WgpuCompareFunction.Less, SilkCompareFunction.Less)]
    [InlineData(WgpuCompareFunction.LessEqual, SilkCompareFunction.LessEqual)]
    [InlineData(WgpuCompareFunction.Greater, SilkCompareFunction.Greater)]
    [InlineData(WgpuCompareFunction.GreaterEqual, SilkCompareFunction.GreaterEqual)]
    [InlineData(WgpuCompareFunction.Equal, SilkCompareFunction.Equal)]
    [InlineData(WgpuCompareFunction.NotEqual, SilkCompareFunction.NotEqual)]
    [InlineData(WgpuCompareFunction.Always, SilkCompareFunction.Always)]
    public void CompareFunctionsAgree(WgpuCompareFunction ours, SilkCompareFunction theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuBlendFactor.Zero, SilkBlendFactor.Zero)]
    [InlineData(WgpuBlendFactor.One, SilkBlendFactor.One)]
    [InlineData(WgpuBlendFactor.Src, SilkBlendFactor.Src)]
    [InlineData(WgpuBlendFactor.OneMinusSrc, SilkBlendFactor.OneMinusSrc)]
    [InlineData(WgpuBlendFactor.SrcAlpha, SilkBlendFactor.SrcAlpha)]
    [InlineData(WgpuBlendFactor.OneMinusSrcAlpha, SilkBlendFactor.OneMinusSrcAlpha)]
    [InlineData(WgpuBlendFactor.Dst, SilkBlendFactor.Dst)]
    [InlineData(WgpuBlendFactor.OneMinusDst, SilkBlendFactor.OneMinusDst)]
    [InlineData(WgpuBlendFactor.DstAlpha, SilkBlendFactor.DstAlpha)]
    [InlineData(WgpuBlendFactor.OneMinusDstAlpha, SilkBlendFactor.OneMinusDstAlpha)]
    [InlineData(WgpuBlendFactor.SrcAlphaSaturated, SilkBlendFactor.SrcAlphaSaturated)]
    [InlineData(WgpuBlendFactor.Constant, SilkBlendFactor.Constant)]
    [InlineData(WgpuBlendFactor.OneMinusConstant, SilkBlendFactor.OneMinusConstant)]
    public void BlendFactorsAgree(WgpuBlendFactor ours, SilkBlendFactor theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuStencilOperation.Keep, SilkStencilOperation.Keep)]
    [InlineData(WgpuStencilOperation.Zero, SilkStencilOperation.Zero)]
    [InlineData(WgpuStencilOperation.Replace, SilkStencilOperation.Replace)]
    [InlineData(WgpuStencilOperation.Invert, SilkStencilOperation.Invert)]
    [InlineData(WgpuStencilOperation.IncrementClamp, SilkStencilOperation.IncrementClamp)]
    [InlineData(WgpuStencilOperation.DecrementClamp, SilkStencilOperation.DecrementClamp)]
    [InlineData(WgpuStencilOperation.IncrementWrap, SilkStencilOperation.IncrementWrap)]
    [InlineData(WgpuStencilOperation.DecrementWrap, SilkStencilOperation.DecrementWrap)]
    public void StencilOperationsAgree(WgpuStencilOperation ours, SilkStencilOperation theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuVertexFormat.Uint8X4, SilkVertexFormat.Uint8x4)]
    [InlineData(WgpuVertexFormat.Unorm8X4, SilkVertexFormat.Unorm8x4)]
    [InlineData(WgpuVertexFormat.Snorm8X4, SilkVertexFormat.Snorm8x4)]
    [InlineData(WgpuVertexFormat.Unorm16X2, SilkVertexFormat.Unorm16x2)]
    [InlineData(WgpuVertexFormat.Snorm16X4, SilkVertexFormat.Snorm16x4)]
    [InlineData(WgpuVertexFormat.Float16X2, SilkVertexFormat.Float16x2)]
    [InlineData(WgpuVertexFormat.Float16X4, SilkVertexFormat.Float16x4)]
    [InlineData(WgpuVertexFormat.Float32, SilkVertexFormat.Float32)]
    [InlineData(WgpuVertexFormat.Float32X2, SilkVertexFormat.Float32x2)]
    [InlineData(WgpuVertexFormat.Float32X3, SilkVertexFormat.Float32x3)]
    [InlineData(WgpuVertexFormat.Float32X4, SilkVertexFormat.Float32x4)]
    [InlineData(WgpuVertexFormat.Uint32, SilkVertexFormat.Uint32)]
    public void VertexFormatsAgree(WgpuVertexFormat ours, SilkVertexFormat theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuLoadOp.Clear, SilkLoadOp.Clear)]
    [InlineData(WgpuLoadOp.Load, SilkLoadOp.Load)]
    public void LoadOpsAgree(WgpuLoadOp ours, SilkLoadOp theirs) => Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuStoreOp.Store, SilkStoreOp.Store)]
    [InlineData(WgpuStoreOp.Discard, SilkStoreOp.Discard)]
    public void StoreOpsAgree(WgpuStoreOp ours, SilkStoreOp theirs) => Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuPresentMode.Fifo, SilkPresentMode.Fifo)]
    [InlineData(WgpuPresentMode.FifoRelaxed, SilkPresentMode.FifoRelaxed)]
    [InlineData(WgpuPresentMode.Immediate, SilkPresentMode.Immediate)]
    [InlineData(WgpuPresentMode.Mailbox, SilkPresentMode.Mailbox)]
    public void PresentModesAgree(WgpuPresentMode ours, SilkPresentMode theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuFeatureName.DepthClipControl, SilkFeatureName.DepthClipControl)]
    [InlineData(WgpuFeatureName.Depth32FloatStencil8, SilkFeatureName.Depth32floatStencil8)]
    [InlineData(WgpuFeatureName.TimestampQuery, SilkFeatureName.TimestampQuery)]
    [InlineData(WgpuFeatureName.TextureCompressionBc, SilkFeatureName.TextureCompressionBC)]
    [InlineData(WgpuFeatureName.TextureCompressionEtc2, SilkFeatureName.TextureCompressionEtc2)]
    [InlineData(WgpuFeatureName.TextureCompressionAstc, SilkFeatureName.TextureCompressionAstc)]
    [InlineData(WgpuFeatureName.IndirectFirstInstance, SilkFeatureName.IndirectFirstInstance)]
    [InlineData(WgpuFeatureName.ShaderF16, SilkFeatureName.ShaderF16)]
    [InlineData(WgpuFeatureName.Rg11B10UfloatRenderable, SilkFeatureName.RG11B10UfloatRenderable)]
    [InlineData(WgpuFeatureName.Bgra8UnormStorage, SilkFeatureName.Bgra8UnormStorage)]
    [InlineData(WgpuFeatureName.Float32Filterable, SilkFeatureName.Float32filterable)]
    public void FeatureNamesAgree(WgpuFeatureName ours, SilkFeatureName theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuSurfaceStatus.Success, SilkSurfaceStatus.Success)]
    [InlineData(WgpuSurfaceStatus.Timeout, SilkSurfaceStatus.Timeout)]
    [InlineData(WgpuSurfaceStatus.Outdated, SilkSurfaceStatus.Outdated)]
    [InlineData(WgpuSurfaceStatus.Lost, SilkSurfaceStatus.Lost)]
    [InlineData(WgpuSurfaceStatus.OutOfMemory, SilkSurfaceStatus.OutOfMemory)]
    [InlineData(WgpuSurfaceStatus.DeviceLost, SilkSurfaceStatus.DeviceLost)]
    public void SurfaceStatusesAgree(WgpuSurfaceStatus ours, SilkSurfaceStatus theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuTextureViewDimension.Dimension1D, SilkTextureViewDimension.Dimension1D)]
    [InlineData(WgpuTextureViewDimension.Dimension2D, SilkTextureViewDimension.Dimension2D)]
    [InlineData(WgpuTextureViewDimension.Dimension2DArray, SilkTextureViewDimension.Dimension2DArray)]
    [InlineData(WgpuTextureViewDimension.Cube, SilkTextureViewDimension.DimensionCube)]
    [InlineData(WgpuTextureViewDimension.CubeArray, SilkTextureViewDimension.DimensionCubeArray)]
    [InlineData(WgpuTextureViewDimension.Dimension3D, SilkTextureViewDimension.Dimension3D)]
    public void TextureViewDimensionsAgree(WgpuTextureViewDimension ours, SilkTextureViewDimension theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuAddressMode.Repeat, SilkAddressMode.Repeat)]
    [InlineData(WgpuAddressMode.MirrorRepeat, SilkAddressMode.MirrorRepeat)]
    [InlineData(WgpuAddressMode.ClampToEdge, SilkAddressMode.ClampToEdge)]
    public void AddressModesAgree(WgpuAddressMode ours, SilkAddressMode theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuPrimitiveTopology.PointList, SilkPrimitiveTopology.PointList)]
    [InlineData(WgpuPrimitiveTopology.LineList, SilkPrimitiveTopology.LineList)]
    [InlineData(WgpuPrimitiveTopology.LineStrip, SilkPrimitiveTopology.LineStrip)]
    [InlineData(WgpuPrimitiveTopology.TriangleList, SilkPrimitiveTopology.TriangleList)]
    [InlineData(WgpuPrimitiveTopology.TriangleStrip, SilkPrimitiveTopology.TriangleStrip)]
    public void TopologiesAgree(WgpuPrimitiveTopology ours, SilkPrimitiveTopology theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuBufferBindingType.Uniform, SilkBufferBindingType.Uniform)]
    [InlineData(WgpuBufferBindingType.Storage, SilkBufferBindingType.Storage)]
    [InlineData(WgpuBufferBindingType.ReadOnlyStorage, SilkBufferBindingType.ReadOnlyStorage)]
    public void BufferBindingTypesAgree(WgpuBufferBindingType ours, SilkBufferBindingType theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    [Theory]
    [InlineData(WgpuTextureSampleType.Float, SilkTextureSampleType.Float)]
    [InlineData(WgpuTextureSampleType.UnfilterableFloat, SilkTextureSampleType.UnfilterableFloat)]
    [InlineData(WgpuTextureSampleType.Depth, SilkTextureSampleType.Depth)]
    [InlineData(WgpuTextureSampleType.Sint, SilkTextureSampleType.Sint)]
    [InlineData(WgpuTextureSampleType.Uint, SilkTextureSampleType.Uint)]
    public void TextureSampleTypesAgree(WgpuTextureSampleType ours, SilkTextureSampleType theirs) =>
        Assert.Equal((uint)theirs, (uint)ours);

    /// <summary>
    ///     Present mode is the one that is <em>not</em> a cast, and the test says so out loud.
    /// </summary>
    /// <remarks>
    ///     The RHI numbers <c>Mailbox</c> 2 and <c>Immediate</c> 3; WebGPU numbers them the other way
    ///     round. A backend that cast between them would compile, run, and give a player tearing
    ///     where they asked for dropped frames — which is exactly the class of bug the rest of this
    ///     file exists to catch, arriving from the other direction.
    /// </remarks>
    [Fact]
    public void EnginePresentModesAreNotWebGpuPresentModes() {
        Assert.NotEqual((uint)Graphics.PresentMode.Mailbox, (uint)WgpuPresentMode.Mailbox);
        Assert.NotEqual((uint)Graphics.PresentMode.Immediate, (uint)WgpuPresentMode.Immediate);

        Assert.Equal(WgpuPresentMode.Mailbox, WebGpuConversions.ToWebGpu(Graphics.PresentMode.Mailbox));
        Assert.Equal(WgpuPresentMode.Immediate, WebGpuConversions.ToWebGpu(Graphics.PresentMode.Immediate));
    }
}
