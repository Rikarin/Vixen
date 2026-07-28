// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Udp;

namespace Vixen.Net.Fuzz.Targets;

/// <summary>The UDP transport, taking datagrams from anybody who can reach the port.</summary>
/// <remarks>
///     <para>
///         <b>The most exposed code in the module, and the last of it to be fuzzed.</b> Everything
///         else in this harness sits <i>above</i> the handshake: a snapshot, a call or an input is
///         only parsed once a connection exists and a client has been admitted. A datagram is parsed
///         before any of that, by a server that is listening on a public port, from a source address
///         that costs nothing to forge.
///     </para>
///     <para>
///         <b>What that reaches is three layers at once.</b> The packet-kind switch, the four-step
///         connect handshake with its salt and cookie, and — once a connection is up — the reliability
///         layer's sequence windows and the fragment reassembler, which is the one that holds pooled
///         buffers keyed by a number the sender chose.
///     </para>
///     <para>
///         <b>It establishes a real connection first, and that is what makes the target reach
///         anything.</b> The first version of this did not, and it managed two distinct behaviours in
///         two million cases — every datagram was refused at the handshake, because completing one
///         needs the server's cookie and the cookie is eight random bytes the fuzzer cannot guess.
///         That is the cookie working exactly as designed, and it means the reliability layer and the
///         reassembler — the parts most worth fuzzing — sit behind a door no amount of mutation
///         opens. So the target opens it the way an attacker would: connect properly, then send
///         rubbish. An authenticated client is still an untrusted one.
///     </para>
///     <para>
///         <b>Where a datagram appears to come from depends on what it claims to be.</b> A message,
///         an acknowledgement, a keep-alive or a disconnect names a connection, so it arrives from the
///         connected peer — from anywhere else it is meaningless and the transport is right to say so
///         in one comparison. Everything else arrives from a stranger whose address varies, which is
///         the shape of every UDP exhaustion attack and is what <see cref="Held" /> is watching.
///     </para>
/// </remarks>
public sealed class UdpTransportTarget : IFuzzTarget, IDisposable {
    static readonly IPEndPoint Peer = new(IPAddress.Parse("10.0.0.1"), 40001);

    readonly FuzzSocketFactory sockets = new();
    readonly UdpTransport transport;
    readonly CountingEvents events = new();

    Counters before;
    uint clock;
    uint connection;

    /// <summary>Creates the target with a listening server half and one connected peer.</summary>
    public UdpTransportTarget() {
        transport = new(sockets);
        transport.StartServer();
        Establish();
    }

    /// <inheritdoc />
    public string Name => "udp";

    /// <inheritdoc />
    public string What => "UdpTransport.Poll: a datagram from an unauthenticated stranger";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     A refused datagram allocates nothing — the kind switch and the length checks are all
    ///     comparisons. An accepted handshake allocates a connection and its channel state, and an
    ///     accepted fragment rents a pooled buffer, so the per-byte term covers the reassembler
    ///     copying a payload it was given. What the allowance is really asserting is that <b>nothing
    ///     allocates in proportion to a number inside the datagram</b>, which is the whole family of
    ///     bug this target exists for.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 8192 + (64L * inputLength);

    /// <summary>How many connections the server is holding.</summary>
    /// <remarks>
    ///     <b>The oracle that matters more than the allocation one here.</b> A stream of connect
    ///     requests from forged addresses is the classic way to make a UDP server remember an
    ///     unbounded number of strangers, and it is not visible as an allocation spike — each one is
    ///     small, and it is the *never freeing them* that is the attack. A ceiling on this is a
    ///     statement that the transport's connection table cannot be grown from outside.
    /// </remarks>
    public long Held => transport.ConnectionCount;

    /// <summary>The most it may hold before that is a finding.</summary>
    /// <remarks>
    ///     <para>
    ///         Generous, and deliberately so: this is not a check that the limit is well chosen, it is
    ///         a check that a limit exists. A run that reaches this has found a table an attacker
    ///         fills.
    ///     </para>
    ///     <para>
    ///         <b>It does not move, and the reason is worth knowing rather than assuming.</b> The
    ///         handshake is <i>stateless until the cookie comes back</i> — the challenge is a hash of
    ///         a per-process secret, the source address and the client's salt, so a connect request
    ///         is answered and forgotten. There is no half-open table, so there is nothing to fill.
    ///         The oracle is here to keep that true, not because it was in doubt.
    ///     </para>
    /// </remarks>
    public long HeldCap => 4096;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        var buffer = new byte[UdpProtocol.MaxDatagramBytes];

        // One well-formed datagram of every kind the protocol has, which is what gives the mutator
        // something with a valid first byte to work from — random bytes are refused by the kind
        // switch and would spend the whole budget there.
        Add(corpus, buffer, UdpProtocol.WriteConnectRequest(buffer, 0x1234_5678));
        Add(corpus, buffer, UdpProtocol.WriteConnectChallenge(buffer, 0x1234_5678, 0x9ABC_DEF0));
        Add(corpus, buffer, UdpProtocol.WriteConnectResponse(buffer, 0x1234_5678, 0x9ABC_DEF0));
        Add(corpus, buffer, UdpProtocol.WriteConnectAccept(buffer, 0x1234_5678, 1));
        Add(corpus, buffer, UdpProtocol.WriteConnectDenied(buffer, 0x1234_5678, UdpDenyReason.Full));
        Add(corpus, buffer, UdpProtocol.WriteDisconnect(buffer, 1, UdpDenyReason.Shutdown));
        Add(corpus, buffer, UdpProtocol.WriteKeepAlive(buffer, 1));
        Add(corpus, buffer, UdpProtocol.WriteAck(buffer, 1, Channel.Reliable, 7, 0xFFFF_FFFF));

        // A message on every channel, and the fragment fields at both ends of what a byte can say —
        // "fragment 255 of 1" is the arithmetic the reassembler has to refuse rather than index by.
        ReadOnlySpan<byte> payload = [1, 2, 3, 4, 5, 6, 7, 8];

        // Addressed to the connection that was established, so a mutation of one of these is a
        // message an admitted client could actually have sent. Seeds naming a connection that does
        // not exist are refused at the first comparison and guide nothing.
        foreach (var channel in (Channel[])[Channel.Reliable, Channel.Unreliable, Channel.Sequenced]) {
            Add(corpus, buffer, UdpProtocol.WriteMessage(buffer, connection, channel, 1, 0, 0, 1, payload));
            Add(corpus, buffer, UdpProtocol.WriteMessage(buffer, connection, channel, 2, 0, 0, 56, payload));
            Add(corpus, buffer, UdpProtocol.WriteMessage(buffer, connection, channel, 3, 0, 255, 1, payload));
            Add(corpus, buffer, UdpProtocol.WriteMessage(buffer, connection, channel, 4, 0, 0, 0, payload));
        }

        Add(corpus, buffer, UdpProtocol.WriteAck(buffer, connection, Channel.Reliable, 1, 1));
        Add(corpus, buffer, UdpProtocol.WriteKeepAlive(buffer, connection));

        // And the degenerate lengths, which the kind switch is the first thing to see.
        corpus.Add([]);
        corpus.Add([0]);
        corpus.Add([255]);
    }

    /// <inheritdoc />
    public void Maintain() {
        // Time moves, so timeouts and retransmissions are reached rather than being state the target
        // can never enter. A run is millions of cases, so this is hours of transport time.
        clock++;
        transport.Poll(TimeSpan.FromMilliseconds(20), events);

        // A case is perfectly able to send a disconnect for the connection this target depends on,
        // and one will. Without this the target would keep running and quietly stop reaching
        // anything past the handshake, which is the failure it was rebuilt to avoid.
        if (transport.ConnectionCount == 0) {
            Establish();
        }

        before = Read();
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        sockets.Offer(input, From(input));
        transport.Poll(TimeSpan.Zero, events);

        var after = Read();
        var signature = ((after.Connections - before.Connections) * 7919L)
            + ((after.Rejected - before.Rejected) * 131L)
            + ((after.Connected - before.Connected) * 127L)
            + ((after.Disconnected - before.Disconnected) * 113L)
            + ((after.Data - before.Data) * 109L)
            + (after.Bytes - before.Bytes);

        before = after;

        return signature;
    }

    /// <inheritdoc />
    public void Dispose() {
        transport.Dispose();
        sockets.Dispose();
    }

    static void Add(ICollection<byte[]> corpus, byte[] buffer, int length) => corpus.Add(buffer[..length].ToArray());

    Counters Read() =>
        new(
            transport.ConnectionCount,
            transport.RejectedDatagramCount,
            events.Connected,
            events.Disconnected,
            events.Data,
            events.Bytes
        );

    /// <summary>Who a datagram appears to be from, decided by what it claims to be.</summary>
    IPEndPoint From(ReadOnlySpan<byte> input) {
        if (!input.IsEmpty && input[0] is >= (byte)UdpPacketKind.Disconnect and <= (byte)UdpPacketKind.Ack) {
            return Peer;
        }

        // A stranger, derived from the input rather than random so a case reproduces whole, with the
        // clock folded in so a replayed corpus entry is a different stranger each cycle — which is
        // what makes the exhaustion shape reachable at all.
        var host = input.IsEmpty ? clock : (uint)(input[0] | (clock << 8));

        return new(new IPAddress((host & 0x00FF_FFFF) | 0x0B00_0000), 1 + (int)(host % 60000));
    }

    /// <summary>Walks the four-step handshake, reading the server's replies out of the socket.</summary>
    void Establish() {
        var buffer = new byte[UdpProtocol.MaxDatagramBytes];
        const uint Salt = 0x0BAD_F00D;

        sockets.Offer(buffer.AsSpan(0, UdpProtocol.WriteConnectRequest(buffer, Salt)), Peer);
        transport.Poll(TimeSpan.Zero, events);

        // The challenge is the whole point: it is derived from a secret this target does not have,
        // so it has to be read back rather than computed.
        if (!sockets.TryTakeSent(out var challenge)
            || !UdpProtocol.TryReadKind(challenge, out var kind)
            || kind != UdpPacketKind.ConnectChallenge
            || !UdpProtocol.TryReadUInt32(challenge, 5, out var cookie)) {
            return;
        }

        sockets.Offer(buffer.AsSpan(0, UdpProtocol.WriteConnectResponse(buffer, Salt, cookie)), Peer);
        transport.Poll(TimeSpan.Zero, events);

        if (sockets.TryTakeSent(out var accept)
            && UdpProtocol.TryReadKind(accept, out kind)
            && kind == UdpPacketKind.ConnectAccept
            && UdpProtocol.TryReadUInt32(accept, 5, out var id)) {
            connection = id;
        }
    }

    readonly record struct Counters(int Connections, long Rejected, long Connected, long Disconnected, long Data, long Bytes);

    /// <summary>A socket that hands out whatever the fuzzer last offered it.</summary>
    sealed class FuzzSocketFactory : IDatagramSocketFactory, IDisposable {
        readonly FuzzSocket socket = new();

        public void Dispose() => socket.Dispose();

        public IDatagramSocket Bind(IPEndPoint endPoint) {
            socket.Bind(endPoint);

            return socket;
        }

        public void Offer(ReadOnlySpan<byte> datagram, IPEndPoint from) => socket.Offer(datagram, from);

        public bool TryTakeSent(out ReadOnlySpan<byte> datagram) => socket.TryTakeSent(out datagram);

        sealed class FuzzSocket : IDatagramSocket {
            readonly byte[] pending = new byte[UdpProtocol.MaxDatagramBytes];
            readonly byte[] sent = new byte[UdpProtocol.MaxDatagramBytes];

            IPEndPoint? sender;
            int length;
            bool waiting;
            int sentLength;
            bool hasSent;

            public EndPoint? LocalEndPoint { get; private set; }

            public void Bind(IPEndPoint endPoint) => LocalEndPoint = endPoint;

            // Kept rather than dropped, because the handshake's challenge is unguessable by design
            // and reading it back is the only way past the door. What the transport writes is not
            // otherwise under test.
            public void SendTo(ReadOnlySpan<byte> payload, EndPoint destination) {
                sentLength = Math.Min(payload.Length, sent.Length);
                payload[..sentLength].CopyTo(sent);
                hasSent = true;
            }

            public bool TryTakeSent(out ReadOnlySpan<byte> datagram) {
                datagram = hasSent ? sent.AsSpan(0, sentLength) : default;
                hasSent = false;

                return !datagram.IsEmpty;
            }

            public void Offer(ReadOnlySpan<byte> datagram, IPEndPoint from) {
                length = Math.Min(datagram.Length, pending.Length);
                datagram[..length].CopyTo(pending);
                sender = from;
                waiting = true;
            }

            public bool TryReceiveFrom(Span<byte> buffer, out EndPoint from, out int length) {
                from = sender ?? new IPEndPoint(IPAddress.Loopback, 1);
                length = 0;

                if (!waiting) {
                    return false;
                }

                waiting = false;
                length = Math.Min(this.length, buffer.Length);
                pending.AsSpan(0, length).CopyTo(buffer);

                return true;
            }

            public void Dispose() { }
        }
    }

    /// <summary>Counts what the transport raised, so a case that got through is visible.</summary>
    /// <remarks>
    ///     A target whose events went nowhere would prove the transport refuses everything, which is
    ///     not the claim. The byte total folds the payload in, so two datagrams that both delivered
    ///     but delivered different amounts are different cases.
    /// </remarks>
    sealed class CountingEvents : ITransportEvents {
        public long Connected { get; private set; }
        public long Disconnected { get; private set; }
        public long Data { get; private set; }
        public long Bytes { get; private set; }

        public void OnConnected(TransportRole role, ConnectionId connection) => Connected++;

        public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) =>
            Disconnected++;

        public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
            Data++;
            Bytes += payload.Length;
        }
    }
}
