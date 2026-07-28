// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Generated;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Vixen.Net.Time;
using Vixen.Net.Transport;

namespace Vixen.Samples.Multiplayer;

/// <summary>What smoothing had to do, added up across every object a client is holding.</summary>
/// <param name="Interpolated">Samples taken between two snapshots, which is the healthy case.</param>
/// <param name="Extrapolated">Samples guessed past the newest snapshot, because none had arrived.</param>
/// <param name="Snapped">Samples that gave up interpolating, because the jump was too far to walk.</param>
/// <param name="Starved">Samples with nothing to sit between, which is a buffer that ran dry.</param>
internal readonly record struct MotionCounters(long Interpolated, long Extrapolated, long Snapped, long Starved);

/// <summary>A player: a copy of the world it did not decide, and one fighter it may ask about.</summary>
/// <remarks>
///     <para>
///         Its world holds the same components as the server's and none of the rules. Nothing here
///         moves anything: positions arrive as snapshots, and what is <i>drawn</i> is
///         <see cref="TryView" /> — the snapshot buffer sampled at the interpolation tick, which is
///         deliberately behind the server so that there are always two snapshots to sit between.
///     </para>
///     <para>
///         The bot in <see cref="SendInput" /> is a function of the tick and the player id, with no
///         randomness anywhere: a local match is therefore the same match every run, and a failure
///         is reproducible without a recording.
///     </para>
/// </remarks>
internal sealed class GameClient : ISessionMessageHandler, IDisposable {
    const int FireEveryTicks = 11;
    const float CloseRange = 12f;

    readonly NetworkSession session;
    readonly World world = new("client");
    readonly ReplicationRegistry registry = new();
    readonly RpcManifest manifest = new();
    readonly ReplicationClient replication;
    readonly RpcRouter router;
    readonly TimeSpan tickDuration;
    readonly QueryDescription networked = new QueryDescription().WithAll<NetworkId, NetworkTransform>();
    readonly Dictionary<uint, AvatarController> controllers = [];
    readonly Dictionary<uint, SnapshotBuffer> buffers = [];
    readonly HashSet<uint> present = [];
    readonly List<uint> departed = [];
    readonly byte[] acknowledgement = new byte[MatchProtocol.MaxBytes];
    readonly byte[] envelope = new byte[MatchProtocol.MaxBytes + 1];

    NetworkId self;

    /// <summary>The session.</summary>
    public NetworkSession Session => session;

    /// <summary>This client's copy of the world.</summary>
    public World World => world;

    /// <summary>What it has applied.</summary>
    public ReplicationClient Replication => replication;

    /// <summary>The fighter this player owns, or <see cref="NetworkId.None" /> before it arrives.</summary>
    public NetworkId Self => self;

    /// <summary>Whether to stop asking for anything. Set during the settle phase of a local match.</summary>
    public bool Idle { get; set; }

    /// <summary>Payload bytes taken off the session, before the transport's own framing.</summary>
    public long BytesReceived { get; private set; }

    /// <summary>Snapshots that arrived and decoded.</summary>
    public long SnapshotsApplied { get; private set; }

    /// <summary>Remote calls this client ran.</summary>
    public long CallsRun => router.AcceptedCount;

    /// <summary>Hits this client was told about, across every fighter it holds.</summary>
    public int HitsSeen {
        get {
            var total = 0;

            foreach (var controller in controllers.Values) {
                total += controller.HitsSeen;
            }

            return total;
        }
    }

    /// <summary>How many networked entities it is holding.</summary>
    public int EntityCount => replication.EntityCount;

    /// <summary>What the snapshot buffers did, added up across every object.</summary>
    public MotionCounters Motion {
        get {
            var counters = default(MotionCounters);

            foreach (var buffer in buffers.Values) {
                counters = new(
                    counters.Interpolated + buffer.InterpolatedCount,
                    counters.Extrapolated + buffer.ExtrapolatedCount,
                    counters.Snapped + buffer.SnappedCount,
                    counters.Starved + buffer.StarvedCount
                );
            }

            return counters;
        }
    }

    /// <summary>Stands a client up.</summary>
    /// <param name="transport">What it connects over. Disposed with the session.</param>
    /// <param name="options">The session's settings. The content hash is filled in as the server's is.</param>
    public GameClient(ITransport transport, SessionOptions? options = null) {
        ReplicatedComponents.RegisterAll(registry);
        registry.Register(new NetworkTransformReplicator());
        RpcMethods.RegisterAll(manifest);

        var settings = (options ?? new()) with {
            MaxPlayers = 8,
            ContentHash = ((ulong)registry.ManifestHash << 32) | manifest.ManifestHash
        };

        session = new(transport, settings, ownsTransport: true);
        replication = new(registry);
        router = new(manifest, new SessionRpcTransport(session), RpcRole.Client);
        tickDuration = settings.TickRate.Duration;
    }

    /// <summary>Connects.</summary>
    public void StartClient() => session.StartClient();

    /// <summary>Runs the client for a frame.</summary>
    /// <param name="elapsed">How long since the last one.</param>
    /// <returns>How many ticks were run.</returns>
    public int Update(TimeSpan elapsed) {
        var ticks = session.Update(elapsed, this);

        for (var i = 0; i < ticks; i++) {
            router.Advance(tickDuration);
            Reconcile();
            Observe();
            SendInput();
        }

        return ticks;
    }

    /// <inheritdoc />
    public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) {
        BytesReceived += payload.Length;

        if (!NetworkPayload.TryUnwrap(payload, out var kind, out var inner)) {
            return;
        }

        switch (kind) {
            case PayloadKind.Replication:
                Apply(inner);

                break;

            case PayloadKind.Rpc:
                router.Receive(from, inner);

                break;

            default:
                break;
        }
    }

    /// <summary>Where this client would draw something, as opposed to where it last heard it was.</summary>
    /// <param name="id">Which object.</param>
    /// <param name="position">Where to draw it.</param>
    /// <returns>Whether anything has been heard about it at all.</returns>
    /// <remarks>
    ///     The interpolation tick rather than the current one: far enough behind the server that two
    ///     snapshots usually straddle it, which is what makes motion smooth without predicting
    ///     anything. <c>TickManager</c> derives the delay from measured jitter, so a good connection
    ///     is not made to wait for a bad one's worth of buffer.
    /// </remarks>
    public bool TryView(NetworkId id, out Vector3 position) {
        position = default;

        if (!buffers.TryGetValue(id.Value, out var buffer)
            || !buffer.TrySample(session.Clock.InterpolationTick, session.Clock.Alpha, out var sample)) {
            return false;
        }

        position = sample.Position;

        return true;
    }

    /// <summary>What the last snapshot said about something, without interpolation.</summary>
    /// <param name="id">Which object.</param>
    /// <param name="transform">Its transform.</param>
    /// <returns>Whether this client holds it.</returns>
    public bool TryLatest(NetworkId id, out NetworkTransform transform) {
        transform = default;

        return replication.TryGetEntity(id, out var entity)
            && world.IsAlive(entity)
            && world.TryGet(entity, out transform);
    }

    /// <summary>What the last snapshot said about somebody's health and score.</summary>
    /// <param name="id">Which object.</param>
    /// <param name="vitals">Their vitals.</param>
    /// <returns>Whether this client holds them.</returns>
    public bool TryVitals(NetworkId id, out Vitals vitals) {
        vitals = default;

        return replication.TryGetEntity(id, out var entity)
            && world.IsAlive(entity)
            && world.TryGet(entity, out vitals);
    }

    /// <summary>Stops the session and the transport under it.</summary>
    public void Dispose() => session.Dispose();

    void Apply(ReadOnlySpan<byte> snapshot) {
        if (!replication.TryApply(world, snapshot)) {
            // Not acknowledged, on purpose. The server's baseline does not advance, so everything in
            // the snapshot that failed comes again — a decode failure costs a tick rather than a
            // desync.
            return;
        }

        SnapshotsApplied++;

        if (MatchProtocol.TryWriteAcknowledgement(replication.AppliedTick, acknowledgement, out var message)
            && NetworkPayload.TryWrap(PayloadKind.Game, message, envelope, out var wrapped)) {
            session.SendToServer(wrapped, MatchProtocol.AckChannel);
        }
    }

    void Reconcile() {
        present.Clear();

        var mine = session.LocalPlayer?.Id.Value ?? 0u;

        foreach (var chunk in world.Chunks(networked)) {
            foreach (var entity in chunk.Entities) {
                var id = world.Read<NetworkId>(entity);

                if (!id.IsValid) {
                    continue;
                }

                present.Add(id.Value);
                Track(entity, id);

                if (mine != 0 && world.TryGet<Combatant>(entity, out var combatant) && combatant.Owner == mine) {
                    self = id;
                }
            }
        }

        Forget();
    }

    void Track(Entity entity, NetworkId id) {
        if (!controllers.ContainsKey(id.Value)) {
            // No Arena: a client's controller has nothing to act on, because the handlers that would
            // act are the ones a client is refused permission to run.
            var controller = new AvatarController(id, router);
            controllers[id.Value] = controller;
            router.Register(id, controller);
        }

        if (!buffers.TryGetValue(id.Value, out var buffer)) {
            buffer = new();
            buffers[id.Value] = buffer;
        }

        if (!replication.HasApplied) {
            return;
        }

        // Every networked object gets a sample every applied tick, whether or not its transform was
        // in that snapshot. "It did not change" is as much a fact about where it is at that tick as
        // a new position would be, and the buffer drops anything that is not newer than what it
        // already holds, so re-adding is free.
        ref readonly var transform = ref world.Read<NetworkTransform>(entity);
        buffer.Add(new(replication.AppliedTick, transform.Position, transform.Rotation));
    }

    void Forget() {
        departed.Clear();

        foreach (var id in controllers.Keys) {
            if (!present.Contains(id)) {
                departed.Add(id);
            }
        }

        foreach (var id in departed) {
            if (controllers.Remove(id, out var controller)) {
                router.Unregister(new(id), controller);
            }

            buffers.Remove(id);

            if (self.Value == id) {
                self = NetworkId.None;
            }
        }
    }

    /// <summary>Asks where everything is, which is what a render pass does and nothing else does.</summary>
    /// <remarks>
    ///     Called once a tick so that the smoothing counters mean something. A client that filled its
    ///     snapshot buffers and never sampled them would be a client that had not tested the half of
    ///     the motion layer players actually see.
    /// </remarks>
    void Observe() {
        foreach (var id in buffers.Keys) {
            TryView(new(id), out _);
        }
    }

    /// <summary>Chases the nearest fighter, circles it once close, and shoots at it.</summary>
    /// <remarks>
    ///     <para>
    ///         Everything it decides comes from <b>this client's copy of the world</b>, which is the
    ///         only thing a player has either. That is also where the missing lag compensation turns
    ///         into a number rather than a paragraph: the aim is computed from the newest snapshot,
    ///         which is already half a round trip old, and the server resolves the shot against where
    ///         the target is when the call lands. Run the local match at <c>--latency 0</c> and at
    ///         <c>--latency 60</c> and compare the hit counts; the difference is what lag
    ///         compensation would give back.
    ///     </para>
    ///     <para>
    ///         A function of the tick and the player id, with no randomness: a local match is the
    ///         same match every run.
    ///     </para>
    /// </remarks>
    void SendInput() {
        if (Idle || !self.IsValid || !controllers.TryGetValue(self.Value, out var controller)) {
            return;
        }

        if (!TryLatest(self, out var here)) {
            return;
        }

        var move = Wander();
        var aim = 0f;

        if (TryNearest(here.Position, out var toward)) {
            aim = MathF.Atan2(toward.X, toward.Z);

            // Close in, then circle. Two fighters walking into each other and stopping is a pair
            // that never moves again, which replicates nothing and proves nothing.
            var sideways = session.LocalPlayer!.Id.Value % 2 == 0 ? 1f : -1f;

            move = Vector3.Normalize(
                toward.Length() > CloseRange ? toward : new(-toward.Z * sideways, 0f, toward.X * sideways)
            );
        }

        controller.Rpc.Steer(move.X, move.Z, aim);

        if (session.Tick.Value % FireEveryTicks == 0) {
            controller.Rpc.Fire();
        }
    }

    /// <summary>Where the nearest other fighter is, relative to here.</summary>
    /// <param name="from">Where this client's fighter is, as it last heard.</param>
    /// <param name="toward">The offset to the nearest other one.</param>
    /// <returns>Whether there is one, far enough away to have a direction.</returns>
    bool TryNearest(in Vector3 from, out Vector3 toward) {
        var nearest = float.MaxValue;

        toward = Vector3.Zero;

        foreach (var id in buffers.Keys) {
            if (id == self.Value || !TryLatest(new(id), out var other)) {
                continue;
            }

            var offset = other.Position - from;
            var distance = offset.LengthSquared();

            if (distance < nearest && distance > 0.01f) {
                nearest = distance;
                toward = offset;
            }
        }

        return nearest < float.MaxValue;
    }

    /// <summary>A circle, for a client with nobody to fight.</summary>
    Vector3 Wander() {
        var angle = (session.Tick.Value * 0.06f) + (session.LocalPlayer!.Id.Value * 0.8f);

        return new(MathF.Cos(angle), 0f, MathF.Sin(angle));
    }
}
