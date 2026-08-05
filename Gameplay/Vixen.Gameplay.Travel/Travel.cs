// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Travel;

/// <summary>What sort of way of getting somewhere it is.</summary>
/// <remarks>
///     ⚠ <b>Five kinds and one mechanism.</b> Doc 28: every one of them <em>"resolves to
///     <c>RequestTransfer</c>, and the only thing this library adds is the fiction: the cost, the
///     unlock, the requirement query, and the UI"</em>. The kind is what a client draws and what a
///     designer thinks in; it changes nothing about what happens.
/// </remarks>
public enum TravelKind {
    /// <summary>A hole in the world you walk into.</summary>
    Portal,

    /// <summary>Somewhere unlocked by finding it and paid for to use.</summary>
    Waypoint,

    /// <summary>A route with a fare and a flight time.</summary>
    Taxi,

    /// <summary>Going to where somebody else is.</summary>
    Summon,

    /// <summary>The door of a dungeon.</summary>
    InstanceEntrance
}

/// <summary>Why travel was refused.</summary>
public enum TravelRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>This build has no such way of getting anywhere.</summary>
    Unknown,

    /// <summary>They have not unlocked it.</summary>
    Locked,

    /// <summary>A requirement is not met.</summary>
    Requirements,

    /// <summary>They cannot pay the fare.</summary>
    Cost,

    /// <summary>They are somewhere it cannot be used from.</summary>
    WrongPlace,

    /// <summary>It goes where they already are.</summary>
    AlreadyThere
}

/// <summary>A portal, a waypoint, a taxi route, a summon or an instance door.</summary>
[DataContract("TravelPointDefinition")]
public sealed record TravelPointDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What sort it is.</summary>
    public TravelKind Kind { get; set; }

    /// <summary>The address of the map it is on, or empty for one usable anywhere.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The address of the map it goes to.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>What the destination is called, for a client that has not loaded the map.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>The tag that unlocks it — the one a point of interest grants. Empty for always open.</summary>
    /// <remarks>
    ///     ⚠ <b>A tag rather than a reference to <c>Vixen.Gameplay.Exploration</c>.</b> Doc 28's spine
    ///     forbids the edge, and the tag is better anyway: a waypoint can then be unlocked by finding
    ///     it, by finishing a quest, by buying it, or by any other thing that grants a tag.
    /// </remarks>
    public string UnlockedBy { get; set; } = string.Empty;

    /// <summary>The address of what the fare is paid in. Empty for free.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>How much.</summary>
    public long Cost { get; set; }

    /// <summary>How long it takes, in seconds. Zero for instant.</summary>
    public float Seconds { get; set; }

    /// <summary>What else has to be true.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (UnlockedBy.Length > 0) {
            tags.Add(UnlockedBy);
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>A travel point with its names resolved.</summary>
public sealed class TravelPoint {
    internal TravelPoint(
        TravelPointDefinition definition,
        DefId from,
        DefId to,
        DefId currency,
        GameplayTagRange unlock,
        RequirementSet requirements
    ) {
        Definition = definition;
        From = from;
        To = to;
        Currency = currency;
        Unlock = unlock;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public TravelPointDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What sort it is.</summary>
    public TravelKind Kind => Definition.Kind;

    /// <summary>Which map it is on, or <see cref="DefId.None" /> for anywhere.</summary>
    public DefId From { get; }

    /// <summary>Which map it goes to.</summary>
    public DefId To { get; }

    /// <summary>What the fare is paid in.</summary>
    public DefId Currency { get; }

    /// <summary>How much, never below zero.</summary>
    public long Cost => Math.Max(0, Definition.Cost);

    /// <summary>Whether it costs anything.</summary>
    public bool IsFree => Cost == 0 || !Currency.IsSome;

    /// <summary>What unlocks it, or an empty range for always open.</summary>
    public GameplayTagRange Unlock { get; }

    /// <summary>How long it takes.</summary>
    public float Seconds => MathF.Max(0f, Definition.Seconds);

    /// <summary>What else has to be true.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>What a realm is being asked to do. The whole output of this library.</summary>
/// <param name="Player">Who is going.</param>
/// <param name="Point">By what means.</param>
/// <param name="To">Which map.</param>
/// <param name="Currency">What the fare is in, or <see cref="DefId.None" />.</param>
/// <param name="Cost">How much.</param>
/// <param name="Seconds">How long it takes.</param>
/// <remarks>
///     ⚠ <b>An order, not a transfer.</b> Doc 27's <c>RequestTransfer</c> is the one mechanism, and
///     doc 28 says the only thing this library adds is the fiction round it. Anything here that moved
///     a player would be a second transfer protocol, which is exactly what doc 27's being one
///     mechanism was for.
/// </remarks>
public readonly record struct TransferOrder(
    PlayerId Player,
    DefId Point,
    DefId To,
    DefId Currency,
    long Cost,
    float Seconds
);

/// <summary>Every way of getting anywhere a build knows, compiled once.</summary>
public sealed class TravelLibrary {
    readonly Dictionary<uint, TravelPoint> points;
    readonly string[] problems;

    TravelLibrary(Dictionary<uint, TravelPoint> points, string[] problems) {
        this.points = points;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static TravelLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Everything, in address order.</summary>
    public IEnumerable<TravelPoint> Points =>
        points.Values.OrderBy(point => point.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static TravelLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var points = new Dictionary<uint, TravelPoint>();

        foreach (var definition in catalog.OfType<TravelPointDefinition>()) {
            if (definition.To.Length == 0) {
                problems.Add($"'{definition.Address}' goes nowhere.");
            }

            if (definition.Cost > 0 && definition.Currency.Length == 0) {
                problems.Add(
                    $"'{definition.Address}' costs {definition.Cost} of nothing, so it is free and reads "
                    + "as though it is not."
                );
            }

            if (definition.Kind == TravelKind.Waypoint && definition.UnlockedBy.Length == 0) {
                problems.Add(
                    $"'{definition.Address}' is a waypoint nothing unlocks, so it is a portal — say so, or "
                    + "give it the tag whatever discovers it grants."
                );
            }

            if (definition.From.Length > 0 && string.Equals(definition.From, definition.To, StringComparison.Ordinal)) {
                problems.Add($"'{definition.Address}' goes from '{definition.From}' to itself.");
            }

            points.Add(
                definition.Id.Value,
                new(
                    definition,
                    DefId.From(definition.From),
                    DefId.From(definition.To),
                    DefId.From(definition.Currency),
                    definition.UnlockedBy.Length > 0 ? tags.RangeOf(definition.UnlockedBy) : GameplayTagRange.Empty,
                    RequirementSet.Compile(definition.Requires, tags)
                )
            );
        }

        return new(points, [.. problems]);
    }

    /// <summary>Finds one.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public TravelPoint? Find(DefId id) => points.GetValueOrDefault(id.Value);

    /// <summary>Everywhere somebody could go from where they are.</summary>
    /// <param name="from">Which map they are on.</param>
    /// <param name="context">What their requirements and unlock are evaluated against.</param>
    /// <returns>The points they could use, in address order.</returns>
    public IEnumerable<TravelPoint> AvailableFrom(DefId from, IRequirementContext? context) =>
        Points.Where(point => Travelling.CanUse(point, from, context) == TravelRefusal.None);
}

/// <summary>Whether somebody may go somewhere, and what to hand the realm if they may.</summary>
/// <remarks>
///     <b>Doc 28 § Travel, and the whole of it:</b> <em>"every one of them resolves to
///     <c>RequestTransfer</c>, and the only thing this library adds is the fiction: the cost, the
///     unlock, the requirement query, and the UI. That is the payoff of doc 27's protocol being one
///     mechanism — a game adds a new way to travel by authoring a definition."</em>
/// </remarks>
public static class Travelling {
    /// <summary>Whether a point can be used from where somebody is, and why not.</summary>
    /// <param name="point">Which point.</param>
    /// <param name="from">Which map they are on.</param>
    /// <param name="context">What their requirements and unlock are evaluated against, or null to skip both.</param>
    /// <returns>The refusal, or <see cref="TravelRefusal.None" />.</returns>
    public static TravelRefusal CanUse(TravelPoint point, DefId from, IRequirementContext? context) {
        ArgumentNullException.ThrowIfNull(point);

        if (point.From.IsSome && from.IsSome && point.From != from) {
            return TravelRefusal.WrongPlace;
        }

        if (point.To == from && from.IsSome) {
            return TravelRefusal.AlreadyThere;
        }

        if (context is null) {
            return TravelRefusal.None;
        }

        // ⚠ The unlock before the requirements, because "you have not found this yet" is the answer a
        // player needs and "you are not level 40" is noise once they cannot see the waypoint at all.
        if (point.Unlock.IsSome && !context.HasTag(point.Unlock)) {
            return TravelRefusal.Locked;
        }

        return point.Requirements.IsMetBy(context) ? TravelRefusal.None : TravelRefusal.Requirements;
    }

    /// <summary>Works out what the realm is being asked to do.</summary>
    /// <param name="point">Which point.</param>
    /// <param name="player">Who is going.</param>
    /// <param name="from">Which map they are on.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <param name="purse">How much of each currency they have, or null to skip the fare check.</param>
    /// <param name="order">What to hand the realm.</param>
    /// <returns>The refusal, or <see cref="TravelRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>It does not take the fare.</b> The order carries what is owed and the caller's ledger
    ///     moves it, on the same terms as every other cost in this framework — and for the specific
    ///     reason that a fare taken here and a transfer that then fails is a player who paid to stay
    ///     where they were.
    /// </remarks>
    public static TravelRefusal Order(
        TravelPoint point,
        PlayerId player,
        DefId from,
        IRequirementContext? context,
        IReadOnlyDictionary<uint, long>? purse,
        out TransferOrder order
    ) {
        ArgumentNullException.ThrowIfNull(point);

        order = default;

        var refusal = CanUse(point, from, context);

        if (refusal != TravelRefusal.None) {
            return refusal;
        }

        if (!point.IsFree && purse is not null && purse.GetValueOrDefault(point.Currency.Value) < point.Cost) {
            return TravelRefusal.Cost;
        }

        order = new(player, point.Id, point.To, point.Currency, point.IsFree ? 0 : point.Cost, point.Seconds);

        return TravelRefusal.None;
    }
}
