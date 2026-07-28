// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>The RHI's vocabulary translated into GL's.</summary>
/// <remarks>
///     <para>
///         Total functions with no default arm, so adding an RHI enum member fails to compile here
///         rather than falling through to whatever the last case happened to be. That has caught
///         more real bugs in graphics backends than any other single habit: a silent default turns
///         "we forgot to map <c>Min</c>" into "every min-blend is an add", which draws something
///         plausible everywhere and correct nowhere.
///     </para>
///     <para>
///         Where GL genuinely cannot express something, the mapping throws with the reason rather
///         than picking the nearest thing. <c>docs/rhi-backend-mapping.md</c> lists every one of
///         those, and there are fewer than expected.
///     </para>
/// </remarks>
static class GlEnums {
    /// <summary>The depth or stencil comparison.</summary>
    public static uint Compare(CompareFunction function) => function switch {
        CompareFunction.Never => GlConstants.Never,
        CompareFunction.Less => GlConstants.Less,
        CompareFunction.Equal => GlConstants.Equal,
        CompareFunction.LessEqual => GlConstants.LessEqual,
        CompareFunction.Greater => GlConstants.Greater,
        CompareFunction.NotEqual => GlConstants.NotEqual,
        CompareFunction.GreaterEqual => GlConstants.GreaterEqual,
        CompareFunction.Always => GlConstants.Always,
        _ => throw Unmapped(function)
    };

    /// <summary>What a stencil test does to the value.</summary>
    public static uint Stencil(StencilOperation operation) => operation switch {
        StencilOperation.Keep => GlConstants.Keep,
        StencilOperation.Zero => GlConstants.Zero,
        StencilOperation.Replace => GlConstants.Replace,
        StencilOperation.IncrementClamp => GlConstants.Increment,
        StencilOperation.DecrementClamp => GlConstants.Decrement,
        StencilOperation.Invert => GlConstants.Invert,
        StencilOperation.IncrementWrap => GlConstants.IncrementWrap,
        StencilOperation.DecrementWrap => GlConstants.DecrementWrap,
        _ => throw Unmapped(operation)
    };

    /// <summary>One side of a blend equation.</summary>
    public static uint Blend(BlendFactor factor) => factor switch {
        BlendFactor.Zero => GlConstants.Zero,
        BlendFactor.One => GlConstants.One,
        BlendFactor.SourceColour => GlConstants.SourceColour,
        BlendFactor.OneMinusSourceColour => GlConstants.OneMinusSourceColour,
        BlendFactor.SourceAlpha => GlConstants.SourceAlpha,
        BlendFactor.OneMinusSourceAlpha => GlConstants.OneMinusSourceAlpha,
        BlendFactor.DestinationColour => GlConstants.DestinationColour,
        BlendFactor.OneMinusDestinationColour => GlConstants.OneMinusDestinationColour,
        BlendFactor.DestinationAlpha => GlConstants.DestinationAlpha,
        BlendFactor.OneMinusDestinationAlpha => GlConstants.OneMinusDestinationAlpha,
        BlendFactor.Constant => GlConstants.ConstantColour,
        BlendFactor.OneMinusConstant => GlConstants.OneMinusConstantColour,
        BlendFactor.SourceAlphaSaturated => GlConstants.SourceAlphaSaturate,
        _ => throw Unmapped(factor)
    };

    /// <summary>How the two sides of a blend combine.</summary>
    public static uint BlendOp(BlendOperation operation) => operation switch {
        BlendOperation.Add => GlConstants.FuncAdd,
        BlendOperation.Subtract => GlConstants.FuncSubtract,
        BlendOperation.ReverseSubtract => GlConstants.FuncReverseSubtract,
        BlendOperation.Min => GlConstants.Min,
        BlendOperation.Max => GlConstants.Max,
        _ => throw Unmapped(operation)
    };

    /// <summary>What the vertices mean.</summary>
    public static uint Topology(PrimitiveTopology topology) => topology switch {
        PrimitiveTopology.PointList => GlConstants.Points,
        PrimitiveTopology.LineList => GlConstants.Lines,
        PrimitiveTopology.LineStrip => GlConstants.LineStrip,
        PrimitiveTopology.TriangleList => GlConstants.Triangles,
        PrimitiveTopology.TriangleStrip => GlConstants.TriangleStrip,
        _ => throw Unmapped(topology)
    };

    /// <summary>The index component type.</summary>
    public static uint Index(IndexFormat format) => format switch {
        IndexFormat.UInt16 => GlConstants.UnsignedShort,
        IndexFormat.UInt32 => GlConstants.UnsignedInt,
        _ => throw Unmapped(format)
    };

    /// <summary>How many bytes one index occupies.</summary>
    public static int IndexSize(IndexFormat format) => format switch {
        IndexFormat.UInt16 => 2,
        IndexFormat.UInt32 => 4,
        _ => throw Unmapped(format)
    };

    /// <summary>Which faces to discard, or <c>0</c> for none.</summary>
    /// <remarks>
    ///     Zero rather than <c>GL_NONE</c> with a separate flag, because GL says "cull nothing" by
    ///     disabling the capability rather than by naming a face — one of the small shape
    ///     differences that a state cache has to model rather than translate.
    /// </remarks>
    public static uint Cull(CullMode mode) => mode switch {
        CullMode.None => 0,
        CullMode.Front => GlConstants.Front,
        CullMode.Back => GlConstants.Back,
        _ => throw Unmapped(mode)
    };

    /// <summary>Which winding is front.</summary>
    /// <remarks>
    ///     <b>Inverted, and that is not a bug.</b> Every profile here changes the direction of the
    ///     clip-to-window <c>y</c> mapping — GL 4.5 with <c>glClipControl(GL_UPPER_LEFT, …)</c>, the
    ///     rest in the vertex shader — so that clip <c>y = +1</c> lands at texel row zero, which is
    ///     where the reference backend puts it through its negative-height viewport. Changing that
    ///     direction reverses triangle winding, so counter-clockwise in the RHI reaches the
    ///     rasteriser clockwise, and a backend that passed the winding through unchanged would cull
    ///     exactly the triangles it should keep — which looks like a missing mesh rather than like a
    ///     convention error, and is the reason <c>cull-front</c> and <c>cull-back</c> are separate
    ///     golden fixtures rather than one.
    /// </remarks>
    public static uint Winding(FrontFace face) => face switch {
        FrontFace.CounterClockwise => GlConstants.Clockwise,
        FrontFace.Clockwise => GlConstants.CounterClockwise,
        _ => throw Unmapped(face)
    };

    /// <summary>Filled or wireframe.</summary>
    public static uint Fill(FillMode mode) => mode switch {
        FillMode.Solid => GlConstants.Fill,
        FillMode.Wireframe => GlConstants.Line,
        _ => throw Unmapped(mode)
    };

    /// <summary>What a sampler does outside <c>[0, 1]</c>.</summary>
    public static uint Address(AddressMode mode, GlProfile profile) => mode switch {
        AddressMode.Repeat => GlConstants.Repeat,
        AddressMode.MirrorRepeat => GlConstants.MirroredRepeat,
        AddressMode.ClampToEdge => GlConstants.ClampToEdge,

        // Silently degrading this one is how a shadow map ends up shadowing everything outside its
        // bounds with whatever the edge texel held — a rendering bug with no error behind it. GLES
        // gets clamp-to-edge and a caller who asked for a border gets told.
        AddressMode.ClampToBorder => profile.HasBorderClamp()
            ? GlConstants.ClampToBorder
            : throw new NotSupportedException(
                $"A sampler asked to clamp to a border, which {profile} has no core support for. "
                + "Clamp to edge and pad the texture, or gate on GraphicsDeviceFeatures."
            ),
        _ => throw Unmapped(mode)
    };

    /// <summary>The minification filter, which in GL carries the mip filter as well.</summary>
    /// <remarks>
    ///     GL folds two of the RHI's three filter choices into one enumerant, so this is a product
    ///     rather than a translation. Vulkan, D3D12 and WebGPU all keep them separate; GL is the
    ///     odd one, and it is the direction that loses information rather than the one that gains
    ///     it, so nothing is lost going this way.
    /// </remarks>
    public static uint MinFilter(FilterMode minification, FilterMode mip, bool hasMips) {
        if (!hasMips) {
            return minification == FilterMode.Linear ? GlConstants.Linear : GlConstants.Nearest;
        }

        return (minification, mip) switch {
            (FilterMode.Nearest, FilterMode.Nearest) => GlConstants.NearestMipmapNearest,
            (FilterMode.Nearest, FilterMode.Linear) => GlConstants.NearestMipmapLinear,
            (FilterMode.Linear, FilterMode.Nearest) => GlConstants.LinearMipmapNearest,
            (FilterMode.Linear, FilterMode.Linear) => GlConstants.LinearMipmapLinear,
            _ => throw Unmapped(minification)
        };
    }

    /// <summary>The magnification filter, which has no mip component.</summary>
    public static uint MagFilter(FilterMode magnification) => magnification switch {
        FilterMode.Nearest => GlConstants.Nearest,
        FilterMode.Linear => GlConstants.Linear,
        _ => throw Unmapped(magnification)
    };

    /// <summary>The four components of a border colour.</summary>
    public static float[] Border(BorderColour colour) => colour switch {
        BorderColour.TransparentBlack => [0f, 0f, 0f, 0f],
        BorderColour.OpaqueBlack => [0f, 0f, 0f, 1f],
        BorderColour.OpaqueWhite => [1f, 1f, 1f, 1f],
        _ => throw Unmapped(colour)
    };

    /// <summary>The GL shader type for a stage.</summary>
    public static uint Stage(ShaderStage stage) => stage switch {
        ShaderStage.Vertex => GlConstants.VertexShader,
        ShaderStage.Fragment => GlConstants.FragmentShader,
        ShaderStage.Compute => GlConstants.ComputeShader,
        ShaderStage.Geometry => GlConstants.GeometryShader,
        ShaderStage.TessellationControl => GlConstants.TessellationControlShader,
        ShaderStage.TessellationEvaluation => GlConstants.TessellationEvaluationShader,

        // Task and mesh shaders are a vendor extension on GL and are in no core profile. This is one
        // of the two places the backend refuses rather than emulates.
        _ => throw new NotSupportedException(
            $"{stage} has no OpenGL shader type. Ask GraphicsDeviceFeatures.HasMeshShaders first."
        )
    };

    /// <summary>A vertex attribute's component count, type, and whether it is normalised.</summary>
    /// <remarks>
    ///     The <c>Integer</c> flag decides between <c>glVertexAttribPointer</c> and
    ///     <c>glVertexAttribIPointer</c>, and choosing wrong is invisible until a bone index arrives
    ///     in the shader as <c>1.0</c> having been "normalised" from <c>1</c>.
    /// </remarks>
    public static (int Size, uint Type, bool Normalised, bool Integer) Vertex(VertexFormat format) =>
        format switch {
            VertexFormat.Float32 => (1, GlConstants.Float, false, false),
            VertexFormat.Float32X2 => (2, GlConstants.Float, false, false),
            VertexFormat.Float32X3 => (3, GlConstants.Float, false, false),
            VertexFormat.Float32X4 => (4, GlConstants.Float, false, false),
            VertexFormat.Float16X2 => (2, GlConstants.HalfFloat, false, false),
            VertexFormat.Float16X4 => (4, GlConstants.HalfFloat, false, false),
            VertexFormat.UNorm8X4 => (4, GlConstants.UnsignedByte, true, false),
            VertexFormat.SNorm8X4 => (4, GlConstants.Byte, true, false),
            VertexFormat.UInt8X4 => (4, GlConstants.UnsignedByte, false, true),
            VertexFormat.UInt32 => (1, GlConstants.UnsignedInt, false, true),
            VertexFormat.UNorm16X2 => (2, GlConstants.UnsignedShort, true, false),
            VertexFormat.SNorm16X4 => (4, GlConstants.Short, true, false),
            _ => throw Unmapped(format)
        };

    /// <summary>How many bytes a vertex attribute occupies.</summary>
    public static int VertexSize(VertexFormat format) {
        var (size, type, _, _) = Vertex(format);

        var component = type switch {
            GlConstants.Byte or GlConstants.UnsignedByte => 1,
            GlConstants.Short or GlConstants.UnsignedShort or GlConstants.HalfFloat => 2,
            _ => 4
        };

        return size * component;
    }

    /// <summary>The target a texture of a given shape binds to.</summary>
    public static uint TextureTarget(TextureDescription description) {
        if (description.SampleCount > 1) {
            return GlConstants.Texture2DMultisample;
        }

        return description.Dimension switch {
            // GL has no 1D textures in GLES at all, and a 1×n 2D texture is what every engine uses
            // for one on desktop too. Mapping rather than refusing, because the difference is
            // invisible to a shader that samples with a single coordinate.
            TextureDimension.Texture1D => GlConstants.Texture2D,
            TextureDimension.Texture2D => description.ArrayLayers > 1
                ? GlConstants.Texture2DArray
                : GlConstants.Texture2D,
            TextureDimension.Texture3D => GlConstants.Texture3D,
            TextureDimension.TextureCube => GlConstants.TextureCubeMap,
            _ => throw Unmapped(description.Dimension)
        };
    }

    static ArgumentOutOfRangeException Unmapped<T>(T value) where T : struct, Enum =>
        new(nameof(value), value, $"{typeof(T).Name}.{value} has no OpenGL mapping.");
}
