// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>Something happened: a kill, a pickup, a discovery, a purchase.</summary>
/// <remarks>
///     <para>
///         <b>The other half of doc 28's dependency spine.</b> The spine says that where two features
///         genuinely need to meet — loot dropping from a raid encounter, a quest counting a kill — they
///         meet <em>"through tags and events rather than through a reference"</em>. Tags were built in
///         G0 and events were not, which left the sentence half true: a quest library that had to be
///         told about kills by name would need a reference to combat, and the horizontal edge the spine
///         forbids would be back.
///     </para>
///     <para>
///         <b>A verb, a subject and a place, and nothing about who is watching.</b> What makes the
///         seam work is that a poster names what happened in its own vocabulary and never names an
///         audience. Combat posts <c>Event.Kill</c> with the victim's tags; whether that advances a
///         quest, an achievement, a dynamic event or nothing at all is not combat's business and it
///         costs combat nothing when the answer is nothing.
///     </para>
///     <para>
///         ⚠ <b><see cref="Tags" /> is borrowed for the duration of the dispatch and must not be
///         kept.</b> It is the subject's own live set — the victim's tags, the item's tags — passed by
///         reference so that filtering a thousand kills allocates nothing. A subscriber that stored it
///         would be holding a set that goes on changing, and for a corpse, one that is about to be
///         recycled.
///     </para>
/// </remarks>
/// <param name="Verb">What happened — <c>Event.Kill</c>. The one field every filter tests first.</param>
/// <param name="Subject">What it happened to or with: the creature, the item, the recipe, the currency.</param>
/// <param name="Scene">Where, as a map's address. <see cref="DefId.None" /> for nowhere in particular.</param>
/// <param name="Amount">How many or how much. One kill, thirty ore, five hundred gold.</param>
/// <param name="Instigator">Who caused it, in whatever numbering the game gives players.</param>
/// <param name="Tags">The subject's tags, borrowed. Null when it has none.</param>
public readonly record struct GameplayEvent(
    GameplayTag Verb,
    DefId Subject = default,
    DefId Scene = default,
    int Amount = 1,
    ulong Instigator = 0,
    GameplayTagSet? Tags = null
);

/// <summary>Which events a subscriber wants: a verb, optionally a subject, a place and a tag query.</summary>
/// <remarks>
///     <para>
///         <b>This is what doc 28 means by "costs nothing when nothing dies".</b> "Kill ten undead in
///         Queensdale" is this struct — a verb range, a scene and a tag query — and matching it is a
///         handful of integer comparisons against an event that was going to be posted anyway. There is
///         no polling and no per-frame work for an objective nobody is progressing.
///     </para>
///     <para>
///         ⚠ <b>An empty verb range matches nothing, and that is the point.</b> A filter is built from
///         a name a designer wrote, and <see cref="GameplayTagTable.RangeOf(string)" /> answers an
///         empty range for a name the content does not have. The other reading — empty means
///         unfiltered — turns one typo in one objective into an objective that completes on the first
///         thing that happens anywhere. Wanting every verb is <see cref="EveryVerb" />, which is a
///         thing a caller has to write down.
///     </para>
/// </remarks>
/// <param name="Verb">The verb prefix. <see cref="EveryVerb" /> for any.</param>
/// <param name="Subject">The exact subject, or <see cref="DefId.None" /> for any.</param>
/// <param name="Scene">The map, or <see cref="DefId.None" /> for anywhere.</param>
/// <param name="Tags">A query over the subject's tags, or null for any.</param>
public readonly record struct GameplayEventFilter(
    GameplayTagRange Verb,
    DefId Subject = default,
    DefId Scene = default,
    GameplayTagQuery? Tags = null
) {
    /// <summary>The verb range that matches any verb at all. Written down, never defaulted to.</summary>
    public static GameplayTagRange EveryVerb => new(1u, uint.MaxValue);

    /// <summary>The filter that matches everything. What a logger or a recorder subscribes with.</summary>
    public static GameplayEventFilter Everything { get; } = new(EveryVerb);

    /// <summary>Whether this filter can ever match anything.</summary>
    /// <remarks>
    ///     False for a filter whose verb did not resolve, which is what a caller reports as a content
    ///     problem rather than leaving as an objective that silently never advances.
    /// </remarks>
    public bool IsSome => Verb.IsSome;

    /// <summary>Whether an event matches.</summary>
    /// <param name="gameplayEvent">The event.</param>
    /// <returns>Whether it does.</returns>
    public bool Matches(in GameplayEvent gameplayEvent) =>
        Verb.Contains(gameplayEvent.Verb)
        && (!Subject.IsSome || Subject == gameplayEvent.Subject)
        && (!Scene.IsSome || Scene == gameplayEvent.Scene)
        && (Tags is null || Tags.Matches(gameplayEvent.Tags));
}
