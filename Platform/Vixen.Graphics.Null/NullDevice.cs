// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
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
/// <remarks>
///     <para>
///         ⚠ <b>The only multi-queue device the engine can test against.</b> Every real device the
///         tree runs on has one universal family, so this is where a schedule with two queues, a
///         cross-queue wait and an ownership handover is actually executed. It reports
///         <c>HasAsyncCompute</c> and <c>HasTimelineSemaphores</c> for that reason.
///     </para>
///     <para>
///         Its timeline is honest about the one thing a timeline is for and about nothing else. Work
///         completes on submission, so every point is reached the moment it exists and no wait ever
///         blocks — but a wait for a point this device never <em>issued</em> is refused, because
///         that is the mistake with no other detector: on hardware it is a device-side hang, with no
///         validation message and no stack.
///     </para>
/// </remarks>
sealed class NullSubmitter(QueueKind kind, CommandRecorder? recorder) : ICommandSubmitter {
    /// <summary>How to find the queue a point belongs to. Set by the device once all three exist.</summary>
    internal Func<QueueKind, NullSubmitter>? Resolve { get; set; }

    /// <summary>How many points this queue has handed out.</summary>
    internal ulong Issued { get; private set; }

    public QueueKind Kind => kind;

    public bool HasTimeline => true;

    public TimelinePoint Submit(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<TimelinePoint> waitFor) {
        foreach (var point in waitFor) {
            if (point.IsNone) {
                continue;
            }

            var producer = Resolve?.Invoke(point.Queue);

            if (producer is not null && point.Value > producer.Issued) {
                throw new InvalidOperationException(
                    $"A submission to the {kind} queue waited for value {point.Value} on the "
                    + $"{point.Queue} queue, which has only issued {producer.Issued}. Nothing will "
                    + "ever signal it, so on a real device this is a hang with no message at all. A "
                    + "TimelinePoint may only be one a submitter handed back."
                );
            }

            recorder?.Record(new(RecordedCommandKind.WaitForPoint, 0, (long)point.Queue, (long)point.Value));
        }

        Submit(lists);

        // Advanced even for an empty submission, so that the value a caller is handed always names
        // *this* call. Handing back the previous value would be a point that is reached before the
        // work the caller thinks it named.
        Issued++;

        recorder?.Record(
            new(RecordedCommandKind.Submit, 0, (long)kind, lists.Length, (long)Issued, waitFor.Length)
        );

        return new(kind, Issued);
    }

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

    public void WaitIdle() => recorder?.Record(new(RecordedCommandKind.QueueWaitIdle, 0, (long)kind));
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
    readonly HandlePool<GpuQueryPool> queryPools = new();
    readonly HandlePool<GpuAccelerationStructure> accelerationStructures = new();
    readonly Lock gate = new();
    readonly List<DescriptorWrite>? recordedWrites;

    bool disposed;

    /// <summary>Creates a device, reporting failure rather than throwing.</summary>
    /// <param name="options">What to build it out of.</param>
    /// <param name="device">The device. Always produced.</param>
    /// <param name="reason">Always <see langword="null" />.</param>
    /// <returns>Always <see langword="true" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Here for the shape, not because it can fail.</b> A backend selector walks a
    ///     preference list and calls every candidate identically; a Null device that had to be
    ///     constructed differently from the others would put a special case in the one code path
    ///     whose job is not having any. That it always succeeds is also why it is the sensible last
    ///     entry in any chain — see <c>GraphicsHost</c>.
    /// </remarks>
    public static bool TryCreate(
        NullDeviceOptions options,
        [NotNullWhen(true)] out NullDevice? device,
        [NotNullWhen(false)] out string? reason
    ) {
        device = new(options);
        reason = null;

        return true;
    }

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
        recordedWrites = options.Record ? [] : null;
        Adapter = new NullAdapter(Features);

        var graphics = new NullSubmitter(QueueKind.Graphics, Recorder);
        var compute = new NullSubmitter(QueueKind.Compute, Recorder);
        var transfer = new NullSubmitter(QueueKind.Transfer, Recorder);

        // Three distinct submitters, unlike every real device the tree runs on — so a point names a
        // counter of its own and "waited for a value nothing will signal" is a question with an
        // answer here. Wired after construction because each has to be able to find the other two.
        NullSubmitter Find(QueueKind wanted) => wanted switch {
            QueueKind.Compute => compute,
            QueueKind.Transfer => transfer,
            _ => graphics
        };

        graphics.Resolve = Find;
        compute.Resolve = Find;
        transfer.Resolve = Find;

        GraphicsQueue = graphics;
        ComputeQueue = compute;
        TransferQueue = transfer;
    }

    /// <summary>The recorded command stream, or <see langword="null" /> if recording is off.</summary>
    public CommandRecorder? Recorder { get; }

    /// <summary>
    ///     Every <see cref="DescriptorWrite" /> this device has been given, in order, or
    ///     <see langword="null" /> if recording is off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The count is not the interesting fact; the handles are.</b>
    ///         <see cref="DescriptorWrites" /> already answers "how many", which is what a
    ///         deduplication test asks. What it cannot answer is <em>what a write points at</em> — and
    ///         a descriptor that satisfies every check and names nothing is the failure this backend
    ///         exists to catch without a driver. A host that resolves a binding to a handle it never
    ///         created writes a well-formed descriptor over a dead slot, and the picture is a black
    ///         texture rather than an error.
    ///     </para>
    ///     <para>
    ///         Kept beside <see cref="Recorder" /> and off by the same switch, for the same reason: a
    ///         Null device is a shipping backend as well as a test one, and a server that accumulated
    ///         every descriptor it was ever handed would run out of memory some hours in.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<DescriptorWrite>? RecordedWrites => recordedWrites;

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
    /// <remarks>
    ///     The assertion a leak test wants: run a subsystem's create-and-destroy cycle a hundred
    ///     times and this comes back to where it started, or something is not returning what it
    ///     took.
    /// </remarks>
    public int LiveResourceCount {
        get {
            lock (gate) {
                return buffers.Count + textures.Count + views.Count + samplers.Count + shaders.Count
                    + pipelines.Count + pipelineLayouts.Count + setLayouts.Count + descriptorSets.Count
                    + queryPools.Count + accelerationStructures.Count;
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
        HasDrawIndirectFirstInstance = true,
        HasDrawIndirectCount = true,
        HasTimelineSemaphores = true,
        HasAsyncCompute = true,
        HasAsyncTransfer = true,
        HasSparseResources = true,
        HasRayTracing = true,
        HasFloat64 = true,
        HasInt64Atomics = true,
        HasSubgroupOperations = true,
        HasDynamicRendering = true,
        HasDepthClamp = true,
        HasWireframe = true,
        HasAnisotropicFiltering = true,
        HasIndependentBlend = true,
        HasPipelineStatistics = true,
        HasTimestampQueries = true,

        // One tick is one nanosecond, which is what a desktop NVIDIA part reports and is the value
        // that makes a synthetic reading readable: `Vixen.Editor.Profiler`'s tests can write a
        // duration in nanoseconds and read the same number back rather than through a scale factor
        // whose only purpose would be to prove the multiplication happened.
        TimestampPeriod = 1f,

        MaxTextureSize = 16384,
        MaxColourAttachments = 8,
        MaxVertexBuffers = 16,
        MaxAnisotropy = 16f,

        // A desktop driver's order of magnitude rather than a round number, because a table sized
        // against this is the thing a test is checking and a suspiciously tidy ceiling is one nobody
        // would notice being hit.
        MaxBindlessDescriptors = 500_000,

        // Five, because a table is a set of its own and a shader that indexes one binds five. A
        // device claiming bindless with four bindable sets is a combination no real device reports
        // and one this file should not be the first to invent — see DescriptorSetSlot.Bindless.
        MaxDescriptorSets = 8,

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
    ///         be one the set declares, the kind has to be the one it was declared as, and the element
    ///         has to be inside it. None of the three needs a GPU and all of them are undefined
    ///         behaviour on one — a release driver overwrites a neighbouring descriptor, and the
    ///         symptom is the wrong texture on an object that has nothing to do with the code that was
    ///         wrong.
    ///     </para>
    ///     <para>
    ///         <strong>The kind check is here rather than left to the Vulkan backend, which also makes
    ///         it, because that one only runs on a machine with a driver and the validation layers
    ///         switched on.</strong> Without them the write lands, the shader reads whichever kind it
    ///         was compiled for, and what comes back is a wrong frame rather than an error. The
    ///         dynamic kinds are compared exactly rather than folded into their static counterparts: a
    ///         <see cref="DescriptorKind.DynamicUniformBuffer" /> written as a
    ///         <see cref="DescriptorKind.UniformBuffer" /> is a descriptor that takes no offset at
    ///         bind time, so every per-draw offset the caller passes is ignored and every object draws
    ///         with the first one's block. That is what turning it on found.
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
                recordedWrites?.Add(write);
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

            if (declared.Kind != write.Kind) {
                throw new ArgumentException(
                    $"Binding {write.Binding} was declared as {declared.Kind} and is being written as "
                    + $"{write.Kind}. No driver checks this and the shader reads whichever it was "
                    + "compiled for, so the result would be silently wrong."
                );
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
    public QueryPoolHandle CreateQueryPool(in QueryPoolDescription description) {
        if (!Features.HasTimestampQueries) {
            throw new NotSupportedException(
                $"Query pool '{description.Name}' was asked for on a device reporting no timestamp "
                + "queries. Ask Features.HasTimestampQueries and leave the timeline empty."
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Count, nameof(description));

        lock (gate) {
            return new(queryPools.Add(new NullQueryPool(description)));
        }
    }

    /// <inheritdoc />
    public void Destroy(QueryPoolHandle handle) {
        lock (gate) {
            queryPools.Remove(handle.Value);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The numbers are arbitrary but stable</b>, the synthetic-timestamp stance: a backend
    ///     with no GPU has no builder to ask, and answering zero would size a buffer no test could
    ///     tell from a bug. So the structure is 256 + 64 bytes per primitive and the scratch 128 + 16
    ///     — plausible desktop-driver magnitudes, deterministic so an assertion can write the
    ///     arithmetic down, and nothing should read them out of this device and call them a
    ///     measurement.
    /// </remarks>
    public AccelerationStructureSizes GetAccelerationStructureSizes(in AccelerationStructureBuildInput input) {
        if (!Features.HasRayTracing) {
            throw new NotSupportedException(
                "Acceleration-structure sizes were asked for on a device that reports no ray tracing. "
                + "Ask Features.HasRayTracing and take the distance-field tracer."
            );
        }

        var primitives = PrimitiveCount(input);
        ArgumentOutOfRangeException.ThrowIfNegative(primitives, nameof(input));

        return new(256 + 64L * primitives, 128 + 16L * primitives);
    }

    /// <inheritdoc />
    public AccelerationStructureHandle CreateAccelerationStructure(in AccelerationStructureDescription description) {
        if (!Features.HasRayTracing) {
            throw new NotSupportedException(
                $"Acceleration structure '{description.Name}' was asked for on a device that reports no "
                + "ray tracing. Ask Features.HasRayTracing and take the distance-field tracer."
            );
        }

        // The size the device itself answered is never zero — see GetAccelerationStructureSizes —
        // so a zero here is a caller that invented the number, which is the mistake the description
        // documents as corruption on a real backend.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Size, nameof(description));

        lock (gate) {
            return new(accelerationStructures.Add(new NullAccelerationStructure(description)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Synthetic, nonzero, and stable for the handle's lifetime</b> — derived from the
    ///     handle's packed value under a recognisable high byte, so two structures never share an
    ///     address, an address survives being written into an instance buffer and compared later,
    ///     and a number leaking into a real API call is identifiable at a glance in a debugger.
    /// </remarks>
    public ulong GetAccelerationStructureAddress(AccelerationStructureHandle handle) {
        if (!Features.HasRayTracing) {
            throw new NotSupportedException(
                "An acceleration-structure address was asked for on a device that reports no ray "
                + "tracing. Ask Features.HasRayTracing and take the distance-field tracer."
            );
        }

        lock (gate) {
            if (!accelerationStructures.Contains(handle.Value)) {
                throw new ArgumentException(
                    "The acceleration structure does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }
        }

        return SyntheticAddressBase | handle.Value.Packed;
    }

    /// <inheritdoc />
    public void Destroy(AccelerationStructureHandle handle) {
        lock (gate) {
            accelerationStructures.Remove(handle.Value);
        }
    }

    /// <summary>How many primitives one build input describes — triangles for a bottom level,
    ///     instances for a top.</summary>
    static int PrimitiveCount(in AccelerationStructureBuildInput input) =>
        input.Kind == AccelerationStructureKind.TopLevel
            ? input.Instances.Count
            : input.Triangles.IndexCount / 3;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The readings are synthetic and monotonic, and they are not zero.</b> A backend with
    ///     no GPU has no clock to read, and the tempting answer — resolve to zero — makes every
    ///     duration zero, which is a result a test cannot tell from a bug. Instead each query
    ///     answers with the pool's own counter, so a pair subtracts to a positive number and a
    ///     caller's arithmetic is exercised. It is a shape, not a measurement, and nothing should
    ///     read a number out of this device and call it a frame time.
    /// </remarks>
    public bool TryResolveQueries(QueryPoolHandle pool, int first, Span<ulong> results) {
        ArgumentOutOfRangeException.ThrowIfNegative(first);

        lock (gate) {
            if (!queryPools.TryGet(pool.Value, out var target)) {
                throw new ArgumentException("The query pool does not exist, or has been destroyed.", nameof(pool));
            }

            var description = ((NullQueryPool)target).Description;

            if (first + results.Length > description.Count) {
                throw new ArgumentOutOfRangeException(
                    nameof(first),
                    $"Reading {results.Length} queries from {first} runs off the end of '{description.Name}', "
                    + $"which holds {description.Count}."
                );
            }

            for (var index = 0; index < results.Length; index++) {
                results[index] = (ulong)(first + index + 1) * NullQueryPool.SyntheticTick;
            }

            return true;
        }
    }

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
        return new NullCommandList(kind, name, Features.HasDrawIndirectCount, Features.HasRayTracing);
    }

    /// <inheritdoc />
    public void BeginFrame() {
        ObjectDisposedException.ThrowIf(disposed, this);

        IsFrameOpen = true;
        FrameCount++;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Tracked properly rather than answered <see langword="false" />, and this is the device
    ///     where that matters most.</b> Nothing nests badly here — which is exactly why every test of
    ///     a caller's frame discipline runs on this device, and a stub that always said "no frame is
    ///     open" would make all of them pass whatever the caller did. See #775.
    /// </remarks>
    public bool IsFrameOpen { get; private set; }

    /// <inheritdoc />
    public void EndFrame() {
        ObjectDisposedException.ThrowIf(disposed, this);
        IsFrameOpen = false;
    }

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
            queryPools.Clear();
            accelerationStructures.Clear();
        }
    }

    internal (TextureHandle Texture, TextureViewHandle View) CreateBackBuffer(PixelFormat format, Int2 size) {
        var texture = CreateTexture(
            new(format, Math.Max(1, size.X), Math.Max(1, size.Y), TextureUsage.ColourTarget, Name: "SwapChain")
        );

        return (texture, CreateTextureView(texture));
    }

    /// <summary>The high bits of every synthetic acceleration-structure address.</summary>
    /// <remarks>
    ///     Arbitrary but recognisable — a value that turns up where a real GPU address was expected
    ///     names its origin — and high enough that OR-ing a packed handle underneath it never
    ///     collides two structures or produces zero.
    /// </remarks>
    const ulong SyntheticAddressBase = 0xACCE_0000_0000_0000;

    sealed class NullBuffer(BufferDescription description) : GpuBuffer {
        public BufferDescription Description { get; } = description;
    }

    sealed class NullAccelerationStructure(AccelerationStructureDescription description) : GpuAccelerationStructure {
        public AccelerationStructureDescription Description { get; } = description;
    }

    sealed class NullQueryPool(QueryPoolDescription description) : GpuQueryPool {
        /// <summary>How far apart two consecutive synthetic readings are, in ticks.</summary>
        /// <remarks>
        ///     A thousand, so a pair of adjacent queries reads as a microsecond at the device's
        ///     one-nanosecond period — a plausible pass, and a number nobody would mistake for a
        ///     measurement.
        /// </remarks>
        public const ulong SyntheticTick = 1000;

        public QueryPoolDescription Description { get; } = description;
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
        ///     A backend with no GPU has no reason to remember this except the one that matters: a
        ///     write of the wrong kind, or an element written past the end of an array binding, is
        ///     undefined on a real device and caught here without one — which is what this backend is
        ///     for.
        /// </remarks>
        public DescriptorBinding[] Bindings { get; } = bindings;
    }

    sealed class NullDescriptorSet(DescriptorSetLayoutHandle layout) : GpuDescriptorSet {
        public DescriptorSetLayoutHandle Layout { get; } = layout;
    }
}
