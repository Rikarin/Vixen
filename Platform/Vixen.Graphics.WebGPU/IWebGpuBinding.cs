// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>How WebGPU is reached.</summary>
/// <remarks>
///     <para>
///         WebGPU is one API with two entirely unrelated ways in. <c>Silk.NET.WebGPU</c> binds
///         <c>webgpu.h</c>, which Dawn and wgpu-native implement and which a browser does not expose
///         at all; a browser reaches WebGPU through <c>navigator.gpu</c> and JavaScript. So
///         <c>docs/plan/05</c> asks for two <em>surface implementations</em> behind one backend, and
///         this is the seam between them.
///     </para>
///     <para>
///         Called a binding rather than a surface only because <c>SurfaceHandle</c> already
///         means "a window to present to" everywhere else in this engine, and a type named
///         <c>IWebGpuSurface</c> that was not about windows would be read wrongly by everyone.
///     </para>
///     <para>
///         <b>Everything above this line is shared.</b> Translation, validation, handle lifetime,
///         deferred destruction, the recorded command stream and its replay are all written against
///         this interface, which is what makes the browser surface a few hundred lines of interop
///         rather than a second backend — and what makes the whole translation layer testable
///         against a fake, on a machine with no GPU. The implementations own marshalling and
///         nothing else: not one of these methods decides anything.
///     </para>
///     <para>
///         The methods are fine-grained because replay is shared. In a browser each call crosses the
///         interop boundary, which is not free; the recorded stream is a flat array of blittable
///         structs precisely so a future bulk path can hand the whole thing over at once without
///         disturbing anything above.
///     </para>
/// </remarks>
public interface IWebGpuBinding : IDisposable {
    /// <summary>What the implementation says about the adapter.</summary>
    WebGpuAdapterInfo AdapterInfo { get; }

    /// <summary>What the device reports it can do.</summary>
    WebGpuLimits Limits { get; }

    /// <summary>Whether there is a surface to present to.</summary>
    bool HasSurface { get; }

    /// <summary>The format the surface prefers, or
    /// <see cref="WgpuTextureFormat.Undefined" /> when there is no surface.</summary>
    WgpuTextureFormat PreferredSurfaceFormat { get; }

    /// <summary>Whether the device was created with an optional feature enabled.</summary>
    /// <param name="feature">The feature.</param>
    bool HasFeature(WgpuFeatureName feature);

    // ── Resources ───────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a buffer.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateBuffer(in WgpuBufferDescriptor descriptor);

    /// <summary>Creates a texture.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateTexture(in WgpuTextureDescriptor descriptor);

    /// <summary>Creates a view of a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateTextureView(WebGpuObject texture, in WgpuTextureViewDescriptor descriptor);

    /// <summary>Creates a sampler.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateSampler(in WgpuSamplerDescriptor descriptor);

    /// <summary>Creates a shader module.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateShaderModule(in WgpuShaderModuleDescriptor descriptor);

    /// <summary>Creates a bind group layout.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateBindGroupLayout(in WgpuBindGroupLayoutDescriptor descriptor);

    /// <summary>Creates a pipeline layout.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreatePipelineLayout(in WgpuPipelineLayoutDescriptor descriptor);

    /// <summary>Creates a bind group.</summary>
    /// <param name="descriptor">What to create.</param>
    WebGpuObject CreateBindGroup(in WgpuBindGroupDescriptor descriptor);

    /// <summary>Compiles a render pipeline.</summary>
    /// <param name="descriptor">What to compile.</param>
    WebGpuObject CreateRenderPipeline(in WgpuRenderPipelineDescriptor descriptor);

    /// <summary>Compiles a compute pipeline.</summary>
    /// <param name="descriptor">What to compile.</param>
    WebGpuObject CreateComputePipeline(in WgpuComputePipelineDescriptor descriptor);

    /// <summary>Releases an object this binding created.</summary>
    /// <param name="kind">What kind of object it is.</param>
    /// <param name="handle">The object.</param>
    /// <remarks>
    ///     One method rather than eleven, because the caller always knows the kind and the
    ///     implementations do not otherwise differ — the native surface calls
    ///     <c>wgpu…Release</c>, the browser one drops a table entry.
    /// </remarks>
    void Release(WebGpuObjectKind kind, WebGpuObject handle);

    // ── Queue ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes into a buffer through the queue.</summary>
    /// <param name="buffer">The buffer, which must be a copy destination.</param>
    /// <param name="offset">Where to start writing, in bytes.</param>
    /// <param name="data">What to write.</param>
    void WriteBuffer(WebGpuObject buffer, long offset, ReadOnlySpan<byte> data);

    /// <summary>Maps a buffer, copies out of it and unmaps it.</summary>
    /// <param name="buffer">The buffer, which must be map-readable.</param>
    /// <param name="offset">Where to start reading, in bytes.</param>
    /// <param name="destination">Where to put it.</param>
    /// <returns><see langword="false" /> if the map failed, leaving
    /// <paramref name="destination" /> untouched.</returns>
    /// <remarks>
    ///     Blocking, and knowingly so. WebGPU's map is asynchronous everywhere and genuinely
    ///     unblockable in a browser's main thread, so the browser surface reports readback
    ///     unsupported rather than deadlocking; see its README.
    /// </remarks>
    bool ReadBuffer(WebGpuObject buffer, long offset, Span<byte> destination);

    /// <summary>Submits finished command buffers, in order.</summary>
    /// <param name="commands">The command buffers.</param>
    void Submit(ReadOnlySpan<WebGpuObject> commands);

    /// <summary>Blocks until everything submitted has finished.</summary>
    /// <returns><see langword="false" /> if the implementation cannot block, which a browser
    /// cannot.</returns>
    bool WaitIdle();

    /// <summary>Lets the implementation run its callbacks.</summary>
    /// <remarks>
    ///     <c>wgpuInstanceProcessEvents</c>. A browser has an event loop of its own and does
    ///     nothing here.
    /// </remarks>
    void Tick();

    // ── Encoding ────────────────────────────────────────────────────────────────────────────

    /// <summary>Takes a command encoder.</summary>
    /// <param name="label">A name for the debugger.</param>
    WebGpuObject CreateCommandEncoder(string label);

    /// <summary>Finishes an encoder into a command buffer.</summary>
    /// <param name="encoder">The encoder, which is released.</param>
    /// <param name="label">A name for the debugger.</param>
    WebGpuObject FinishCommandEncoder(WebGpuObject encoder, string label);

    /// <summary>Copies between buffers.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="source">The source.</param>
    /// <param name="sourceOffset">Where to read from, in bytes.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="destinationOffset">Where to write to, in bytes.</param>
    /// <param name="size">How many bytes.</param>
    void CopyBufferToBuffer(
        WebGpuObject encoder,
        WebGpuObject source,
        long sourceOffset,
        WebGpuObject destination,
        long destinationOffset,
        long size
    );

    /// <summary>Copies from a buffer into a texture.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="source">Where to read from.</param>
    /// <param name="destination">Where to write to.</param>
    /// <param name="width">The region's width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="depthOrLayers">Its depth in slices, or its layer count.</param>
    void CopyBufferToTexture(
        WebGpuObject encoder,
        in WgpuImageCopyBuffer source,
        in WgpuImageCopyTexture destination,
        int width,
        int height,
        int depthOrLayers
    );

    /// <summary>Copies from a texture into a buffer.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="source">Where to read from.</param>
    /// <param name="destination">Where to write to.</param>
    /// <param name="width">The region's width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="depthOrLayers">Its depth in slices, or its layer count.</param>
    void CopyTextureToBuffer(
        WebGpuObject encoder,
        in WgpuImageCopyTexture source,
        in WgpuImageCopyBuffer destination,
        int width,
        int height,
        int depthOrLayers
    );

    /// <summary>Copies between textures.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="source">Where to read from.</param>
    /// <param name="destination">Where to write to.</param>
    /// <param name="width">The region's width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="depthOrLayers">Its depth in slices, or its layer count.</param>
    void CopyTextureToTexture(
        WebGpuObject encoder,
        in WgpuImageCopyTexture source,
        in WgpuImageCopyTexture destination,
        int width,
        int height,
        int depthOrLayers
    );

    /// <summary>Opens a named group on an encoder.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="name">The group's name.</param>
    void EncoderPushDebugGroup(WebGpuObject encoder, string name);

    /// <summary>Closes the innermost group on an encoder.</summary>
    /// <param name="encoder">The encoder.</param>
    void EncoderPopDebugGroup(WebGpuObject encoder);

    /// <summary>Marks a point on an encoder.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="name">The marker's name.</param>
    void EncoderInsertDebugMarker(WebGpuObject encoder, string name);

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <summary>Begins a render pass.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="descriptor">What to render into.</param>
    WebGpuObject BeginRenderPass(WebGpuObject encoder, in WgpuRenderPassDescriptor descriptor);

    /// <summary>Ends a render pass and releases its encoder.</summary>
    /// <param name="pass">The pass.</param>
    void EndRenderPass(WebGpuObject pass);

    /// <summary>Binds a pipeline in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="pipeline">The pipeline.</param>
    void RenderPassSetPipeline(WebGpuObject pass, WebGpuObject pipeline);

    /// <summary>Binds a bind group in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="group">Which group index.</param>
    /// <param name="bindGroup">The bind group.</param>
    /// <param name="dynamicOffsets">Offsets for its dynamic bindings, in declaration order.</param>
    void RenderPassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    );

    /// <summary>Binds a vertex buffer in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="slot">Which binding.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">Where its data starts, in bytes.</param>
    /// <param name="size">How much of it, in bytes.</param>
    void RenderPassSetVertexBuffer(WebGpuObject pass, uint slot, WebGpuObject buffer, long offset, long size);

    /// <summary>Binds the index buffer in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="format">How wide an index is.</param>
    /// <param name="offset">Where its data starts, in bytes.</param>
    /// <param name="size">How much of it, in bytes.</param>
    void RenderPassSetIndexBuffer(
        WebGpuObject pass,
        WebGpuObject buffer,
        WgpuIndexFormat format,
        long offset,
        long size
    );

    /// <summary>Sets the viewport.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="x">The left edge, in pixels.</param>
    /// <param name="y">The top edge, in pixels.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="minDepth">The near depth.</param>
    /// <param name="maxDepth">The far depth.</param>
    void RenderPassSetViewport(
        WebGpuObject pass,
        float x,
        float y,
        float width,
        float height,
        float minDepth,
        float maxDepth
    );

    /// <summary>Sets the scissor rectangle.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="x">The left edge, in pixels.</param>
    /// <param name="y">The top edge, in pixels.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    void RenderPassSetScissorRect(WebGpuObject pass, uint x, uint y, uint width, uint height);

    /// <summary>Sets the blend constant.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <param name="a">The alpha channel.</param>
    void RenderPassSetBlendConstant(WebGpuObject pass, double r, double g, double b, double a);

    /// <summary>Sets the stencil reference value.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="reference">The value.</param>
    void RenderPassSetStencilReference(WebGpuObject pass, uint reference);

    /// <summary>Draws without indices.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="vertexCount">How many vertices.</param>
    /// <param name="instanceCount">How many instances.</param>
    /// <param name="firstVertex">The first vertex.</param>
    /// <param name="firstInstance">The first instance.</param>
    void RenderPassDraw(
        WebGpuObject pass,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance
    );

    /// <summary>Draws with indices.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="indexCount">How many indices.</param>
    /// <param name="instanceCount">How many instances.</param>
    /// <param name="firstIndex">The first index.</param>
    /// <param name="baseVertex">A value added to every index.</param>
    /// <param name="firstInstance">The first instance.</param>
    void RenderPassDrawIndexed(
        WebGpuObject pass,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance
    );

    /// <summary>Draws with arguments the GPU wrote.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="arguments">The buffer holding them.</param>
    /// <param name="offset">Where they start, in bytes.</param>
    void RenderPassDrawIndexedIndirect(WebGpuObject pass, WebGpuObject arguments, long offset);

    /// <summary>Opens a named group in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="name">The group's name.</param>
    void RenderPassPushDebugGroup(WebGpuObject pass, string name);

    /// <summary>Closes the innermost group in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    void RenderPassPopDebugGroup(WebGpuObject pass);

    /// <summary>Marks a point in a render pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="name">The marker's name.</param>
    void RenderPassInsertDebugMarker(WebGpuObject pass, string name);

    // ── Compute passes ──────────────────────────────────────────────────────────────────────

    /// <summary>Begins a compute pass.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="label">A name for the debugger.</param>
    WebGpuObject BeginComputePass(WebGpuObject encoder, string label);

    /// <summary>Ends a compute pass and releases its encoder.</summary>
    /// <param name="pass">The pass.</param>
    void EndComputePass(WebGpuObject pass);

    /// <summary>Binds a pipeline in a compute pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="pipeline">The pipeline.</param>
    void ComputePassSetPipeline(WebGpuObject pass, WebGpuObject pipeline);

    /// <summary>Binds a bind group in a compute pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="group">Which group index.</param>
    /// <param name="bindGroup">The bind group.</param>
    /// <param name="dynamicOffsets">Offsets for its dynamic bindings, in declaration order.</param>
    void ComputePassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    );

    /// <summary>Runs a compute shader.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="groupsX">Workgroups in X.</param>
    /// <param name="groupsY">Workgroups in Y.</param>
    /// <param name="groupsZ">Workgroups in Z.</param>
    void ComputePassDispatch(WebGpuObject pass, uint groupsX, uint groupsY, uint groupsZ);

    /// <summary>Runs a compute shader with a group count the GPU wrote.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="arguments">The buffer holding it.</param>
    /// <param name="offset">Where it starts, in bytes.</param>
    void ComputePassDispatchIndirect(WebGpuObject pass, WebGpuObject arguments, long offset);

    /// <summary>Opens a named group in a compute pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="name">The group's name.</param>
    void ComputePassPushDebugGroup(WebGpuObject pass, string name);

    /// <summary>Closes the innermost group in a compute pass.</summary>
    /// <param name="pass">The pass.</param>
    void ComputePassPopDebugGroup(WebGpuObject pass);

    /// <summary>Marks a point in a compute pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="name">The marker's name.</param>
    void ComputePassInsertDebugMarker(WebGpuObject pass, string name);

    // ── Surface ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Configures the surface for presentation.</summary>
    /// <param name="configuration">How to configure it.</param>
    void ConfigureSurface(in WgpuSurfaceConfiguration configuration);

    /// <summary>Takes the surface's next texture.</summary>
    /// <param name="texture">The texture, when one was acquired.</param>
    /// <returns>What happened.</returns>
    WgpuSurfaceStatus AcquireSurfaceTexture(out WebGpuObject texture);

    /// <summary>Presents the acquired texture.</summary>
    void PresentSurface();
}

/// <summary>What kind of thing a <see cref="WebGpuObject" /> refers to.</summary>
/// <remarks>
///     Only so <see cref="IWebGpuBinding.Release" /> can be one method. A token carries no type of
///     its own — it is a pointer on one surface and an index on the other — and the caller always
///     knows what it asked for.
/// </remarks>
public enum WebGpuObjectKind : byte {
    /// <summary>A buffer.</summary>
    Buffer = 0,

    /// <summary>A texture.</summary>
    Texture = 1,

    /// <summary>A texture view.</summary>
    TextureView = 2,

    /// <summary>A sampler.</summary>
    Sampler = 3,

    /// <summary>A shader module.</summary>
    ShaderModule = 4,

    /// <summary>A bind group layout.</summary>
    BindGroupLayout = 5,

    /// <summary>A pipeline layout.</summary>
    PipelineLayout = 6,

    /// <summary>A bind group.</summary>
    BindGroup = 7,

    /// <summary>A render pipeline.</summary>
    RenderPipeline = 8,

    /// <summary>A compute pipeline.</summary>
    ComputePipeline = 9,

    /// <summary>A command buffer.</summary>
    CommandBuffer = 10
}
