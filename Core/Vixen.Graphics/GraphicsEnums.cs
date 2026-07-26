// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>Which queue work is submitted to.</summary>
/// <remarks>
///     Three, because that is what the hardware has: a queue that can do everything, one that can do
///     compute and copies, and one that can only copy but can do it while the others are busy. A
///     backend with fewer maps several of these onto one, which is legal and is what
///     <see cref="GraphicsDeviceFeatures.HasAsyncCompute" /> is for.
/// </remarks>
public enum QueueKind : byte {
    /// <summary>Draws, dispatches and copies.</summary>
    Graphics = 0,

    /// <summary>Dispatches and copies, in parallel with graphics where the hardware allows.</summary>
    Compute = 1,

    /// <summary>Copies only, usually over a dedicated DMA engine.</summary>
    Transfer = 2
}

/// <summary>What a buffer is for.</summary>
/// <remarks>
///     Flags, because a buffer is often several things — a vertex buffer that compute also writes to
///     is <c>Vertex | Storage</c> — and because every backend wants the full set at creation. A
///     usage added later means recreating the buffer.
/// </remarks>
[Flags]
public enum BufferUsage {
    /// <summary>Nothing. Not a valid buffer.</summary>
    None = 0,

    /// <summary>Vertex attributes.</summary>
    Vertex = 1 << 0,

    /// <summary>Indices.</summary>
    Index = 1 << 1,

    /// <summary>A uniform (constant) buffer.</summary>
    Uniform = 1 << 2,

    /// <summary>A read-write structured buffer.</summary>
    Storage = 1 << 3,

    /// <summary>Arguments for an indirect draw or dispatch.</summary>
    Indirect = 1 << 4,

    /// <summary>The source of a copy.</summary>
    CopySource = 1 << 5,

    /// <summary>The destination of a copy.</summary>
    CopyDestination = 1 << 6
}

/// <summary>Where a resource's memory lives and who can reach it.</summary>
public enum MemoryAccess : byte {
    /// <summary>GPU-local, not visible to the CPU. What everything the GPU reads often should be.</summary>
    DeviceLocal = 0,

    /// <summary>CPU-writable and GPU-readable — a staging buffer, or per-frame constants.</summary>
    /// <remarks>
    ///     On a discrete GPU this is host memory the GPU reads over the bus, so it is the right home
    ///     for data written once and read once and the wrong home for a mesh. On an integrated or
    ///     mobile GPU there is only one pool and the distinction costs nothing, which is what
    ///     <see cref="GraphicsDeviceFeatures.HasUnifiedMemory" /> reports.
    /// </remarks>
    HostUpload = 1,

    /// <summary>GPU-writable and CPU-readable — a readback buffer.</summary>
    HostReadback = 2
}

/// <summary>What a texture is for.</summary>
[Flags]
public enum TextureUsage {
    /// <summary>Nothing. Not a valid texture.</summary>
    None = 0,

    /// <summary>Sampled by a shader.</summary>
    Sampled = 1 << 0,

    /// <summary>Read and written by a shader without a sampler.</summary>
    Storage = 1 << 1,

    /// <summary>A colour attachment.</summary>
    ColourTarget = 1 << 2,

    /// <summary>A depth or stencil attachment.</summary>
    DepthStencilTarget = 1 << 3,

    /// <summary>The source of a copy.</summary>
    CopySource = 1 << 4,

    /// <summary>The destination of a copy.</summary>
    CopyDestination = 1 << 5
}

/// <summary>The shape of a texture.</summary>
public enum TextureDimension : byte {
    /// <summary>A line of texels.</summary>
    Texture1D = 0,

    /// <summary>The ordinary case.</summary>
    Texture2D = 1,

    /// <summary>A volume.</summary>
    Texture3D = 2,

    /// <summary>Six square faces. An array of these is a cube array.</summary>
    TextureCube = 3
}

/// <summary>A shader stage.</summary>
[Flags]
public enum ShaderStage {
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>Vertex.</summary>
    Vertex = 1 << 0,

    /// <summary>Fragment, or pixel.</summary>
    Fragment = 1 << 1,

    /// <summary>Compute.</summary>
    Compute = 1 << 2,

    /// <summary>Geometry. Absent on mobile and slow almost everywhere.</summary>
    Geometry = 1 << 3,

    /// <summary>Tessellation control, or hull.</summary>
    TessellationControl = 1 << 4,

    /// <summary>Tessellation evaluation, or domain.</summary>
    TessellationEvaluation = 1 << 5,

    /// <summary>Task, or amplification.</summary>
    Task = 1 << 6,

    /// <summary>Mesh.</summary>
    Mesh = 1 << 7,

    /// <summary>Every stage a graphics pipeline can have.</summary>
    AllGraphics = Vertex | Fragment | Geometry | TessellationControl | TessellationEvaluation,

    /// <summary>Every stage.</summary>
    All = AllGraphics | Compute | Task | Mesh
}

/// <summary>What the vertices mean.</summary>
public enum PrimitiveTopology : byte {
    /// <summary>Each vertex is a point.</summary>
    PointList = 0,

    /// <summary>Each pair is a line.</summary>
    LineList = 1,

    /// <summary>Each vertex continues a line.</summary>
    LineStrip = 2,

    /// <summary>Each triple is a triangle.</summary>
    TriangleList = 3,

    /// <summary>Each vertex continues a triangle strip.</summary>
    TriangleStrip = 4
}

/// <summary>How wide an index is.</summary>
public enum IndexFormat : byte {
    /// <summary>16-bit. Half the bandwidth, and enough for almost every mesh.</summary>
    UInt16 = 0,

    /// <summary>32-bit.</summary>
    UInt32 = 1
}

/// <summary>Which faces to discard.</summary>
public enum CullMode : byte {
    /// <summary>Draw both sides.</summary>
    None = 0,

    /// <summary>Discard front faces.</summary>
    Front = 1,

    /// <summary>Discard back faces. The ordinary case.</summary>
    Back = 2
}

/// <summary>Which winding is a front face.</summary>
/// <remarks>
///     Counter-clockwise, per <c>Vixen.Core.Mathematics/Conventions.md</c>. Stated as an enum anyway
///     because a mirrored transform flips winding, and a shadow pass that renders back faces needs
///     to say so.
/// </remarks>
public enum FrontFace : byte {
    /// <summary>Counter-clockwise. The engine's convention.</summary>
    CounterClockwise = 0,

    /// <summary>Clockwise.</summary>
    Clockwise = 1
}

/// <summary>How to fill a triangle.</summary>
public enum FillMode : byte {
    /// <summary>Filled.</summary>
    Solid = 0,

    /// <summary>Edges only. Debug visualisation; unavailable on some mobile hardware.</summary>
    Wireframe = 1
}

/// <summary>A comparison, for depth, stencil and shadow samplers.</summary>
public enum CompareFunction : byte {
    /// <summary>Never passes.</summary>
    Never = 0,

    /// <summary>Passes when less than the stored value.</summary>
    Less = 1,

    /// <summary>Passes when equal.</summary>
    Equal = 2,

    /// <summary>Passes when less than or equal.</summary>
    LessEqual = 3,

    /// <summary>Passes when greater.</summary>
    /// <remarks>
    ///     The engine's depth test, because depth is reversed: near maps to 1 and far to 0, which is
    ///     what makes a float32 depth buffer precise where precision is wanted. See
    ///     <c>Conventions.md</c>.
    /// </remarks>
    Greater = 4,

    /// <summary>Passes when not equal.</summary>
    NotEqual = 5,

    /// <summary>Passes when greater than or equal.</summary>
    GreaterEqual = 6,

    /// <summary>Always passes.</summary>
    Always = 7
}

/// <summary>What to do with a stencil value.</summary>
public enum StencilOperation : byte {
    /// <summary>Leave it.</summary>
    Keep = 0,

    /// <summary>Set it to zero.</summary>
    Zero = 1,

    /// <summary>Set it to the reference value.</summary>
    Replace = 2,

    /// <summary>Add one, clamping.</summary>
    IncrementClamp = 3,

    /// <summary>Subtract one, clamping.</summary>
    DecrementClamp = 4,

    /// <summary>Flip every bit.</summary>
    Invert = 5,

    /// <summary>Add one, wrapping.</summary>
    IncrementWrap = 6,

    /// <summary>Subtract one, wrapping.</summary>
    DecrementWrap = 7
}

/// <summary>One side of a blend equation.</summary>
public enum BlendFactor : byte {
    /// <summary>Zero.</summary>
    Zero = 0,

    /// <summary>One.</summary>
    One = 1,

    /// <summary>The source colour.</summary>
    SourceColour = 2,

    /// <summary>One minus the source colour.</summary>
    OneMinusSourceColour = 3,

    /// <summary>The source alpha.</summary>
    SourceAlpha = 4,

    /// <summary>One minus the source alpha.</summary>
    OneMinusSourceAlpha = 5,

    /// <summary>The destination colour.</summary>
    DestinationColour = 6,

    /// <summary>One minus the destination colour.</summary>
    OneMinusDestinationColour = 7,

    /// <summary>The destination alpha.</summary>
    DestinationAlpha = 8,

    /// <summary>One minus the destination alpha.</summary>
    OneMinusDestinationAlpha = 9,

    /// <summary>The blend constant.</summary>
    Constant = 10,

    /// <summary>One minus the blend constant.</summary>
    OneMinusConstant = 11,

    /// <summary>The source alpha, clamped to one minus the destination alpha.</summary>
    SourceAlphaSaturated = 12
}

/// <summary>How the two sides of a blend combine.</summary>
public enum BlendOperation : byte {
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

/// <summary>Which channels a draw is allowed to write.</summary>
[Flags]
public enum ColourWriteMask : byte {
    /// <summary>None. A depth-only pass.</summary>
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

/// <summary>How a sampler filters.</summary>
public enum FilterMode : byte {
    /// <summary>Take the nearest texel.</summary>
    Nearest = 0,

    /// <summary>Blend between texels.</summary>
    Linear = 1
}

/// <summary>What a sampler does outside <c>[0, 1]</c>.</summary>
public enum AddressMode : byte {
    /// <summary>Tile.</summary>
    Repeat = 0,

    /// <summary>Tile, flipping every other tile.</summary>
    MirrorRepeat = 1,

    /// <summary>Extend the edge texel.</summary>
    ClampToEdge = 2,

    /// <summary>Use the sampler's border colour.</summary>
    ClampToBorder = 3
}

/// <summary>What a sampler returns outside the texture when clamping to a border.</summary>
public enum BorderColour : byte {
    /// <summary>Transparent black.</summary>
    TransparentBlack = 0,

    /// <summary>Opaque black.</summary>
    OpaqueBlack = 1,

    /// <summary>Opaque white. What a shadow map wants, so unshadowed is the default.</summary>
    OpaqueWhite = 2
}

/// <summary>How a vertex attribute advances.</summary>
public enum VertexStepMode : byte {
    /// <summary>Once per vertex.</summary>
    Vertex = 0,

    /// <summary>Once per instance.</summary>
    Instance = 1
}

/// <summary>The type of a vertex attribute.</summary>
public enum VertexFormat : byte {
    /// <summary>One float.</summary>
    Float32 = 0,

    /// <summary>Two floats.</summary>
    Float32X2 = 1,

    /// <summary>Three floats.</summary>
    Float32X3 = 2,

    /// <summary>Four floats.</summary>
    Float32X4 = 3,

    /// <summary>Two half floats.</summary>
    Float16X2 = 4,

    /// <summary>Four half floats.</summary>
    Float16X4 = 5,

    /// <summary>Four bytes, <c>[0, 1]</c>. Vertex colours and bone weights.</summary>
    UNorm8X4 = 6,

    /// <summary>Four bytes, <c>[-1, 1]</c>. Packed normals and tangents.</summary>
    SNorm8X4 = 7,

    /// <summary>Four bytes as unsigned integers. Bone indices.</summary>
    UInt8X4 = 8,

    /// <summary>One unsigned 32-bit integer.</summary>
    UInt32 = 9,

    /// <summary>Two shorts, <c>[0, 1]</c>. Packed texture coordinates.</summary>
    UNorm16X2 = 10,

    /// <summary>Four shorts, <c>[-1, 1]</c>.</summary>
    SNorm16X4 = 11
}

/// <summary>What happens to an attachment when a render pass begins.</summary>
public enum LoadAction : byte {
    /// <summary>Keep what is there.</summary>
    Load = 0,

    /// <summary>Fill it with the clear value.</summary>
    Clear = 1,

    /// <summary>
    ///     Do not care — the pass overwrites every pixel.
    /// </summary>
    /// <remarks>
    ///     Not a micro-optimisation on a tiled mobile GPU: <see cref="Load" /> makes the driver read
    ///     the whole attachment from main memory into tile memory before the pass, which on a phone
    ///     is measurable in milliseconds and in battery. Saying so when it is true is one of the
    ///     highest-value things this enum does.
    /// </remarks>
    DontCare = 2
}

/// <summary>What happens to an attachment when a render pass ends.</summary>
public enum StoreAction : byte {
    /// <summary>Write it back.</summary>
    Store = 0,

    /// <summary>
    ///     Throw it away — a depth buffer nothing reads after the pass, a multisampled target that
    ///     has already been resolved.
    /// </summary>
    DontCare = 1,

    /// <summary>Resolve a multisampled attachment into its resolve target and discard the samples.</summary>
    Resolve = 2
}

/// <summary>How a swapchain waits for the display.</summary>
public enum PresentMode : byte {
    /// <summary>
    ///     Wait for the vertical blank, queueing at most one frame ahead. Never tears.
    /// </summary>
    /// <remarks>The only mode every backend and every driver is required to support, which is why
    /// it is the default and the fallback.</remarks>
    Fifo = 0,

    /// <summary>Like <see cref="Fifo" />, but a frame that misses the blank is shown immediately —
    /// so it tears rather than stuttering.</summary>
    FifoRelaxed = 1,

    /// <summary>Replace the queued frame with the newest one. No tearing, no waiting, dropped
    /// frames.</summary>
    Mailbox = 2,

    /// <summary>Present as soon as the frame is ready. Tears; the lowest latency there is.</summary>
    Immediate = 3
}

/// <summary>What a swapchain did when asked for its next image.</summary>
public enum SwapChainStatus : byte {
    /// <summary>An image was acquired.</summary>
    Ready = 0,

    /// <summary>
    ///     The swapchain no longer matches its surface and has to be rebuilt — the window resized,
    ///     the display changed, or Android took the surface away.
    /// </summary>
    /// <remarks>
    ///     Reported rather than thrown, because it is not an error: it happens every time a user
    ///     drags a window edge, and treating a routine event as exceptional is how a resize costs an
    ///     exception per frame.
    /// </remarks>
    OutOfDate = 1,

    /// <summary>
    ///     An image was acquired, but the swapchain no longer matches its surface exactly. The frame
    ///     can be presented; the swapchain should be rebuilt afterwards.
    /// </summary>
    Suboptimal = 2,

    /// <summary>The device was lost. Everything has to be recreated.</summary>
    DeviceLost = 3
}

/// <summary>What a resource is being used for, for the purpose of a barrier.</summary>
/// <remarks>
///     <para>
///         Specified against Vulkan's <c>synchronization2</c> rather than the older
///         source-and-destination-stage model, and deliberately: D3D12's Enhanced Barriers map onto
///         it directly, so a D3D12 backend added later is additive rather than a re-specification
///         (ADR-001). The GL backend elides barriers entirely, which is legal because GL's model is
///         implicit.
///     </para>
///     <para>
///         Flags rather than a single state, because a resource can legitimately be several things
///         at once — sampled by the fragment stage while also being a copy source — and a barrier
///         has to name all of them or the driver serialises more than it needs to.
///     </para>
/// </remarks>
[Flags]
public enum ResourceState {
    /// <summary>Nothing is using it. The state a resource is created in.</summary>
    Undefined = 0,

    /// <summary>Read as a vertex or index buffer.</summary>
    VertexInput = 1 << 0,

    /// <summary>Read as indirect draw arguments.</summary>
    IndirectArgument = 1 << 1,

    /// <summary>Read as a uniform buffer.</summary>
    UniformRead = 1 << 2,

    /// <summary>Sampled or read as a storage resource by a shader.</summary>
    ShaderRead = 1 << 3,

    /// <summary>Written by a shader as a storage resource.</summary>
    ShaderWrite = 1 << 4,

    /// <summary>A colour attachment being written.</summary>
    ColourTarget = 1 << 5,

    /// <summary>A depth or stencil attachment being written.</summary>
    DepthStencilWrite = 1 << 6,

    /// <summary>A depth attachment being tested but not written.</summary>
    DepthStencilRead = 1 << 7,

    /// <summary>The source of a copy.</summary>
    CopySource = 1 << 8,

    /// <summary>The destination of a copy.</summary>
    CopyDestination = 1 << 9,

    /// <summary>Being presented.</summary>
    Present = 1 << 10,

    /// <summary>Mapped by the host.</summary>
    HostAccess = 1 << 11
}
