// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>The translation layer, which is where a wrong answer is silent.</summary>
/// <remarks>
///     A format mapped to the wrong WebGPU enum does not throw: it renders the wrong colours, or
///     samples a normal map as sRGB, and the bug is found by eye weeks later. So every mapping is
///     asserted, and the substitutions WebGPU forces — a border colour it does not have, a load
///     operation it does not have — are asserted <em>as substitutions</em>, so that changing one is
///     a decision rather than an accident.
/// </remarks>
public class WebGpuConversionTests {
    [Fact]
    public void EveryEngineFormatEitherMapsOrIsNamedAsMissing() {
        // The two that have no WebGPU equivalent, and the reason is the same for both: the
        // specification has 16-bit integer and half-float formats and no 16-bit normalised ones.
        PixelFormat[] absent = [PixelFormat.R16UNorm, PixelFormat.Rgba16UNorm];

        foreach (var format in System.Enum.GetValues<PixelFormat>()) {
            if (format == PixelFormat.Undefined) {
                continue;
            }

            var mapped = format.ToWebGpu();

            if (absent.Contains(format)) {
                Assert.Equal(WgpuTextureFormat.Undefined, mapped);
                continue;
            }

            Assert.True(mapped != WgpuTextureFormat.Undefined, $"{format} has no WebGPU format.");
        }
    }

    [Fact]
    public void FormatsRoundTripBackToTheEngine() {
        foreach (var format in System.Enum.GetValues<PixelFormat>()) {
            var mapped = format.ToWebGpu();

            if (mapped == WgpuTextureFormat.Undefined) {
                continue;
            }

            Assert.Equal(format, mapped.ToEngine());
        }
    }

    [Fact]
    public void AMissingFormatSaysWhatIsMissing() {
        var thrown = Assert.Throws<NotSupportedException>(
            () => PixelFormat.Rgba16UNorm.Require("Terrain.Heights")
        );

        Assert.Contains("Terrain.Heights", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("16-bit normalised", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Depth24Plus has no defined byte layout, so it may be an attachment and not a copy.
    /// </summary>
    [Fact]
    public void Depth24PlusIsAnAttachmentAndNotACopy() {
        Assert.Equal(WgpuTextureFormat.Depth24PlusStencil8, PixelFormat.Depth24UNormStencil8.ToWebGpu());
        Assert.False(PixelFormat.Depth24UNormStencil8.CanCopy());
        Assert.True(PixelFormat.Depth32Float.CanCopy());
    }

    /// <summary>
    ///     WebGPU has no border colours, so a shadow sampler's clamp-to-border becomes
    ///     clamp-to-edge — and the test says so, because the alternative is discovering it from a
    ///     smeared cascade edge.
    /// </summary>
    [Fact]
    public void ClampToBorderBecomesClampToEdge() {
        Assert.Equal(WgpuAddressMode.ClampToEdge, WebGpuConversions.ToWebGpu(AddressMode.ClampToBorder));
        Assert.Equal(WgpuAddressMode.ClampToEdge, WebGpuConversions.ToWebGpu(AddressMode.ClampToEdge));
        Assert.Equal(WgpuAddressMode.Repeat, WebGpuConversions.ToWebGpu(AddressMode.Repeat));
        Assert.Equal(WgpuAddressMode.MirrorRepeat, WebGpuConversions.ToWebGpu(AddressMode.MirrorRepeat));
    }

    /// <summary>
    ///     WebGPU has two load operations and no "do not care". Clear is the right of the two: it
    ///     costs a tile fill and never a read from main memory, which is what DontCare exists to
    ///     avoid.
    /// </summary>
    [Fact]
    public void DontCareLoadsBecomeClearsAndNotLoads() {
        Assert.Equal(WgpuLoadOp.Clear, WebGpuConversions.ToWebGpu(LoadAction.DontCare));
        Assert.Equal(WgpuLoadOp.Clear, WebGpuConversions.ToWebGpu(LoadAction.Clear));
        Assert.Equal(WgpuLoadOp.Load, WebGpuConversions.ToWebGpu(LoadAction.Load));
    }

    /// <summary>A resolve is a store operation of discard plus a resolve target.</summary>
    [Fact]
    public void ResolveDiscardsTheSamples() {
        Assert.Equal(WgpuStoreOp.Store, WebGpuConversions.ToWebGpu(StoreAction.Store));
        Assert.Equal(WgpuStoreOp.Discard, WebGpuConversions.ToWebGpu(StoreAction.DontCare));
        Assert.Equal(WgpuStoreOp.Discard, WebGpuConversions.ToWebGpu(StoreAction.Resolve));
    }

    [Fact]
    public void HostUploadBuffersAreCopyDestinations() {
        var usage = WebGpuConversions.ToWebGpu(BufferUsage.Uniform, MemoryAccess.HostUpload);

        Assert.True(usage.HasFlag(WgpuBufferUsage.Uniform));
        Assert.True(usage.HasFlag(WgpuBufferUsage.CopyDst));
        Assert.False(usage.HasFlag(WgpuBufferUsage.MapWrite));
    }

    /// <summary>
    ///     WebGPU forbids MapRead alongside anything but CopyDst, so a readback buffer's other usages
    ///     are dropped rather than merged.
    /// </summary>
    [Fact]
    public void ReadbackBuffersAreMapReadAndCopyDestinationAndNothingElse() {
        var usage = WebGpuConversions.ToWebGpu(
            BufferUsage.Storage | BufferUsage.CopyDestination,
            MemoryAccess.HostReadback
        );

        Assert.Equal(WgpuBufferUsage.MapRead | WgpuBufferUsage.CopyDst, usage);
    }

    [Fact]
    public void BothKindsOfRenderTargetAreOneWebGpuFlag() {
        Assert.Equal(WgpuTextureUsage.RenderAttachment, WebGpuConversions.ToWebGpu(TextureUsage.ColourTarget));

        Assert.Equal(
            WgpuTextureUsage.RenderAttachment,
            WebGpuConversions.ToWebGpu(TextureUsage.DepthStencilTarget)
        );
    }

    /// <summary>
    ///     WebGPU has three shader stages. The rest are dropped, so a layout naming a real stage
    ///     alongside geometry is still the layout it was.
    /// </summary>
    [Fact]
    public void StagesWebGpuLacksAreDropped() {
        var stages = WebGpuConversions.ToWebGpu(ShaderStage.Vertex | ShaderStage.Geometry | ShaderStage.Fragment);
        Assert.Equal(WgpuShaderStage.Vertex | WgpuShaderStage.Fragment, stages);
        Assert.Equal(WgpuShaderStage.None, WebGpuConversions.ToWebGpu(ShaderStage.Mesh));
    }

    /// <summary>
    ///     A cube map is a 2D texture in WebGPU as in Vulkan; "cube" is a property of the view.
    /// </summary>
    [Theory]
    [InlineData(TextureDimension.Texture2D, 1, WgpuTextureViewDimension.Dimension2D)]
    [InlineData(TextureDimension.Texture2D, 4, WgpuTextureViewDimension.Dimension2DArray)]
    [InlineData(TextureDimension.TextureCube, 6, WgpuTextureViewDimension.Cube)]
    [InlineData(TextureDimension.TextureCube, 12, WgpuTextureViewDimension.CubeArray)]
    [InlineData(TextureDimension.Texture3D, 1, WgpuTextureViewDimension.Dimension3D)]
    [InlineData(TextureDimension.Texture1D, 1, WgpuTextureViewDimension.Dimension1D)]
    public void ViewDimensionsFollowTheLayerCount(
        TextureDimension dimension,
        int layers,
        WgpuTextureViewDimension expected
    ) =>
        Assert.Equal(expected, WebGpuConversions.ToViewDimension(dimension, layers));

    /// <summary>
    ///     The depth-or-layers field is one slot for two ideas, and no texture has both.
    /// </summary>
    [Fact]
    public void VolumesUseDepthAndArraysUseLayers() {
        var volume = WebGpuConversions.ToWebGpu(
            new TextureDescription(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Depth: 4, Dimension: TextureDimension.Texture3D)
        );

        var array = WebGpuConversions.ToWebGpu(
            new TextureDescription(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, ArrayLayers: 7)
        );

        Assert.Equal(4, volume.DepthOrArrayLayers);
        Assert.Equal(7, array.DepthOrArrayLayers);
    }

    /// <summary>
    ///     A wholly default blend means opaque, not "write no channels" — the RHI resolves that in
    ///     ColourTargetState.EffectiveBlend, and reading the raw field here would undo it.
    /// </summary>
    [Fact]
    public void ADefaultColourTargetWritesEveryChannel() {
        var target = WebGpuConversions.ToWebGpu(new ColourTargetState(PixelFormat.Rgba8UNorm));

        Assert.False(target.BlendEnabled);
        Assert.Equal(WgpuColorWriteMask.All, target.WriteMask);
    }

    [Fact]
    public void AlphaBlendingMapsBothHalves() {
        var target = WebGpuConversions.ToWebGpu(
            new ColourTargetState(PixelFormat.Rgba8UNorm, BlendState.AlphaBlend)
        );

        Assert.True(target.BlendEnabled);
        Assert.Equal(WgpuBlendFactor.SrcAlpha, target.Colour.SourceFactor);
        Assert.Equal(WgpuBlendFactor.OneMinusSrcAlpha, target.Colour.DestinationFactor);
        Assert.Equal(WgpuBlendFactor.One, target.Alpha.SourceFactor);
        Assert.Equal(WgpuBlendFactor.OneMinusSrcAlpha, target.Alpha.DestinationFactor);
    }

    /// <summary>
    ///     Depth bias lives with the rasterizer in the RHI and with the depth-stencil state in
    ///     WebGPU, so the conversion takes both and this is where that stays true.
    /// </summary>
    [Fact]
    public void DepthBiasMovesFromTheRasterizerToTheDepthState() {
        var depth = WebGpuConversions.ToWebGpu(
            DepthStencilState.Default,
            PixelFormat.Depth32Float,
            new RasterizerState(CullMode.Back, DepthBias: 4f, DepthBiasSlope: 1.5f)
        );

        Assert.Equal(4, depth.DepthBias);
        Assert.Equal(1.5f, depth.DepthBiasSlopeScale);
        Assert.Equal(WgpuCompareFunction.Greater, depth.DepthCompare);
    }

    /// <summary>A pipeline that does not test depth still has to say something, and it is Always.</summary>
    [Fact]
    public void NoDepthTestIsAlways() {
        var depth = WebGpuConversions.ToWebGpu(
            DepthStencilState.Disabled,
            PixelFormat.Depth32Float,
            RasterizerState.Default
        );

        Assert.Equal(WgpuCompareFunction.Always, depth.DepthCompare);
        Assert.False(depth.DepthWriteEnabled);
    }

    /// <summary>
    ///     WebGPU allows anisotropy above one only when all three filters are linear, and rejects the
    ///     combination rather than ignoring it.
    /// </summary>
    [Fact]
    public void PointSamplingGivesUpItsAnisotropy() {
        var point = WebGpuConversions.ToWebGpu(SamplerDescription.PointClamp, true);
        var linear = WebGpuConversions.ToWebGpu(SamplerDescription.LinearRepeat with { Anisotropy = 16f }, true);

        Assert.Equal(1, point.MaxAnisotropy);
        Assert.Equal(16, linear.MaxAnisotropy);
    }

    [Fact]
    public void ADeviceWithoutAnisotropyAsksForNone() {
        var sampler = WebGpuConversions.ToWebGpu(
            SamplerDescription.LinearRepeat with { Anisotropy = 16f },
            false
        );

        Assert.Equal(1, sampler.MaxAnisotropy);
    }

    /// <summary>A buffer size is rounded up to four, which WebGPU requires and callers do not know.</summary>
    [Fact]
    public void BufferSizesAreRoundedUpToFour() {
        var descriptor = WebGpuConversions.ToWebGpu(new BufferDescription(13, BufferUsage.Vertex));
        Assert.Equal(16, descriptor.Size);
    }

    [Theory]
    [InlineData(PrimitiveTopology.TriangleStrip, true)]
    [InlineData(PrimitiveTopology.LineStrip, true)]
    [InlineData(PrimitiveTopology.TriangleList, false)]
    [InlineData(PrimitiveTopology.PointList, false)]
    public void StripsAreToldFromLists(PrimitiveTopology topology, bool strip) =>
        Assert.Equal(strip, WebGpuConversions.IsStrip(topology));

    /// <summary>
    ///     A timeout is not out-of-date, but a caller's options are the same and the RHI has no third
    ///     answer — so it is reported as out-of-date rather than as Ready with no texture.
    /// </summary>
    [Theory]
    [InlineData(WgpuSurfaceStatus.Success, false, SwapChainStatus.Ready)]
    [InlineData(WgpuSurfaceStatus.Success, true, SwapChainStatus.Suboptimal)]
    [InlineData(WgpuSurfaceStatus.Outdated, false, SwapChainStatus.OutOfDate)]
    [InlineData(WgpuSurfaceStatus.Timeout, false, SwapChainStatus.OutOfDate)]
    [InlineData(WgpuSurfaceStatus.Lost, false, SwapChainStatus.DeviceLost)]
    [InlineData(WgpuSurfaceStatus.DeviceLost, false, SwapChainStatus.DeviceLost)]
    public void SurfaceStatusesBecomeSwapChainStatuses(
        WgpuSurfaceStatus status,
        bool suboptimal,
        SwapChainStatus expected
    ) =>
        Assert.Equal(expected, WebGpuConversions.ToEngine(status, suboptimal));

    /// <summary>
    ///     What a browser on the guaranteed floor makes the engine do, asserted on a machine with no
    ///     browser. This is the interesting case: the floor is what a large share of real devices
    ///     report verbatim, and the fallback paths it forces are the ones nobody exercises by
    ///     accident.
    /// </summary>
    [Fact]
    public void TheGuaranteedFloorReportsWhatWebGpuActuallyHas() {
        var features = WebGpuCapabilities.Describe(
            WebGpuLimits.Guaranteed,
            WgpuAdapterType.Unknown,
            _ => false
        );

        Assert.True(features.HasCompute);
        Assert.True(features.HasDynamicRendering);
        Assert.True(features.HasIndependentBlend);
        Assert.False(features.HasGeometryShaders);
        Assert.False(features.HasTessellation);
        Assert.False(features.HasMeshShaders);
        Assert.False(features.HasBindless);
        Assert.False(features.HasMultiDrawIndirect);
        Assert.False(features.HasAsyncCompute);
        Assert.False(features.HasAsyncTransfer);
        Assert.False(features.HasTimelineSemaphores);
        Assert.False(features.HasSparseResources);
        Assert.False(features.HasWireframe);
        Assert.False(features.HasDepthClamp);
        Assert.False(features.HasUnifiedMemory);

        Assert.Equal(8192, features.MaxTextureSize);
        Assert.Equal(4, features.MaxDescriptorSets);
        Assert.Equal(128, features.MaxPushConstantSize);
        Assert.True(features.SupportsSampleCount(1));
        Assert.True(features.SupportsSampleCount(4));
        Assert.False(features.SupportsSampleCount(2));
        Assert.False(features.SupportsSampleCount(8));
    }

    /// <summary>
    ///     A limit reported as zero means "did not say", and believing it produces a device that
    ///     claims it cannot render.
    /// </summary>
    /// <remarks>
    ///     Not hypothetical: wgpu-native 0.19 reports <c>maxColorAttachments</c> as zero on every
    ///     adapter, and the backend duly reported a device with no colour attachments — on an M1 Max
    ///     that had just told it about sixteen thousand texels and eight bind groups.
    /// </remarks>
    [Fact]
    public void AnUnreportedLimitBecomesTheGuaranteedFloor() {
        var reported = WebGpuLimits.Guaranteed with {
            MaxTextureDimension2D = 16384,
            MaxColorAttachments = 0,
            MinUniformBufferOffsetAlignment = 0
        };

        var normalised = reported.OrGuaranteed();

        Assert.Equal(8, normalised.MaxColorAttachments);
        Assert.Equal(256, normalised.MinUniformBufferOffsetAlignment);

        // And what was reported is kept, which is the other half: normalising must not clamp a
        // device down to the floor it exceeded.
        Assert.Equal(16384, normalised.MaxTextureDimension2D);
    }

    [Fact]
    public void ADeviceReportingNothingAtAllStillLooksLikeWebGpu() {
        var features = WebGpuCapabilities.Describe(default, WgpuAdapterType.Unknown, _ => false);

        Assert.Equal(8192, features.MaxTextureSize);
        Assert.Equal(4, features.MaxDescriptorSets);
        Assert.Equal(8, features.MaxColourAttachments);
        Assert.Equal(8, features.MaxVertexBuffers);
        Assert.True(features.MaxComputeWorkgroupSize.X >= 256);
    }

    [Fact]
    public void DepthClampFollowsTheDepthClipControlFeature() {
        var with = WebGpuCapabilities.Describe(
            WebGpuLimits.Guaranteed,
            WgpuAdapterType.DiscreteGpu,
            feature => feature == WgpuFeatureName.DepthClipControl
        );

        Assert.True(with.HasDepthClamp);
    }

    /// <summary>
    ///     An integrated GPU shares the CPU's pool. A browser reports Unknown for everything, so the
    ///     web answer is "assume not" — which costs a staging copy and is never wrong unsafely.
    /// </summary>
    [Theory]
    [InlineData(WgpuAdapterType.IntegratedGpu, true)]
    [InlineData(WgpuAdapterType.DiscreteGpu, false)]
    [InlineData(WgpuAdapterType.Unknown, false)]
    public void UnifiedMemoryFollowsTheAdapterKind(WgpuAdapterType kind, bool unified) =>
        Assert.Equal(unified, WebGpuCapabilities.Describe(WebGpuLimits.Guaranteed, kind, _ => false).HasUnifiedMemory);

    /// <summary>
    ///     Asking for a feature an adapter lacks fails device creation outright, so the request is an
    ///     intersection rather than a wish list.
    /// </summary>
    [Fact]
    public void OnlyFeaturesTheAdapterOffersAreAskedFor() {
        var wanted = WebGpuCapabilities.Wanted(
            feature => feature is WgpuFeatureName.TextureCompressionBc or WgpuFeatureName.DepthClipControl
        );

        Assert.Contains(WgpuFeatureName.TextureCompressionBc, wanted);
        Assert.Contains(WgpuFeatureName.DepthClipControl, wanted);
        Assert.DoesNotContain(WgpuFeatureName.TextureCompressionAstc, wanted);
        Assert.Empty(WebGpuCapabilities.Wanted(_ => false));
    }

    /// <summary>
    ///     A dynamic buffer binding is a different WebGPU binding from a static one, and the layout
    ///     is the only place that says so.
    /// </summary>
    [Fact]
    public void DynamicBufferBindingsSayTheyAreDynamic() {
        var dynamic = WebGpuConversions.ToWebGpu(
            new DescriptorBinding(3, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)
        );

        var fixedBinding = WebGpuConversions.ToWebGpu(
            new DescriptorBinding(3, DescriptorKind.UniformBuffer, ShaderStage.Vertex)
        );

        Assert.True(dynamic.HasDynamicOffset);
        Assert.False(fixedBinding.HasDynamicOffset);
        Assert.Equal(WgpuBufferBindingType.Uniform, dynamic.BufferType);
    }
}
