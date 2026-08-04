// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;

namespace Vixen.Live.Realms;

/// <summary>The grains a realm talks to, behind one seam.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a convenience — it is what makes <see cref="RealmCluster" /> testable.</b> An
///         <c>IClusterClient</c> cannot be stood up without a cluster, so a test of "does a shard
///         report ready, and does it drain when told to" would need a silo. Behind this interface it
///         needs four fakes that return <c>Task</c>s.
///     </para>
///     <para>
///         It is deliberately three lookups and nothing else. Anything richer would be a second
///         orchestrator API on the realm side, and the whole point of ADR-016 is that a realm asks
///         narrow questions rarely.
///     </para>
/// </remarks>
public interface IRealmGrains {
    /// <summary>The grain for a shard.</summary>
    /// <param name="shard">Which shard.</param>
    /// <returns>Its grain.</returns>
    IShardGrain Shard(ShardId shard);

    /// <summary>The grain for a map.</summary>
    /// <param name="key">Which map, region and version.</param>
    /// <returns>Its grain.</returns>
    IMapGrain Map(ShardKey key);

    /// <summary>The grain for a character.</summary>
    /// <param name="player">Which character.</param>
    /// <returns>Its grain.</returns>
    IPlayerGrain Player(PlayerKey player);
}

/// <summary>The real one, over an Orleans cluster client.</summary>
/// <param name="cluster">The connected client. Owned by whoever built it.</param>
public sealed class ClusterGrains(IClusterClient cluster) : IRealmGrains {
    /// <inheritdoc />
    public IShardGrain Shard(ShardId shard) => cluster.GetGrain<IShardGrain>(shard.Value);

    /// <inheritdoc />
    public IMapGrain Map(ShardKey key) => cluster.GetGrain<IMapGrain>(Keys.ForMap(key));

    /// <inheritdoc />
    public IPlayerGrain Player(PlayerKey player) => cluster.GetGrain<IPlayerGrain>(Keys.ForPlayer(player));
}

/// <summary>How often a realm renews what it holds.</summary>
/// <param name="LeaseRenewal">
///     How often a held lease is renewed. Comfortably inside <c>LeaseOptions.Lifetime</c>, because a
///     realm that renewed at the last moment would lose its right to write to one slow round trip.
/// </param>
public sealed record RealmClusterOptions(TimeSpan LeaseRenewal) {
    /// <summary>The defaults.</summary>
    public static RealmClusterOptions Default { get; } = new(TimeSpan.FromSeconds(5));
}

/// <summary>The realm's half of the control plane. ADR-016 and ADR-018, wired.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every grain call in this file goes through <c>RealmDirectory</c>, and that is the
///         whole point of the class.</b> Doc 27 M1 names a grain call reaching the frame path as the
///         single way this design fails — "it will not look like a bug, it will look like occasional
///         stutter". So nothing here awaits: it posts, the realm keeps simulating, and the answer is
///         applied on the realm's own thread at a defined point in a later frame.
///     </para>
///     <para>
///         ⚠ <b>The realm learns what to do from the replies it was already collecting.</b> Draining
///         arrives in the answer to a heartbeat; a lost lease arrives in the answer to a renewal.
///         Nothing in the cluster calls into a realm, which means a realm needs no inbound port, no
///         inbound authentication and no firewall rule beyond the one its players use.
///     </para>
///     <para>
///         ⚠ <b>A realm with no cluster is a realm, not a broken one.</b> Doc 27 § Cost's L0 is a
///         dedicated server with a lifecycle and no orchestrator; this class is what a deployment
///         adds when it grows one, and <c>RealmSpec.ClusterEndpoint</c> being empty is the ordinary
///         case rather than a misconfiguration.
///     </para>
/// </remarks>
public sealed class RealmCluster : IDisposable {
    readonly Dictionary<PlayerKey, long> leases = [];
    readonly RealmClusterOptions options;
    readonly IRealmGrains grains;
    readonly RealmHost host;

    TimeSpan sinceRenewal;
    bool disposed;

    /// <summary>How many heartbeats have been posted.</summary>
    public long HeartbeatCount { get; private set; }

    /// <summary>How many leases this realm believes it holds.</summary>
    public int LeaseCount => leases.Count;

    /// <summary>How many leases were taken away by somebody else.</summary>
    /// <remarks>
    ///     Worth counting rather than only logging: a realm losing leases it did not give up is
    ///     either a transfer storm or a cluster that thinks this shard is dead, and both look like
    ///     "players cannot pick anything up" from the inside.
    /// </remarks>
    public long LeasesLost { get; private set; }

    /// <summary>Wires a realm to a cluster.</summary>
    /// <param name="host">The shard.</param>
    /// <param name="grains">How to reach the grains.</param>
    /// <param name="options">How often to renew, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host" /> or <paramref name="grains" /> is null.</exception>
    public RealmCluster(RealmHost host, IRealmGrains grains, RealmClusterOptions? options = null) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(grains);

        this.host = host;
        this.grains = grains;
        this.options = options ?? RealmClusterOptions.Default;

        host.StateChanged += OnStateChanged;
        host.Sampled += OnSampled;
        host.PlayerAdmitted += OnPlayerAdmitted;
        host.PlayerReleased += OnPlayerReleased;
    }

    /// <summary>Renews what is held. Called once per realm update, after the host's own.</summary>
    /// <param name="elapsed">How long the last frame took.</param>
    public void Update(TimeSpan elapsed) {
        if (disposed) {
            return;
        }

        sinceRenewal += elapsed;

        if (sinceRenewal < options.LeaseRenewal) {
            return;
        }

        sinceRenewal = TimeSpan.Zero;

        foreach (var held in leases.ToList()) {
            Renew(held.Key, held.Value);
        }
    }

    /// <summary>Stops listening. Does not disconnect the cluster client, which it does not own.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        host.StateChanged -= OnStateChanged;
        host.Sampled -= OnSampled;
        host.PlayerAdmitted -= OnPlayerAdmitted;
        host.PlayerReleased -= OnPlayerReleased;
    }

    void OnStateChanged(ShardState state) {
        var shard = grains.Shard(host.Spec.Shard);

        switch (state) {
            case ShardState.Ready:
                host.Directory.Ask(_ => Done(shard.Ready(host.Spec.Endpoint)), _ => { });

                break;

            case ShardState.Stopped:
                host.Directory.Ask(_ => Done(shard.Stopped()), _ => { });

                break;

            default:
                // Draining is not reported: the cluster is where the decision came from, and a realm
                // that echoed it back would be telling the orchestrator something it already knows.
                break;
        }
    }

    void OnSampled(RealmHealth health) {
        HeartbeatCount++;

        var sample = new ShardHeartbeat(
            health.Population,
            health.TickP99Milliseconds,
            health.TickMeanMilliseconds,
            health.Blocked,
            health.SampledAt
        );

        host.Directory.Ask(
            _ => grains.Shard(host.Spec.Shard).Heartbeat(sample),
            state => {
                // ⚠ This is how a realm learns it should be draining, and it costs nothing: the
                // heartbeat was going to be sent anyway, and the answer arrives on the realm's own
                // thread inside RealmDirectory.Drain.
                if (state == ShardState.Draining) {
                    host.Drain();
                }
            }
        );
    }

    void OnPlayerAdmitted(RealmPlayer player) {
        host.Directory.Ask(
            _ => grains.Player(player.Key).AcquireLease(host.Spec.Shard),
            lease => {
                if (lease.Granted) {
                    leases[player.Key] = lease.Epoch;
                }
            }
        );
    }

    void OnPlayerReleased(RealmPlayer player) {
        if (leases.Remove(player.Key, out var epoch)) {
            host.Directory.Ask(
                _ => Done(grains.Player(player.Key).ReleaseLease(host.Spec.Shard, epoch)),
                _ => { }
            );
        }

        // The map's roster, so the next placement's affinity counts are honest. Told even when no
        // lease was held: a player refused at the door was still counted as an arrival.
        host.Directory.Ask(
            _ => Done(grains.Map(host.Spec.Key).PlayerLeft(player.Key, host.Spec.Shard)),
            _ => { }
        );
    }

    void Renew(PlayerKey player, long epoch) =>
        host.Directory.Ask(
            _ => grains.Player(player).RenewLease(host.Spec.Shard, epoch),
            lease => {
                if (lease.Granted) {
                    leases[player] = lease.Epoch;

                    return;
                }

                // Superseded. Doc 27 ADR-021: the realm keeps simulating — a lease loss mid-combat
                // must be survivable — and stops writing durable state until it comes back or the
                // transfer hands the buffered mutations to the new holder.
                leases.Remove(player);
                LeasesLost++;
            }
        );

    /// <summary>Turns a <c>Task</c> into something <c>RealmDirectory.Ask</c> can carry.</summary>
    /// <remarks>
    ///     <c>Ask</c> is deliberately typed on an answer, because the point of it is applying one on
    ///     the realm's thread. A call with no answer still wants the same posting discipline, and
    ///     giving it a second overload would be one more way to reach a grain from a frame.
    /// </remarks>
    static async Task<bool> Done(Task call) {
        await call.ConfigureAwait(false);

        return true;
    }
}
