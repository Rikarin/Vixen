// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Sessions;

namespace Vixen.Net.Replication;

/// <summary>What one rule has to say about whether a player is told about an object.</summary>
public enum Interest : byte {
    /// <summary>Nothing to say. The next rule decides, or the chain's fallback does.</summary>
    /// <remarks>
    ///     <b>The answer most rules give most of the time, and the reason a chain works.</b> A scene
    ///     rule knows that an object in a level you have not loaded is hidden; it knows nothing about
    ///     whether one in a level you <i>have</i> loaded is close enough to matter. Saying so — rather
    ///     than voting "observed" and forcing every later rule to be able to overrule it — is what
    ///     lets the rules be written independently and put in any order.
    /// </remarks>
    Undecided = 0,

    /// <summary>Tell them about it, and stop asking.</summary>
    Observed = 1,

    /// <summary>Do not, and stop asking.</summary>
    Hidden = 2
}

/// <summary>One opinion about whether a player is told about an object.</summary>
/// <remarks>
///     Rules are asked in order and <b>the first definite answer wins</b>, which is what
///     [16](../../../docs/plan/16-networking.md)'s "scene scope → explicit overrides → distance grid"
///     ordering means: an explicit answer placed before the grid is one the grid cannot overrule, and
///     that is exactly what "explicit override" has to mean to be worth having.
/// </remarks>
public interface IInterestRule {
    /// <summary>Decides, or declines to.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">Who is being told.</param>
    /// <param name="entity">The object.</param>
    /// <returns>This rule's opinion.</returns>
    Interest Decide(World world, PlayerId player, Entity entity);
}

/// <summary>Where the entities a chain considers come from.</summary>
/// <remarks>
///     <para>
///         <b>Separate from the rules, and that separation is where the scaling is.</b> A rule filters
///         what it is given, so a chain of rules over every networked entity costs one sweep of the
///         world <i>per player</i> — ten thousand objects and two hundred players is two million
///         questions a tick, whatever the rules then say. A source that can answer "what is near this
///         player" without looking at everything is what turns that into one pass plus a small lookup
///         each, and it is the whole of the difference between twenty players and two hundred.
///     </para>
///     <para>
///         Which is why the distance grid is a source rather than a rule. It reads as a filter — "is
///         this within range" — and writing it as one would produce something that passes every test
///         and scales like the thing it was meant to replace.
///     </para>
/// </remarks>
public interface IInterestSource {
    /// <summary>Fills <paramref name="into" /> with the entities worth asking about.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">Who is being told.</param>
    /// <param name="into">Where to put them. Cleared by the caller.</param>
    void Candidates(World world, PlayerId player, List<Entity> into);
}

/// <summary>Every networked entity, which is what a chain considers unless told otherwise.</summary>
public sealed class AllNetworkedSource : IInterestSource {
    static readonly QueryDescription Networked = new QueryDescription().RequireAll([ComponentType<NetworkId>.Id]);

    /// <inheritdoc />
    public void Candidates(World world, PlayerId player, List<Entity> into) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(into);

        foreach (var chunk in world.Chunks(Networked)) {
            into.AddRange(chunk.Entities);
        }
    }
}

/// <summary>Resolvers composed: a source of candidates and rules that decide about them.</summary>
/// <remarks>
///     <para>
///         [16](../../../docs/plan/16-networking.md) asks for composable resolvers "in evaluation
///         order: scene scope → explicit visibility overrides → distance grid → LOD rate reduction",
///         and this is the first three of those. <b>The fourth is deliberately not here</b>, and the
///         reason is worth stating because implementing the sentence as written produces a bug that
///         looks like the feature working.
///     </para>
///     <para>
///         <b>Rate reduction is not an interest decision.</b> Leaving an object out of the observed
///         set does not mean "skip it this tick" — <c>ReplicationServer</c> treats an object that has
///         left the set as one the client should <i>drop</i>, because leaving interest and being
///         destroyed are deliberately the same mechanism. So an LOD written as a rule would despawn
///         and respawn every distant object on every tick it skipped. Rate belongs to the record
///         writer, where skipping is already what the bandwidth budget does; see
///         <see cref="IReplicationRate" />.
///     </para>
///     <para>
///         <b>The fallback is <see cref="Interest.Observed" /></b>, so a chain with no rules is the
///         behaviour a new project already had. Adding a rule can then only ever <i>hide</i> things,
///         which is the direction in which mistakes are visible: an object that should not be there is
///         noticed, and one that silently is not is debugged.
///     </para>
/// </remarks>
public sealed class InterestChain : IInterestResolver {
    readonly List<Entity> candidates = [];

    /// <summary>The rules, asked in order until one has an opinion.</summary>
    public IList<IInterestRule> Rules { get; } = [];

    /// <summary>Where candidates come from. Every networked entity, unless replaced.</summary>
    public IInterestSource Source { get; set; } = new AllNetworkedSource();

    /// <summary>What to do about an object no rule had an opinion on.</summary>
    public Interest Fallback { get; set; } = Interest.Observed;

    /// <summary>How many candidates the last resolve considered.</summary>
    public int ConsideredCount { get; private set; }

    /// <summary>How many of them were hidden.</summary>
    /// <remarks>
    ///     The pair to watch. Considered says what the source cost and hidden says what the rules
    ///     saved — and a chain whose hidden count is near zero is one whose rules are doing nothing
    ///     for the time they take, while a source whose considered count is the whole world is a grid
    ///     that has not been wired up.
    /// </remarks>
    public int HiddenCount { get; private set; }

    /// <inheritdoc />
    public void Resolve(World world, PlayerId player, List<Entity> observed) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(observed);

        candidates.Clear();
        Source.Candidates(world, player, candidates);

        ConsideredCount = candidates.Count;
        HiddenCount = 0;

        foreach (var entity in candidates) {
            if (Decide(world, player, entity) == Interest.Observed) {
                observed.Add(entity);
            } else {
                HiddenCount++;
            }
        }
    }

    /// <summary>What the chain says about one object, for a test or a diagnostic.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">Who is being told.</param>
    /// <param name="entity">The object.</param>
    /// <returns>
    ///     <see cref="Interest.Observed" /> or <see cref="Interest.Hidden" />, never
    ///     <see cref="Interest.Undecided" /> — the fallback is what makes that true.
    /// </returns>
    public Interest Decide(World world, PlayerId player, Entity entity) {
        foreach (var rule in Rules) {
            var verdict = rule.Decide(world, player, entity);

            if (verdict != Interest.Undecided) {
                return verdict;
            }
        }

        return Fallback;
    }
}

/// <summary>Visibility a game has decided by hand, which nothing after it may argue with.</summary>
/// <remarks>
///     <para>
///         The escape hatch every interest scheme needs and few have: a spectator who should see a
///         player across the map, a quest marker that stays visible at any range, an object revealed
///         by a scripted event, a teammate shown through walls. Placed <b>before</b> the distance grid
///         in the chain, which is the whole point — an override the grid could overrule would not be
///         one.
///     </para>
///     <para>
///         Keyed by <see cref="NetworkId" /> rather than by <c>Entity</c>, because an override is a
///         decision about the object rather than about the row it currently occupies, and because it
///         is the id a game has in hand when a rule fires.
///     </para>
/// </remarks>
public sealed class ExplicitInterestRule : IInterestRule {
    readonly Dictionary<uint, Dictionary<uint, Interest>> byPlayer = [];

    /// <summary>How many players have an override of any kind.</summary>
    public int PlayerCount => byPlayer.Count;

    /// <summary>Shows an object to a player whatever anything after this would say.</summary>
    /// <param name="player">Who.</param>
    /// <param name="id">What.</param>
    public void Show(PlayerId player, NetworkId id) => Set(player, id, Interest.Observed);

    /// <summary>Hides one from them whatever anything after this would say.</summary>
    /// <param name="player">Who.</param>
    /// <param name="id">What.</param>
    public void Hide(PlayerId player, NetworkId id) => Set(player, id, Interest.Hidden);

    /// <summary>Takes an override off, leaving the rest of the chain to decide.</summary>
    /// <param name="player">Who.</param>
    /// <param name="id">What.</param>
    /// <returns>Whether there was one.</returns>
    public bool Clear(PlayerId player, NetworkId id) =>
        byPlayer.TryGetValue(player.Value, out var overrides) && overrides.Remove(id.Value);

    /// <summary>Forgets a player who has gone.</summary>
    /// <param name="player">Who.</param>
    public void Forget(PlayerId player) => byPlayer.Remove(player.Value);

    /// <summary>Forgets an object that has been destroyed, for every player.</summary>
    /// <param name="id">What.</param>
    /// <remarks>
    ///     Ids are not reused within a session, so a leaked override is memory rather than a wrong
    ///     answer — but a server that runs for a week is one where memory is the failure.
    /// </remarks>
    public void Forget(NetworkId id) {
        foreach (var overrides in byPlayer.Values) {
            overrides.Remove(id.Value);
        }
    }

    /// <inheritdoc />
    public Interest Decide(World world, PlayerId player, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        if (byPlayer.Count == 0 || !world.TryGet<NetworkId>(entity, out var id)) {
            return Interest.Undecided;
        }

        return byPlayer.TryGetValue(player.Value, out var overrides)
            && overrides.TryGetValue(id.Value, out var verdict)
                ? verdict
                : Interest.Undecided;
    }

    void Set(PlayerId player, NetworkId id, Interest verdict) {
        if (!byPlayer.TryGetValue(player.Value, out var overrides)) {
            overrides = [];
            byPlayer[player.Value] = overrides;
        }

        overrides[id.Value] = verdict;
    }
}
