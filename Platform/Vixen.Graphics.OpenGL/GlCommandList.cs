// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>A command list that records into managed memory and replays on the GL thread.</summary>
/// <remarks>
///     <para>
///         Everything here is a struct write. No GL call is made while recording, no GL object is
///         touched, and nothing needs a current context — which is what lets the RHI keep saying
///         "recording is safe on any thread" on a backend where it plainly is not.
///     </para>
///     <para>
///         Validation that does not need a device happens here rather than at replay, because a
///         throw at the call site names the caller and a throw at replay names the submit.
///     </para>
/// </remarks>
sealed class GlCommandList(GlDevice device, QueueKind kind, string name) : ICommandList {
    readonly GlCommandRecorder recorder = new();

    bool inPass;
    bool disposed;

    /// <inheritdoc />
    public QueueKind Kind => kind;

    /// <inheritdoc />
    public bool IsRecorded { get; private set; }

    /// <summary>Whether this list has been submitted.</summary>
    public bool Submitted { get; private set; }

    /// <summary>A name for the debugger.</summary>
    public string Name => name;

    /// <summary>What was recorded, for the replay and for tests.</summary>
    public GlCommandRecorder Recorder => recorder;

    /// <inheritdoc />
    public void Finish() {
        if (IsRecorded) {
            throw new InvalidOperationException($"Command list '{name}' was finished twice.");
        }

        if (inPass) {
            throw new InvalidOperationException(
                $"Command list '{name}' was finished inside a render pass. Every backend leaves the "
                + "attachments in an undefined state when this happens; catching it here means catching "
                + "it in the same stack frame that opened the pass."
            );
        }

        IsRecorded = true;
    }

    /// <summary>Marks the list submitted, so a second submission is caught.</summary>
    public void MarkSubmitted() => Submitted = true;

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void BeginRenderPass(in RenderPassDescription description) {
        Recording();

        if (inPass) {
            throw new InvalidOperationException(
                "Render passes do not nest. Vulkan and D3D12 both forbid it and GL has no way to "
                + "express it, so a nested pass would be three different wrong things."
            );
        }

        Span<GlAttachment> recorded = stackalloc GlAttachment[
            description.ColourAttachments.Length + (description.DepthStencil is null ? 0 : 1)
        ];

        for (var index = 0; index < description.ColourAttachments.Length; index++) {
            var attachment = description.ColourAttachments[index];

            recorded[index] = new(
                attachment.View,
                attachment.Load,
                attachment.Store,
                attachment.ClearColour,
                0f,
                0,
                false,
                false
            );
        }

        if (description.DepthStencil is { } depth) {
            recorded[^1] = new(
                depth.View,
                depth.DepthLoad,
                depth.DepthStore,
                default,
                depth.ClearDepth,
                depth.ClearStencil,
                true,
                depth.IsReadOnly
            );
        }

        var (index0, count) = recorder.AddAttachments(recorded);

        recorder.Add(new() {
            Kind = GlCommandKind.BeginRenderPass,
            PayloadIndex = index0,
            PayloadCount = count,
            Int0 = description.ColourAttachments.Length,
            Int1 = recorder.AddName(description.Name)
        });

        inPass = true;
    }

    /// <inheritdoc />
    public void EndRenderPass() {
        Recording();

        if (!inPass) {
            throw new InvalidOperationException("EndRenderPass without a pass.");
        }

        recorder.Add(new() { Kind = GlCommandKind.EndRenderPass });
        inPass = false;
    }

    // ── State ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void SetViewport(in Viewport viewport) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.SetViewport,
            Float0 = viewport.X,
            Float1 = viewport.Y,
            Float2 = viewport.Width,
            Float3 = viewport.Height,
            Float4 = viewport.MinDepth,
            Float5 = viewport.MaxDepth
        });
    }

    /// <inheritdoc />
    public void SetScissor(in ScissorRect scissor) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.SetScissor,
            Int0 = scissor.X,
            Int1 = scissor.Y,
            Int2 = scissor.Width,
            Int3 = scissor.Height
        });
    }

    /// <inheritdoc />
    public void SetBlendConstant(in Color4 colour) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.SetBlendConstant,
            Float0 = colour.R,
            Float1 = colour.G,
            Float2 = colour.B,
            Float3 = colour.A
        });
    }

    /// <inheritdoc />
    public void SetStencilReference(uint reference) {
        Recording();
        recorder.Add(new() { Kind = GlCommandKind.SetStencilReference, Uint0 = reference });
    }

    /// <inheritdoc />
    public void BindPipeline(PipelineHandle pipeline) {
        Recording();
        recorder.Add(new() { Kind = GlCommandKind.BindPipeline, Pipeline = pipeline });
    }

    /// <inheritdoc />
    public void BindDescriptorSet(
        DescriptorSetSlot slot,
        DescriptorSetHandle descriptors,
        ReadOnlySpan<uint> dynamicOffsets = default
    ) {
        Recording();
        var (index, count) = recorder.AddUInts(dynamicOffsets);

        recorder.Add(new() {
            Kind = GlCommandKind.BindDescriptorSet,
            Int0 = (int)slot,
            Descriptors = descriptors,
            PayloadIndex = index,
            PayloadCount = count
        });
    }

    /// <inheritdoc />
    public void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data) {
        Recording();

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var (index, count) = recorder.AddBytes(data);

        recorder.Add(new() {
            Kind = GlCommandKind.PushConstants,
            Int0 = offset,
            Int1 = (int)stages,
            PayloadIndex = index,
            PayloadCount = count
        });
    }

    /// <inheritdoc />
    public void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.BindVertexBuffer,
            Int0 = slot,
            Buffer0 = buffer,
            Long0 = offset
        });
    }

    /// <inheritdoc />
    public void BindIndexBuffer(BufferHandle buffer, IndexFormat format = IndexFormat.UInt16, long offset = 0) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.BindIndexBuffer,
            Int0 = (int)format,
            Buffer0 = buffer,
            Long0 = offset
        });
    }

    // ── Drawing ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) {
        Recording();
        InPass(nameof(Draw));

        recorder.Add(new() {
            Kind = GlCommandKind.Draw,
            Int0 = vertexCount,
            Int1 = instanceCount,
            Int2 = firstVertex,
            Int3 = firstInstance
        });
    }

    /// <inheritdoc />
    public void DrawIndexed(
        int indexCount,
        int instanceCount = 1,
        int firstIndex = 0,
        int vertexOffset = 0,
        int firstInstance = 0
    ) {
        Recording();
        InPass(nameof(DrawIndexed));

        recorder.Add(new() {
            Kind = GlCommandKind.DrawIndexed,
            Int0 = indexCount,
            Int1 = instanceCount,
            Int2 = firstIndex,
            Int3 = vertexOffset,
            Uint0 = (uint)firstInstance
        });
    }

    /// <inheritdoc />
    public void DrawIndexedIndirect(BufferHandle arguments, long offset = 0, int drawCount = 1, int stride = 20) {
        Recording();
        InPass(nameof(DrawIndexedIndirect));

        recorder.Add(new() {
            Kind = GlCommandKind.DrawIndexedIndirect,
            Buffer0 = arguments,
            Long0 = offset,
            Int0 = drawCount,
            Int1 = stride
        });
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Refused rather than emulated. Reading the count back to the host and issuing that many
    ///     draws is the emulation, and it is a full pipeline stall in the middle of a frame — the
    ///     round trip this whole path exists to avoid. The honest fallback is the padded form, which
    ///     is what <c>GpuDrawArguments</c> writes when the capability is absent.
    /// </remarks>
    public void DrawIndexedIndirectCount(
        BufferHandle arguments,
        BufferHandle count,
        long offset = 0,
        long countOffset = 0,
        int maxDrawCount = 1,
        int stride = 20
    ) =>
        throw new NotSupportedException(
            "A draw whose count comes from a buffer needs glMultiDrawElementsIndirectCount, which is "
            + "core in GL 4.6 and this backend targets 4.5 at most — Features.HasDrawIndirectCount "
            + "reports false here at every profile. Issue DrawIndexedIndirect at the run's maximum "
            + "length with the culled arguments zeroed instead."
        );

    /// <inheritdoc />
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1) {
        Recording();

        if (inPass) {
            throw new InvalidOperationException(
                "A dispatch was recorded inside a render pass. Vulkan allows it and GL does not — "
                + "there is no compute stage inside a framebuffer's scope — so the RHI takes the "
                + "stricter of the two and this is rejected everywhere."
            );
        }

        recorder.Add(new() {
            Kind = GlCommandKind.Dispatch,
            Int0 = groupsX,
            Int1 = groupsY,
            Int2 = groupsZ
        });
    }

    /// <inheritdoc />
    public void DispatchIndirect(BufferHandle arguments, long offset = 0) {
        Recording();
        recorder.Add(new() { Kind = GlCommandKind.DispatchIndirect, Buffer0 = arguments, Long0 = offset });
    }

    /// <inheritdoc />
    public void BuildAccelerationStructure(
        AccelerationStructureHandle target,
        in AccelerationStructureBuildInput input,
        BufferHandle scratch,
        long scratchOffset = 0
    ) =>
        throw new NotSupportedException(
            "An acceleration-structure build was recorded on the OpenGL backend, which has no ray "
            + "tracing — see GlDevice.CreateAccelerationStructure for why. Ask "
            + "Features.HasRayTracing and take the distance-field tracer."
        );

    // ── Transfers and synchronisation ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Barrier(in BarrierGroup barriers) {
        Recording();

        if (barriers.IsEmpty) {
            return;
        }

        var (textures, textureCount) = recorder.AddTextureBarriers(barriers.Textures);
        var (buffers, bufferCount) = recorder.AddBufferBarriers(barriers.Buffers);

        recorder.Add(new() {
            Kind = GlCommandKind.Barrier,
            PayloadIndex = textures,
            PayloadCount = textureCount,
            Int0 = buffers,
            Int1 = bufferCount
        });
    }

    /// <inheritdoc />
    public void CopyBuffer(
        BufferHandle source,
        long sourceOffset,
        BufferHandle destination,
        long destinationOffset,
        long size
    ) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.CopyBuffer,
            Buffer0 = source,
            Buffer1 = destination,
            Long0 = sourceOffset,
            Long1 = destinationOffset,
            Long2 = size
        });
    }

    /// <inheritdoc />
    public void CopyBufferToTexture(
        BufferHandle source,
        long sourceOffset,
        in TextureRegion destination,
        Int3 size
    ) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.CopyBufferToTexture,
            Buffer0 = source,
            Long0 = sourceOffset,
            Texture0 = destination.Texture,
            Int0 = destination.MipLevel,
            Int1 = destination.ArrayLayer,
            Float0 = destination.Origin.X,
            Float1 = destination.Origin.Y,
            Float2 = destination.Origin.Z,
            Float3 = size.X,
            Float4 = size.Y,
            Float5 = size.Z
        });
    }

    /// <inheritdoc />
    public void CopyTextureToBuffer(
        in TextureRegion source,
        Int3 size,
        BufferHandle destination,
        long destinationOffset
    ) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.CopyTextureToBuffer,
            Buffer0 = destination,
            Long0 = destinationOffset,
            Texture0 = source.Texture,
            Int0 = source.MipLevel,
            Int1 = source.ArrayLayer,
            Float0 = source.Origin.X,
            Float1 = source.Origin.Y,
            Float2 = source.Origin.Z,
            Float3 = size.X,
            Float4 = size.Y,
            Float5 = size.Z
        });
    }

    /// <inheritdoc />
    public void CopyTexture(in TextureRegion source, in TextureRegion destination, Int3 size) {
        Recording();

        recorder.Add(new() {
            Kind = GlCommandKind.CopyTexture,
            Texture0 = source.Texture,
            Texture1 = destination.Texture,
            Int0 = source.MipLevel,
            Int1 = source.ArrayLayer,
            Int2 = destination.MipLevel,
            Int3 = destination.ArrayLayer,
            Long0 = Pack(source.Origin),
            Long1 = Pack(destination.Origin),
            Long2 = Pack(size)
        });
    }

    // ── Debugging ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void PushDebugGroup(string name) {
        Recording();
        recorder.Add(new() { Kind = GlCommandKind.PushDebugGroup, Int0 = recorder.AddName(name) });
    }

    /// <inheritdoc />
    public void PopDebugGroup() {
        Recording();
        recorder.Add(new() { Kind = GlCommandKind.PopDebugGroup });
    }

    /// <inheritdoc />
    public void InsertDebugMarker(string name) {
        Recording();
        recorder.Add(new() { Kind = GlCommandKind.InsertDebugMarker, Int0 = recorder.AddName(name) });
    }

    /// <inheritdoc />
    public void ResetQueries(QueryPoolHandle pool, int first, int count) =>
        throw new NotSupportedException(
            "The OpenGL backend has no timestamp queries — see GlDevice.CreateQueryPool for why."
        );

    /// <inheritdoc />
    public void WriteTimestamp(QueryPoolHandle pool, int index) =>
        throw new NotSupportedException(
            "The OpenGL backend has no timestamp queries — see GlDevice.CreateQueryPool for why."
        );

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        device.Return(this);
    }

    /// <summary>Puts the list back into a recordable state, for the pool.</summary>
    public void Rearm() {
        recorder.Reset();
        IsRecorded = false;
        Submitted = false;
        inPass = false;
        disposed = false;
    }

    /// <summary>Packs a three-component origin into one field.</summary>
    /// <remarks>
    ///     A texture-to-texture copy is the one command with three separate three-component vectors,
    ///     and widening the command struct by six ints for it would cost every other command the
    ///     memory. Twenty-one bits each is far more than any texture dimension GL will accept.
    /// </remarks>
    internal static long Pack(Int3 value) =>
        ((long)(value.X & 0x1FFFFF) << 42) | ((long)(value.Y & 0x1FFFFF) << 21) | (uint)(value.Z & 0x1FFFFF);

    /// <summary>Unpacks what <see cref="Pack" /> packed.</summary>
    internal static Int3 Unpack(long value) =>
        new((int)((value >> 42) & 0x1FFFFF), (int)((value >> 21) & 0x1FFFFF), (int)(value & 0x1FFFFF));

    void Recording() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsRecorded) {
            throw new InvalidOperationException(
                $"Command list '{name}' was recorded into after Finish()."
            );
        }
    }

    void InPass(string what) {
        if (!inPass) {
            throw new InvalidOperationException(
                $"{what} was recorded outside a render pass. GL would draw into whichever framebuffer "
                + "happened to be bound, which is the class of bug that renders into the last pass's "
                + "target and looks like a barrier problem."
            );
        }
    }
}
