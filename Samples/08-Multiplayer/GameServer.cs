// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
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

    // What each connection is told about, rather than everything there is. See `Interest`.
    readonly InterestGrid grid;
    readonly ExplicitInterestRule overrides = new();
    readonly InterestChain interest;
    readonly List<Entity> observed = [];

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

    /// <summary>What decides who is told about what.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An <see cref="InterestGrid" /> is the chain's <i>source</i> and
    ///         <see cref="ExplicitInterestRule" /> is a rule after it, and the order is not
    ///         decoration.</b> A rule only ever sees the candidates the source produced, so
    ///         <c>Show</c> can keep something visible that a later rule would have hidden and cannot
    ///         resurrect an object the grid never offered. In an arena forty metres across with a
    ///         ninety-six metre radius nothing is ever out of range, which is the point of the
    ///         default: the chain runs, the grid buckets, and the match still converges. Narrow the
    ///         radius (<c>--interest-radius</c>) and fighters start being hidden — and
    ///         <c>LocalMatch</c> then checks that a client holds exactly what the chain says it
    ///         should and nothing else.
    ///     </para>
    /// </remarks>
    public InterestChain Interest => interest;

    /// <summary>Where the candidates come from.</summary>
    public InterestGrid Grid => grid;

    /// <summary>Stands a server up.</summary>
    /// <param name="transport">What it listens on. Disposed with the session.</param>
    /// <param name="options">
    ///     The session's settings. The content hash is filled in from the two manifests, so a peer
    ///     built against different components or different calls is refused at the handshake rather
    ///     than at the first packet that means something different to each of them.
    /// </param>
    /// <param name="interestRadius">
    ///     How far a player is told about things. The arena is eighty metres across, so the default
    ///     ninety-six sees all of it and the chain hides nothing — which is what keeps the default
    ///     run's convergence check about replication rather than about interest.
    /// </param>
    public GameServer(ITransport transport, SessionOptions? options = null, float interestRadius = 96f) {
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

        // A third of the radius, which is what InterestGrid's own remarks ask for: large enough that
        // a query walks a handful of cells, small enough that most of what it finds is genuinely
        // close.
        grid = new() { CellSize = MathF.Max(1f, interestRadius / 3f), Radius = interestRadius };
        interest = new() { Source = grid, Rules = { overrides } };

        replication = new(registry, interest);
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

    /// <summary>What the chain says one player is entitled to be told about, right now.</summary>
    /// <param name="player">Whose view.</param>
    /// <param name="into">Filled with the network ids. Cleared first.</param>
    /// <remarks>
    ///     The same call <see cref="ReplicationServer" /> makes per connection, run again by whoever
    ///     is checking the clients. Asking the chain rather than remembering what was sent is what
    ///     makes the check a statement about the interest wiring: a client holding an object the
    ///     chain says is hidden, or missing one it says is observed, is a disagreement either way.
    /// </remarks>
    public void Observed(PlayerId player, HashSet<uint> into) {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        observed.Clear();
        interest.Resolve(world, player, observed);

        foreach (var entity in observed) {
            if (world.TryGet<NetworkId>(entity, out var id)) {
                into.Add(id.Value);
            }
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
            var fighter = arena.Spawn(player);

            // The override the rule exists for, in the one direction it can work: a player is never
            // told to stop watching their own avatar, whatever the grid would have said about the
            // distance between them and themselves. ⚠ The other direction does not work at all —
            // Show cannot resurrect an object the source never offered as a candidate, which is
            // every example in ExplicitInterestRule's own remarks. Issue #1042.
            overrides.Show(player, fighter.Id);
        }

        foreach (var player in leaving) {
            arena.Remove(player);

            // Three maps keyed by player, and a match that ran for a day would leak all three.
            grid.Forget(player);
            overrides.Forget(player);
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

        // ⚠ Once a tick and before any connection is resolved, which is the entire reason the grid
        // is a source and not a rule: written as a rule this would be one distance test per object
        // per player, and it is instead one sweep of the world shared by everybody. A rebuild that
        // ran per connection would pass every test and scale like the thing it replaces.
        grid.Rebuild(world);

        foreach (var fighter in arena.Fighters) {
            // Their own avatar is where they are looking from. A spectator's camera would go here
            // instead, and a player with no viewpoint at all is told about everything and counted.
            grid.SetViewpoint(fighter.Player, world.Read<NetworkTransform>(fighter.Entity).Position);
        }

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
