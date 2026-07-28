// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>A WebGPU texture format, with <c>webgpu.h</c>'s numbering.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists rather than <c>Silk.NET.WebGPU.TextureFormat</c>:</b> the browser
///         surface cannot reference Silk.NET at all — it reaches WebGPU through
///         <c>navigator.gpu</c> — so the vocabulary the two surfaces share has to be the backend's
///         own. Everything above <see cref="IWebGpuBinding" /> is written in these terms.
///     </para>
///     <para>
///         The values are <c>webgpu.h</c>'s, so the native surface casts rather than switches, and
///         the browser surface indexes a string table by the same number.
///         <c>WebGpuEnumAgreementTests</c> asserts every one of them against Silk's, which is what
///         makes the cast safe across a binding upgrade rather than merely convenient.
///     </para>
/// </remarks>
public enum WgpuTextureFormat : uint {
    /// <summary>No format.</summary>
    Undefined = 0,

    /// <summary>One 8-bit channel, <c>[0, 1]</c>.</summary>
    R8Unorm = 1,

    /// <summary>One 8-bit channel, <c>[-1, 1]</c>.</summary>
    R8Snorm = 2,

    /// <summary>One 8-bit unsigned integer channel.</summary>
    R8Uint = 3,

    /// <summary>One 8-bit signed integer channel.</summary>
    R8Sint = 4,

    /// <summary>One 16-bit unsigned integer channel.</summary>
    R16Uint = 5,

    /// <summary>One 16-bit signed integer channel.</summary>
    R16Sint = 6,

    /// <summary>One 16-bit half-float channel.</summary>
    R16Float = 7,

    /// <summary>Two 8-bit channels, <c>[0, 1]</c>.</summary>
    Rg8Unorm = 8,

    /// <summary>Two 8-bit channels, <c>[-1, 1]</c>.</summary>
    Rg8Snorm = 9,

    /// <summary>Two 8-bit unsigned integer channels.</summary>
    Rg8Uint = 10,

    /// <summary>Two 8-bit signed integer channels.</summary>
    Rg8Sint = 11,

    /// <summary>One 32-bit float channel.</summary>
    R32Float = 12,

    /// <summary>One 32-bit unsigned integer channel.</summary>
    R32Uint = 13,

    /// <summary>One 32-bit signed integer channel.</summary>
    R32Sint = 14,

    /// <summary>Two 16-bit unsigned integer channels.</summary>
    Rg16Uint = 15,

    /// <summary>Two 16-bit signed integer channels.</summary>
    Rg16Sint = 16,

    /// <summary>Two 16-bit half-float channels.</summary>
    Rg16Float = 17,

    /// <summary>Four 8-bit channels, <c>[0, 1]</c>.</summary>
    Rgba8Unorm = 18,

    /// <summary>Four 8-bit channels, sRGB-encoded.</summary>
    Rgba8UnormSrgb = 19,

    /// <summary>Four 8-bit channels, <c>[-1, 1]</c>.</summary>
    Rgba8Snorm = 20,

    /// <summary>Four 8-bit unsigned integer channels.</summary>
    Rgba8Uint = 21,

    /// <summary>Four 8-bit signed integer channels.</summary>
    Rgba8Sint = 22,

    /// <summary>Four 8-bit channels in BGRA order, <c>[0, 1]</c>.</summary>
    Bgra8Unorm = 23,

    /// <summary>Four 8-bit channels in BGRA order, sRGB-encoded.</summary>
    Bgra8UnormSrgb = 24,

    /// <summary>Ten bits per colour and two of alpha, as unsigned integers.</summary>
    Rgb10A2Uint = 25,

    /// <summary>Ten bits per colour and two of alpha, <c>[0, 1]</c>.</summary>
    Rgb10A2Unorm = 26,

    /// <summary>Eleven bits each for red and green, ten for blue, as small floats.</summary>
    Rg11B10Ufloat = 27,

    /// <summary>Nine-bit mantissas with a shared five-bit exponent.</summary>
    Rgb9E5Ufloat = 28,

    /// <summary>Two 32-bit float channels.</summary>
    Rg32Float = 29,

    /// <summary>Two 32-bit unsigned integer channels.</summary>
    Rg32Uint = 30,

    /// <summary>Two 32-bit signed integer channels.</summary>
    Rg32Sint = 31,

    /// <summary>Four 16-bit unsigned integer channels.</summary>
    Rgba16Uint = 32,

    /// <summary>Four 16-bit signed integer channels.</summary>
    Rgba16Sint = 33,

    /// <summary>Four 16-bit half-float channels.</summary>
    Rgba16Float = 34,

    /// <summary>Four 32-bit float channels.</summary>
    Rgba32Float = 35,

    /// <summary>Four 32-bit unsigned integer channels.</summary>
    Rgba32Uint = 36,

    /// <summary>Four 32-bit signed integer channels.</summary>
    Rgba32Sint = 37,

    /// <summary>An 8-bit stencil with no depth.</summary>
    Stencil8 = 38,

    /// <summary>16-bit unsigned normalised depth.</summary>
    Depth16Unorm = 39,

    /// <summary>At least 24 bits of depth, of an implementation-chosen layout.</summary>
    Depth24Plus = 40,

    /// <summary>At least 24 bits of depth with an 8-bit stencil.</summary>
    Depth24PlusStencil8 = 41,

    /// <summary>32-bit float depth.</summary>
    Depth32Float = 42,

    /// <summary>32-bit float depth with an 8-bit stencil.</summary>
    Depth32FloatStencil8 = 43,

    /// <summary>BC1 with one bit of alpha.</summary>
    Bc1RgbaUnorm = 44,

    /// <summary>BC1, sRGB-encoded.</summary>
    Bc1RgbaUnormSrgb = 45,

    /// <summary>BC3 with interpolated alpha.</summary>
    Bc3RgbaUnorm = 48,

    /// <summary>BC3, sRGB-encoded.</summary>
    Bc3RgbaUnormSrgb = 49,

    /// <summary>BC4, one channel.</summary>
    Bc4RUnorm = 50,

    /// <summary>BC5, two channels.</summary>
    Bc5RgUnorm = 52,

    /// <summary>BC6H, three HDR channels, unsigned.</summary>
    Bc6HRgbUfloat = 54,

    /// <summary>BC7, colour and alpha.</summary>
    Bc7RgbaUnorm = 56,

    /// <summary>BC7, sRGB-encoded.</summary>
    Bc7RgbaUnormSrgb = 57,

    /// <summary>ETC2 with one bit of alpha.</summary>
    Etc2Rgb8A1Unorm = 60,

    /// <summary>ETC2 with full alpha.</summary>
    Etc2Rgba8Unorm = 62,

    /// <summary>ASTC with a 4×4 block.</summary>
    Astc4X4Unorm = 68,

    /// <summary>ASTC with a 4×4 block, sRGB-encoded.</summary>
    Astc4X4UnormSrgb = 69,

    /// <summary>ASTC with an 8×8 block.</summary>
    Astc8X8Unorm = 82,

    /// <summary>ASTC with an 8×8 block, sRGB-encoded.</summary>
    Astc8X8UnormSrgb = 83
}

/// <summary>What a WebGPU buffer may be used for.</summary>
[Flags]
public enum WgpuBufferUsage : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Mappable for reading.</summary>
    MapRead = 1 << 0,

    /// <summary>Mappable for writing.</summary>
    MapWrite = 1 << 1,

    /// <summary>The source of a copy.</summary>
    CopySrc = 1 << 2,

    /// <summary>The destination of a copy.</summary>
    CopyDst = 1 << 3,

    /// <summary>Indices.</summary>
    Index = 1 << 4,

    /// <summary>Vertex attributes.</summary>
    Vertex = 1 << 5,

    /// <summary>A uniform buffer.</summary>
    Uniform = 1 << 6,

    /// <summary>A storage buffer.</summary>
    Storage = 1 << 7,

    /// <summary>Indirect draw or dispatch arguments.</summary>
    Indirect = 1 << 8,

    /// <summary>A query resolve target.</summary>
    QueryResolve = 1 << 9
}

/// <summary>What a WebGPU texture may be used for.</summary>
[Flags]
public enum WgpuTextureUsage : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The source of a copy.</summary>
    CopySrc = 1 << 0,

    /// <summary>The destination of a copy.</summary>
    CopyDst = 1 << 1,

    /// <summary>Sampled by a shader.</summary>
    TextureBinding = 1 << 2,

    /// <summary>Read or written as a storage texture.</summary>
    StorageBinding = 1 << 3,

    /// <summary>A colour or depth-stencil attachment.</summary>
    RenderAttachment = 1 << 4
}

/// <summary>The shape of a WebGPU texture.</summary>
public enum WgpuTextureDimension : uint {
    /// <summary>A line of texels.</summary>
    Dimension1D = 0,

    /// <summary>The ordinary case.</summary>
    Dimension2D = 1,

    /// <summary>A volume.</summary>
    Dimension3D = 2
}

/// <summary>How a WebGPU texture view is read.</summary>
public enum WgpuTextureViewDimension : uint {
    /// <summary>Whatever the texture is.</summary>
    Undefined = 0,

    /// <summary>A line of texels.</summary>
    Dimension1D = 1,

    /// <summary>One 2D image.</summary>
    Dimension2D = 2,

    /// <summary>An array of 2D images.</summary>
    Dimension2DArray = 3,

    /// <summary>Six faces.</summary>
    Cube = 4,

    /// <summary>An array of cubes.</summary>
    CubeArray = 5,

    /// <summary>A volume.</summary>
    Dimension3D = 6
}

/// <summary>Which planes of a texture a view or copy touches.</summary>
public enum WgpuTextureAspect : uint {
    /// <summary>All of them.</summary>
    All = 0,

    /// <summary>Stencil only.</summary>
    StencilOnly = 1,

    /// <summary>Depth only.</summary>
    DepthOnly = 2
}

/// <summary>Which shader stages see a binding.</summary>
[Flags]
public enum WgpuShaderStage : uint {
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Vertex.</summary>
    Vertex = 1 << 0,

    /// <summary>Fragment.</summary>
    Fragment = 1 << 1,

    /// <summary>Compute.</summary>
    Compute = 1 << 2
}

/// <summary>What a buffer binding is.</summary>
public enum WgpuBufferBindingType : uint {
    /// <summary>Not a buffer binding.</summary>
    Undefined = 0,

    /// <summary>A uniform buffer.</summary>
    Uniform = 1,

    /// <summary>A read-write storage buffer.</summary>
    Storage = 2,

    /// <summary>A read-only storage buffer.</summary>
    ReadOnlyStorage = 3
}

/// <summary>What a sampler binding is.</summary>
public enum WgpuSamplerBindingType : uint {
    /// <summary>Not a sampler binding.</summary>
    Undefined = 0,

    /// <summary>An ordinary filtering sampler.</summary>
    Filtering = 1,

    /// <summary>A sampler that does not filter.</summary>
    NonFiltering = 2,

    /// <summary>A shadow-comparison sampler.</summary>
    Comparison = 3
}

/// <summary>How a sampled texture's texels are interpreted.</summary>
public enum WgpuTextureSampleType : uint {
    /// <summary>Not a texture binding.</summary>
    Undefined = 0,

    /// <summary>Filterable floats.</summary>
    Float = 1,

    /// <summary>Floats a sampler may not filter.</summary>
    UnfilterableFloat = 2,

    /// <summary>Depth, for a comparison sampler.</summary>
    Depth = 3,

    /// <summary>Signed integers.</summary>
    Sint = 4,

    /// <summary>Unsigned integers.</summary>
    Uint = 5
}

/// <summary>How a shader reaches a storage texture.</summary>
public enum WgpuStorageTextureAccess : uint {
    /// <summary>Not a storage-texture binding.</summary>
    Undefined = 0,

    /// <summary>Written only.</summary>
    WriteOnly = 1,

    /// <summary>Read only.</summary>
    ReadOnly = 2,

    /// <summary>Read and written.</summary>
    ReadWrite = 3
}

/// <summary>What the vertices mean.</summary>
public enum WgpuPrimitiveTopology : uint {
    /// <summary>Each vertex is a point.</summary>
    PointList = 0,

    /// <summary>Each pair is a line.</summary>
    LineList = 1,

    /// <summary>Each vertex continues a line.</summary>
    LineStrip = 2,

    /// <summary>Each triple is a triangle.</summary>
    TriangleList = 3,

    /// <summary>Each vertex continues a strip.</summary>
    TriangleStrip = 4
}

/// <summary>How wide an index is.</summary>
public enum WgpuIndexFormat : uint {
    /// <summary>Unset — legal only for a non-strip topology.</summary>
    Undefined = 0,

    /// <summary>16-bit.</summary>
    Uint16 = 1,

    /// <summary>32-bit.</summary>
    Uint32 = 2
}

/// <summary>Which winding is a front face.</summary>
public enum WgpuFrontFace : uint {
    /// <summary>Counter-clockwise. The engine's convention.</summary>
    Ccw = 0,

    /// <summary>Clockwise.</summary>
    Cw = 1
}

/// <summary>Which faces to discard.</summary>
public enum WgpuCullMode : uint {
    /// <summary>Draw both sides.</summary>
    None = 0,

    /// <summary>Discard front faces.</summary>
    Front = 1,

    /// <summary>Discard back faces.</summary>
    Back = 2
}

/// <summary>The type of a vertex attribute.</summary>
public enum WgpuVertexFormat : uint {
    /// <summary>No format.</summary>
    Undefined = 0,

    /// <summary>Four bytes as unsigned integers.</summary>
    Uint8X4 = 2,

    /// <summary>Four bytes, <c>[0, 1]</c>.</summary>
    Unorm8X4 = 6,

    /// <summary>Four bytes, <c>[-1, 1]</c>.</summary>
    Snorm8X4 = 8,

    /// <summary>Two shorts, <c>[0, 1]</c>.</summary>
    Unorm16X2 = 13,

    /// <summary>Four shorts, <c>[-1, 1]</c>.</summary>
    Snorm16X4 = 16,

    /// <summary>Two half floats.</summary>
    Float16X2 = 17,

    /// <summary>Four half floats.</summary>
    Float16X4 = 18,

    /// <summary>One float.</summary>
    Float32 = 19,

    /// <summary>Two floats.</summary>
    Float32X2 = 20,

    /// <summary>Three floats.</summary>
    Float32X3 = 21,

    /// <summary>Four floats.</summary>
    Float32X4 = 22,

    /// <summary>One unsigned 32-bit integer.</summary>
    Uint32 = 23
}

/// <summary>How a vertex attribute advances.</summary>
public enum WgpuVertexStepMode : uint {
    /// <summary>Once per vertex.</summary>
    Vertex = 0,

    /// <summary>Once per instance.</summary>
    Instance = 1,

    /// <summary>The buffer is declared but not read.</summary>
    VertexBufferNotUsed = 2
}

/// <summary>One side of a blend equation.</summary>
public enum WgpuBlendFactor : uint {
    /// <summary>Zero.</summary>
    Zero = 0,

    /// <summary>One.</summary>
    One = 1,

    /// <summary>The source colour.</summary>
    Src = 2,

    /// <summary>One minus the source colour.</summary>
    OneMinusSrc = 3,

    /// <summary>The source alpha.</summary>
    SrcAlpha = 4,

    /// <summary>One minus the source alpha.</summary>
    OneMinusSrcAlpha = 5,

    /// <summary>The destination colour.</summary>
    Dst = 6,

    /// <summary>One minus the destination colour.</summary>
    OneMinusDst = 7,

    /// <summary>The destination alpha.</summary>
    DstAlpha = 8,

    /// <summary>One minus the destination alpha.</summary>
    OneMinusDstAlpha = 9,

    /// <summary>The source alpha, clamped to one minus the destination alpha.</summary>
    SrcAlphaSaturated = 10,

    /// <summary>The blend constant.</summary>
    Constant = 11,

    /// <summary>One minus the blend constant.</summary>
    OneMinusConstant = 12
}

/// <summary>How the two sides of a blend combine.</summary>
public enum WgpuBlendOperation : uint {
    /// <summary>Source plus destination.</summary>
    Add = 0,

    /// <summary>Source minus destination.</summary>
    Subtract = 1,

    /// <summary>Destination minus source.</summary>
    ReverseSubtract = 2,

    /// <summary>The smaller of the two.</summary>
    Min = 3,

    /// <summary>The larger of the two.</summary>
    Max = 4
}

/// <summary>Which channels a draw may write.</summary>
[Flags]
public enum WgpuColorWriteMask : uint {
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Red.</summary>
    Red = 1 << 0,

    /// <summary>Green.</summary>
    Green = 1 << 1,

    /// <summary>Blue.</summary>
    Blue = 1 << 2,

    /// <summary>Alpha.</summary>
    Alpha = 1 << 3,

    /// <summary>All four.</summary>
    All = Red | Green | Blue | Alpha
}

/// <summary>A comparison, for depth, stencil and shadow samplers.</summary>
public enum WgpuCompareFunction : uint {
    /// <summary>Unset.</summary>
    Undefined = 0,

    /// <summary>Never passes.</summary>
    Never = 1,

    /// <summary>Passes when less.</summary>
    Less = 2,

    /// <summary>Passes when less or equal.</summary>
    LessEqual = 3,

    /// <summary>Passes when greater. The engine's depth test, under reversed Z.</summary>
    Greater = 4,

    /// <summary>Passes when greater or equal.</summary>
    GreaterEqual = 5,

    /// <summary>Passes when equal.</summary>
    Equal = 6,

    /// <summary>Passes when not equal.</summary>
    NotEqual = 7,

    /// <summary>Always passes.</summary>
    Always = 8
}

/// <summary>What to do with a stencil value.</summary>
public enum WgpuStencilOperation : uint {
    /// <summary>Leave it.</summary>
    Keep = 0,

    /// <summary>Set it to zero.</summary>
    Zero = 1,

    /// <summary>Set it to the reference value.</summary>
    Replace = 2,

    /// <summary>Flip every bit.</summary>
    Invert = 3,

    /// <summary>Add one, clamping.</summary>
    IncrementClamp = 4,

    /// <summary>Subtract one, clamping.</summary>
    DecrementClamp = 5,

    /// <summary>Add one, wrapping.</summary>
    IncrementWrap = 6,

    /// <summary>Subtract one, wrapping.</summary>
    DecrementWrap = 7
}

/// <summary>What a sampler does outside <c>[0, 1]</c>.</summary>
/// <remarks>
///     There is no <c>ClampToBorder</c>: WebGPU has no border colours at all. See
///     <see cref="WebGpuConversions.ToWebGpu(AddressMode)" /> for what the RHI's does instead, and
///     why a shadow sampler is the case that notices.
/// </remarks>
public enum WgpuAddressMode : uint {
    /// <summary>Tile.</summary>
    Repeat = 0,

    /// <summary>Tile, flipping every other tile.</summary>
    MirrorRepeat = 1,

    /// <summary>Extend the edge texel.</summary>
    ClampToEdge = 2
}

/// <summary>How a sampler filters within a mip level.</summary>
public enum WgpuFilterMode : uint {
    /// <summary>Take the nearest texel.</summary>
    Nearest = 0,

    /// <summary>Blend between texels.</summary>
    Linear = 1
}

/// <summary>How a sampler filters between mip levels.</summary>
public enum WgpuMipmapFilterMode : uint {
    /// <summary>Take the nearest level.</summary>
    Nearest = 0,

    /// <summary>Blend between levels.</summary>
    Linear = 1
}

/// <summary>What happens to an attachment when a pass begins.</summary>
public enum WgpuLoadOp : uint {
    /// <summary>Unset.</summary>
    Undefined = 0,

    /// <summary>Fill it with the clear value.</summary>
    Clear = 1,

    /// <summary>Keep what is there.</summary>
    Load = 2
}

/// <summary>What happens to an attachment when a pass ends.</summary>
public enum WgpuStoreOp : uint {
    /// <summary>Unset.</summary>
    Undefined = 0,

    /// <summary>Write it back.</summary>
    Store = 1,

    /// <summary>Throw it away.</summary>
    Discard = 2
}

/// <summary>How a surface waits for the display.</summary>
public enum WgpuPresentMode : uint {
    /// <summary>Wait for the vertical blank. The only mode every implementation supports.</summary>
    Fifo = 0,

    /// <summary>Like <see cref="Fifo" />, but a late frame is shown immediately.</summary>
    FifoRelaxed = 1,

    /// <summary>Present as soon as the frame is ready.</summary>
    Immediate = 2,

    /// <summary>Replace the queued frame with the newest one.</summary>
    Mailbox = 3
}

/// <summary>How a surface's alpha is composited with the page or desktop behind it.</summary>
public enum WgpuCompositeAlphaMode : uint {
    /// <summary>Whatever the implementation prefers.</summary>
    Auto = 0,

    /// <summary>Ignore alpha.</summary>
    Opaque = 1,

    /// <summary>Premultiplied.</summary>
    Premultiplied = 2,

    /// <summary>Straight.</summary>
    Unpremultiplied = 3,

    /// <summary>Inherited from the window system.</summary>
    Inherit = 4
}

/// <summary>What a surface did when asked for its next texture.</summary>
public enum WgpuSurfaceStatus : uint {
    /// <summary>A texture was acquired.</summary>
    Success = 0,

    /// <summary>Nothing was ready in time.</summary>
    Timeout = 1,

    /// <summary>The surface no longer matches its configuration.</summary>
    Outdated = 2,

    /// <summary>The surface is gone.</summary>
    Lost = 3,

    /// <summary>The implementation ran out of memory.</summary>
    OutOfMemory = 4,

    /// <summary>The device was lost.</summary>
    DeviceLost = 5
}

/// <summary>An optional WebGPU feature.</summary>
/// <remarks>
///     Only the ones the backend asks for. WebGPU's feature set is small and every member here maps
///     onto something <see cref="GraphicsDeviceFeatures" /> reports or a
///     <see cref="PixelFormat" /> family the engine ships content in.
/// </remarks>
public enum WgpuFeatureName : uint {
    /// <summary>No feature.</summary>
    Undefined = 0,

    /// <summary>Depth may be clamped rather than clipped.</summary>
    DepthClipControl = 1,

    /// <summary>The <see cref="WgpuTextureFormat.Depth32FloatStencil8" /> format.</summary>
    Depth32FloatStencil8 = 2,

    /// <summary>Timestamp queries.</summary>
    TimestampQuery = 3,

    /// <summary>The BC (DXT) compressed formats — desktop content.</summary>
    TextureCompressionBc = 4,

    /// <summary>The ETC2 compressed formats — mobile content.</summary>
    TextureCompressionEtc2 = 5,

    /// <summary>The ASTC compressed formats — mobile content.</summary>
    TextureCompressionAstc = 6,

    /// <summary>An indirect draw may set <c>firstInstance</c>.</summary>
    IndirectFirstInstance = 7,

    /// <summary>Half floats in shaders.</summary>
    ShaderF16 = 8,

    /// <summary><see cref="WgpuTextureFormat.Rg11B10Ufloat" /> may be a colour attachment.</summary>
    Rg11B10UfloatRenderable = 9,

    /// <summary><see cref="WgpuTextureFormat.Bgra8Unorm" /> may be a storage texture.</summary>
    Bgra8UnormStorage = 10,

    /// <summary>32-bit float textures may be filtered.</summary>
    Float32Filterable = 11
}

/// <summary>Which adapter to prefer.</summary>
public enum WgpuPowerPreference : uint {
    /// <summary>No preference.</summary>
    Undefined = 0,

    /// <summary>The one that uses least power.</summary>
    LowPower = 1,

    /// <summary>The fastest one.</summary>
    HighPerformance = 2
}

/// <summary>What kind of device an adapter is, in WebGPU's terms.</summary>
public enum WgpuAdapterType : uint {
    /// <summary>A separate card.</summary>
    DiscreteGpu = 0,

    /// <summary>Part of the CPU package.</summary>
    IntegratedGpu = 1,

    /// <summary>A software implementation.</summary>
    Cpu = 2,

    /// <summary>The implementation did not say. What a browser always reports.</summary>
    Unknown = 3
}
