// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;

namespace Vixen.Net.Fuzz.Targets;

/// <summary>The first byte of every packet, as the wire numbers them.</summary>
/// <remarks>
///     Hard-coded rather than taken from <c>SystemMessage</c>, which is internal to
///     <c>Vixen.Net</c> — and correctly so, because these numbers are the protocol rather than an
///     implementation detail. Writing them out here is also what a fuzzer ought to be doing: it is
///     testing the wire, and a wire constant that moves when an enum is reordered was never a wire
///     constant.
/// </remarks>
internal static class Wire {
    public const byte ConnectRequest = 1;
    public const byte ConnectAccepted = 2;
    public const byte ConnectRejected = 3;
    public const byte Ping = 4;
    public const byte Pong = 5;
    public const byte User = 6;
}

/// <summary>The server's receive path, from an unauthenticated connection and from a player.</summary>
/// <remarks>
///     <para>
///         <b>This is the most exposed code in the engine.</b> Everything else a server decodes has
///         at least been through the handshake; this has not. Anybody who can reach the port can
///         reach <c>BeginAuthentication</c>, and it reads a protocol version, a content hash, a
///         credential blob and a reconnect token out of bytes that arrived from nowhere in
///         particular.
///     </para>
///     <para>
///         <b>Two connections a case, because there are two states and only one of them is easy to
///         reach.</b> A fresh connection exercises the pre-handshake path; a player that was
///         admitted at construction and kept alive exercises the one behind it — ping, pong, and the
///         user payloads everything above the session travels in. Fuzzing only the first would leave
///         the branch that runs for every real packet of a real match untested.
///     </para>
/// </remarks>
public sealed class HandshakeTarget : IFuzzTarget, IDisposable, ISessionMessageHandler {
    static readonly ConnectionId Established = new(1);

    readonly InjectingTransport transport = new();
    readonly NetworkSession session;
    readonly byte[] payload = new byte[Mutator.MaxLength];

    uint nextConnection = 2;
    ConnectionId previous;
    long joined;
    long left;
    long messages;

    /// <summary>Creates the target, with a server already listening and one player already in.</summary>
    public HandshakeTarget() {
        session = new(
            transport,
            new SessionOptions {
                // Zero, so a connection that goes away takes its player with it on the same frame.
                // A reconnect window would leave a player per case behind and the target would be
                // measuring the growth of its own harness within a hundred cases.
                ReconnectWindow = TimeSpan.Zero,
                MaxPlayers = 8
            }
        );

        session.PlayerJoined += _ => joined++;
        session.PlayerLeft += (_, _) => left++;
        session.StartServer();

        Admit();
    }

    /// <inheritdoc />
    public string Name => "handshake";

    /// <inheritdoc />
    public string What => "NetworkSession's server half: the pre-handshake path and a player's";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     A rejection composes a sentence — "this server speaks protocol 1, and you speak
    ///     4292918302" — and a handshake copies the credential blob out of the packet, so this path
    ///     legitimately allocates a constant amount that has nothing to do with how long the input
    ///     was. The multiple is what guards the part that does: a blob is capped at
    ///     <c>MaxAuthenticationPayloadBytes</c>, and the claim is that nothing else scales with the
    ///     packet at all.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 8192 + (16L * inputLength);

    /// <summary>How many players and half-finished handshakes the session is holding.</summary>
    public long Held => session.Players.Count;

    /// <summary>
    ///     How many it may hold: the one that was admitted, and headroom for a case that admits
    ///     another before <see cref="Maintain" /> takes the previous connection away.
    /// </summary>
    /// <remarks>
    ///     Well under <c>MaxPlayers</c>, so this catches a session that keeps players it should have
    ///     retired rather than only one that has run out of seats. Running out of seats is a
    ///     <i>correct</i> outcome — the server is full — and a cap set there would prove nothing.
    /// </remarks>
    public long HeldCap => 4;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        var options = new SessionOptions();

        corpus.Add(ConnectRequest(options.ProtocolVersion, options.ContentHash, [], []));
        corpus.Add(ConnectRequest(options.ProtocolVersion, options.ContentHash, [1, 2, 3], new byte[16]));

        // A version and a hash that will not match, which is the branch that composes a sentence
        // and is therefore the one worth watching allocate.
        corpus.Add(ConnectRequest(99, 0xDEADBEEFDEADBEEF, new byte[400], new byte[16]));

        // The truncations of a handshake, one field at a time. Every one of these is what a real
        // network produces when a datagram is clipped.
        var whole = ConnectRequest(options.ProtocolVersion, options.ContentHash, [7, 7, 7], new byte[16]);

        for (var length = 1; length < whole.Length; length += 3) {
            corpus.Add(whole[..length]);
        }

        corpus.Add([Wire.Ping, 11, 0, 0, 0]);
        corpus.Add([Wire.Pong, 11, 0, 0, 0]);
        corpus.Add([Wire.User, 9, 9, 9, 9]);
        corpus.Add([Wire.ConnectAccepted]);
        corpus.Add([0xFF]);
    }

    /// <inheritdoc />
    public void Maintain() {
        if (previous.IsValid) {
            // Last case's connection goes away, so the pending table and the player list are bounded
            // by one rather than by how long the run is.
            transport.Drop(TransportRole.Server, previous);
            session.Update(TimeSpan.FromMilliseconds(16), this);
            previous = ConnectionId.None;
        }

        if (session.Players.Count == 0) {
            // The last input closed the connection the established player was on, which is a
            // legitimate outcome and not one to keep fuzzing without.
            Admit();
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var length = Math.Min(input.Length, payload.Length);
        input[..length].CopyTo(payload);

        var fresh = new ConnectionId(nextConnection++);
        previous = fresh;

        transport.Connect(fresh);
        transport.Deliver(TransportRole.Server, fresh, Channel.Reliable, payload, length);
        transport.Deliver(TransportRole.Server, Established, Channel.Unreliable, payload, length);

        // A frame, not a poke. Everything the session does with what arrived — deciding pending
        // connections, retiring players, answering pings — happens here.
        session.Update(TimeSpan.FromMilliseconds(16), this);

        return (joined * 1_000_003L)
            + (left * 7919L)
            + (messages * 31L)
            + (transport.Sent * 17L)
            + (transport.Disconnects * 13L)
            + session.Players.Count;
    }

    /// <inheritdoc />
    void ISessionMessageHandler.OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> message) => messages++;

    /// <summary>Stops the session.</summary>
    public void Dispose() {
        session.Dispose();
        transport.Dispose();
    }

    void Admit() {
        var options = new SessionOptions();
        var request = ConnectRequest(options.ProtocolVersion, options.ContentHash, [], []);

        transport.Connect(Established);
        transport.Deliver(TransportRole.Server, Established, Channel.Reliable, request, request.Length);
        session.Update(TimeSpan.FromMilliseconds(16), this);
    }

    static byte[] ConnectRequest(uint protocol, ulong contentHash, byte[] credentials, byte[] token) {
        var buffer = new byte[1024];
        var writer = new PacketWriter(buffer);

        writer.WriteByte(Wire.ConnectRequest);
        writer.WriteVariable(protocol);
        writer.WriteUInt64(contentHash);
        writer.WriteBlob(credentials);
        writer.WriteBlob(token);

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }

}

/// <summary>The client's receive path: what a server can talk a client into.</summary>
/// <remarks>
///     <para>
///         Less exposed than the server's and worth fuzzing anyway. A client believes rather a lot
///         about the peer it connected to — its player id, its tick, its reconnect token, its
///         identity string — and "the server is trusted" is a weaker statement than it sounds when
///         the server is a listen server run by another player, or a relay, or something that has
///         got in between.
///     </para>
///     <para>
///         The client is restarted when an input stops it, because a stopped session decodes nothing
///         and a run that quietly stopped fuzzing after the first <c>ConnectRejected</c> would
///         report a clean million cases having tested one.
///     </para>
/// </remarks>
public sealed class SessionClientTarget : IFuzzTarget, IDisposable, ISessionMessageHandler {
    readonly InjectingTransport transport = new();
    readonly NetworkSession session;
    readonly byte[] payload = new byte[Mutator.MaxLength];

    long connected;
    long rejected;
    long disconnected;
    long messages;

    /// <summary>Creates the target, with a client half already trying to connect.</summary>
    public SessionClientTarget() {
        session = new(transport);
        session.Connected += _ => connected++;
        session.Rejected += (_, _) => rejected++;
        session.Disconnected += _ => disconnected++;
        session.StartClient();
    }

    /// <inheritdoc />
    public string Name => "client";

    /// <inheritdoc />
    public string What => "NetworkSession's client half: accept, reject, ping, pong and user payloads";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     An accepted handshake copies a token and decodes an identity string, both capped by the
    ///     reader; a rejection decodes a reason, likewise. Everything else on this path is
    ///     allocation-free, so the multiple is the claim and the constant is the restart.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 8192 + (16L * inputLength);

    /// <summary>How many players this client believes are in the session.</summary>
    public long Held => session.Players.Count;

    /// <summary>
    ///     How many it may believe in: itself, and nothing else. A client is told about its own
    ///     player and no others — the server's list is not the client's to have — so anything past
    ///     one is a client that has been talked into keeping a record it will never use again.
    /// </summary>
    /// <remarks>
    ///     Two rather than one, for the frame in which a restart has not yet retired the previous
    ///     local player. This is the oracle that caught the accept path keeping a player per packet.
    /// </remarks>
    public long HeldCap => 2;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        corpus.Add(Accepted(1, 100, new byte[16], "player"));
        corpus.Add(Accepted(0, 0, [], ""));
        corpus.Add(Accepted(uint.MaxValue, uint.MaxValue, new byte[16], new string('x', 200)));

        var whole = Accepted(1, 100, new byte[16], "player");

        for (var length = 1; length < whole.Length; length += 2) {
            corpus.Add(whole[..length]);
        }

        corpus.Add(Rejected(2, "wrong content"));
        corpus.Add(Ping(7));
        corpus.Add(Pong(7, 900));
        corpus.Add([Wire.User, 1, 2, 3, 4]);
        corpus.Add([Wire.ConnectRequest, 1]);
    }

    /// <inheritdoc />
    public void Maintain() {
        if (session.State == SessionState.Stopped) {
            session.StartClient();
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var length = Math.Min(input.Length, payload.Length);
        input[..length].CopyTo(payload);

        transport.Deliver(TransportRole.Client, ConnectionId.None, Channel.Reliable, payload, length);
        session.Update(TimeSpan.FromMilliseconds(16), this);

        return (connected * 1_000_003L)
            + (rejected * 7919L)
            + (disconnected * 131L)
            + (messages * 31L)
            + (transport.Sent * 17L)
            + (session.LocalPlayer is null ? 0 : 3L)
            + session.Tick.Value;
    }

    /// <inheritdoc />
    void ISessionMessageHandler.OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> message) => messages++;

    /// <summary>Stops the session.</summary>
    public void Dispose() {
        session.Dispose();
        transport.Dispose();
    }

    static byte[] Accepted(uint playerId, uint tick, byte[] token, string identity) {
        var buffer = new byte[1024];
        var writer = new PacketWriter(buffer);

        writer.WriteByte(Wire.ConnectAccepted);
        writer.WriteVariable(playerId);
        writer.WriteTick(new(tick));
        writer.WriteBlob(token);
        writer.WriteString(identity);

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }

    static byte[] Rejected(byte reason, string text) {
        var buffer = new byte[512];
        var writer = new PacketWriter(buffer);

        writer.WriteByte(Wire.ConnectRejected);
        writer.WriteByte(reason);
        writer.WriteString(text);

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }

    static byte[] Ping(uint id) {
        var buffer = new byte[16];
        var writer = new PacketWriter(buffer);

        writer.WriteByte(Wire.Ping);
        writer.WriteUInt32(id);
        writer.WriteTick(new(1));

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }

    static byte[] Pong(uint id, uint tick) {
        var buffer = new byte[16];
        var writer = new PacketWriter(buffer);

        writer.WriteByte(Wire.Pong);
        writer.WriteUInt32(id);
        writer.WriteTick(new(tick));

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }
}
