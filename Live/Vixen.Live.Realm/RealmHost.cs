// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Engine.Scenes;
using Vixen.Live.Transfer;
using Vixen.Net.Sessions;

namespace Vixen.Live.Realms;

/// <summary>What a realm host is allowed to differ in.</summary>
public sealed record RealmHostOptions {
    /// <summary>Where lifecycle signals are written.</summary>
    /// <remarks>
    ///     Standard output, because that is what every one of ADR-019's three backends can already
    ///     read — <c>Process</c> directly, Docker through the container's streams, Kubernetes through
    ///     the pod's. A test supplies a list instead.
    /// </remarks>
    public Action<string> Output { get; init; } = Console.Out.WriteLine;

    /// <summary>How often health is sampled.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = RealmHeartbeat.DefaultInterval;

    /// <summary>
    ///     How long a drained shard with nobody left on it waits before stopping.
    /// </summary>
    /// <remarks>
    ///     Not zero. A player who was moved out may have a reconnect in flight, and a shard that
    ///     vanished the instant its population reached zero would turn a lost packet into a lost
    ///     session. Doc 27 § Shard kinds calls this the idle grace.
    /// </remarks>
    public TimeSpan IdleGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The realm's clock. Replaceable so a test does not have to wait.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>How long each step of a transfer may take before it is given up on.</summary>
    /// <remarks>
    ///     ⚠ <b>The overlap deadline is the one worth tuning, and it is a content decision.</b> It is
    ///     how long a client gets to download and load the target's map while still playing here, so
    ///     a game whose maps are two gigabytes wants longer than one whose maps are two hundred
    ///     megabytes. Cutting it short turns a slow connection into a failed map change.
    /// </remarks>
    public TransferDeadlines Transfers { get; init; } = new();
}

/// <summary>A shard, as the process carrying it sees itself.</summary>
/// <remarks>
///     <para>
///         A realm is a normal Vixen application ([doc 17](../../docs/plan/17-app-heads-and-shipping.md)
///         Model B) built with <c>BuildVariant.Server</c>: headless host, <c>Vixen.Graphics.Null</c>,
///         server content profile. What this adds is everything doc 27 § The realm lists — the spec
///         it booted from, the door, the map's lifetime, the heartbeat, and the one place a control
///         plane is ever called.
///     </para>
///     <para>
///         ⚠ <b><see cref="Update" /> is the realm's <c>PreUpdate</c> and runs before the game's own
///         step.</b> The order inside it is load-bearing: answers from the control plane are applied
///         first, so a frame sees a consistent view of decisions taken since the last one; signals
///         are read next, because a drain that arrived should stop admitting before the session is
///         polled; and health is sampled last, over a tick that has actually happened.
///     </para>
///     <para>
///         ⚠ <b>It does not own the game.</b> Replication, RPC, interest and the world belong to the
///         realm's own <see cref="Session" /> and its systems. This class knows about players only
///         insofar as admission and drain readiness need it to.
///     </para>
/// </remarks>
public sealed class RealmHost : IDisposable {
    readonly ConcurrentQueue<string> signals = new();
    readonly RealmHostOptions options;
    readonly TransferTicketSigner signer;
    readonly bool ownsSigner;

    TimeSpan emptyFor;
    bool disposed;

    /// <summary>What this shard was told to be.</summary>
    public RealmSpec Spec { get; }

    /// <summary>Where it is in its life.</summary>
    public ShardState State { get; private set; } = ShardState.Starting;

    /// <summary>The session players connect to.</summary>
    public NetworkSession Session { get; }

    /// <summary>The door.</summary>
    public PlayerAdmission Admission { get; }

    /// <summary>The map.</summary>
    public MapLifetime Map { get; }

    /// <summary>The one place a control plane is called.</summary>
    public RealmDirectory Directory { get; } = new();

    /// <summary>The health sampler.</summary>
    public RealmHeartbeat Heartbeat { get; }

    /// <summary>Every transfer this realm is part of, leaving and arriving.</summary>
    /// <remarks>
    ///     ⚠ <b>Stepped by <see cref="Update" /> and by nothing else.</b> A transfer expiring is a
    ///     decision about a player the frame is simulating, so it happens where the frame can see it
    ///     rather than on a timer — the same reason <c>RealmDirectory</c> drains where it does.
    /// </remarks>
    public RealmTransfers Transfers { get; }

    /// <summary>How many players are on this shard.</summary>
    public int Population => Admission.Count;

    /// <summary>Raised every time health is sampled.</summary>
    /// <remarks>
    ///     Where the L1 orchestrator's heartbeat call is attached, and where an L0 realm's log line
    ///     is. Nothing here decides what to do with a sample, because a realm that decided its own
    ///     shard was unhealthy would be the second opinion in a system whose whole design is that
    ///     exactly one place decides a given question.
    /// </remarks>
    public event Action<RealmHealth>? Sampled;

    /// <summary>Raised when the shard moves through <see cref="ShardState" />.</summary>
    public event Action<ShardState>? StateChanged;

    /// <summary>Raised when a ticketed player finishes joining.</summary>
    public event Action<RealmPlayer>? PlayerAdmitted;

    /// <summary>Raised when a player leaves, for any reason.</summary>
    public event Action<RealmPlayer>? PlayerReleased;

    /// <summary>A transfer ended, committed or aborted.</summary>
    /// <remarks>
    ///     ⚠ <b>A committed one means despawn them and an aborted one means do not</b>, and those are
    ///     the two things a realm cannot be allowed to get wrong. The event hands the decision to the
    ///     game once, at a defined point in the frame, rather than leaving it to be inferred.
    /// </remarks>
    public event Action<Vixen.Live.Transfer.SourceTransfer>? Finished;

    /// <summary>Decides whether a player can be moved right now. Doc 27 § Drain.</summary>
    /// <remarks>
    ///     ⚠ <b>The engine ships a default that says everybody, always, and does not pretend to know
    ///     better.</b> "In a scripted encounter" is a sentence only the game can finish, and a
    ///     built-in guess would be wrong in the direction that matters — a raid interrupted by a
    ///     rollout is the failure this hook exists to make impossible.
    /// </remarks>
    public Func<RealmPlayer, TransferReadiness> Readiness { get; set; } = _ => TransferReadiness.Ready;

    /// <summary>Stands a realm up.</summary>
    /// <param name="spec">What this shard is.</param>
    /// <param name="session">
    ///     Opens the session players connect to, over a transport bound to
    ///     <see cref="RealmSpec.Endpoint" />. Not started — <see cref="Start" /> does that.
    /// </param>
    /// <param name="signer">
    ///     The cluster key. Null makes one from the spec, which is a <b>development</b> convenience
    ///     and not a security mechanism — see <see cref="DevelopmentSigner" />.
    /// </param>
    /// <param name="options">What to differ in, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="spec" /> or <paramref name="session" /> is null, or the factory returned null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="spec" /> is not runnable.</exception>
    /// <remarks>
    ///     ⚠ <b>The session arrives as a factory rather than as a session, because it needs the door
    ///     to be built first.</b> <c>NetworkSession</c> takes its <c>ISessionAuthenticator</c> at
    ///     construction — doc 16's design, and the right one: an authenticator installed later would
    ///     mean a window in which a session is listening and admitting everybody. So the host builds
    ///     <see cref="PlayerAdmission" /> out of the spec, hands it over, and takes back the session
    ///     that was built around it.
    /// </remarks>
    public RealmHost(
        RealmSpec spec,
        Func<PlayerAdmission, NetworkSession> session,
        TransferTicketSigner? signer = null,
        RealmHostOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(session);

        if (!spec.IsValid) {
            throw new ArgumentException($"`{spec}` is not a runnable spec.", nameof(spec));
        }

        Spec = spec;
        this.options = options ?? new();

        ownsSigner = signer is null;
        this.signer = signer ?? DevelopmentSigner(spec);

        Admission = new(spec, this.signer) { Now = this.options.Now };
        Map = new(spec.Key);
        Heartbeat = new(this.options.HeartbeatInterval);
        Transfers = new(this.options.Transfers);

        Session = session(Admission)
            ?? throw new ArgumentNullException(nameof(session), "The session factory returned null.");

        Session.PlayerJoined += OnPlayerJoined;
        Session.PlayerLeft += OnPlayerLeft;
    }

    /// <summary>Starts listening. The shard is <see cref="ShardState.Starting" /> until its map is up.</summary>
    public void Start() {
        ObjectDisposedException.ThrowIf(disposed, this);

        Session.StartServer();
    }

    /// <summary>Hands the realm a line from its launcher.</summary>
    /// <param name="line">A line of standard input.</param>
    /// <remarks>
    ///     ⚠ <b>Thread-safe, and the only member that is.</b> Standard input is read on a thread of
    ///     its own — it has to be, because reading it blocks — so this queues and
    ///     <see cref="Update" /> acts. Everything else on this class belongs to the realm's thread.
    /// </remarks>
    public void Signal(string line) {
        if (RealmSignals.ReadCommand(line) is { Length: > 0 } command) {
            signals.Enqueue(command);
        }
    }

    /// <summary>One realm update, before the game's own.</summary>
    /// <param name="elapsed">How long the last frame took.</param>
    /// <param name="scenes">The scene manager, or null for a head with no world.</param>
    /// <param name="messages">
    ///     Where the session's user payloads go, or null to drop them.
    /// </param>
    /// <returns>How many messages the session handled.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the realm's only <c>Session.Update</c>, so a game's payload handler has to
    ///     arrive here.</b> A realm that called <c>Session.Update</c> again from its own step would
    ///     advance the session twice a frame — two ticks of clock drift correction, two rounds of
    ///     timeouts — which is a class of bug that presents as a session that ages twice as fast as
    ///     the world in it.
    /// </remarks>
    public int Update(
        TimeSpan elapsed,
        SceneManager? scenes = null,
        ISessionMessageHandler? messages = null
    ) {
        ObjectDisposedException.ThrowIf(disposed, this);

        // 1. Answers first, so the frame sees one consistent view of what the control plane decided.
        Directory.Drain();

        // 2. Then the launcher, so a drain that arrived stops admission before anybody else is let in.
        while (signals.TryDequeue(out var command)) {
            Apply(command);
        }

        // 3. Then the map, which is what turns Starting into Ready.
        if (State == ShardState.Starting && Map.Resolve(scenes)) {
            Advance(ShardState.Ready);
            options.Output(RealmSignals.FormatReady(Spec.Endpoint));
        }

        var handled = Session.Update(elapsed, messages);

        // 4. Then readiness, which the game answers and a drain consumes.
        var blocked = 0;

        foreach (var player in Admission.Players) {
            player.Readiness = Readiness(player);

            if (player.Readiness == TransferReadiness.Blocked) {
                blocked++;
            }
        }

        // 5. Then transfers, before health so that the sample and the in-flight count agree about
        //    the same instant — a heartbeat taken either side of a commit would report a population
        //    that no realm ever had.
        foreach (var transfer in Transfers.Step(options.Now())) {
            Finished?.Invoke(transfer);
        }

        // ⚠ An arrival that aged out may still be holding a session here. It was admitted by its
        // ticket at t3 and its transfer then died, so its source is still simulating it and this
        // realm must not — and an admission with no arrival behind it is a slot against the cap held
        // for somebody who is playing elsewhere. Not a disconnect: the client is told nothing,
        // because there is nothing for it to do that it is not already doing.
        foreach (var player in Transfers.Lapsed) {
            if (Admission.TryGet(player, out var arrival) && arrival is { } stranded && stranded.Id.Value != 0) {
                Admission.Release(stranded.Id);
            }
        }

        // 6. Then health, over a tick that has actually happened.
        Heartbeat.Observe(elapsed);

        if (Heartbeat.IsDue(elapsed)) {
            Sampled?.Invoke(
                new(
                    Spec.Shard,
                    State,
                    Population,
                    Heartbeat.TickP99(),
                    Heartbeat.TickMean(),
                    blocked,
                    options.Now()
                )
            );
        }

        // 7. And finally the only thing that ends a realm by itself: a drained shard that emptied
        //    and stayed empty. The grace is what stops a reconnect in flight from arriving at a
        //    process that has already gone.
        if (State == ShardState.Draining) {
            emptyFor = Population == 0 ? emptyFor + elapsed : TimeSpan.Zero;

            if (emptyFor >= options.IdleGrace) {
                Stop();
            }
        }

        return handled;
    }

    /// <summary>Stops taking arrivals and starts moving people out.</summary>
    /// <remarks>
    ///     Idempotent, and one-way. Doc 27 § Drain: nothing is force-disconnected by this — the
    ///     players already here leave at moments <see cref="Readiness" /> approved of, and the shard
    ///     stops when the last one has gone.
    /// </remarks>
    public void Drain() {
        if (State is not ShardState.Ready and not ShardState.Starting) {
            return;
        }

        Admission.IsDraining = true;
        Map.Quiesce();
        emptyFor = TimeSpan.Zero;
        Advance(ShardState.Draining);
        options.Output(RealmSignals.Draining);
    }

    /// <summary>Ends the shard.</summary>
    public void Stop() {
        if (State is ShardState.Stopping or ShardState.Stopped) {
            return;
        }

        Advance(ShardState.Stopping);
        Session.Stop();
        Advance(ShardState.Stopped);
        options.Output(RealmSignals.Stopped);
    }

    /// <summary>Releases what the realm owns.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Session.PlayerJoined -= OnPlayerJoined;
        Session.PlayerLeft -= OnPlayerLeft;
        Directory.Dispose();

        if (ownsSigner) {
            signer.Dispose();
        }
    }

    /// <summary>
    ///     A cluster key derived from the shard's own spec, for a realm nobody handed one to.
    /// </summary>
    /// <param name="spec">The shard.</param>
    /// <returns>A signer only this shard's own tickets validate against.</returns>
    /// <remarks>
    ///     ⚠ <b>This is not a security mechanism and is not meant to look like one.</b> It is derived
    ///     from values that travel in plain text on a command line, so anybody who can see the
    ///     process can mint tickets for it. What it buys is that a development realm and its test
    ///     harness agree about admission with no key management at all, and that a deployment which
    ///     forgot to configure a key gets a shard nobody else's tickets work on — a fleet that
    ///     refuses everybody, which is loud — rather than a shard that admits anybody, which is not.
    /// </remarks>
    public static TransferTicketSigner DevelopmentSigner(RealmSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);

        Span<byte> key = stackalloc byte[TransferTicketSigner.MinimumKeyBytes];

        spec.Shard.Value.TryWriteBytes(key);
        BitConverter.TryWriteBytes(key[16..], spec.Seed);
        BitConverter.TryWriteBytes(key[24..], spec.Key.Version.Content);

        return new(key);
    }

    void Apply(string command) {
        if (command == RealmSignals.Drain) {
            Drain();

            return;
        }

        if (command == RealmSignals.Stop) {
            Stop();
        }
    }

    void Advance(ShardState state) {
        if (State == state) {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    void OnPlayerJoined(NetworkPlayer player) {
        if (Admission.Bind(player) is { } admitted) {
            PlayerAdmitted?.Invoke(admitted);
        }
    }

    void OnPlayerLeft(NetworkPlayer player, PlayerLeaveReason reason) {
        if (Admission.Release(player.Id) is { } released) {
            PlayerReleased?.Invoke(released);
        }
    }
}
