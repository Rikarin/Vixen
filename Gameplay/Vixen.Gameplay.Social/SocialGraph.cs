// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Social;

/// <summary>How somebody is showing up.</summary>
public enum PresenceStatus {
    /// <summary>Not here.</summary>
    Offline,

    /// <summary>Here.</summary>
    Online,

    /// <summary>Here, not looking.</summary>
    Away,

    /// <summary>Here, do not ask.</summary>
    Busy,

    /// <summary>Here, and telling everybody they are not.</summary>
    Invisible
}

/// <summary>Where somebody is and what they are doing.</summary>
/// <param name="Player">Who.</param>
/// <param name="Status">How they are showing up.</param>
/// <param name="Realm">Which realm, in whatever naming the fleet uses.</param>
/// <param name="Scene">Which map, by address.</param>
/// <param name="Note">What they typed as their status.</param>
public readonly record struct PresenceRecord(
    PlayerId Player,
    PresenceStatus Status,
    string Realm = "",
    DefId Scene = default,
    string Note = ""
) {
    /// <summary>What a stranger is told about somebody who is not there.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The record.</returns>
    public static PresenceRecord Away(PlayerId player) => new(player, PresenceStatus.Offline);
}

/// <summary>What one player is to another, in a graph being read back.</summary>
/// <remarks>
///     ⚠ <b>One value rather than a set of flags, because the four are exclusive.</b>
///     <see cref="SocialGraph.Block" /> drops the friendship and both requests, so "blocked friend"
///     is not a state this graph can be in — and a flags enum would invite storage to write one.
/// </remarks>
public enum SocialTie {
    /// <summary>Nothing. What <see cref="SocialGraph.Seat" /> treats as a removal.</summary>
    None,

    /// <summary>A friend.</summary>
    Friend,

    /// <summary>Blocked by the owner.</summary>
    Blocked,

    /// <summary>The owner has asked them and nobody has answered.</summary>
    Requested,

    /// <summary>They have asked the owner.</summary>
    Received
}

/// <summary>One player's friends and blocks.</summary>
/// <remarks>
///     <para>
///         <b>Friendship is mutual and asked for; a block is one-way and silent.</b> Those are two
///         different relations and games that model them as one list with a flag end up letting
///         somebody discover they have been blocked, which is the one thing a block has to avoid.
///     </para>
///     <para>
///         ⚠ <b>Blocking removes the friendship and cancels the pending requests, both ways.</b>
///         Otherwise the person just blocked is still on the friends list, still visible in presence,
///         and still able to be re-invited by a UI that reads the list rather than asking.
///     </para>
/// </remarks>
public sealed class SocialGraph {
    readonly HashSet<PlayerId> friends = [];
    readonly HashSet<PlayerId> blocked = [];
    readonly HashSet<PlayerId> outgoing = [];
    readonly HashSet<PlayerId> incoming = [];

    /// <summary>Makes an empty graph for one player.</summary>
    /// <param name="owner">Whose it is.</param>
    public SocialGraph(PlayerId owner) => Owner = owner;

    /// <summary>Whose it is.</summary>
    public PlayerId Owner { get; }

    /// <summary>Their friends.</summary>
    public IReadOnlyCollection<PlayerId> Friends => friends;

    /// <summary>Who they have blocked.</summary>
    public IReadOnlyCollection<PlayerId> Blocked => blocked;

    /// <summary>Requests they have sent and nobody has answered.</summary>
    public IReadOnlyCollection<PlayerId> Outgoing => outgoing;

    /// <summary>Requests waiting for them.</summary>
    public IReadOnlyCollection<PlayerId> Incoming => incoming;

    /// <summary>Whether somebody is a friend.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they are.</returns>
    public bool IsFriend(PlayerId player) => friends.Contains(player);

    /// <summary>Whether the owner has blocked somebody.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they have.</returns>
    public bool HasBlocked(PlayerId player) => blocked.Contains(player);

    /// <summary>Whether somebody has blocked the owner.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they have, as far as this graph has been told.</returns>
    /// <remarks>
    ///     ⚠ <b>Answered from the <em>other</em> side's graph by whatever owns both, not from here.</b>
    ///     A graph that recorded who had blocked its owner would be a graph a client could be sent, and
    ///     a block the blocked person can see is not a block. <see cref="SocialGraphs" /> is what pairs
    ///     them up; this method exists so a caller with only one graph gets a safe <c>false</c> rather
    ///     than a compile error that pushes them into inventing something worse.
    /// </remarks>
    public bool IsBlockedBy(PlayerId player) => Mirror?.Invoke(player, Owner) == true;

    /// <summary>How to ask whether somebody has blocked the owner. Set by <see cref="SocialGraphs" />.</summary>
    internal Func<PlayerId, PlayerId, bool>? Mirror { get; set; }

    /// <summary>Asks somebody to be a friend.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether the request was made — false when they are blocked, already a friend, or the owner.</returns>
    public bool Request(PlayerId player) {
        if (!player.IsSome || player == Owner || blocked.Contains(player) || friends.Contains(player)) {
            return false;
        }

        // An incoming request that is answered by an outgoing one is an acceptance, which is what a
        // player means by sending it and what every client does when the two cross in the post.
        if (incoming.Remove(player)) {
            friends.Add(player);
            outgoing.Remove(player);

            return true;
        }

        return outgoing.Add(player);
    }

    /// <summary>Records that somebody has asked.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether it was recorded.</returns>
    public bool Receive(PlayerId player) {
        if (!player.IsSome || player == Owner || blocked.Contains(player) || friends.Contains(player)) {
            return false;
        }

        return incoming.Add(player);
    }

    /// <summary>Says yes to a request.</summary>
    /// <param name="player">Who asked.</param>
    /// <returns>Whether there was one.</returns>
    public bool Accept(PlayerId player) {
        if (!incoming.Remove(player)) {
            return false;
        }

        friends.Add(player);

        return true;
    }

    /// <summary>Says no, or withdraws one.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether there was one either way.</returns>
    public bool Dismiss(PlayerId player) => incoming.Remove(player) | outgoing.Remove(player);

    /// <summary>Stops being friends.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were.</returns>
    public bool Unfriend(PlayerId player) => friends.Remove(player);

    /// <summary>Blocks somebody, which unfriends them and drops every request either way.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were not already blocked.</returns>
    public bool Block(PlayerId player) {
        if (!player.IsSome || player == Owner) {
            return false;
        }

        friends.Remove(player);
        incoming.Remove(player);
        outgoing.Remove(player);

        return blocked.Add(player);
    }

    /// <summary>Unblocks somebody. It does not put the friendship back.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were blocked.</returns>
    public bool Unblock(PlayerId player) => blocked.Remove(player);

    /// <summary>What somebody is to the owner.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The tie, or <see cref="SocialTie.None" />.</returns>
    public SocialTie TieTo(PlayerId player) {
        if (blocked.Contains(player)) {
            return SocialTie.Blocked;
        }

        if (friends.Contains(player)) {
            return SocialTie.Friend;
        }

        if (outgoing.Contains(player)) {
            return SocialTie.Requested;
        }

        return incoming.Contains(player) ? SocialTie.Received : SocialTie.None;
    }

    /// <summary>Puts a tie in with no checks at all.</summary>
    /// <param name="player">Who.</param>
    /// <param name="tie">What they are. <see cref="SocialTie.None" /> takes them out.</param>
    /// <returns>Whether anything moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a player action, and the reason a stored graph can be read back.</b>
    ///         <see cref="Request" /> refuses somebody who is blocked and <see cref="Block" /> drops
    ///         the friendship, which are the rules for <em>making</em> a tie. Replaying them over a
    ///         graph that already holds the answer would apply today's rules to yesterday's state and
    ///         silently drop what a patch has since made illegal — <c>Guild.Seat</c> and
    ///         <c>HousePlot.Assign</c> are the same seam for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exclusive, so seating one tie clears the other three.</b> A graph read back with
    ///         somebody on two lists is a graph where <see cref="Block" />'s guarantee has already
    ///         failed, and the cheapest place to stop that is the only door state comes in through.
    ///     </para>
    /// </remarks>
    public bool Seat(PlayerId player, SocialTie tie) {
        if (!player.IsSome || player == Owner) {
            return false;
        }

        var was = TieTo(player);

        friends.Remove(player);
        blocked.Remove(player);
        outgoing.Remove(player);
        incoming.Remove(player);

        switch (tie) {
            case SocialTie.Friend:
                friends.Add(player);

                break;

            case SocialTie.Blocked:
                blocked.Add(player);

                break;

            case SocialTie.Requested:
                outgoing.Add(player);

                break;

            case SocialTie.Received:
                incoming.Add(player);

                break;
        }

        return was != tie;
    }

    /// <summary>Everybody the owner has any tie to, in player order.</summary>
    /// <returns>Them and what they are.</returns>
    /// <remarks>Player order, so two realms holding the same graph write the same bytes.</remarks>
    public IEnumerable<KeyValuePair<PlayerId, SocialTie>> Ties() =>
        friends.Select(player => new KeyValuePair<PlayerId, SocialTie>(player, SocialTie.Friend))
            .Concat(blocked.Select(player => new KeyValuePair<PlayerId, SocialTie>(player, SocialTie.Blocked)))
            .Concat(outgoing.Select(player => new KeyValuePair<PlayerId, SocialTie>(player, SocialTie.Requested)))
            .Concat(incoming.Select(player => new KeyValuePair<PlayerId, SocialTie>(player, SocialTie.Received)))
            .OrderBy(tie => tie.Key);

    /// <summary>Records the other half of a friendship <see cref="SocialGraphs" /> has agreed.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were not already a friend.</returns>
    internal bool Link(PlayerId player) {
        incoming.Remove(player);
        outgoing.Remove(player);

        return friends.Add(player);
    }
}

/// <summary>Everybody's graphs together, which is what makes a block mean anything.</summary>
/// <remarks>
///     A block is a fact about a <em>pair</em>, and one player's graph can only hold half of it.
///     This is the half that knows both — it is what a realm holds, and it is what
///     <see cref="PresenceBook" /> and a group invite ask.
/// </remarks>
public sealed class SocialGraphs {
    readonly Dictionary<PlayerId, SocialGraph> graphs = [];

    /// <summary>How many players it knows about.</summary>
    public int Count => graphs.Count;

    /// <summary>One player's graph, making an empty one if there is none.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their graph.</returns>
    public SocialGraph Of(PlayerId player) {
        if (graphs.TryGetValue(player, out var graph)) {
            return graph;
        }

        graph = new(player) { Mirror = HasBlocked };
        graphs.Add(player, graph);

        return graph;
    }

    /// <summary>Whether one player has blocked another.</summary>
    /// <param name="who">The one who might have blocked.</param>
    /// <param name="whom">The one who might be blocked.</param>
    /// <returns>Whether they have.</returns>
    public bool HasBlocked(PlayerId who, PlayerId whom) =>
        graphs.TryGetValue(who, out var graph) && graph.HasBlocked(whom);

    /// <summary>Whether anything at all stops two players interacting.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether either has blocked the other.</returns>
    /// <remarks>
    ///     ⚠ <b>Symmetric, deliberately.</b> A block is one-way as a *fact* — only one of them chose
    ///     it — but it has to be two-way as a *rule*, or the blocked player can still whisper, still
    ///     invite and still trade, which is every avenue the block was for.
    /// </remarks>
    public bool IsSevered(PlayerId left, PlayerId right) =>
        HasBlocked(left, right) || HasBlocked(right, left);

    /// <summary>Sends a friend request and records it on both sides.</summary>
    /// <param name="from">Who is asking.</param>
    /// <param name="to">Who is being asked.</param>
    /// <returns>Whether it went through.</returns>
    public bool Request(PlayerId from, PlayerId to) {
        if (IsSevered(from, to)) {
            return false;
        }

        return Of(to).Receive(from) & Of(from).Request(to);
    }

    /// <summary>Accepts a request on both sides.</summary>
    /// <param name="who">Who was asked.</param>
    /// <param name="from">Who asked.</param>
    /// <returns>Whether there was one.</returns>
    public bool Accept(PlayerId who, PlayerId from) {
        if (!Of(who).Accept(from)) {
            return false;
        }

        // Both sides, in one call, because a friendship recorded on one side is what a client shows as
        // "they are your friend and you are not theirs".
        Of(from).Link(who);

        return true;
    }

    /// <summary>Ends a friendship on both sides, because half a friendship is a support ticket.</summary>
    /// <param name="who">One.</param>
    /// <param name="other">The other.</param>
    /// <returns>Whether they were friends.</returns>
    public bool Unfriend(PlayerId who, PlayerId other) => Of(who).Unfriend(other) | Of(other).Unfriend(who);
}

/// <summary>Who is online, and what each viewer is allowed to be told.</summary>
public sealed class PresenceBook {
    readonly Dictionary<PlayerId, PresenceRecord> records = [];
    readonly SocialGraphs graphs;

    /// <summary>Makes a book over a set of graphs.</summary>
    /// <param name="graphs">Where blocks are answered from.</param>
    public PresenceBook(SocialGraphs graphs) {
        ArgumentNullException.ThrowIfNull(graphs);

        this.graphs = graphs;
    }

    /// <summary>How many people it has a record for.</summary>
    public int Count => records.Count;

    /// <summary>Records where somebody is.</summary>
    /// <param name="record">The record.</param>
    public void Set(PresenceRecord record) {
        if (record.Player.IsSome) {
            records[record.Player] = record;
        }
    }

    /// <summary>Forgets somebody. What a logout does.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether there was a record.</returns>
    public bool Clear(PlayerId player) => records.Remove(player);

    /// <summary>The truth about somebody, for the realm.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their record, or an offline one.</returns>
    public PresenceRecord Of(PlayerId player) =>
        records.TryGetValue(player, out var record) ? record : PresenceRecord.Away(player);

    /// <summary>What one player may be told about another.</summary>
    /// <param name="viewer">Who is asking.</param>
    /// <param name="subject">Who they are asking about.</param>
    /// <returns>The record, redacted to offline where it must be.</returns>
    /// <remarks>
    ///     ⚠ <b>Redacted here rather than at the UI, because presence is the leak.</b> An invisible
    ///     player whose map still went over the wire is an invisible player anybody with a packet
    ///     capture can follow, and a blocked player who can still see "online, Queensdale" has not
    ///     been blocked in the way that matters. A player always sees themselves.
    /// </remarks>
    public PresenceRecord As(PlayerId viewer, PlayerId subject) {
        if (viewer == subject) {
            return Of(subject);
        }

        var record = Of(subject);

        return record.Status == PresenceStatus.Invisible || graphs.IsSevered(viewer, subject)
            ? PresenceRecord.Away(subject)
            : record;
    }
}
