// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Vixen.Core.Collections;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>What to build a <see cref="GlDevice" /> out of.</summary>
/// <param name="Api">The GL entry points, already current on this thread.</param>
/// <param name="Present">
///     What to call to present the default framebuffer, or <see langword="null" /> for a device that
///     only renders offscreen.
/// </param>
/// <param name="FramesInFlight">How many frames may be recorded before the first has to finish.</param>
public readonly record struct GlDeviceOptions(
    IGlApi Api,
    Action? Present = null,
    int FramesInFlight = 2
);

/// <summary>The OpenGL adapter.</summary>
sealed class GlAdapter(GlProfile profile, string name, string driverVersion) : IGraphicsAdapter {
    public string Name => name;

    /// <summary>Always unknown.</summary>
    /// <remarks>
    ///     GL has no equivalent of <c>VkPhysicalDeviceType</c>. <c>GL_RENDERER</c> is a marketing
    ///     string, and inferring discrete-versus-integrated from it is a substring match against a
    ///     list that goes out of date — which is worse than saying so.
    /// </remarks>
    public AdapterKind Kind => AdapterKind.Unknown;

    public string DriverVersion => driverVersion;

    /// <summary>Always zero.</summary>
    /// <remarks>Core GL has no memory query at all; <c>NVX_gpu_memory_info</c> and
    /// <c>ATI_meminfo</c> are vendor extensions that disagree about units.</remarks>
    public ulong DeviceMemory => 0;

    public GraphicsDeviceFeatures Features { get; } = profile.Features();
}

/// <summary>The graphics device over an OpenGL context.</summary>
/// <remarks>
///     <para>
///         <b>ADR-001's abstraction validator.</b> D3D12 is postponed past 1.0 and this backend
///         inherits its job: proving the RHI is API-neutral rather than a Vulkan wrapper with the
///         serial numbers filed off. It is a harder test than D3D12 would have been, because GL is
///         further away in every direction that matters — no pipeline objects, no descriptor sets, no
///         explicit barriers, no multithreaded recording, and a clip space the other way up.
///     </para>
///     <para>
///         Every one of those is answered in a named place rather than smeared through the device:
///         <see cref="GlProgramCache" /> and <see cref="GlStateCache" /> for pipelines,
///         <see cref="GlBindingPlan" /> for descriptor sets, <see cref="Replay" /> for barriers,
///         <see cref="GlCommandList" /> for threading, and <see cref="GlslTranslator" /> for clip
///         space. The findings are collected in <c>docs/rhi-backend-mapping.md</c>.
///     </para>
///     <para>
///         <b>Threading.</b> Every GL call happens on the thread that owns the context, which is the
///         thread that constructs the device and the thread that submits. Command lists are recorded
///         anywhere.
///     </para>
///     <para>
///         <b>Deferred destruction is free here.</b> The RHI requires that destroying a resource an
///         in-flight frame still references be safe. GL gives that for nothing: <c>glDelete*</c>
///         unbinds the name and the driver keeps the object alive until nothing references it. That
///         is one of the two places where GL's implicitness is an advantage rather than a tax.
///     </para>
/// </remarks>
public sealed partial class GlDevice : IGraphicsDevice {
    readonly IGlApi gl;
    readonly Action? present;
    readonly GlStateCache state;
    readonly GlFramebufferCache framebuffers;
    readonly GlProgramCache programs;

    readonly HandlePool<GpuBuffer> buffers = new();
    readonly HandlePool<GpuTexture> textures = new();
    readonly HandlePool<GpuTextureView> views = new();
    readonly HandlePool<GpuSampler> samplers = new();
    readonly HandlePool<GpuShader> shaders = new();
    readonly HandlePool<GpuPipeline> pipelines = new();
    readonly HandlePool<GpuPipelineLayout> pipelineLayouts = new();
    readonly HandlePool<GpuDescriptorSetLayout> setLayouts = new();
    readonly HandlePool<GpuDescriptorSet> descriptorSets = new();

    readonly Stack<GlCommandList> pool = new();
    readonly Lock gate = new();

    uint readbackFramebuffer;
    bool disposed;

    /// <summary>Creates a device, reporting failure rather than throwing.</summary>
    /// <param name="options">What to build it out of.</param>
    /// <param name="device">The device, when it was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    /// <returns>Whether a device was created.</returns>
    /// <remarks>
    ///     <para>
    ///         The shape <c>VulkanDevice.TryCreate</c> uses, so a selector walking a preference list
    ///         calls every backend identically.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this cannot do is find a GL context.</b>
    ///         <see cref="GlDeviceOptions.Api" /> is entry points <i>already current on this
    ///         thread</i>, and making them current is the platform's job — one no
    ///         <c>Vixen.Platform</c> implementation does yet, which is why an app head cannot boot on
    ///         this backend however it is asked. Handing in null is therefore the ordinary way to
    ///         arrive here rather than a programming error, and it is reported as a sentence instead
    ///         of an <see cref="ArgumentNullException" />.
    ///     </para>
    /// </remarks>
    public static bool TryCreate(
        GlDeviceOptions options,
        [NotNullWhen(true)] out GlDevice? device,
        [NotNullWhen(false)] out string? reason
    ) {
        device = null;

        if (options.Api is null) {
            reason = "there are no GL entry points. A GL device needs a context that is already "
                + "current on this thread, and no Vixen.Platform implementation creates one yet.";

            return false;
        }

        device = new(options);
        reason = null;

        return true;
    }

    /// <summary>Creates the device over a context that is already current.</summary>
    /// <param name="options">What to build it out of.</param>
    public GlDevice(GlDeviceOptions options) {
        gl = options.Api ?? throw new ArgumentNullException(nameof(options), "A device needs GL entry points.");
        present = options.Present;
        FramesInFlight = Math.Max(1, options.FramesInFlight);
        Profile = gl.Profile;
        Features = Profile.Features();
        Adapter = new GlAdapter(Profile, $"OpenGL ({Profile})", "unknown");

        state = new(gl);
        framebuffers = new(gl);
        programs = new(gl);

        var submitter = new GlSubmitter(this);
        GraphicsQueue = submitter;

        // One queue, three names. GL has exactly one command stream, and reporting three that are
        // secretly the same is what HasAsyncCompute and HasAsyncTransfer are for — both false here,
        // so a renderer that overlaps work knows not to bother.
        ComputeQueue = submitter;
        TransferQueue = submitter;

        // Vulkan's clip space, on the one profile that can simply be told. Everywhere else the
        // vertex shader does it — see GlslTranslator — and the two paths have to agree, which is
        // what `clip-space` in the golden suite is for.
        if (Profile.HasClipControl()) {
            gl.ClipControl(GlConstants.UpperLeft, GlConstants.ZeroToOne);
        }

        // sRGB conversion is a property of the attachment's format in the RHI, as it is in Vulkan
        // and D3D12. In desktop GL it is a global switch that gates whether the format is honoured
        // at all, so it is turned on once and never touched: a linear attachment is unaffected by
        // it. GLES and WebGL2 have no such switch — GL_FRAMEBUFFER_SRGB is not an enumerant they
        // accept, and enabling it is GL_INVALID_ENUM — because they already do what turning it on
        // makes desktop GL do. See GlProfiles.HasFramebufferSrgbControl.
        if (Profile.HasFramebufferSrgbControl()) {
            gl.Enable(GlConstants.FramebufferSrgb);
        }

        // Strip restart is the only topology the RHI has that needs it, and GLES 3.0 has no way to
        // choose the index — the fixed index is the only option, and it is what every other API
        // uses too.
        gl.Enable(GlConstants.PrimitiveRestartFixedIndex);
    }

    /// <summary>Which dialect this device drives.</summary>
    public GlProfile Profile { get; }

    /// <inheritdoc />
    public IGraphicsAdapter Adapter { get; }

    /// <inheritdoc />
    public GraphicsDeviceFeatures Features { get; }

    /// <inheritdoc />
    public ICommandSubmitter GraphicsQueue { get; }

    /// <inheritdoc />
    public ICommandSubmitter ComputeQueue { get; }

    /// <inheritdoc />
    public ICommandSubmitter TransferQueue { get; }

    /// <inheritdoc />
    public int FramesInFlight { get; }

    /// <inheritdoc />
    /// <remarks>Incremented by <see cref="BeginFrame" />, so it names this frame rather than the last.</remarks>
    public long FrameCount { get; private set; }

    /// <summary>How many resources are alive, across every kind.</summary>
    public int LiveResourceCount {
        get {
            lock (gate) {
                return buffers.Count + textures.Count + views.Count + samplers.Count + shaders.Count
                    + pipelines.Count + pipelineLayouts.Count + setLayouts.Count + descriptorSets.Count;
            }
        }
    }

    /// <summary>How many framebuffer objects the pass cache holds.</summary>
    public int FramebufferCount => framebuffers.Count;

    /// <summary>How many distinct programs have been linked.</summary>
    public int ProgramCount => programs.Count;

    // ── Buffers ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public BufferHandle CreateBuffer(in BufferDescription description) {
        description.Validate();

        if ((description.Usage & BufferUsage.Storage) != 0 && !Profile.HasStorageBuffers()) {
            throw new NotSupportedException(
                $"Buffer '{description.Name}' is a storage buffer, which {Profile} has none of. Ask "
                + "GraphicsDeviceFeatures.HasCompute and take the uniform-buffer path."
            );
        }

        var name = gl.GenBuffer();
        var buffer = new GlBuffer(name, description);

        state.BindBuffer(buffer.Target, name);

        gl.BufferData(
            buffer.Target,
            (nuint)description.Size,
            description.Access switch {
                // The usage hint is the only thing GL is told about where memory should live, and it
                // is a hint the driver may ignore. Mapping the RHI's three accesses onto it exactly
                // is the most that can be said.
                MemoryAccess.HostUpload => GlConstants.StreamDraw,
                MemoryAccess.HostReadback => GlConstants.StreamRead,
                _ => GlConstants.StaticDraw
            }
        );

        Label(0x82E0, name, description.Name);

        lock (gate) {
            return new(buffers.Add(buffer));
        }
    }

    /// <inheritdoc />
    public void Write(BufferHandle buffer, long offset, ReadOnlySpan<byte> data) {
        var target = Buffer(buffer);

        if (target.Description.Access == MemoryAccess.DeviceLocal) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' is device-local and cannot be written by the host. "
                + "Stage it through an upload buffer and copy."
            );
        }

        if (offset < 0 || offset + data.Length > target.Description.Size) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Writing {data.Length} bytes at {offset} runs off the end of "
                + $"'{target.Description.Name}', which is {target.Description.Size} bytes."
            );
        }

        // Through GL_COPY_WRITE_BUFFER rather than the buffer's home target, so that writing a
        // vertex buffer between two draws does not knock out the array binding the second one needs.
        state.BindBuffer(GlConstants.CopyWriteBuffer, target.Name);
        gl.BufferSubData(GlConstants.CopyWriteBuffer, (nint)offset, data);
    }

    /// <inheritdoc />
    public void Read(BufferHandle buffer, long offset, Span<byte> destination) {
        var target = Buffer(buffer);

        if (target.Description.Access != MemoryAccess.HostReadback) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' is not a readback buffer."
            );
        }

        state.BindBuffer(GlConstants.CopyReadBuffer, target.Name);
        gl.GetBufferSubData(GlConstants.CopyReadBuffer, (nint)offset, destination);
    }

    // ── Textures ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TextureHandle CreateTexture(in TextureDescription description) {
        description.Validate();

        if (description.Width > Features.MaxTextureSize || description.Height > Features.MaxTextureSize) {
            throw new ArgumentException(
                $"Texture '{description.Name}' is {description.Width}×{description.Height}, larger than "
                + $"this device's {Features.MaxTextureSize} limit."
            );
        }

        if ((description.Usage & TextureUsage.Storage) != 0 && !Profile.HasStorageTextures()) {
            throw new NotSupportedException(
                $"Texture '{description.Name}' is a storage image, which {Profile} has none of."
            );
        }

        var format = GlFormats.Of(description.Format, Profile);
        var target = GlEnums.TextureTarget(description);
        var name = gl.GenTexture();

        state.ActiveTexture(0);
        gl.BindTexture(target, name);

        var levels = description.EffectiveMipLevels;

        if (description.SampleCount > 1) {
            gl.TexStorage2DMultisample(
                target,
                description.SampleCount,
                format.Internal,
                description.Width,
                description.Height
            );
        } else if (target is GlConstants.Texture3D or GlConstants.Texture2DArray) {
            gl.TexStorage3D(
                target,
                levels,
                format.Internal,
                description.Width,
                description.Height,
                target == GlConstants.Texture3D ? description.Depth : description.ArrayLayers
            );
        } else {
            gl.TexStorage2D(target, levels, format.Internal, description.Width, description.Height);
        }

        // Said explicitly because GL's default is a full chain regardless of what was allocated, and
        // a texture sampled beyond its allocated levels is incomplete — which samples as opaque
        // black on every driver and looks like a missing texture rather than a mip range.
        if (description.SampleCount == 1) {
            gl.TexParameter(target, GlConstants.TextureBaseLevel, 0);
            gl.TexParameter(target, GlConstants.TextureMaxLevel, levels - 1);
        }

        Label(0x1702, name, description.Name);

        // The unit's shadow is now wrong: this bind went around the cache so that creation does not
        // disturb a sampler binding, and the cache has to be told rather than guess.
        state.Invalidate();

        lock (gate) {
            return new(textures.Add(new GlTexture(name, description, target)));
        }
    }

    /// <inheritdoc />
    public TextureViewHandle CreateTextureView(
        TextureHandle texture,
        PixelFormat format = PixelFormat.Undefined,
        int baseMipLevel = 0,
        int mipLevelCount = 0,
        int baseArrayLayer = 0,
        int arrayLayerCount = 0
    ) {
        var target = Texture(texture);
        var description = target.Description;

        if (baseMipLevel < 0 || baseMipLevel >= description.EffectiveMipLevels) {
            throw new ArgumentOutOfRangeException(
                nameof(baseMipLevel),
                $"Texture '{description.Name}' has {description.EffectiveMipLevels} mip levels."
            );
        }

        var effective = format == PixelFormat.Undefined ? description.Format : format;

        if (effective != description.Format) {
            throw new NotSupportedException(
                $"A view of '{description.Name}' asked to reinterpret {description.Format} as {effective}. "
                + "GL 4.3's glTextureView could do it and GLES cannot, so this backend does not offer it "
                + "on any profile rather than offering it on one. Create a second texture and copy."
            );
        }

        lock (gate) {
            return new(views.Add(new GlTextureView(
                texture,
                effective,
                baseMipLevel,
                mipLevelCount > 0 ? mipLevelCount : description.EffectiveMipLevels - baseMipLevel,
                baseArrayLayer,
                arrayLayerCount > 0 ? arrayLayerCount : description.ArrayLayers - baseArrayLayer
            )));
        }
    }

    /// <inheritdoc />
    public SamplerHandle CreateSampler(in SamplerDescription description) {
        var name = gl.GenSampler();

        gl.SamplerParameter(
            name,
            GlConstants.TextureMinFilter,
            (int)GlEnums.MinFilter(description.MinFilter, description.MipFilter, description.MaxLod > 0f)
        );

        gl.SamplerParameter(name, GlConstants.TextureMagFilter, (int)GlEnums.MagFilter(description.MagFilter));
        gl.SamplerParameter(name, GlConstants.TextureWrapS, (int)GlEnums.Address(description.AddressU, Profile));
        gl.SamplerParameter(name, GlConstants.TextureWrapT, (int)GlEnums.Address(description.AddressV, Profile));
        gl.SamplerParameter(name, GlConstants.TextureWrapR, (int)GlEnums.Address(description.AddressW, Profile));
        gl.SamplerParameter(name, GlConstants.TextureMinLod, description.MinLod);
        gl.SamplerParameter(name, GlConstants.TextureMaxLod, description.MaxLod);

        if (Profile.HasBorderClamp()) {
            gl.SamplerParameter(name, GlConstants.TextureBorderColour, GlEnums.Border(description.Border));
        }

        if (description.LodBias != 0f && Profile >= GlProfile.Core45) {
            gl.SamplerParameter(name, GlConstants.TextureLodBias, description.LodBias);
        }

        if (description.Anisotropy > 1f && Profile.HasAnisotropy()) {
            gl.SamplerParameter(name, GlConstants.TextureMaxAnisotropy, description.Anisotropy);
        }

        if (description.Compare is { } compare) {
            gl.SamplerParameter(name, GlConstants.TextureCompareMode, (int)GlConstants.CompareRefToTexture);
            gl.SamplerParameter(name, GlConstants.TextureCompareFunc, (int)GlEnums.Compare(compare));
        }

        Label(0x82E6, name, description.Name);

        lock (gate) {
            return new(samplers.Add(new GlSampler(name, description)));
        }
    }

    // ── Shaders, layouts and pipelines ──────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    ///     The bytecode is UTF-8 GLSL, not SPIR-V. <c>ShaderFormat</c> names <c>GlslSource</c> and
    ///     <c>EsslSource</c> separately from <c>Spirv</c> precisely so that the content build hands
    ///     each backend the dialect it consumes, and the RHI's "never parses shader source" rule is
    ///     about the <em>RHI</em> — a backend whose driver takes text has to hand it text.
    /// </remarks>
    public ShaderHandle CreateShader(ShaderStage stage, ReadOnlySpan<byte> bytecode, string name = "") {
        if (bytecode.IsEmpty) {
            throw new ArgumentException($"Shader '{name}' has no bytecode.", nameof(bytecode));
        }

        if (bytecode.Length >= 4 && BitConverter.ToUInt32(bytecode[..4]) == 0x07230203) {
            throw new ArgumentException(
                $"Shader '{name}' is SPIR-V, and this backend consumes GLSL. Compiling SPIR-V here would "
                + "mean carrying a cross-compiler in a runtime assembly; the content build produces the "
                + "dialect each target wants (docs/plan/07).",
                nameof(bytecode)
            );
        }

        lock (gate) {
            return new(shaders.Add(new GlShader(stage, Encoding.UTF8.GetString(bytecode), name)));
        }
    }

    /// <inheritdoc />
    public DescriptorSetLayoutHandle CreateDescriptorSetLayout(in DescriptorSetLayoutDescription description) {
        description.Validate();

        foreach (var binding in description.Bindings) {
            if (binding.Count == 0) {
                throw new NotSupportedException(
                    $"Binding {binding.Binding} in '{description.Name}' is unbounded, which needs bindless "
                    + "descriptors. No OpenGL profile has them in core — ask "
                    + "GraphicsDeviceFeatures.HasBindless."
                );
            }
        }

        lock (gate) {
            return new(setLayouts.Add(new GlDescriptorSetLayout(description)));
        }
    }

    /// <inheritdoc />
    public PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDescription description) {
        var sets = description.Sets ?? [];
        var shapes = new List<(DescriptorSetSlot, DescriptorBinding[], string)>(sets.Length);

        foreach (var handle in sets) {
            var layout = SetLayout(handle);
            shapes.Add((layout.Slot, layout.Bindings, layout.Name));
        }

        var pushBytes = 0;

        foreach (var range in description.PushConstants ?? []) {
            pushBytes = Math.Max(pushBytes, range.Offset + range.Size);
        }

        if (pushBytes > Features.MaxPushConstantSize) {
            throw new ArgumentException(
                $"Pipeline layout '{description.Name}' declares {pushBytes} bytes of push constants and "
                + $"this device allows {Features.MaxPushConstantSize}."
            );
        }

        var plan = GlBindingPlan.Build(shapes, pushBytes);

        lock (gate) {
            return new(pipelineLayouts.Add(new GlPipelineLayout(plan, sets, pushBytes)));
        }
    }

    /// <inheritdoc />
    public DescriptorSetHandle CreateDescriptorSet(DescriptorSetLayoutHandle layout, string name = "") {
        var shape = SetLayout(layout);

        lock (gate) {
            return new(descriptorSets.Add(new GlDescriptorSet(layout, shape.Slot, shape.Bindings, name)));
        }
    }

    /// <inheritdoc />
    public void UpdateDescriptorSet(DescriptorSetHandle descriptors, ReadOnlySpan<DescriptorWrite> writes) {
        var set = DescriptorSet(descriptors);

        foreach (var write in writes) {
            set.Write(write);
        }

        if (set.StandaloneSamplers > 1) {
            throw new NotSupportedException(
                $"Descriptor set '{set.Name}' binds {set.StandaloneSamplers} standalone samplers. GL "
                + "attaches a sampler to a texture unit rather than binding it in its own right, so a set "
                + "with more than one has no unambiguous meaning here. Carry the sampler on the texture "
                + "write instead — DescriptorWrite has a Sampler field for exactly this."
            );
        }
    }

    /// <inheritdoc />
    public PipelineHandle CreateGraphicsPipeline(in GraphicsPipelineDescription description) {
        description.Validate();

        var layout = PipelineLayout(description.Layout);
        var vertex = Shader(description.Vertex);
        var stages = new List<(ShaderStage, string, string)> { (ShaderStage.Vertex, vertex.Source, vertex.Name) };

        if (description.Fragment.IsValid) {
            var fragment = Shader(description.Fragment);
            stages.Add((ShaderStage.Fragment, fragment.Source, fragment.Name));
        }

        if (description.Rasterizer.Fill == FillMode.Wireframe && !Profile.HasWireframe()) {
            throw new NotSupportedException(
                $"Pipeline '{description.Name}' asks for wireframe fill, which GLES has no "
                + "glPolygonMode for. Ask GraphicsDeviceFeatures.HasWireframe."
            );
        }

        var (program, push) = programs.Get(
            new(description.Vertex, description.Fragment, description.Layout),
            stages,
            layout
        );

        var vertexArray = BuildVertexArray(description.VertexBuffers ?? []);
        var pipeline = new GlPipeline(program, description, layout, vertexArray) {
            PushConstantLocation = push
        };

        if (pipeline.IndependentBlend && !Features.HasIndependentBlend) {
            gl.DeleteVertexArray(vertexArray);

            throw new NotSupportedException(
                $"Pipeline '{description.Name}' gives its colour targets different blend states, which "
                + $"{Profile} cannot express — GLES 3.0's glBlendFunc is global. Ask "
                + "GraphicsDeviceFeatures.HasIndependentBlend."
            );
        }

        lock (gate) {
            return new(pipelines.Add(pipeline));
        }
    }

    /// <inheritdoc />
    public PipelineHandle CreateComputePipeline(in ComputePipelineDescription description) {
        description.Validate();

        if (!Features.HasCompute) {
            throw new NotSupportedException(
                $"Compute pipeline '{description.Name}' was asked for on {Profile}, which has no compute "
                + "shaders. Ask GraphicsDeviceFeatures.HasCompute and take the fullscreen-fragment path "
                + "(docs/plan/06)."
            );
        }

        var layout = PipelineLayout(description.Layout);
        var compute = Shader(description.Compute);

        var (program, push) = programs.Get(
            new(description.Compute, ShaderHandle.Null, description.Layout),
            [(ShaderStage.Compute, compute.Source, compute.Name)],
            layout
        );

        lock (gate) {
            return new(pipelines.Add(new GlPipeline(program, description, layout) {
                PushConstantLocation = push
            }));
        }
    }

    /// <inheritdoc />
    public ISwapChain CreateSwapChain(in SwapChainDescription description) =>
        new GlSwapChain(this, description, present);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Never, and it is a profile decision rather than a gap.</b> <c>glQueryCounter</c> with
    ///     <c>GL_TIMESTAMP</c> is core on desktop GL from 3.3 and is absent from every GLES profile
    ///     and from WebGL2 — which is what this backend exists for. A timeline available on the one
    ///     configuration that also has Vulkan, and missing on the three that do not, is a feature
    ///     nobody could rely on; <see cref="GraphicsDeviceFeatures.HasTimestampQueries" /> says so
    ///     and the GPU profiler shows the reason rather than an empty chart.
    /// </remarks>
    public QueryPoolHandle CreateQueryPool(in QueryPoolDescription description) =>
        throw new NotSupportedException(
            $"Query pool '{description.Name}' was asked for on the OpenGL backend, which reports no "
            + "timestamp queries: GL_TIMESTAMP is desktop-only and this backend targets GLES and "
            + "WebGL2. Ask Features.HasTimestampQueries first."
        );

    /// <inheritdoc />
    public void Destroy(QueryPoolHandle handle) {
        // Nothing can have been created, so nothing can be destroyed. Silent rather than throwing,
        // because a Destroy that throws turns a clean-up path into a second failure.
    }

    /// <inheritdoc />
    public bool TryResolveQueries(QueryPoolHandle pool, int first, Span<ulong> results) =>
        throw new NotSupportedException(
            "The OpenGL backend has no timestamp queries, so no pool exists to resolve."
        );

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Never, and it is not a driver generation away.</b> GL has no acceleration
    ///     structures at any profile — hardware ray tracing arrived with the explicit APIs and was
    ///     never back-ported — so <see cref="GraphicsDeviceFeatures.HasRayTracing" /> reports false
    ///     here permanently. The distance-field tracer is the path on this backend.
    /// </remarks>
    public AccelerationStructureSizes GetAccelerationStructureSizes(in AccelerationStructureBuildInput input) =>
        throw new NotSupportedException(
            "Acceleration-structure sizes were asked for on the OpenGL backend, which has no ray "
            + "tracing — GL never grew acceleration structures. Ask Features.HasRayTracing and take "
            + "the distance-field tracer."
        );

    /// <inheritdoc />
    public AccelerationStructureHandle CreateAccelerationStructure(in AccelerationStructureDescription description) =>
        throw new NotSupportedException(
            $"Acceleration structure '{description.Name}' was asked for on the OpenGL backend, which "
            + "has no ray tracing. Ask Features.HasRayTracing and take the distance-field tracer."
        );

    /// <inheritdoc />
    public ulong GetAccelerationStructureAddress(AccelerationStructureHandle handle) =>
        throw new NotSupportedException(
            "The OpenGL backend has no ray tracing, so no acceleration structure exists to address."
        );

    /// <inheritdoc />
    public void Destroy(AccelerationStructureHandle handle) {
        // Nothing can have been created, so nothing can be destroyed. Silent rather than throwing,
        // because a Destroy that throws turns a clean-up path into a second failure.
    }

    // ── Destruction ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Destroy(BufferHandle handle) {
        lock (gate) {
            if (buffers.TryGet(handle.Value, out var buffer)) {
                gl.DeleteBuffer(((GlBuffer)buffer).Name);
                buffers.Remove(handle.Value);
            }
        }
    }

    /// <inheritdoc />
    public void Destroy(TextureHandle handle) {
        lock (gate) {
            if (textures.TryGet(handle.Value, out var texture)) {
                gl.DeleteTexture(((GlTexture)texture).Name);
                textures.Remove(handle.Value);
            }
        }
    }

    /// <inheritdoc />
    public void Destroy(TextureViewHandle handle) {
        framebuffers.Forget(handle);

        lock (gate) {
            views.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(SamplerHandle handle) {
        lock (gate) {
            if (samplers.TryGet(handle.Value, out var sampler)) {
                gl.DeleteSampler(((GlSampler)sampler).Name);
                samplers.Remove(handle.Value);
            }
        }
    }

    /// <inheritdoc />
    public void Destroy(ShaderHandle handle) {
        lock (gate) {
            shaders.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The program is not deleted here: it belongs to <see cref="GlProgramCache" /> and is very
    ///     likely shared with another pipeline that differs only in blend state. The vertex array is
    ///     this pipeline's alone and goes.
    /// </remarks>
    public void Destroy(PipelineHandle handle) {
        lock (gate) {
            if (pipelines.TryGet(handle.Value, out var pipeline) && pipeline is GlPipeline { VertexArray: > 0 } typed) {
                gl.DeleteVertexArray(typed.VertexArray);
            }

            pipelines.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(PipelineLayoutHandle handle) {
        lock (gate) {
            pipelineLayouts.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(DescriptorSetLayoutHandle handle) {
        lock (gate) {
            setLayouts.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(DescriptorSetHandle handle) {
        lock (gate) {
            descriptorSets.Remove(handle.Value);
        }
    }

    // ── Frames and submission ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public ICommandList BeginCommandList(QueueKind kind = QueueKind.Graphics, string name = "") {
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate) {
            if (pool.TryPop(out var recycled)) {
                recycled.Rearm();
                return recycled;
            }
        }

        return new GlCommandList(this, kind, name);
    }

    /// <inheritdoc />
    public void BeginFrame() {
        ObjectDisposedException.ThrowIf(disposed, this);

        IsFrameOpen = true;
        FrameCount++;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Kept even though nothing here nests badly</b> — GL has one implicit queue and no
    ///     per-slot command pools to reset. It is stored because the contract has no default: a
    ///     backend allowed to answer <see langword="false" /> for free is one that reports "no frame
    ///     is open" on the day somebody ports the offending caller to it. See #775.
    /// </remarks>
    public bool IsFrameOpen { get; private set; }

    /// <inheritdoc />
    public void EndFrame() {
        ObjectDisposedException.ThrowIf(disposed, this);
        IsFrameOpen = false;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>glFinish</c>, which is the only thing GL has. It is a full pipeline drain — heavier
    ///     than a Vulkan fence wait, which waits for one submission — and the RHI already says this
    ///     is a hammer for shutdown and resize rather than something a frame does.
    /// </remarks>
    public void WaitIdle() => gl.Finish();

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        gl.Finish();

        if (readbackFramebuffer != 0) {
            gl.DeleteFramebuffer(readbackFramebuffer);
            readbackFramebuffer = 0;
        }

        framebuffers.Dispose();
        programs.Dispose();

        lock (gate) {
            foreach (var (_, buffer) in buffers) {
                gl.DeleteBuffer(((GlBuffer)buffer).Name);
            }

            foreach (var (_, texture) in textures) {
                gl.DeleteTexture(((GlTexture)texture).Name);
            }

            foreach (var (_, sampler) in samplers) {
                gl.DeleteSampler(((GlSampler)sampler).Name);
            }

            foreach (var (_, pipeline) in pipelines) {
                if (pipeline is GlPipeline { VertexArray: > 0 } typed) {
                    gl.DeleteVertexArray(typed.VertexArray);
                }
            }

            buffers.Clear();
            textures.Clear();
            views.Clear();
            samplers.Clear();
            shaders.Clear();
            pipelines.Clear();
            pipelineLayouts.Clear();
            setLayouts.Clear();
            descriptorSets.Clear();
            pool.Clear();
        }
    }

    /// <summary>Puts a command list back in the pool.</summary>
    internal void Return(GlCommandList list) {
        lock (gate) {
            if (pool.Count < 32) {
                pool.Push(list);
            }
        }
    }

    /// <summary>Builds the vertex array holding a pipeline's attribute enables and divisors.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Only the enables and the divisors, not the formats. In the non-DSA path every
    ///         profile shares, <c>glVertexAttribPointer</c> captures whatever is bound to
    ///         <c>GL_ARRAY_BUFFER</c> at the moment it is called — so an attribute's format and its
    ///         buffer are one piece of state and cannot be set apart. The formats are therefore set
    ///         when a vertex buffer is bound, in <c>ApplyVertexBuffers</c>, and only for the
    ///         slot that changed.
    ///     </para>
    ///     <para>
    ///         GL 4.3's <c>glVertexAttribFormat</c> would separate them, and using it would mean two
    ///         vertex paths for one behaviour. The lazy attach costs one call per changed slot per
    ///         draw and is the same on all three profiles.
    ///     </para>
    /// </remarks>
    uint BuildVertexArray(VertexBufferLayout[] layouts) {
        var array = gl.GenVertexArray();
        state.BindVertexArray(array);

        foreach (var layout in layouts) {
            foreach (var attribute in layout.Attributes ?? []) {
                gl.EnableVertexAttribArray(attribute.Location);
                gl.VertexAttribDivisor(attribute.Location, layout.StepMode == VertexStepMode.Instance ? 1u : 0u);
            }
        }

        return array;
    }

    void Label(uint identifier, uint name, string label) {
        if (label.Length > 0 && Profile.HasDebugOutput()) {
            gl.ObjectLabel(identifier, name, label);
        }
    }

    internal GlBuffer Buffer(BufferHandle handle) {
        lock (gate) {
            return buffers.TryGet(handle.Value, out var buffer)
                ? (GlBuffer)buffer
                : throw new ArgumentException("The buffer does not exist, or has been destroyed.", nameof(handle));
        }
    }

    internal GlTexture Texture(TextureHandle handle) {
        lock (gate) {
            return textures.TryGet(handle.Value, out var texture)
                ? (GlTexture)texture
                : throw new ArgumentException("The texture does not exist, or has been destroyed.", nameof(handle));
        }
    }

    GlTextureView View(TextureViewHandle handle) {
        lock (gate) {
            return views.TryGet(handle.Value, out var view)
                ? (GlTextureView)view
                : throw new ArgumentException("The view does not exist, or has been destroyed.", nameof(handle));
        }
    }

    GlSampler Sampler(SamplerHandle handle) {
        lock (gate) {
            return samplers.TryGet(handle.Value, out var sampler)
                ? (GlSampler)sampler
                : throw new ArgumentException("The sampler does not exist, or has been destroyed.", nameof(handle));
        }
    }

    GlShader Shader(ShaderHandle handle) {
        lock (gate) {
            return shaders.TryGet(handle.Value, out var shader)
                ? (GlShader)shader
                : throw new ArgumentException("The shader does not exist, or has been destroyed.", nameof(handle));
        }
    }

    internal GlPipeline Pipeline(PipelineHandle handle) {
        lock (gate) {
            return pipelines.TryGet(handle.Value, out var pipeline)
                ? (GlPipeline)pipeline
                : throw new ArgumentException("The pipeline does not exist, or has been destroyed.", nameof(handle));
        }
    }

    GlPipelineLayout PipelineLayout(PipelineLayoutHandle handle) {
        lock (gate) {
            return pipelineLayouts.TryGet(handle.Value, out var layout)
                ? (GlPipelineLayout)layout
                : throw new ArgumentException("The layout does not exist, or has been destroyed.", nameof(handle));
        }
    }

    GlDescriptorSetLayout SetLayout(DescriptorSetLayoutHandle handle) {
        lock (gate) {
            return setLayouts.TryGet(handle.Value, out var layout)
                ? (GlDescriptorSetLayout)layout
                : throw new ArgumentException("The layout does not exist, or has been destroyed.", nameof(handle));
        }
    }

    GlDescriptorSet DescriptorSet(DescriptorSetHandle handle) {
        lock (gate) {
            return descriptorSets.TryGet(handle.Value, out var set)
                ? (GlDescriptorSet)set
                : throw new ArgumentException("The set does not exist, or has been destroyed.", nameof(handle));
        }
    }

    /// <summary>The one queue, wearing three names.</summary>
    sealed class GlSubmitter(GlDevice device) : ICommandSubmitter {
        public QueueKind Kind => QueueKind.Graphics;

        public void Submit(ReadOnlySpan<ICommandList> lists) {
            foreach (var list in lists) {
                if (!list.IsRecorded) {
                    throw new InvalidOperationException(
                        "A command list was submitted before Finish() was called on it."
                    );
                }

                if (list is not GlCommandList typed) {
                    throw new ArgumentException(
                        "A command list from another backend was submitted to the OpenGL device."
                    );
                }

                if (typed.Submitted) {
                    throw new InvalidOperationException(
                        "A command list was submitted twice. A list is a one-shot recording."
                    );
                }

                typed.MarkSubmitted();
                device.Replay(typed.Recorder);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        ///     <c>glFlush</c> rather than <c>glFinish</c>: this says "start the work", which is what
        ///     a queue wait means on an API where submission is implicit and ordering is total.
        ///     Waiting for it to <em>finish</em> is <see cref="WaitIdle" />.
        /// </remarks>
        public void WaitIdle() => device.gl.Flush();
    }
}
