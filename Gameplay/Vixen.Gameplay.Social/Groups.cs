// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Social;

/// <summary>One group's identity.</summary>
/// <param name="Value">The number.</param>
/// <remarks>
///     A <see cref="Guid" /> rather than a hash of anything, because a group is made at runtime by
///     players and there is no address to hash. It is the same shape doc 27's gate protocol already
///     carries a party and a guild as.
/// </remarks>
public readonly record struct GroupId(Guid Value) {
    /// <summary>No group.</summary>
    public static GroupId None => default;

    /// <summary>Whether this names a group.</summary>
    public bool IsSome => Value != Guid.Empty;

    /// <summary>Mints a fresh one.</summary>
    /// <returns>The id.</returns>
    public static GroupId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => IsSome ? Value.ToString("N")[..8] : "no group";
}

/// <summary>What kind of group it is.</summary>
/// <remarks>
///     ⚠ <b>Three kinds and one implementation, which is the decision worth reading.</b> A party, a
///     raid squad and a match team differ in how many fit, whether there are subgroups and who may
///     invite — all of which are numbers on a policy. Writing three types would be three
///     implementations of "the leader left, who is leader now", and games get that wrong once per type.
/// </remarks>
public enum GroupKind {
    /// <summary>Small and ad-hoc.</summary>
    Party,

    /// <summary>Large, with subgroups.</summary>
    Squad,

    /// <summary>Match-scoped, and usually nobody may leave.</summary>
    Team
}

/// <summary>Why a group operation was refused.</summary>
public enum GroupRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>The group is at capacity.</summary>
    Full,

    /// <summary>They are already in it.</summary>
    AlreadyIn,

    /// <summary>They are not in it.</summary>
    NotIn,

    /// <summary>Only the leader may do that, and this is not the leader.</summary>
    NotLeader,

    /// <summary>There is no invite for them.</summary>
    NoInvite,

    /// <summary>There was, and it has expired.</summary>
    InviteExpired,

    /// <summary>That subgroup is at capacity, or there is no such subgroup.</summary>
    BadSubgroup,

    /// <summary>This group's policy has no such role.</summary>
    UnknownRole,

    /// <summary>One of them has blocked the other.</summary>
    Blocked,

    /// <summary>The policy does not allow it at all.</summary>
    Forbidden
}

/// <summary>How big a group is, who may invite, and what roles it has.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///     <see cref="ModifierDefinition" />.
/// </remarks>
[DataContract("GroupPolicyDefinition")]
public sealed record GroupPolicyDefinition : Definition {
    /// <summary>What kind of group this describes.</summary>
    public GroupKind Kind { get; set; }

    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How many fit.</summary>
    public int MaximumMembers { get; set; } = 5;

    /// <summary>How many fit in one subgroup, or zero for a group with no subgroups.</summary>
    public int SubgroupSize { get; set; }

    /// <summary>Whether anybody but the leader may invite.</summary>
    public bool MembersMayInvite { get; set; }

    /// <summary>Whether anybody may leave. False for a match team.</summary>
    public bool MembersMayLeave { get; set; } = true;

    /// <summary>How long an invite stands, in seconds.</summary>
    public float InviteSeconds { get; set; } = 60f;

    /// <summary>What being in one is — <c>Group.Party</c>. Empty for a group nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The roles a member may take — <c>Role.Tank</c>. Empty for a group with no roles.</summary>
    public List<string> Roles { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var role in Roles) {
            tags.Add(role);
        }
    }
}

/// <summary>A group policy with its names resolved.</summary>
public sealed class GroupPolicy {
    readonly GameplayTag[] roles;

    internal GroupPolicy(GroupPolicyDefinition definition, GameplayTag tag, GameplayTag[] roles) {
        Definition = definition;
        Tag = tag;
        this.roles = roles;
    }

    /// <summary>The one a group gets when nothing was authored: a party of five.</summary>
    public static GroupPolicy Default { get; } = new(new(), GameplayTag.None, []);

    /// <summary>What it was compiled from.</summary>
    public GroupPolicyDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What kind of group it describes.</summary>
    public GroupKind Kind => Definition.Kind;

    /// <summary>What being in one is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>How many fit, never below one.</summary>
    public int MaximumMembers => Math.Max(1, Definition.MaximumMembers);

    /// <summary>How many fit in one subgroup, or zero.</summary>
    public int SubgroupSize => Math.Max(0, Definition.SubgroupSize);

    /// <summary>How many subgroups there are. One for a group with none.</summary>
    public int Subgroups =>
        SubgroupSize == 0 ? 1 : (MaximumMembers + SubgroupSize - 1) / SubgroupSize;

    /// <summary>How long an invite stands.</summary>
    public float InviteSeconds => MathF.Max(0f, Definition.InviteSeconds);

    /// <summary>The roles a member may take.</summary>
    public ReadOnlySpan<GameplayTag> Roles => roles;

    /// <summary>Whether a tag is one of this group's roles.</summary>
    /// <param name="role">The tag, or <see cref="GameplayTag.None" /> for no role.</param>
    /// <returns>Whether it may be set.</returns>
    public bool AllowsRole(GameplayTag role) => !role.IsSome || roles.Contains(role);
}

/// <summary>One member of a group.</summary>
/// <param name="Player">Who.</param>
/// <param name="Subgroup">Which subgroup, or zero.</param>
/// <param name="Role">What they signed up as, or <see cref="GameplayTag.None" />.</param>
/// <param name="Joined">The order they joined in — what leadership falls to.</param>
public readonly record struct GroupMember(PlayerId Player, int Subgroup, GameplayTag Role, int Joined);

/// <summary>An invite standing until somebody answers or it expires.</summary>
/// <param name="To">Who was invited.</param>
/// <param name="From">Who invited them.</param>
/// <param name="Expires">When it stops standing, on the caller's clock.</param>
public readonly record struct GroupInvite(PlayerId To, PlayerId From, float Expires);

/// <summary>A party, a squad or a team: the volatile membership and everything that changes it.</summary>
/// <remarks>
///     <para>
///         <b>Volatile, and doc 28 says the durable copy is a grain</b> — party and squad state
///         outlives any one shard and drives doc 27's placement. This is the logic; keeping it is
///         <see cref="ISocialStore" />'s, for the same reason <c>IPityStore</c> exists.
///     </para>
///     <para>
///         ⚠ <b>A non-empty group always has exactly one leader, and that is the invariant everything
///         else is arranged around.</b> A leaderless group cannot invite, cannot kick and cannot be
///         disbanded — it is a group that has to be cleaned up by an operator. So leadership passes on
///         a leader leaving or being removed, and it passes to the <em>longest-standing</em> remaining
///         member rather than to whoever a dictionary enumerated first: the rule has to be one a
///         client can predict and two servers agree on.
///     </para>
/// </remarks>
public sealed class PlayerGroup {
    readonly List<GroupMember> members = [];
    readonly List<GroupInvite> invites = [];

    int joined;

    /// <summary>Makes a group with one member, who leads it.</summary>
    /// <param name="policy">How big it is and who may do what.</param>
    /// <param name="founder">Who starts it.</param>
    /// <param name="id">Its id, or a fresh one.</param>
    public PlayerGroup(GroupPolicy policy, PlayerId founder, GroupId id = default) {
        ArgumentNullException.ThrowIfNull(policy);

        Policy = policy;
        Id = id.IsSome ? id : GroupId.New();

        if (founder.IsSome) {
            members.Add(new(founder, 0, GameplayTag.None, joined++));
            Leader = founder;
        }
    }

    /// <summary>Its id.</summary>
    public GroupId Id { get; }

    /// <summary>How big it is and who may do what.</summary>
    public GroupPolicy Policy { get; }

    /// <summary>Who leads it, or <see cref="PlayerId.None" /> when it is empty.</summary>
    public PlayerId Leader { get; private set; }

    /// <summary>Its members, in the order they joined.</summary>
    public IReadOnlyList<GroupMember> Members => members;

    /// <summary>How many are in it.</summary>
    public int Count => members.Count;

    /// <summary>Whether nobody is left.</summary>
    public bool IsEmpty => members.Count == 0;

    /// <summary>The invites standing right now.</summary>
    public IReadOnlyList<GroupInvite> Invites => invites;

    /// <summary>Raised whenever the membership changes.</summary>
    public event Action<PlayerGroup>? Changed;

    /// <summary>Whether somebody is in it.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they are.</returns>
    public bool Contains(PlayerId player) => IndexOf(player) >= 0;

    /// <summary>One member.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Them, or null.</returns>
    public GroupMember? Find(PlayerId player) {
        var index = IndexOf(player);

        return index >= 0 ? members[index] : null;
    }

    /// <summary>Invites somebody.</summary>
    /// <param name="from">Who is inviting.</param>
    /// <param name="to">Who is being invited.</param>
    /// <param name="now">The clock.</param>
    /// <param name="graph">The inviter's social graph, so a block refuses — or null to skip that.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Capacity counts the standing invites, not just the members.</b> A party of four with
    ///     three invites out is a party that becomes a party of seven the moment they are all accepted,
    ///     and the check that happens on accept is a check that happens after somebody was told yes.
    /// </remarks>
    public GroupRefusal Invite(PlayerId from, PlayerId to, float now, SocialGraph? graph = null) {
        if (!Contains(from)) {
            return GroupRefusal.NotIn;
        }

        if (!Policy.Definition.MembersMayInvite && from != Leader) {
            return GroupRefusal.NotLeader;
        }

        if (Contains(to)) {
            return GroupRefusal.AlreadyIn;
        }

        Expire(now);

        if (members.Count + invites.Count >= Policy.MaximumMembers) {
            return GroupRefusal.Full;
        }

        if (graph is not null && (graph.HasBlocked(to) || graph.IsBlockedBy(to))) {
            return GroupRefusal.Blocked;
        }

        var standing = invites.FindIndex(invite => invite.To == to);
        var fresh = new GroupInvite(to, from, now + Policy.InviteSeconds);

        if (standing >= 0) {
            invites[standing] = fresh;
        } else {
            invites.Add(fresh);
        }

        return GroupRefusal.None;
    }

    /// <summary>Takes an invite up.</summary>
    /// <param name="player">Who is joining.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal Accept(PlayerId player, float now) {
        var index = invites.FindIndex(invite => invite.To == player);

        if (index < 0) {
            return GroupRefusal.NoInvite;
        }

        var invite = invites[index];

        invites.RemoveAt(index);

        if (now > invite.Expires) {
            return GroupRefusal.InviteExpired;
        }

        if (Contains(player)) {
            return GroupRefusal.AlreadyIn;
        }

        if (members.Count >= Policy.MaximumMembers) {
            return GroupRefusal.Full;
        }

        members.Add(new(player, FirstFreeSubgroup(), GameplayTag.None, joined++));

        if (!Leader.IsSome) {
            Leader = player;
        }

        Changed?.Invoke(this);

        return GroupRefusal.None;
    }

    /// <summary>Turns an invite down, or lets a leader withdraw one.</summary>
    /// <param name="player">Who was invited.</param>
    /// <returns>Whether there was one.</returns>
    public bool Decline(PlayerId player) {
        var index = invites.FindIndex(invite => invite.To == player);

        if (index < 0) {
            return false;
        }

        invites.RemoveAt(index);

        return true;
    }

    /// <summary>Forgets the invites that have run out.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>How many were dropped.</returns>
    public int Expire(float now) => invites.RemoveAll(invite => now > invite.Expires);

    /// <summary>Leaves.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal Leave(PlayerId player) =>
        Policy.Definition.MembersMayLeave ? Remove(player) : GroupRefusal.Forbidden;

    /// <summary>Throws somebody out.</summary>
    /// <param name="by">Who is doing it.</param>
    /// <param name="player">Who is going.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal Kick(PlayerId by, PlayerId player) {
        if (by != Leader) {
            return GroupRefusal.NotLeader;
        }

        // ⚠ A leader kicking themselves is a leave, not a refusal: the gesture exists in every client
        // and refusing it leaves a leader who cannot get out of their own party.
        return Remove(player);
    }

    /// <summary>Hands leadership over.</summary>
    /// <param name="by">Who is handing it over.</param>
    /// <param name="player">Who is taking it.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal Promote(PlayerId by, PlayerId player) {
        if (by != Leader) {
            return GroupRefusal.NotLeader;
        }

        if (!Contains(player)) {
            return GroupRefusal.NotIn;
        }

        Leader = player;
        Changed?.Invoke(this);

        return GroupRefusal.None;
    }

    /// <summary>Sets what somebody signed up as.</summary>
    /// <param name="player">Who.</param>
    /// <param name="role">Which role, or <see cref="GameplayTag.None" /> for none.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal SetRole(PlayerId player, GameplayTag role) {
        var index = IndexOf(player);

        if (index < 0) {
            return GroupRefusal.NotIn;
        }

        if (!Policy.AllowsRole(role)) {
            return GroupRefusal.UnknownRole;
        }

        members[index] = members[index] with { Role = role };
        Changed?.Invoke(this);

        return GroupRefusal.None;
    }

    /// <summary>Moves somebody to another subgroup.</summary>
    /// <param name="by">Who is doing it.</param>
    /// <param name="player">Who is moving.</param>
    /// <param name="subgroup">Where to.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal MoveTo(PlayerId by, PlayerId player, int subgroup) {
        if (by != Leader) {
            return GroupRefusal.NotLeader;
        }

        var index = IndexOf(player);

        if (index < 0) {
            return GroupRefusal.NotIn;
        }

        if ((uint)subgroup >= (uint)Policy.Subgroups) {
            return GroupRefusal.BadSubgroup;
        }

        if (subgroup == members[index].Subgroup) {
            return GroupRefusal.None;
        }

        if (Policy.SubgroupSize > 0 && Occupancy(subgroup) >= Policy.SubgroupSize) {
            return GroupRefusal.BadSubgroup;
        }

        members[index] = members[index] with { Subgroup = subgroup };
        Changed?.Invoke(this);

        return GroupRefusal.None;
    }

    /// <summary>How many are in a subgroup.</summary>
    /// <param name="subgroup">Which one.</param>
    /// <returns>How many.</returns>
    public int Occupancy(int subgroup) {
        var count = 0;

        foreach (var member in members) {
            if (member.Subgroup == subgroup) {
                count++;
            }
        }

        return count;
    }

    /// <summary>Empties it.</summary>
    /// <param name="by">Who is doing it.</param>
    /// <returns>The refusal, or <see cref="GroupRefusal.None" />.</returns>
    public GroupRefusal Disband(PlayerId by) {
        if (by != Leader) {
            return GroupRefusal.NotLeader;
        }

        members.Clear();
        invites.Clear();
        Leader = PlayerId.None;
        Changed?.Invoke(this);

        return GroupRefusal.None;
    }

    GroupRefusal Remove(PlayerId player) {
        var index = IndexOf(player);

        if (index < 0) {
            return GroupRefusal.NotIn;
        }

        members.RemoveAt(index);
        invites.RemoveAll(invite => invite.From == player);

        if (Leader == player) {
            // The longest-standing remaining member, which is a rule a client can predict and two
            // servers agree on. "Whoever is first in the list" is neither once a list has been sorted.
            Leader = PlayerId.None;

            var earliest = int.MaxValue;

            foreach (var member in members) {
                if (member.Joined < earliest) {
                    earliest = member.Joined;
                    Leader = member.Player;
                }
            }
        }

        Changed?.Invoke(this);

        return GroupRefusal.None;
    }

    int IndexOf(PlayerId player) {
        for (var index = 0; index < members.Count; index++) {
            if (members[index].Player == player) {
                return index;
            }
        }

        return -1;
    }

    int FirstFreeSubgroup() {
        if (Policy.SubgroupSize == 0) {
            return 0;
        }

        for (var subgroup = 0; subgroup < Policy.Subgroups; subgroup++) {
            if (Occupancy(subgroup) < Policy.SubgroupSize) {
                return subgroup;
            }
        }

        return 0;
    }
}
