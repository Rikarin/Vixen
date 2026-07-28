// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>The GL enumerant values this backend passes to <see cref="IGlApi" />.</summary>
/// <remarks>
///     <para>
///         Spelled out rather than taken from <c>Silk.NET.OpenGL</c>'s enums, and that is the point:
///         <see cref="IGlApi" /> is the seam that makes the translation layer testable without a
///         driver, and an interface whose parameters were Silk enums would drag the binding — and
///         therefore a context — into every test. The values are the ones in the registry and do not
///         change; that is what a stable enumerant means.
///     </para>
///     <para>
///         Only what the backend emits is here. A constant nobody passes is a constant nobody has
///         checked against the registry.
///     </para>
/// </remarks>
public static class GlConstants {
    // ── Buffer targets and usage ────────────────────────────────────────────────────────────

    /// <summary><c>GL_ARRAY_BUFFER</c>.</summary>
    public const uint ArrayBuffer = 0x8892;

    /// <summary><c>GL_ELEMENT_ARRAY_BUFFER</c>.</summary>
    public const uint ElementArrayBuffer = 0x8893;

    /// <summary><c>GL_UNIFORM_BUFFER</c>.</summary>
    public const uint UniformBuffer = 0x8A11;

    /// <summary><c>GL_SHADER_STORAGE_BUFFER</c>.</summary>
    public const uint ShaderStorageBuffer = 0x90D2;

    /// <summary><c>GL_COPY_READ_BUFFER</c>.</summary>
    public const uint CopyReadBuffer = 0x8F36;

    /// <summary><c>GL_COPY_WRITE_BUFFER</c>.</summary>
    public const uint CopyWriteBuffer = 0x8F37;

    /// <summary><c>GL_PIXEL_PACK_BUFFER</c>.</summary>
    public const uint PixelPackBuffer = 0x88EB;

    /// <summary><c>GL_PIXEL_UNPACK_BUFFER</c>.</summary>
    public const uint PixelUnpackBuffer = 0x88EC;

    /// <summary><c>GL_DRAW_INDIRECT_BUFFER</c>.</summary>
    public const uint DrawIndirectBuffer = 0x8F3F;

    /// <summary><c>GL_DISPATCH_INDIRECT_BUFFER</c>.</summary>
    public const uint DispatchIndirectBuffer = 0x90EE;

    /// <summary><c>GL_STATIC_DRAW</c>.</summary>
    public const uint StaticDraw = 0x88E4;

    /// <summary><c>GL_STREAM_DRAW</c>.</summary>
    public const uint StreamDraw = 0x88E0;

    /// <summary><c>GL_STREAM_READ</c>.</summary>
    public const uint StreamRead = 0x88E1;

    /// <summary><c>GL_MAP_READ_BIT</c>.</summary>
    public const uint MapReadBit = 0x0001;

    /// <summary><c>GL_MAP_WRITE_BIT</c>.</summary>
    public const uint MapWriteBit = 0x0002;

    // ── Texture targets ─────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_TEXTURE_2D</c>.</summary>
    public const uint Texture2D = 0x0DE1;

    /// <summary><c>GL_TEXTURE_3D</c>.</summary>
    public const uint Texture3D = 0x806F;

    /// <summary><c>GL_TEXTURE_2D_ARRAY</c>.</summary>
    public const uint Texture2DArray = 0x8C1A;

    /// <summary><c>GL_TEXTURE_CUBE_MAP</c>.</summary>
    public const uint TextureCubeMap = 0x8513;

    /// <summary><c>GL_TEXTURE_CUBE_MAP_POSITIVE_X</c> — the other five faces follow it.</summary>
    public const uint TextureCubeMapPositiveX = 0x8515;

    /// <summary><c>GL_TEXTURE_2D_MULTISAMPLE</c>.</summary>
    public const uint Texture2DMultisample = 0x9100;

    // ── Sampler and texture parameters ──────────────────────────────────────────────────────

    /// <summary><c>GL_TEXTURE_MIN_FILTER</c>.</summary>
    public const uint TextureMinFilter = 0x2801;

    /// <summary><c>GL_TEXTURE_MAG_FILTER</c>.</summary>
    public const uint TextureMagFilter = 0x2800;

    /// <summary><c>GL_TEXTURE_WRAP_S</c>.</summary>
    public const uint TextureWrapS = 0x2802;

    /// <summary><c>GL_TEXTURE_WRAP_T</c>.</summary>
    public const uint TextureWrapT = 0x2803;

    /// <summary><c>GL_TEXTURE_WRAP_R</c>.</summary>
    public const uint TextureWrapR = 0x8072;

    /// <summary><c>GL_TEXTURE_MIN_LOD</c>.</summary>
    public const uint TextureMinLod = 0x813A;

    /// <summary><c>GL_TEXTURE_MAX_LOD</c>.</summary>
    public const uint TextureMaxLod = 0x813B;

    /// <summary><c>GL_TEXTURE_LOD_BIAS</c>.</summary>
    public const uint TextureLodBias = 0x8501;

    /// <summary><c>GL_TEXTURE_BASE_LEVEL</c>.</summary>
    public const uint TextureBaseLevel = 0x813C;

    /// <summary><c>GL_TEXTURE_MAX_LEVEL</c>.</summary>
    public const uint TextureMaxLevel = 0x813D;

    /// <summary><c>GL_TEXTURE_COMPARE_MODE</c>.</summary>
    public const uint TextureCompareMode = 0x884C;

    /// <summary><c>GL_TEXTURE_COMPARE_FUNC</c>.</summary>
    public const uint TextureCompareFunc = 0x884D;

    /// <summary><c>GL_COMPARE_REF_TO_TEXTURE</c>.</summary>
    public const uint CompareRefToTexture = 0x884E;

    /// <summary><c>GL_TEXTURE_BORDER_COLOR</c>.</summary>
    public const uint TextureBorderColour = 0x1004;

    /// <summary><c>GL_TEXTURE_MAX_ANISOTROPY</c>.</summary>
    public const uint TextureMaxAnisotropy = 0x84FE;

    /// <summary><c>GL_NEAREST</c>.</summary>
    public const uint Nearest = 0x2600;

    /// <summary><c>GL_LINEAR</c>.</summary>
    public const uint Linear = 0x2601;

    /// <summary><c>GL_NEAREST_MIPMAP_NEAREST</c>.</summary>
    public const uint NearestMipmapNearest = 0x2700;

    /// <summary><c>GL_LINEAR_MIPMAP_NEAREST</c>.</summary>
    public const uint LinearMipmapNearest = 0x2701;

    /// <summary><c>GL_NEAREST_MIPMAP_LINEAR</c>.</summary>
    public const uint NearestMipmapLinear = 0x2702;

    /// <summary><c>GL_LINEAR_MIPMAP_LINEAR</c>.</summary>
    public const uint LinearMipmapLinear = 0x2703;

    /// <summary><c>GL_REPEAT</c>.</summary>
    public const uint Repeat = 0x2901;

    /// <summary><c>GL_MIRRORED_REPEAT</c>.</summary>
    public const uint MirroredRepeat = 0x8370;

    /// <summary><c>GL_CLAMP_TO_EDGE</c>.</summary>
    public const uint ClampToEdge = 0x812F;

    /// <summary><c>GL_CLAMP_TO_BORDER</c>.</summary>
    public const uint ClampToBorder = 0x812D;

    // ── Framebuffers ────────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_FRAMEBUFFER</c>.</summary>
    public const uint Framebuffer = 0x8D40;

    /// <summary><c>GL_READ_FRAMEBUFFER</c>.</summary>
    public const uint ReadFramebuffer = 0x8CA8;

    /// <summary><c>GL_DRAW_FRAMEBUFFER</c>.</summary>
    public const uint DrawFramebuffer = 0x8CA9;

    /// <summary><c>GL_COLOR_ATTACHMENT0</c> — the rest follow it.</summary>
    public const uint ColourAttachment0 = 0x8CE0;

    /// <summary><c>GL_DEPTH_ATTACHMENT</c>.</summary>
    public const uint DepthAttachment = 0x8D00;

    /// <summary><c>GL_STENCIL_ATTACHMENT</c>.</summary>
    public const uint StencilAttachment = 0x8D20;

    /// <summary><c>GL_DEPTH_STENCIL_ATTACHMENT</c>.</summary>
    public const uint DepthStencilAttachment = 0x821A;

    /// <summary><c>GL_FRAMEBUFFER_COMPLETE</c>.</summary>
    public const uint FramebufferComplete = 0x8CD5;

    /// <summary><c>GL_COLOR</c>, for <c>glClearBufferfv</c>.</summary>
    public const uint Colour = 0x1800;

    /// <summary><c>GL_DEPTH</c>.</summary>
    public const uint Depth = 0x1801;

    /// <summary><c>GL_STENCIL</c>.</summary>
    public const uint Stencil = 0x1802;

    /// <summary><c>GL_DEPTH_STENCIL</c>.</summary>
    public const uint DepthStencil = 0x84F9;

    /// <summary><c>GL_NONE</c>.</summary>
    public const uint None = 0;

    // ── Capabilities ────────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_DEPTH_TEST</c>.</summary>
    public const uint DepthTest = 0x0B71;

    /// <summary><c>GL_STENCIL_TEST</c>.</summary>
    public const uint StencilTest = 0x0B90;

    /// <summary><c>GL_CULL_FACE</c>.</summary>
    public const uint CullFace = 0x0B44;

    /// <summary><c>GL_BLEND</c>.</summary>
    public const uint Blend = 0x0BE2;

    /// <summary><c>GL_SCISSOR_TEST</c>.</summary>
    public const uint ScissorTest = 0x0C11;

    /// <summary><c>GL_POLYGON_OFFSET_FILL</c>.</summary>
    public const uint PolygonOffsetFill = 0x8037;

    /// <summary><c>GL_DEPTH_CLAMP</c>.</summary>
    public const uint DepthClamp = 0x864F;

    /// <summary><c>GL_FRAMEBUFFER_SRGB</c>.</summary>
    public const uint FramebufferSrgb = 0x8DB9;

    /// <summary><c>GL_PRIMITIVE_RESTART_FIXED_INDEX</c>.</summary>
    public const uint PrimitiveRestartFixedIndex = 0x8D69;

    // ── Comparison and stencil ──────────────────────────────────────────────────────────────

    /// <summary><c>GL_NEVER</c>.</summary>
    public const uint Never = 0x0200;

    /// <summary><c>GL_LESS</c>.</summary>
    public const uint Less = 0x0201;

    /// <summary><c>GL_EQUAL</c>.</summary>
    public const uint Equal = 0x0202;

    /// <summary><c>GL_LEQUAL</c>.</summary>
    public const uint LessEqual = 0x0203;

    /// <summary><c>GL_GREATER</c>.</summary>
    public const uint Greater = 0x0204;

    /// <summary><c>GL_NOTEQUAL</c>.</summary>
    public const uint NotEqual = 0x0205;

    /// <summary><c>GL_GEQUAL</c>.</summary>
    public const uint GreaterEqual = 0x0206;

    /// <summary><c>GL_ALWAYS</c>.</summary>
    public const uint Always = 0x0207;

    /// <summary><c>GL_KEEP</c>.</summary>
    public const uint Keep = 0x1E00;

    /// <summary><c>GL_ZERO</c> — also the zero blend factor.</summary>
    public const uint Zero = 0;

    /// <summary><c>GL_REPLACE</c>.</summary>
    public const uint Replace = 0x1E01;

    /// <summary><c>GL_INCR</c>.</summary>
    public const uint Increment = 0x1E02;

    /// <summary><c>GL_DECR</c>.</summary>
    public const uint Decrement = 0x1E03;

    /// <summary><c>GL_INVERT</c>.</summary>
    public const uint Invert = 0x150A;

    /// <summary><c>GL_INCR_WRAP</c>.</summary>
    public const uint IncrementWrap = 0x8507;

    /// <summary><c>GL_DECR_WRAP</c>.</summary>
    public const uint DecrementWrap = 0x8508;

    // ── Blending ────────────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_ONE</c>.</summary>
    public const uint One = 1;

    /// <summary><c>GL_SRC_COLOR</c>.</summary>
    public const uint SourceColour = 0x0300;

    /// <summary><c>GL_ONE_MINUS_SRC_COLOR</c>.</summary>
    public const uint OneMinusSourceColour = 0x0301;

    /// <summary><c>GL_SRC_ALPHA</c>.</summary>
    public const uint SourceAlpha = 0x0302;

    /// <summary><c>GL_ONE_MINUS_SRC_ALPHA</c>.</summary>
    public const uint OneMinusSourceAlpha = 0x0303;

    /// <summary><c>GL_DST_ALPHA</c>.</summary>
    public const uint DestinationAlpha = 0x0304;

    /// <summary><c>GL_ONE_MINUS_DST_ALPHA</c>.</summary>
    public const uint OneMinusDestinationAlpha = 0x0305;

    /// <summary><c>GL_DST_COLOR</c>.</summary>
    public const uint DestinationColour = 0x0306;

    /// <summary><c>GL_ONE_MINUS_DST_COLOR</c>.</summary>
    public const uint OneMinusDestinationColour = 0x0307;

    /// <summary><c>GL_SRC_ALPHA_SATURATE</c>.</summary>
    public const uint SourceAlphaSaturate = 0x0308;

    /// <summary><c>GL_CONSTANT_COLOR</c>.</summary>
    public const uint ConstantColour = 0x8001;

    /// <summary><c>GL_ONE_MINUS_CONSTANT_COLOR</c>.</summary>
    public const uint OneMinusConstantColour = 0x8002;

    /// <summary><c>GL_FUNC_ADD</c>.</summary>
    public const uint FuncAdd = 0x8006;

    /// <summary><c>GL_FUNC_SUBTRACT</c>.</summary>
    public const uint FuncSubtract = 0x800A;

    /// <summary><c>GL_FUNC_REVERSE_SUBTRACT</c>.</summary>
    public const uint FuncReverseSubtract = 0x800B;

    /// <summary><c>GL_MIN</c>.</summary>
    public const uint Min = 0x8007;

    /// <summary><c>GL_MAX</c>.</summary>
    public const uint Max = 0x8008;

    // ── Faces and winding ───────────────────────────────────────────────────────────────────

    /// <summary><c>GL_FRONT</c>.</summary>
    public const uint Front = 0x0404;

    /// <summary><c>GL_BACK</c>.</summary>
    public const uint Back = 0x0405;

    /// <summary><c>GL_FRONT_AND_BACK</c>.</summary>
    public const uint FrontAndBack = 0x0408;

    /// <summary><c>GL_CW</c>.</summary>
    public const uint Clockwise = 0x0900;

    /// <summary><c>GL_CCW</c>.</summary>
    public const uint CounterClockwise = 0x0901;

    /// <summary><c>GL_FILL</c>.</summary>
    public const uint Fill = 0x1B02;

    /// <summary><c>GL_LINE</c>.</summary>
    public const uint Line = 0x1B01;

    // ── Primitives ──────────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_POINTS</c>.</summary>
    public const uint Points = 0x0000;

    /// <summary><c>GL_LINES</c>.</summary>
    public const uint Lines = 0x0001;

    /// <summary><c>GL_LINE_STRIP</c>.</summary>
    public const uint LineStrip = 0x0003;

    /// <summary><c>GL_TRIANGLES</c>.</summary>
    public const uint Triangles = 0x0004;

    /// <summary><c>GL_TRIANGLE_STRIP</c>.</summary>
    public const uint TriangleStrip = 0x0005;

    // ── Component types ─────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_BYTE</c>.</summary>
    public const uint Byte = 0x1400;

    /// <summary><c>GL_UNSIGNED_BYTE</c>.</summary>
    public const uint UnsignedByte = 0x1401;

    /// <summary><c>GL_SHORT</c>.</summary>
    public const uint Short = 0x1402;

    /// <summary><c>GL_UNSIGNED_SHORT</c>.</summary>
    public const uint UnsignedShort = 0x1403;

    /// <summary><c>GL_INT</c>.</summary>
    public const uint Int = 0x1404;

    /// <summary><c>GL_UNSIGNED_INT</c>.</summary>
    public const uint UnsignedInt = 0x1405;

    /// <summary><c>GL_FLOAT</c>.</summary>
    public const uint Float = 0x1406;

    /// <summary><c>GL_HALF_FLOAT</c>.</summary>
    public const uint HalfFloat = 0x140B;

    /// <summary><c>GL_UNSIGNED_INT_2_10_10_10_REV</c>.</summary>
    public const uint UnsignedInt2101010Rev = 0x8368;

    /// <summary><c>GL_UNSIGNED_INT_10F_11F_11F_REV</c>.</summary>
    public const uint UnsignedInt10F11F11FRev = 0x8C3B;

    /// <summary><c>GL_UNSIGNED_INT_24_8</c>.</summary>
    public const uint UnsignedInt248 = 0x84FA;

    /// <summary><c>GL_FLOAT_32_UNSIGNED_INT_24_8_REV</c>.</summary>
    public const uint Float32UnsignedInt248Rev = 0x8DAD;

    // ── Pixel formats ───────────────────────────────────────────────────────────────────────

    /// <summary><c>GL_RED</c>.</summary>
    public const uint Red = 0x1903;

    /// <summary><c>GL_RG</c>.</summary>
    public const uint RedGreen = 0x8227;

    /// <summary><c>GL_RGB</c>.</summary>
    public const uint Rgb = 0x1907;

    /// <summary><c>GL_RGBA</c>.</summary>
    public const uint Rgba = 0x1908;

    /// <summary><c>GL_BGRA</c>.</summary>
    public const uint Bgra = 0x80E1;

    /// <summary><c>GL_RED_INTEGER</c>.</summary>
    public const uint RedInteger = 0x8D94;

    /// <summary><c>GL_RGBA_INTEGER</c>.</summary>
    public const uint RgbaInteger = 0x8D99;

    /// <summary><c>GL_DEPTH_COMPONENT</c>.</summary>
    public const uint DepthComponent = 0x1902;

    // ── Shaders and programs ────────────────────────────────────────────────────────────────

    /// <summary><c>GL_VERTEX_SHADER</c>.</summary>
    public const uint VertexShader = 0x8B31;

    /// <summary><c>GL_FRAGMENT_SHADER</c>.</summary>
    public const uint FragmentShader = 0x8B30;

    /// <summary><c>GL_GEOMETRY_SHADER</c>.</summary>
    public const uint GeometryShader = 0x8DD9;

    /// <summary><c>GL_COMPUTE_SHADER</c>.</summary>
    public const uint ComputeShader = 0x91B9;

    /// <summary><c>GL_TESS_CONTROL_SHADER</c>.</summary>
    public const uint TessellationControlShader = 0x8E88;

    /// <summary><c>GL_TESS_EVALUATION_SHADER</c>.</summary>
    public const uint TessellationEvaluationShader = 0x8E87;

    // ── Clip control, barriers and labels ───────────────────────────────────────────────────

    /// <summary><c>GL_LOWER_LEFT</c>.</summary>
    public const uint LowerLeft = 0x8CA1;

    /// <summary><c>GL_UPPER_LEFT</c>.</summary>
    public const uint UpperLeft = 0x8CA2;

    /// <summary><c>GL_NEGATIVE_ONE_TO_ONE</c>.</summary>
    public const uint NegativeOneToOne = 0x935E;

    /// <summary><c>GL_ZERO_TO_ONE</c>.</summary>
    public const uint ZeroToOne = 0x935F;

    /// <summary><c>GL_ALL_BARRIER_BITS</c>.</summary>
    public const uint AllBarrierBits = 0xFFFFFFFF;

    /// <summary><c>GL_SHADER_STORAGE_BARRIER_BIT</c>.</summary>
    public const uint ShaderStorageBarrierBit = 0x00002000;

    /// <summary><c>GL_SHADER_IMAGE_ACCESS_BARRIER_BIT</c>.</summary>
    public const uint ShaderImageAccessBarrierBit = 0x00000020;

    /// <summary><c>GL_TEXTURE_FETCH_BARRIER_BIT</c>.</summary>
    public const uint TextureFetchBarrierBit = 0x00000008;

    /// <summary><c>GL_FRAMEBUFFER_BARRIER_BIT</c>.</summary>
    public const uint FramebufferBarrierBit = 0x00000400;

    /// <summary><c>GL_BUFFER_UPDATE_BARRIER_BIT</c>.</summary>
    public const uint BufferUpdateBarrierBit = 0x00000200;

    /// <summary><c>GL_DEBUG_SOURCE_APPLICATION</c>.</summary>
    public const uint DebugSourceApplication = 0x824A;

    /// <summary><c>GL_DEBUG_TYPE_MARKER</c>.</summary>
    public const uint DebugTypeMarker = 0x8268;

    /// <summary><c>GL_DEBUG_SEVERITY_NOTIFICATION</c>.</summary>
    public const uint DebugSeverityNotification = 0x826B;
}
