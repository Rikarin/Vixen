// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Collections;

namespace Vixen.Graphics.WebGPU;

/// <summary>What to create a WebGPU device as.</summary>
public readonly record struct WebGpuDeviceOptions() {
    /// <summary>How many frames may be recorded before the first has to have finished.</summary>
    /// <remarks>
    ///     Two by default: one being recorded while one is on the GPU. Three trades a frame of
    ///     latency for a little more tolerance of a spiky frame time, which is a decision for the
    ///     renderer rather than for the RHI.
    /// </remarks>
    public int FramesInFlight { get; init; } = 2;

    /// <summary>Where the backend logs.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>How many push-constant writes one frame may make.</summary>
    /// <remarks>
    ///     WebGPU has no push constants, so each write takes an aligned slot of a ring buffer —
    ///     256 bytes on every implementation that has reported an alignment. A thousand slots is a
    ///     quarter of a megabyte per frame in flight, which is the right trade for a desktop and
    ///     worth turning down for a browser that draws less. Raising it is what the exhaustion
    ///     message tells you to do; see <see cref="PushConstantRing" />.
    /// </remarks>
    public int PushConstantSlotsPerFrame { get; init; } = 1024;
}

/// <summary>The WebGPU device: everything the RHI can do, done twice over.</summary>
/// <remarks>
///     <para>
///         <b>This class contains no WebGPU calls.</b> It talks to <see cref="IWebGpuBinding" />,
///         which is native Dawn or wgpu on a desktop and <c>navigator.gpu</c> in a browser — the two
///         surface implementations <c>docs/plan/05</c> asks for. Translation, validation, handle
///         lifetime and command replay are therefore written once and are identical on both, and
///         all of it is testable against a fake binding on a machine with neither.
///     </para>
///     <para>
///         Handles index tables this object owns. Nothing here is finalisable and nothing is
///         reference-counted by the collector: a GPU object's lifetime belongs to the renderer.
///     </para>
///     <para>
///         <b>Destruction is deferred, and WebGPU makes that easy rather than hard.</b> Releasing a
///         WebGPU object drops a reference; the implementation keeps the object alive until the work
///         that names it has finished, which is exactly the guarantee Vulkan does not give. The
///         frame-delayed retirement below is therefore belt and braces rather than the load-bearing
///         part — and it stays, because the RHI's contract is stated in frames and a backend that
///         quietly relied on someone else's reference counting would be one binding upgrade from
///         being wrong.
///     </para>
/// </remarks>
public sealed partial class WebGpuDevice : IGraphicsDevice {
    readonly IWebGpuBinding binding;
    readonly ILogger? logger;
    readonly int pushConstantSlotsPerFrame;

    readonly HandlePool<GpuBuffer> buffers = new(64);
    readonly HandlePool<GpuTexture> textures = new(64);
    readonly HandlePool<GpuTextureView> views = new(64);
    readonly HandlePool<GpuSampler> samplers = new(16);
    readonly HandlePool<GpuShader> shaders = new(32);
    readonly HandlePool<GpuDescriptorSetLayout> setLayouts = new(16);
    readonly HandlePool<GpuPipelineLayout> pipelineLayouts = new(16);
    readonly HandlePool<GpuDescriptorSet> descriptorSets = new(64);
    readonly HandlePool<GpuPipeline> pipelines = new(32);

    readonly Lock gate = new();
    readonly List<Action>[] retiring;

    PushConstantRing? pushConstants;
    bool disposed;

    /// <summary>Creates a device over a binding.</summary>
    /// <param name="binding">How WebGPU is reached.</param>
    /// <remarks>Two overloads rather than one with <c>= default</c>: a record struct's property
    /// initialisers do not run for <c>default</c>, so an omitted argument would have meant one frame
    /// in flight rather than the documented two.</remarks>
    public WebGpuDevice(IWebGpuBinding binding) : this(binding, new WebGpuDeviceOptions()) { }

    /// <summary>Creates a device over a binding.</summary>
    /// <param name="binding">How WebGPU is reached.</param>
    /// <param name="options">What to create it as.</param>
    public WebGpuDevice(IWebGpuBinding binding, WebGpuDeviceOptions options) {
        ArgumentNullException.ThrowIfNull(binding);

        this.binding = binding;
        logger = options.Logger;
        pushConstantSlotsPerFrame = Math.Max(1, options.PushConstantSlotsPerFrame);
        FramesInFlight = Math.Max(1, options.FramesInFlight);

        var info = binding.AdapterInfo;
        Features = WebGpuCapabilities.Describe(binding.Limits, info.Kind, binding.HasFeature);
        Adapter = new WebGpuAdapter(info, Features);

        GraphicsQueue = new WebGpuQueue(this, QueueKind.Graphics);

        // All three are the graphics queue, and both of the others report themselves as such. WebGPU
        // has exactly one queue: saying so here is what makes Features.HasAsyncCompute's `false`
        // consistent with what a caller finds when it asks for the compute submitter.
        ComputeQueue = GraphicsQueue;
        TransferQueue = GraphicsQueue;

        retiring = new List<Action>[FramesInFlight];

        for (var index = 0; index < FramesInFlight; index++) {
            retiring[index] = [];
        }

        // Guarded rather than left to the generated call site: one of these arguments allocates, and
        // the boot log is the one place where a disabled Information level is normal.
        if (options.Logger is { } log && log.IsEnabled(LogLevel.Information)) {
            var kind = info.Kind.ToString();
            var mode = binding.HasSurface ? "presenting" : "offscreen";
            WebGpuLog.DeviceCreated(log, info.Name, kind, info.DriverDescription, mode);
        }
    }

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
    ///     times, step <see cref="FramesInFlight" /> frames so the deferred destruction has run, and
    ///     this comes back to where it started.
    /// </remarks>
    public int LiveResourceCount {
        get {
            lock (gate) {
                return buffers.Count + textures.Count + views.Count + samplers.Count + shaders.Count
                    + pipelines.Count + pipelineLayouts.Count + setLayouts.Count + descriptorSets.Count;
            }
        }
    }

    /// <summary>Which frame slot is being recorded into.</summary>
    internal int FrameSlot => (int)(FrameCount % FramesInFlight);

    internal IWebGpuBinding Binding => binding;

    /// <inheritdoc />
    public ICommandList BeginCommandList(QueueKind kind = QueueKind.Graphics, string name = "") {
        ThrowIfDisposed();
        return new WebGpuCommandList(this, kind, name);
    }

    /// <inheritdoc />
    public void BeginFrame() {
        lock (gate) {
            ThrowIfDisposed();
            var slot = FrameSlot;

            // Only once this slot has been round at least once. Beginning frame N means frame
            // N - FramesInFlight has finished, so what it held is safe — but for the first
            // FramesInFlight frames there is no such frame, and draining the slot then would free
            // a resource destroyed moments ago in the frame currently being recorded. Vulkan gets
            // this from a fence it waits on; here it is arithmetic, and the arithmetic has to be
            // written down because there is nothing to block on.
            if (FrameCount >= FramesInFlight) {
                foreach (var action in retiring[slot]) {
                    action();
                }

                retiring[slot].Clear();
            }

            pushConstants?.BeginFrame(slot);
            binding.Tick();
        }
    }

    /// <inheritdoc />
    public void EndFrame() {
        lock (gate) {
            ThrowIfDisposed();
            FrameCount++;
        }
    }

    /// <inheritdoc />
    public void WaitIdle() {
        lock (gate) {
            if (disposed) {
                return;
            }

            // A browser cannot block its own event loop waiting for a queue, so the browser surface
            // reports that it did not wait rather than pretending. Anything that genuinely needs the
            // GPU to have finished — a readback — asks through ReadBuffer, which fails honestly there
            // for the same reason.
            if (!binding.WaitIdle() && logger is { } log) {
                WebGpuLog.CannotWaitIdle(log);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            binding.WaitIdle();
            disposed = true;

            foreach (var pending in retiring) {
                foreach (var action in pending) {
                    action();
                }

                pending.Clear();
            }

            ReleaseAll();
            pushConstants?.Dispose();
            pushConstants = null;
            binding.Dispose();
        }
    }

    /// <summary>Runs an action once the frame being recorded now cannot still be on the GPU.</summary>
    /// <param name="action">What to do.</param>
    internal void Retire(Action action) {
        if (disposed) {
            action();
            return;
        }

        retiring[FrameSlot].Add(action);
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, typeof(WebGpuDevice));

    /// <summary>Releases every object still in a table, in dependency order.</summary>
    /// <remarks>
    ///     Pipelines before layouts before the things layouts name. WebGPU is reference-counted and
    ///     would tolerate any order; this one is the order a reader would expect, and it is the order
    ///     that keeps a debug build's leak report readable when it comes.
    /// </remarks>
    void ReleaseAll() {
        foreach (var (_, item) in pipelines) {
            var pipeline = (WebGpuPipeline)item;

            Release(
                pipeline.IsCompute ? WebGpuObjectKind.ComputePipeline : WebGpuObjectKind.RenderPipeline,
                pipeline.Handle
            );
        }

        foreach (var (_, item) in descriptorSets) {
            Release(WebGpuObjectKind.BindGroup, ((WebGpuDescriptorSet)item).Handle);
        }

        foreach (var (_, item) in pipelineLayouts) {
            Release(WebGpuObjectKind.PipelineLayout, ((WebGpuPipelineLayout)item).Handle);
        }

        foreach (var (_, item) in setLayouts) {
            Release(WebGpuObjectKind.BindGroupLayout, ((WebGpuDescriptorSetLayout)item).Handle);
        }

        foreach (var (_, item) in shaders) {
            Release(WebGpuObjectKind.ShaderModule, ((WebGpuShader)item).Handle);
        }

        foreach (var (_, item) in samplers) {
            Release(WebGpuObjectKind.Sampler, ((WebGpuSampler)item).Handle);
        }

        foreach (var (_, item) in views) {
            var view = (WebGpuTextureView)item;

            if (view.Owned) {
                Release(WebGpuObjectKind.TextureView, view.Handle);
            }
        }

        foreach (var (_, item) in textures) {
            var texture = (WebGpuTexture)item;

            if (texture.Owned) {
                Release(WebGpuObjectKind.Texture, texture.Handle);
            }
        }

        foreach (var (_, item) in buffers) {
            Release(WebGpuObjectKind.Buffer, ((WebGpuBuffer)item).Handle);
        }

        pipelines.Clear();
        descriptorSets.Clear();
        pipelineLayouts.Clear();
        setLayouts.Clear();
        shaders.Clear();
        samplers.Clear();
        views.Clear();
        textures.Clear();
        buffers.Clear();
    }

    void Release(WebGpuObjectKind kind, WebGpuObject handle) {
        if (handle.IsValid) {
            binding.Release(kind, handle);
        }
    }
}
