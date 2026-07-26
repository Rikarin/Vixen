// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>One barrier, as the graph actually emitted it.</summary>
/// <param name="Texture">The texture, when it is one.</param>
/// <param name="Buffer">The buffer, when it is one.</param>
/// <param name="Before">What the graph claimed it was.</param>
/// <param name="After">What the graph moved it to.</param>
/// <param name="Group">Which <see cref="BarrierGroup" /> it arrived in, from 0.</param>
public readonly record struct ObservedBarrier(
    TextureHandle Texture,
    BufferHandle Buffer,
    ResourceState Before,
    ResourceState After,
    int Group
);

/// <summary>A render pass, as the graph actually began it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Colour">Its colour attachments, in order.</param>
/// <param name="Depth">Its depth attachment, if any.</param>
public readonly record struct ObservedPass(
    string Name,
    ColourAttachment[] Colour,
    DepthStencilAttachment? Depth
);

/// <summary>
///     A command list that records what the graph asked for and does nothing else.
/// </summary>
/// <remarks>
///     <para>
///         The Null backend records a command stream too, and it is the right tool for asserting
///         that a renderer emitted the right calls. It is the wrong tool <em>here</em>: it records a
///         barrier as a pair of counts, and the property this project has to establish is about the
///         barriers' contents — which resource, from which state, to which. So the graph's output is
///         captured in full and replayed.
///     </para>
///     <para>
///         Everything a pass body might record is accepted and dropped. That is deliberate: the tests
///         that care about what a pass draws use the Null backend, and mixing the two would make this
///         a second, worse implementation of it.
///     </para>
/// </remarks>
public sealed class TrackingCommandList : ICommandList {
    readonly List<ObservedBarrier> observed = [];
    readonly List<ObservedPass> passes = [];
    readonly List<string> order = [];

    /// <summary>Every barrier, flattened, in the order they were recorded.</summary>
    public IReadOnlyList<ObservedBarrier> Barriers => observed;

    /// <summary>Every render pass that began.</summary>
    public IReadOnlyList<ObservedPass> Passes => passes;

    /// <summary>How many <see cref="BarrierGroup" /> calls were made.</summary>
    public int BarrierGroups { get; private set; }

    /// <summary>A readable trace: barrier groups and passes, interleaved.</summary>
    public IReadOnlyList<string> Order => order;

    /// <inheritdoc />
    public QueueKind Kind => QueueKind.Graphics;

    /// <inheritdoc />
    public bool IsRecorded { get; private set; }

    /// <inheritdoc />
    public void Finish() => IsRecorded = true;

    /// <inheritdoc />
    public void BeginRenderPass(in RenderPassDescription description) {
        passes.Add(new(description.Name, description.ColourAttachments.ToArray(), description.DepthStencil));
        order.Add($"pass {description.Name}");
    }

    /// <inheritdoc />
    public void EndRenderPass() { }

    /// <inheritdoc />
    public void Barrier(in BarrierGroup barriers) {
        if (barriers.IsEmpty) {
            return;
        }

        foreach (var barrier in barriers.Buffers) {
            observed.Add(new(default, barrier.Buffer, barrier.Before, barrier.After, BarrierGroups));
        }

        foreach (var barrier in barriers.Textures) {
            observed.Add(new(barrier.Texture, default, barrier.Before, barrier.After, BarrierGroups));
        }

        order.Add($"barrier x{barriers.Buffers.Length + barriers.Textures.Length}");
        BarrierGroups++;
    }

    /// <inheritdoc />
    public void SetViewport(in Viewport viewport) { }

    /// <inheritdoc />
    public void SetScissor(in ScissorRect scissor) { }

    /// <inheritdoc />
    public void SetBlendConstant(in Color4 colour) { }

    /// <inheritdoc />
    public void SetStencilReference(uint reference) { }

    /// <inheritdoc />
    public void BindPipeline(PipelineHandle pipeline) { }

    /// <inheritdoc />
    public void BindDescriptorSet(
        DescriptorSetSlot slot,
        DescriptorSetHandle descriptors,
        ReadOnlySpan<uint> dynamicOffsets = default
    ) { }

    /// <inheritdoc />
    public void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data) { }

    /// <inheritdoc />
    public void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0) { }

    /// <inheritdoc />
    public void BindIndexBuffer(BufferHandle buffer, IndexFormat format = IndexFormat.UInt16, long offset = 0) { }

    /// <inheritdoc />
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) { }

    /// <inheritdoc />
    public void DrawIndexed(
        int indexCount,
        int instanceCount = 1,
        int firstIndex = 0,
        int vertexOffset = 0,
        int firstInstance = 0
    ) { }

    /// <inheritdoc />
    public void DrawIndexedIndirect(BufferHandle arguments, long offset = 0, int drawCount = 1, int stride = 20) { }

    /// <inheritdoc />
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1) { }

    /// <inheritdoc />
    public void DispatchIndirect(BufferHandle arguments, long offset = 0) { }

    /// <inheritdoc />
    public void CopyBuffer(
        BufferHandle source,
        long sourceOffset,
        BufferHandle destination,
        long destinationOffset,
        long size
    ) { }

    /// <inheritdoc />
    public void CopyBufferToTexture(
        BufferHandle source,
        long sourceOffset,
        in TextureRegion destination,
        Int3 size
    ) { }

    /// <inheritdoc />
    public void CopyTextureToBuffer(
        in TextureRegion source,
        Int3 size,
        BufferHandle destination,
        long destinationOffset
    ) { }

    /// <inheritdoc />
    public void CopyTexture(in TextureRegion source, in TextureRegion destination, Int3 size) { }

    /// <inheritdoc />
    public void PushDebugGroup(string name) { }

    /// <inheritdoc />
    public void PopDebugGroup() { }

    /// <inheritdoc />
    public void InsertDebugMarker(string name) { }

    /// <inheritdoc />
    public void Dispose() { }
}
