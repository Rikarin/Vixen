// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Gameplay.Housing;

/// <summary>What a piece of furniture can be put on.</summary>
/// <remarks>
///     ⚠ <b>Declared, never measured.</b> This library does not know whether the point somebody
///     picked is on a wall — that is a scene question with a collision mesh behind it. The caller says
///     which surface it found, and what is checked here is whether the furniture and the plot both
///     allow that <em>kind</em> of surface. The realm makes the geometric decision; this makes the
///     content one.
/// </remarks>
[Flags]
public enum HouseSurface {
    /// <summary>Nowhere. What an unauthored piece says, and a reported problem.</summary>
    Nothing = 0,

    /// <summary>The floor.</summary>
    Floor = 1 << 0,

    /// <summary>A wall.</summary>
    Wall = 1 << 1,

    /// <summary>The ceiling.</summary>
    Ceiling = 1 << 2,

    /// <summary>On top of something else already placed.</summary>
    Tabletop = 1 << 3,

    /// <summary>Outside, in a yard or on a plot with no roof.</summary>
    Outdoors = 1 << 4,

    /// <summary>Anywhere. What a plot with no restrictions says.</summary>
    Anywhere = Floor | Wall | Ceiling | Tabletop | Outdoors
}

/// <summary>What somebody is allowed to do in a house.</summary>
/// <remarks>
///     Four, and deliberately not more. A house has a handful of relationships and a handful of
///     verbs; the moment it has thirty permissions it needs a guild's rank matrix, and doc 28 says
///     guild housing is where that belongs.
/// </remarks>
public enum HouseAction {
    /// <summary>Come in.</summary>
    Enter,

    /// <summary>Use what is in it — a crafting station, a bed, a portal.</summary>
    Use,

    /// <summary>Place, move and remove furniture.</summary>
    Decorate,

    /// <summary>Change who is allowed what.</summary>
    Administer
}

/// <summary>How close to the owner somebody is.</summary>
/// <remarks>
///     <para>
///         <b>A ladder rather than a set of flags, and that is the difference from a guild.</b> A
///         guild has dozens of orthogonal permissions and as many ranks as a leader invents, so
///         <c>Vixen.Gameplay.Social</c> makes a permission a tag. A house has five relationships and
///         four verbs, and on a ladder "is my friend allowed to decorate" is one integer comparison.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>Banned</c> rung, and there must not be.</b> A ban expressed as the
///         bottom of the ladder is a ban that does nothing to a house whose owner has opened it to the
///         public — because then everybody is at the bottom of the ladder and the bottom is allowed
///         in. A ban is a separate set that beats the ladder outright.
///     </para>
/// </remarks>
public enum HouseTier {
    /// <summary>A stranger.</summary>
    None = 0,

    /// <summary>Somebody who may come in.</summary>
    Visitor = 1,

    /// <summary>Somebody who may come in and use things.</summary>
    Guest = 2,

    /// <summary>Somebody who lives there.</summary>
    Resident = 3,

    /// <summary>Whoever it belongs to.</summary>
    Owner = 4
}

/// <summary>Why something in a house was refused.</summary>
public enum HousingRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>There is no such plot, furniture or placement.</summary>
    Unknown,

    /// <summary>They are not allowed to.</summary>
    Forbidden,

    /// <summary>They are barred from the place entirely.</summary>
    Banned,

    /// <summary>It would cost more than the plot has left.</summary>
    OutOfBudget,

    /// <summary>There are already as many of that piece as the plot allows.</summary>
    TooMany,

    /// <summary>That piece does not go on that kind of surface, or the plot has no such surface.</summary>
    WrongSurface,

    /// <summary>A requirement is not met.</summary>
    Requirements
}

/// <summary>Who a plot belongs to: one player, or one guild.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An opaque key rather than a <c>GuildId</c>, because doc 28's spine forbids the
///         edge.</b> Only <c>Items</c> and <c>Combat</c> may be depended on horizontally, so housing
///         cannot reference <c>Vixen.Gameplay.Social</c>. The guild half is a <see cref="Guid" />
///         because that is what a <c>GuildId</c> wraps, and a game converts at the edge.
///     </para>
///     <para>
///         <b>Doc 28 says guild housing is "the same thing with an <c>IGuildGrain</c> owner and a
///         permission matrix instead of a single owner", and that is exactly what happens here:</b> a
///         guild plot has no implicit owner, so <see cref="HousePlot.TierOf" /> answers from the
///         explicit grants alone. Mapping a guild rank onto a house tier is the guild's matrix, and it
///         is applied by whoever owns the guild — not by this library, which would have to guess.
///     </para>
/// </remarks>
/// <param name="Player">Whose it is, or <see cref="PlayerId.None" /> for a guild's.</param>
/// <param name="Guild">Which guild's it is, or <see cref="Guid.Empty" /> for a player's.</param>
public readonly record struct HouseOwner(PlayerId Player, Guid Guild) {
    /// <summary>Nobody's. An unsold plot.</summary>
    public static HouseOwner None => default;

    /// <summary>Whether it belongs to anybody.</summary>
    public bool IsSome => Player.IsSome || Guild != Guid.Empty;

    /// <summary>Whether a guild owns it.</summary>
    public bool IsGuild => Guild != Guid.Empty;

    /// <summary>Makes a player's.</summary>
    /// <param name="player">Whose.</param>
    /// <returns>The owner.</returns>
    public static HouseOwner Of(PlayerId player) => new(player, Guid.Empty);

    /// <summary>Makes a guild's.</summary>
    /// <param name="guild">Which guild — a <c>GuildId</c>'s value.</param>
    /// <returns>The owner.</returns>
    public static HouseOwner OfGuild(Guid guild) => new(PlayerId.None, guild);

    /// <inheritdoc />
    public override string ToString() =>
        IsGuild ? $"guild {Guild:N}"[..14] : Player.IsSome ? Player.ToString() : "nobody";
}

/// <summary>One thing that can be put in a house.</summary>
[DataContract("FurnitureDefinition")]
public sealed record FurnitureDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What it costs against the plot's budget.</summary>
    /// <remarks>
    ///     ⚠ <b>The budget is the only thing standing between a housing feature and a realm that
    ///     cannot load.</b> Ten thousand plots with unbounded furniture is unbounded work on every
    ///     zone-in, and a cost per piece is how a designer says a chandelier is not a candle.
    /// </remarks>
    public int Cost { get; set; } = 1;

    /// <summary>What it may sit on.</summary>
    public HouseSurface Surfaces { get; set; } = HouseSurface.Floor;

    /// <summary>How many of it one plot may hold, or zero for as many as the budget allows.</summary>
    public int MaximumPerPlot { get; set; }

    /// <summary>What having it placed grants — <c>House.Has.Forge</c>. Empty for one nothing asks about.</summary>
    /// <remarks>
    ///     How a crafting station in a house is reached by a library that may not reference this one:
    ///     the tag is granted while the piece is down and taken back when it is picked up, and the
    ///     recipe's requirement asks about the tag.
    /// </remarks>
    public string Tag { get; set; } = string.Empty;

    /// <summary>What has to be true to place it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>A kind of house: how much fits in it, how it snaps, and what each verb costs in standing.</summary>
[DataContract("PlotDefinition")]
public sealed record PlotDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How much furniture fits, in the same units as a piece's cost.</summary>
    public int Budget { get; set; } = 200;

    /// <summary>What surfaces it offers.</summary>
    public HouseSurface Surfaces { get; set; } = HouseSurface.Anywhere;

    /// <summary>How far apart the grid is, in metres. Zero for free placement.</summary>
    /// <remarks>
    ///     ⚠ <b>The snap is content, not a client setting, because both ends have to agree.</b> The
    ///     authority table gives the client the placement preview and the realm the validity check; if
    ///     they round differently, every placement a player makes comes back corrected by a
    ///     centimetre, for ever.
    /// </remarks>
    public float SnapGrid { get; set; }

    /// <summary>How far apart the allowed facings are, in degrees. Zero for free rotation.</summary>
    public float SnapDegrees { get; set; } = 15f;

    /// <summary>What being in one grants — <c>House.Inside</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The lowest standing that may come in.</summary>
    public HouseTier EnterTier { get; set; } = HouseTier.Visitor;

    /// <summary>The lowest standing that may use what is in it.</summary>
    public HouseTier UseTier { get; set; } = HouseTier.Guest;

    /// <summary>The lowest standing that may decorate.</summary>
    public HouseTier DecorateTier { get; set; } = HouseTier.Resident;

    /// <summary>The lowest standing that may change who is allowed what.</summary>
    public HouseTier AdministerTier { get; set; } = HouseTier.Owner;

    /// <summary>What a stranger is treated as, before an owner changes it.</summary>
    public HouseTier Openness { get; set; } = HouseTier.None;

    /// <summary>What has to be true to own one.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>A piece of furniture with its names resolved.</summary>
public sealed class Furniture {
    internal Furniture(FurnitureDefinition definition, GameplayTag tag, RequirementSet requirements) {
        Definition = definition;
        Tag = tag;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public FurnitureDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What it costs, never below zero.</summary>
    public int Cost => Math.Max(0, Definition.Cost);

    /// <summary>What it may sit on.</summary>
    public HouseSurface Surfaces => Definition.Surfaces;

    /// <summary>How many of it one plot may hold, or zero for unlimited.</summary>
    public int MaximumPerPlot => Math.Max(0, Definition.MaximumPerPlot);

    /// <summary>What having it placed grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What has to be true to place it.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>A kind of house with its names resolved, and the snap both ends share.</summary>
public sealed class Plot {
    internal Plot(PlotDefinition definition, GameplayTag tag, RequirementSet requirements) {
        Definition = definition;
        Tag = tag;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public PlotDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>How much furniture fits, never below zero.</summary>
    public int Budget => Math.Max(0, Definition.Budget);

    /// <summary>What surfaces it offers.</summary>
    public HouseSurface Surfaces => Definition.Surfaces;

    /// <summary>What being in one grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What a stranger is treated as, before an owner changes it.</summary>
    public HouseTier Openness => Definition.Openness;

    /// <summary>What has to be true to own one.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>The lowest standing a verb needs.</summary>
    /// <param name="action">Which verb.</param>
    /// <returns>The tier.</returns>
    public HouseTier Needs(HouseAction action) => action switch {
        HouseAction.Enter => Definition.EnterTier,
        HouseAction.Use => Definition.UseTier,
        HouseAction.Decorate => Definition.DecorateTier,
        _ => Definition.AdministerTier
    };

    /// <summary>Puts a point on the grid.</summary>
    /// <param name="position">Where the player pointed.</param>
    /// <returns>Where it actually goes.</returns>
    /// <remarks>
    ///     ⚠ <b>Called by the client to preview and by the realm to store, and it must be the same
    ///     call.</b> That is why it is here rather than in the editor's gizmo or in a client's input
    ///     code — the manipulation grammar is doc 24's, but the rounding is content's.
    /// </remarks>
    public Vector3 Snap(Vector3 position) {
        var grid = Definition.SnapGrid;

        if (grid <= 0f) {
            return position;
        }

        return new(
            MathF.Round(position.X / grid) * grid,
            MathF.Round(position.Y / grid) * grid,
            MathF.Round(position.Z / grid) * grid
        );
    }

    /// <summary>Puts a facing on the allowed increments.</summary>
    /// <param name="yaw">Which way the player turned it, in radians.</param>
    /// <returns>Which way it actually faces, in radians, wrapped to −π…π.</returns>
    public float SnapYaw(float yaw) {
        var step = Definition.SnapDegrees;

        if (step <= 0f) {
            return MathUtil.WrapAngle(yaw);
        }

        var radians = MathUtil.DegreesToRadians(step);

        return MathUtil.WrapAngle(MathF.Round(yaw / radians) * radians);
    }
}

/// <summary>Every plot and every piece of furniture a build knows, compiled once.</summary>
public sealed class HousingLibrary {
    readonly Dictionary<uint, Plot> plots;
    readonly Dictionary<uint, Furniture> furniture;
    readonly string[] problems;

    HousingLibrary(Dictionary<uint, Plot> plots, Dictionary<uint, Furniture> furniture, string[] problems) {
        this.plots = plots;
        this.furniture = furniture;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static HousingLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every plot, in address order.</summary>
    public IEnumerable<Plot> Plots => plots.Values.OrderBy(plot => plot.Definition.Address, StringComparer.Ordinal);

    /// <summary>Every piece of furniture, in address order.</summary>
    public IEnumerable<Furniture> Furniture =>
        furniture.Values.OrderBy(piece => piece.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static HousingLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var plots = new Dictionary<uint, Plot>();
        var furniture = new Dictionary<uint, Furniture>();

        foreach (var definition in catalog.OfType<PlotDefinition>()) {
            if (definition.Budget <= 0) {
                problems.Add($"'{definition.Address}' has a budget of {definition.Budget}, so nothing fits in it.");
            }

            if (definition.Surfaces == HouseSurface.Nothing) {
                problems.Add($"'{definition.Address}' offers no surfaces, so nothing can be placed in it.");
            }

            // ⚠ A ladder that is not monotonic is a house where a guest may redecorate but not walk in.
            if (definition.UseTier < definition.EnterTier
                || definition.DecorateTier < definition.UseTier
                || definition.AdministerTier < definition.DecorateTier) {
                problems.Add(
                    $"'{definition.Address}' asks for less standing to decorate or administer than to "
                    + "come in, which is a house somebody can furnish from the doorstep."
                );
            }

            if (definition.Openness >= definition.DecorateTier) {
                problems.Add(
                    $"'{definition.Address}' is open to strangers at {definition.Openness}, which is at "
                    + $"or above the {definition.DecorateTier} it takes to decorate — so anybody may."
                );
            }

            plots.Add(
                definition.Id.Value,
                new(definition, tags.Resolve(definition.Tag), RequirementSet.Compile(definition.Requires, tags))
            );
        }

        foreach (var definition in catalog.OfType<FurnitureDefinition>()) {
            if (definition.Surfaces == HouseSurface.Nothing) {
                problems.Add($"'{definition.Address}' goes on no surface, so it can never be placed.");
            }

            if (definition.Cost < 0) {
                problems.Add(
                    $"'{definition.Address}' costs {definition.Cost}, and furniture that pays a plot to "
                    + "hold it makes the budget unbounded."
                );
            }

            furniture.Add(
                definition.Id.Value,
                new(definition, tags.Resolve(definition.Tag), RequirementSet.Compile(definition.Requires, tags))
            );
        }

        // Reported here rather than at placement time, because a piece nothing can ever hold is a
        // content mistake and a designer should hear about it from the build.
        foreach (var piece in furniture.Values) {
            if (plots.Count > 0 && plots.Values.All(plot => (plot.Surfaces & piece.Surfaces) == HouseSurface.Nothing)) {
                problems.Add(
                    $"'{piece.Definition.Address}' goes on {piece.Surfaces} and no plot in this build has "
                    + "one, so it can never be placed anywhere."
                );
            }
        }

        return new(plots, furniture, [.. problems]);
    }

    /// <summary>Finds a plot.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Plot? FindPlot(DefId id) => plots.GetValueOrDefault(id.Value);

    /// <summary>Finds a piece of furniture.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Furniture? FindFurniture(DefId id) => furniture.GetValueOrDefault(id.Value);
}
