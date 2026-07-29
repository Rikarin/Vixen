// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>A buffer, and what it was created as.</summary>
/// <remarks>
///     The description is kept because WebGPU will not tell us: a bounds check on a host write, and
///     the size a vertex binding defaults to, both need it, and asking the implementation costs a
///     call that a browser turns into an interop crossing.
/// </remarks>
sealed class WebGpuBuffer(WebGpuObject handle, BufferDescription description) : GpuBuffer {
    public WebGpuObject Handle { get; } = handle;

    public BufferDescription Description { get; } = description;

    /// <summary>The size WebGPU was asked for, which is the engine's rounded up to four.</summary>
    public long AllocatedSize { get; } = (description.Size + 3) & ~3L;
}

/// <summary>A texture, and what it was created as.</summary>
sealed class WebGpuTexture(WebGpuObject handle, TextureDescription description, bool owned) : GpuTexture {
    public WebGpuObject Handle { get; } = handle;

    public TextureDescription Description { get; } = description;

    /// <summary>
    ///     Whether destroying this should release the WebGPU object.
    /// </summary>
    /// <remarks>
    ///     False for a swapchain image. A surface texture belongs to the surface and is invalidated
    ///     by the next present whether or not anyone released it; releasing it ourselves is a
    ///     double-free on the native surface and a dangling table entry in the browser.
    /// </remarks>
    public bool Owned { get; } = owned;
}

/// <summary>A view, and the texture it came from.</summary>
sealed class WebGpuTextureView(
    WebGpuObject handle,
    TextureHandle texture,
    PixelFormat format,
    bool owned
) : GpuTextureView {
    public WebGpuObject Handle { get; } = handle;

    public TextureHandle Texture { get; } = texture;

    public PixelFormat Format { get; } = format;

    /// <summary>Whether destroying this should release the WebGPU object.</summary>
    public bool Owned { get; } = owned;
}

/// <summary>A sampler, and whether it compares.</summary>
sealed class WebGpuSampler(WebGpuObject handle, SamplerDescription description) : GpuSampler {
    public WebGpuObject Handle { get; } = handle;

    /// <summary>Whether this is a shadow-comparison sampler.</summary>
    /// <remarks>
    ///     Kept so <c>UpdateDescriptorSet</c> can say what is wrong when one is bound through a
    ///     layout that declares the other kind — see
    ///     <see cref="WebGpuConversions.ToWebGpu(in DescriptorBinding)" />.
    /// </remarks>
    public bool Compares { get; } = description.Compare is not null;

    /// <summary>Whether it filters, which a non-filtering binding does not allow.</summary>
    public bool Filters { get; } = description.MinFilter == FilterMode.Linear
        || description.MagFilter == FilterMode.Linear
        || description.MipFilter == FilterMode.Linear
        || description.Anisotropy > 1f;

    public string Name { get; } = description.Name;
}

/// <summary>A shader module.</summary>
sealed class WebGpuShader(WebGpuObject handle, ShaderStage stage, string name) : GpuShader {
    public WebGpuObject Handle { get; } = handle;

    public ShaderStage Stage { get; } = stage;

    public string Name { get; } = name;
}

/// <summary>A bind group layout, and the RHI description it was built from.</summary>
sealed class WebGpuDescriptorSetLayout(
    WebGpuObject handle,
    DescriptorSetLayoutDescription description
) : GpuDescriptorSetLayout {
    public WebGpuObject Handle { get; } = handle;

    public DescriptorSetLayoutDescription Description { get; } = description;

    public DescriptorBinding[] Bindings { get; } = description.Bindings;
}

/// <summary>A pipeline layout, and where its emulated push constants live.</summary>
sealed class WebGpuPipelineLayout(
    WebGpuObject handle,
    int pushConstantGroup,
    int setCount,
    string name
) : GpuPipelineLayout {
    public WebGpuObject Handle { get; } = handle;

    /// <summary>Which bind group carries the push-constant block, or <c>-1</c> for none.</summary>
    public int PushConstantGroup { get; } = pushConstantGroup;

    /// <summary>How many of the caller's own descriptor sets it has.</summary>
    public int SetCount { get; } = setCount;

    public string Name { get; } = name;
}

/// <summary>A bind group, rebuilt whenever what it binds changes.</summary>
/// <remarks>
///     WebGPU bind groups are immutable, and the RHI's are not: <c>UpdateDescriptorSet</c> points an
///     existing set at different resources. So this holds the entries the caller has filled in so
///     far and a bind group built from them, and an update rebuilds the group and retires the old
///     one — which is safe precisely because destruction here is deferred by
///     <see cref="IGraphicsDevice.FramesInFlight" /> frames.
/// </remarks>
sealed class WebGpuDescriptorSet(DescriptorSetLayoutHandle layout, WebGpuDescriptorSetLayout resolved, string name)
    : GpuDescriptorSet {
    /// <summary>What has been bound, indexed the same as the layout's bindings.</summary>
    public WgpuBindGroupEntry[] Entries { get; } = new WgpuBindGroupEntry[resolved.Bindings.Length];

    /// <summary>Which of them have been bound at all.</summary>
    public bool[] Filled { get; } = new bool[resolved.Bindings.Length];

    public DescriptorSetLayoutHandle Layout { get; } = layout;

    public WebGpuDescriptorSetLayout ResolvedLayout { get; } = resolved;

    public string Name { get; } = name;

    /// <summary>The bind group, or <see cref="WebGpuObject.Null" /> before every binding is filled.</summary>
    public WebGpuObject Handle { get; set; }

    /// <summary>Whether every binding has a resource, and the group could therefore be built.</summary>
    public bool IsComplete {
        get {
            foreach (var filled in Filled) {
                if (!filled) {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>A compiled pipeline, and what a bind has to know about it.</summary>
sealed class WebGpuPipeline(
    WebGpuObject handle,
    bool isCompute,
    int pushConstantGroup,
    string name
) : GpuPipeline {
    public WebGpuObject Handle { get; } = handle;

    public bool IsCompute { get; } = isCompute;

    /// <summary>Which bind group carries the push-constant block, or <c>-1</c> for none.</summary>
    /// <remarks>
    ///     Copied off the layout rather than looked up through it, because replay reads it once per
    ///     <c>PushConstants</c> and a pipeline outlives the handle its layout was created from.
    /// </remarks>
    public int PushConstantGroup { get; } = pushConstantGroup;

    public string Name { get; } = name;
}
