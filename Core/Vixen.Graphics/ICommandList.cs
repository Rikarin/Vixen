// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics;

/// <summary>What a render pass renders into.</summary>
/// <param name="colourAttachments">The colour attachments, in the order the pipeline writes them.</param>
/// <param name="depthStencil">The depth-stencil attachment, or <see langword="null" /> for none.</param>
/// <param name="name">A name for the debugger, RenderDoc and the frame graph.</param>
/// <param name="renderArea">The region the pass affects, or <see langword="null" /> for the whole attachment.</param>
public readonly ref struct RenderPassDescription(
    ReadOnlySpan<ColourAttachment> colourAttachments,
    DepthStencilAttachment? depthStencil = null,
    string name = "",
    ScissorRect? renderArea = null
) {
    /// <summary>The colour attachments, in the order the pipeline writes them.</summary>
    public ReadOnlySpan<ColourAttachment> ColourAttachments { get; } = colourAttachments;

    /// <summary>The depth-stencil attachment, or <see langword="null" /> for none.</summary>
    public DepthStencilAttachment? DepthStencil { get; } = depthStencil;

    /// <summary>A name for the debugger, RenderDoc and the frame graph.</summary>
    public string Name { get; } = name;

    /// <summary>The region the pass affects, or <see langword="null" /> for the whole attachment.</summary>
    /// <remarks>
    ///     <para>
    ///         What a <see cref="LoadAction.Clear" /> is confined by. A scissor rectangle confines
    ///         <em>draws</em>; the load op happens when the pass begins, before any draw, and applies
    ///         to this region — so a pass that owns one tile of a cached atlas must say so here or
    ///         its clear wipes every other tile in the texture. That is not hypothetical: the
    ///         virtual shadow atlas relied on the scissor and lost every page but the last drawn.
    ///     </para>
    ///     <para>
    ///         Honoured by the Vulkan backend. Backends without the concept (OpenGL, WebGPU) treat
    ///         it as the whole attachment — a caller that needs a confined clear on those backends
    ///         must load and overdraw instead.
    ///     </para>
    /// </remarks>
    public ScissorRect? RenderArea { get; } = renderArea;
}

/// <summary>Where one texture copy reads from or writes to.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="MipLevel">Which mip level.</param>
/// <param name="ArrayLayer">Which array layer.</param>
/// <param name="Origin">The corner of the region, in texels.</param>
public readonly record struct TextureRegion(
    TextureHandle Texture,
    int MipLevel = 0,
    int ArrayLayer = 0,
    Int3 Origin = default
);

/// <summary>A list of recorded work, submitted to a queue as a unit.</summary>
/// <remarks>
///     <para>
///         Recorded on any thread — one list per thread per frame — which is what makes a renderer
///         scale with cores rather than with the main thread. The OpenGL backend cannot record in
///         parallel and does not pretend to: it records into a replayable buffer in managed memory
///         and replays it on the GL thread at submit, which keeps this contract true everywhere at a
///         modest CPU cost (<c>docs/plan/05</c>).
///     </para>
///     <para>
///         Disposing returns the list to its pool. It is not a resource with a lifetime the caller
///         has to reason about; it is a scratch buffer.
///     </para>
/// </remarks>
public interface ICommandList : IDisposable {
    /// <summary>Which queue this list may be submitted to.</summary>
    QueueKind Kind { get; }

    /// <summary>Whether recording has finished.</summary>
    bool IsRecorded { get; }

    /// <summary>Finishes recording. The list may then be submitted, once.</summary>
    void Finish();

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <summary>Begins a render pass.</summary>
    /// <param name="description">What to render into, and what to do with it.</param>
    /// <remarks>
    ///     Passes do not nest, and everything a draw needs — pipeline, viewport, scissor, bindings —
    ///     is scoped to the pass. That is the render-pass model both Vulkan and D3D12 have, and it
    ///     is what lets a tiled mobile GPU keep the whole pass in tile memory.
    /// </remarks>
    void BeginRenderPass(in RenderPassDescription description);

    /// <summary>Ends the current render pass.</summary>
    void EndRenderPass();

    // ── State ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Sets the viewport.</summary>
    /// <param name="viewport">The viewport, in pixels.</param>
    void SetViewport(in Viewport viewport);

    /// <summary>Sets the scissor rectangle.</summary>
    /// <param name="scissor">The rectangle, in pixels.</param>
    void SetScissor(in ScissorRect scissor);

    /// <summary>Sets the blend constant used by
    /// <see cref="BlendFactor.Constant" />.</summary>
    /// <param name="colour">The constant.</param>
    void SetBlendConstant(in Color4 colour);

    /// <summary>Sets the stencil reference value.</summary>
    /// <param name="reference">The value.</param>
    void SetStencilReference(uint reference);

    /// <summary>Binds a pipeline.</summary>
    /// <param name="pipeline">The pipeline.</param>
    void BindPipeline(PipelineHandle pipeline);

    /// <summary>Binds a descriptor set.</summary>
    /// <param name="slot">Which of the four conventional sets.</param>
    /// <param name="descriptors">The set.</param>
    /// <param name="dynamicOffsets">
    ///     Offsets for the set's dynamic bindings, in declaration order. How per-draw transforms are
    ///     bound without a descriptor set per object.
    /// </param>
    void BindDescriptorSet(DescriptorSetSlot slot, DescriptorSetHandle descriptors, ReadOnlySpan<uint> dynamicOffsets = default);

    /// <summary>Writes push constants.</summary>
    /// <param name="stages">Which stages see them.</param>
    /// <param name="offset">The offset within the block, in bytes.</param>
    /// <param name="data">The bytes.</param>
    void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data);

    /// <summary>Binds a vertex buffer.</summary>
    /// <param name="slot">Which vertex buffer binding.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">Where its data starts, in bytes.</param>
    void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0);

    /// <summary>Binds the index buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="format">How wide an index is.</param>
    /// <param name="offset">Where its data starts, in bytes.</param>
    void BindIndexBuffer(BufferHandle buffer, IndexFormat format = IndexFormat.UInt16, long offset = 0);

    // ── Drawing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Draws without indices.</summary>
    /// <param name="vertexCount">How many vertices.</param>
    /// <param name="instanceCount">How many instances.</param>
    /// <param name="firstVertex">The first vertex.</param>
    /// <param name="firstInstance">The first instance.</param>
    void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0);

    /// <summary>Draws with indices.</summary>
    /// <param name="indexCount">How many indices.</param>
    /// <param name="instanceCount">How many instances.</param>
    /// <param name="firstIndex">The first index.</param>
    /// <param name="vertexOffset">A value added to every index.</param>
    /// <param name="firstInstance">The first instance.</param>
    void DrawIndexed(
        int indexCount,
        int instanceCount = 1,
        int firstIndex = 0,
        int vertexOffset = 0,
        int firstInstance = 0
    );

    /// <summary>Draws with arguments the GPU wrote.</summary>
    /// <param name="arguments">The buffer holding them.</param>
    /// <param name="offset">Where they start, in bytes.</param>
    /// <param name="drawCount">How many draws. More than one needs
    /// <see cref="GraphicsDeviceFeatures.HasMultiDrawIndirect" />.</param>
    /// <param name="stride">Bytes between consecutive argument structures.</param>
    void DrawIndexedIndirect(BufferHandle arguments, long offset = 0, int drawCount = 1, int stride = 20);

    /// <summary>Draws with arguments the GPU wrote, as many of them as the GPU said.</summary>
    /// <param name="arguments">The buffer holding them.</param>
    /// <param name="count">A buffer holding one <c>uint</c>: how many of the arguments to read.</param>
    /// <param name="offset">Where the arguments start, in bytes.</param>
    /// <param name="countOffset">Where the count is, in bytes. Four-byte aligned.</param>
    /// <param name="maxDrawCount">
    ///     A ceiling on what the count may say, and the length of the region the arguments occupy.
    /// </param>
    /// <param name="stride">Bytes between consecutive argument structures.</param>
    /// <remarks>
    ///     <para>
    ///         Needs <see cref="GraphicsDeviceFeatures.HasDrawIndirectCount" />. What it buys is the
    ///         last thing between a compacted draw list and a single command: without it the host has
    ///         to name a count, so a list the GPU compacted must be issued at its maximum length with
    ///         the tail zeroed, and every culled object still costs a command.
    ///     </para>
    ///     <para>
    ///         ⚠ <paramref name="maxDrawCount" /> is not advisory. It is what the region was sized
    ///         for, and a device is entitled to read up to that many argument structures whatever the
    ///         count buffer says — so a value larger than the allocation is a read past the end of a
    ///         buffer, which is undefined rather than clamped.
    ///     </para>
    /// </remarks>
    void DrawIndexedIndirectCount(
        BufferHandle arguments,
        BufferHandle count,
        long offset = 0,
        long countOffset = 0,
        int maxDrawCount = 1,
        int stride = 20
    );

    /// <summary>Runs a compute shader.</summary>
    /// <param name="groupsX">Workgroups in X.</param>
    /// <param name="groupsY">Workgroups in Y.</param>
    /// <param name="groupsZ">Workgroups in Z.</param>
    void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1);

    /// <summary>Runs a compute shader with a group count the GPU wrote.</summary>
    /// <param name="arguments">The buffer holding it.</param>
    /// <param name="offset">Where it starts, in bytes.</param>
    void DispatchIndirect(BufferHandle arguments, long offset = 0);

    /// <summary>Builds an acceleration structure from its geometry.</summary>
    /// <param name="target">The structure, created at the size the same input was measured to.</param>
    /// <param name="input">Everything the build reads — the record
    ///     <see cref="IGraphicsDevice.GetAccelerationStructureSizes" /> sized.</param>
    /// <param name="scratch">The build's working memory — at least
    ///     <see cref="AccelerationStructureSizes.Scratch" /> bytes of
    ///     <see cref="BufferUsage.Storage" /> + <see cref="BufferUsage.ShaderDeviceAddress" />.</param>
    /// <param name="scratchOffset">Where in it the build may write, in bytes.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ Needs <see cref="GraphicsDeviceFeatures.HasRayTracing" />; outside a render pass,
    ///         like a dispatch. The geometry buffers must already hold their data — the caller's
    ///         copies and barriers, as for any read — and a top-level build reads bottom-level
    ///         structures whose own builds must have been recorded <em>before it on the same
    ///         queue</em>.
    ///     </para>
    ///     <para>
    ///         <b>The build's own hazard is the backend's.</b> The command ends with the barrier
    ///         that makes the structure readable by ray queries and by later builds — builds are
    ///         rare and coarse, so a barrier per build costs nothing measurable, and the alternative
    ///         is a new resource-state vocabulary every caller of a niche feature must learn before
    ///         the first query works.
    ///     </para>
    /// </remarks>
    void BuildAccelerationStructure(
        AccelerationStructureHandle target,
        in AccelerationStructureBuildInput input,
        BufferHandle scratch,
        long scratchOffset = 0
    );

    // ── Transfers and synchronisation ───────────────────────────────────────────────────────

    /// <summary>States that resources are changing what they are used for.</summary>
    /// <param name="barriers">Everything changing at this point.</param>
    /// <remarks>
    ///     Grouped, because a driver given ten barriers together inserts one stall and given them
    ///     one at a time inserts ten — and the backend cannot batch them for us, since by the time
    ///     it sees the second the first has been recorded.
    /// </remarks>
    void Barrier(in BarrierGroup barriers);

    /// <summary>Copies between buffers.</summary>
    /// <param name="source">The source.</param>
    /// <param name="sourceOffset">Where to read from, in bytes.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="destinationOffset">Where to write to, in bytes.</param>
    /// <param name="size">How many bytes.</param>
    void CopyBuffer(
        BufferHandle source,
        long sourceOffset,
        BufferHandle destination,
        long destinationOffset,
        long size
    );

    /// <summary>Copies from a buffer into a texture.</summary>
    /// <param name="source">The source.</param>
    /// <param name="sourceOffset">Where to read from, in bytes.</param>
    /// <param name="destination">Which part of which texture to write.</param>
    /// <param name="size">The region's size in texels.</param>
    void CopyBufferToTexture(BufferHandle source, long sourceOffset, in TextureRegion destination, Int3 size);

    /// <summary>Copies from a texture into a buffer.</summary>
    /// <param name="source">Which part of which texture to read.</param>
    /// <param name="size">The region's size in texels.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="destinationOffset">Where to write to, in bytes.</param>
    void CopyTextureToBuffer(in TextureRegion source, Int3 size, BufferHandle destination, long destinationOffset);

    /// <summary>Copies between textures.</summary>
    /// <param name="source">Which part of which texture to read.</param>
    /// <param name="destination">Which part of which texture to write.</param>
    /// <param name="size">The region's size in texels.</param>
    void CopyTexture(in TextureRegion source, in TextureRegion destination, Int3 size);

    // ── Queries ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Puts a range of a pool's queries back into the state a write expects.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="first">The first query to reset.</param>
    /// <param name="count">How many.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not optional, and not something a backend can do for you.</b> Vulkan makes
    ///         writing a timestamp into a query that has not been reset undefined behaviour, and the
    ///         reset is itself a command — it happens where it is recorded rather than when it is
    ///         called, so a pool reset from the host between submissions would be racing the frame
    ///         still reading it. Reset the range at the top of the list that writes it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Outside a render pass.</b> A reset is a transfer-shaped operation and is not
    ///         allowed inside one, which is the same rule <see cref="CopyBuffer" /> follows.
    ///     </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    ///     The device does not report <see cref="GraphicsDeviceFeatures.HasTimestampQueries" />.
    /// </exception>
    void ResetQueries(QueryPoolHandle pool, int first, int count);

    /// <summary>Writes the GPU clock into one of a pool's queries.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="index">Which query.</param>
    /// <remarks>
    ///     <para>
    ///         Allowed inside a render pass, which is the point: a pass's cost is the difference
    ///         between a write before its first draw and a write after its last, and a timestamp that
    ///         had to be taken outside the pass would measure the pass plus whatever the driver does
    ///         at its boundaries.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It records "when everything before this had finished", not "when this line ran".</b>
    ///         The write is bottom-of-pipe, so two timestamps around a draw bracket the draw's
    ///         completion rather than its issue — which is what makes a pair meaningful and also why
    ///         a pair around a single tiny draw on a deeply pipelined GPU can read as zero.
    ///     </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    ///     The device does not report <see cref="GraphicsDeviceFeatures.HasTimestampQueries" />.
    /// </exception>
    void WriteTimestamp(QueryPoolHandle pool, int index);

    // ── Debugging ───────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a named group in a capture.</summary>
    /// <param name="name">The group's name.</param>
    /// <remarks>
    ///     Not decoration. A RenderDoc capture of a real frame is thousands of calls, and the
    ///     difference between one with groups and one without is the difference between finding the
    ///     shadow pass in seconds and scrolling for ten minutes.
    /// </remarks>
    void PushDebugGroup(string name);

    /// <summary>Closes the innermost debug group.</summary>
    void PopDebugGroup();

    /// <summary>Marks a point in a capture.</summary>
    /// <param name="name">The marker's name.</param>
    void InsertDebugMarker(string name);
}
