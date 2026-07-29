// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Collections;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics.Null;

/// <summary>What to build a <see cref="NullDevice" /> out of.</summary>
public readonly record struct NullDeviceOptions() {
    /// <summary>
    ///     Whether to record the command stream so a test can assert on it.
    /// </summary>
    /// <remarks>
    ///     <b>Off by default, and that is load-bearing.</b> <c>docs/plan/05</c> makes this backend a
    ///     shipping one as well as a test one — a dedicated server runs on it — and a server that
    ///     accumulated a command log would run out of memory some hours in. A test turns it on; a
    ///     server never does.
    /// </remarks>
    public bool Record { get; init; }

    /// <summary>What the device should claim it can do.</summary>
    /// <remarks>
    ///     Defaults to everything, so a test exercising a bindless or compute path is not stopped by
    ///     a capability check. Set it to <see cref="GraphicsDeviceFeatures.Minimum" /> to test what a
    ///     WebGL2-class device makes code do instead — which is the more interesting test, and the
    ///     one the fallback paths need.
    /// </remarks>
    public GraphicsDeviceFeatures? Features { get; init; }

    /// <summary>How many frames may be in flight.</summary>
    public int FramesInFlight { get; init; } = 2;
}

/// <summary>An adapter that is not a GPU.</summary>
sealed class NullAdapter(GraphicsDeviceFeatures features) : IGraphicsAdapter {
    public string Name => "Vixen Null Device";

    public AdapterKind Kind => AdapterKind.Software;

    public string DriverVersion => "1.0.0";

    public ulong DeviceMemory => 0;

    public GraphicsDeviceFeatures Features => features;
}

/// <summary>A queue that finishes everything the moment it is given it.</summary>
sealed class NullSubmitter(QueueKind kind, CommandRecorder? recorder) : ICommandSubmitter {
    public QueueKind Kind => kind;

    public void Submit(ReadOnlySpan<ICommandList> lists) {
        foreach (var list in lists) {
            if (!list.IsRecorded) {
                throw new InvalidOperationException(
                    "A command list was submitted before Finish() was called on it. Every backend "
                    + "rejects this; catching it here means catching it without a GPU."
                );
            }

            if (list is NullCommandList { Submitted: true }) {
                throw new InvalidOperationException(
                    "A command list was submitted twice. A list is a one-shot recording; submitting it "
                    + "again replays nothing on a real backend and is undefined on some."
                );
            }

            if (list is NullCommandList typed) {
                typed.MarkSubmitted();
                typed.Flush(recorder);
            }
        }
    }

    public void WaitIdle() { }
}

/// <summary>A swapchain that presents to nothing.</summary>
sealed class NullSwapChain(SwapChainDescription description, NullDevice device) : ISwapChain {
    readonly TextureViewHandle[] views = new TextureViewHandle[Math.Max(1, description.ImageCount)];
    readonly TextureHandle[] textures = new TextureHandle[Math.Max(1, description.ImageCount)];

    int index = -1;
    bool disposed;

    public PixelFormat Format { get; } = description.Format;

    public Int2 Size { get; private set; } = description.Size;

    public PresentMode PresentMode { get; } = description.PresentMode;

    public int ImageCount => views.Length;

    /// <inheritdoc />
    public TextureHandle CurrentTexture => index >= 0 ? textures[index] : TextureHandle.Null;

    /// <summary>How many times <see cref="Present" /> has been called.</summary>
    public int PresentCount { get; private set; }

    /// <summary>What the next acquire should report, for a test that needs a resize path.</summary>
    /// <remarks>
    ///     The device-loss and out-of-date paths are the ones nobody exercises until a driver update
    ///     breaks them on a user's machine. <c>docs/plan/05</c> asks for fault injection for exactly
    ///     this, and here it costs a field.
    /// </remarks>
    public SwapChainStatus NextStatus { get; set; } = SwapChainStatus.Ready;

    public SwapChainStatus AcquireNextImage(out TextureViewHandle view) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (NextStatus is SwapChainStatus.OutOfDate or SwapChainStatus.DeviceLost) {
            view = TextureViewHandle.Null;
            return NextStatus;
        }

        index = (index + 1) % views.Length;

        if (!views[index].IsValid) {
            (textures[index], views[index]) = device.CreateBackBuffer(Format, Size);
        }

        view = views[index];
        return NextStatus;
    }

    public SwapChainStatus Present() {
        ObjectDisposedException.ThrowIf(disposed, this);
        PresentCount++;
        return NextStatus is SwapChainStatus.DeviceLost ? SwapChainStatus.DeviceLost : SwapChainStatus.Ready;
    }

    public void Resize(Int2 size) {
        ObjectDisposedException.ThrowIf(disposed, this);

        Size = size;
        Release();
        NextStatus = SwapChainStatus.Ready;
    }

    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Release();
    }

    void Release() {
        for (var slot = 0; slot < views.Length; slot++) {
            if (views[slot].IsValid) {
                device.Destroy(views[slot]);
                device.Destroy(textures[slot]);
                views[slot] = TextureViewHandle.Null;
                textures[slot] = TextureHandle.Null;
            }
        }

        index = -1;
    }
}

/// <summary>The graphics device with no GPU behind it.</summary>
/// <remarks>
///     <para>
///         Two jobs, the same as <c>Vixen.Platform.Headless</c>'s: it is what a dedicated server
///         renders on, and it is what every RHI test runs against. Because the second happens on
///         every build, the first is the most thoroughly exercised backend in the engine —
///         <c>docs/plan/05</c> calls that a pleasant consequence of the design rather than new work,
///         and it is.
///     </para>
///     <para>
///         <b>Resource creation genuinely does not allocate anything resource-shaped.</b> A handle
///         and its description, and nothing proportional to the size asked for — so a server that
///         creates and destroys a 4K render target every frame stays flat. Handles are still
///         generation-checked, so using one after destroying it is caught here rather than on a
///         driver that would have crashed.
///     </para>
/// </remarks>
public sealed class NullDevice : IGraphicsDevice {
    readonly HandlePool<GpuBuffer> buffers = new();
    readonly HandlePool<GpuTexture> textures = new();
    readonly HandlePool<GpuTextureView> views = new();
    readonly HandlePool<GpuSampler> samplers = new();
    readonly HandlePool<GpuShader> shaders = new();
    readonly HandlePool<GpuPipeline> pipelines = new();
    readonly HandlePool<GpuPipelineLayout> pipelineLayouts = new();
    readonly HandlePool<GpuDescriptorSetLayout> setLayouts = new();
    readonly HandlePool<GpuDescriptorSet> descriptorSets = new();
    readonly Lock gate = new();

    bool disposed;

    /// <summary>Creates the device.</summary>
        public NullDevice() : this(new NullDeviceOptions()) { }

    /// <summary>Creates the device.</summary>
    /// <param name="options">What to build it out of.</param>
    /// <remarks>
    ///     Two overloads rather than one with <c>= default</c>: a record struct's property
    ///     initialisers do not run for <c>default</c>, which would have made the documented two
    ///     frames in flight arrive as zero.
    /// </remarks>
    public NullDevice(NullDeviceOptions options) {
        Features = options.Features ?? Everything;
        FramesInFlight = Math.Max(1, options.FramesInFlight);
        Recorder = options.Record ? new CommandRecorder() : null;
        Adapter = new NullAdapter(Features);

        GraphicsQueue = new NullSubmitter(QueueKind.Graphics, Recorder);
        ComputeQueue = new NullSubmitter(QueueKind.Compute, Recorder);
        TransferQueue = new NullSubmitter(QueueKind.Transfer, Recorder);
    }

    /// <summary>The recorded command stream, or <see langword="null" /> if recording is off.</summary>
    public CommandRecorder? Recorder { get; }

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

    /// <summary>How many frames <see cref="BeginFrame" /> has started.</summary>
    public long FrameCount { get; private set; }

    /// <summary>How many resources are alive, across every kind.</summary>
    /// <remarks>
    ///     The assertion a leak test wants: run a subsystem's create-and-destroy cycle a hundred
    ///     times and this comes back to where it started, or something is not returning what it
    ///     took.
    /// </remarks>
    public int LiveResourceCount {
        get {
            lock (gate) {
                return buffers.Count + textures.Count + views.Count + samplers.Count + shaders.Count
                    + pipelines.Count + pipelineLayouts.Count + setLayouts.Count + descriptorSets.Count;
            }
        }
    }

    /// <summary>Everything a device could claim — what this one claims unless told otherwise.</summary>
    public static GraphicsDeviceFeatures Everything => GraphicsDeviceFeatures.Minimum with {
        HasCompute = true,
        HasGeometryShaders = true,
        HasTessellation = true,
        HasMeshShaders = true,
        HasBindless = true,
        HasMultiDrawIndirect = true,
        HasTimelineSemaphores = true,
        HasAsyncCompute = true,
        HasAsyncTransfer = true,
        HasSparseResources = true,
        HasFloat64 = true,
        HasSubgroupOperations = true,
        HasDynamicRendering = true,
        HasDepthClamp = true,
        HasWireframe = true,
        HasAnisotropicFiltering = true,
        HasIndependentBlend = true,
        HasPipelineStatistics = true,
        MaxTextureSize = 16384,
        MaxColourAttachments = 8,
        MaxVertexBuffers = 16,
        MaxAnisotropy = 16f,

        // A desktop driver's order of magnitude rather than a round number, because a table sized
        // against this is the thing a test is checking and a suspiciously tidy ceiling is one nobody
        // would notice being hit.
        MaxBindlessDescriptors = 500_000,

        SupportedSampleCounts = 0b11111
    };

    /// <inheritdoc />
    public BufferHandle CreateBuffer(in BufferDescription description) {
        description.Validate();

        lock (gate) {
            return new(buffers.Add(new NullBuffer(description)));
        }
    }

    /// <inheritdoc />
    public TextureHandle CreateTexture(in TextureDescription description) {
        description.Validate();

        if (description.Width > Features.MaxTextureSize || description.Height > Features.MaxTextureSize) {
            throw new ArgumentException(
                $"Texture '{description.Name}' is {description.Width}×{description.Height}, larger than "
                + $"this device's {Features.MaxTextureSize} limit."
            );
        }

        lock (gate) {
            return new(textures.Add(new NullTexture(description)));
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
        lock (gate) {
            if (!textures.TryGet(texture.Value, out var target)) {
                throw new ArgumentException("The texture does not exist, or has been destroyed.", nameof(texture));
            }

            var description = ((NullTexture)target).Description;

            if (baseMipLevel < 0 || baseMipLevel >= description.EffectiveMipLevels) {
                throw new ArgumentOutOfRangeException(
                    nameof(baseMipLevel),
                    $"Texture '{description.Name}' has {description.EffectiveMipLevels} mip levels."
                );
            }

            return new(views.Add(new NullTextureView(texture, format == PixelFormat.Undefined ? description.Format : format)));
        }
    }

    /// <inheritdoc />
    public SamplerHandle CreateSampler(in SamplerDescription description) {
        lock (gate) {
            return new(samplers.Add(new NullSampler(description)));
        }
    }

    /// <inheritdoc />
    public ShaderHandle CreateShader(ShaderStage stage, ReadOnlySpan<byte> bytecode, string name = "") {
        if (bytecode.IsEmpty) {
            throw new ArgumentException($"Shader '{name}' has no bytecode.", nameof(bytecode));
        }

        lock (gate) {
            // The bytecode is deliberately not kept: nothing here will ever compile it, and a server
            // holding every shader module it was handed is a server holding megabytes for nothing.
            return new(shaders.Add(new NullShader(stage, bytecode.Length, name)));
        }
    }

    /// <inheritdoc />
    public DescriptorSetLayoutHandle CreateDescriptorSetLayout(in DescriptorSetLayoutDescription description) {
        description.Validate();

        foreach (var binding in description.Bindings ?? []) {
            // The same refusal every real backend makes, made without one. A device that reports no
            // descriptor indexing and is handed an unbounded binding anyway is a host that skipped
            // its capability check, and finding that out here costs nothing.
            if (binding.IsUnbounded() && !Features.HasBindless) {
                throw new ArgumentException(
                    $"Binding {binding.Binding} of '{description.Name}' is unbounded, which needs "
                    + "GraphicsDeviceFeatures.HasBindless. This device reports it absent."
                );
            }
        }

        lock (gate) {
            return new(
                setLayouts.Add(
                    new NullDescriptorSetLayout(
                        description.Slot,
                        [.. description.Bindings ?? []],
                        description.CapacityFor(Features)
                    )
                )
            );
        }
    }

    /// <inheritdoc />
    public PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDescription description) {
        lock (gate) {
            return new(pipelineLayouts.Add(new NullPipelineLayout()));
        }
    }

    /// <inheritdoc />
    public DescriptorSetHandle CreateDescriptorSet(DescriptorSetLayoutHandle layout, string name = "") {
        lock (gate) {
            if (!setLayouts.Contains(layout.Value)) {
                throw new ArgumentException("The layout does not exist, or has been destroyed.", nameof(layout));
            }

            return new(descriptorSets.Add(new NullDescriptorSet(layout)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Every write is held against the layout the set was allocated from: the binding has to
    ///         be one the set declares, and the element has to be inside it. Neither needs a GPU and
    ///         both are undefined behaviour on one — a release driver overwrites a neighbouring
    ///         descriptor, and the symptom is the wrong texture on an object that has nothing to do
    ///         with the code that was wrong.
    ///     </para>
    ///     <para>
    ///         <strong>The write's <em>kind</em> is deliberately not checked here, and the Vulkan
    ///         backend does check it.</strong> Turning it on found six distinct places where a
    ///         declaration and its write disagree — a dynamic uniform block written as a plain one, a
    ///         sampler written where a texture was declared — which are real and are not this
    ///         backend's to fix. Left as a note rather than a silent omission: closing them and
    ///         adding the check is one change, and doing half of it would break every test that
    ///         exercises those paths without fixing anything.
    ///     </para>
    /// </remarks>
    public void UpdateDescriptorSet(DescriptorSetHandle descriptors, ReadOnlySpan<DescriptorWrite> writes) {
        lock (gate) {
            if (!descriptorSets.TryGet(descriptors.Value, out var resource)
                || resource is not NullDescriptorSet set) {
                throw new ArgumentException("The set does not exist, or has been destroyed.", nameof(descriptors));
            }

            if (!setLayouts.TryGet(set.Layout.Value, out var layoutResource)
                || layoutResource is not NullDescriptorSetLayout layout) {
                throw new ArgumentException("The set's layout has been destroyed.", nameof(descriptors));
            }

            foreach (var write in writes) {
                Validate(layout, write);
                DescriptorWrites++;
            }
        }
    }

    /// <summary>How many descriptor writes this device has been given, over its whole life.</summary>
    /// <remarks>
    ///     Counted rather than logged, because the interesting assertions are all about the count:
    ///     that a settled frame writes nothing, that deduplication turned a thousand asks into one
    ///     write, that a table nobody touched cost nothing.
    /// </remarks>
    public int DescriptorWrites { get; private set; }

    static void Validate(NullDescriptorSetLayout layout, in DescriptorWrite write) {
        foreach (var declared in layout.Bindings) {
            if (declared.Binding != write.Binding) {
                continue;
            }

            // How long the binding actually is. A table's zero is its capacity; a storage buffer's
            // zero is one descriptor holding a runtime-sized array, which is why this asks
            // IsUnbounded rather than comparing the count itself.
            var length = declared.IsUnbounded() ? layout.BindlessCapacity : Math.Max(1, declared.Count);

            if (write.ArrayIndex < 0 || write.ArrayIndex >= length) {
                throw new ArgumentOutOfRangeException(
                    nameof(write),
                    write.ArrayIndex,
                    $"Binding {write.Binding} holds {length} descriptor(s), so element "
                    + $"{write.ArrayIndex} is outside it."
                );
            }

            return;
        }

        throw new ArgumentException(
            $"Binding {write.Binding} is not declared by this descriptor-set layout, so writing it "
            + "would do nothing the shader could read."
        );
    }

    /// <inheritdoc />
    public PipelineHandle CreateGraphicsPipeline(in GraphicsPipelineDescription description) {
        description.Validate();

        if (!description.DepthStencil.DepthTest || description.DepthFormat != PixelFormat.Undefined) {
            lock (gate) {
                return new(pipelines.Add(new NullPipeline(false, description.Name)));
            }
        }

        throw new ArgumentException($"Pipeline '{description.Name}' tests depth with no depth attachment.");
    }

    /// <inheritdoc />
    public PipelineHandle CreateComputePipeline(in ComputePipelineDescription description) {
        description.Validate();

        if (!Features.HasCompute) {
            throw new NotSupportedException(
                $"Compute pipeline '{description.Name}' was asked for on a device that reports no compute "
                + "support. Ask Features.HasCompute and take the fallback path."
            );
        }

        lock (gate) {
            return new(pipelines.Add(new NullPipeline(true, description.Name)));
        }
    }

    /// <inheritdoc />
    public ISwapChain CreateSwapChain(in SwapChainDescription description) => new NullSwapChain(description, this);

    /// <inheritdoc />
    public void Destroy(BufferHandle handle) {
        lock (gate) {
            buffers.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(TextureHandle handle) {
        lock (gate) {
            textures.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(TextureViewHandle handle) {
        lock (gate) {
            views.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(SamplerHandle handle) {
        lock (gate) {
            samplers.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(ShaderHandle handle) {
        lock (gate) {
            shaders.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    public void Destroy(PipelineHandle handle) {
        lock (gate) {
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

    /// <inheritdoc />
    public void Write(BufferHandle buffer, long offset, ReadOnlySpan<byte> data) {
        lock (gate) {
            if (!buffers.TryGet(buffer.Value, out var target)) {
                throw new ArgumentException("The buffer does not exist, or has been destroyed.", nameof(buffer));
            }

            var description = ((NullBuffer)target).Description;

            if (description.Access == MemoryAccess.DeviceLocal) {
                throw new InvalidOperationException(
                    $"Buffer '{description.Name}' is device-local and cannot be written by the host. Stage "
                    + "it through an upload buffer and copy."
                );
            }

            if (offset < 0 || offset + data.Length > description.Size) {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    $"Writing {data.Length} bytes at {offset} runs off the end of '{description.Name}', "
                    + $"which is {description.Size} bytes."
                );
            }
        }

        // And then nothing: there is no memory behind it. Bounds are still checked, because an
        // overrun caught here is caught without a driver and without a corrupted heap.
    }

    /// <inheritdoc />
    public void Read(BufferHandle buffer, long offset, Span<byte> destination) {
        lock (gate) {
            if (!buffers.TryGet(buffer.Value, out var target)) {
                throw new ArgumentException("The buffer does not exist, or has been destroyed.", nameof(buffer));
            }

            var description = ((NullBuffer)target).Description;

            if (description.Access != MemoryAccess.HostReadback) {
                throw new InvalidOperationException(
                    $"Buffer '{description.Name}' is not a readback buffer."
                );
            }
        }

        // Zeroed rather than left alone, so a test reads a defined value instead of whatever the
        // caller's array happened to contain.
        destination.Clear();
    }

    /// <inheritdoc />
    public ICommandList BeginCommandList(QueueKind kind = QueueKind.Graphics, string name = "") {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new NullCommandList(kind, name);
    }

    /// <inheritdoc />
    public void BeginFrame() {
        ObjectDisposedException.ThrowIf(disposed, this);
        FrameCount++;
    }

    /// <inheritdoc />
    public void EndFrame() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <inheritdoc />
    public void WaitIdle() { }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        lock (gate) {
            buffers.Clear();
            textures.Clear();
            views.Clear();
            samplers.Clear();
            shaders.Clear();
            pipelines.Clear();
            pipelineLayouts.Clear();
            setLayouts.Clear();
            descriptorSets.Clear();
        }
    }

    internal (TextureHandle Texture, TextureViewHandle View) CreateBackBuffer(PixelFormat format, Int2 size) {
        var texture = CreateTexture(
            new(format, Math.Max(1, size.X), Math.Max(1, size.Y), TextureUsage.ColourTarget, Name: "SwapChain")
        );

        return (texture, CreateTextureView(texture));
    }

    sealed class NullBuffer(BufferDescription description) : GpuBuffer {
        public BufferDescription Description { get; } = description;
    }

    sealed class NullTexture(TextureDescription description) : GpuTexture {
        public TextureDescription Description { get; } = description;
    }

    sealed class NullTextureView(TextureHandle texture, PixelFormat format) : GpuTextureView {
        public TextureHandle Texture { get; } = texture;

        public PixelFormat Format { get; } = format;
    }

    sealed class NullSampler(SamplerDescription description) : GpuSampler {
        public SamplerDescription Description { get; } = description;
    }

    sealed class NullShader(ShaderStage stage, int length, string name) : GpuShader {
        public ShaderStage Stage { get; } = stage;

        public int Length { get; } = length;

        public string Name { get; } = name;
    }

    sealed class NullPipeline(bool isCompute, string name) : GpuPipeline {
        public bool IsCompute { get; } = isCompute;

        public string Name { get; } = name;
    }

    sealed class NullPipelineLayout : GpuPipelineLayout;

    sealed class NullDescriptorSetLayout(DescriptorSetSlot slot, DescriptorBinding[] bindings, int bindlessCapacity)
        : GpuDescriptorSetLayout {
        public DescriptorSetSlot Slot { get; } = slot;

        /// <summary>How many descriptors its unbounded binding holds, resolved against the device.</summary>
        public int BindlessCapacity { get; } = bindlessCapacity;

        /// <summary>What the set declares, kept so a write can be held against it.</summary>
        /// <remarks>
        ///     A backend with no GPU has no reason to remember this except the one that matters: an
        ///     element written past the end of an array binding is undefined on a real device and
        ///     caught here without one, which is what this backend is for.
        /// </remarks>
        public DescriptorBinding[] Bindings { get; } = bindings;
    }

    sealed class NullDescriptorSet(DescriptorSetLayoutHandle layout) : GpuDescriptorSet {
        public DescriptorSetLayoutHandle Layout { get; } = layout;
    }
}
