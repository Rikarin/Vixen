// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net;
using Vixen.Net.Messaging;

namespace Vixen.Samples.Multiplayer;

/// <summary>The game's own messages, in the payload space the session leaves it.</summary>
/// <remarks>
///     <para>
///         There is exactly one, and it is the acknowledgement. <c>ReplicationServer</c> needs to be
///         told the newest tick a client applied cleanly — that is what advances the baseline, and
///         what makes the next snapshot a delta rather than the world — and the engine deliberately
///         does not define how it gets there. It is a message, the game already has a message
///         channel, and inventing a second one inside the engine would be a second thing to keep in
///         step with the first.
///     </para>
///     <para>
///         <b>Sequenced, not reliable.</b> An acknowledgement that is lost costs one snapshot's worth
///         of re-sending and is corrected by the next one a tick later; an acknowledgement that
///         arrives <i>late</i> would walk the baseline backwards and re-send what the client already
///         has. Sequenced is precisely "drop anything older than what I have already delivered",
///         which is the guarantee this needs and the one it does not pay for.
///     </para>
/// </remarks>
internal static class MatchProtocol {
    /// <summary>How big any of these get.</summary>
    public const int MaxBytes = 8;

    /// <summary>The channel acknowledgements travel on, and why is above.</summary>
    public const Channel AckChannel = Channel.Sequenced;

    const byte AcknowledgeOpcode = 1;

    /// <summary>Writes "I have applied everything up to here".</summary>
    /// <param name="tick">The newest tick that decoded cleanly.</param>
    /// <param name="buffer">Where to write it.</param>
    /// <param name="message">The message.</param>
    /// <returns>Whether it fit.</returns>
    public static bool TryWriteAcknowledgement(Tick tick, Span<byte> buffer, out ReadOnlySpan<byte> message) {
        var writer = new PacketWriter(buffer);
        writer.WriteByte(AcknowledgeOpcode);
        writer.WriteTick(tick);

        return writer.TryFinish(out message);
    }

    /// <summary>Reads one.</summary>
    /// <param name="payload">The bytes as they arrived.</param>
    /// <param name="tick">The tick they acknowledged.</param>
    /// <returns>Whether this was an acknowledgement and it decoded.</returns>
    public static bool TryReadAcknowledgement(ReadOnlySpan<byte> payload, out Tick tick) {
        tick = default;

        var reader = new PacketReader(payload);

        return reader.TryReadByte(out var opcode) && opcode == AcknowledgeOpcode && reader.TryReadTick(out tick);
    }
}
