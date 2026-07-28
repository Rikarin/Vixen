// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>Something a WebGPU implementation owns, referred to by an opaque token.</summary>
/// <remarks>
///     <para>
///         A pointer on the native surface and a table index in the browser, and neither is the
///         backend's business — which is the point. The backend holds these, hands them back, and
///         never dereferences one.
///     </para>
///     <para>
///         Not a <see cref="Vixen.Core.Collections.Handle{T}" />: those are generation-checked slots
///         in a table this engine owns, and these are neither. Use-after-free is caught a layer up,
///         where the RHI's handles live.
///     </para>
/// </remarks>
/// <param name="Value">The token.</param>
public readonly record struct WebGpuObject(ulong Value) {
    /// <summary>No object.</summary>
    public static WebGpuObject Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != 0;
}

/// <summary>What to create a WebGPU buffer as.</summary>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Usage">Everything it will be used for.</param>
/// <param name="Label">A name for the debugger and the implementation's error messages.</param>
public readonly record struct WgpuBufferDescriptor(long Size, WgpuBufferUsage Usage, string Label = "");

/// <summary>What to create a WebGPU texture as.</summary>
/// <param name="Format">Its format.</param>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height in texels.</param>
/// <param name="DepthOrArrayLayers">Its depth, or its array layer count.</param>
/// <param name="MipLevelCount">How many mip levels.</param>
/// <param name="SampleCount">How many samples per texel.</param>
/// <param name="Dimension">Its shape.</param>
/// <param name="Usage">Everything it will be used for.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuTextureDescriptor(
    WgpuTextureFormat Format,
    int Width,
    int Height,
    int DepthOrArrayLayers,
    int MipLevelCount,
    int SampleCount,
    WgpuTextureDimension Dimension,
    WgpuTextureUsage Usage,
    string Label = ""
);

/// <summary>What to create a view of a texture as.</summary>
/// <param name="Format">The format to read it as.</param>
/// <param name="Dimension">How to read it.</param>
/// <param name="BaseMipLevel">The first mip level.</param>
/// <param name="MipLevelCount">How many levels.</param>
/// <param name="BaseArrayLayer">The first array layer.</param>
/// <param name="ArrayLayerCount">How many layers.</param>
/// <param name="Aspect">Which planes.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuTextureViewDescriptor(
    WgpuTextureFormat Format,
    WgpuTextureViewDimension Dimension,
    int BaseMipLevel,
    int MipLevelCount,
    int BaseArrayLayer,
    int ArrayLayerCount,
    WgpuTextureAspect Aspect,
    string Label = ""
);

/// <summary>What to create a WebGPU sampler as.</summary>
/// <param name="AddressU">What to do outside <c>[0, 1]</c> horizontally.</param>
/// <param name="AddressV">What to do outside <c>[0, 1]</c> vertically.</param>
/// <param name="AddressW">What to do outside <c>[0, 1]</c> in depth.</param>
/// <param name="MagFilter">How to filter when magnified.</param>
/// <param name="MinFilter">How to filter when minified.</param>
/// <param name="MipmapFilter">How to filter between levels.</param>
/// <param name="LodMinClamp">The lowest level to sample.</param>
/// <param name="LodMaxClamp">The highest level to sample.</param>
/// <param name="Compare">The shadow comparison, or
/// <see cref="WgpuCompareFunction.Undefined" /> for an ordinary sampler.</param>
/// <param name="MaxAnisotropy">How many anisotropic samples.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuSamplerDescriptor(
    WgpuAddressMode AddressU,
    WgpuAddressMode AddressV,
    WgpuAddressMode AddressW,
    WgpuFilterMode MagFilter,
    WgpuFilterMode MinFilter,
    WgpuMipmapFilterMode MipmapFilter,
    float LodMinClamp,
    float LodMaxClamp,
    WgpuCompareFunction Compare,
    ushort MaxAnisotropy,
    string Label = ""
);

/// <summary>How shader source or bytecode is spelled.</summary>
public enum WgpuShaderSource : byte {
    /// <summary>WGSL source. The only form a browser accepts.</summary>
    Wgsl = 0,

    /// <summary>SPIR-V. Accepted by Dawn and wgpu-native, never by a browser.</summary>
    SpirV = 1
}

/// <summary>What to create a shader module from.</summary>
/// <param name="Source">Which form <paramref name="Code" /> is in.</param>
/// <param name="Code">The module, as UTF-8 WGSL or as SPIR-V words.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuShaderModuleDescriptor(
    WgpuShaderSource Source,
    byte[] Code,
    string Label = ""
);

/// <summary>One binding in a bind group layout.</summary>
/// <param name="Binding">Its index within the group.</param>
/// <param name="Visibility">Which stages see it.</param>
/// <param name="BufferType">What kind of buffer, if it is one.</param>
/// <param name="HasDynamicOffset">Whether the buffer is bound with a per-draw offset.</param>
/// <param name="SamplerType">What kind of sampler, if it is one.</param>
/// <param name="TextureSampleType">How a sampled texture's texels are read, if it is one.</param>
/// <param name="TextureViewDimension">How a texture binding is read.</param>
/// <param name="Multisampled">Whether a texture binding is multisampled.</param>
/// <param name="StorageAccess">How a storage texture is reached, if it is one.</param>
/// <param name="StorageFormat">A storage texture's format.</param>
public readonly record struct WgpuBindGroupLayoutEntry(
    uint Binding,
    WgpuShaderStage Visibility,
    WgpuBufferBindingType BufferType = WgpuBufferBindingType.Undefined,
    bool HasDynamicOffset = false,
    WgpuSamplerBindingType SamplerType = WgpuSamplerBindingType.Undefined,
    WgpuTextureSampleType TextureSampleType = WgpuTextureSampleType.Undefined,
    WgpuTextureViewDimension TextureViewDimension = WgpuTextureViewDimension.Undefined,
    bool Multisampled = false,
    WgpuStorageTextureAccess StorageAccess = WgpuStorageTextureAccess.Undefined,
    WgpuTextureFormat StorageFormat = WgpuTextureFormat.Undefined
);

/// <summary>What to create a bind group layout as.</summary>
/// <param name="Entries">What it contains.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuBindGroupLayoutDescriptor(
    WgpuBindGroupLayoutEntry[] Entries,
    string Label = ""
);

/// <summary>What to create a pipeline layout as.</summary>
/// <param name="BindGroupLayouts">The group layouts, in group order.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuPipelineLayoutDescriptor(
    WebGpuObject[] BindGroupLayouts,
    string Label = ""
);

/// <summary>One resource bound into a bind group.</summary>
/// <param name="Binding">Which binding within the group.</param>
/// <param name="Buffer">The buffer, for a buffer binding.</param>
/// <param name="Offset">Where in the buffer it starts.</param>
/// <param name="Size">How much of the buffer.</param>
/// <param name="Sampler">The sampler, for a sampler binding.</param>
/// <param name="TextureView">The view, for a texture binding.</param>
public readonly record struct WgpuBindGroupEntry(
    uint Binding,
    WebGpuObject Buffer = default,
    long Offset = 0,
    long Size = 0,
    WebGpuObject Sampler = default,
    WebGpuObject TextureView = default
);

/// <summary>What to create a bind group as.</summary>
/// <param name="Layout">Its shape.</param>
/// <param name="Entries">What it binds.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuBindGroupDescriptor(
    WebGpuObject Layout,
    WgpuBindGroupEntry[] Entries,
    string Label = ""
);

/// <summary>One vertex attribute.</summary>
/// <param name="Format">Its type.</param>
/// <param name="Offset">Its offset within the vertex, in bytes.</param>
/// <param name="ShaderLocation">Its shader location.</param>
public readonly record struct WgpuVertexElement(WgpuVertexFormat Format, long Offset, uint ShaderLocation);

/// <summary>One vertex buffer's layout.</summary>
/// <param name="ArrayStride">Bytes between consecutive elements.</param>
/// <param name="StepMode">Whether it advances per vertex or per instance.</param>
/// <param name="Attributes">What an element contains.</param>
public readonly record struct WgpuVertexBufferLayout(
    long ArrayStride,
    WgpuVertexStepMode StepMode,
    WgpuVertexElement[] Attributes
);

/// <summary>One half of a blend equation.</summary>
/// <param name="Operation">How the two sides combine.</param>
/// <param name="SourceFactor">The source factor.</param>
/// <param name="DestinationFactor">The destination factor.</param>
public readonly record struct WgpuBlendComponent(
    WgpuBlendOperation Operation,
    WgpuBlendFactor SourceFactor,
    WgpuBlendFactor DestinationFactor
);

/// <summary>One colour target of a render pipeline.</summary>
/// <param name="Format">Its format.</param>
/// <param name="BlendEnabled">Whether to blend at all.</param>
/// <param name="Colour">The colour blend equation.</param>
/// <param name="Alpha">The alpha blend equation.</param>
/// <param name="WriteMask">Which channels may be written.</param>
public readonly record struct WgpuColourTargetState(
    WgpuTextureFormat Format,
    bool BlendEnabled,
    WgpuBlendComponent Colour,
    WgpuBlendComponent Alpha,
    WgpuColorWriteMask WriteMask
);

/// <summary>What the stencil test does on one side.</summary>
/// <param name="Compare">The comparison.</param>
/// <param name="FailOp">What to do when the stencil test fails.</param>
/// <param name="DepthFailOp">What to do when stencil passes and depth fails.</param>
/// <param name="PassOp">What to do when both pass.</param>
public readonly record struct WgpuStencilFaceState(
    WgpuCompareFunction Compare,
    WgpuStencilOperation FailOp,
    WgpuStencilOperation DepthFailOp,
    WgpuStencilOperation PassOp
);

/// <summary>The depth and stencil state of a render pipeline.</summary>
/// <param name="Format">The depth attachment's format.</param>
/// <param name="DepthWriteEnabled">Whether depth is written.</param>
/// <param name="DepthCompare">The depth comparison.</param>
/// <param name="Front">The stencil test for front faces.</param>
/// <param name="Back">The stencil test for back faces.</param>
/// <param name="StencilReadMask">Which stencil bits the comparison sees.</param>
/// <param name="StencilWriteMask">Which stencil bits may be written.</param>
/// <param name="DepthBias">A constant added to depth.</param>
/// <param name="DepthBiasSlopeScale">A factor on the polygon's depth slope.</param>
/// <param name="DepthBiasClamp">The largest bias that may be applied.</param>
public readonly record struct WgpuDepthStencilState(
    WgpuTextureFormat Format,
    bool DepthWriteEnabled,
    WgpuCompareFunction DepthCompare,
    WgpuStencilFaceState Front,
    WgpuStencilFaceState Back,
    uint StencilReadMask,
    uint StencilWriteMask,
    int DepthBias,
    float DepthBiasSlopeScale,
    float DepthBiasClamp
);

/// <summary>What to compile a render pipeline from.</summary>
/// <param name="Layout">The bind group layouts and their order.</param>
/// <param name="VertexModule">The vertex shader module.</param>
/// <param name="VertexEntryPoint">Its entry point.</param>
/// <param name="VertexBuffers">The vertex buffer layouts, in binding order.</param>
/// <param name="FragmentModule">The fragment shader module, or
/// <see cref="WebGpuObject.Null" /> for a depth-only pipeline.</param>
/// <param name="FragmentEntryPoint">Its entry point.</param>
/// <param name="ColourTargets">The colour targets, in order.</param>
/// <param name="Topology">What the vertices mean.</param>
/// <param name="StripIndexFormat">The index format, for a strip topology.</param>
/// <param name="FrontFace">Which winding is front.</param>
/// <param name="CullMode">Which faces to discard.</param>
/// <param name="UnclippedDepth">Whether depth is clamped rather than clipped.</param>
/// <param name="DepthStencil">The depth and stencil state, or <see langword="null" /> for
/// none.</param>
/// <param name="SampleCount">How many samples the attachments have.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuRenderPipelineDescriptor(
    WebGpuObject Layout,
    WebGpuObject VertexModule,
    string VertexEntryPoint,
    WgpuVertexBufferLayout[] VertexBuffers,
    WebGpuObject FragmentModule,
    string FragmentEntryPoint,
    WgpuColourTargetState[] ColourTargets,
    WgpuPrimitiveTopology Topology,
    WgpuIndexFormat StripIndexFormat,
    WgpuFrontFace FrontFace,
    WgpuCullMode CullMode,
    bool UnclippedDepth,
    WgpuDepthStencilState? DepthStencil,
    int SampleCount,
    string Label = ""
);

/// <summary>What to compile a compute pipeline from.</summary>
/// <param name="Layout">The bind group layouts and their order.</param>
/// <param name="Module">The compute shader module.</param>
/// <param name="EntryPoint">Its entry point.</param>
/// <param name="Label">A name for the debugger.</param>
public readonly record struct WgpuComputePipelineDescriptor(
    WebGpuObject Layout,
    WebGpuObject Module,
    string EntryPoint,
    string Label = ""
);

/// <summary>One colour attachment of a render pass.</summary>
/// <param name="View">What to render into.</param>
/// <param name="ResolveTarget">Where to resolve a multisampled attachment.</param>
/// <param name="LoadOp">What to do with it at the start of the pass.</param>
/// <param name="StoreOp">What to do with it at the end.</param>
/// <param name="ClearR">The red channel of the clear value.</param>
/// <param name="ClearG">The green channel.</param>
/// <param name="ClearB">The blue channel.</param>
/// <param name="ClearA">The alpha channel.</param>
public readonly record struct WgpuColourAttachment(
    WebGpuObject View,
    WebGpuObject ResolveTarget,
    WgpuLoadOp LoadOp,
    WgpuStoreOp StoreOp,
    double ClearR,
    double ClearG,
    double ClearB,
    double ClearA
);

/// <summary>The depth-stencil attachment of a render pass.</summary>
/// <param name="View">What to render into.</param>
/// <param name="DepthLoadOp">What to do with depth at the start.</param>
/// <param name="DepthStoreOp">What to do with depth at the end.</param>
/// <param name="DepthClearValue">What to clear depth to.</param>
/// <param name="DepthReadOnly">Whether depth is only tested.</param>
/// <param name="StencilLoadOp">What to do with stencil at the start.</param>
/// <param name="StencilStoreOp">What to do with stencil at the end.</param>
/// <param name="StencilClearValue">What to clear stencil to.</param>
/// <param name="StencilReadOnly">Whether stencil is only tested.</param>
public readonly record struct WgpuDepthStencilAttachment(
    WebGpuObject View,
    WgpuLoadOp DepthLoadOp,
    WgpuStoreOp DepthStoreOp,
    float DepthClearValue,
    bool DepthReadOnly,
    WgpuLoadOp StencilLoadOp,
    WgpuStoreOp StencilStoreOp,
    uint StencilClearValue,
    bool StencilReadOnly
);

/// <summary>What a render pass renders into.</summary>
/// <param name="ColourAttachments">The colour attachments, in order.</param>
/// <param name="ColourAttachmentCount">How many of them are in use.</param>
/// <param name="DepthStencil">The depth-stencil attachment, or <see langword="null" />.</param>
/// <param name="Label">A name for the debugger and the capture.</param>
/// <remarks>
///     Takes an array plus a count rather than a right-sized array, because a pass descriptor is
///     built once per pass per frame and allocating one there is an allocation in the frame loop.
///     The recorder keeps a grown-once buffer and passes the live prefix.
/// </remarks>
public readonly record struct WgpuRenderPassDescriptor(
    WgpuColourAttachment[] ColourAttachments,
    int ColourAttachmentCount,
    WgpuDepthStencilAttachment? DepthStencil,
    string Label = ""
);

/// <summary>Where a texture copy reads from or writes to.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="MipLevel">Which mip level.</param>
/// <param name="OriginX">The left edge of the region, in texels.</param>
/// <param name="OriginY">The top edge.</param>
/// <param name="OriginZ">The first slice or array layer.</param>
/// <param name="Aspect">Which planes.</param>
public readonly record struct WgpuImageCopyTexture(
    WebGpuObject Texture,
    int MipLevel,
    int OriginX,
    int OriginY,
    int OriginZ,
    WgpuTextureAspect Aspect
);

/// <summary>Where a texture copy's linear side reads from or writes to.</summary>
/// <param name="Buffer">The buffer.</param>
/// <param name="Offset">Where its data starts, in bytes.</param>
/// <param name="BytesPerRow">The distance between rows, in bytes.</param>
/// <param name="RowsPerImage">The distance between slices, in rows.</param>
public readonly record struct WgpuImageCopyBuffer(
    WebGpuObject Buffer,
    long Offset,
    int BytesPerRow,
    int RowsPerImage
);

/// <summary>How to configure a surface for presentation.</summary>
/// <param name="Format">The format its textures are in.</param>
/// <param name="Usage">What they may be used for.</param>
/// <param name="Width">Their width in pixels.</param>
/// <param name="Height">Their height in pixels.</param>
/// <param name="PresentMode">How it waits for the display.</param>
/// <param name="AlphaMode">How its alpha composites.</param>
public readonly record struct WgpuSurfaceConfiguration(
    WgpuTextureFormat Format,
    WgpuTextureUsage Usage,
    int Width,
    int Height,
    WgpuPresentMode PresentMode,
    WgpuCompositeAlphaMode AlphaMode
);

/// <summary>What a WebGPU implementation reports it can do.</summary>
/// <remarks>
///     Every field is one of <c>WGPULimits</c>'s, kept in its own record so
///     <see cref="WebGpuCapabilities" /> can derive <see cref="GraphicsDeviceFeatures" /> from it in
///     a pure function a test can drive with a browser's numbers on a machine that has no browser.
/// </remarks>
public readonly record struct WebGpuLimits {
    /// <summary>The largest 2D texture edge, in texels.</summary>
    public int MaxTextureDimension2D { get; init; }

    /// <summary>The largest 3D texture edge, in texels.</summary>
    public int MaxTextureDimension3D { get; init; }

    /// <summary>The largest array texture layer count.</summary>
    public int MaxTextureArrayLayers { get; init; }

    /// <summary>The largest bound bind group count.</summary>
    public int MaxBindGroups { get; init; }

    /// <summary>The largest uniform buffer binding, in bytes.</summary>
    public long MaxUniformBufferBindingSize { get; init; }

    /// <summary>The alignment a dynamic uniform offset has to satisfy.</summary>
    public int MinUniformBufferOffsetAlignment { get; init; }

    /// <summary>The largest bound vertex buffer count.</summary>
    public int MaxVertexBuffers { get; init; }

    /// <summary>The largest buffer, in bytes.</summary>
    public long MaxBufferSize { get; init; }

    /// <summary>The largest vertex attribute count across all buffers.</summary>
    public int MaxVertexAttributes { get; init; }

    /// <summary>The largest colour attachment count in one pass.</summary>
    public int MaxColorAttachments { get; init; }

    /// <summary>The largest dynamic uniform buffer count in one pipeline layout.</summary>
    public int MaxDynamicUniformBuffersPerPipelineLayout { get; init; }

    /// <summary>The largest compute workgroup, in X.</summary>
    public int MaxComputeWorkgroupSizeX { get; init; }

    /// <summary>The largest compute workgroup, in Y.</summary>
    public int MaxComputeWorkgroupSizeY { get; init; }

    /// <summary>The largest compute workgroup, in Z.</summary>
    public int MaxComputeWorkgroupSizeZ { get; init; }

    /// <summary>
    ///     What the WebGPU specification guarantees every implementation supports.
    /// </summary>
    /// <remarks>
    ///     The floor rather than a guess: an implementation that reports nothing is treated as
    ///     offering exactly this, which is the set the specification says a conforming one has to
    ///     offer, and which a browser on a phone frequently reports verbatim.
    /// </remarks>
    public static WebGpuLimits Guaranteed => new() {
        MaxTextureDimension2D = 8192,
        MaxTextureDimension3D = 2048,
        MaxTextureArrayLayers = 256,
        MaxBindGroups = 4,
        MaxUniformBufferBindingSize = 65536,
        MinUniformBufferOffsetAlignment = 256,
        MaxVertexBuffers = 8,
        MaxBufferSize = 268435456,
        MaxVertexAttributes = 16,
        MaxColorAttachments = 8,
        MaxDynamicUniformBuffersPerPipelineLayout = 8,
        MaxComputeWorkgroupSizeX = 256,
        MaxComputeWorkgroupSizeY = 256,
        MaxComputeWorkgroupSizeZ = 64
    };
}

/// <summary>What a WebGPU implementation says about the adapter it is running on.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What kind of device it is.</param>
/// <param name="DriverDescription">The driver's own description, for logs and bug reports.</param>
public readonly record struct WebGpuAdapterInfo(string Name, WgpuAdapterType Kind, string DriverDescription);
