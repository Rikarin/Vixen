// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Gameplay;
using Vixen.Gameplay.Combat;
using Vixen.Gameplay.Loot;

namespace Vixen.Samples.Mmo.Rules;

/// <summary>What kind of thing it is, which is the whole of how hard it hits.</summary>
public enum CreatureRank {
    /// <summary>One player, one fight.</summary>
    Normal,

    /// <summary>Two or three players, or one very good one.</summary>
    Elite,

    /// <summary>A named spawn that is not always up.</summary>
    Rare,

    /// <summary>The reason a group came.</summary>
    Boss
}

/// <summary>A thing that stands in the world, hits back, and drops something.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the sample's own definition type, and it exists because
///         <a href="../../../docs/plan/28-gameplay-framework.md">doc 28</a> has no equivalent.</b>
///         Every <em>part</em> of a fight is in the libraries and nothing is a fighter:
///         <c>Vixen.Gameplay.Ai</c> says what spawns and how far it may be pulled,
///         <c>Vixen.Gameplay.Combat</c> says what an ability does and how threat accrues,
///         <c>Vixen.Gameplay.Loot</c> says what drops and <c>Vixen.Gameplay.Items</c> says what the
///         drop is — but nothing says <em>"a level 6 boar with 240 health, the
///         <c>Creature.Beast.Boar</c> tag, these abilities, this table and this leash"</em>.
///     </para>
///     <para>
///         <b>The gap is structural rather than an oversight.</b> A creature needs <c>Items</c>,
///         <c>Combat</c>, <c>Loot</c> and <c>Ai</c> at once, and doc 28's spine allows only
///         <c>Items</c> and <c>Combat</c> to be depended on — so the type cannot live in any existing
///         library without breaking the spine. Which is exactly why it lives in a <em>game's</em>
///         assembly: a game may reference all twenty, and the spine is a rule about the libraries
///         rather than about their users. Whether the engine should grow a
///         <c>Vixen.Gameplay.Encounters</c> to hold it is task #45.
///     </para>
///     <para>
///         ⚠ <b>The tags are the load-bearing field.</b> A quest's Kill objective counts by tag, so a
///         creature that grants nothing is a creature no quest can be about — which is precisely the
///         state this sample was in before this type existed: four spawn tables naming creatures that
///         did not exist, and every Kill objective waiting on a tag with no grantor.
///     </para>
/// </remarks>
[DataContract("CreatureDefinition")]
public sealed record CreatureDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How hard, before the numbers.</summary>
    public CreatureRank Rank { get; set; }

    /// <summary>What level it fights at.</summary>
    public int Level { get; set; } = 1;

    /// <summary>How much it has.</summary>
    public int Health { get; set; } = 100;

    /// <summary>What it hits for, before its abilities.</summary>
    public int Damage { get; set; } = 10;

    /// <summary>What it mitigates with.</summary>
    public int Armour { get; set; }

    /// <summary>What it is worth, in experience.</summary>
    public int Experience { get; set; }

    /// <summary>Whose side it is on — <c>Faction.Hostile.Barrow</c>.</summary>
    public string Faction { get; set; } = string.Empty;

    /// <summary>What it is, for a Kill objective to count and for a resistance to apply.</summary>
    /// <remarks>⚠ Without one of these it is a creature no quest can be about. See the type's remarks.</remarks>
    public List<string> Tags { get; set; } = [];

    /// <summary>What it casts, by address.</summary>
    public List<string> Abilities { get; set; } = [];

    /// <summary>What it drops, by address.</summary>
    public string Loot { get; set; } = string.Empty;

    /// <summary>Which behaviour tree drives it, by address. Empty for one that only leashes and swings.</summary>
    public string Script { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }

        if (Faction.Length > 0) {
            tags.Add(Faction);
        }
    }
}

/// <summary>A creature with its addresses resolved.</summary>
public sealed class Creature {
    internal Creature(CreatureDefinition definition, GameplayTag[] tags, AbilityTemplate[] abilities, LootTable? loot) {
        Definition = definition;
        Tags = tags;
        Abilities = abilities;
        Loot = loot;
    }

    /// <summary>What it was compiled from.</summary>
    public CreatureDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>How hard.</summary>
    public CreatureRank Rank => Definition.Rank;

    /// <summary>What level.</summary>
    public int Level => Definition.Level;

    /// <summary>How much it has.</summary>
    public int Health => Math.Max(1, Definition.Health);

    /// <summary>What it is, resolved.</summary>
    public IReadOnlyList<GameplayTag> Tags { get; }

    /// <summary>What it casts, resolved.</summary>
    public IReadOnlyList<AbilityTemplate> Abilities { get; }

    /// <summary>What it drops, or null for something that drops nothing.</summary>
    public LootTable? Loot { get; }
}

/// <summary>Every creature a build knows, compiled once against the libraries it points at.</summary>
/// <remarks>
///     ⚠ <b>It takes the other libraries rather than the catalog, which is the point of it.</b> A
///     creature is the one definition in this game that is a join — its abilities are Combat's, its
///     drops are Loot's, its tags are the kernel's — so compiling it is where a dangling reference is
///     caught. Nothing in <c>Gameplay/</c> could do this, because nothing in <c>Gameplay/</c> may see
///     all three at once.
/// </remarks>
public sealed class CreatureLibrary {
    readonly Dictionary<uint, Creature> creatures;
    readonly string[] problems;

    CreatureLibrary(Dictionary<uint, Creature> creatures, string[] problems) {
        this.creatures = creatures;
        this.problems = problems;
    }

    /// <summary>Every creature, in address order.</summary>
    public IEnumerable<Creature> All =>
        creatures.Values.OrderBy(creature => creature.Definition.Address, StringComparer.Ordinal);

    /// <summary>How many.</summary>
    public int Count => creatures.Count;

    /// <summary>What did not resolve.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles them.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <param name="abilities">Combat's library, for the ability addresses.</param>
    /// <param name="loot">Loot's library, for the table addresses.</param>
    /// <returns>The library.</returns>
    public static CreatureLibrary Compile(DefinitionCatalog catalog, AbilityLibrary abilities, LootLibrary loot) {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(loot);

        var problems = new List<string>();
        var compiled = new Dictionary<uint, Creature>();

        foreach (var definition in catalog.OfType<CreatureDefinition>()) {
            if (definition.Tags.Count == 0) {
                problems.Add(
                    $"'{definition.Address}' grants no tags, so no Kill objective can ever count it and "
                    + "no resistance can ever apply to it."
                );
            }

            var tags = definition.Tags.Select(catalog.Tags.Resolve).Where(tag => tag.IsSome).ToArray();
            var cast = new List<AbilityTemplate>();

            foreach (var address in definition.Abilities) {
                if (abilities.Find(address) is { } ability) {
                    cast.Add(ability);
                } else {
                    problems.Add($"'{definition.Address}' casts '{address}', which is not an ability in this build.");
                }
            }

            LootTable? table = null;

            if (definition.Loot.Length > 0) {
                table = loot.Find(DefId.From(definition.Loot));

                if (table is null) {
                    problems.Add($"'{definition.Address}' drops '{definition.Loot}', which is not a table in this build.");
                }
            }

            // ⚠ A boss with no loot is almost certainly a mistake, and a normal one with none is
            // almost certainly not. Worth saying only in the case where it is worth saying.
            if (definition.Rank == CreatureRank.Boss && table is null) {
                problems.Add($"'{definition.Address}' is a boss and drops nothing.");
            }

            compiled.Add(definition.Id.Value, new(definition, tags, [.. cast], table));
        }

        return new(compiled, [.. problems]);
    }

    /// <summary>Finds one.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Creature? Find(DefId id) => creatures.GetValueOrDefault(id.Value);
}
