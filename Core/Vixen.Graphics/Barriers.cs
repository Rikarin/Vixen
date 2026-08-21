// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>A buffer changing what it is being used for.</summary>
/// <param name="Buffer">The buffer.</param>
/// <param name="Before">What was using it.</param>
/// <param name="After">What is about to.</param>
/// <param name="SourceQueue">Which queue owns it now.</param>
/// <param name="DestinationQueue">Which queue is to own it next.</param>
/// <remarks>
///     The two queues are equal in every barrier that is not a cross-queue handover, and equal is
///     the default — see <see cref="TextureBarrier" /> for what stating two different ones means.
/// </remarks>
public readonly record struct BufferBarrier(
    BufferHandle Buffer,
    ResourceState Before,
    ResourceState After,
    QueueKind SourceQueue = QueueKind.Graphics,
    QueueKind DestinationQueue = QueueKind.Graphics
) {
    /// <summary>Whether this barrier hands the buffer from one queue to another.</summary>
    public bool TransfersOwnership => SourceQueue != DestinationQueue;
}

/// <summary>A texture changing what it is being used for, and therefore its layout.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="Before">What was using it.</param>
/// <param name="After">What is about to.</param>
/// <param name="BaseMipLevel">The first mip level the barrier covers.</param>
/// <param name="MipLevelCount">How many levels, or <c>0</c> for all of them.</param>
/// <param name="BaseArrayLayer">The first array layer the barrier covers.</param>
/// <param name="ArrayLayerCount">How many layers, or <c>0</c> for all of them.</param>
/// <param name="SourceQueue">Which queue owns it now.</param>
/// <param name="DestinationQueue">Which queue is to own it next.</param>
/// <remarks>
///     <para>
///         Subresource ranges are here rather than implied, because the common case in a real renderer
///         is transitioning one mip of a chain — generating mips, or reading level <c>n</c> while writing
///         level <c>n+1</c> — and a barrier that covers the whole texture there serialises work that did
///         not need serialising.
///     </para>
///     <para>
///         <b>Two different queues make this an ownership transfer</b>, and an ownership transfer is
///         <em>two</em> barriers, not one: the same barrier is recorded a second time on the
///         destination queue's list, and the contents of a resource whose release was recorded
///         without its matching acquire are undefined. Recording only one half is the shape of
///         corruption that reproduces on one vendor and not another — a driver that happens to leave
///         the memory alone looks correct forever.
///         <c>Vixen.Graphics.RenderGraph</c> emits both halves; hand-written code has to do it in
///         pairs.
///     </para>
///     <para>
///         ⚠ <b>Equal queues mean no transfer at all, and that is the default.</b> A backend with one
///         queue, or one whose two <see cref="QueueKind" />s land on the same hardware family,
///         records exactly what it records today — which is what makes an async-scheduled frame and a
///         single-queue frame the same frame.
///     </para>
/// </remarks>
public readonly record struct TextureBarrier(
    TextureHandle Texture,
    ResourceState Before,
    ResourceState After,
    int BaseMipLevel = 0,
    int MipLevelCount = 0,
    int BaseArrayLayer = 0,
    int ArrayLayerCount = 0,
    QueueKind SourceQueue = QueueKind.Graphics,
    QueueKind DestinationQueue = QueueKind.Graphics
) {
    /// <summary>Whether this barrier hands the texture from one queue to another.</summary>
    public bool TransfersOwnership => SourceQueue != DestinationQueue;
}

/// <summary>Everything that changes state at one point in a command list.</summary>
/// <remarks>
///     <para>
///         Submitted as a group rather than one at a time, and that is the whole point: a driver
///         given ten barriers together inserts one pipeline stall, and given them one at a time
///         inserts ten. Batching is not an optimisation the backend can do for us either — by the
///         time it sees the second barrier the first has already been recorded.
///     </para>
///     <para>
///         <c>Vixen.Graphics.RenderGraph</c> builds these automatically for the code that wants that;
///         hand-written barriers stay available for the hot paths that do not.
///     </para>
/// </remarks>
/// <param name="buffers">The buffers changing state.</param>
/// <param name="textures">The textures changing state.</param>
public readonly ref struct BarrierGroup(
    ReadOnlySpan<BufferBarrier> buffers,
    ReadOnlySpan<TextureBarrier> textures
) {
    /// <summary>The buffers changing state.</summary>
    public ReadOnlySpan<BufferBarrier> Buffers { get; } = buffers;

    /// <summary>The textures changing state.</summary>
    public ReadOnlySpan<TextureBarrier> Textures { get; } = textures;

    /// <summary>Whether the group would do anything.</summary>
    public bool IsEmpty => Buffers.IsEmpty && Textures.IsEmpty;
}
