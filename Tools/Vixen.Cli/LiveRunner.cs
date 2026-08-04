// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Live;
using Vixen.Live.Cluster;

namespace Vixen.Cli;

/// <summary>What each `vixen live` verb prints. Doc 27 § Diagnostics, in a terminal.</summary>
/// <remarks>
///     ⚠ <b>Every method takes its writer rather than reaching for <see cref="Console" />,</b> which is
///     the rule the rest of this CLI already follows: a command whose behaviour can only be checked by
///     running a process is one whose behaviour is not checked.
/// </remarks>
public static class LiveRunner {
    /// <summary>Prints a map's fleet — doc 27 § Diagnostics' fleet view, as a table.</summary>
    /// <param name="operations">The cluster.</param>
    /// <param name="key">Which map.</param>
    /// <param name="writer">Where it goes.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static async Task<int> StatusAsync(
        ILiveOperations operations,
        ShardKey key,
        TextWriter writer,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(writer);

        var shards = await operations.ShardsAsync(key, cancellation).ConfigureAwait(false);

        if (shards.Count == 0) {
            // ⚠ Not an error. A map with no shards is what every map looks like before anybody plays
            // it, and an operator who typed the name wrong wants to be told the name they typed.
            await writer.WriteLineAsync($"No shards for {key}.").ConfigureAwait(false);

            return (int)ExitCode.Success;
        }

        await writer.WriteLineAsync(
                $"{"SHARD",-38} {"STATE",-10} {"POP",5} {"CAP",5}  ENDPOINT"
            )
            .ConfigureAwait(false);

        foreach (var shard in shards.OrderBy(shard => shard.State).ThenBy(shard => shard.Shard.Value)) {
            await writer.WriteLineAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{shard.Shard,-38} {shard.State,-10} {shard.Population,5} {shard.Capacity.HardCap,5}  {shard.Endpoint}"
                    )
                )
                .ConfigureAwait(false);
        }

        return (int)ExitCode.Success;
    }

    /// <summary>Tells a shard to move its players out.</summary>
    /// <param name="operations">The cluster.</param>
    /// <param name="shard">Which shard.</param>
    /// <param name="reason">Why.</param>
    /// <param name="writer">Where the confirmation goes.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Says what a drain is, because the word suggests something more violent than it is.</b>
    ///     Doc 27 § Drain: nothing is force-disconnected, players leave at moments the game approves
    ///     of, and a shard with a raid in it can hold out for the hard deadline. An operator who
    ///     expected the shard to stop within seconds needs to know that before they type it, not
    ///     after.
    /// </remarks>
    public static async Task<int> DrainAsync(
        ILiveOperations operations,
        ShardId shard,
        string reason,
        TextWriter writer,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(writer);

        if (!shard.IsValid) {
            await writer.WriteLineAsync("That is not a shard id.").ConfigureAwait(false);

            return (int)ExitCode.UsageError;
        }

        await operations.DrainAsync(shard, reason, cancellation).ConfigureAwait(false);

        await writer.WriteLineAsync(
                $"{shard} is draining: {reason}. Nobody is disconnected — players leave at safe moments, "
                + "and the shard stops when the last one has gone."
            )
            .ConfigureAwait(false);

        return (int)ExitCode.Success;
    }

    /// <summary>Why a player went where they went.</summary>
    /// <param name="operations">The cluster.</param>
    /// <param name="key">Which map to ask.</param>
    /// <param name="player">Who.</param>
    /// <param name="writer">Where it goes.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static async Task<int> ExplainAsync(
        ILiveOperations operations,
        ShardKey key,
        PlayerKey player,
        TextWriter writer,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(writer);

        if (!player.IsValid) {
            await writer.WriteLineAsync("That is not an account/character pair.").ConfigureAwait(false);

            return (int)ExitCode.UsageError;
        }

        await writer.WriteLineAsync(await operations.ExplainAsync(key, player, cancellation).ConfigureAwait(false))
            .ConfigureAwait(false);

        return (int)ExitCode.Success;
    }

    /// <summary>Reads or moves a region's rollout target.</summary>
    /// <param name="operations">The cluster.</param>
    /// <param name="region">Which region.</param>
    /// <param name="target">What to aim at, or <c>default</c> to only read.</param>
    /// <param name="writer">Where it goes.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Rolling back is this with the old pair, and the command says so</b> — doc 27
    ///     § Upgrades: nothing about the mechanism is directional. At three in the morning that
    ///     sentence is the whole procedure, and it should not have to be remembered.
    /// </remarks>
    public static async Task<int> UpgradeAsync(
        ILiveOperations operations,
        string region,
        RealmVersion target,
        TextWriter writer,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(writer);

        if (target.IsValid) {
            await operations.SetTargetAsync(region, target, cancellation).ConfigureAwait(false);

            await writer.WriteLineAsync(
                    $"{region} is now rolling to {target}. Old-version shards drain as their players "
                    + $"reach safe moments; roll back with the same command and the old pair."
                )
                .ConfigureAwait(false);

            return (int)ExitCode.Success;
        }

        var (current, spread) = await operations.RolloutAsync(region, cancellation).ConfigureAwait(false);

        await writer.WriteLineAsync(
                current.IsValid
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{region} is aiming at {current}; {spread:P1} of its shards are not there yet."
                    )
                    : $"{region} has no target set, so every shard stays on whatever it started with."
            )
            .ConfigureAwait(false);

        return (int)ExitCode.Success;
    }
}
