// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Orleans;
using Vixen.Live.Cluster;

namespace Vixen.Live.Gate;

/// <summary>The two questions the gate asks the control plane, and the only two.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A seam over the cluster for the reason <c>IRealmGrains</c> and <c>IClusterApi</c>
///         are.</b> Behind it is <see cref="ClusterFleetDirectory" /> and a silo; in a test it is a
///         dictionary. That is what makes the gate's actual behaviour — version filtering, the
///         character-belongs-to-this-account check, minting the ticket, the difference between
///         <c>Starting</c> and <c>Refused</c> — assertable on every push instead of only against a
///         running Orleans cluster.
///     </para>
///     <para>
///         The gate is allowed to hold a cluster client (ADR-017 excludes the <em>client</em>, not
///         the gate) and the calls are all on the service plane, where a low-millisecond round trip
///         is exactly what the budget is. ADR-016's rule is about a <em>frame</em>, and the gate does
///         not have one.
///     </para>
/// </remarks>
public interface IFleetDirectory {
    /// <summary>Asks the map where this player should go.</summary>
    /// <param name="request">Who, and for what.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>A shard, a wait, or a refusal.</returns>
    Task<PlaceResult> PlaceAsync(PlaceRequest request, CancellationToken cancellation);

    /// <summary>The epoch a realm admitting this character will take.</summary>
    /// <param name="player">Which character.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>One past whatever is held now.</returns>
    /// <remarks>
    ///     ⚠ <b>The gate does not take the lease, it predicts it.</b> Acquiring is the receiving
    ///     realm's call, because a gate that acquired would have taken the lease off whoever holds it
    ///     for every player who merely opened the character screen. The number in the ticket is what
    ///     the realm will ask for, and the realm's own <c>AcquireLease</c> is what makes it true — so
    ///     a ticket that is never redeemed costs nothing and a stale one is superseded on arrival.
    /// </remarks>
    Task<long> NextLeaseEpochAsync(PlayerKey player, CancellationToken cancellation);
}

/// <summary>The real one, over an Orleans cluster client.</summary>
/// <param name="cluster">The client. Owned by whoever built it.</param>
public sealed class ClusterFleetDirectory(IClusterClient cluster) : IFleetDirectory {
    readonly IClusterClient cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));

    /// <inheritdoc />
    public Task<PlaceResult> PlaceAsync(PlaceRequest request, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(request);

        return cluster.GetGrain<IMapGrain>(Keys.ForMap(request.Key)).Place(request);
    }

    /// <inheritdoc />
    public async Task<long> NextLeaseEpochAsync(PlayerKey player, CancellationToken cancellation) {
        var lease = await cluster.GetGrain<IPlayerGrain>(Keys.ForPlayer(player)).Lease().ConfigureAwait(false);

        return lease.Epoch + 1;
    }
}
