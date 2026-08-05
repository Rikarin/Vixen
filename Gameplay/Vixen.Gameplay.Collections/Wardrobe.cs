// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Collections;

/// <summary>One character's presentation: what their gear is shown as, what is hidden, and their title.</summary>
/// <remarks>
///     <para>
///         <b>Per character, where the collection behind it is per account.</b> That split is the one
///         thing doc 28's paragraph does not say and every game needs: unlocks are account-wide, but
///         two alts have different transmog and different titles out of the same wardrobe.
///     </para>
///     <para>
///         ⚠ <b>An override to something no longer unlocked resolves to the real item, never to
///         nothing.</b> An appearance can be taken back — a refund, a season ending, a patch — and the
///         character wearing it must not turn invisible. That is why <see cref="Resolve" /> checks the
///         unlock every single time instead of being told when one goes away: there is no
///         notification to miss.
///     </para>
///     <para>
///         ⚠ <b>Hiding a slot and overriding it are separate, and hiding wins.</b> "No helmet" and "a
///         different helmet" are different wishes. A game that models hiding as an override to nothing
///         loses the player's chosen look the moment they tick the box, and cannot give it back when
///         they untick it.
///     </para>
///     <para>
///         <b>Doc 28 calls transmog "one field and one visual-resolution rule".</b> The field is per
///         slot and lives here rather than on the item, because a sixteen-byte <c>ItemInstance</c>
///         cannot hold variable-size per-copy data — which is the amendment doc 28 already records
///         under Items, alongside the gem list and the custom name.
///     </para>
/// </remarks>
public sealed class Wardrobe {
    readonly Dictionary<GameplayTag, DefId> overrides = [];
    readonly HashSet<GameplayTag> hidden = [];

    /// <summary>Makes an empty wardrobe over a collection.</summary>
    /// <param name="record">The account's collection.</param>
    public Wardrobe(CollectionRecord record) {
        ArgumentNullException.ThrowIfNull(record);

        Record = record;
    }

    /// <summary>The account's collection.</summary>
    public CollectionRecord Record { get; }

    /// <summary>How many slots are overridden.</summary>
    public int Count => overrides.Count;

    /// <summary>How many are hidden.</summary>
    public int Hiding => hidden.Count;

    /// <summary>What is written after their name, or <see cref="DefId.None" />.</summary>
    public DefId Title { get; private set; }

    /// <summary>Shows one slot as something else.</summary>
    /// <param name="appearance">What to show. Must be an appearance, and unlocked.</param>
    /// <returns>Whether it took.</returns>
    public bool Show(Collectible appearance) {
        ArgumentNullException.ThrowIfNull(appearance);

        if (appearance.Kind != CollectibleKind.Appearance
            || !appearance.Slot.IsSome
            || !Record.IsUnlocked(appearance.Id)) {
            return false;
        }

        overrides[appearance.Slot] = appearance.Id;

        return true;
    }

    /// <summary>Puts a slot's override back with no checks at all.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="appearance">What it is shown as. <see cref="DefId.None" /> takes the override off.</param>
    /// <returns>Whether anything moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unchecked, and here that is not merely safe but <em>right</em>.</b>
    ///         <see cref="Resolve" /> already re-checks the unlock every time it draws, so an
    ///         appearance a patch has taken back falls through to the worn item on its own. Checking
    ///         again at load would throw the player's choice away permanently, where leaving it
    ///         stored means a re-granted appearance simply starts showing again.
    ///     </para>
    ///     <para>
    ///         It is <c>Guild.Seat</c>'s and <c>HousePlot.Assign</c>'s seam otherwise:
    ///         <see cref="Show" /> is what a player does and it wants a compiled
    ///         <see cref="Collectible" />, which is content this build may no longer have.
    ///     </para>
    /// </remarks>
    public bool Seat(GameplayTag slot, DefId appearance) {
        if (!slot.IsSome) {
            return false;
        }

        if (!appearance.IsSome) {
            return overrides.Remove(slot);
        }

        if (overrides.TryGetValue(slot, out var already) && already == appearance) {
            return false;
        }

        overrides[slot] = appearance;

        return true;
    }

    /// <summary>Puts the worn title back without asking whether they still have it.</summary>
    /// <param name="title">Which, or <see cref="DefId.None" /> for none.</param>
    /// <remarks>⚠ <see cref="Worn" /> re-checks it every time, which is why this does not have to.</remarks>
    public void SeatTitle(DefId title) => Title = title;

    /// <summary>Stops showing one slot as something else.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>Whether there was an override.</returns>
    public bool Restore(GameplayTag slot) => overrides.Remove(slot);

    /// <summary>What a slot is overridden with, or <see cref="DefId.None" />.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>The appearance.</returns>
    public DefId OverrideOf(GameplayTag slot) => overrides.GetValueOrDefault(slot);

    /// <summary>Hides or unhides a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="hide">Whether to hide it.</param>
    /// <returns>Whether it changed.</returns>
    public bool Hide(GameplayTag slot, bool hide = true) {
        if (!slot.IsSome) {
            return false;
        }

        return hide ? hidden.Add(slot) : hidden.Remove(slot);
    }

    /// <summary>Whether a slot is hidden.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>Whether it is.</returns>
    public bool IsHidden(GameplayTag slot) => hidden.Contains(slot);

    /// <summary>Works out what to draw in a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="worn">What is actually equipped there. <see cref="DefId.None" /> for nothing.</param>
    /// <returns>What to draw, or <see cref="DefId.None" /> for nothing at all.</returns>
    /// <remarks>
    ///     ⚠ <b>The whole rule, in three lines, and the order matters.</b> Hidden beats overridden so
    ///     that unticking the box gives the chosen look back; an override that is no longer unlocked
    ///     falls through to the worn item rather than to nothing, so nobody ends up invisible.
    /// </remarks>
    public DefId Resolve(GameplayTag slot, DefId worn) {
        if (hidden.Contains(slot)) {
            return DefId.None;
        }

        if (overrides.TryGetValue(slot, out var appearance) && Record.IsUnlocked(appearance)) {
            return appearance;
        }

        return worn;
    }

    /// <summary>Puts a title after their name.</summary>
    /// <param name="title">Which, or null to take it off.</param>
    /// <returns>Whether it took.</returns>
    public bool Wear(Collectible? title) {
        if (title is null) {
            Title = DefId.None;

            return true;
        }

        if (title.Kind != CollectibleKind.Title || !Record.IsUnlocked(title.Id)) {
            return false;
        }

        Title = title.Id;

        return true;
    }

    /// <summary>What is written after their name, checked against the collection as it is now.</summary>
    /// <returns>The title, or <see cref="DefId.None" /> for one they no longer have.</returns>
    /// <remarks>
    ///     Resolved rather than stored, for the same reason <see cref="Resolve" /> is: a title taken
    ///     back must stop showing, and there is no notification to hang that on.
    /// </remarks>
    public DefId Worn() => Title.IsSome && Record.IsUnlocked(Title) ? Title : DefId.None;

    /// <summary>Every slot that is overridden, in tag order.</summary>
    /// <returns>The slot and what it is shown as.</returns>
    public IEnumerable<KeyValuePair<GameplayTag, DefId>> Overrides => overrides.OrderBy(pair => pair.Key);

    /// <summary>Every hidden slot, in tag order.</summary>
    public IEnumerable<GameplayTag> Hidden => hidden.Order();
}
