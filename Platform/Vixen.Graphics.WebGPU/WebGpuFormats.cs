// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>The RHI's formats in WebGPU's terms.</summary>
/// <remarks>
///     <para>
///         Pure functions with no device in sight, and deliberately so: this is the half of a backend
///         that can be tested without an implementation, and it is also the half where a mistake is
///         silent. A format mapped to the wrong WebGPU enum does not fail — it renders the wrong
///         colours, or samples a normal map as sRGB, and the bug is found by eye weeks later.
///     </para>
///     <para>
///         WebGPU's format list is shorter than Vulkan's, and the gaps are not oversights: it has no
///         16-bit normalised formats at all, and no fixed-point 24-bit depth. Each is named below
///         with what happens instead, because "returns Undefined" at a call site three layers away is
///         a worse answer than a sentence here.
///     </para>
/// </remarks>
public static class WebGpuFormats {
    /// <summary>The WebGPU format for one of ours.</summary>
    /// <param name="format">The engine format.</param>
    /// <returns><see cref="WgpuTextureFormat.Undefined" /> for a format WebGPU does not have.</returns>
    public static WgpuTextureFormat ToWebGpu(this PixelFormat format) => format switch {
        PixelFormat.R8UNorm => WgpuTextureFormat.R8Unorm,
        PixelFormat.R8SNorm => WgpuTextureFormat.R8Snorm,
        PixelFormat.R8UInt => WgpuTextureFormat.R8Uint,
        PixelFormat.R8SInt => WgpuTextureFormat.R8Sint,

        PixelFormat.Rg8UNorm => WgpuTextureFormat.Rg8Unorm,
        PixelFormat.Rg8SNorm => WgpuTextureFormat.Rg8Snorm,
        PixelFormat.R16Float => WgpuTextureFormat.R16Float,
        PixelFormat.R16UInt => WgpuTextureFormat.R16Uint,

        // R16UNorm has no WebGPU equivalent: the specification has 16-bit integer and half-float
        // formats and no 16-bit normalised ones. R16Float is the nearest thing and is *not*
        // substituted here, because it changes both the range and the precision of every texel and
        // the caller is the only one who can say whether that is acceptable.
        PixelFormat.R16UNorm => WgpuTextureFormat.Undefined,

        PixelFormat.Rgba8UNorm => WgpuTextureFormat.Rgba8Unorm,
        PixelFormat.Rgba8UNormSrgb => WgpuTextureFormat.Rgba8UnormSrgb,
        PixelFormat.Bgra8UNorm => WgpuTextureFormat.Bgra8Unorm,
        PixelFormat.Bgra8UNormSrgb => WgpuTextureFormat.Bgra8UnormSrgb,
        PixelFormat.Rgba8SNorm => WgpuTextureFormat.Rgba8Snorm,
        PixelFormat.Rg16Float => WgpuTextureFormat.Rg16Float,
        PixelFormat.R32Float => WgpuTextureFormat.R32Float,
        PixelFormat.R32UInt => WgpuTextureFormat.R32Uint,
        PixelFormat.Rgb10A2UNorm => WgpuTextureFormat.Rgb10A2Unorm,
        PixelFormat.Rg11B10Float => WgpuTextureFormat.Rg11B10Ufloat,

        PixelFormat.Rgba16Float => WgpuTextureFormat.Rgba16Float,
        PixelFormat.Rg32Float => WgpuTextureFormat.Rg32Float,

        // Rgba16UNorm, for the same reason as R16UNorm.
        PixelFormat.Rgba16UNorm => WgpuTextureFormat.Undefined,

        PixelFormat.Rgba32Float => WgpuTextureFormat.Rgba32Float,
        PixelFormat.Rgba32UInt => WgpuTextureFormat.Rgba32Uint,

        PixelFormat.Depth16UNorm => WgpuTextureFormat.Depth16Unorm,
        PixelFormat.Depth32Float => WgpuTextureFormat.Depth32Float,

        // WebGPU has no D24_UNORM_S8_UINT. `depth24plus-stencil8` is "at least 24 bits of depth",
        // whose actual layout the implementation chooses — so this is a legitimate mapping for an
        // attachment and a wrong one for a copy, which is why CanCopy below refuses it. The engine's
        // depth is Depth32Float anyway (reversed Z, docs/plan/05); this format exists for content
        // that arrived from elsewhere.
        PixelFormat.Depth24UNormStencil8 => WgpuTextureFormat.Depth24PlusStencil8,
        PixelFormat.Depth32FloatStencil8 => WgpuTextureFormat.Depth32FloatStencil8,

        PixelFormat.Bc1RgbaUNorm => WgpuTextureFormat.Bc1RgbaUnorm,
        PixelFormat.Bc1RgbaUNormSrgb => WgpuTextureFormat.Bc1RgbaUnormSrgb,
        PixelFormat.Bc3RgbaUNorm => WgpuTextureFormat.Bc3RgbaUnorm,
        PixelFormat.Bc3RgbaUNormSrgb => WgpuTextureFormat.Bc3RgbaUnormSrgb,
        PixelFormat.Bc4RUNorm => WgpuTextureFormat.Bc4RUnorm,
        PixelFormat.Bc5RgUNorm => WgpuTextureFormat.Bc5RgUnorm,
        PixelFormat.Bc6HRgbUFloat => WgpuTextureFormat.Bc6HRgbUfloat,
        PixelFormat.Bc7RgbaUNorm => WgpuTextureFormat.Bc7RgbaUnorm,
        PixelFormat.Bc7RgbaUNormSrgb => WgpuTextureFormat.Bc7RgbaUnormSrgb,

        PixelFormat.Etc2Rgb8A1UNorm => WgpuTextureFormat.Etc2Rgb8A1Unorm,
        PixelFormat.Etc2Rgba8UNorm => WgpuTextureFormat.Etc2Rgba8Unorm,

        PixelFormat.Astc4X4UNorm => WgpuTextureFormat.Astc4X4Unorm,
        PixelFormat.Astc4X4UNormSrgb => WgpuTextureFormat.Astc4X4UnormSrgb,
        PixelFormat.Astc8X8UNorm => WgpuTextureFormat.Astc8X8Unorm,
        PixelFormat.Astc8X8UNormSrgb => WgpuTextureFormat.Astc8X8UnormSrgb,

        _ => WgpuTextureFormat.Undefined
    };

    /// <summary>One of ours for a WebGPU format.</summary>
    /// <param name="format">The WebGPU format.</param>
    /// <returns><see cref="PixelFormat.Undefined" /> for a format the RHI does not name.</returns>
    /// <remarks>
    ///     What a surface's preferred format is read back through: the implementation picks, and the
    ///     swapchain has to report the choice in the RHI's vocabulary.
    /// </remarks>
    public static PixelFormat ToEngine(this WgpuTextureFormat format) => format switch {
        WgpuTextureFormat.R8Unorm => PixelFormat.R8UNorm,
        WgpuTextureFormat.R8Snorm => PixelFormat.R8SNorm,
        WgpuTextureFormat.R8Uint => PixelFormat.R8UInt,
        WgpuTextureFormat.R8Sint => PixelFormat.R8SInt,

        WgpuTextureFormat.Rg8Unorm => PixelFormat.Rg8UNorm,
        WgpuTextureFormat.Rg8Snorm => PixelFormat.Rg8SNorm,
        WgpuTextureFormat.R16Float => PixelFormat.R16Float,
        WgpuTextureFormat.R16Uint => PixelFormat.R16UInt,

        WgpuTextureFormat.Rgba8Unorm => PixelFormat.Rgba8UNorm,
        WgpuTextureFormat.Rgba8UnormSrgb => PixelFormat.Rgba8UNormSrgb,
        WgpuTextureFormat.Bgra8Unorm => PixelFormat.Bgra8UNorm,
        WgpuTextureFormat.Bgra8UnormSrgb => PixelFormat.Bgra8UNormSrgb,
        WgpuTextureFormat.Rgba8Snorm => PixelFormat.Rgba8SNorm,
        WgpuTextureFormat.Rg16Float => PixelFormat.Rg16Float,
        WgpuTextureFormat.R32Float => PixelFormat.R32Float,
        WgpuTextureFormat.R32Uint => PixelFormat.R32UInt,
        WgpuTextureFormat.Rgb10A2Unorm => PixelFormat.Rgb10A2UNorm,
        WgpuTextureFormat.Rg11B10Ufloat => PixelFormat.Rg11B10Float,

        WgpuTextureFormat.Rgba16Float => PixelFormat.Rgba16Float,
        WgpuTextureFormat.Rg32Float => PixelFormat.Rg32Float,
        WgpuTextureFormat.Rgba32Float => PixelFormat.Rgba32Float,
        WgpuTextureFormat.Rgba32Uint => PixelFormat.Rgba32UInt,

        WgpuTextureFormat.Depth16Unorm => PixelFormat.Depth16UNorm,
        WgpuTextureFormat.Depth32Float => PixelFormat.Depth32Float,
        WgpuTextureFormat.Depth24PlusStencil8 => PixelFormat.Depth24UNormStencil8,
        WgpuTextureFormat.Depth32FloatStencil8 => PixelFormat.Depth32FloatStencil8,

        WgpuTextureFormat.Bc1RgbaUnorm => PixelFormat.Bc1RgbaUNorm,
        WgpuTextureFormat.Bc1RgbaUnormSrgb => PixelFormat.Bc1RgbaUNormSrgb,
        WgpuTextureFormat.Bc3RgbaUnorm => PixelFormat.Bc3RgbaUNorm,
        WgpuTextureFormat.Bc3RgbaUnormSrgb => PixelFormat.Bc3RgbaUNormSrgb,
        WgpuTextureFormat.Bc4RUnorm => PixelFormat.Bc4RUNorm,
        WgpuTextureFormat.Bc5RgUnorm => PixelFormat.Bc5RgUNorm,
        WgpuTextureFormat.Bc6HRgbUfloat => PixelFormat.Bc6HRgbUFloat,
        WgpuTextureFormat.Bc7RgbaUnorm => PixelFormat.Bc7RgbaUNorm,
        WgpuTextureFormat.Bc7RgbaUnormSrgb => PixelFormat.Bc7RgbaUNormSrgb,

        WgpuTextureFormat.Etc2Rgb8A1Unorm => PixelFormat.Etc2Rgb8A1UNorm,
        WgpuTextureFormat.Etc2Rgba8Unorm => PixelFormat.Etc2Rgba8UNorm,

        WgpuTextureFormat.Astc4X4Unorm => PixelFormat.Astc4X4UNorm,
        WgpuTextureFormat.Astc4X4UnormSrgb => PixelFormat.Astc4X4UNormSrgb,
        WgpuTextureFormat.Astc8X8Unorm => PixelFormat.Astc8X8UNorm,
        WgpuTextureFormat.Astc8X8UnormSrgb => PixelFormat.Astc8X8UNormSrgb,

        _ => PixelFormat.Undefined
    };

    /// <summary>The WebGPU format, or an exception naming what is missing.</summary>
    /// <param name="format">The engine format.</param>
    /// <param name="what">What is being created, for the message.</param>
    /// <exception cref="NotSupportedException">WebGPU has no such format.</exception>
    public static WgpuTextureFormat Require(this PixelFormat format, string what) {
        var mapped = format.ToWebGpu();

        if (mapped == WgpuTextureFormat.Undefined) {
            throw new NotSupportedException(
                $"'{what}' asked for {format}, which WebGPU does not have. The specification has no "
                + "16-bit normalised formats; pick a half-float or an 8-bit normalised one."
            );
        }

        return mapped;
    }

    /// <summary>Whether a texture in this format may take part in a buffer copy.</summary>
    /// <param name="format">The engine format.</param>
    /// <remarks>
    ///     <c>depth24plus</c> and <c>depth24plus-stencil8</c> may not: their bit layout is the
    ///     implementation's business, so there is no defined byte pattern for a copy to move. Every
    ///     other format may.
    /// </remarks>
    public static bool CanCopy(this PixelFormat format) =>
        format != PixelFormat.Depth24UNormStencil8;

    /// <summary>The WebGPU vertex format for one of ours.</summary>
    /// <param name="format">The engine format.</param>
    public static WgpuVertexFormat ToWebGpu(this VertexFormat format) => format switch {
        VertexFormat.Float32 => WgpuVertexFormat.Float32,
        VertexFormat.Float32X2 => WgpuVertexFormat.Float32X2,
        VertexFormat.Float32X3 => WgpuVertexFormat.Float32X3,
        VertexFormat.Float32X4 => WgpuVertexFormat.Float32X4,
        VertexFormat.Float16X2 => WgpuVertexFormat.Float16X2,
        VertexFormat.Float16X4 => WgpuVertexFormat.Float16X4,
        VertexFormat.UNorm8X4 => WgpuVertexFormat.Unorm8X4,
        VertexFormat.SNorm8X4 => WgpuVertexFormat.Snorm8X4,
        VertexFormat.UInt8X4 => WgpuVertexFormat.Uint8X4,
        VertexFormat.UInt32 => WgpuVertexFormat.Uint32,
        VertexFormat.UNorm16X2 => WgpuVertexFormat.Unorm16X2,
        VertexFormat.SNorm16X4 => WgpuVertexFormat.Snorm16X4,
        _ => WgpuVertexFormat.Undefined
    };

    /// <summary>How a texture in this format is read by a shader.</summary>
    /// <param name="format">The engine format.</param>
    /// <remarks>
    ///     WebGPU wants this stated in the bind group layout rather than inferred from the view, and
    ///     gets it wrong loudly rather than quietly — a depth texture declared <c>float</c> fails
    ///     layout validation. The one that is easy to miss is that 32-bit float textures are
    ///     <em>unfilterable</em> unless <see cref="WgpuFeatureName.Float32Filterable" /> is on, so
    ///     that decision is passed in rather than assumed.
    /// </remarks>
    /// <param name="float32Filterable">Whether the device has
    /// <see cref="WgpuFeatureName.Float32Filterable" />.</param>
    public static WgpuTextureSampleType SampleType(this PixelFormat format, bool float32Filterable) {
        if (format.HasDepth()) {
            return WgpuTextureSampleType.Depth;
        }

        return format switch {
            PixelFormat.R8UInt or PixelFormat.R16UInt or PixelFormat.R32UInt or PixelFormat.Rgba32UInt =>
                WgpuTextureSampleType.Uint,
            PixelFormat.R8SInt => WgpuTextureSampleType.Sint,
            PixelFormat.R32Float or PixelFormat.Rg32Float or PixelFormat.Rgba32Float =>
                float32Filterable ? WgpuTextureSampleType.Float : WgpuTextureSampleType.UnfilterableFloat,
            _ => WgpuTextureSampleType.Float
        };
    }

    /// <summary>Which planes of a texture in this format a view covers.</summary>
    /// <param name="format">The engine format.</param>
    /// <remarks>
    ///     A view used as a sampled texture may cover only one plane of a combined depth-stencil
    ///     format, and the engine samples depth — so a <c>depth32float-stencil8</c> view is
    ///     depth-only. An attachment view covers all of it, which is what
    ///     <see cref="WgpuTextureAspect.All" /> means and what the depth-stencil attachment path
    ///     asks for separately.
    /// </remarks>
    public static WgpuTextureAspect SampledAspect(this PixelFormat format) =>
        format.HasDepth() && format.HasStencil() ? WgpuTextureAspect.DepthOnly : WgpuTextureAspect.All;

    /// <summary>The sample counts WebGPU guarantees, as the RHI's bit mask.</summary>
    /// <remarks>
    ///     One and four, and nothing else. WebGPU has no way to ask for eight or sixteen: the
    ///     specification fixes the set, so unlike Vulkan there is nothing to query and this is a
    ///     constant rather than a capability read.
    /// </remarks>
    public const int SupportedSampleCounts = 0b101;
}
