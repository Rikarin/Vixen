// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>A GL buffer object and what it was created as.</summary>
sealed class GlBuffer(uint name, in BufferDescription description) : GpuBuffer {
    /// <summary>The GL name.</summary>
    public uint Name { get; } = name;

    /// <summary>What it was created as.</summary>
    public BufferDescription Description { get; } = description;

    /// <summary>
    ///     The target this buffer is bound to for ordinary work.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         GL buffers are typeless in principle and typed in practice: the first target a buffer
    ///         is bound to is a hint the driver allocates against, and a buffer that is bound as
    ///         <c>GL_ARRAY_BUFFER</c> once and <c>GL_UNIFORM_BUFFER</c> forever after is one the
    ///         driver has placed badly.
    ///     </para>
    ///     <para>
    ///         So the usage flags pick a home target at creation, in the order that matters most for
    ///         placement. Copies and transfers still bind to <c>GL_COPY_READ_BUFFER</c> and
    ///         <c>GL_COPY_WRITE_BUFFER</c>, which exist precisely so that a transfer does not
    ///         disturb the binding a draw is about to use.
    ///     </para>
    /// </remarks>
    public uint Target { get; } = Home(description.Usage);

    static uint Home(BufferUsage usage) {
        if ((usage & BufferUsage.Index) != 0) {
            return GlConstants.ElementArrayBuffer;
        }

        if ((usage & BufferUsage.Vertex) != 0) {
            return GlConstants.ArrayBuffer;
        }

        if ((usage & BufferUsage.Uniform) != 0) {
            return GlConstants.UniformBuffer;
        }

        if ((usage & BufferUsage.Storage) != 0) {
            return GlConstants.ShaderStorageBuffer;
        }

        return (usage & BufferUsage.Indirect) != 0
            ? GlConstants.DrawIndirectBuffer
            : GlConstants.CopyReadBuffer;
    }
}

/// <summary>A GL texture object and what it was created as.</summary>
sealed class GlTexture(uint name, in TextureDescription description, uint target) : GpuTexture {
    /// <summary>The GL name.</summary>
    public uint Name { get; } = name;

    /// <summary>What it was created as.</summary>
    public TextureDescription Description { get; } = description;

    /// <summary>Which target it binds to.</summary>
    public uint Target { get; } = target;

    /// <summary>Whether it is layered, and therefore attached with
    /// <c>glFramebufferTextureLayer</c>.</summary>
    public bool IsLayered =>
        Target is GlConstants.Texture2DArray or GlConstants.Texture3D or GlConstants.TextureCubeMap;
}

/// <summary>A view of part of a texture.</summary>
/// <remarks>
///     <para>
///         <b>Not a GL object.</b> GL 4.3 has <c>glTextureView</c> and GLES has nothing at all, so a
///         view here is a record of which subresource to attach or sample — the texture, a mip
///         level, an array layer, and a format to read it as.
///     </para>
///     <para>
///         The consequence is real and worth stating: a view that <em>reinterprets</em> a format —
///         reading an <c>Rgba8UNorm</c> texture as <c>Rgba8UNormSrgb</c>, which the RHI allows and
///         Vulkan does for free — cannot be done on this backend without a copy. Attaching and
///         sampling a subresource, which is what views are overwhelmingly used for, costs nothing.
///     </para>
/// </remarks>
sealed class GlTextureView(
    TextureHandle texture,
    PixelFormat format,
    int baseMipLevel,
    int mipLevelCount,
    int baseArrayLayer,
    int arrayLayerCount
) : GpuTextureView {
    /// <summary>The texture it views.</summary>
    public TextureHandle Texture { get; } = texture;

    /// <summary>The format it is read as.</summary>
    public PixelFormat Format { get; } = format;

    /// <summary>The first mip level.</summary>
    public int BaseMipLevel { get; } = baseMipLevel;

    /// <summary>How many mip levels.</summary>
    public int MipLevelCount { get; } = mipLevelCount;

    /// <summary>The first array layer.</summary>
    public int BaseArrayLayer { get; } = baseArrayLayer;

    /// <summary>How many array layers.</summary>
    public int ArrayLayerCount { get; } = arrayLayerCount;
}

/// <summary>A GL sampler object.</summary>
sealed class GlSampler(uint name, in SamplerDescription description) : GpuSampler {
    /// <summary>The GL name.</summary>
    public uint Name { get; } = name;

    /// <summary>What it was created as.</summary>
    public SamplerDescription Description { get; } = description;
}

/// <summary>A shader, held as source until a pipeline gives it a layout to compile against.</summary>
/// <remarks>
///     <para>
///         <b>Compiled late, and that is forced rather than chosen.</b> GL's unit of compilation
///         that can be bound is the <em>program</em> — vertex and fragment linked together — and the
///         binding indices a shader needs depend on the pipeline layout, which is not known until
///         the pipeline is created. So <c>CreateShader</c> keeps the source and
///         <c>CreateGraphicsPipeline</c> translates, compiles and links.
///     </para>
///     <para>
///         Which is the same statement as "a GL pipeline is a program plus a state block", from the
///         other end. It also means a shader shared by two pipelines with different layouts is
///         compiled twice — correct, and the reason <see cref="GlProgramCache" /> keys on the
///         combination rather than on the shaders.
///     </para>
/// </remarks>
sealed class GlShader(ShaderStage stage, string source, string name) : GpuShader {
    /// <summary>Which stage it is for.</summary>
    public ShaderStage Stage { get; } = stage;

    /// <summary>The GL-dialect GLSL.</summary>
    public string Source { get; } = source;

    /// <summary>A name for the debugger.</summary>
    public string Name { get; } = name;
}

/// <summary>The shape of one descriptor set.</summary>
sealed class GlDescriptorSetLayout(in DescriptorSetLayoutDescription description) : GpuDescriptorSetLayout {
    /// <summary>Which of the four conventional sets this is.</summary>
    public DescriptorSetSlot Slot { get; } = description.Slot;

    /// <summary>What it contains.</summary>
    public DescriptorBinding[] Bindings { get; } = description.Bindings;

    /// <summary>A name for the debugger.</summary>
    public string Name { get; } = description.Name;
}

/// <summary>A pipeline layout: four set shapes, a push-constant size, and where GL puts them.</summary>
sealed class GlPipelineLayout(GlBindingPlan plan, DescriptorSetLayoutHandle[] sets, int pushConstantBytes)
    : GpuPipelineLayout {
    /// <summary>Where each binding lives in GL's flat namespaces.</summary>
    public GlBindingPlan Plan { get; } = plan;

    /// <summary>The set layouts, as declared.</summary>
    public DescriptorSetLayoutHandle[] Sets { get; } = sets;

    /// <summary>The push-constant block size in bytes.</summary>
    public int PushConstantBytes { get; } = pushConstantBytes;
}

/// <summary>A pipeline: a linked program and the state block to apply when it is bound.</summary>
/// <remarks>
///     ADR-001's sentence "PSOs become program+state tuples" is this class. Everything the
///     description says about raster, depth, stencil and blend is kept as it was given, because GL
///     applies it as loose state at bind time and <see cref="GlStateCache" /> needs the whole of it
///     to diff against what is already set.
/// </remarks>
sealed class GlPipeline : GpuPipeline {
    /// <summary>A graphics pipeline.</summary>
    public GlPipeline(
        uint program,
        in GraphicsPipelineDescription description,
        GlPipelineLayout layout,
        uint vertexArray
    ) {
        Program = program;
        Layout = layout;
        VertexArray = vertexArray;
        IsCompute = false;
        Topology = GlEnums.Topology(description.Topology);
        Rasterizer = description.Rasterizer;
        DepthStencil = description.DepthStencil;
        VertexBuffers = description.VertexBuffers ?? [];
        Name = description.Name;

        Blend = description.ColourTargets.Length > 0
            ? description.ColourTargets[0].EffectiveBlend
            : BlendState.Opaque;

        // GL 4.5 has per-attachment blending and GLES 3.0 does not, so the RHI's independent-blend
        // capability decides whether a description with two different blend states is legal here.
        // Reported rather than silently taking the first, because "the second target blends like the
        // first" is a picture nobody can debug.
        IndependentBlend = description.ColourTargets.Length > 1
            && description.ColourTargets.Skip(1).Any(target => target.EffectiveBlend != Blend);
    }

    /// <summary>A compute pipeline.</summary>
    public GlPipeline(uint program, in ComputePipelineDescription description, GlPipelineLayout layout) {
        Program = program;
        Layout = layout;
        VertexArray = 0;
        IsCompute = true;
        Topology = GlConstants.Triangles;
        Rasterizer = RasterizerState.Default;
        DepthStencil = DepthStencilState.Disabled;
        Blend = BlendState.Opaque;
        VertexBuffers = [];
        Name = description.Name;
    }

    /// <summary>The linked GL program.</summary>
    public uint Program { get; }

    /// <summary>The layout it was compiled against.</summary>
    public GlPipelineLayout Layout { get; }

    /// <summary>
    ///     The vertex array object holding this pipeline's attribute format.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One VAO per pipeline, not one per draw and not one shared. It holds the attribute
    ///         enables and the instancing divisors, which belong to the pipeline and never change.
    ///     </para>
    ///     <para>
    ///         ⚠ It does <em>not</em> hold the attribute formats, however much it looks as though it
    ///         should. In the non-DSA path all three profiles share,
    ///         <c>glVertexAttribPointer</c> captures whatever is bound to <c>GL_ARRAY_BUFFER</c> when
    ///         it is called — so an attribute's format and its buffer are one piece of state and
    ///         cannot be set apart. The formats are therefore applied when a vertex buffer is bound,
    ///         which is why the command list defers vertex-buffer binds until the draw: doing them
    ///         before <c>BindPipeline</c> would write into whichever VAO happened to be current.
    ///     </para>
    /// </remarks>
    public uint VertexArray { get; }

    /// <summary>Whether it is a compute pipeline.</summary>
    public bool IsCompute { get; }

    /// <summary>The GL primitive mode.</summary>
    public uint Topology { get; }

    /// <summary>The raster state to apply on bind.</summary>
    public RasterizerState Rasterizer { get; }

    /// <summary>The depth-stencil state to apply on bind.</summary>
    public DepthStencilState DepthStencil { get; }

    /// <summary>The blend state to apply on bind.</summary>
    public BlendState Blend { get; }

    /// <summary>Whether the description asked for per-attachment blend states that differ.</summary>
    public bool IndependentBlend { get; }

    /// <summary>The vertex buffer layouts, for stride and offset at bind time.</summary>
    public VertexBufferLayout[] VertexBuffers { get; }

    /// <summary>A name for the debugger.</summary>
    public string Name { get; }

    /// <summary>Where push constants land, or <c>-1</c> if the program declares none.</summary>
    public int PushConstantLocation { get; init; } = -1;
}

/// <summary>A descriptor set: in GL, a list of things to bind, kept on the CPU.</summary>
/// <remarks>
///     <para>
///         There is no GL object here and there cannot be. Vulkan's descriptor set is memory the
///         driver reads at draw time; GL's equivalent is a sequence of <c>glBindBufferRange</c> and
///         <c>glBindTexture</c> calls made before the draw. So this holds the writes and the replay
///         makes the calls, which is what ADR-001 means by "descriptor sets become bind-group
///         caches".
///     </para>
///     <para>
///         The consequence a renderer will feel: updating a set is free here and binding one is not,
///         which is the opposite of Vulkan. Code that allocates a set per draw — the thing the RHI's
///         dynamic-offset bindings exist to avoid — is slow on Vulkan and slow here, for different
///         reasons, which is a reassuring kind of agreement.
///     </para>
/// </remarks>
sealed class GlDescriptorSet(
    DescriptorSetLayoutHandle layout,
    DescriptorSetSlot slot,
    DescriptorBinding[] bindings,
    string name
) : GpuDescriptorSet {
    readonly Dictionary<(uint Binding, int ArrayIndex), DescriptorWrite> writes = [];

    /// <summary>The layout it was created from.</summary>
    public DescriptorSetLayoutHandle Layout { get; } = layout;

    /// <summary>Which of the four conventional sets it is.</summary>
    public DescriptorSetSlot Slot { get; } = slot;

    /// <summary>A name for the debugger.</summary>
    public string Name { get; } = name;

    /// <summary>
    ///     The sampler to use for a texture binding that did not carry one of its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one place this backend has to invent a rule.</b> Vulkan, D3D12 and WebGPU all
    ///         let a sampler be a descriptor in its own right, bound independently of the textures
    ///         that read through it. GL has no such thing: <c>glBindSampler</c> takes a
    ///         <em>texture unit</em>, so a sampler is always attached to a texture, and there is no
    ///         way to express "this sampler, for whichever textures the shader pairs it with".
    ///     </para>
    ///     <para>
    ///         The rule, stated once here rather than discovered per fixture: a texture write may
    ///         carry its own sampler and that wins; otherwise the set's first standalone
    ///         <see cref="DescriptorKind.Sampler" /> write applies to every texture unit the set
    ///         binds. A set with two standalone samplers and no per-texture ones is the case that
    ///         cannot be expressed, and it is rejected rather than resolved arbitrarily.
    ///     </para>
    /// </remarks>
    public SamplerHandle DefaultSampler { get; private set; }

    /// <summary>How many standalone sampler bindings the set has been given.</summary>
    public int StandaloneSamplers { get; private set; }

    /// <summary>Every write, by binding and array element.</summary>
    public IReadOnlyDictionary<(uint Binding, int ArrayIndex), DescriptorWrite> Writes => writes;

    /// <summary>What the <em>layout</em> says a binding is, whatever the write said.</summary>
    /// <remarks>
    ///     <para>
    ///         The layout is authoritative and the write is not, and the difference is not academic.
    ///         <c>DescriptorWrite.Uniform</c> produces <see cref="DescriptorKind.UniformBuffer" />
    ///         because that is the common case and there is no separate helper for the dynamic one —
    ///         so a caller who declared a binding dynamic in the layout, bound it with the obvious
    ///         helper, and passed a dynamic offset would have that offset silently dropped by a
    ///         backend that trusted the write.
    ///     </para>
    ///     <para>
    ///         Which is a per-draw transform landing on the wrong object: a picture, not an error.
    ///         Vulkan happens to catch it because its <c>VkWriteDescriptorSet</c> carries a type the
    ///         validation layers check against the layout; nothing here would.
    ///     </para>
    /// </remarks>
    public DescriptorKind KindOf(uint binding, DescriptorKind fallback) {
        foreach (var declared in bindings) {
            if (declared.Binding == binding) {
                return declared.Kind;
            }
        }

        return fallback;
    }

    /// <summary>Records a write.</summary>
    public void Write(in DescriptorWrite write) {
        writes[(write.Binding, write.ArrayIndex)] = write;

        if (write.Kind != DescriptorKind.Sampler) {
            return;
        }

        if (!DefaultSampler.IsValid) {
            DefaultSampler = write.Sampler;
            StandaloneSamplers = 1;
            return;
        }

        if (DefaultSampler != write.Sampler) {
            StandaloneSamplers++;
        }
    }
}
