// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>What one account owns, as a state machine a test can drive.</summary>
/// <remarks>
///     <para>
///         The grains-over-state-machines pattern a fourth time, for the reason
///         <see cref="PlayerLeaseState" /> gives: a machine a test constructs and drives, and a grain
///         that supplies the one property it cannot give itself, which is never being re-entered.
///     </para>
///     <para>
///         ⚠ <b>There is no lock in this file and there must never be one.</b> What makes it correct
///         is that <see cref="AccountGrain" /> takes one turn at a time. A lock would make the reason
///         "we remembered to" rather than "the runtime guarantees it".
///     </para>
///     <para>
///         ⚠ <b>Unlocking is idempotent on the address, and there is no idempotency key.</b> Two
///         realms racing to grant the same mount to two characters of one account is ordinary rather
///         than exceptional — that is what "account-wide" means — so the second must be a no-op. A
///         key would make it a no-op only for a <em>retry</em> of the same grant, and two genuinely
///         different grants of one mount would still write two rows.
///     </para>
///     <para>
///         ⚠ <b>An order is assigned here rather than trusted from the caller.</b> Two realms cannot
///         agree on a counter without asking, and asking is this call — so whatever a caller put in
///         the field is ignored. It is a counter and not a clock for the reason doc 28 gives about
///         collections: nothing in that library has one, and a counter is what replays identically.
///     </para>
/// </remarks>
public sealed class AccountState {
    readonly Dictionary<string, AccountUnlock> unlocks = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> earned = new(StringComparer.Ordinal);

    int order;

    /// <summary>How many things it owns.</summary>
    public int Count => unlocks.Count;

    /// <summary>How many achievements it has earned.</summary>
    public int Earned => earned.Count;

    /// <summary>What those achievements are worth.</summary>
    public int Points { get; private set; }

    /// <summary>How many times this has changed.</summary>
    public uint Revision { get; private set; }

    /// <summary>Everything, as one answer.</summary>
    /// <returns>The holdings.</returns>
    public AccountHoldings Holdings() =>
        new(
            [.. unlocks.Values.OrderBy(unlock => unlock.Order)],
            [.. earned.OrderBy(pair => pair.Value).Select(pair => pair.Key)],
            Points,
            Revision
        );

    /// <summary>Gives the account something.</summary>
    /// <param name="unlock">What, and where it came from.</param>
    /// <returns>Whether it was new.</returns>
    public bool Unlock(AccountUnlock unlock) {
        ArgumentNullException.ThrowIfNull(unlock);

        if (string.IsNullOrEmpty(unlock.Address) || unlocks.ContainsKey(unlock.Address)) {
            return false;
        }

        unlocks.Add(unlock.Address, unlock with { Order = ++order });
        Revision++;

        return true;
    }

    /// <summary>Records an achievement.</summary>
    /// <param name="address">Which.</param>
    /// <param name="points">What it is worth. Below zero counts as nothing.</param>
    /// <returns>Whether it was new.</returns>
    public bool Earn(string address, int points) {
        if (string.IsNullOrEmpty(address) || !earned.TryAdd(address, ++order)) {
            return false;
        }

        Points += Math.Max(0, points);
        Revision++;

        return true;
    }

    /// <summary>Takes something back.</summary>
    /// <param name="address">What.</param>
    /// <returns>Whether they had it.</returns>
    public bool Revoke(string address) {
        if (string.IsNullOrEmpty(address) || !unlocks.Remove(address)) {
            return false;
        }

        Revision++;

        return true;
    }

    /// <summary>Puts a saved account back, as it was, with no checks.</summary>
    /// <param name="saved">What it had.</param>
    /// <remarks>
    ///     ⚠ <b>Not a replay, for doc 28's reason.</b> Re-running the grants would re-derive them
    ///     against today's content, so a patch that removed a promotion would take back what somebody
    ///     was given. The order is kept as saved and the counter resumes past it, so nothing that
    ///     already exists is renumbered.
    /// </remarks>
    public void Restore(AccountHoldings saved) {
        ArgumentNullException.ThrowIfNull(saved);

        unlocks.Clear();
        earned.Clear();
        Points = saved.Points;
        Revision = saved.Revision;
        order = 0;

        foreach (var unlock in saved.Unlocks) {
            if (!string.IsNullOrEmpty(unlock.Address)) {
                unlocks[unlock.Address] = unlock;
                order = Math.Max(order, unlock.Order);
            }
        }

        foreach (var achievement in saved.Achievements) {
            if (!string.IsNullOrEmpty(achievement)) {
                earned[achievement] = ++order;
            }
        }
    }
}

/// <summary>One account, keyed by the account's own guid.</summary>
/// <remarks>
///     ⚠ <b>Keyed by the account and not by the character</b>, which is the whole reason it exists
///     beside <see cref="IPlayerGrain" /> rather than being folded into it. <c>PlayerKey.Account</c>
///     is the guid to use.
/// </remarks>
public sealed class AccountGrain : Grain, IAccountGrain {
    readonly AccountState account = new();

    /// <inheritdoc />
    public Task<AccountHoldings> Holdings() => Task.FromResult(account.Holdings());

    /// <inheritdoc />
    public Task<bool> Unlock(AccountUnlock unlock) => Task.FromResult(account.Unlock(unlock));

    /// <inheritdoc />
    public Task<bool> Earn(string address, int points) => Task.FromResult(account.Earn(address, points));

    /// <inheritdoc />
    public Task<bool> Revoke(string address) => Task.FromResult(account.Revoke(address));
}
