// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net;

/// <summary>
///     The delivery guarantee a message is sent with.
/// </summary>
/// <remarks>
///     <para>
///         Four channels rather than two, because "reliable or not" is the wrong question twice over.
///         A chat line must arrive but does not care what order it arrives in relative to an
///         inventory update; a position update must not be applied after a newer one has already
///         been applied, but losing it costs nothing because another follows in 50 ms.
///     </para>
///     <para>
///         A transport that cannot honour a channel natively implements it — the guarantee is the
///         contract, not the mechanism. <c>Vixen.Net.Transport.Local</c> honours all four for free
///         because an in-process queue loses and reorders nothing.
///     </para>
/// </remarks>
public enum Channel : byte {
    /// <summary>
    ///     Arrives, exactly once, in the order it was sent. Retransmitted until acknowledged, and
    ///     held at the receiver until its predecessors arrive. The default, and the expensive one:
    ///     a single lost packet stalls everything behind it on this channel.
    /// </summary>
    Reliable = 0,

    /// <summary>
    ///     Arrives, exactly once, in whatever order it gets there. Retransmitted until acknowledged
    ///     but never held for a predecessor, so one loss delays one message rather than the queue
    ///     behind it. The right channel for independent events — a chat line, a pickup, an RPC whose
    ///     arguments do not depend on the last one.
    /// </summary>
    ReliableUnordered = 1,

    /// <summary>
    ///     May be lost, duplicated or reordered, and is never retransmitted. The channel for state
    ///     that supersedes itself: transforms, animation, anything where the next packet makes this
    ///     one irrelevant.
    /// </summary>
    Unreliable = 2,

    /// <summary>
    ///     May be lost, and is never retransmitted, but is never delivered out of order: a packet
    ///     older than one already delivered is dropped rather than applied. What
    ///     <see cref="Unreliable" /> wants to be for a value that must never go backwards.
    /// </summary>
    Sequenced = 3
}

/// <summary>What each <see cref="Channel" /> promises, asked rather than remembered.</summary>
/// <remarks>
///     These four predicates are the whole of the channel contract, and everything above the
///     transport — the reliability layer, the delta encoder, the simulation decorator — derives its
///     behaviour from them rather than from a <c>switch</c> of its own. A fifth channel would then
///     be one enum member and one line here.
/// </remarks>
public static class ChannelExtensions {
    /// <summary>Whether the channel promises the message arrives at all.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns><see langword="true" /> for the two reliable channels.</returns>
    public static bool IsReliable(this Channel channel) =>
        channel is Channel.Reliable or Channel.ReliableUnordered;

    /// <summary>Whether the channel promises messages are observed in the order they were sent.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>
    ///     <see langword="true" /> for <see cref="Channel.Reliable" /> and
    ///     <see cref="Channel.Sequenced" />. The two mean different things by it — one waits, the
    ///     other discards — but a receiver never sees an older message after a newer one either way.
    /// </returns>
    public static bool IsOrdered(this Channel channel) =>
        channel is Channel.Reliable or Channel.Sequenced;

    /// <summary>Whether a message on this channel may be silently dropped.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns><see langword="true" /> when the channel is not reliable.</returns>
    public static bool MayDrop(this Channel channel) => !channel.IsReliable();

    /// <summary>Whether a message on this channel may be delivered more than once.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>
    ///     <see langword="true" /> only for <see cref="Channel.Unreliable" />. A reliable channel
    ///     deduplicates by acknowledgement and a sequenced one by sequence number, so a duplicate on
    ///     either is a defect in the layer rather than something the layer above must tolerate.
    /// </returns>
    public static bool MayDuplicate(this Channel channel) => channel is Channel.Unreliable;
}
