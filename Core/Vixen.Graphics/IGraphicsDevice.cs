// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics;

/// <summary>What kind of GPU an adapter is.</summary>
public enum AdapterKind : byte {
    /// <summary>The driver did not say.</summary>
    Unknown = 0,

    /// <summary>A separate card with its own memory.</summary>
    Discrete = 1,

    /// <summary>Part of the CPU package, sharing system memory.</summary>
    Integrated = 2,

    /// <summary>A software implementation — lavapipe, WARP, softpipe.</summary>
    /// <remarks>
    ///     Not a curiosity: lavapipe is a conformant Vulkan 1.3 driver with no GPU, and it is what
    ///     makes the full backend, the validation layers and the golden-image suite testable on a
    ///     standard CI runner (<c>docs/plan/10 § Linux</c>). Adapter selection must not skip it.
    /// </remarks>
    Software = 3
}

/// <summary>A physical device the process could render on.</summary>
public interface IGraphicsAdapter {
    /// <summary>The driver's name for it.</summary>
    string Name { get; }

    /// <summary>What kind of device it is.</summary>
    AdapterKind Kind { get; }

    /// <summary>The driver's version string, for logs and bug reports.</summary>
    string DriverVersion { get; }

    /// <summary>How much device-local memory it has, in bytes, or <c>0</c> if it does not say.</summary>
    ulong DeviceMemory { get; }

    /// <summary>What a device created on it would be able to do.</summary>
    GraphicsDeviceFeatures Features { get; }
}

/// <summary>A queue work is submitted to.</summary>
/// <remarks>
///     Named a submitter rather than a queue because the analyzers reserve the <c>Queue</c> suffix
///     for collection types and this is not one — it takes work, it does not hold it.
/// </remarks>
public interface ICommandSubmitter {
    /// <summary>What kind of work it takes.</summary>
    QueueKind Kind { get; }

    /// <summary>Submits recorded command lists, in order.</summary>
    /// <param name="lists">The lists. Each must have been ended.</param>
    /// <remarks>
    ///     A batch rather than one at a time: a submission is expensive on every API, and submitting
    ///     four lists together costs roughly what submitting one does.
    /// </remarks>
    void Submit(ReadOnlySpan<ICommandList> lists);

    /// <summary>Blocks until everything submitted to this queue has finished.</summary>
    /// <remarks>
    ///     A hammer. Correct at shutdown and while recreating a swapchain, and wrong in a frame — a
    ///     renderer that waits per frame has no pipelining and runs at the speed of the slower of
    ///     the CPU and GPU rather than at the speed of both.
    /// </remarks>
    void WaitIdle();
}

/// <summary>The images a window is presented from.</summary>
public interface ISwapChain : IDisposable {
    /// <summary>The format of its images.</summary>
    PixelFormat Format { get; }

    /// <summary>Their size in pixels.</summary>
    Int2 Size { get; }

    /// <summary>How it waits for the display.</summary>
    PresentMode PresentMode { get; }

    /// <summary>How many images it cycles through.</summary>
    int ImageCount { get; }

    /// <summary>The texture behind the image currently acquired.</summary>
    /// <remarks>
    ///     <para>
    ///         A view is what a render pass attaches, and it is what <see cref="AcquireNextImage" />
    ///         returns — but a <em>barrier</em> names a texture, and anything that tracks resource
    ///         state automatically therefore needs both. <c>Vixen.Graphics.RenderGraph</c> is the
    ///         first caller and will not be the last: every backend has this to hand, and the
    ///         alternative is each of them exposing it through a cast to its own type.
    ///     </para>
    ///     <para>
    ///         Valid only between a successful <see cref="AcquireNextImage" /> and the next
    ///         <see cref="Present" />, like the view it accompanies.
    ///     </para>
    /// </remarks>
    TextureHandle CurrentTexture { get; }

    /// <summary>Takes the next image to render into.</summary>
    /// <param name="view">A view of the image, valid until the next <see cref="Present" />.</param>
    /// <returns>What happened. Anything but <see cref="SwapChainStatus.Ready" /> or
    /// <see cref="SwapChainStatus.Suboptimal" /> means nothing was acquired.</returns>
    /// <remarks>
    ///     Returns a status rather than throwing, because
    ///     <see cref="SwapChainStatus.OutOfDate" /> is not an error — it happens every time a user
    ///     drags a window edge, and treating a routine event as exceptional costs an exception per
    ///     frame for the duration of the drag.
    /// </remarks>
    SwapChainStatus AcquireNextImage(out TextureViewHandle view);

    /// <summary>Presents the acquired image.</summary>
    /// <returns>What happened.</returns>
    SwapChainStatus Present();

    /// <summary>Rebuilds it at a new size.</summary>
    /// <param name="size">The new size in pixels.</param>
    /// <remarks>
    ///     The caller is responsible for having waited: every image the swapchain owns may still be
    ///     in flight, and recreating underneath them is undefined on every API.
    /// </remarks>
    void Resize(Int2 size);
}

/// <summary>What to create a swapchain as.</summary>
/// <param name="Surface">The window surface to present to.</param>
/// <param name="Size">The size in pixels.</param>
/// <param name="Format">
///     The preferred format. A backend may return a different one, so read
///     <see cref="ISwapChain.Format" /> back rather than assuming.
/// </param>
/// <param name="PresentMode">
///     The preferred present mode. Falls back to <see cref="Graphics.PresentMode.Fifo" />, which is
///     the only one every driver must support.
/// </param>
/// <param name="ImageCount">How many images to ask for.</param>
public readonly record struct SwapChainDescription(
    SurfaceHandle Surface,
    Int2 Size,
    PixelFormat Format = PixelFormat.Bgra8UNormSrgb,
    PresentMode PresentMode = PresentMode.Fifo,
    int ImageCount = 3
);

/// <summary>A logical device: what creates resources, records work and submits it.</summary>
/// <remarks>
///     <para>
///         Creation returns handles into tables this device owns, and <c>Destroy</c> returns them.
///         There is no <c>IDisposable</c> per resource and no finaliser anywhere: a GPU object's
///         lifetime is decided by the renderer, not by a collector that runs whenever it likes and
///         has no idea whether the GPU is still reading.
///     </para>
///     <para>
///         Destroying a resource is deferred internally until the frames that might still reference
///         it have retired. That is the backend's job and not the caller's — a renderer that had to
///         track it would track it wrongly.
///     </para>
/// </remarks>
public interface IGraphicsDevice : IDisposable {
    /// <summary>The adapter this device runs on.</summary>
    IGraphicsAdapter Adapter { get; }

    /// <summary>What it can do.</summary>
    GraphicsDeviceFeatures Features { get; }

    /// <summary>The queue that can do everything.</summary>
    ICommandSubmitter GraphicsQueue { get; }

    /// <summary>The compute queue, which is <see cref="GraphicsQueue" /> where the hardware has
    /// only one.</summary>
    ICommandSubmitter ComputeQueue { get; }

    /// <summary>The transfer queue, which is <see cref="GraphicsQueue" /> where the hardware has
    /// only one.</summary>
    ICommandSubmitter TransferQueue { get; }

    /// <summary>How many frames may be recorded before the first has to have finished.</summary>
    int FramesInFlight { get; }

    /// <summary>Creates a buffer.</summary>
    /// <param name="description">What to create.</param>
    BufferHandle CreateBuffer(in BufferDescription description);

    /// <summary>Creates a texture.</summary>
    /// <param name="description">What to create.</param>
    TextureHandle CreateTexture(in TextureDescription description);

    /// <summary>Creates a view of part of a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="format">The format to read it as, or
    /// <see cref="PixelFormat.Undefined" /> for the texture's own.</param>
    /// <param name="baseMipLevel">The first mip level.</param>
    /// <param name="mipLevelCount">How many levels, or <c>0</c> for all of them.</param>
    /// <param name="baseArrayLayer">The first array layer.</param>
    /// <param name="arrayLayerCount">How many layers, or <c>0</c> for all of them.</param>
    TextureViewHandle CreateTextureView(
        TextureHandle texture,
        PixelFormat format = PixelFormat.Undefined,
        int baseMipLevel = 0,
        int mipLevelCount = 0,
        int baseArrayLayer = 0,
        int arrayLayerCount = 0
    );

    /// <summary>Creates a sampler.</summary>
    /// <param name="description">What to create.</param>
    SamplerHandle CreateSampler(in SamplerDescription description);

    /// <summary>Creates a shader module from compiled bytecode.</summary>
    /// <param name="stage">Which stage it is for.</param>
    /// <param name="bytecode">The compiled module. The RHI never parses shader source.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    ShaderHandle CreateShader(ShaderStage stage, ReadOnlySpan<byte> bytecode, string name = "");

    /// <summary>Creates a descriptor-set layout.</summary>
    /// <param name="description">What to create.</param>
    DescriptorSetLayoutHandle CreateDescriptorSetLayout(in DescriptorSetLayoutDescription description);

    /// <summary>Creates a pipeline layout.</summary>
    /// <param name="description">What to create.</param>
    PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDescription description);

    /// <summary>Creates a descriptor set.</summary>
    /// <param name="layout">Its shape.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    DescriptorSetHandle CreateDescriptorSet(DescriptorSetLayoutHandle layout, string name = "");

    /// <summary>Points a descriptor set's bindings at resources.</summary>
    /// <param name="descriptors">The set.</param>
    /// <param name="writes">What to bind where.</param>
    void UpdateDescriptorSet(DescriptorSetHandle descriptors, ReadOnlySpan<DescriptorWrite> writes);

    /// <summary>Compiles a graphics pipeline.</summary>
    /// <param name="description">What to compile.</param>
    PipelineHandle CreateGraphicsPipeline(in GraphicsPipelineDescription description);

    /// <summary>Compiles a compute pipeline.</summary>
    /// <param name="description">What to compile.</param>
    PipelineHandle CreateComputePipeline(in ComputePipelineDescription description);

    /// <summary>Creates a swapchain for a window surface.</summary>
    /// <param name="description">What to create.</param>
    ISwapChain CreateSwapChain(in SwapChainDescription description);

    /// <summary>Creates a pool of GPU queries.</summary>
    /// <param name="description">What to create.</param>
    /// <returns>The pool.</returns>
    /// <remarks>
    ///     ⚠ <b>Ask <see cref="Features" /> first.</b> A device without
    ///     <see cref="GraphicsDeviceFeatures.HasTimestampQueries" /> throws here rather than handing
    ///     back a pool that measures nothing — a profiler drawing a timeline of zeroes is worse than
    ///     one that says the device cannot be timed, because the first is a wrong answer and the
    ///     second is an answer.
    /// </remarks>
    /// <exception cref="NotSupportedException">The device cannot answer this kind of query.</exception>
    QueryPoolHandle CreateQueryPool(in QueryPoolDescription description);

    /// <summary>Returns a query pool.</summary>
    /// <param name="handle">The pool.</param>
    void Destroy(QueryPoolHandle handle);

    /// <summary>Asks how much memory one acceleration-structure build needs.</summary>
    /// <param name="input">Everything the build will read. The counts are what sizing uses; the
    ///     buffers may still be empty.</param>
    /// <returns>The structure's size and the build's scratch size.</returns>
    /// <remarks>
    ///     ⚠ <b>Ask <see cref="Features" /> first.</b> A device without
    ///     <see cref="GraphicsDeviceFeatures.HasRayTracing" /> throws here, the
    ///     <see cref="CreateQueryPool" /> stance: the distance-field tracer is the fallback, and a
    ///     structure that cannot be queried is not a fallback.
    /// </remarks>
    /// <exception cref="NotSupportedException">The device has no ray tracing.</exception>
    AccelerationStructureSizes GetAccelerationStructureSizes(in AccelerationStructureBuildInput input);

    /// <summary>Creates an acceleration structure — empty until a build fills it.</summary>
    /// <param name="description">What to create, sized by
    ///     <see cref="GetAccelerationStructureSizes" />.</param>
    /// <exception cref="NotSupportedException">The device has no ray tracing.</exception>
    AccelerationStructureHandle CreateAccelerationStructure(in AccelerationStructureDescription description);

    /// <summary>The GPU address a top-level build's instances refer to a bottom level by.</summary>
    /// <param name="handle">A bottom-level structure.</param>
    /// <exception cref="NotSupportedException">The device has no ray tracing.</exception>
    ulong GetAccelerationStructureAddress(AccelerationStructureHandle handle);

    /// <summary>Returns an acceleration structure.</summary>
    /// <param name="handle">The structure.</param>
    void Destroy(AccelerationStructureHandle handle);

    /// <summary>Reads out whatever a pool's queries have answered.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="first">The first query to read.</param>
    /// <param name="results">
    ///     Where the readings go, one per query. Its length is how many are read.
    /// </param>
    /// <returns>
    ///     Whether every query in the range had an answer. <see langword="false" /> leaves
    ///     <paramref name="results" /> untouched.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It does not wait, and that is the whole contract.</b> A query is answered when the
    ///         submission that wrote it has finished on the GPU, which is up to
    ///         <see cref="FramesInFlight" /> frames after it was recorded. Blocking here would stall
    ///         the CPU on the GPU once per frame — a profiler that halves the frame rate it is
    ///         measuring — so the caller records into pool <i>n</i>, asks about pool
    ///         <i>n − FramesInFlight</i>, and gets <see langword="false" /> until the answer is there.
    ///     </para>
    ///     <para>
    ///         All or nothing, rather than a count: a partially resolved range is one where the pairs
    ///         a caller subtracts may straddle the boundary, and a half-resolved pair reads as a
    ///         nonsense duration rather than as missing data.
    ///     </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">The device cannot answer this kind of query.</exception>
    bool TryResolveQueries(QueryPoolHandle pool, int first, Span<ulong> results);

    /// <summary>
    ///     Returns a buffer.
    /// </summary>
    /// <param name="handle">The buffer.</param>
    /// <remarks>
    ///     <para>
    ///         <strong>Every <c>Destroy</c> on this interface is deferred, and a backend that frees
    ///         immediately is wrong.</strong> Destroying an object a submitted command buffer still
    ///         references is undefined behaviour, and the window in which it is unsafe is exactly
    ///         <see cref="FramesInFlight" /> frames wide — which the caller has no way of knowing and
    ///         should not have to. So the handle becomes invalid to the caller here and the object is
    ///         freed once no frame that could reference it is still running.
    ///     </para>
    ///     <para>
    ///         That is why a renderer may recreate a buffer mid-frame without waiting: the old handle
    ///         is safe to hand back even while the frame that used it is on the GPU. What is
    ///         <em>not</em> safe, and what no backend can defer for you, is overwriting the
    ///         <em>contents</em> of a resource a running frame is reading — see
    ///         <see cref="DescriptorAllocator" /> for the shape of the answer to that.
    ///     </para>
    ///     <para>
    ///         A backend with no GPU satisfies this trivially, because no frame it runs can reference
    ///         anything.
    ///     </para>
    /// </remarks>
    void Destroy(BufferHandle handle);

    /// <summary>Returns a texture.</summary>
    /// <param name="handle">The texture.</param>
    void Destroy(TextureHandle handle);

    /// <summary>Returns a texture view.</summary>
    /// <param name="handle">The view.</param>
    void Destroy(TextureViewHandle handle);

    /// <summary>Returns a sampler.</summary>
    /// <param name="handle">The sampler.</param>
    void Destroy(SamplerHandle handle);

    /// <summary>Returns a shader module.</summary>
    /// <param name="handle">The module.</param>
    void Destroy(ShaderHandle handle);

    /// <summary>Returns a pipeline.</summary>
    /// <param name="handle">The pipeline.</param>
    void Destroy(PipelineHandle handle);

    /// <summary>Returns a pipeline layout.</summary>
    /// <param name="handle">The layout.</param>
    void Destroy(PipelineLayoutHandle handle);

    /// <summary>Returns a descriptor-set layout.</summary>
    /// <param name="handle">The layout.</param>
    void Destroy(DescriptorSetLayoutHandle handle);

    /// <summary>Returns a descriptor set.</summary>
    /// <param name="handle">The set.</param>
    void Destroy(DescriptorSetHandle handle);

    /// <summary>Writes into a host-visible buffer.</summary>
    /// <param name="buffer">The buffer, which must not be
    /// <see cref="MemoryAccess.DeviceLocal" />.</param>
    /// <param name="offset">Where to start writing, in bytes.</param>
    /// <param name="data">What to write.</param>
    void Write(BufferHandle buffer, long offset, ReadOnlySpan<byte> data);

    /// <summary>Reads out of a readback buffer.</summary>
    /// <param name="buffer">The buffer, which must be
    /// <see cref="MemoryAccess.HostReadback" />.</param>
    /// <param name="offset">Where to start reading, in bytes.</param>
    /// <param name="destination">Where to put it.</param>
    /// <remarks>
    ///     The caller is responsible for having waited on the work that filled it. There is no
    ///     implicit synchronisation here, deliberately: an implicit wait is a stall the caller
    ///     cannot see, and finding it later means reading the RHI's source rather than their own.
    /// </remarks>
    void Read(BufferHandle buffer, long offset, Span<byte> destination);

    /// <summary>Takes a command list to record into.</summary>
    /// <param name="kind">Which queue it will be submitted to.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    /// <returns>A list, already begun. Recording is safe on any thread; one list per thread.</returns>
    ICommandList BeginCommandList(QueueKind kind = QueueKind.Graphics, string name = "");

    /// <summary>Marks the start of a frame, retiring whatever the oldest in-flight frame held.</summary>
    void BeginFrame();

    /// <summary>Marks the end of a frame.</summary>
    void EndFrame();

    /// <summary>Blocks until every queue is idle.</summary>
    void WaitIdle();
}

/// <summary>One binding to point at a resource.</summary>
/// <param name="Binding">Which binding within the set.</param>
/// <param name="Kind">What kind of resource it is.</param>
/// <param name="Buffer">The buffer, for a buffer binding.</param>
/// <param name="Offset">Where in the buffer it starts.</param>
/// <param name="Size">How much of the buffer, or <c>0</c> for the rest of it.</param>
/// <param name="TextureView">The view, for a texture binding.</param>
/// <param name="Sampler">The sampler, for a sampler binding.</param>
/// <param name="ArrayIndex">Which element, for an array binding.</param>
/// <param name="Structure">The structure, for an acceleration-structure binding.</param>
public readonly record struct DescriptorWrite(
    uint Binding,
    DescriptorKind Kind,
    BufferHandle Buffer = default,
    long Offset = 0,
    long Size = 0,
    TextureViewHandle TextureView = default,
    SamplerHandle Sampler = default,
    int ArrayIndex = 0,
    AccelerationStructureHandle Structure = default
) {
    /// <summary>Binds a uniform buffer.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">Where it starts.</param>
    /// <param name="size">How much of it, or <c>0</c> for the rest.</param>
    public static DescriptorWrite Uniform(uint binding, BufferHandle buffer, long offset = 0, long size = 0) =>
        new(binding, DescriptorKind.UniformBuffer, buffer, offset, size);

    /// <summary>Binds a uniform buffer whose offset is supplied per draw.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">Where the range the dynamic offset is measured from starts.</param>
    /// <param name="size">
    ///     How much of it one draw sees — the <em>block's</em> size, not the buffer's, since a
    ///     dynamic offset names where a block starts and this says how far it extends. Passing
    ///     <c>0</c> extends it to the end of the buffer, which lets a shader read every other draw's
    ///     block.
    /// </param>
    /// <remarks>
    ///     Distinct from <see cref="Uniform" /> rather than a flag on it, because the difference is
    ///     one a descriptor-set layout declares and a backend cannot infer: a set written as a plain
    ///     <see cref="DescriptorKind.UniformBuffer" /> takes no offset at bind time, so every
    ///     per-draw offset handed to
    ///     <see cref="ICommandList.BindDescriptorSet(DescriptorSetSlot, DescriptorSetHandle, ReadOnlySpan{uint})" />
    ///     is ignored and every object draws with the first one's data.
    /// </remarks>
    public static DescriptorWrite DynamicUniform(uint binding, BufferHandle buffer, long offset = 0, long size = 0) =>
        new(binding, DescriptorKind.DynamicUniformBuffer, buffer, offset, size);

    /// <summary>Binds a storage buffer.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">Where it starts.</param>
    /// <param name="size">How much of it, or <c>0</c> for the rest.</param>
    public static DescriptorWrite Storage(uint binding, BufferHandle buffer, long offset = 0, long size = 0) =>
        new(binding, DescriptorKind.StorageBuffer, buffer, offset, size);

    /// <summary>Binds a sampled texture.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="view">The view.</param>
    /// <param name="arrayIndex">Which element, for an array binding.</param>
    public static DescriptorWrite Texture(uint binding, TextureViewHandle view, int arrayIndex = 0) =>
        new(binding, DescriptorKind.SampledTexture, TextureView: view, ArrayIndex: arrayIndex);

    /// <summary>Binds a storage image — one a shader loads and stores rather than samples.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="view">The view.</param>
    /// <param name="arrayIndex">Which element, for an array binding.</param>
    /// <remarks>
    ///     Distinct from <see cref="Texture" /> rather than a flag on it, because the difference is one a
    ///     descriptor-set layout declares and no driver checks at bind time: a storage image written as a
    ///     sampled one is accepted, and the shader reads whichever it was compiled for. That is silently
    ///     wrong in the direction nobody looks — a compute pass whose output is a texture that never
    ///     changes.
    /// </remarks>
    public static DescriptorWrite StorageImage(uint binding, TextureViewHandle view, int arrayIndex = 0) =>
        new(binding, DescriptorKind.StorageTexture, TextureView: view, ArrayIndex: arrayIndex);

    /// <summary>Binds a sampler.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="sampler">The sampler.</param>
    public static DescriptorWrite SamplerAt(uint binding, SamplerHandle sampler) =>
        new(binding, DescriptorKind.Sampler, Sampler: sampler);

    /// <summary>Binds a top-level acceleration structure for a ray query to open against.</summary>
    /// <param name="binding">Which binding.</param>
    /// <param name="structure">The structure — built, or the query finds whatever a build never
    ///     wrote.</param>
    public static DescriptorWrite Acceleration(uint binding, AccelerationStructureHandle structure) =>
        new(binding, DescriptorKind.AccelerationStructure, Structure: structure);
}
