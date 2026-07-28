// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.Net.Transport.Udp;

namespace Vixen.Net.Transport.Udp.Tests;

/// <summary>Datagram sockets with no operating system underneath them.</summary>
/// <remarks>
///     <para>
///         The reason the transport has a socket seam. Sequencing, retransmission, reassembly and the
///         four channels' different promises are logic, and logic tested against a real socket is
///         logic tested against a scheduler — the same test passes on a fast machine and fails on a
///         loaded one, which is worse than not having it.
///     </para>
///     <para>
///         Here a datagram is delivered when the receiver polls and not before, nothing is ever
///         reordered unless a test asks for it, and <see cref="LossPattern" /> makes "the third packet
///         is lost" a fact rather than a probability. There is a real-socket test as well, and it
///         asserts that the adapter works — not that the protocol does.
///     </para>
/// </remarks>
public sealed class DatagramBus : IDatagramSocketFactory {
    readonly Dictionary<EndPoint, BusSocket> sockets = [];
    readonly List<(byte[] Payload, EndPoint Destination, EndPoint From, int Remaining)> held = [];

    int nextPort = 40000;

    /// <summary>Datagrams that were delivered.</summary>
    public long DeliveredCount { get; private set; }

    /// <summary>Datagrams that were thrown away by <see cref="LossPattern" />.</summary>
    public long DroppedCount { get; private set; }

    /// <summary>
    ///     Decides which datagrams are lost. Given the number of the datagram, in order, and the
    ///     bytes; return true to drop it.
    /// </summary>
    public Func<long, ReadOnlyMemory<byte>, bool>? LossPattern { get; set; }

    /// <summary>How many datagrams have been sent through the bus, lost or not.</summary>
    public long SentCount { get; private set; }

    /// <summary>Decides which datagrams arrive twice. Given the number of the datagram, in order.</summary>
    public Func<long, bool>? DuplicatePattern { get; set; }

    /// <summary>
    ///     Decides which datagrams are held back, and for how many later deliveries. Given the number
    ///     of the datagram, in order; return zero to deliver it now.
    /// </summary>
    /// <remarks>
    ///     What makes reordering a fact rather than a probability. A sequenced channel's whole promise
    ///     is about what happens when an old datagram arrives after a new one, and that is not
    ///     something a test can wait for.
    /// </remarks>
    public Func<long, int>? DelayPattern { get; set; }

    /// <inheritdoc />
    public IDatagramSocket Bind(IPEndPoint endPoint) {
        var port = endPoint.Port == 0 ? nextPort++ : endPoint.Port;
        var address = new IPEndPoint(IPAddress.Loopback, port);

        if (sockets.ContainsKey(address)) {
            throw new InvalidOperationException($"{address} is already bound.");
        }

        var socket = new BusSocket(this, address);
        sockets[address] = socket;

        return socket;
    }

    void Deliver(ReadOnlySpan<byte> payload, EndPoint destination, EndPoint from) {
        SentCount++;

        if (LossPattern is not null && LossPattern(SentCount, payload.ToArray())) {
            DroppedCount++;

            return;
        }

        var hold = DelayPattern?.Invoke(SentCount) ?? 0;

        if (hold > 0) {
            held.Add((payload.ToArray(), destination, from, hold));

            return;
        }

        Arrive(payload, destination, from);

        if (DuplicatePattern is not null && DuplicatePattern(SentCount)) {
            Arrive(payload, destination, from);
        }

        Release();
    }

    void Release() {
        for (var i = held.Count - 1; i >= 0; i--) {
            var (payload, destination, from, remaining) = held[i];

            if (--remaining > 0) {
                held[i] = (payload, destination, from, remaining);

                continue;
            }

            held.RemoveAt(i);
            Arrive(payload, destination, from);
        }
    }

    void Arrive(ReadOnlySpan<byte> payload, EndPoint destination, EndPoint from) {
        if (!sockets.TryGetValue(destination, out var socket)) {
            // Nothing is listening there. On a real network this is an ICMP the sender ignores.
            return;
        }

        DeliveredCount++;
        socket.Enqueue(payload, from);
    }

    void Close(EndPoint address) => sockets.Remove(address);

    sealed class BusSocket(DatagramBus bus, IPEndPoint address) : IDatagramSocket {
        readonly Queue<(byte[] Payload, EndPoint From)> inbox = new();

        public EndPoint? LocalEndPoint => address;

        public void SendTo(ReadOnlySpan<byte> payload, EndPoint destination) =>
            bus.Deliver(payload, destination, address);

        public bool TryReceiveFrom(Span<byte> buffer, out EndPoint from, out int length) {
            from = address;
            length = 0;

            if (inbox.Count == 0) {
                return false;
            }

            var (payload, sender) = inbox.Dequeue();
            payload.CopyTo(buffer);
            from = sender;
            length = payload.Length;

            return true;
        }

        public void Enqueue(ReadOnlySpan<byte> payload, EndPoint from) => inbox.Enqueue((payload.ToArray(), from));

        public void Dispose() {
            inbox.Clear();
            bus.Close(address);
        }
    }
}
