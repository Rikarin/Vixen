// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;

namespace Vixen.Net.Transport.Udp;

/// <summary>A thing that sends and receives datagrams.</summary>
/// <remarks>
///     <para>
///         The seam between the reliability layer and the operating system, and the reason it exists
///         is testing. Everything interesting about this transport — sequencing, retransmission,
///         reassembly, the four channels' different promises — is logic, and logic tested against a
///         real socket is logic tested against a scheduler. Over an in-memory bus the same code is a
///         pure function of the calls made to it, which is the property the rest of this stack is
///         built on.
///     </para>
///     <para>
///         It is deliberately thin: bind, send, try-receive. Anything smarter would be logic that
///         only runs in production, which is the thing the seam exists to avoid.
///     </para>
/// </remarks>
public interface IDatagramSocket : IDisposable {
    /// <summary>Where this socket is, once it is bound.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>Sends one datagram.</summary>
    /// <param name="payload">The bytes. Sent whole or not at all.</param>
    /// <param name="destination">Where to.</param>
    void SendTo(ReadOnlySpan<byte> payload, EndPoint destination);

    /// <summary>Takes one datagram, if one is waiting.</summary>
    /// <param name="buffer">Where to put it.</param>
    /// <param name="from">Who sent it.</param>
    /// <param name="length">How many bytes it was.</param>
    /// <returns>
    ///     Whether there was one. Never blocks: a transport polls until this says no, and then gets
    ///     on with the frame.
    /// </returns>
    bool TryReceiveFrom(Span<byte> buffer, out EndPoint from, out int length);
}

/// <summary>Makes the sockets a transport needs.</summary>
/// <remarks>
///     A transport has two halves and therefore up to two sockets — one bound to a known port for the
///     server, one on whatever port the operating system gives out for the client. A factory rather
///     than a constructor argument because the transport decides how many it needs and when.
/// </remarks>
public interface IDatagramSocketFactory {
    /// <summary>Makes a socket bound to an address.</summary>
    /// <param name="endPoint">Where to bind. A port of zero means "anywhere".</param>
    /// <returns>The socket.</returns>
    IDatagramSocket Bind(IPEndPoint endPoint);
}
