// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;

namespace Vixen.Live.Gate.Tests;

/// <summary>A control plane that is two fields.</summary>
/// <remarks>
///     ⚠ <b>The gate's decisions are the thing under test, and none of them is Orleans's.</b> Version
///     filtering, the character-belongs-to-this-account check, the difference between <c>Starting</c>
///     and <c>Refused</c>, and what a minted ticket says are all decided before the cluster is asked
///     or after it has answered. <c>IFleetDirectory</c> is the seam that lets those be asserted in a
///     hundred microseconds instead of against a silo.
/// </remarks>
sealed class FakeFleet : IFleetDirectory {
    /// <summary>What the map will answer with.</summary>
    public PlaceResult Answer { get; set; } =
        new(PlaceStatus.Placed, ShardId.New(), new("realm.example", 30000), "the only shard, and it has room");

    /// <summary>What the player's lease is at, so the ticket names one past it.</summary>
    public long Epoch { get; set; } = 4;

    /// <summary>Every request it was asked, in order.</summary>
    public List<PlaceRequest> Asked { get; } = [];

    /// <inheritdoc />
    public Task<PlaceResult> PlaceAsync(PlaceRequest request, CancellationToken cancellation) {
        Asked.Add(request);

        return Task.FromResult(Answer);
    }

    /// <inheritdoc />
    public Task<long> NextLeaseEpochAsync(PlayerKey player, CancellationToken cancellation) =>
        Task.FromResult(Epoch + 1);
}
