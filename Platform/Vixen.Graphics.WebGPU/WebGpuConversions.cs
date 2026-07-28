// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>The RHI's vocabulary in WebGPU's terms.</summary>
/// <remarks>
///     <para>
///         Every one of these is a pure function of its argument, so the whole of the backend's
///         decision-making is testable without an implementation, a driver or a browser — which is
///         most of what a backend gets wrong.
///     </para>
///     <para>
///         Where WebGPU has no equivalent the substitution is named here and only here, with what it
///         costs. Three of them are worth knowing before reading any further:
///         <see cref="ToWebGpu(AddressMode)" /> has nowhere to put a border colour,
///         <see cref="ToWebGpu(LoadAction)" /> has no "do not care", and
///         <see cref="ToWebGpu(PresentMode)" /> is <em>not</em> a cast — WebGPU numbers
///         <c>Immediate</c> and <c>Mailbox</c> the other way round from the RHI, and a cast there
///         silently swaps tearing for frame-dropping.
///     </para>
/// </remarks>
public static class WebGpuConversions {
    /// <summary>What a buffer has to be created with, given what it is for and where it lives.</summary>
    /// <param name="usage">Everything it will be used for.</param>
    /// <param name="access">Where its memory lives.</param>
    /// <remarks>
    ///     <para>
    ///         A host-writable buffer gets <see cref="WgpuBufferUsage.CopyDst" /> rather than
    ///         <see cref="WgpuBufferUsage.MapWrite" />: <c>IGraphicsDevice.Write</c> is synchronous
    ///         and WebGPU's map is not, on either surface. <c>queue.writeBuffer</c> is the call that
    ///         has synchronous semantics, and it wants a copy destination.
    ///     </para>
    ///     <para>
    ///         A readback buffer does get <see cref="WgpuBufferUsage.MapRead" />, because there is no
    ///         synchronous alternative — and WebGPU forbids combining it with anything but
    ///         <see cref="WgpuBufferUsage.CopyDst" />, which is why the other flags are dropped
    ///         rather than merged. <c>BufferDescription.Validate</c> already requires a readback
    ///         buffer to be a copy destination.
    ///     </para>
    /// </remarks>
    public static WgpuBufferUsage ToWebGpu(BufferUsage usage, MemoryAccess access) {
        if (access == MemoryAccess.HostReadback) {
            return WgpuBufferUsage.MapRead | WgpuBufferUsage.CopyDst;
        }

        var result = WgpuBufferUsage.None;

        if ((usage & BufferUsage.Vertex) != 0) {
            result |= WgpuBufferUsage.Vertex;
        }

        if ((usage & BufferUsage.Index) != 0) {
            result |= WgpuBufferUsage.Index;
        }

        if ((usage & BufferUsage.Uniform) != 0) {
            result |= WgpuBufferUsage.Uniform;
        }

        if ((usage & BufferUsage.Storage) != 0) {
            result |= WgpuBufferUsage.Storage;
        }

        if ((usage & BufferUsage.Indirect) != 0) {
            result |= WgpuBufferUsage.Indirect;
        }

        if ((usage & BufferUsage.CopySource) != 0) {
            result |= WgpuBufferUsage.CopySrc;
        }

        if ((usage & BufferUsage.CopyDestination) != 0 || access == MemoryAccess.HostUpload) {
            result |= WgpuBufferUsage.CopyDst;
        }

        return result;
    }

    /// <summary>What a texture has to be created with.</summary>
    /// <param name="usage">Everything it will be used for.</param>
    public static WgpuTextureUsage ToWebGpu(TextureUsage usage) {
        var result = WgpuTextureUsage.None;

        if ((usage & TextureUsage.Sampled) != 0) {
            result |= WgpuTextureUsage.TextureBinding;
        }

        if ((usage & TextureUsage.Storage) != 0) {
            result |= WgpuTextureUsage.StorageBinding;
        }

        // One flag for both, because WebGPU does not distinguish: a colour target and a
        // depth-stencil target are both render attachments and the format decides which.
        if ((usage & (TextureUsage.ColourTarget | TextureUsage.DepthStencilTarget)) != 0) {
            result |= WgpuTextureUsage.RenderAttachment;
        }

        if ((usage & TextureUsage.CopySource) != 0) {
            result |= WgpuTextureUsage.CopySrc;
        }

        if ((usage & TextureUsage.CopyDestination) != 0) {
            result |= WgpuTextureUsage.CopyDst;
        }

        return result;
    }

    /// <summary>The WebGPU texture dimension for one of ours.</summary>
    /// <param name="dimension">The engine dimension.</param>
    /// <remarks>
    ///     A cube map is a 2D texture with six layers in WebGPU as in Vulkan — "cube" is a property
    ///     of the <em>view</em>, not of the texture, which is why
    ///     <see cref="ToViewDimension" /> takes the layer count as well.
    /// </remarks>
    public static WgpuTextureDimension ToWebGpu(TextureDimension dimension) => dimension switch {
        TextureDimension.Texture1D => WgpuTextureDimension.Dimension1D,
        TextureDimension.Texture3D => WgpuTextureDimension.Dimension3D,
        _ => WgpuTextureDimension.Dimension2D
    };

    /// <summary>How a view of a texture is read.</summary>
    /// <param name="dimension">The texture's shape.</param>
    /// <param name="arrayLayers">How many layers the view covers.</param>
    public static WgpuTextureViewDimension ToViewDimension(TextureDimension dimension, int arrayLayers) =>
        dimension switch {
            TextureDimension.Texture1D => WgpuTextureViewDimension.Dimension1D,
            TextureDimension.Texture3D => WgpuTextureViewDimension.Dimension3D,
            TextureDimension.TextureCube => arrayLayers > 6
                ? WgpuTextureViewDimension.CubeArray
                : WgpuTextureViewDimension.Cube,
            _ => arrayLayers > 1
                ? WgpuTextureViewDimension.Dimension2DArray
                : WgpuTextureViewDimension.Dimension2D
        };

    /// <summary>Which stages a binding is visible to, in WebGPU's terms.</summary>
    /// <param name="stages">The engine stages.</param>
    /// <remarks>
    ///     WebGPU has three stages and no more. Geometry, tessellation, task and mesh have no
    ///     representation and are dropped rather than rejected: a layout that names them alongside a
    ///     real stage is still a usable layout, and one that names only them is caught by
    ///     <c>DescriptorSetLayoutDescription.Validate</c> upstream — which rejects a binding visible
    ///     to no stage — as soon as it reaches a WebGPU device.
    /// </remarks>
    public static WgpuShaderStage ToWebGpu(ShaderStage stages) {
        var result = WgpuShaderStage.None;

        if ((stages & ShaderStage.Vertex) != 0) {
            result |= WgpuShaderStage.Vertex;
        }

        if ((stages & ShaderStage.Fragment) != 0) {
            result |= WgpuShaderStage.Fragment;
        }

        if ((stages & ShaderStage.Compute) != 0) {
            result |= WgpuShaderStage.Compute;
        }

        return result;
    }

    /// <summary>The WebGPU comparison for one of ours.</summary>
    /// <param name="compare">The engine comparison.</param>
    public static WgpuCompareFunction ToWebGpu(CompareFunction compare) => compare switch {
        CompareFunction.Never => WgpuCompareFunction.Never,
        CompareFunction.Less => WgpuCompareFunction.Less,
        CompareFunction.Equal => WgpuCompareFunction.Equal,
        CompareFunction.LessEqual => WgpuCompareFunction.LessEqual,
        CompareFunction.Greater => WgpuCompareFunction.Greater,
        CompareFunction.NotEqual => WgpuCompareFunction.NotEqual,
        CompareFunction.GreaterEqual => WgpuCompareFunction.GreaterEqual,
        _ => WgpuCompareFunction.Always
    };

    /// <summary>The WebGPU stencil operation for one of ours.</summary>
    /// <param name="operation">The engine operation.</param>
    public static WgpuStencilOperation ToWebGpu(StencilOperation operation) => operation switch {
        StencilOperation.Keep => WgpuStencilOperation.Keep,
        StencilOperation.Zero => WgpuStencilOperation.Zero,
        StencilOperation.Replace => WgpuStencilOperation.Replace,
        StencilOperation.IncrementClamp => WgpuStencilOperation.IncrementClamp,
        StencilOperation.DecrementClamp => WgpuStencilOperation.DecrementClamp,
        StencilOperation.Invert => WgpuStencilOperation.Invert,
        StencilOperation.IncrementWrap => WgpuStencilOperation.IncrementWrap,
        _ => WgpuStencilOperation.DecrementWrap
    };

    /// <summary>The WebGPU cull mode for one of ours.</summary>
    /// <param name="cull">The engine cull mode.</param>
    public static WgpuCullMode ToWebGpu(CullMode cull) => cull switch {
        CullMode.Front => WgpuCullMode.Front,
        CullMode.Back => WgpuCullMode.Back,
        _ => WgpuCullMode.None
    };

    /// <summary>The WebGPU winding for one of ours.</summary>
    /// <param name="face">The engine winding.</param>
    public static WgpuFrontFace ToWebGpu(FrontFace face) =>
        face == FrontFace.Clockwise ? WgpuFrontFace.Cw : WgpuFrontFace.Ccw;

    /// <summary>The WebGPU topology for one of ours.</summary>
    /// <param name="topology">The engine topology.</param>
    public static WgpuPrimitiveTopology ToWebGpu(PrimitiveTopology topology) => topology switch {
        PrimitiveTopology.PointList => WgpuPrimitiveTopology.PointList,
        PrimitiveTopology.LineList => WgpuPrimitiveTopology.LineList,
        PrimitiveTopology.LineStrip => WgpuPrimitiveTopology.LineStrip,
        PrimitiveTopology.TriangleStrip => WgpuPrimitiveTopology.TriangleStrip,
        _ => WgpuPrimitiveTopology.TriangleList
    };

    /// <summary>Whether a topology is a strip, and therefore needs a strip index format.</summary>
    /// <param name="topology">The engine topology.</param>
    /// <remarks>
    ///     WebGPU requires <c>stripIndexFormat</c> on a strip pipeline and <em>forbids</em> it on a
    ///     list one, so this is a validation rule rather than a nicety.
    /// </remarks>
    public static bool IsStrip(PrimitiveTopology topology) =>
        topology is PrimitiveTopology.LineStrip or PrimitiveTopology.TriangleStrip;

    /// <summary>The WebGPU index format for one of ours.</summary>
    /// <param name="format">The engine format.</param>
    public static WgpuIndexFormat ToWebGpu(IndexFormat format) =>
        format == IndexFormat.UInt32 ? WgpuIndexFormat.Uint32 : WgpuIndexFormat.Uint16;

    /// <summary>The WebGPU address mode for one of ours.</summary>
    /// <param name="mode">The engine mode.</param>
    /// <remarks>
    ///     <b><see cref="AddressMode.ClampToBorder" /> becomes
    ///     <see cref="WgpuAddressMode.ClampToEdge" />, and that is visible.</b> WebGPU has no border
    ///     colours at all, so there is nothing else to map it to. The case that notices is
    ///     <see cref="SamplerDescription.Shadow" />, which clamps to an opaque-white border so that
    ///     everything outside a shadow map reads as lit: on WebGPU it reads as whatever the map's
    ///     edge texel says instead, which smears the cascade's edge outward. A renderer that cares
    ///     clamps the lookup itself, and every renderer targeting the web has to.
    /// </remarks>
    public static WgpuAddressMode ToWebGpu(AddressMode mode) => mode switch {
        AddressMode.MirrorRepeat => WgpuAddressMode.MirrorRepeat,
        AddressMode.ClampToEdge or AddressMode.ClampToBorder => WgpuAddressMode.ClampToEdge,
        _ => WgpuAddressMode.Repeat
    };

    /// <summary>The WebGPU filter for one of ours.</summary>
    /// <param name="filter">The engine filter.</param>
    public static WgpuFilterMode ToWebGpu(FilterMode filter) =>
        filter == FilterMode.Nearest ? WgpuFilterMode.Nearest : WgpuFilterMode.Linear;

    /// <summary>The WebGPU mip filter for one of ours.</summary>
    /// <param name="filter">The engine filter.</param>
    public static WgpuMipmapFilterMode ToMipmapFilter(FilterMode filter) =>
        filter == FilterMode.Nearest ? WgpuMipmapFilterMode.Nearest : WgpuMipmapFilterMode.Linear;

    /// <summary>The WebGPU blend factor for one of ours.</summary>
    /// <param name="factor">The engine factor.</param>
    public static WgpuBlendFactor ToWebGpu(BlendFactor factor) => factor switch {
        BlendFactor.Zero => WgpuBlendFactor.Zero,
        BlendFactor.One => WgpuBlendFactor.One,
        BlendFactor.SourceColour => WgpuBlendFactor.Src,
        BlendFactor.OneMinusSourceColour => WgpuBlendFactor.OneMinusSrc,
        BlendFactor.SourceAlpha => WgpuBlendFactor.SrcAlpha,
        BlendFactor.OneMinusSourceAlpha => WgpuBlendFactor.OneMinusSrcAlpha,
        BlendFactor.DestinationColour => WgpuBlendFactor.Dst,
        BlendFactor.OneMinusDestinationColour => WgpuBlendFactor.OneMinusDst,
        BlendFactor.DestinationAlpha => WgpuBlendFactor.DstAlpha,
        BlendFactor.OneMinusDestinationAlpha => WgpuBlendFactor.OneMinusDstAlpha,
        BlendFactor.Constant => WgpuBlendFactor.Constant,
        BlendFactor.OneMinusConstant => WgpuBlendFactor.OneMinusConstant,
        _ => WgpuBlendFactor.SrcAlphaSaturated
    };

    /// <summary>The WebGPU blend operation for one of ours.</summary>
    /// <param name="operation">The engine operation.</param>
    public static WgpuBlendOperation ToWebGpu(BlendOperation operation) => operation switch {
        BlendOperation.Subtract => WgpuBlendOperation.Subtract,
        BlendOperation.ReverseSubtract => WgpuBlendOperation.ReverseSubtract,
        BlendOperation.Min => WgpuBlendOperation.Min,
        BlendOperation.Max => WgpuBlendOperation.Max,
        _ => WgpuBlendOperation.Add
    };

    /// <summary>The WebGPU write mask for one of ours.</summary>
    /// <param name="mask">The engine mask.</param>
    public static WgpuColorWriteMask ToWebGpu(ColourWriteMask mask) {
        var result = WgpuColorWriteMask.None;

        if ((mask & ColourWriteMask.Red) != 0) {
            result |= WgpuColorWriteMask.Red;
        }

        if ((mask & ColourWriteMask.Green) != 0) {
            result |= WgpuColorWriteMask.Green;
        }

        if ((mask & ColourWriteMask.Blue) != 0) {
            result |= WgpuColorWriteMask.Blue;
        }

        if ((mask & ColourWriteMask.Alpha) != 0) {
            result |= WgpuColorWriteMask.Alpha;
        }

        return result;
    }

    /// <summary>The WebGPU load operation for one of ours.</summary>
    /// <param name="load">The engine action.</param>
    /// <remarks>
    ///     <b><see cref="LoadAction.DontCare" /> becomes <see cref="WgpuLoadOp.Clear" />.</b> WebGPU
    ///     has exactly two load operations and no way to say "the pass overwrites every pixel". Clear
    ///     is the right of the two: it costs a tile fill and never a read from main memory, which is
    ///     what <c>DontCare</c> exists to avoid. Mapping it to <see cref="WgpuLoadOp.Load" /> would
    ///     have been the expensive answer <em>and</em> would have made an uninitialised attachment
    ///     read back whatever was in it.
    /// </remarks>
    public static WgpuLoadOp ToWebGpu(LoadAction load) =>
        load == LoadAction.Load ? WgpuLoadOp.Load : WgpuLoadOp.Clear;

    /// <summary>The WebGPU store operation for one of ours.</summary>
    /// <param name="store">The engine action.</param>
    /// <remarks>
    ///     <see cref="StoreAction.Resolve" /> becomes <see cref="WgpuStoreOp.Discard" />, which is
    ///     not a mistake: the resolve is expressed by the attachment's resolve target, and the store
    ///     operation then says what to do with the multisampled data afterwards. Keeping it would be
    ///     writing samples nobody reads.
    /// </remarks>
    public static WgpuStoreOp ToWebGpu(StoreAction store) =>
        store == StoreAction.Store ? WgpuStoreOp.Store : WgpuStoreOp.Discard;

    /// <summary>The WebGPU present mode for one of ours.</summary>
    /// <param name="mode">The engine mode.</param>
    /// <remarks>
    ///     <b>Not a cast.</b> The RHI numbers <c>Mailbox</c> 2 and <c>Immediate</c> 3; WebGPU numbers
    ///     them the other way round. A cast compiles, runs, and quietly gives a player tearing where
    ///     they asked for dropped frames.
    /// </remarks>
    public static WgpuPresentMode ToWebGpu(PresentMode mode) => mode switch {
        PresentMode.FifoRelaxed => WgpuPresentMode.FifoRelaxed,
        PresentMode.Mailbox => WgpuPresentMode.Mailbox,
        PresentMode.Immediate => WgpuPresentMode.Immediate,
        _ => WgpuPresentMode.Fifo
    };

    /// <summary>What a surface acquisition means to the RHI.</summary>
    /// <param name="status">What WebGPU said.</param>
    /// <param name="suboptimal">Whether it also said the configuration no longer matches.</param>
    public static SwapChainStatus ToEngine(WgpuSurfaceStatus status, bool suboptimal) => status switch {
        WgpuSurfaceStatus.Success => suboptimal ? SwapChainStatus.Suboptimal : SwapChainStatus.Ready,

        // Timeout is not out-of-date, but the caller's options are the same — skip the frame and try
        // again — and the RHI has no third answer. Reported as out-of-date so a renderer that
        // reconfigures on it does no harm, rather than as Ready with no texture, which would crash it.
        WgpuSurfaceStatus.Outdated or WgpuSurfaceStatus.Timeout => SwapChainStatus.OutOfDate,
        _ => SwapChainStatus.DeviceLost
    };

    /// <summary>What a descriptor binding is, in WebGPU's terms.</summary>
    /// <param name="binding">The engine binding.</param>
    /// <remarks>
    ///     <para>
    ///         The one thing WebGPU wants that the RHI does not say: a sampled-texture binding has to
    ///         declare how its texels are read — float, unfilterable float, depth or integer — and a
    ///         sampler binding has to declare whether it compares. <see cref="DescriptorBinding" />
    ///         carries a kind, some stages and a count, and nothing about formats.
    ///     </para>
    ///     <para>
    ///         So the common case is assumed: a filterable float texture and a filtering sampler.
    ///         <b>A shadow map is the case that is not the common case</b>, and binding a depth
    ///         texture or a comparison sampler through a layout built this way is rejected — clearly,
    ///         and by <c>WebGpuDevice.UpdateDescriptorSet</c> rather than by an implementation error
    ///         message a frame later. See the README's known gaps: closing it means the RHI's binding
    ///         description growing a sample type, which every other backend would ignore.
    ///     </para>
    /// </remarks>
    public static WgpuBindGroupLayoutEntry ToWebGpu(in DescriptorBinding binding) {
        var visibility = ToWebGpu(binding.Stages);

        return binding.Kind switch {
            DescriptorKind.UniformBuffer => new(binding.Binding, visibility, WgpuBufferBindingType.Uniform),
            DescriptorKind.DynamicUniformBuffer => new(
                binding.Binding,
                visibility,
                WgpuBufferBindingType.Uniform,
                HasDynamicOffset: true
            ),
            DescriptorKind.StorageBuffer => new(binding.Binding, visibility, WgpuBufferBindingType.Storage),
            DescriptorKind.DynamicStorageBuffer => new(
                binding.Binding,
                visibility,
                WgpuBufferBindingType.Storage,
                HasDynamicOffset: true
            ),
            DescriptorKind.SampledTexture => new(
                binding.Binding,
                visibility,
                TextureSampleType: WgpuTextureSampleType.Float,
                TextureViewDimension: WgpuTextureViewDimension.Dimension2D
            ),
            DescriptorKind.StorageTexture => new(
                binding.Binding,
                visibility,
                StorageAccess: WgpuStorageTextureAccess.WriteOnly,
                StorageFormat: WgpuTextureFormat.Rgba8Unorm,
                TextureViewDimension: WgpuTextureViewDimension.Dimension2D
            ),
            _ => new(binding.Binding, visibility, SamplerType: WgpuSamplerBindingType.Filtering)
        };
    }

    /// <summary>What to create a WebGPU buffer as, for one of ours.</summary>
    /// <param name="description">The engine description.</param>
    public static WgpuBufferDescriptor ToWebGpu(in BufferDescription description) => new(
        // WebGPU requires a buffer size to be a multiple of four. Rounding up is invisible: the
        // engine's own bounds checks use the requested size, so the padding is unreachable.
        (description.Size + 3) & ~3L,
        ToWebGpu(description.Usage, description.Access),
        description.Name
    );

    /// <summary>What to create a WebGPU texture as, for one of ours.</summary>
    /// <param name="description">The engine description.</param>
    public static WgpuTextureDescriptor ToWebGpu(in TextureDescription description) => new(
        description.Format.Require(description.Name),
        description.Width,
        description.Height,

        // One field for two ideas, which is WebGPU's shape: a 3D texture's depth and a 2D array's
        // layer count share a slot, and no texture has both.
        description.Dimension == TextureDimension.Texture3D ? description.Depth : description.ArrayLayers,
        description.EffectiveMipLevels,
        description.SampleCount,
        ToWebGpu(description.Dimension),
        ToWebGpu(description.Usage),
        description.Name
    );

    /// <summary>What to create a WebGPU sampler as, for one of ours.</summary>
    /// <param name="description">The engine description.</param>
    /// <param name="anisotropySupported">Whether the device filters anisotropically.</param>
    public static WgpuSamplerDescriptor ToWebGpu(in SamplerDescription description, bool anisotropySupported) {
        var anisotropy = anisotropySupported ? (ushort)Math.Clamp((int)description.Anisotropy, 1, 16) : (ushort)1;

        // WebGPU allows anisotropy above one only when all three filters are linear, and rejects the
        // combination rather than ignoring it. Every renderer that asks for 16× on a point-sampled
        // lookup texture means "as sharp as possible", so the anisotropy is what gives way.
        if (description.MinFilter != FilterMode.Linear
            || description.MagFilter != FilterMode.Linear
            || description.MipFilter != FilterMode.Linear) {
            anisotropy = 1;
        }

        return new(
            ToWebGpu(description.AddressU),
            ToWebGpu(description.AddressV),
            ToWebGpu(description.AddressW),
            ToWebGpu(description.MagFilter),
            ToWebGpu(description.MinFilter),
            ToMipmapFilter(description.MipFilter),
            description.MinLod,
            description.MaxLod,
            description.Compare is { } compare ? ToWebGpu(compare) : WgpuCompareFunction.Undefined,
            anisotropy,
            description.Name
        );
    }

    /// <summary>The blend equation to build a colour target from.</summary>
    /// <param name="target">The engine target.</param>
    public static WgpuColourTargetState ToWebGpu(in ColourTargetState target) {
        // EffectiveBlend rather than Blend: a wholly default BlendState is C#'s all-zeros, which
        // spells "write no colour channels". The RHI resolves that once, and reading the raw field
        // here would undo it — see ColourTargetState.EffectiveBlend.
        var blend = target.EffectiveBlend;

        return new(
            target.Format.Require("colour target"),
            blend.Enabled,
            new(
                ToWebGpu(blend.ColourOperation),
                ToWebGpu(blend.SourceColour),
                ToWebGpu(blend.DestinationColour)
            ),
            new(
                ToWebGpu(blend.AlphaOperation),
                ToWebGpu(blend.SourceAlpha),
                ToWebGpu(blend.DestinationAlpha)
            ),
            ToWebGpu(blend.WriteMask)
        );
    }

    /// <summary>The depth-stencil state to build a pipeline from.</summary>
    /// <param name="state">The engine state.</param>
    /// <param name="format">The depth attachment's format.</param>
    /// <param name="rasterizer">The rasterizer state, which is where depth bias lives in the RHI.</param>
    /// <remarks>
    ///     Depth bias moves house here: the RHI keeps it with the rasterizer, as Vulkan does, and
    ///     WebGPU keeps it with the depth-stencil state. Passing both is what makes that visible at
    ///     the call site rather than a field that quietly never arrives.
    /// </remarks>
    public static WgpuDepthStencilState ToWebGpu(
        in DepthStencilState state,
        PixelFormat format,
        in RasterizerState rasterizer
    ) => new(
        format.Require("depth attachment"),
        state.DepthWrite,

        // A pipeline that does not test depth still has to say something, and WebGPU's answer for
        // "no test" is Always rather than an absent comparison.
        state.DepthTest ? ToWebGpu(state.DepthCompare) : WgpuCompareFunction.Always,
        state.StencilTest ? ToWebGpu(state.Front) : PassThroughStencil,
        state.StencilTest ? ToWebGpu(state.Back) : PassThroughStencil,
        state.StencilReadMask,
        state.StencilWriteMask,
        (int)rasterizer.DepthBias,
        rasterizer.DepthBiasSlope,
        0f
    );

    /// <summary>The stencil face state for one of ours.</summary>
    /// <param name="face">The engine state.</param>
    public static WgpuStencilFaceState ToWebGpu(in StencilFaceState face) => new(
        ToWebGpu(face.Compare),
        ToWebGpu(face.Fail),
        ToWebGpu(face.DepthFail),
        ToWebGpu(face.Pass)
    );

    /// <summary>A stencil face that always passes and never writes.</summary>
    static WgpuStencilFaceState PassThroughStencil => new(
        WgpuCompareFunction.Always,
        WgpuStencilOperation.Keep,
        WgpuStencilOperation.Keep,
        WgpuStencilOperation.Keep
    );
}
