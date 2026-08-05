// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;

namespace Vixen.Live.Realms.Tests;

/// <summary>A realm and however many clients a test wants, all in this process, driven by hand.</summary>
/// <remarks>
///     Doc 16's <c>Transport.Local</c> is what makes this cost a millisecond instead of a port, and
///     what makes admission testable end to end: the handshake a client goes through here is byte
///     for byte the one it goes through over UDP.
/// </remarks>
sealed class RealmFixture : IDisposable {
    /// <summary>One step of the loop these sessions are driven by.</summary>
    public static TimeSpan Step { get; } = TimeSpan.FromMilliseconds(16);

    static readonly byte[] Key = Encoding.UTF8.GetBytes("a-test-cluster-key-of-32-bytes!!!!!!");

    readonly LocalNetwork network = new();
    readonly List<NetworkSession> clients = [];

    /// <summary>Every lifecycle line the realm wrote.</summary>
    public List<string> Output { get; } = [];

    /// <summary>The cluster key both ends of a test share.</summary>
    public TransferTicketSigner Signer { get; } = new(Key);

    /// <summary>The shard.</summary>
    public RealmSpec Spec { get; }

    /// <summary>The realm.</summary>
    public RealmHost Host { get; }

    /// <summary>The realm's clock, which a test moves rather than waits for.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>What the wire does to the packets, on the realm's side and every client's.</summary>
    /// <remarks>
    ///     ⚠ <b>A clean wire cannot fail the interesting way.</b> Admission is a handshake with a
    ///     deadline, and a deadline only means anything when something can be late — so every
    ///     admission test on <c>Perfect</c> proves the two state machines agree with each other and
    ///     nothing about whether they survive a network. Doc 27 § Testing asks for this leg by name.
    /// </remarks>
    public NetworkSimulationProfile Wire { get; }

    /// <summary>The simulation's seed, so a failure is replayable.</summary>
    public ulong Seed { get; }

    public RealmFixture(
        ShardCapacity? capacity = null,
        TimeSpan? idleGrace = null,
        NetworkSimulationProfile? wire = null,
        ulong seed = 20260805
    ) {
        Wire = wire ?? NetworkSimulationProfile.Perfect;
        Seed = seed;

        Spec = new() {
            Shard = ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = new("127.0.0.1", 7777),
            Capacity = capacity ?? new(100, 120),
            TickRate = 30
        };

        Host = new(
            Spec,
            // ⚠ The realm's own stream and each client's are seeded differently — see Connect. One
            // shared stream drops the same packet on both sides of the same step, which is a
            // synchronised outage rather than a network.
            admission => new(Wrap(0), Options(), admission, ownsTransport: true),
            Signer,
            new() {
                Output = Output.Add,
                Now = () => Now,
                IdleGrace = idleGrace ?? TimeSpan.FromSeconds(10),
                HeartbeatInterval = TimeSpan.FromMilliseconds(64)
            }
        );

        Host.Start();
    }

    /// <summary>A ticket this realm will accept.</summary>
    /// <param name="player">Who it admits, or null for somebody new.</param>
    /// <param name="target">Which shard, or null for this one.</param>
    /// <returns>The signed ticket.</returns>
    public TransferTicket Ticket(PlayerKey? player = null, ShardId? target = null) =>
        Signer.Sign(
            new() {
                Player = player ?? new(Guid.NewGuid(), Guid.NewGuid()),
                Target = target ?? Spec.Shard,
                Endpoint = Spec.Endpoint,
                LeaseEpoch = 1,
                Expires = Now + TimeSpan.FromSeconds(30)
            }
        );

    /// <summary>Connects a client presenting a ticket, without pumping.</summary>
    /// <param name="ticket">What it presents, or null for nothing at all.</param>
    /// <returns>The client's session.</returns>
    public NetworkSession Connect(TransferTicket? ticket) {
        var session = new NetworkSession(
            Wrap(clients.Count + 1),
            Options() with {
                AuthenticationPayload = ticket is null ? [] : Encoding.UTF8.GetBytes(ticket.Encode())
            },
            ownsTransport: true
        );

        clients.Add(session);
        session.StartClient();

        return session;
    }

    /// <summary>Runs the realm and every client for a while.</summary>
    /// <param name="rounds">How many steps.</param>
    /// <param name="messages">Where the realm's user payloads go.</param>
    public void Pump(int rounds = 8, ISessionMessageHandler? messages = null) {
        for (var round = 0; round < rounds; round++) {
            Host.Update(Step, scenes: null, messages);

            foreach (var client in clients) {
                client.Update(Step);
            }
        }
    }

    /// <summary>Puts the simulation on a transport, or hands the transport straight back.</summary>
    /// <param name="stream">Which random stream this end gets.</param>
    /// <returns>The transport.</returns>
    /// <remarks>
    ///     ⚠ <b><c>Perfect</c> is not wrapped at all rather than wrapped in a no-op.</b> The
    ///     simulation queues and re-times every payload even when it drops none, so wrapping the
    ///     default would change the timing of every existing test in this project for no reason.
    /// </remarks>
    ITransport Wrap(int stream) =>
        Wire == NetworkSimulationProfile.Perfect
            ? new LocalTransport(network)
            : new NetworkSimulation(new LocalTransport(network), Wire, Seed + (ulong)stream);

    /// <summary>Marks the map up, as the host's startup scene would have.</summary>
    /// <remarks>
    ///     A test has no content and no world, so the scene lookup <see cref="MapLifetime.Resolve" />
    ///     does has nothing to find. This is the other door into the same state, and it is the one a
    ///     persistent shard rehydrating authored state uses in production too.
    /// </remarks>
    public void MapIsUp() => Host.Map.Ready(new(1));

    public void Dispose() {
        foreach (var client in clients) {
            client.Dispose();
        }

        Host.Session.Dispose();
        Host.Dispose();
        Signer.Dispose();
    }

    /// <summary>Records what the realm was told, so a test can read it back.</summary>
    public sealed class PayloadRecorder : ISessionMessageHandler {
        /// <summary>Every payload, decoded.</summary>
        public List<string> Texts { get; } = [];

        /// <inheritdoc />
        public void OnMessage(PlayerId from, Vixen.Net.Channel channel, ReadOnlySpan<byte> payload) =>
            Texts.Add(Encoding.UTF8.GetString(payload));
    }

    SessionOptions Options() =>
        new() {
            MaxPlayers = Spec.Capacity.HardCap,
            ContentHash = Spec.Key.Version.Content,
            AuthenticationTimeout = TimeSpan.FromSeconds(5)
        };
}
