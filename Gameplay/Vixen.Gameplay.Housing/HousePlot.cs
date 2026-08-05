// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Gameplay.Housing;

/// <summary>One thing that is down in a house.</summary>
/// <remarks>
///     ⚠ <b>A position and a facing, not a transform.</b> Furniture turns about the up axis and
///     nothing else — which is what every housing feature that shipped does, because free three-axis
///     rotation is unauthorable with a mouse and doubles a row that ten thousand houses each have
///     hundreds of. A prop that hangs at an angle is scene content, not a player's decoration.
/// </remarks>
/// <param name="Id">Which placement it is, unique within its plot and never reused.</param>
/// <param name="Furniture">What it is.</param>
/// <param name="Surface">What the caller found it standing on.</param>
/// <param name="Position">Where, snapped.</param>
/// <param name="Yaw">Which way it faces, in radians, snapped.</param>
/// <param name="By">Who put it there. Worth having in a guild hall.</param>
public readonly record struct Placement(
    int Id,
    DefId Furniture,
    HouseSurface Surface,
    Vector3 Position,
    float Yaw,
    PlayerId By
);

/// <summary>One house: who may do what in it, and what is down.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here takes a clock, and that is what makes doc 28's claim true.</b> Housing is
///         affordable because <em>"ten thousand houses are ten thousand rows, not ten thousand
///         processes"</em> — and a plot only survives hibernation if it has nothing that has to keep
///         running. There is no timer, no decay, no growth and no tick in this type; every question it
///         answers is a function of what is stored. Anything that has to age — a plant that wilts, a
///         rent clock — is the caller's, and is a timestamp compared on load rather than a process.
///     </para>
///     <para>
///         ⚠ <b>It reports what must happen and never holds anything.</b> Removing a piece hands back
///         the <see cref="DefId" /> that came out; whether it goes to a bag, to the mail or nowhere is
///         the caller's, exactly as with a quest's rewards and an inventory's transfer order.
///     </para>
///     <para>
///         ⚠ <b>A ban beats everything, including the owner's own openness.</b> Modelling a ban as the
///         bottom rung of the tier ladder makes it useless on a house open to the public — everybody is
///         on the bottom rung there, and the bottom rung is admitted.
///     </para>
///     <para>
///         ⚠ <b>An owner cannot be banned from, or demoted in, their own house.</b> The alternative is
///         a bug or a mis-click that locks somebody out of a house only a support ticket can reopen.
///     </para>
/// </remarks>
public sealed class HousePlot {
    readonly List<Placement> placements = [];
    readonly Dictionary<DefId, int> counts = [];
    readonly Dictionary<PlayerId, HouseTier> granted = [];
    readonly HashSet<PlayerId> banned = [];

    int next = 1;

    /// <summary>Makes an empty house.</summary>
    /// <param name="library">Where the furniture comes from.</param>
    /// <param name="plot">What kind it is.</param>
    /// <param name="owner">Whose it is.</param>
    public HousePlot(HousingLibrary library, Plot plot, HouseOwner owner) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(plot);

        Library = library;
        Plot = plot;
        Owner = owner;
        Openness = plot.Openness;
    }

    /// <summary>Where the furniture comes from.</summary>
    public HousingLibrary Library { get; }

    /// <summary>What kind it is.</summary>
    public Plot Plot { get; }

    /// <summary>Whose it is.</summary>
    public HouseOwner Owner { get; }

    /// <summary>What a stranger is treated as.</summary>
    public HouseTier Openness { get; private set; }

    /// <summary>What is down, in the order it was placed.</summary>
    public IReadOnlyList<Placement> Placements => placements;

    /// <summary>How much of the budget is used.</summary>
    public int Spent { get; private set; }

    /// <summary>How much is left. Negative for a plot a patch has made too small for its contents.</summary>
    public int Free => Plot.Budget - Spent;

    /// <summary>How many people have been given standing.</summary>
    public int Granted => granted.Count;

    /// <summary>Who is barred, in id order.</summary>
    public IEnumerable<PlayerId> Banned => banned.Order();

    /// <summary>How many times it has changed.</summary>
    /// <remarks>
    ///     <b>What a single-writer grain wants instead of a change event.</b> A subscription is a live
    ///     object and a hibernating plot has nowhere to keep one; a counter says "this differs from
    ///     what was saved" and doubles as the version an optimistic write checks.
    /// </remarks>
    public uint Revision { get; private set; }

    /// <summary>What standing somebody has.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their tier.</returns>
    /// <remarks>
    ///     A guild plot has no implicit owner, so its answer comes from the explicit grants alone —
    ///     which is where a guild's rank matrix lands after whoever owns the guild has applied it.
    /// </remarks>
    public HouseTier TierOf(PlayerId player) {
        if (!Owner.IsGuild && player.IsSome && player == Owner.Player) {
            return HouseTier.Owner;
        }

        return granted.TryGetValue(player, out var tier) ? tier : Openness;
    }

    /// <summary>Whether somebody is barred.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they are.</returns>
    public bool IsBanned(PlayerId player) => banned.Contains(player);

    /// <summary>Whether somebody may do something.</summary>
    /// <param name="player">Who.</param>
    /// <param name="action">What.</param>
    /// <returns>Whether they may.</returns>
    public bool Can(PlayerId player, HouseAction action) =>
        !IsBanned(player) && TierOf(player) >= Plot.Needs(action);

    /// <summary>Opens or closes the house to strangers.</summary>
    /// <param name="by">Who is changing it.</param>
    /// <param name="openness">What a stranger is treated as.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    public HousingRefusal SetOpenness(PlayerId by, HouseTier openness) {
        var refusal = MayAdminister(by);

        if (refusal != HousingRefusal.None) {
            return refusal;
        }

        // Nobody may hand out standing they do not have — including to the whole world at once.
        if (openness > TierOf(by)) {
            return HousingRefusal.Forbidden;
        }

        Openness = openness;
        Revision++;

        return HousingRefusal.None;
    }

    /// <summary>Gives somebody standing, or takes it away with <see cref="HouseTier.None" />.</summary>
    /// <param name="by">Who is granting.</param>
    /// <param name="player">Who to.</param>
    /// <param name="tier">What standing.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Nobody may grant standing at or above their own, or change somebody who already has
    ///     it.</b> Without the first, a resident promotes a friend to owner and the house has two
    ///     owners, one of whom can evict the other; without the second, the promoted friend is demoted
    ///     by the person they outrank.
    /// </remarks>
    public HousingRefusal Grant(PlayerId by, PlayerId player, HouseTier tier) {
        if (!player.IsSome) {
            return HousingRefusal.Unknown;
        }

        var refusal = MayAdminister(by);

        if (refusal != HousingRefusal.None) {
            return refusal;
        }

        if (!Owner.IsGuild && player == Owner.Player) {
            return HousingRefusal.Forbidden;
        }

        var mine = TierOf(by);

        if (tier >= mine || TierOf(player) >= mine) {
            return HousingRefusal.Forbidden;
        }

        if (tier == HouseTier.None) {
            granted.Remove(player);
        } else {
            granted[player] = tier;
        }

        Revision++;

        return HousingRefusal.None;
    }

    /// <summary>Bars somebody.</summary>
    /// <param name="by">Who is barring.</param>
    /// <param name="player">Who from.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    public HousingRefusal Ban(PlayerId by, PlayerId player) {
        if (!player.IsSome) {
            return HousingRefusal.Unknown;
        }

        var refusal = MayAdminister(by);

        if (refusal != HousingRefusal.None) {
            return refusal;
        }

        if (!Owner.IsGuild && player == Owner.Player) {
            return HousingRefusal.Forbidden;
        }

        if (TierOf(player) >= TierOf(by)) {
            return HousingRefusal.Forbidden;
        }

        // A ban drops whatever standing they had, so unbanning does not silently restore it.
        granted.Remove(player);

        if (banned.Add(player)) {
            Revision++;
        }

        return HousingRefusal.None;
    }

    /// <summary>Lets somebody back in, at no standing.</summary>
    /// <param name="by">Who is unbarring.</param>
    /// <param name="player">Who.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    public HousingRefusal Unban(PlayerId by, PlayerId player) {
        var refusal = MayAdminister(by);

        if (refusal != HousingRefusal.None) {
            return refusal;
        }

        if (banned.Remove(player)) {
            Revision++;
        }

        return HousingRefusal.None;
    }

    /// <summary>Sets somebody's standing with no checks at all.</summary>
    /// <param name="player">Who.</param>
    /// <param name="tier">What standing, or <see cref="HouseTier.None" /> to take it away.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a player action, and the reason a guild hall works.</b> <see cref="Grant" />
    ///         asks whether the granter outranks the grantee, and on a guild plot nobody outranks
    ///         anybody, because <see cref="TierOf" /> has no implicit owner to start from. What lands
    ///         standing there is the guild's own rank matrix, applied wholesale by whoever holds the
    ///         guild — an authority above this library, not a player standing in the hall.
    ///     </para>
    ///     <para>
    ///         It is also what loads a save. Replaying <see cref="Grant" /> would re-derive the rules
    ///         against today's content and quietly drop standing a patch has since made illegal.
    ///     </para>
    /// </remarks>
    public void Assign(PlayerId player, HouseTier tier) {
        if (!player.IsSome) {
            return;
        }

        if (tier == HouseTier.None) {
            granted.Remove(player);
        } else {
            granted[player] = tier;
        }

        Revision++;
    }

    /// <summary>Bars or unbars somebody with no checks at all. The authority's, like <see cref="Assign" />.</summary>
    /// <param name="player">Who.</param>
    /// <param name="banned">Whether they are barred.</param>
    public void Bar(PlayerId player, bool banned) {
        if (!player.IsSome) {
            return;
        }

        if (banned ? this.banned.Add(player) : this.banned.Remove(player)) {
            Revision++;
        }
    }

    /// <summary>Sets what a stranger is treated as with no checks at all. The authority's.</summary>
    /// <param name="openness">The tier.</param>
    public void Open(HouseTier openness) {
        Openness = openness;
        Revision++;
    }

    /// <summary>How many of something is down.</summary>
    /// <param name="furniture">Which piece.</param>
    /// <returns>How many.</returns>
    public int CountOf(DefId furniture) => counts.GetValueOrDefault(furniture);

    /// <summary>Finds a placement.</summary>
    /// <param name="id">Its id within the plot.</param>
    /// <returns>It, or null.</returns>
    public Placement? Find(int id) {
        var index = IndexOf(id);

        return index < 0 ? null : placements[index];
    }

    /// <summary>Whether a piece could be put down, and why not.</summary>
    /// <param name="by">Who is placing it.</param>
    /// <param name="furniture">What.</param>
    /// <param name="surface">What the caller found it standing on.</param>
    /// <param name="context">What its requirements are evaluated against, or null to skip them.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    public HousingRefusal CanPlace(
        PlayerId by,
        Furniture furniture,
        HouseSurface surface,
        IRequirementContext? context = null
    ) {
        ArgumentNullException.ThrowIfNull(furniture);

        if (!Can(by, HouseAction.Decorate)) {
            return IsBanned(by) ? HousingRefusal.Banned : HousingRefusal.Forbidden;
        }

        // Exactly one surface, and one both the piece and the plot allow. A caller that passes two
        // is guessing, and a guess here is a chandelier on the lawn.
        if (!IsOneSurface(surface)
            || (furniture.Surfaces & surface) == HouseSurface.Nothing
            || (Plot.Surfaces & surface) == HouseSurface.Nothing) {
            return HousingRefusal.WrongSurface;
        }

        if (furniture.Cost > Free) {
            return HousingRefusal.OutOfBudget;
        }

        if (furniture.MaximumPerPlot > 0 && CountOf(furniture.Id) >= furniture.MaximumPerPlot) {
            return HousingRefusal.TooMany;
        }

        if (context is not null && !furniture.Requirements.IsMetBy(context)) {
            return HousingRefusal.Requirements;
        }

        return HousingRefusal.None;
    }

    /// <summary>Puts a piece down.</summary>
    /// <param name="by">Who.</param>
    /// <param name="furniture">What.</param>
    /// <param name="surface">What the caller found it standing on.</param>
    /// <param name="position">Where they pointed. Snapped before it is stored.</param>
    /// <param name="yaw">Which way they turned it, in radians. Snapped before it is stored.</param>
    /// <param name="placed">Where it actually went.</param>
    /// <param name="context">What its requirements are evaluated against.</param>
    /// <param name="grantTo">Where the tag having it down grants goes, or null.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Snapped before the checks and stored snapped, never after.</b> A plot that validated
    ///     the raw point and stored the rounded one is a house whose furniture moves a few centimetres
    ///     every time somebody logs in — and a client that previewed the raw point shows a placement
    ///     that is not where the piece lands.
    /// </remarks>
    public HousingRefusal Place(
        PlayerId by,
        Furniture furniture,
        HouseSurface surface,
        Vector3 position,
        float yaw,
        out Placement placed,
        IRequirementContext? context = null,
        GameplayTagSet? grantTo = null
    ) {
        placed = default;

        var refusal = CanPlace(by, furniture, surface, context);

        if (refusal != HousingRefusal.None) {
            return refusal;
        }

        placed = new(next++, furniture.Id, surface, Plot.Snap(position), Plot.SnapYaw(yaw), by);

        placements.Add(placed);
        counts[furniture.Id] = CountOf(furniture.Id) + 1;
        Spent += furniture.Cost;
        Revision++;

        if (grantTo is not null && furniture.Tag.IsSome) {
            grantTo.Add(furniture.Tag);
        }

        return HousingRefusal.None;
    }

    /// <summary>Moves a piece that is already down.</summary>
    /// <param name="by">Who.</param>
    /// <param name="id">Which placement.</param>
    /// <param name="surface">What the caller found it standing on now.</param>
    /// <param name="position">Where they pointed.</param>
    /// <param name="yaw">Which way they turned it, in radians.</param>
    /// <param name="moved">Where it actually went.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Not a remove followed by a place.</b> A move keeps the placement's id, does not touch
    ///     the budget, and cannot fail on a full house — a plot furnished to the last point would
    ///     otherwise be a plot in which nothing can be nudged.
    /// </remarks>
    public HousingRefusal Move(
        PlayerId by,
        int id,
        HouseSurface surface,
        Vector3 position,
        float yaw,
        out Placement moved
    ) {
        moved = default;

        if (!Can(by, HouseAction.Decorate)) {
            return IsBanned(by) ? HousingRefusal.Banned : HousingRefusal.Forbidden;
        }

        var index = IndexOf(id);

        if (index < 0) {
            return HousingRefusal.Unknown;
        }

        var placement = placements[index];
        var furniture = Library.FindFurniture(placement.Furniture);

        if (!IsOneSurface(surface)
            || (Plot.Surfaces & surface) == HouseSurface.Nothing
            || (furniture is not null && (furniture.Surfaces & surface) == HouseSurface.Nothing)) {
            return HousingRefusal.WrongSurface;
        }

        moved = placement with {
            Surface = surface, Position = Plot.Snap(position), Yaw = Plot.SnapYaw(yaw)
        };

        placements[index] = moved;
        Revision++;

        return HousingRefusal.None;
    }

    /// <summary>Picks a piece up.</summary>
    /// <param name="by">Who.</param>
    /// <param name="id">Which placement.</param>
    /// <param name="returned">What came out, for the caller to put somewhere.</param>
    /// <param name="revokeFrom">Where the tag having it down granted is taken back from, or null.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>One grant out for one grant in, and the counting is not this library's.</b> A house
    ///     with two forges must not lose its forge tag while one is still standing — but
    ///     <see cref="GameplayTagSet" /> is already a counted set, so two placements hold the tag
    ///     twice and the first removal drops the count to one rather than clearing it. The
    ///     hand-written version of that rule ("revoke only on the last one") revokes once for two
    ///     grants and leaks the tag for the rest of the session, which is what the test found.
    /// </remarks>
    public HousingRefusal Remove(PlayerId by, int id, out DefId returned, GameplayTagSet? revokeFrom = null) {
        returned = DefId.None;

        if (!Can(by, HouseAction.Decorate)) {
            return IsBanned(by) ? HousingRefusal.Banned : HousingRefusal.Forbidden;
        }

        var index = IndexOf(id);

        if (index < 0) {
            return HousingRefusal.Unknown;
        }

        var placement = placements[index];
        var furniture = Library.FindFurniture(placement.Furniture);

        placements.RemoveAt(index);
        returned = placement.Furniture;

        var left = CountOf(returned) - 1;

        if (left <= 0) {
            counts.Remove(returned);
        } else {
            counts[returned] = left;
        }

        Spent -= furniture?.Cost ?? 0;
        Revision++;

        if (revokeFrom is not null && furniture is { Tag.IsSome: true }) {
            revokeFrom.Remove(furniture.Tag);
        }

        return HousingRefusal.None;
    }

    /// <summary>Picks everything up.</summary>
    /// <param name="by">Who.</param>
    /// <param name="returned">What came out, one entry per placement.</param>
    /// <param name="revokeFrom">Where the tags being furnished granted are taken back from, or null.</param>
    /// <returns>The refusal, or <see cref="HousingRefusal.None" />.</returns>
    public HousingRefusal Clear(PlayerId by, ICollection<DefId> returned, GameplayTagSet? revokeFrom = null) {
        ArgumentNullException.ThrowIfNull(returned);

        if (!Can(by, HouseAction.Decorate)) {
            return IsBanned(by) ? HousingRefusal.Banned : HousingRefusal.Forbidden;
        }

        foreach (var placement in placements) {
            returned.Add(placement.Furniture);

            if (revokeFrom is not null && Library.FindFurniture(placement.Furniture) is { Tag.IsSome: true } piece) {
                revokeFrom.Remove(piece.Tag);
            }
        }

        placements.Clear();
        counts.Clear();
        Spent = 0;
        Revision++;

        return HousingRefusal.None;
    }

    /// <summary>What is spent, recomputed from what is down.</summary>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     For a save that is being audited, and for the property test that says the running total and
    ///     the recomputed one never disagree — which is the housing version of the inventory library's
    ///     conservation oracle.
    /// </remarks>
    public int Recount() {
        var total = 0;

        foreach (var placement in placements) {
            total += Library.FindFurniture(placement.Furniture)?.Cost ?? 0;
        }

        return total;
    }

    /// <summary>Puts a saved layout back, as it was, with no checks.</summary>
    /// <param name="saved">What was stored.</param>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <see cref="Place" /> in a loop.</b> A layout that was legal when it
    ///     was made must load even after a patch lowered the budget or added a requirement, or a
    ///     content change silently deletes people's houses. Reconciling a plot that is now over budget
    ///     is the caller's, and it is a decision — hide the overflow, refuse new placements, or ask
    ///     the owner — not a default this library may pick. <see cref="Free" /> goes negative and says
    ///     so.
    /// </remarks>
    public void Restore(IEnumerable<Placement> saved) {
        ArgumentNullException.ThrowIfNull(saved);

        placements.Clear();
        counts.Clear();
        Spent = 0;
        next = 1;

        foreach (var placement in saved) {
            placements.Add(placement);
            counts[placement.Furniture] = CountOf(placement.Furniture) + 1;
            Spent += Library.FindFurniture(placement.Furniture)?.Cost ?? 0;
            next = Math.Max(next, placement.Id + 1);
        }

        Revision++;
    }

    /// <summary>Every tag the furniture that is down grants.</summary>
    /// <param name="into">Where they go.</param>
    /// <returns>How many were added.</returns>
    public int CollectTags(GameplayTagSet into) {
        ArgumentNullException.ThrowIfNull(into);

        var added = 0;

        foreach (var placement in placements) {
            if (Library.FindFurniture(placement.Furniture) is { Tag.IsSome: true } furniture && into.Add(furniture.Tag)) {
                added++;
            }
        }

        return added;
    }

    static bool IsOneSurface(HouseSurface surface) =>
        surface != HouseSurface.Nothing && (surface & (surface - 1)) == HouseSurface.Nothing;

    HousingRefusal MayAdminister(PlayerId by) {
        if (IsBanned(by)) {
            return HousingRefusal.Banned;
        }

        return Can(by, HouseAction.Administer) ? HousingRefusal.None : HousingRefusal.Forbidden;
    }

    int IndexOf(int id) {
        for (var index = 0; index < placements.Count; index++) {
            if (placements[index].Id == id) {
                return index;
            }
        }

        return -1;
    }
}
