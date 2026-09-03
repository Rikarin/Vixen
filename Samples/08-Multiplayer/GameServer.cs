// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Engine.Frames;
using Vixen.Net;
using Vixen.Net.Diagnostics;
using Vixen.Net.Engine;
using Vixen.Net.Generated;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Vixen.Net.Time;
using Vixen.Net.Transport;

namespace Vixen.Samples.Multiplayer;

/// <summary>The authority: one world, one clock, and one copy of the rules.</summary>
/// <remarks>
///     <para>
///         Every layer of <c>Vixen.Net</c> meets here, and the shape of a tick is the point of the
///         whole sample: refill the rate limits, advance the world's version, apply the joins and
///         leaves that arrived, run the game, capture what changed once, and write one snapshot per
///         connection out of that capture.
///     </para>
///     <para>
///         <b>The order of the first two and the capture is load-bearing.</b>
///         <c>ReplicationServer.Capture</c> takes everything written since the previous capture, so a
///         write that lands on the far side of <c>AdvanceVersion</c> is simply never sent — no error,
///         no warning, just a client that never learns about it. That is why joins are queued out of
///         the session's event and applied inside <see cref="Step" /> rather than where they arrive:
///         a player spawned from the event handler would be spawned before the advance, and would be
///         invisible to everybody until the next thing about them changed.
///     </para>
/// </remarks>
internal sealed class GameServer : ISessionMessageHandler, IDisposable {
    readonly NetworkSession session;
    readonly World world = new("server");

    // The behaviour half. `EngineLoop` is given this world rather than making its own, because the
    // arena writes into it directly and a second world would be a second copy of the match.
    readonly EngineLoop loop;
    readonly ReplicationRegistry registry = new();
    readonly RpcManifest manifest = new();
    readonly ReplicationServer replication;
    readonly RpcRouter router;
    readonly NetworkIdAllocator ids = new();
    readonly Arena arena;
    readonly TimeSpan tickDuration;
    readonly byte[] snapshot = new byte[2048];
    readonly byte[] envelope = new byte[2049];
    readonly List<PlayerId> joining = [];
    readonly List<PlayerId> leaving = [];
    readonly byte[] lastSnapshot = new byte[2048];

    int lastSnapshotLength;

    /// <summary>The session, for whoever is driving this.</summary>
    public NetworkSession Session => session;

    /// <summary>The game.</summary>
    public Arena Arena => arena;

    /// <summary>The authoritative world, for whoever is checking the clients against it.</summary>
    public World World => world;

    /// <summary>The tick the authority is on.</summary>
    public Tick Tick => session.Tick;

    /// <summary>How many snapshots have gone out.</summary>
    public long SnapshotCount { get; private set; }

    /// <summary>How many bytes those snapshots were, before the transport's own framing.</summary>
    public long SnapshotBytes { get; private set; }

    /// <summary>How many ticks have been simulated.</summary>
    public long StepCount { get; private set; }

    /// <summary>Records sent as a difference from what the connection already held.</summary>
    public long DeltaRecordCount => replication.DeltaRecordCount;

    /// <summary>Records sent whole.</summary>
    public long WholeRecordCount => replication.WholeRecordCount;

    /// <summary>Where the bandwidth went. Attached from the start, because it is nearly free.</summary>
    public BandwidthLedger Ledger { get; } = new();

    /// <summary>The component types, so a snapshot can be taken apart for a report.</summary>
    public ReplicationRegistry Registry => registry;

    /// <summary>Payloads that arrived claiming to be a snapshot, which only a server sends.</summary>
    public long BogusPayloadCount { get; private set; }

    /// <summary>Stands a server up.</summary>
    /// <param name="transport">What it listens on. Disposed with the session.</param>
    /// <param name="options">
    ///     The session's settings. The content hash is filled in from the two manifests, so a peer
    ///     built against different components or different calls is refused at the handshake rather
    ///     than at the first packet that means something different to each of them.
    /// </param>
    public GameServer(ITransport transport, SessionOptions? options = null) {
        loop = new(world);

        // ⚠ Added by hand, and it has to be: the sweep is what turns a SyncVar written from ordinary
        // behaviour code into an entity the capture will look at, and without it the write stays on
        // the server for ever with nothing saying so. It is not a default system because most games
        // are not networked.
        loop.Add(new SyncStateSweepSystem(loop.Behaviors));

        ReplicatedComponents.RegisterAll(registry);
        registry.Register(new NetworkTransformReplicator());

        // The behaviour's two records: its fields, and its lists. Two replicators rather than one so
        // that a killfeed appended to does not re-send a streak, and a streak does not re-send the
        // feed — the same argument Combatant and Vitals make one layer down.
        registry.Register(new SyncStateReplicator<FighterScore>(loop.Behaviors));
        registry.Register(new SyncListReplicator<FighterScore>(loop.Behaviors));

        RpcMethods.RegisterAll(manifest);

        var settings = (options ?? new()) with {
            MaxPlayers = 8,
            ContentHash = ((ulong)registry.ManifestHash << 32) | manifest.ManifestHash
        };

        session = new(transport, settings, ownsTransport: true);
        replication = new(registry);
        router = new(manifest, new SessionRpcTransport(session), RpcRole.Server);
        arena = new(world, ids, replication, router, settings.TickRate, loop.Behaviors);
        tickDuration = settings.TickRate.Duration;

        replication.Ledger = Ledger;
        router.Ledger = Ledger;

        session.PlayerJoined += player => joining.Add(player.Id);
        session.PlayerLeft += (player, _) => leaving.Add(player.Id);
    }

    /// <summary>Starts listening.</summary>
    public void StartServer() => session.StartServer();

    /// <summary>Runs the server for a frame.</summary>
    /// <param name="elapsed">How long since the last one.</param>
    /// <returns>How many ticks were simulated.</returns>
    public int Update(TimeSpan elapsed) {
        var ticks = session.Update(elapsed, this);

        for (var i = 0; i < ticks; i++) {
            Step();
        }

        return ticks;
    }

    /// <inheritdoc />
    public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) {
        if (!NetworkPayload.TryUnwrap(payload, out var kind, out var inner)) {
            return;
        }

        switch (kind) {
            case PayloadKind.Rpc:
                router.Receive(from, inner);

                break;

            case PayloadKind.Game:
                if (MatchProtocol.TryReadAcknowledgement(inner, out var applied)) {
                    replication.Acknowledge(from, applied);
                }

                break;

            default:
                // Only a server writes snapshots. One arriving here is a client trying it on, or a
                // bug — either way it is counted rather than parsed.
                BogusPayloadCount++;

                break;
        }
    }

    /// <summary>The last snapshot that went out, for taking apart.</summary>
    public ReadOnlySpan<byte> LastSnapshot => lastSnapshot.AsSpan(0, lastSnapshotLength);

    /// <summary>Stops the session and the transport under it.</summary>
    public void Dispose() => session.Dispose();

    void Step() {
        router.Advance(tickDuration);
        world.AdvanceVersion();

        foreach (var player in joining) {
            arena.Spawn(player);
        }

        foreach (var player in leaving) {
            arena.Remove(player);
        }

        joining.Clear();
        leaving.Clear();

        arena.Step();

        // ⚠ After the arena and before the capture, and neither half of that is arbitrary. The
        // behaviours read state the arena has just written — FighterScore notices a fighter whose
        // health reached zero — and the sweep at the end of LateUpdate is what marks what they wrote.
        // A frame run after the capture would ship every behaviour change one tick late, which
        // presents as a scoreboard that lags the kill rather than as a bug.
        loop.Frame(tickDuration);

        Ledger.Advance(tickDuration);

        // Once, whatever the player count. What each connection gets is a copy of these bits minus
        // what it has already acknowledged — fifty players cost fifty memcpys and one encode.
        replication.Capture(world, session.Tick);
        Broadcast();

        StepCount++;
    }

    void Broadcast() {
        foreach (var player in session.Players) {
            if (!player.IsConnected) {
                continue;
            }

            if (!replication.TryWriteSnapshot(world, player.Id, session.Tick, snapshot, out var bits)) {
                // Nothing changed that this connection has not acknowledged. A tick that says
                // nothing costs nothing, which is what makes an idle match free.
                continue;
            }

            if (!NetworkPayload.TryWrap(PayloadKind.Replication, bits, envelope, out var wrapped)) {
                continue;
            }

            session.SendToPlayer(player.Id, wrapped, Channel.Unreliable);
            SnapshotCount++;
            SnapshotBytes += wrapped.Length;

            // Kept so the report can take one apart. A packet inspector on a live connection is the
            // same call on a copy of the bytes; there is nothing else to it.
            lastSnapshotLength = bits.Length;
            bits.CopyTo(lastSnapshot);
        }
    }
}
