// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Collections;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics;

/// <summary>A GPU buffer.</summary>
/// <remarks>
///     A marker a backend subclasses to hold whatever it needs, and the type
///     <see cref="Handle{T}" /> is generic over — so a <see cref="BufferHandle" /> cannot be passed
///     where a <see cref="TextureHandle" /> is wanted. Instances live in a backend-owned table and
///     are reached only through handles, which is what keeps GPU objects out of the garbage
///     collector's way.
/// </remarks>
public abstract class GpuBuffer {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuBuffer() { }
}

/// <summary>A GPU texture.</summary>
public abstract class GpuTexture {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuTexture() { }
}

/// <summary>A view of part of a texture, possibly reinterpreted.</summary>
public abstract class GpuTextureView {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuTextureView() { }
}

/// <summary>A sampler.</summary>
public abstract class GpuSampler {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuSampler() { }
}

/// <summary>A compiled pipeline.</summary>
public abstract class GpuPipeline {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuPipeline() { }
}

/// <summary>The shape of the descriptor sets and push constants a pipeline expects.</summary>
public abstract class GpuPipelineLayout {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuPipelineLayout() { }
}

/// <summary>The shape of one descriptor set.</summary>
public abstract class GpuDescriptorSetLayout {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuDescriptorSetLayout() { }
}

/// <summary>A bound group of resources.</summary>
public abstract class GpuDescriptorSet {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuDescriptorSet() { }
}

/// <summary>A shader module.</summary>
public abstract class GpuShader {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuShader() { }
}

/// <summary>A buffer, by handle.</summary>
public readonly record struct BufferHandle(Handle<GpuBuffer> Value) {
    /// <summary>No buffer.</summary>
    public static BufferHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuBuffer>.Null;
}

/// <summary>A texture, by handle.</summary>
public readonly record struct TextureHandle(Handle<GpuTexture> Value) {
    /// <summary>No texture.</summary>
    public static TextureHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuTexture>.Null;
}

/// <summary>A texture view, by handle.</summary>
public readonly record struct TextureViewHandle(Handle<GpuTextureView> Value) {
    /// <summary>No view.</summary>
    public static TextureViewHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuTextureView>.Null;
}

/// <summary>A sampler, by handle.</summary>
public readonly record struct SamplerHandle(Handle<GpuSampler> Value) {
    /// <summary>No sampler.</summary>
    public static SamplerHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuSampler>.Null;
}

/// <summary>A pipeline, by handle.</summary>
public readonly record struct PipelineHandle(Handle<GpuPipeline> Value) {
    /// <summary>No pipeline.</summary>
    public static PipelineHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuPipeline>.Null;
}

/// <summary>A pipeline layout, by handle.</summary>
public readonly record struct PipelineLayoutHandle(Handle<GpuPipelineLayout> Value) {
    /// <summary>No layout.</summary>
    public static PipelineLayoutHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuPipelineLayout>.Null;
}

/// <summary>A descriptor-set layout, by handle.</summary>
public readonly record struct DescriptorSetLayoutHandle(Handle<GpuDescriptorSetLayout> Value) {
    /// <summary>No layout.</summary>
    public static DescriptorSetLayoutHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuDescriptorSetLayout>.Null;
}

/// <summary>A descriptor set, by handle.</summary>
public readonly record struct DescriptorSetHandle(Handle<GpuDescriptorSet> Value) {
    /// <summary>No set.</summary>
    public static DescriptorSetHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuDescriptorSet>.Null;
}

/// <summary>A shader module, by handle.</summary>
public readonly record struct ShaderHandle(Handle<GpuShader> Value) {
    /// <summary>No shader.</summary>
    public static ShaderHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuShader>.Null;
}

/// <summary>What to create a buffer as.</summary>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Usage">Everything it will be used for.</param>
/// <param name="Access">Where its memory lives.</param>
/// <param name="Name">
///     A name for the debugger, RenderDoc and the validation layers. Not optional in spirit: a
///     capture full of <c>VkBuffer 0x7f…</c> is a capture nobody can read, and naming resources is
///     the difference between a five-minute investigation and an afternoon.
/// </param>
public readonly record struct BufferDescription(
    long Size,
    BufferUsage Usage,
    MemoryAccess Access = MemoryAccess.DeviceLocal,
    string Name = ""
) {
    /// <summary>Checks the description is one a backend could satisfy.</summary>
    /// <exception cref="ArgumentException">It is not, with a message naming what is wrong.</exception>
    public void Validate() {
        if (Size <= 0) {
            throw new ArgumentException($"A buffer needs a positive size; '{Name}' asked for {Size}.");
        }

        if (Usage == BufferUsage.None) {
            throw new ArgumentException($"Buffer '{Name}' declares no usage, so nothing could ever bind it.");
        }

        if (Access == MemoryAccess.HostReadback && (Usage & BufferUsage.CopyDestination) == 0) {
            throw new ArgumentException(
                $"Buffer '{Name}' is for readback but is not a copy destination, so nothing could ever "
                + "put data in it."
            );
        }
    }
}

/// <summary>What to create a texture as.</summary>
/// <param name="Format">Its format.</param>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height in texels.</param>
/// <param name="Usage">Everything it will be used for.</param>
/// <param name="Depth">Its depth in texels, for a 3D texture.</param>
/// <param name="MipLevels">How many mip levels, or <c>0</c> for a full chain.</param>
/// <param name="ArrayLayers">How many array layers. A cube map has six per cube.</param>
/// <param name="SampleCount">How many samples per texel.</param>
/// <param name="Dimension">Its shape.</param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
public readonly record struct TextureDescription(
    PixelFormat Format,
    int Width,
    int Height,
    TextureUsage Usage,
    int Depth = 1,
    int MipLevels = 1,
    int ArrayLayers = 1,
    int SampleCount = 1,
    TextureDimension Dimension = TextureDimension.Texture2D,
    string Name = ""
) {
    /// <summary>The mip level count, with <c>0</c> resolved to a full chain.</summary>
    public int EffectiveMipLevels =>
        MipLevels > 0 ? MipLevels : PixelFormats.MipLevelCount(Width, Height, Depth);

    /// <summary>How many bytes every level and layer occupies, tightly packed.</summary>
    /// <remarks>
    ///     What a staging buffer has to be, and what an asset's on-disk size is compared against.
    ///     A backend's own allocation is larger — alignment, tiling, padding — so this is the upload
    ///     size and not the residency size.
    /// </remarks>
    public long TotalSize {
        get {
            var total = 0L;
            var levels = EffectiveMipLevels;

            for (var level = 0; level < levels; level++) {
                total += Format.LevelSize(
                    Math.Max(1, Width >> level),
                    Math.Max(1, Height >> level),
                    Math.Max(1, Depth >> level)
                );
            }

            return total * ArrayLayers;
        }
    }

    /// <summary>Checks the description is one a backend could satisfy.</summary>
    /// <exception cref="ArgumentException">It is not, with a message naming what is wrong.</exception>
    public void Validate() {
        if (Format == PixelFormat.Undefined) {
            throw new ArgumentException($"Texture '{Name}' has no format.");
        }

        if (Width <= 0 || Height <= 0 || Depth <= 0) {
            throw new ArgumentException(
                $"Texture '{Name}' has a non-positive extent: {Width}×{Height}×{Depth}."
            );
        }

        if (Usage == TextureUsage.None) {
            throw new ArgumentException($"Texture '{Name}' declares no usage, so nothing could ever bind it.");
        }

        if (SampleCount <= 0 || (SampleCount & (SampleCount - 1)) != 0) {
            throw new ArgumentException(
                $"Texture '{Name}' asked for {SampleCount} samples; a sample count must be a power of two."
            );
        }

        if (SampleCount > 1 && EffectiveMipLevels > 1) {
            throw new ArgumentException(
                $"Texture '{Name}' is multisampled and has mip levels. No API allows both: a multisampled "
                + "image has to be resolved before it can be filtered."
            );
        }

        if (Dimension == TextureDimension.TextureCube && ArrayLayers % 6 != 0) {
            throw new ArgumentException(
                $"Cube texture '{Name}' has {ArrayLayers} layers, which is not a whole number of cubes."
            );
        }

        if (Format.IsDepthStencil() && (Usage & TextureUsage.Storage) != 0) {
            throw new ArgumentException(
                $"Texture '{Name}' is a depth format used as storage, which no backend allows."
            );
        }
    }
}

/// <summary>What to create a sampler as.</summary>
/// <param name="MinFilter">How to filter when the texture is minified.</param>
/// <param name="MagFilter">How to filter when it is magnified.</param>
/// <param name="MipFilter">How to filter between mip levels.</param>
/// <param name="AddressU">What to do outside <c>[0, 1]</c> horizontally.</param>
/// <param name="AddressV">What to do outside <c>[0, 1]</c> vertically.</param>
/// <param name="AddressW">What to do outside <c>[0, 1]</c> in depth.</param>
/// <param name="Anisotropy">How many anisotropic samples, or <c>1</c> for none.</param>
/// <param name="Compare">
///     A comparison to run instead of returning the sampled value, for a shadow sampler, or
///     <see langword="null" /> for an ordinary one.
/// </param>
/// <param name="Border">What to return outside the texture when clamping to a border.</param>
/// <param name="MinLod">The lowest mip level to sample.</param>
/// <param name="MaxLod">The highest mip level to sample.</param>
/// <param name="LodBias">A bias added to the computed mip level.</param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
public readonly record struct SamplerDescription(
    FilterMode MinFilter = FilterMode.Linear,
    FilterMode MagFilter = FilterMode.Linear,
    FilterMode MipFilter = FilterMode.Linear,
    AddressMode AddressU = AddressMode.Repeat,
    AddressMode AddressV = AddressMode.Repeat,
    AddressMode AddressW = AddressMode.Repeat,
    float Anisotropy = 1f,
    CompareFunction? Compare = null,
    BorderColour Border = BorderColour.TransparentBlack,
    float MinLod = 0f,
    float MaxLod = 1000f,
    float LodBias = 0f,
    string Name = ""
) {
    /// <summary>Trilinear repeat, which is what most textures want.</summary>
    public static SamplerDescription LinearRepeat => new(Name: "LinearRepeat");

    /// <summary>Trilinear, clamped to the edge — a full-screen or lookup texture.</summary>
    public static SamplerDescription LinearClamp => new(
        AddressU: AddressMode.ClampToEdge,
        AddressV: AddressMode.ClampToEdge,
        AddressW: AddressMode.ClampToEdge,
        Name: "LinearClamp"
    );

    /// <summary>Unfiltered and clamped — a data texture where interpolation would be nonsense.</summary>
    public static SamplerDescription PointClamp => new(
        FilterMode.Nearest,
        FilterMode.Nearest,
        FilterMode.Nearest,
        AddressMode.ClampToEdge,
        AddressMode.ClampToEdge,
        AddressMode.ClampToEdge,
        Name: "PointClamp"
    );

    /// <summary>
    ///     A shadow-comparison sampler: linear, clamped to a white border, comparing with
    ///     <see cref="CompareFunction.GreaterEqual" /> for the engine's reversed depth.
    /// </summary>
    public static SamplerDescription Shadow => new(
        AddressU: AddressMode.ClampToBorder,
        AddressV: AddressMode.ClampToBorder,
        AddressW: AddressMode.ClampToBorder,
        Compare: CompareFunction.GreaterEqual,
        Border: BorderColour.OpaqueWhite,
        Name: "Shadow"
    );
}

/// <summary>One attachment in a render pass.</summary>
/// <param name="View">What to render into.</param>
/// <param name="Load">What to do with it at the start of the pass.</param>
/// <param name="Store">What to do with it at the end.</param>
/// <param name="ClearColour">What to clear to, when <paramref name="Load" /> clears.</param>
/// <param name="ResolveView">
///     Where to resolve a multisampled attachment, when <paramref name="Store" /> resolves.
/// </param>
public readonly record struct ColourAttachment(
    TextureViewHandle View,
    LoadAction Load = LoadAction.Clear,
    StoreAction Store = StoreAction.Store,
    Color4 ClearColour = default,
    TextureViewHandle ResolveView = default
);

/// <summary>The depth-stencil attachment in a render pass.</summary>
/// <param name="View">What to render into.</param>
/// <param name="DepthLoad">What to do with depth at the start of the pass.</param>
/// <param name="DepthStore">What to do with depth at the end.</param>
/// <param name="ClearDepth">
///     What to clear depth to. Defaults to <c>0</c>, which is <em>far</em> under the engine's
///     reversed-Z convention — clearing to 1 here is the classic mistake and produces a scene that
///     depth-tests away entirely.
/// </param>
/// <param name="StencilLoad">What to do with stencil at the start of the pass.</param>
/// <param name="StencilStore">What to do with stencil at the end.</param>
/// <param name="ClearStencil">What to clear stencil to.</param>
/// <param name="IsReadOnly">
///     Whether the pass only tests depth, which lets a backend keep it readable by a shader at the
///     same time.
/// </param>
public readonly record struct DepthStencilAttachment(
    TextureViewHandle View,
    LoadAction DepthLoad = LoadAction.Clear,
    StoreAction DepthStore = StoreAction.Store,
    float ClearDepth = 0f,
    LoadAction StencilLoad = LoadAction.DontCare,
    StoreAction StencilStore = StoreAction.DontCare,
    byte ClearStencil = 0,
    bool IsReadOnly = false
);
