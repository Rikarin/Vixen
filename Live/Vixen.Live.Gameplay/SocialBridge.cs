// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Gameplay.Social;
using Vixen.Live.Persistence;

namespace Vixen.Live.Gameplay;

/// <summary>What one guild edit is.</summary>
public enum GuildEditKind {
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Somebody joined.</summary>
    Add,

    /// <summary>Somebody went.</summary>
    Remove,

    /// <summary>Somebody moved rung.</summary>
    SetRank,

    /// <summary>A rung was renamed.</summary>
    RenameRank
}

/// <summary>One change to a guild that has not been written down.</summary>
/// <remarks>
///     ⚠ <b>An operation and not a state, and that is the whole design.</b> A roster this realm holds
///     is only the members it can name (see <see cref="SocialBridge" />), so writing one down would
///     delete everybody who happened to be offline. An operation touches exactly who it names.
/// </remarks>
/// <param name="Guild">Which guild.</param>
/// <param name="Kind">What happened.</param>
/// <param name="By">Who did it. Every <c>IGuildGrain</c> method needs one, because every guild rule is about authority.</param>
/// <param name="Player">Who it happened to.</param>
/// <param name="Rank">Which rung, for <see cref="GuildEditKind.SetRank" /> and <see cref="GuildEditKind.RenameRank" />.</param>
/// <param name="Name">What a rung is now called.</param>
public readonly record struct GuildEdit(
    GuildId Guild,
    GuildEditKind Kind,
    PlayerKey By,
    PlayerKey Player,
    int Rank = 0,
    string Name = ""
);

/// <summary>One player's friends and blocks, durably, as the realm last worked it out.</summary>
/// <param name="Owner">Whose.</param>
/// <param name="Links">Everybody they have a tie to, in key order.</param>
public readonly record struct PendingGraph(PlayerKey Owner, ImmutableArray<SocialLink> Links) {
    /// <summary>Whether two say the same thing.</summary>
    /// <param name="other">The other.</param>
    /// <returns>Whether they do.</returns>
    /// <remarks>
    ///     ⚠ Hand-written, for the trap doc 27 § Slice two records: a record's generated equality
    ///     compares an <see cref="ImmutableArray{T}" /> by <em>reference</em>, so a drained write
    ///     never equals the one queued and <see cref="SocialBridge.Settle(PendingGraph)" /> never
    ///     removes anything.
    /// </remarks>
    public bool Equals(PendingGraph other) => Owner == other.Owner && Links.SequenceEqual(other.Links);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Owner, Links.Length);
}

/// <summary>One durable tie.</summary>
/// <param name="Player">Who, durably.</param>
/// <param name="Tie">What they are.</param>
public readonly record struct SocialLink(PlayerKey Player, SocialTie Tie);

/// <summary>
///     Doc 28's social rules against doc 27's <c>IGuildGrain</c>: answered in the frame, written down
///     afterwards, and never awaited.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="LedgerBridge" />'s and <see cref="LockoutBridge" />'s shape</b> —
///         <see cref="ISocialStore" /> is synchronous because a guild-chat line and a party invite
///         both ask it mid-frame, and ADR-016 forbids awaiting the grain that owns the answer. So it
///         is answered from a view and the change is posted.
///     </para>
///     <para>
///         ⚠ <b>The roster this realm holds is only the members it can name, and a 500-member guild
///         has maybe thirty of them online.</b> A gameplay <see cref="PlayerId" /> is a session id
///         widened (see <see cref="GameplayIdentityMap.From" />), so somebody who is not connected to
///         <em>this realm</em> has no gameplay id at all and cannot be seated. That is not a defect
///         to engineer around — it is the same partial view <see cref="LedgerBridge" /> already keeps
///         of a balance — but it makes one thing lethal: <b>a partial roster must never be written
///         back as the whole truth</b>, or the write deletes everybody who was offline.
///     </para>
///     <para>
///         ⚠ <b>So a guild is written as operations, and there is no state-shaped save.</b>
///         <see cref="ISocialStore.SaveGuild" /> is implemented and counted rather than obeyed —
///         see the remarks on it — because a diff of two rosters has thrown away <em>who did it</em>,
///         and every <c>IGuildGrain</c> method needs an actor since every guild rule is about
///         authority. <see cref="Invite" />, <see cref="Kick" />, <see cref="Promote" /> and
///         <see cref="Rename" /> are the path that keeps the actor.
///     </para>
///     <para>
///         ⚠ <b><see cref="GuildOf" /> has <see cref="LockoutBridge" />'s cold-read problem.</b>
///         Returning <see cref="GuildId.None" /> for somebody whose guild was never loaded reads as
///         <em>"in no guild"</em>, which admits them to a rival's guild chat and drops the tag their
///         hall's permissions hang off. The interface cannot say <em>"I do not know"</em>, so
///         <see cref="IsWarm" /> is what a caller checks and a cold read is counted and raised.
///     </para>
///     <para>
///         ⚠ <b>The local checks are optimism and the grain re-checks everything.</b> A cap of 500
///         measured against thirty seated members will say yes when the guild is full. That is
///         <see cref="LedgerBridge" />'s trade exactly: the frame is answered, the grain is the
///         authority, and the disagreement is counted in <see cref="Divergences" />.
///     </para>
/// </remarks>
public sealed class SocialBridge : ISocialStore {
    readonly Dictionary<GuildId, Guild> guilds = [];
    readonly Dictionary<PlayerId, GuildId> membership = [];
    readonly HashSet<PlayerId> warm = [];
    readonly List<GuildEdit> edits = [];

    readonly SocialGraphs graphs = new();
    readonly HashSet<PlayerId> warmGraphs = [];
    readonly Dictionary<PlayerKey, Dictionary<PlayerKey, SocialTie>> durable = [];
    readonly List<PendingGraph> writes = [];

    readonly IGameplayIdentity identity;
    readonly SocialLibrary library;

    Action<PlayerId>? cold;
    Action<GuildEdit>? refused;

    /// <summary>Makes one.</summary>
    /// <param name="identity">Who a gameplay id is, durably.</param>
    /// <param name="library">Where a charter address is resolved. Null for <see cref="GuildCharter.Default" /> throughout.</param>
    public SocialBridge(IGameplayIdentity identity, SocialLibrary? library = null) {
        ArgumentNullException.ThrowIfNull(identity);

        this.identity = identity;
        this.library = library ?? SocialLibrary.Empty;
    }

    /// <summary>How many guilds the view holds.</summary>
    public int Guilds => guilds.Count;

    /// <summary>How many players have had their guild resolved.</summary>
    public int Warm => warm.Count;

    /// <summary>How many guild edits are waiting.</summary>
    public int Pending => edits.Count;

    /// <summary>How many graph writes are waiting.</summary>
    public int PendingGraphs => writes.Count;

    /// <summary>
    ///     How many times somebody's guild or graph was asked about who had never been loaded. Never
    ///     anything but zero.
    /// </summary>
    public int ColdReads { get; private set; }

    /// <summary>How many queued edits the grain refused after the view had allowed them.</summary>
    /// <remarks>
    ///     ⚠ Not zero in normal running, unlike <see cref="LedgerBridge.Divergences" />. A cap
    ///     measured against a partial roster is <em>expected</em> to be wrong sometimes, and that is
    ///     the trade this bridge makes on purpose. What it is for is noticing when the number stops
    ///     looking like the online fraction of a roster.
    /// </remarks>
    public int Divergences { get; private set; }

    /// <summary>How many times a guild was saved by state rather than by operation.</summary>
    /// <remarks>Always zero in a fleet that uses the operations. See <see cref="SaveGuild" />.</remarks>
    public int StateWrites { get; private set; }

    /// <summary>Raised when a guild or a graph is read for somebody who was never loaded.</summary>
    public event Action<PlayerId>? Cold { add => cold += value; remove => cold -= value; }

    /// <summary>Raised when the grain refused an edit the view had already applied.</summary>
    public event Action<GuildEdit>? Refused { add => refused += value; remove => refused -= value; }

    // ── Loading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether somebody's guild has been resolved.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether it has.</returns>
    public bool IsWarm(PlayerId player) => warm.Contains(player);

    /// <summary>Puts somebody's guild in, as the cluster gave it.</summary>
    /// <param name="player">Who is being loaded.</param>
    /// <param name="row">Their guild, or null for somebody who is in none.</param>
    /// <returns>The guild as this realm holds it, or null.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null is a real answer and marks them warm</b>, for
    ///         <see cref="LockoutBridge.Warmed" />'s reason: "in no guild" and "nobody has asked" are
    ///         the same absence in the view and must not be the same fact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A guild already in the view is not rebuilt from the row — only this player is
    ///         seated into it.</b> Rebuilding would throw away every edit made since it was loaded
    ///         and not yet written down, which is a kick that comes back.
    ///     </para>
    /// </remarks>
    public Guild? Warmed(PlayerId player, GuildRow? row) {
        warm.Add(player);
        membership.Remove(player);

        if (row is null) {
            return null;
        }

        var id = new GuildId(row.Id);

        if (!guilds.TryGetValue(id, out var guild)) {
            guild = Rebuild(row);
            guilds.Add(id, guild);

            foreach (var seated in guild.Roster.Keys) {
                membership[seated] = id;
            }
        }

        foreach (var member in row.Members) {
            if (identity.PlayerFor(member.Player) == player && player.IsSome) {
                guild.Seat(player, member.Rank);
                membership[player] = id;
            }
        }

        return guild;
    }

    /// <summary>Puts somebody's friends and blocks in, as storage gave them.</summary>
    /// <param name="owner">Whose, durably.</param>
    /// <param name="links">Their ties. Empty is a real answer and marks them warm.</param>
    /// <returns>The graph as this realm holds it, or null when the owner is not on this realm.</returns>
    /// <remarks>
    ///     ⚠ <b>The durable set is kept whole and the graph is a projection of the nameable part of
    ///     it.</b> Most of a friends list is offline, and a graph that held only the online part
    ///     would lose the rest the first time it was saved. It matters most for a <em>block</em>: the
    ///     person blocked is usually not here, and a block that is not in the graph when they arrive
    ///     is a block that has leaked — which is why <see cref="Admitted" /> exists.
    /// </remarks>
    public SocialGraph? Warmed(PlayerKey owner, IEnumerable<SocialLink> links) {
        ArgumentNullException.ThrowIfNull(links);

        var set = new Dictionary<PlayerKey, SocialTie>();

        foreach (var link in links) {
            if (link.Tie != SocialTie.None) {
                set[link.Player] = link.Tie;
            }
        }

        durable[owner] = set;

        var player = identity.PlayerFor(owner);

        if (!player.IsSome) {
            return null;
        }

        warmGraphs.Add(player);

        var graph = graphs.Of(player);

        foreach (var (key, tie) in set) {
            var other = identity.PlayerFor(key);

            if (other.IsSome) {
                graph.Seat(other, tie);
            }
        }

        return graph;
    }

    /// <summary>Projects everything this realm knows about somebody who has just been let in.</summary>
    /// <param name="key">Their durable identity.</param>
    /// <param name="player">What gameplay calls them.</param>
    /// <returns>How many graphs gained a tie.</returns>
    /// <remarks>
    ///     ⚠ <b>What stops a block leaking.</b> Somebody blocked while they were offline is a
    ///     <see cref="PlayerKey" /> in a durable set and nothing in any graph, so
    ///     <c>SocialGraphs.IsSevered</c> answers false and they can whisper, invite and trade — every
    ///     avenue the block was for. This is the sweep that seats them the moment they have a
    ///     gameplay id.
    /// </remarks>
    public int Admitted(PlayerKey key, PlayerId player) {
        if (!player.IsSome) {
            return 0;
        }

        var seated = 0;

        foreach (var (owner, set) in durable) {
            if (!set.TryGetValue(key, out var tie)) {
                continue;
            }

            var host = identity.PlayerFor(owner);

            if (host.IsSome && warmGraphs.Contains(host) && graphs.Of(host).Seat(player, tie)) {
                seated++;
            }
        }

        return seated;
    }

    /// <summary>Forgets somebody who left.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were here.</returns>
    /// <remarks>
    ///     ⚠ <b>Their pending writes are kept</b>, for <see cref="LockoutBridge.Forget" />'s reason,
    ///     and <b>the guild is kept too if anybody else is still in it</b> — a guild is not one
    ///     player's, and dropping it when one member logs out would make the next member's chat go
    ///     cold.
    /// </remarks>
    public bool Forget(PlayerId player) {
        warmGraphs.Remove(player);

        if (membership.Remove(player, out var id) && guilds.TryGetValue(id, out var guild)) {
            guild.Unseat(player);

            if (guild.Count == 0) {
                guilds.Remove(id);
            }
        }

        return warm.Remove(player);
    }

    // ── Answering in the frame ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Guild? LoadGuild(GuildId id) => guilds.GetValueOrDefault(id);

    /// <inheritdoc />
    /// <remarks>⚠ A cold read is counted and raised — see the remarks on the type.</remarks>
    public GuildId GuildOf(PlayerId player) {
        if (!warm.Contains(player)) {
            ColdReads++;
            cold?.Invoke(player);
        }

        return membership.GetValueOrDefault(player);
    }

    /// <inheritdoc />
    public SocialGraph? LoadGraph(PlayerId player) {
        if (warmGraphs.Contains(player)) {
            return graphs.Of(player);
        }

        ColdReads++;
        cold?.Invoke(player);

        return null;
    }

    /// <summary>Every graph this realm holds, which is what makes a block mean anything.</summary>
    /// <remarks>What <c>PresenceBook</c> and a group invite ask. See <c>SocialGraphs.IsSevered</c>.</remarks>
    public SocialGraphs Graphs => graphs;

    // ── Writing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Invites somebody, applying the rules here and queueing the write.</summary>
    /// <param name="by">Who is inviting.</param>
    /// <param name="id">Which guild.</param>
    /// <param name="player">Who joins.</param>
    /// <param name="invitePermission">Which permission inviting needs.</param>
    /// <returns>The refusal, or <see cref="GuildRefusal.None" />.</returns>
    public GuildRefusal Invite(PlayerId by, GuildId id, PlayerId player, GameplayTagRange invitePermission = default) {
        if (!guilds.TryGetValue(id, out var guild)) {
            return GuildRefusal.NotIn;
        }

        var refusal = guild.Add(by, player, invitePermission);

        if (refusal == GuildRefusal.None) {
            membership[player] = id;
            Queue(id, GuildEditKind.Add, by, player, guild.RankOf(player));
        }

        return refusal;
    }

    /// <summary>Removes somebody, applying the rules here and queueing the write.</summary>
    /// <param name="by">Who is doing it.</param>
    /// <param name="id">Which guild.</param>
    /// <param name="player">Who goes.</param>
    /// <param name="kickPermission">Which permission removing somebody else needs.</param>
    /// <returns>The refusal, or <see cref="GuildRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Only somebody this realm can name may be removed through here.</b> Kicking a member
    ///     who is offline is a guild-panel action against the grain, which is doc 27's service plane
    ///     — it never reaches a realm, and it must not, because a realm cannot name them.
    /// </remarks>
    public GuildRefusal Kick(PlayerId by, GuildId id, PlayerId player, GameplayTagRange kickPermission = default) {
        if (!guilds.TryGetValue(id, out var guild)) {
            return GuildRefusal.NotIn;
        }

        var refusal = guild.Remove(by, player, kickPermission);

        if (refusal == GuildRefusal.None) {
            membership.Remove(player);
            Queue(id, GuildEditKind.Remove, by, player);
        }

        return refusal;
    }

    /// <summary>Moves somebody to another rung, applying the rules here and queueing the write.</summary>
    /// <param name="by">Who is doing it.</param>
    /// <param name="id">Which guild.</param>
    /// <param name="player">Who moves.</param>
    /// <param name="rank">Which rung.</param>
    /// <returns>The refusal, or <see cref="GuildRefusal.None" />.</returns>
    public GuildRefusal Promote(PlayerId by, GuildId id, PlayerId player, int rank) {
        if (!guilds.TryGetValue(id, out var guild)) {
            return GuildRefusal.NotIn;
        }

        var refusal = guild.SetRank(by, player, rank);

        if (refusal == GuildRefusal.None) {
            // The rank asked for, not the rank landed on: a handover moves two people and the grain
            // does the same thing from the same call, so replaying the request keeps them in step.
            Queue(id, GuildEditKind.SetRank, by, player, rank);
        }

        return refusal;
    }

    /// <summary>Renames a rung, applying it here and queueing the write.</summary>
    /// <param name="by">Who is doing it. Rank zero only, which the grain re-checks.</param>
    /// <param name="id">Which guild.</param>
    /// <param name="rank">Which rung.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>Whether there is such a rung.</returns>
    public bool Rename(PlayerId by, GuildId id, int rank, string name) {
        if (!guilds.TryGetValue(id, out var guild) || !guild.RenameRank(rank, name)) {
            return false;
        }

        Queue(id, GuildEditKind.RenameRank, by, PlayerId.None, rank, name);

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Counted, and it writes nothing.</b> Two things are missing from a state-shaped save
    ///     and neither can be recovered: the roster is only the members this realm can name, so
    ///     writing it down deletes the rest; and a diff of two rosters cannot say <em>who</em> moved
    ///     anybody, which every <c>IGuildGrain</c> method needs because every guild rule is about
    ///     authority. <see cref="Invite" />, <see cref="Kick" />, <see cref="Promote" /> and
    ///     <see cref="Rename" /> are the same operations with the actor kept, and a non-zero
    ///     <see cref="StateWrites" /> says something in the game is still going the other way.
    /// </remarks>
    public void SaveGuild(Guild guild) {
        ArgumentNullException.ThrowIfNull(guild);

        StateWrites++;

        // Kept, so a guild founded this frame is at least answerable. Nothing is queued.
        guilds[guild.Id] = guild;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A graph <em>is</em> state-shaped and safely so, which is the difference.</b> It has
    ///     one owner, every change in it is theirs, and so nothing has been thrown away by handing
    ///     over the whole thing. Only the nameable part is diffed: a tie to somebody this realm
    ///     cannot name is carried through untouched, for the same reason a guild's absent members
    ///     are.
    /// </remarks>
    public void SaveGraph(SocialGraph graph) {
        ArgumentNullException.ThrowIfNull(graph);

        if (!identity.TryResolve(graph.Owner, out var owner)) {
            ColdReads++;
            cold?.Invoke(graph.Owner);

            return;
        }

        if (!durable.TryGetValue(owner, out var set)) {
            set = [];
            durable.Add(owner, set);
        }

        var live = new Dictionary<PlayerKey, SocialTie>();

        foreach (var (player, tie) in graph.Ties()) {
            if (identity.TryResolve(player, out var key)) {
                live[key] = tie;
            }
        }

        // Only what this realm can name is diffed away; everybody else stays exactly as loaded.
        foreach (var key in set.Keys.Where(key => identity.PlayerFor(key).IsSome && !live.ContainsKey(key)).ToArray()) {
            set.Remove(key);
        }

        foreach (var (key, tie) in live) {
            set[key] = tie;
        }

        var write = new PendingGraph(
            owner,
            [
                .. set.OrderBy(pair => pair.Key.Account)
                    .ThenBy(pair => pair.Key.Character)
                    .Select(pair => new SocialLink(pair.Key, pair.Value))
            ]
        );

        // One write per owner, replaced rather than appended: a friends list is a whole set and the
        // last one is the truth.
        var index = writes.FindIndex(waiting => waiting.Owner == owner);

        if (index >= 0) {
            writes[index] = write;
        } else {
            writes.Add(write);
        }
    }

    // ── The outbox ────────────────────────────────────────────────────────────────────────────

    /// <summary>Takes every guild edit waiting to be written down.</summary>
    /// <returns>Them, oldest first. Not removed — see <see cref="Settle(GuildEdit)" />.</returns>
    public ImmutableArray<GuildEdit> Drain() => [.. edits];

    /// <summary>Takes every graph waiting to be written down.</summary>
    /// <returns>Them, oldest first. Not removed — see <see cref="Settle(PendingGraph)" />.</returns>
    public ImmutableArray<PendingGraph> DrainGraphs() => [.. writes];

    /// <summary>Says an edit landed.</summary>
    /// <param name="edit">Which.</param>
    /// <returns>Whether it was waiting.</returns>
    public bool Settle(GuildEdit edit) => edits.Remove(edit);

    /// <summary>Says the grain refused an edit the view had already applied.</summary>
    /// <param name="edit">Which.</param>
    /// <param name="refusal">What it said, or <see cref="GuildRefusal.None" /> for a write that landed.</param>
    /// <returns>Whether it was waiting.</returns>
    /// <remarks>
    ///     ⚠ <b>The view is not rolled back.</b> Undoing a join two frames later is a player who was
    ///     in the guild, saw the roster, said hello and was silently ejected — and the next
    ///     <see cref="Warmed(PlayerId, GuildRow?)" /> corrects it from the authority anyway. What the
    ///     realm owes is telling them, which is what <see cref="Refused" /> is for.
    /// </remarks>
    public bool Settle(GuildEdit edit, GuildRefusal refusal) {
        var waiting = edits.Remove(edit);

        if (refusal != GuildRefusal.None) {
            Divergences++;
            refused?.Invoke(edit);
        }

        return waiting;
    }

    /// <summary>Says a graph landed.</summary>
    /// <param name="write">Which.</param>
    /// <returns>Whether it was waiting.</returns>
    public bool Settle(PendingGraph write) => writes.Remove(write);

    // ── Mapping ───────────────────────────────────────────────────────────────────────────────

    Guild Rebuild(GuildRow row) {
        var charter = library.FindCharter(DefId.From(row.Charter)) ?? GuildCharter.Default;
        var guild = new Guild(charter, PlayerId.None, row.Name, new(row.Id));

        foreach (var member in row.Members) {
            var player = identity.PlayerFor(member.Player);

            if (player.IsSome) {
                guild.Seat(player, member.Rank);
            }
        }

        foreach (var (rank, name) in row.RankNames) {
            guild.RenameRank(rank, name);
        }

        return guild;
    }

    void Queue(GuildId id, GuildEditKind kind, PlayerId by, PlayerId player, int rank = 0, string name = "") {
        // An edit nobody can be named for is an edit the grain will refuse, and queueing it would
        // spend a round trip to find that out. Counted as a cold read, because that is what it is:
        // somebody acted whom this realm cannot resolve.
        if (by.IsSome && !identity.TryResolve(by, out _)) {
            ColdReads++;
            cold?.Invoke(by);

            return;
        }

        if (player.IsSome && !identity.TryResolve(player, out _)) {
            ColdReads++;
            cold?.Invoke(player);

            return;
        }

        identity.TryResolve(by, out var actor);
        identity.TryResolve(player, out var subject);

        edits.Add(new(id, kind, actor, subject, rank, name));
    }
}
