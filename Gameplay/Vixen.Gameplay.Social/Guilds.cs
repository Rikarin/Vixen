// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Social;

/// <summary>One guild's identity.</summary>
/// <param name="Value">The number.</param>
public readonly record struct GuildId(Guid Value) {
    /// <summary>No guild.</summary>
    public static GuildId None => default;

    /// <summary>Whether this names a guild.</summary>
    public bool IsSome => Value != Guid.Empty;

    /// <summary>Mints a fresh one.</summary>
    /// <returns>The id.</returns>
    public static GuildId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => IsSome ? Value.ToString("N")[..8] : "no guild";
}

/// <summary>Why a guild operation was refused.</summary>
public enum GuildRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>The guild is at capacity.</summary>
    Full,

    /// <summary>They are already in it.</summary>
    AlreadyIn,

    /// <summary>They are not in it.</summary>
    NotIn,

    /// <summary>Their rank does not carry the permission this needs.</summary>
    NoPermission,

    /// <summary>There is no such rank.</summary>
    UnknownRank,

    /// <summary>You may not act on somebody at or above your own rank.</summary>
    OutranksYou,

    /// <summary>It would leave the guild with nobody who can run it.</summary>
    WouldStrand
}

/// <summary>One rung of an authored rank ladder.</summary>
[DataContract("GuildRankDefinition")]
public sealed class GuildRankDefinition {
    /// <summary>What it is called — <c>Officer</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What it may do — <c>Guild.Permission.Invite</c>.</summary>
    /// <remarks>
    ///     Doc 28: <em>"a permission matrix that is a tag query per action, so a game adds a guild
    ///     permission by adding a tag"</em>. There is no enum of permissions here and there is not
    ///     meant to be one.
    /// </remarks>
    public List<string> Permissions { get; set; } = [];
}

/// <summary>What a new guild starts as: its cap and its rank ladder.</summary>
/// <remarks>
///     ⚠ <b>A charter is content and a guild is not.</b> A guild is made by players at runtime, so it
///     is durable state rather than an authored definition; what a designer authors is what a *new*
///     one starts with. Getting this backwards makes every guild in the game share one editable
///     object.
/// </remarks>
[DataContract("GuildCharterDefinition")]
public sealed record GuildCharterDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How many members a guild may have.</summary>
    public int MaximumMembers { get; set; } = 500;

    /// <summary>The ladder, highest first. The first rung is the one that founded it.</summary>
    public List<GuildRankDefinition> Ranks { get; set; } = [];

    /// <summary>What being in one is — <c>Guild.Member</c>. Empty for a guild nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var rank in Ranks) {
            foreach (var permission in rank.Permissions) {
                tags.Add(permission);
            }
        }
    }
}

/// <summary>One rung of a guild's ladder, with its permissions resolved.</summary>
public sealed class GuildRank {
    readonly GameplayTagSet permissions = new();

    internal GuildRank(string name, int order, IEnumerable<GameplayTag> permissions) {
        Name = name;
        Order = order;

        foreach (var permission in permissions) {
            if (permission.IsSome) {
                this.permissions.Add(permission);
            }
        }
    }

    /// <summary>What it is called.</summary>
    public string Name { get; internal set; }

    /// <summary>Where it sits. Zero is the top.</summary>
    public int Order { get; }

    /// <summary>What it may do.</summary>
    public GameplayTagSet Permissions => permissions;

    /// <summary>Whether it carries a permission.</summary>
    /// <param name="permission">The prefix, resolved.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>The top rank carries everything, whatever the charter said.</b> A guild whose leader
    ///     rank was authored without <c>Guild.Permission.Invite</c> is a guild nobody can ever invite
    ///     to and nobody can fix, because fixing it needs a permission too. This is the one hard-coded
    ///     rule in the matrix and it is here to stop a content mistake bricking a player's guild.
    /// </remarks>
    public bool Allows(GameplayTagRange permission) => Order == 0 || permissions.ContainsAny(permission);
}

/// <summary>A guild: a roster, a ladder, and a permission matrix that is a tag query per action.</summary>
/// <remarks>
///     <para>
///         <b>Durable state, and doc 28 says it lives in a grain</b> — <c>IGuildGrain</c>, which doc 27
///         explicitly hands to this document. What is here is the rules; keeping it is
///         <see cref="ISocialStore" />'s.
///     </para>
///     <para>
///         ⚠ <b>A guild always has exactly one member at rank zero.</b> Every operation that could
///         remove the last one is refused with <see cref="GuildRefusal.WouldStrand" />: a guild whose
///         leader left, demoted themselves or was somehow removed is a guild that cannot invite,
///         cannot promote and cannot be disbanded, and the only fix is an operator with database
///         access. The leader has to hand it over first, which is one extra click and no support
///         tickets.
///     </para>
///     <para>
///         ⚠ <b>Nobody may act on somebody at or above their own rank.</b> Without it an officer can
///         kick the leader, and every guild in the game is one disgruntled officer from being emptied.
///     </para>
/// </remarks>
public sealed class Guild {
    readonly Dictionary<PlayerId, int> roster = [];
    readonly List<GuildRank> ranks = [];

    /// <summary>Founds a guild.</summary>
    /// <param name="charter">What it starts as.</param>
    /// <param name="founder">Who founded it, who takes rank zero.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="id">Its id, or a fresh one.</param>
    public Guild(GuildCharter charter, PlayerId founder, string name = "", GuildId id = default) {
        ArgumentNullException.ThrowIfNull(charter);

        Charter = charter;
        Name = name;
        Id = id.IsSome ? id : GuildId.New();

        foreach (var rank in charter.Ranks) {
            ranks.Add(new(rank.Name, rank.Order, rank.Permissions));
        }

        if (ranks.Count == 0) {
            ranks.Add(new("Leader", 0, []));
        }

        if (founder.IsSome) {
            roster.Add(founder, 0);
        }
    }

    /// <summary>Its id.</summary>
    public GuildId Id { get; }

    /// <summary>What it is called.</summary>
    public string Name { get; set; }

    /// <summary>What it started as.</summary>
    public GuildCharter Charter { get; }

    /// <summary>Its ladder, highest first.</summary>
    public IReadOnlyList<GuildRank> Ranks => ranks;

    /// <summary>Its roster, as a player and the index of their rank.</summary>
    public IReadOnlyDictionary<PlayerId, int> Roster => roster;

    /// <summary>How many are in it.</summary>
    public int Count => roster.Count;

    /// <summary>Who runs it, or <see cref="PlayerId.None" /> for a guild with nobody left.</summary>
    public PlayerId Leader {
        get {
            foreach (var (player, rank) in roster) {
                if (rank == 0) {
                    return player;
                }
            }

            return PlayerId.None;
        }
    }

    /// <summary>What rank somebody holds.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The index, or −1 when they are not in it.</returns>
    public int RankOf(PlayerId player) => roster.TryGetValue(player, out var rank) ? rank : -1;

    /// <summary>Whether somebody may do something.</summary>
    /// <param name="player">Who.</param>
    /// <param name="permission">The permission's prefix, resolved.</param>
    /// <returns>Whether their rank carries it.</returns>
    public bool Can(PlayerId player, GameplayTagRange permission) {
        var rank = RankOf(player);

        return rank >= 0 && ranks[rank].Allows(permission);
    }

    /// <summary>Adds somebody at the bottom rung.</summary>
    /// <param name="by">Who is inviting, or <see cref="PlayerId.None" /> for the realm itself.</param>
    /// <param name="player">Who is joining.</param>
    /// <param name="invitePermission">Which permission inviting needs.</param>
    /// <returns>The refusal, or <see cref="GuildRefusal.None" />.</returns>
    public GuildRefusal Add(PlayerId by, PlayerId player, GameplayTagRange invitePermission = default) {
        if (by.IsSome && !Can(by, invitePermission)) {
            return GuildRefusal.NoPermission;
        }

        if (roster.ContainsKey(player)) {
            return GuildRefusal.AlreadyIn;
        }

        if (roster.Count >= Charter.MaximumMembers) {
            return GuildRefusal.Full;
        }

        roster.Add(player, ranks.Count - 1);

        return GuildRefusal.None;
    }

    /// <summary>Removes somebody.</summary>
    /// <param name="by">Who is doing it, or <see cref="PlayerId.None" /> for the realm itself.</param>
    /// <param name="player">Who is going.</param>
    /// <param name="kickPermission">Which permission removing somebody else needs.</param>
    /// <returns>The refusal, or <see cref="GuildRefusal.None" />.</returns>
    public GuildRefusal Remove(PlayerId by, PlayerId player, GameplayTagRange kickPermission = default) {
        if (!roster.TryGetValue(player, out var rank)) {
            return GuildRefusal.NotIn;
        }

        if (by.IsSome && by != player) {
            if (!Can(by, kickPermission)) {
                return GuildRefusal.NoPermission;
            }

            if (RankOf(by) >= rank) {
                return GuildRefusal.OutranksYou;
            }
        }

        // Leaving is allowed to anybody except the last leader, who has to hand it over first.
        if (rank == 0 && roster.Count > 1) {
            return GuildRefusal.WouldStrand;
        }

        roster.Remove(player);

        return GuildRefusal.None;
    }

    /// <summary>Moves somebody to another rung.</summary>
    /// <param name="by">Who is doing it, or <see cref="PlayerId.None" /> for the realm itself.</param>
    /// <param name="player">Who is moving.</param>
    /// <param name="rank">Which rung.</param>
    /// <returns>The refusal, or <see cref="GuildRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Moving somebody to rank zero moves the current leader down one</b>, which is what
    ///     handing a guild over is, and it is the only way rank zero ever changes hands. Doing it as
    ///     two operations would leave a window with two leaders or none.
    /// </remarks>
    public GuildRefusal SetRank(PlayerId by, PlayerId player, int rank) {
        if (!roster.TryGetValue(player, out var current)) {
            return GuildRefusal.NotIn;
        }

        if ((uint)rank >= (uint)ranks.Count) {
            return GuildRefusal.UnknownRank;
        }

        if (by.IsSome) {
            var mine = RankOf(by);

            if (mine < 0) {
                return GuildRefusal.NotIn;
            }

            // Strictly above both where they are and where they are going: an officer who could
            // promote somebody *to* their own rank has promoted somebody who can now demote them.
            if (mine >= current || mine >= rank) {
                return mine == 0 && rank == 0 ? Handover(by, player) : GuildRefusal.OutranksYou;
            }
        } else if (rank == 0) {
            return Handover(Leader, player);
        }

        roster[player] = rank;

        return GuildRefusal.None;
    }

    /// <summary>Renames a rung.</summary>
    /// <param name="rank">Which one.</param>
    /// <param name="name">What it is now called.</param>
    /// <returns>Whether there is such a rung.</returns>
    public bool RenameRank(int rank, string name) {
        if ((uint)rank >= (uint)ranks.Count) {
            return false;
        }

        ranks[rank].Name = name;

        return true;
    }

    /// <summary>Everybody at a rung.</summary>
    /// <param name="rank">Which one.</param>
    /// <returns>Them, in player order so a roster is stable.</returns>
    public IEnumerable<PlayerId> At(int rank) =>
        roster.Where(entry => entry.Value == rank).Select(entry => entry.Key).OrderBy(player => player);

    GuildRefusal Handover(PlayerId from, PlayerId to) {
        if (from == to) {
            return GuildRefusal.None;
        }

        if (!roster.ContainsKey(to) || RankOf(from) != 0) {
            return GuildRefusal.NotIn;
        }

        roster[to] = 0;
        roster[from] = Math.Min(1, ranks.Count - 1);

        return GuildRefusal.None;
    }
}
