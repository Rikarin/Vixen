// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Interaction;

/// <summary>A channel in progress.</summary>
/// <param name="Player">Who is doing it.</param>
/// <param name="Node">Which node.</param>
/// <param name="Started">When they started, on the caller's clock.</param>
/// <param name="Finishes">When it completes.</param>
public readonly record struct Channel(PlayerId Player, DefId Node, float Started, float Finishes);

/// <summary>What using something produced.</summary>
/// <param name="Node">Which node.</param>
/// <param name="Player">Who used it.</param>
/// <param name="Yields">What it yields, by address. <see cref="DefId.None" /> for a door.</param>
/// <param name="Seed">What a loot roll should be seeded from, so the result is reproducible.</param>
public readonly record struct InteractionResult(DefId Node, PlayerId Player, DefId Yields, ulong Seed);

/// <summary>One node in the world: how much is left of it, and who is on it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A shared node is <em>claimed</em> for the duration of a channel, and that is the whole
///         reason this type exists.</b> Without a claim, two players who start mining the same rock at
///         the same moment both finish and it yields twice — the interaction library's version of the
///         duplication bug the container algebra exists to prevent. The claim is released on
///         completion, on interruption and on the claimant walking away.
///     </para>
///     <para>
///         ⚠ <b>Per-player instancing means the <em>uses</em> are per player, not the node.</b> One
///         rock, one respawn timer, and everybody gets their own go at it — which is Guild Wars 2's
///         answer to node-stealing and is a policy on the definition rather than a second kind of node.
///     </para>
///     <para>
///         ⚠ <b>Respawn counts from the completion that emptied it</b>, not from the last attempt. A
///         timer restarted by a failed channel is a node that can be kept out of the world by somebody
///         standing next to it starting and cancelling.
///     </para>
/// </remarks>
public sealed class InteractionNode {
    readonly Dictionary<PlayerId, int> perPlayer = [];

    int shared;

    // Infinity until something empties it, so "has its timer run out" is one comparison and a node
    // that was never depleted can never be spuriously refilled.
    float respawnsAt = float.PositiveInfinity;

    /// <summary>Makes a node, full.</summary>
    /// <param name="interactable">What it is.</param>
    public InteractionNode(Interactable interactable) {
        ArgumentNullException.ThrowIfNull(interactable);

        Interactable = interactable;
        shared = interactable.Uses;
    }

    /// <summary>What it is.</summary>
    public Interactable Interactable { get; }

    /// <summary>Who is on it, or null.</summary>
    public Channel? Claimant { get; private set; }

    /// <summary>Whether anybody is on it.</summary>
    public bool IsClaimed => Claimant is not null;

    /// <summary>How many uses somebody has left of it.</summary>
    /// <param name="player">Who is asking.</param>
    /// <returns>How many, or −1 for a node that never runs out.</returns>
    public int RemainingFor(PlayerId player) {
        if (Interactable.IsUnlimited) {
            return -1;
        }

        return Interactable.Instancing == InteractionInstancing.PerPlayer
            ? perPlayer.TryGetValue(player, out var left) ? left : Interactable.Uses
            : shared;
    }

    /// <summary>Whether somebody may start, and why not.</summary>
    /// <param name="player">Who.</param>
    /// <param name="now">The clock.</param>
    /// <param name="context">What their requirements are evaluated against, or null to skip them.</param>
    /// <returns>The refusal, or <see cref="InteractionRefusal.None" />.</returns>
    public InteractionRefusal CanBegin(PlayerId player, float now, IRequirementContext? context = null) {
        if (Claimant is { } claim && claim.Player != player) {
            return InteractionRefusal.Claimed;
        }

        if (RemainingFor(player) == 0) {
            return now >= respawnsAt ? InteractionRefusal.None : InteractionRefusal.Depleted;
        }

        if (context is not null && !Interactable.Requirements.IsMetBy(context)) {
            return InteractionRefusal.Requirements;
        }

        return InteractionRefusal.None;
    }

    /// <summary>Starts using it.</summary>
    /// <param name="player">Who.</param>
    /// <param name="now">The clock.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <returns>The refusal, or <see cref="InteractionRefusal.None" />.</returns>
    public InteractionRefusal Begin(PlayerId player, float now, IRequirementContext? context = null) {
        var refusal = CanBegin(player, now, context);

        if (refusal != InteractionRefusal.None) {
            return refusal;
        }

        Respawn(now);

        Claimant = new(player, Interactable.Id, now, now + Interactable.ChannelSeconds);

        return InteractionRefusal.None;
    }

    /// <summary>Stops a channel without finishing it. Nothing is consumed.</summary>
    /// <param name="player">Who was on it, or <see cref="PlayerId.None" /> for the world.</param>
    /// <returns>Whether there was a channel to stop.</returns>
    public bool Interrupt(PlayerId player = default) {
        if (Claimant is not { } claim || (player.IsSome && claim.Player != player)) {
            return false;
        }

        Claimant = null;

        return true;
    }

    /// <summary>Whether something that just happened stops the channel.</summary>
    /// <param name="cause">What happened.</param>
    /// <returns>Whether it did.</returns>
    /// <remarks>
    ///     ⚠ <b>Interruption consumes nothing.</b> A node that were spent by an interrupted channel
    ///     would make being attacked while gathering cost the node as well as the time, and a player
    ///     who cancels by accident lose the rock.
    /// </remarks>
    public bool Disturb(InterruptOn cause) =>
        (Interactable.Interrupts & cause) != InterruptOn.Nothing && Interrupt();

    /// <summary>Finishes a channel, if it has run its course.</summary>
    /// <param name="player">Who.</param>
    /// <param name="now">The clock.</param>
    /// <param name="result">What it produced.</param>
    /// <param name="grantTo">Where the tags it grants go, or null.</param>
    /// <returns>The refusal, or <see cref="InteractionRefusal.None" />.</returns>
    public InteractionRefusal Complete(
        PlayerId player,
        float now,
        out InteractionResult result,
        GameplayTagSet? grantTo = null
    ) {
        result = default;

        if (Claimant is not { } claim || claim.Player != player) {
            return InteractionRefusal.NotChannelling;
        }

        if (now < claim.Finishes) {
            return InteractionRefusal.Unfinished;
        }

        Claimant = null;

        if (!Interactable.IsUnlimited) {
            if (Interactable.Instancing == InteractionInstancing.PerPlayer) {
                perPlayer[player] = RemainingFor(player) - 1;
            } else {
                shared--;
            }

            if (RemainingFor(player) <= 0 && Interactable.RespawnSeconds > 0f) {
                respawnsAt = now + Interactable.RespawnSeconds;
            }
        }

        if (grantTo is not null) {
            foreach (var tag in Interactable.Grants) {
                grantTo.Add(tag);
            }
        }

        // Seeded from the node, the player and when it happened, so the roll is reproducible from a
        // log — the same property the loot library's event id gives a drop.
        result = new(
            Interactable.Id,
            player,
            Interactable.Yields,
            GameplayRandom.Mix(Interactable.Id.Value) ^ GameplayRandom.Mix(player.Value ^ (ulong)BitConverter.SingleToUInt32Bits(claim.Started))
        );

        return InteractionRefusal.None;
    }

    /// <summary>Puts it back if its timer has run out, and drops a channel whose owner has gone.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>Whether anything was put back.</returns>
    public bool Respawn(float now) {
        if (now < respawnsAt) {
            return false;
        }

        shared = Interactable.Uses;
        perPlayer.Clear();
        respawnsAt = float.PositiveInfinity;

        return true;
    }

    /// <summary>Advances a channel and reports whether it was finished by the clock alone.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>Whether a channel is now ready to complete.</returns>
    public bool IsReady(float now) => Claimant is { } claim && now >= claim.Finishes;
}
