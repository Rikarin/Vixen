// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.App;
using Vixen.Core;
using Vixen.Net.Sessions;
using Vixen.Net.Time;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Udp;

namespace Vixen.Live.Realms;

/// <summary>What a game's realm derives from. A <c>Game</c> that is a shard.</summary>
/// <remarks>
///     <para>
///         A realm is doc 17's Model B application built as a dedicated server, so it is a
///         <c>Game</c> and everything a game can do it can do. What this base class adds is the six
///         boot decisions that make it a shard, and it takes over exactly four of <c>Game</c>'s hooks
///         to do it — <c>OnConfigure</c>, <c>OnInitialise</c>, <c>OnUpdate</c> and <c>OnShutdown</c>
///         are sealed here, and the realm-shaped versions of all four are offered in their place.
///     </para>
///     <para>
///         ⚠ <b>The map is <c>AppConfig.StartupScene</c>, not a loader of its own.</b>
///         <see cref="OnConfigure" /> points the host at <c>RealmSpec.Key.Map</c> and lets the host
///         do what it already does — including surviving a map that will not open, which leaves a
///         shard that never reports ready rather than one that admits players into an empty world.
///     </para>
///     <para>
///         ⚠ <b>The transport binds to every interface; the endpoint is what clients are told.</b>
///         A spec's host is a node's external address or a relay's name, and is frequently not an
///         address this process can bind. Overriding <see cref="CreateTransport" /> is the seam for
///         a realm that needs something else — a relay allocation, a composite that accepts both
///         (M-Q1) — and it is a placement decision rather than an architecture change precisely
///         because nothing above the transport knows the difference.
///     </para>
/// </remarks>
public abstract class Realm : Game {
    RealmHost? host;
    RealmSpec? spec;

    /// <summary>What this shard was told to be.</summary>
    /// <exception cref="InvalidOperationException">Read before <c>RealmApp</c> bound one.</exception>
    public RealmSpec Spec =>
        spec ?? throw new InvalidOperationException(
            "A realm has no spec until it is started. Run it through RealmApp.Run<TRealm>(args), or "
            + "call Bind(spec) before building the application."
        );

    /// <summary>The shard's own machinery: admission, map, heartbeat, directory.</summary>
    /// <exception cref="InvalidOperationException">Read before <c>OnRealmInitialise</c>.</exception>
    public RealmHost Host =>
        host ?? throw new InvalidOperationException(
            "The realm host does not exist until OnInitialise. OnConfigure runs before the platform, "
            + "which is the point of it — it decides what the platform will be."
        );

    /// <summary>Hands this realm its spec, before the application is built.</summary>
    /// <param name="realmSpec">What this shard is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="realmSpec" /> is null.</exception>
    /// <exception cref="InvalidOperationException">It already has one.</exception>
    /// <remarks>
    ///     Public because doc 17's model is that nothing in the boot path is a black box: a realm
    ///     assembled by hand — an editor running one in-process, a test — does this and then builds
    ///     an <c>AppBuilder</c> itself, which is the same sequence <c>RealmApp.Run</c> runs.
    /// </remarks>
    public void Bind(RealmSpec realmSpec) {
        ArgumentNullException.ThrowIfNull(realmSpec);

        if (spec is not null) {
            throw new InvalidOperationException("This realm already has a spec.");
        }

        spec = realmSpec;
    }

    /// <summary>Runs once, after the shard exists and before the first frame.</summary>
    protected virtual void OnRealmInitialise() { }

    /// <summary>Runs once per frame, after the shard's own update.</summary>
    /// <param name="time">How long the last frame took.</param>
    protected virtual void OnRealmUpdate(GameTime time) { }

    /// <summary>Runs once, before the shard is torn down.</summary>
    protected virtual void OnRealmShutdown() { }

    /// <summary>Where the session's user payloads go, or null to drop them.</summary>
    /// <remarks>
    ///     ⚠ <b>A realm's session is updated once, by the host, and this is how a handler reaches
    ///     it.</b> Calling <c>Host.Session.Update</c> from <see cref="OnRealmUpdate" /> to install
    ///     one would advance the session twice a frame — two rounds of drift correction, two rounds
    ///     of timeouts — which presents as a session that ages twice as fast as the world in it.
    /// </remarks>
    protected virtual ISessionMessageHandler? Messages => null;

    /// <summary>Runs when a ticketed player finishes joining.</summary>
    /// <param name="player">Them.</param>
    protected virtual void OnPlayerAdmitted(RealmPlayer player) { }

    /// <summary>Runs when a player leaves, for any reason.</summary>
    /// <param name="player">Them.</param>
    protected virtual void OnPlayerReleased(RealmPlayer player) { }

    /// <summary>Decides whether a player can be moved to another shard right now.</summary>
    /// <param name="player">Them.</param>
    /// <returns>Ready, soon, or blocked.</returns>
    /// <remarks>
    ///     Doc 27 § Drain. The default says everybody, always, which is right for a game that has
    ///     nothing uninterruptible in it and wrong for every game that does — a boss fight, a story
    ///     step, a match. This is the hook that stops a rollout from ending a raid.
    /// </remarks>
    protected virtual TransferReadiness ReadinessOf(RealmPlayer player) => TransferReadiness.Ready;

    /// <summary>Opens the transport clients connect over.</summary>
    /// <param name="endpoint">Where placement said clients would be sent.</param>
    /// <returns>The transport. The session takes ownership.</returns>
    protected virtual ITransport CreateTransport(RealmEndpoint endpoint) =>
        new UdpTransport(
            new UdpDatagramSocketFactory(),
            new UdpTransportOptions {
                // Every interface, on the port placement published. The spec's host is what a client
                // is told, and on a Kubernetes node or behind a relay it is not an address this
                // process could bind even if it wanted to.
                ListenEndPoint = new(IPAddress.Any, endpoint.Port),
                MaxConnections = Spec.Capacity.HardCap
            }
        );

    /// <summary>The session's settings.</summary>
    /// <returns>What the handshake will enforce.</returns>
    /// <remarks>
    ///     ⚠ <b><c>ContentHash</c> is filled in from the spec and should stay that way.</b> It is the
    ///     catalog's <c>BuildHash</c>, which is the same number placement filtered on (ADR-022) and
    ///     the same number doc 16's handshake already compares. A realm that computed a different one
    ///     here would refuse exactly the clients placement had just decided were compatible.
    /// </remarks>
    protected virtual SessionOptions CreateSessionOptions() =>
        new() {
            MaxPlayers = Spec.Capacity.HardCap,
            TickRate = new TickRate(Spec.TickRate),
            ContentHash = Spec.Key.Version.Content
        };

    /// <summary>The cluster key, or null for <see cref="RealmHost.DevelopmentSigner" />.</summary>
    /// <returns>The signer, which the host disposes if it made it.</returns>
    /// <remarks>
    ///     A real deployment reads this from whatever it already uses for secrets. It is deliberately
    ///     not a field on <see cref="RealmSpec" />: a spec travels on a command line, and a command
    ///     line is visible to every other process on the machine.
    /// </remarks>
    protected virtual TransferTicketSigner? CreateSigner() => null;

    /// <summary>What the host is allowed to differ in.</summary>
    /// <returns>The options.</returns>
    protected virtual RealmHostOptions CreateHostOptions() => new();

    /// <inheritdoc />
    protected sealed override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        config.Name = Spec.Key.SceneName;
        config.Variant = BuildVariant.Server;
        config.Headless = true;
        config.Window = null;
        config.Graphics.Enabled = false;

        // The map, opened by the host before OnInitialise, so the world exists by the time the realm
        // looks for it (docs/plan/27 § The scene-management join).
        config.StartupScene = Spec.Key.Map;

        // A dedicated server paces itself by its tick rate and nothing else: there is no display to
        // be in step with, and a server that ran the frame loop flat out would burn a core to
        // simulate the same thirty steps.
        config.FrameRateLimit = Spec.TickRate;

        OnRealmConfigure(config);
    }

    /// <summary>Adjusts the host's configuration, after the realm's own decisions.</summary>
    /// <param name="config">The defaults, with the shard's boot decisions already applied.</param>
    /// <remarks>
    ///     Everything above this call is a decision doc 27 makes and a realm should not undo —
    ///     headless, server variant, the map, the tick. Everything else is fair game.
    /// </remarks>
    protected virtual void OnRealmConfigure(AppConfig config) { }

    /// <inheritdoc />
    protected sealed override void OnInitialise() {
        host = new(
            Spec,
            admission => new(CreateTransport(Spec.Endpoint), CreateSessionOptions(), admission, ownsTransport: true),
            CreateSigner(),
            CreateHostOptions()
        );

        host.Readiness = ReadinessOf;
        host.PlayerAdmitted += OnPlayerAdmitted;
        host.PlayerReleased += OnPlayerReleased;
        host.Start();

        OnRealmInitialise();
    }

    /// <inheritdoc />
    protected sealed override void OnUpdate(GameTime time) {
        Host.Update(time.UnscaledElapsed, Services.Scenes, Messages);

        OnRealmUpdate(time);
    }

    /// <inheritdoc />
    protected sealed override void OnShutdown() {
        OnRealmShutdown();

        if (host is null) {
            return;
        }

        host.Stop();
        host.Map.Unload(Services.Scenes);
        host.Dispose();
    }
}
