// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;

namespace Vixen.Net.Transport;

/// <summary>
///     Wraps a transport and makes it worse, on purpose and reproducibly: latency, jitter, loss,
///     duplication, and the reordering that falls out of jitter.
/// </summary>
/// <remarks>
///     <para>
///         Netcode developed on localhost is netcode that has never been tested. This is the thing
///         that fixes it, and it is a decorator rather than an option on each transport so that
///         there is exactly one implementation of "bad network" to trust, and it applies to the
///         in-process transport as readily as to a socket.
///     </para>
///     <para>
///         <b>It is a pure function of the calls made to it.</b> The delay budget is spent against
///         the virtual clock <see cref="Poll" /> advances, never against a wall clock, and every
///         random decision comes from a seed the caller supplies. The same seed, the same profile
///         and the same sequence of sends and polls produce the same deliveries on every machine and
///         every run — which is what makes "the bug that only happens at 20 % loss" a test rather
///         than an anecdote.
///     </para>
///     <para>
///         <b>Channel contracts are respected.</b> A <see cref="Channel.Reliable" /> payload is
///         delayed but never lost, never duplicated, and never overtaken by a later one; a
///         <see cref="Channel.Sequenced" /> one may be lost but not reordered. The simulation only
///         does what the real world is allowed to do to that channel, so the layer above is
///         exercised against its contract and not against a violation of it.
///     </para>
/// </remarks>
public sealed class NetworkSimulation : ITransport {
    readonly ITransport inner;
    readonly bool ownsInner;
    readonly PriorityQueue<Pending, (long Due, long Order)> pending = new();
    readonly Dictionary<OrderedKey, long> orderedDue = [];

    NetworkSimulationProfile profile;
    DeterministicRandom random;
    long now;
    long order;

    /// <summary>The transport being made worse.</summary>
    public ITransport Inner => inner;

    /// <summary>How bad it is currently pretending to be. May be changed while running.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A chance is outside 0…1, or a delay is negative.</exception>
    public NetworkSimulationProfile Profile {
        get => profile;
        set {
            Validate(value);
            profile = value;
        }
    }

    /// <summary>Payloads handed to the inner transport so far.</summary>
    public long SentPayloadCount { get; private set; }

    /// <summary>Payloads thrown away so far.</summary>
    public long DroppedPayloadCount { get; private set; }

    /// <summary>Extra copies sent so far.</summary>
    public long DuplicatedPayloadCount { get; private set; }

    /// <summary>Payloads waiting out their delay right now.</summary>
    public int PendingPayloadCount => pending.Count;

    /// <inheritdoc />
    /// <remarks>
    ///     The inner transport's, except that a simulation with anything to inject is lossy whatever
    ///     it wraps — which is the whole point of wrapping it.
    /// </remarks>
    public TransportCapabilities Capabilities {
        get {
            var capabilities = inner.Capabilities;

            return capabilities with {
                IsLossy = capabilities.IsLossy
                    || profile.LossChance > 0
                    || profile.DuplicateChance > 0
                    || profile.Jitter > TimeSpan.Zero
            };
        }
    }

    /// <inheritdoc />
    public TransportState ServerState => inner.ServerState;

    /// <inheritdoc />
    public TransportState ClientState => inner.ClientState;

    /// <summary>Wraps a transport.</summary>
    /// <param name="inner">The transport to send through.</param>
    /// <param name="profile">What to inject.</param>
    /// <param name="seed">
    ///     The seed for every random decision. Required rather than defaulted: a simulation whose
    ///     seed was picked for you is a simulation whose failures you cannot reproduce, and the
    ///     five seconds spent typing a number is the price of every future bug report being
    ///     replayable.
    /// </param>
    /// <param name="ownsInner">
    ///     Whether disposing this disposes <paramref name="inner" />. True by default, because the
    ///     usual construction is <c>new NetworkSimulation(new LocalTransport(…), …)</c> and nothing
    ///     else holds the inner one.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A chance is outside 0…1, or a delay is negative.</exception>
    public NetworkSimulation(
        ITransport inner,
        NetworkSimulationProfile profile,
        ulong seed,
        bool ownsInner = true
    ) {
        Validate(profile);

        this.inner = inner;
        this.profile = profile;
        this.ownsInner = ownsInner;
        random = new(seed);
    }

    /// <inheritdoc />
    public void StartServer() => inner.StartServer();

    /// <inheritdoc />
    public void StopServer() => inner.StopServer();

    /// <inheritdoc />
    public void StartClient() => inner.StartClient();

    /// <inheritdoc />
    public void StopClient() => inner.StopClient();

    /// <inheritdoc />
    /// <remarks>
    ///     Immediate, and so is every other control operation. Only payloads are delayed: a
    ///     disconnect is something this process decided, not something that has to cross a network,
    ///     and payloads still in flight to a connection that has gone are dropped by the inner
    ///     transport when they land, which is what a real one does too.
    /// </remarks>
    public void Disconnect(ConnectionId connection) => inner.Disconnect(connection);

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) =>
        Enqueue(toServer: false, connection, payload, channel);

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) =>
        Enqueue(toServer: true, ConnectionId.None, payload, channel);

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed" /> is negative.</exception>
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        if (elapsed < TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Time does not run backwards.");
        }

        now += elapsed.Ticks;
        Release();
        inner.Poll(elapsed, events);
    }

    /// <summary>Drops everything still in flight and disposes the inner transport if it owns it.</summary>
    public void Dispose() {
        while (pending.TryDequeue(out var packet, out _)) {
            ArrayPool<byte>.Shared.Return(packet.Buffer);
        }

        if (ownsInner) {
            inner.Dispose();
        }
    }

    void Enqueue(bool toServer, ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        var max = inner.Capabilities.MaxPayloadBytes;

        if (payload.Length > max) {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"The transport being simulated carries at most {max} bytes."
            );
        }

        // The draws happen in a fixed order — loss, delay, duplication — and only for the channels
        // whose contract allows that outcome. That is what keeps a run reproducible: the sequence of
        // sends decides the sequence of draws, and nothing else does.
        if (channel.MayDrop() && profile.LossChance > 0 && random.NextDouble() < profile.LossChance) {
            DroppedPayloadCount++;

            return;
        }

        Schedule(toServer, connection, payload, channel);

        if (channel.MayDuplicate() && profile.DuplicateChance > 0 && random.NextDouble() < profile.DuplicateChance) {
            DuplicatedPayloadCount++;
            Schedule(toServer, connection, payload, channel);
        }
    }

    void Schedule(bool toServer, ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        var due = now + profile.Latency.Ticks + random.NextSigned(profile.Jitter.Ticks);

        if (due < now) {
            due = now;
        }

        if (channel.IsOrdered()) {
            // An ordered channel may be delayed but never overtaken, so a payload cannot come due
            // before the one in front of it however the jitter fell.
            var key = new OrderedKey(toServer, connection, channel);

            if (orderedDue.TryGetValue(key, out var previous) && due < previous) {
                due = previous;
            }

            orderedDue[key] = due;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        pending.Enqueue(new(toServer, connection, channel, buffer, payload.Length), (due, order++));
    }

    void Release() {
        while (pending.TryPeek(out _, out var priority) && priority.Due <= now) {
            var packet = pending.Dequeue();

            try {
                if (packet.ToServer) {
                    inner.SendToServer(packet.Buffer.AsSpan(0, packet.Length), packet.Channel);
                } else {
                    inner.SendToClient(packet.Connection, packet.Buffer.AsSpan(0, packet.Length), packet.Channel);
                }

                SentPayloadCount++;
            } finally {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
    }

    static void Validate(NetworkSimulationProfile profile) {
        ArgumentOutOfRangeException.ThrowIfNegative(profile.Latency.Ticks, nameof(profile));
        ArgumentOutOfRangeException.ThrowIfNegative(profile.Jitter.Ticks, nameof(profile));

        if (profile.LossChance is < 0 or > 1) {
            throw new ArgumentOutOfRangeException(nameof(profile), profile.LossChance, "LossChance is a 0…1 chance.");
        }

        if (profile.DuplicateChance is < 0 or > 1) {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.DuplicateChance,
                "DuplicateChance is a 0…1 chance."
            );
        }
    }

    readonly record struct OrderedKey(bool ToServer, ConnectionId Connection, Channel Channel);

    readonly record struct Pending(
        bool ToServer,
        ConnectionId Connection,
        Channel Channel,
        byte[] Buffer,
        int Length
    );
}
