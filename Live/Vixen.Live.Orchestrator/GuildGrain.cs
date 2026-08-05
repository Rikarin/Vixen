// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>One guild's roster, as a state machine a test can drive.</summary>
/// <remarks>
///     <para>
///         Grains over state machines, a fifth time. There is no lock here and there must never be
///         one: what makes it correct is that <see cref="GuildGrain" /> takes one turn at a time.
///     </para>
///     <para>
///         ⚠ <b>It checks rank, never permission.</b> Whether an officer <em>may</em> kick is a tag
///         on a compiled charter and the realm answers it with the code the client greys the button
///         with. What no local check can win is the race — two officers demoting each other at the
///         same moment — so the grain re-checks the part that is arithmetic: you may not act on
///         somebody at or above your own rank.
///     </para>
///     <para>
///         ⚠ <b>Rank 0 is the leader and there is always exactly one of them.</b> Every rule below
///         exists to keep that true, because a guild with no leader is one nobody can administer
///         again and a guild with two is a state no rule here could resolve afterwards.
///     </para>
/// </remarks>
public sealed class GuildState {
    readonly Dictionary<PlayerKey, GuildMember> members = [];
    readonly Dictionary<int, string> rankNames = [];

    /// <summary>Makes one that has not been founded.</summary>
    /// <param name="now">The clock. A parameter so a test does not have to wait.</param>
    public GuildState(Func<DateTimeOffset>? now = null) => Now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The clock.</summary>
    public Func<DateTimeOffset> Now { get; }

    /// <summary>Which charter, by address. Empty until founded.</summary>
    public string Charter { get; private set; } = "";

    /// <summary>What it is called.</summary>
    public string Name { get; private set; } = "";

    /// <summary>How many its charter allows.</summary>
    public int Capacity { get; private set; }

    /// <summary>When it was founded.</summary>
    public DateTimeOffset Founded { get; private set; }

    /// <summary>How many times it has changed.</summary>
    public uint Revision { get; private set; }

    /// <summary>How many are in it.</summary>
    public int Count => members.Count;

    /// <summary>Whether it has been founded.</summary>
    public bool Exists => Charter.Length > 0;

    /// <summary>What it looks like.</summary>
    /// <returns>The record.</returns>
    public GuildRecord Read() =>
        Exists
            ? new(
                Charter,
                Name,
                [.. members.Values.OrderBy(member => member.Joined).ThenBy(member => member.Player.Character)],
                rankNames.ToImmutableDictionary(),
                Founded,
                Revision
            )
            : GuildRecord.None;

    /// <summary>What rank somebody holds, or −1.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The rank.</returns>
    public int RankOf(PlayerKey player) => members.TryGetValue(player, out var member) ? member.Rank : -1;

    /// <summary>Founds it.</summary>
    /// <param name="founder">Who becomes rank 0.</param>
    /// <param name="charter">Which charter.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="capacity">How many it allows.</param>
    /// <returns>The outcome.</returns>
    public GuildOutcome Found(PlayerKey founder, string charter, string name, int capacity) {
        if (Exists) {
            return Outcome(GuildWrite.Founded);
        }

        if (!founder.IsValid || string.IsNullOrEmpty(charter)) {
            return Outcome(GuildWrite.NotFound);
        }

        Charter = charter;
        Name = name ?? "";
        Capacity = Math.Max(1, capacity);
        Founded = Now();
        members[founder] = new(founder, 0, Founded);

        return Changed();
    }

    /// <summary>Adds somebody.</summary>
    /// <param name="by">Who is inviting.</param>
    /// <param name="player">Who joins.</param>
    /// <param name="rank">At what rank.</param>
    /// <returns>The outcome.</returns>
    public GuildOutcome Add(PlayerKey by, PlayerKey player, int rank) {
        if (!Exists) {
            return Outcome(GuildWrite.NotFound);
        }

        var actor = RankOf(by);

        if (actor < 0) {
            return Outcome(GuildWrite.NotAMember);
        }

        if (!player.IsValid) {
            return Outcome(GuildWrite.NoSuchMember);
        }

        // Already here at that rank: nothing to do, and nothing wrong. A retried invite.
        if (RankOf(player) == rank) {
            return Outcome(GuildWrite.Unchanged);
        }

        if (members.ContainsKey(player)) {
            return SetRank(by, player, rank);
        }

        // ⚠ Strictly below the inviter. Otherwise an officer invites somebody as leader and the
        // guild has two, or as their own equal and can no longer remove them.
        if (rank <= actor) {
            return Outcome(GuildWrite.Outranked);
        }

        if (members.Count >= Capacity) {
            return Outcome(GuildWrite.Full);
        }

        members[player] = new(player, rank, Now());

        return Changed();
    }

    /// <summary>Removes somebody.</summary>
    /// <param name="by">Who is removing, or the member themselves for leaving.</param>
    /// <param name="player">Who goes.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     ⚠ <b>The last leader may only leave when they are the last member</b>, which is how a
    ///     guild ends. Otherwise the guild would be left with a roster nobody can administer, and
    ///     no method here could put a leader back.
    /// </remarks>
    public GuildOutcome Remove(PlayerKey by, PlayerKey player) {
        if (!Exists) {
            return Outcome(GuildWrite.NotFound);
        }

        var actor = RankOf(by);
        var target = RankOf(player);

        if (actor < 0) {
            return Outcome(GuildWrite.NotAMember);
        }

        if (target < 0) {
            return Outcome(GuildWrite.NoSuchMember);
        }

        // Leaving is always allowed; removing somebody else needs to outrank them.
        if (by != player && target <= actor) {
            return Outcome(GuildWrite.Outranked);
        }

        if (target == 0 && members.Count > 1) {
            return Outcome(GuildWrite.Outranked);
        }

        members.Remove(player);

        return Changed();
    }

    /// <summary>Moves somebody.</summary>
    /// <param name="by">Who is promoting.</param>
    /// <param name="player">Who moves.</param>
    /// <param name="rank">To what rank.</param>
    /// <returns>The outcome.</returns>
    public GuildOutcome SetRank(PlayerKey by, PlayerKey player, int rank) {
        if (!Exists) {
            return Outcome(GuildWrite.NotFound);
        }

        var actor = RankOf(by);
        var target = RankOf(player);

        if (actor < 0) {
            return Outcome(GuildWrite.NotAMember);
        }

        if (target < 0) {
            return Outcome(GuildWrite.NoSuchMember);
        }

        if (target == rank) {
            return Outcome(GuildWrite.Unchanged);
        }

        // You may only move somebody you already outrank.
        if (target <= actor && by != player) {
            return Outcome(GuildWrite.Outranked);
        }

        if (rank == 0) {
            // Handing the guild over. Rank 0 is single, so the old leader steps down in the same
            // turn — two leaders is a state nothing here could resolve afterwards.
            if (actor != 0) {
                return Outcome(GuildWrite.Outranked);
            }

            members[by] = members[by] with { Rank = 1 };
        } else if (rank <= actor) {
            // ⚠ At their own rank, not merely above it, and a test is what found the difference.
            // Promoting somebody to your own rank makes you peers, and neither of you can act on the
            // other again — the deadlock the rank check exists to prevent, reached from the other
            // side. Handing the guild over is the one exception, and it is the branch above.
            return Outcome(GuildWrite.Outranked);
        }

        members[player] = members[player] with { Rank = rank };

        return Changed();
    }

    /// <summary>Renames a rank, for this guild only.</summary>
    /// <param name="by">Who. Must be rank 0.</param>
    /// <param name="rank">Which rank.</param>
    /// <param name="name">What to call it, or empty for the charter's own.</param>
    /// <returns>The outcome.</returns>
    public GuildOutcome RenameRank(PlayerKey by, int rank, string? name) {
        if (!Exists) {
            return Outcome(GuildWrite.NotFound);
        }

        if (RankOf(by) < 0) {
            return Outcome(GuildWrite.NotAMember);
        }

        if (RankOf(by) != 0) {
            return Outcome(GuildWrite.Outranked);
        }

        if (rank < 0) {
            return Outcome(GuildWrite.NoSuchMember);
        }

        if (string.IsNullOrEmpty(name)) {
            return rankNames.Remove(rank) ? Changed() : Outcome(GuildWrite.Unchanged);
        }

        if (rankNames.TryGetValue(rank, out var already) && string.Equals(already, name, StringComparison.Ordinal)) {
            return Outcome(GuildWrite.Unchanged);
        }

        rankNames[rank] = name;

        return Changed();
    }

    /// <summary>Puts a saved guild back, as it was, with no checks.</summary>
    /// <param name="saved">What it was.</param>
    /// <param name="capacity">How many its charter allows now, which a patch may have changed.</param>
    /// <remarks>
    ///     ⚠ <b>A roster over today's capacity loads anyway.</b> A charter that shrank must not evict
    ///     people; what it does is stop new invites, which falls out of <see cref="Add" />'s check
    ///     without anything here having to decide who goes.
    /// </remarks>
    public void Restore(GuildRecord saved, int capacity) {
        ArgumentNullException.ThrowIfNull(saved);

        members.Clear();
        rankNames.Clear();

        Charter = saved.Charter;
        Name = saved.Name;
        Capacity = Math.Max(1, capacity);
        Founded = saved.Founded;
        Revision = saved.Revision;

        foreach (var member in saved.Members) {
            if (member.Player.IsValid) {
                members[member.Player] = member;
            }
        }

        foreach (var (rank, name) in saved.RankNames) {
            rankNames[rank] = name;
        }
    }

    GuildOutcome Changed() {
        Revision++;

        return new(GuildWrite.Applied, Revision);
    }

    GuildOutcome Outcome(GuildWrite write) => new(write, Revision);
}

/// <summary>One guild, keyed by its own guid.</summary>
public sealed class GuildGrain : Grain, IGuildGrain {
    readonly GuildState guild = new();

    /// <inheritdoc />
    public Task<GuildRecord> Read() => Task.FromResult(guild.Read());

    /// <inheritdoc />
    public Task<GuildOutcome> Found(PlayerKey founder, string charter, string name, int capacity) =>
        Task.FromResult(guild.Found(founder, charter, name, capacity));

    /// <inheritdoc />
    public Task<GuildOutcome> Add(PlayerKey by, PlayerKey player, int rank) =>
        Task.FromResult(guild.Add(by, player, rank));

    /// <inheritdoc />
    public Task<GuildOutcome> Remove(PlayerKey by, PlayerKey player) => Task.FromResult(guild.Remove(by, player));

    /// <inheritdoc />
    public Task<GuildOutcome> SetRank(PlayerKey by, PlayerKey player, int rank) =>
        Task.FromResult(guild.SetRank(by, player, rank));

    /// <inheritdoc />
    public Task<GuildOutcome> RenameRank(PlayerKey by, int rank, string name) =>
        Task.FromResult(guild.RenameRank(by, rank, name));
}
