// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio.Codecs;
using Vixen.Net;
using Vixen.Net.Sessions;

namespace Vixen.Samples.VoiceChat;

/// <summary>The four lines that join <c>Vixen.Audio.Codecs</c> to <c>Vixen.Net</c>.</summary>
/// <remarks>
///     <para>
///         <b>Neither side knows about the other, and this is the whole of the joining.</b>
///         <see cref="VoiceSender" /> produces packets and knows nothing about a socket;
///         <see cref="VoiceReceiver" /> consumes them and knows nothing about a session. What is left
///         is a header and a channel choice, which is what this file is.
///     </para>
///     <para>
///         <b><see cref="Channel.Sequenced" /> and not <see cref="Channel.Reliable" />.</b> Voice
///         that arrives late is worse than voice that never arrives: a retransmitted packet turns up
///         after its moment has passed and the jitter buffer drops it anyway, having stalled
///         everything behind it in the meantime. Sequenced may be lost, is never retransmitted, and
///         is never delivered out of order — which is exactly the promise a talker needs.
///     </para>
/// </remarks>
static class VoiceLink {
    /// <summary>Sequence, then timestamp. Six bytes in front of the Opus.</summary>
    public const int HeaderBytes = 6;

    /// <summary>The same plus one byte saying who is talking, which the server adds on the way out.</summary>
    public const int RelayHeaderBytes = HeaderBytes + 1;

    /// <summary>Enough for a header and the largest packet Opus defines.</summary>
    public const int MaxBytes = RelayHeaderBytes + OpusPacketEncoder.MaxPacketBytes;

    /// <summary>Writes a packet a client sends to the server.</summary>
    public static int Write(Span<byte> destination, in VoicePacketHeader header, ReadOnlySpan<byte> packet) {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, header.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], header.Timestamp);
        packet.CopyTo(destination[HeaderBytes..]);
        return HeaderBytes + packet.Length;
    }

    /// <summary>Reads one back.</summary>
    public static bool Read(ReadOnlySpan<byte> payload, out VoicePacketHeader header, out ReadOnlySpan<byte> packet) {
        if (payload.Length <= HeaderBytes) {
            header = default;
            packet = default;
            return false;
        }

        header = new VoicePacketHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[2..])
        );

        packet = payload[HeaderBytes..];
        return true;
    }

    /// <summary>
    ///     Stamps a client's packet with who sent it and passes it on, without decoding a thing.
    /// </summary>
    /// <remarks>
    ///     <b>The server is a relay and not a mixer.</b> Decoding every talker and re-encoding one
    ///     stream per listener would cost the server a codec per player per player, and would take
    ///     away the thing that makes voice worth positioning — a client that receives each talker
    ///     separately can place them in the world, duck them individually, and put one of them
    ///     underwater. Forwarding bytes costs the server nothing but bandwidth.
    /// </remarks>
    public static int Relay(Span<byte> destination, PlayerId from, ReadOnlySpan<byte> payload) {
        destination[0] = (byte)from.Value;
        payload.CopyTo(destination[1..]);
        return payload.Length + 1;
    }

    /// <summary>Reads a relayed packet.</summary>
    public static bool ReadRelayed(
        ReadOnlySpan<byte> payload,
        out byte from,
        out VoicePacketHeader header,
        out ReadOnlySpan<byte> packet
    ) {
        from = 0;

        if (payload.Length <= RelayHeaderBytes) {
            header = default;
            packet = default;
            return false;
        }

        from = payload[0];
        return Read(payload[1..], out header, out packet);
    }
}
