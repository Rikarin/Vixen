// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Sessions;

namespace Vixen.Net.Messaging;

/// <summary>A message that is about nothing in particular, and how it is written.</summary>
/// <typeparam name="TSelf">The message type itself.</typeparam>
/// <remarks>
///     <para>
///         Hand-written rather than generated, for now. The RPC generator's job is to turn a method
///         signature into a wire format; a broadcast is already a struct whose fields <i>are</i> the
///         wire format, so there is less to derive and the interface is a smaller ask than an
///         attribute plus a generator pass. <c>[Replicated]</c>'s generator is the obvious place to
///         grow this into when a game has thirty of them.
///     </para>
///     <para>
///         Static abstract members, so the id is a property of the <i>type</i> rather than of an
///         instance — the receiver has to know which type to construct before it has one to ask.
///     </para>
/// </remarks>
public interface IBroadcast<TSelf> where TSelf : struct, IBroadcast<TSelf> {
    /// <summary>The name this message is known by on the wire.</summary>
    /// <remarks>
    ///     Hashed into an id at registration. A name rather than a number so that two builds agree
    ///     without anybody maintaining a table of constants, which is the same reasoning
    ///     <c>ReplicationRegistry</c> and <c>RpcManifest</c> use — and it means a renamed message is
    ///     a refused message rather than one that quietly decodes as something else.
    /// </remarks>
    static abstract string BroadcastName { get; }

    /// <summary>Writes this message.</summary>
    /// <param name="writer">Where it goes.</param>
    void Write(ref BitWriter writer);

    /// <summary>Reads one.</summary>
    /// <param name="reader">Where it comes from.</param>
    /// <param name="value">The message, if it decoded.</param>
    /// <returns>Whether it did.</returns>
    static abstract bool TryRead(ref BitReader reader, out TSelf value);
}

/// <summary>Typed messages that are not about a networked object.</summary>
/// <remarks>
///     <para>
///         <b>Why this is not an RPC.</b> A remote call is about an object: it names one, ownership
///         and the rules are checked against it, and it is refused if the receiver does not have it.
///         Chat, a match-start countdown, a scoreboard, a "loading finished" from each client, a
///         server telling everyone the round is over — none of those are about an object, and
///         inventing one to hang them from means every such message needs a spawned entity,
///         ownership and an interest set that all exist only to satisfy the dispatcher.
///     </para>
///     <para>
///         So a broadcast has its own payload kind and its own closed registry. What it keeps from
///         the RPC path is everything that made that path safe: <b>a message names a position in a
///         registry rather than a type</b>, so a packet can never be talked into constructing
///         something; the sender is what the session says it is, never what the packet claims; and a
///         handler that throws does not take the receive loop with it.
///     </para>
///     <para>
///         <b>Rate limiting is deliberately the caller's.</b> A broadcast from a client is exactly as
///         abusable as a server RPC, and the token bucket that already exists lives in
///         <c>RpcRouter</c> — sharing it would mean either coupling the two or duplicating it. A
///         server that accepts client broadcasts should give this a limiter; one that only sends
///         them does not need it. See <see cref="Limits" />.
///     </para>
/// </remarks>
public sealed class BroadcastRouter {
    readonly Dictionary<uint, Registration> byId = [];
    readonly List<uint> order = [];
    readonly Dictionary<uint, double> tokens = [];
    readonly byte[] buffer;

    /// <summary>Creates a router.</summary>
    /// <param name="maxPayloadBytes">The largest broadcast this will encode.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPayloadBytes" /> is too small.</exception>
    public BroadcastRouter(int maxPayloadBytes = 1200) {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 16);
        buffer = new byte[maxPayloadBytes];
    }

    /// <summary>How many broadcasts a connection may send. Null to not limit them.</summary>
    /// <remarks>
    ///     Null by default, because a game that only sends broadcasts from its server does not need
    ///     a limiter and would be paying a dictionary lookup a message for nothing. A server that
    ///     accepts them from clients wants one, and wants it to be a smaller number than the RPC
    ///     limit — a broadcast fans out to everybody, so one client's message is N clients' packets.
    /// </remarks>
    public BroadcastLimits? Limits { get; set; }

    /// <summary>How many message types are registered.</summary>
    public int RegisteredCount => order.Count;

    /// <summary>Messages that were delivered to a handler.</summary>
    public long DeliveredCount { get; private set; }

    /// <summary>Messages refused because the id names nothing registered.</summary>
    public long RefusedByRegistryCount { get; private set; }

    /// <summary>Messages refused because they did not decode, or left bits behind.</summary>
    public long RefusedByPayloadCount { get; private set; }

    /// <summary>Messages refused because the sender is sending too many.</summary>
    public long RefusedByRateLimitCount { get; private set; }

    /// <summary>Starts accepting a message type, and says what to do with one.</summary>
    /// <typeparam name="T">The message.</typeparam>
    /// <param name="handler">
    ///     What to do with one. The sender is what the session said, never what the packet claims.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="handler" /> is null.</exception>
    /// <exception cref="ArgumentException">Another type is already registered under the same name.</exception>
    public void Subscribe<T>(Action<PlayerId, T> handler) where T : struct, IBroadcast<T> {
        ArgumentNullException.ThrowIfNull(handler);

        var id = Identify<T>();

        if (byId.TryGetValue(id, out var existing)) {
            if (!string.Equals(existing.Name, T.BroadcastName, StringComparison.Ordinal)) {
                throw new ArgumentException(
                    $"'{T.BroadcastName}' hashes the same as '{existing.Name}'. Rename one — two messages "
                        + "that share an id are two messages the receiver cannot tell apart.",
                    nameof(handler)
                );
            }

            existing.Handlers.Add((from, reader) => Deliver(handler, from, reader));

            return;
        }

        var registration = new Registration(T.BroadcastName);
        registration.Handlers.Add((from, reader) => Deliver(handler, from, reader));

        byId[id] = registration;
        order.Add(id);
    }

    /// <summary>Encodes a message, ready to hand to a session.</summary>
    /// <typeparam name="T">The message.</typeparam>
    /// <param name="message">It.</param>
    /// <param name="payload">The bytes, if it fit.</param>
    /// <returns>Whether it fit.</returns>
    /// <remarks>
    ///     Encoding is separate from sending because who a broadcast goes to is the session's
    ///     vocabulary and not this type's — a server sends one to everybody, to one player, or to
    ///     the subset a game decided on, and this would only be guessing at which.
    /// </remarks>
    public bool TryEncode<T>(in T message, out ReadOnlySpan<byte> payload) where T : struct, IBroadcast<T> {
        var writer = new BitWriter(buffer);
        writer.WriteVariable(Identify<T>());
        message.Write(ref writer);

        return writer.TryFinish(out payload);
    }

    /// <summary>Takes a broadcast off the wire, checks it, and hands it to whoever asked.</summary>
    /// <param name="from">
    ///     Who sent it, as the session says — never as the packet says.
    ///     <see cref="PlayerId.None" /> when it came from the server.
    /// </param>
    /// <param name="payload">The bytes.</param>
    /// <returns>Whether it reached a handler.</returns>
    public bool Receive(PlayerId from, ReadOnlySpan<byte> payload) {
        var reader = new BitReader(payload);

        if (!reader.TryReadVariable(out var id) || !byId.TryGetValue(id, out var registration)) {
            // An id outside the registry is not a type we can be talked into constructing. The same
            // closed set the replication registry and the RPC manifest are, for the same reason.
            RefusedByRegistryCount++;

            return false;
        }

        if (from.IsValid && !TakeToken(from)) {
            RefusedByRateLimitCount++;

            return false;
        }

        // Every registration has at least one handler — Subscribe is the only way to make one and
        // it adds one — so there is no "registered but nobody listening" case to count. A client
        // that does not care about the scoreboard simply never registers, and its messages are
        // refused by the registry above.
        foreach (var handler in registration.Handlers) {
            // Each handler reads from its own reader over the same bits. Subscribers are independent
            // — one that consumed the payload would starve the next, and one that failed to decode
            // says nothing about whether another can.
            var own = new BitReader(payload);
            own.TryReadVariable(out _);

            if (!handler(from, own)) {
                RefusedByPayloadCount++;

                return false;
            }
        }

        DeliveredCount++;

        return true;
    }

    /// <summary>Refills the rate limiters.</summary>
    /// <param name="elapsed">Time since the last call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed" /> is negative.</exception>
    public void Advance(TimeSpan elapsed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        if (Limits is not { } limits || tokens.Count == 0) {
            return;
        }

        var refill = limits.PerSecond * elapsed.TotalSeconds;

        foreach (var player in new List<uint>(tokens.Keys)) {
            tokens[player] = Math.Min(limits.Burst, tokens[player] + refill);
        }
    }

    /// <summary>Forgets a connection's rate-limit state.</summary>
    /// <param name="player">Who left.</param>
    public void Forget(PlayerId player) => tokens.Remove(player.Value);

    /// <summary>The wire id a message type has.</summary>
    /// <typeparam name="T">The message.</typeparam>
    /// <returns>Its id.</returns>
    public static uint Identify<T>() where T : struct, IBroadcast<T> =>
        Replication.ReplicationRegistry.HashTypeName(T.BroadcastName);

    static bool Deliver<T>(Action<PlayerId, T> handler, PlayerId from, BitReader reader)
        where T : struct, IBroadcast<T> {
        if (!T.TryRead(ref reader, out var message) || reader.BitsRemaining >= 8) {
            // Bits left over means the sender and this build disagree about the shape, which the
            // handshake's content hash should have caught. Refuse rather than guess.
            return false;
        }

        handler(from, message);

        return true;
    }

    bool TakeToken(PlayerId player) {
        if (Limits is not { } limits) {
            return true;
        }

        if (!tokens.TryGetValue(player.Value, out var available)) {
            available = limits.Burst;
        }

        if (available < 1) {
            tokens[player.Value] = available;

            return false;
        }

        tokens[player.Value] = available - 1;

        return true;
    }

    sealed class Registration(string name) {
        public string Name { get; } = name;

        public List<Func<PlayerId, BitReader, bool>> Handlers { get; } = [];
    }
}

/// <summary>How many broadcasts a connection may send.</summary>
/// <remarks>
///     Smaller numbers than <c>RpcLimits</c> on purpose. A broadcast fans out, so one client's
///     message is one packet per player in the session — which makes it the cheapest thing a client
///     has for making the server do work for everybody else.
/// </remarks>
public sealed record BroadcastLimits {
    /// <summary>How many a second a connection may sustain.</summary>
    public int PerSecond { get; init; } = 10;

    /// <summary>How many it may send at once before the rate matters.</summary>
    public int Burst { get; init; } = 20;
}
