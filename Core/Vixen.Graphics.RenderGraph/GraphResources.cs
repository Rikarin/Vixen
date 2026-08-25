// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph;

/// <summary>A texture the graph will provide, named before it exists.</summary>
/// <remarks>
///     An index into one graph's resource table, not a GPU object. It is meaningless to another
///     graph and meaningless after <see cref="RenderGraph.Reset" />, which is what the generation
///     catches — a handle kept across frames is a bug that would otherwise address whatever resource
///     took the slot.
/// </remarks>
/// <param name="Index">Which resource, from 1. Zero is no resource.</param>
/// <param name="Generation">Which build of the graph it belongs to.</param>
public readonly record struct GraphTexture(int Index, int Generation) {
    /// <summary>No texture.</summary>
    public static GraphTexture None => default;

    /// <summary>Whether this names anything.</summary>
    public bool IsValid => Index > 0;
}

/// <summary>A buffer the graph will provide, named before it exists.</summary>
/// <param name="Index">Which resource, from 1. Zero is no resource.</param>
/// <param name="Generation">Which build of the graph it belongs to.</param>
public readonly record struct GraphBuffer(int Index, int Generation) {
    /// <summary>No buffer.</summary>
    public static GraphBuffer None => default;

    /// <summary>Whether this names anything.</summary>
    public bool IsValid => Index > 0;
}

/// <summary>Which queue a pass's work belongs on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A claim about scheduling, not a description of the body.</b> Saying
///         <see cref="Compute" /> is saying "this may leave the graphics queue", and that is only
///         true of a pass whose every input and output the graph can see — because a wait edge is
///         derived from a declared read or write and from nothing else. A dispatch that writes a
///         pyramid, an atlas, a page table or a draw-argument buffer the graph was never told about
///         is a dispatch nothing can be made to wait for, and it declares <see cref="Graphics" />:
///         on one queue, declaration order is execution order, which is the ordering such a pass
///         was always relying on.
///     </para>
///     <para>
///         Seven of this tree's nodes are that shape and say so at their declaration. See
///         <c>docs/guide/rendering/async-compute.md</c> for the audit and the list.
///     </para>
/// </remarks>
public enum PassKind : byte {
    /// <summary>
    ///     Draws — and anything the graph cannot see the whole of. The default, and the only kind
    ///     that may have attachments.
    /// </summary>
    Graphics = 0,

    /// <summary>Dispatches, every resource of which is declared.</summary>
    Compute = 1,

    /// <summary>
    ///     Copies, and nothing else at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ A Vulkan transfer family accepts copies and refuses everything else — including a
    ///     barrier naming a shader stage, which is what any transition to or from
    ///     <see cref="ResourceState.ShaderRead" /> is. A pass that copies into a texture and then
    ///     hands it to a shader is doing two things, and only one of them is a transfer.
    /// </remarks>
    Transfer = 2
}

/// <summary>One transition the graph decided a pass needs, before any of it is recorded.</summary>
/// <param name="Resource">Which resource, as an index into the graph's table from 0.</param>
/// <param name="Before">What it is being used as.</param>
/// <param name="After">What it is about to be used as.</param>
/// <param name="From">Which queue owns it. Equal to <paramref name="To" /> for most barriers.</param>
/// <param name="To">Which queue is to own it.</param>
/// <remarks>
///     A handover is planned as one of these and recorded as two — the release at the end of the
///     owning segment and the acquire in front of the pass that needs it — with identical states, as
///     Vulkan requires. Storing it once is what makes the two halves agree by construction.
/// </remarks>
readonly record struct PlannedBarrier(
    int Resource,
    ResourceState Before,
    ResourceState After,
    QueueKind From,
    QueueKind To
) {
    public bool TransfersOwnership => From != To;
}

/// <summary>How a pass touches a resource.</summary>
/// <param name="Texture">The texture, when it is one.</param>
/// <param name="Buffer">The buffer, when it is one.</param>
/// <param name="State">What the pass needs it to be in.</param>
/// <param name="IsWrite">Whether the pass writes it, which is what culling and ordering turn on.</param>
readonly record struct ResourceUse(
    GraphTexture Texture,
    GraphBuffer Buffer,
    ResourceState State,
    bool IsWrite
) {
    public bool IsTexture => Texture.IsValid;
}

/// <summary>One attachment a pass renders into.</summary>
/// <param name="Texture">What it renders into.</param>
/// <param name="Load">What happens to it at the start of the pass.</param>
/// <param name="Store">
///     What happens at the end, or <see langword="null" /> to let the graph decide — which is the
///     point of declaring attachments here rather than building a
///     <see cref="RenderPassDescription" /> by hand.
/// </param>
/// <param name="ClearColour">What to clear to.</param>
/// <param name="ClearDepth">What to clear depth to. Zero is <em>far</em> under reversed-Z.</param>
/// <param name="ClearStencil">What to clear stencil to.</param>
/// <param name="IsDepth">Whether it is the depth-stencil attachment.</param>
/// <param name="ReadOnly">Whether a depth attachment is only tested, not written.</param>
/// <param name="Resolve">
///     Where a multisampled attachment is resolved to at the end of the pass, or
///     <see cref="GraphTexture.None" /> for an attachment that is not resolved.
/// </param>
/// <param name="ResolveMode">
///     Which sample a depth resolve keeps. Ignored for colour, which always averages.
/// </param>
readonly record struct GraphAttachment(
    GraphTexture Texture,
    LoadAction Load,
    StoreAction? Store,
    Color4 ClearColour,
    float ClearDepth,
    byte ClearStencil,
    bool IsDepth,
    bool ReadOnly,
    GraphTexture Resolve = default,
    DepthResolveMode ResolveMode = DepthResolveMode.Max
);

/// <summary>What the graph knows about one virtual resource.</summary>
sealed class GraphResource {
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required bool IsTexture { get; init; }

    /// <summary>Whether it came from outside the graph and outlives it.</summary>
    public required bool IsImported { get; init; }

    public TextureDescription TextureDescription { get; set; }

    public BufferDescription BufferDescription { get; set; }

    /// <summary>The imported resource, when there is one.</summary>
    public TextureHandle ImportedTexture { get; init; }

    public BufferHandle ImportedBuffer { get; init; }

    /// <summary>The imported view, when the importer had one to give.</summary>
    public TextureViewHandle ImportedView { get; init; }

    /// <summary>What state an imported resource is in when the graph receives it.</summary>
    public ResourceState EntryState { get; init; }

    /// <summary>
    ///     What state an imported resource must be left in.
    /// </summary>
    /// <remarks>
    ///     The whole reason importing takes two states. A swapchain image handed back in
    ///     <c>ColourTarget</c> rather than <c>Present</c> is a validation error at present time, a
    ///     long way from the graph that caused it.
    /// </remarks>
    public ResourceState ExitState { get; init; }

    // ── Filled in by Compile ────────────────────────────────────────────────────────────────

    /// <summary>The first surviving pass that touches it, or <c>-1</c>.</summary>
    public int FirstUse { get; set; } = -1;

    /// <summary>The last surviving pass that touches it, or <c>-1</c>.</summary>
    public int LastUse { get; set; } = -1;

    /// <summary>How many surviving passes read it.</summary>
    public int ReadCount { get; set; }

    /// <summary>Whether any surviving pass writes it.</summary>
    public bool IsWritten { get; set; }

    /// <summary>Whether one queue owns it at a time, or several may use it at once.</summary>
    /// <remarks>
    ///     <para>
    ///         Decided by <c>PlanSharing</c> for a transient, because the graph is what creates one
    ///         and is therefore the only thing that can ask for a different resource. Taken from the
    ///         importer's own description for an import, because the graph did not make it and
    ///         cannot change what it was made as — asking for a handover-free read of a resource
    ///         created <see cref="ResourceSharing.Exclusive" /> would be reading memory nobody
    ///         handed over.
    ///     </para>
    /// </remarks>
    public ResourceSharing Sharing { get; set; }

    /// <summary>The distinct queues that read it, as a set of two bits.</summary>
    /// <remarks>Filled by <c>PlanSharing</c> from the queues the schedule assigned.</remarks>
    public int ReaderQueues { get; set; }

    /// <summary>The distinct queues that write it.</summary>
    public int WriterQueues { get; set; }

    /// <summary>What it is being used as, as the barrier plan walks the passes.</summary>
    public ResourceState CurrentState { get; set; }

    /// <summary>Which queue owns it, as the barrier plan walks the passes.</summary>
    /// <remarks>
    ///     Graphics at the start of every frame, which is a claim about the caller as much as about
    ///     the graph: whatever handed an import in did so from the graphics queue, and the graph
    ///     hands every import back to it before the frame ends so that next frame's claim is true
    ///     again.
    /// </remarks>
    public QueueKind CurrentQueue { get; set; }

    /// <summary>Which segment last touched it, so a release knows which list it belongs at the end of.</summary>
    public int CurrentSegment { get; set; }

    /// <summary>The last segment to write it, and the segments that have read it since.</summary>
    /// <remarks>
    ///     What the cross-queue waits are derived from. A read after a write on another queue, a write
    ///     after a read on another queue, and a write after a write on another queue are all hazards a
    ///     barrier cannot fix on its own — a barrier orders one queue against itself, and two queues
    ///     need something that spans them.
    /// </remarks>
    public int LastWriteSegment { get; set; } = -1;

    public List<int> ReaderSegments { get; } = [];

    /// <summary>The physical texture it was given, for a realised transient or an import.</summary>
    public TextureHandle Texture { get; set; }

    public TextureViewHandle View { get; set; }

    public BufferHandle Buffer { get; set; }

    /// <summary>Which pool entry it borrowed, so it can be given back at its last use.</summary>
    public int PoolSlot { get; set; } = -1;
}
