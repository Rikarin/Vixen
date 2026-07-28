// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Net.Transport.Udp;

/// <summary>What a datagram is.</summary>
/// <remarks>
///     The numbers are the wire format: one is never reused for something else. Nothing but
///     <see cref="Message" /> carries a payload, and everything but the two handshake packets carries
///     the connection id the server assigned, so a datagram can be attributed before it is parsed.
/// </remarks>
enum UdpPacketKind : byte {
    /// <summary>Client to server: let me in. Carries a salt the accept has to echo.</summary>
    ConnectRequest = 1,

    /// <summary>Server to client: you are connection N.</summary>
    ConnectAccept = 2,

    /// <summary>Server to client: no.</summary>
    ConnectDenied = 3,

    /// <summary>Either way: this connection is over.</summary>
    Disconnect = 4,

    /// <summary>Either way: still here. Keeps the timeout at bay and the NAT mapping open.</summary>
    KeepAlive = 5,

    /// <summary>A fragment of one message on one channel.</summary>
    Message = 6,

    /// <summary>What has been received on one channel, so the sender can stop retransmitting it.</summary>
    Ack = 7,

    /// <summary>Server to client: prove you are at the address you say you are.</summary>
    ConnectChallenge = 8,

    /// <summary>Client to server: here is the proof.</summary>
    ConnectResponse = 9
}

/// <summary>Why a connection was refused or ended.</summary>
enum UdpDenyReason : byte {
    Unspecified = 0,
    Full = 1,
    Shutdown = 2,
    Kicked = 3,
    Timeout = 4
}

/// <summary>Reads and writes the datagram headers.</summary>
/// <remarks>
///     <para>
///         Little-endian, byte-aligned, and fixed-size per kind. Bit packing belongs above the
///         transport, where a snapshot knows what it is packing; down here every field is read by
///         code that has not decided yet whether the datagram is worth trusting, and a fixed offset
///         is what makes that cheap and hard to get wrong.
///     </para>
///     <para>
///         Every read is length-checked and returns a <see cref="bool" />. A datagram arrives from a
///         machine we do not control, and the first thing that touches it is the last place an
///         exception should be able to escape from.
///     </para>
/// </remarks>
static class UdpProtocol {
    /// <summary>The largest datagram this transport sends.</summary>
    /// <remarks>
    ///     1200 bytes, which fits inside the smallest path MTU anybody still has, with room for the
    ///     IPv6 and UDP headers. Discovering the real path MTU would buy a few percent and cost a
    ///     probe protocol; it is owed rather than guessed at.
    /// </remarks>
    public const int MaxDatagramBytes = 1200;

    /// <summary>The header on a <see cref="UdpPacketKind.Message" />.</summary>
    public const int MessageHeaderBytes = 12;

    /// <summary>How many payload bytes one datagram can carry.</summary>
    public const int MaxFragmentBytes = MaxDatagramBytes - MessageHeaderBytes;

    /// <summary>The most fragments one message may be split into.</summary>
    /// <remarks>
    ///     Fifty-six fragments of 1188 bytes is a little over 64 KiB, which is what
    ///     <see cref="TransportCapabilities.MaxPayloadBytes" /> promises — the same number the local
    ///     transport caps at, so a payload that works in a test works on a socket.
    /// </remarks>
    public const int MaxFragments = 56;

    /// <summary>The largest payload a caller may send.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>
    ///     How large a connection request is padded to.
    /// </summary>
    /// <remarks>
    ///     Padding is the point. The server answers a request with a challenge and allocates nothing,
    ///     so a forged request costs it one small datagram — and because the request is larger than
    ///     the answer, forging them cannot be used to make this server flood somebody else. A
    ///     handshake whose first reply is bigger than its first request is an amplifier.
    /// </remarks>
    public const int ConnectRequestBytes = 64;

    public static int WriteConnectRequest(Span<byte> buffer, uint salt) {
        buffer[..ConnectRequestBytes].Clear();
        buffer[0] = (byte)UdpPacketKind.ConnectRequest;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], salt);

        return ConnectRequestBytes;
    }

    public static int WriteConnectChallenge(Span<byte> buffer, uint salt, uint challenge) {
        buffer[0] = (byte)UdpPacketKind.ConnectChallenge;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], salt);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[5..], challenge);

        return 9;
    }

    public static int WriteConnectResponse(Span<byte> buffer, uint salt, uint challenge) {
        buffer[..ConnectRequestBytes].Clear();
        buffer[0] = (byte)UdpPacketKind.ConnectResponse;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], salt);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[5..], challenge);

        return ConnectRequestBytes;
    }

    public static int WriteConnectAccept(Span<byte> buffer, uint salt, uint connection) {
        buffer[0] = (byte)UdpPacketKind.ConnectAccept;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], salt);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[5..], connection);

        return 9;
    }

    public static int WriteConnectDenied(Span<byte> buffer, uint salt, UdpDenyReason reason) {
        buffer[0] = (byte)UdpPacketKind.ConnectDenied;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], salt);
        buffer[5] = (byte)reason;

        return 6;
    }

    public static int WriteDisconnect(Span<byte> buffer, uint connection, UdpDenyReason reason) {
        buffer[0] = (byte)UdpPacketKind.Disconnect;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], connection);
        buffer[5] = (byte)reason;

        return 6;
    }

    public static int WriteKeepAlive(Span<byte> buffer, uint connection) {
        buffer[0] = (byte)UdpPacketKind.KeepAlive;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], connection);

        return 5;
    }

    public static int WriteMessage(
        Span<byte> buffer,
        uint connection,
        Channel channel,
        ushort sequence,
        ushort fragmentId,
        byte fragmentIndex,
        byte fragmentCount,
        ReadOnlySpan<byte> payload
    ) {
        buffer[0] = (byte)UdpPacketKind.Message;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], connection);
        buffer[5] = (byte)channel;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..], fragmentId);
        buffer[10] = fragmentIndex;
        buffer[11] = fragmentCount;
        payload.CopyTo(buffer[MessageHeaderBytes..]);

        return MessageHeaderBytes + payload.Length;
    }

    public static int WriteAck(Span<byte> buffer, uint connection, Channel channel, ushort latest, uint history) {
        buffer[0] = (byte)UdpPacketKind.Ack;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], connection);
        buffer[5] = (byte)channel;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], latest);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], history);

        return 12;
    }

    public static bool TryReadKind(ReadOnlySpan<byte> datagram, out UdpPacketKind kind) {
        kind = default;

        if (datagram.IsEmpty || datagram[0] is < 1 or > (byte)UdpPacketKind.ConnectResponse) {
            return false;
        }

        kind = (UdpPacketKind)datagram[0];

        return true;
    }

    public static bool TryReadUInt32(ReadOnlySpan<byte> datagram, int offset, out uint value) {
        value = 0;

        if (datagram.Length < offset + 4) {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(datagram[offset..]);

        return true;
    }

    public static bool TryReadUInt16(ReadOnlySpan<byte> datagram, int offset, out ushort value) {
        value = 0;

        if (datagram.Length < offset + 2) {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(datagram[offset..]);

        return true;
    }

    public static bool TryReadByte(ReadOnlySpan<byte> datagram, int offset, out byte value) {
        value = 0;

        if (datagram.Length <= offset) {
            return false;
        }

        value = datagram[offset];

        return true;
    }

    /// <summary>
    ///     Whether one 16-bit sequence is newer than another, across the wrap.
    /// </summary>
    /// <param name="sequence">The sequence in question.</param>
    /// <param name="than">What to compare it with.</param>
    /// <returns>Whether it is later.</returns>
    /// <remarks>
    ///     The same trick <see cref="Tick" /> uses at 32 bits, and for the same reason: a counter that
    ///     wraps is compared by signed distance or it is compared wrongly twice a minute at a hundred
    ///     packets a second.
    /// </remarks>
    public static bool IsNewer(ushort sequence, ushort than) => (short)(sequence - than) > 0;

    /// <summary>How many sequences apart two are, across the wrap.</summary>
    /// <param name="sequence">The later one.</param>
    /// <param name="than">The earlier one.</param>
    /// <returns>The signed distance.</returns>
    public static int Distance(ushort sequence, ushort than) => (short)(sequence - than);
}
