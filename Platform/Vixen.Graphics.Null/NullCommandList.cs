// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.Null;

/// <summary>A command list that records what it was told and does nothing with it.</summary>
/// <remarks>
///     <para>
///         Records into its own list and hands it to the shared
///         <see cref="CommandRecorder" /> at submit, rather than writing into the recorder as it
///         goes. That is not an optimisation — it is what keeps the stream meaningful: lists are
///         recorded on several threads at once, so interleaving them in real time would produce a
///         log whose order depended on the scheduler. Submission order is the order the GPU would
///         see, and it is the order a test should assert on.
///     </para>
///     <para>
///         The validation here is the part that earns its keep. A draw outside a render pass, a
///         nested pass, a submit before <see cref="Finish" /> — each is undefined behaviour on a real
///         backend and each is caught here, on a machine with no GPU, with a message saying what was
///         wrong.
///     </para>
/// </remarks>
sealed class NullCommandList(
    QueueKind kind,
    string name,
    bool hasDrawIndirectCount = false,
    bool hasRayTracing = false
) : ICommandList {
    readonly List<RecordedCommand> commands = [];

    int passDepth;
    int groupDepth;
    bool finished;
    bool disposed;

    public QueueKind Kind => kind;

    public bool IsRecorded => finished;

    public bool Submitted { get; private set; }

    /// <summary>What this list recorded, before it was submitted.</summary>
    public IReadOnlyList<RecordedCommand> Commands => commands;

    public void Finish() {
        ThrowIfDisposed();

        if (finished) {
            return;
        }

        if (passDepth != 0) {
            throw new InvalidOperationException(
                $"Command list '{name}' was finished inside a render pass. Every pass has to be ended "
                + "before the list is, or the backend has nothing to close it with."
            );
        }

        if (groupDepth != 0) {
            throw new InvalidOperationException(
                $"Command list '{name}' was finished with {groupDepth} debug group(s) still open, which "
                + "makes a capture unreadable from that point on."
            );
        }

        finished = true;
    }

    public void BeginRenderPass(in RenderPassDescription description) {
        ThrowIfRecording();

        if (passDepth > 0) {
            throw new InvalidOperationException(
                "Render passes do not nest. End the current one first — no API allows this, and a "
                + "tiled GPU could not express it at all."
            );
        }

        if (description.ColourAttachments.IsEmpty && description.DepthStencil is null) {
            throw new InvalidOperationException(
                $"Render pass '{description.Name}' has no attachments, so it renders to nothing."
            );
        }

        passDepth++;

        // ⚠ The render area is recorded, and it is not decoration. It is what a `LoadAction.Clear`
        // is confined by — a scissor confines draws, and the load op runs before any draw — so a
        // pass that owns one tile of a cached atlas and forgets to say so wipes every other tile in
        // the texture. That failure produces no error and no missing draw: it produces an atlas in
        // which only the last tile drawn holds real depth. Without the rectangle in the stream there
        // is nothing a test can hold the claim against.
        Add(
            new(
                RecordedCommandKind.BeginRenderPass,
                0,
                description.ColourAttachments.Length,
                description.DepthStencil is null ? 0 : 1,
                description.RenderArea?.X ?? 0,
                description.RenderArea?.Y ?? 0,
                description.RenderArea is { } area ? ((long)area.Width << 32) | (uint)area.Height : 0,
                description.Name
            )
        );
    }

    public void EndRenderPass() {
        ThrowIfRecording();

        if (passDepth == 0) {
            throw new InvalidOperationException("EndRenderPass without a matching BeginRenderPass.");
        }

        passDepth--;
        Add(new(RecordedCommandKind.EndRenderPass, 0));
    }

    public void SetViewport(in Viewport viewport) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.SetViewport, 0, (long)viewport.Width, (long)viewport.Height, (long)viewport.X, (long)viewport.Y));
    }

    public void SetScissor(in ScissorRect scissor) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.SetScissor, 0, scissor.Width, scissor.Height, scissor.X, scissor.Y));
    }

    public void SetBlendConstant(in Color4 colour) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.SetBlendConstant, 0));
    }

    public void SetStencilReference(uint reference) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.SetStencilReference, 0, reference));
    }

    public void BindPipeline(PipelineHandle pipeline) {
        ThrowIfRecording();

        if (!pipeline.IsValid) {
            throw new ArgumentException("A null pipeline was bound.", nameof(pipeline));
        }

        Add(new(RecordedCommandKind.BindPipeline, 0, (long)pipeline.Value.Packed));
    }

    public void BindDescriptorSet(
        DescriptorSetSlot slot,
        DescriptorSetHandle descriptors,
        ReadOnlySpan<uint> dynamicOffsets = default
    ) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.BindDescriptorSet, 0, (long)slot, (long)descriptors.Value.Packed, dynamicOffsets.Length));
    }

    public void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data) {
        ThrowIfRecording();

        // The first four bytes as well as the shape. A push is the one call whose *payload* is the
        // whole point — a matrix at the right offset with the wrong contents draws a picture — and
        // four bytes is every scalar anyone pushes. Zero for a shorter payload, which nothing writes.
        var head = data.Length >= sizeof(uint) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data) : 0u;

        Add(new(RecordedCommandKind.PushConstants, 0, (long)stages, offset, data.Length, head));
    }

    public void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.BindVertexBuffer, 0, slot, (long)buffer.Value.Packed, offset));
    }

    public void BindIndexBuffer(BufferHandle buffer, IndexFormat format = IndexFormat.UInt16, long offset = 0) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.BindIndexBuffer, 0, (long)buffer.Value.Packed, (long)format, offset));
    }

    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) {
        ThrowIfNotDrawing(nameof(Draw));
        Add(new(RecordedCommandKind.Draw, 0, vertexCount, instanceCount, firstVertex, firstInstance));
    }

    public void DrawIndexed(
        int indexCount,
        int instanceCount = 1,
        int firstIndex = 0,
        int vertexOffset = 0,
        int firstInstance = 0
    ) {
        ThrowIfNotDrawing(nameof(DrawIndexed));
        Add(new(RecordedCommandKind.DrawIndexed, 0, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance));
    }

    public void DrawIndexedIndirect(BufferHandle arguments, long offset = 0, int drawCount = 1, int stride = 20) {
        ThrowIfNotDrawing(nameof(DrawIndexedIndirect));
        Add(new(RecordedCommandKind.DrawIndexedIndirect, 0, (long)arguments.Value.Packed, offset, drawCount, stride));
    }

    /// <remarks>
    ///     The capability is checked here rather than left to a real backend, on the same terms as
    ///     the unbounded-binding refusal in <c>NullDevice.CreateDescriptorSetLayout</c>: a host that
    ///     skipped its check finds out in a test rather than on whichever driver it reaches first.
    ///     ⚠ Five arguments fit in a recorded command and this call takes six, so the count buffer's
    ///     offset is the one left out — it is four-byte-aligned bookkeeping, where the two handles,
    ///     the argument offset and the ceiling are what an assertion is actually about.
    /// </remarks>
    public void DrawIndexedIndirectCount(
        BufferHandle arguments,
        BufferHandle count,
        long offset = 0,
        long countOffset = 0,
        int maxDrawCount = 1,
        int stride = 20
    ) {
        ThrowIfNotDrawing(nameof(DrawIndexedIndirectCount));

        if (!hasDrawIndirectCount) {
            throw new InvalidOperationException(
                "DrawIndexedIndirectCount needs GraphicsDeviceFeatures.HasDrawIndirectCount. This "
                + "device reports it absent, and the fallback is DrawIndexedIndirect at the run's "
                + "maximum length with the culled arguments zeroed."
            );
        }

        Add(
            new(
                RecordedCommandKind.DrawIndexedIndirectCount,
                0,
                (long)arguments.Value.Packed,
                (long)count.Value.Packed,
                offset,
                maxDrawCount,
                stride
            )
        );
    }

    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1) {
        ThrowIfRecording();

        if (passDepth > 0) {
            throw new InvalidOperationException(
                "Dispatch inside a render pass. Compute work belongs between passes — no API allows it "
                + "inside one, and a tiled GPU would have to resolve the tile to run it."
            );
        }

        Add(new(RecordedCommandKind.Dispatch, 0, groupsX, groupsY, groupsZ));
    }

    public void DispatchIndirect(BufferHandle arguments, long offset = 0) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.DispatchIndirect, 0, (long)arguments.Value.Packed, offset));
    }

    /// <remarks>
    ///     The capability is checked here on the DrawIndexedIndirectCount terms — a host that
    ///     skipped its check finds out in a test rather than on a driver — but with the exception
    ///     type the device's own refusals use, so a caller catches the same thing whichever end of
    ///     the API it asked first. ⚠ Five argument slots again: the primitive count is recorded
    ///     rather than the raw geometry handles, because "how much was built" is what an assertion
    ///     is about and the input record is what <c>GetAccelerationStructureSizes</c> already
    ///     validated.
    /// </remarks>
    public void BuildAccelerationStructure(
        AccelerationStructureHandle target,
        in AccelerationStructureBuildInput input,
        BufferHandle scratch,
        long scratchOffset = 0
    ) {
        ThrowIfRecording();

        if (!hasRayTracing) {
            throw new NotSupportedException(
                "BuildAccelerationStructure needs GraphicsDeviceFeatures.HasRayTracing. This device "
                + "reports it absent — ask the capability and take the distance-field tracer."
            );
        }

        if (passDepth > 0) {
            throw new InvalidOperationException(
                "An acceleration-structure build inside a render pass. A build sits with the "
                + "dispatches, between passes — no API allows it inside one."
            );
        }

        var primitives = input.Kind == AccelerationStructureKind.TopLevel
            ? input.Instances.Count
            : input.Triangles.IndexCount / 3;

        Add(
            new(
                RecordedCommandKind.BuildAccelerationStructure,
                0,
                (long)target.Value.Packed,
                (long)input.Kind,
                primitives,
                (long)scratch.Value.Packed,
                scratchOffset
            )
        );
    }

    public void Barrier(in BarrierGroup barriers) {
        ThrowIfRecording();

        if (passDepth > 0) {
            throw new InvalidOperationException(
                "A barrier inside a render pass. The transitions a pass needs are declared by its "
                + "attachments' load and store actions; a barrier here would split the pass."
            );
        }

        if (barriers.IsEmpty) {
            return;
        }

        Add(new(RecordedCommandKind.Barrier, 0, barriers.Buffers.Length, barriers.Textures.Length));
    }

    public void CopyBuffer(
        BufferHandle source,
        long sourceOffset,
        BufferHandle destination,
        long destinationOffset,
        long size
    ) {
        ThrowIfCopying();

        if (source == destination) {
            throw new ArgumentException(
                "A buffer was copied onto itself. Overlapping copies are undefined on every API.",
                nameof(destination)
            );
        }

        Add(
            new(
                RecordedCommandKind.CopyBuffer,
                0,
                (long)source.Value.Packed,
                sourceOffset,
                (long)destination.Value.Packed,
                destinationOffset,
                size
            )
        );
    }

    public void CopyBufferToTexture(BufferHandle source, long sourceOffset, in TextureRegion destination, Int3 size) {
        ThrowIfCopying();
        Add(new(RecordedCommandKind.CopyBufferToTexture, 0, (long)source.Value.Packed, sourceOffset, (long)destination.Texture.Value.Packed, destination.MipLevel, size.X));
    }

    public void CopyTextureToBuffer(in TextureRegion source, Int3 size, BufferHandle destination, long destinationOffset) {
        ThrowIfCopying();
        Add(new(RecordedCommandKind.CopyTextureToBuffer, 0, (long)source.Texture.Value.Packed, source.MipLevel, (long)destination.Value.Packed, destinationOffset, size.X));
    }

    public void CopyTexture(in TextureRegion source, in TextureRegion destination, Int3 size) {
        ThrowIfCopying();
        Add(new(RecordedCommandKind.CopyTexture, 0, (long)source.Texture.Value.Packed, source.MipLevel, (long)destination.Texture.Value.Packed, destination.MipLevel, size.X));
    }

    public void ResetQueries(QueryPoolHandle pool, int first, int count) {
        ThrowIfCopying();

        if (count <= 0) {
            return;
        }

        Add(new(RecordedCommandKind.ResetQueries, 0, (long)pool.Value.Packed, first, count));
    }

    public void WriteTimestamp(QueryPoolHandle pool, int index) {
        // ⚠ Not ThrowIfCopying. A timestamp inside a render pass is the whole point — a pass's cost
        // is a pair around its draws — and it is one of the two commands (with a debug marker) that
        // every API allows there.
        ThrowIfRecording();
        Add(new(RecordedCommandKind.WriteTimestamp, 0, (long)pool.Value.Packed, index));
    }

    public void PushDebugGroup(string name) {
        ThrowIfRecording();
        groupDepth++;
        Add(new(RecordedCommandKind.PushDebugGroup, 0, Text: name));
    }

    public void PopDebugGroup() {
        ThrowIfRecording();

        if (groupDepth == 0) {
            throw new InvalidOperationException("PopDebugGroup without a matching PushDebugGroup.");
        }

        groupDepth--;
        Add(new(RecordedCommandKind.PopDebugGroup, 0));
    }

    public void InsertDebugMarker(string name) {
        ThrowIfRecording();
        Add(new(RecordedCommandKind.InsertDebugMarker, 0, Text: name));
    }

    public void Dispose() => disposed = true;

    internal void MarkSubmitted() => Submitted = true;

    internal void Flush(CommandRecorder? recorder) {
        if (recorder is null) {
            return;
        }

        foreach (var command in commands) {
            recorder.Record(command);
        }
    }

    void Add(RecordedCommand command) => commands.Add(command with { Sequence = commands.Count });

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    void ThrowIfRecording() {
        ThrowIfDisposed();

        if (finished) {
            throw new InvalidOperationException(
                $"Command list '{name}' was recorded into after Finish(). A finished list is immutable."
            );
        }
    }

    void ThrowIfNotDrawing(string operation) {
        ThrowIfRecording();

        if (passDepth == 0) {
            throw new InvalidOperationException(
                $"{operation} outside a render pass. There is no target to draw into, and no API allows it."
            );
        }
    }

    void ThrowIfCopying() {
        ThrowIfRecording();

        if (passDepth > 0) {
            throw new InvalidOperationException(
                "A copy inside a render pass, which no API allows — a tiled GPU would have to resolve "
                + "the tile to perform it."
            );
        }
    }
}
