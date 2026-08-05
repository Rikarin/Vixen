// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Loot;

/// <summary>How a drop is shared out.</summary>
/// <remarks>
///     <b>Policies on the drop, not different code paths</b> — doc 28 § Loot. Every one of these
///     rolls the same table with the same evaluator; what differs is how many times it is rolled and
///     who the result belongs to.
/// </remarks>
public enum LootDistribution {
    /// <summary>The table is rolled once per participant, and each gets their own.</summary>
    Personal,

    /// <summary>Rolled once; everything goes into a window everyone can see.</summary>
    Group,

    /// <summary>Rolled once into a window, and each item is contested before it is taken.</summary>
    NeedGreed,

    /// <summary>Rolled once into a window one participant assigns from.</summary>
    MasterLooter
}

/// <summary>How a run of bad luck becomes a guarantee.</summary>
/// <remarks>
///     <para>
///         <b>A first-class field rather than a game's private counter</b> — doc 28 § Loot — because
///         it is durable state, and a pity counter that resets on a realm crash is a support ticket.
///     </para>
///     <para>
///         ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///         <see cref="ModifierDefinition" />.
///     </para>
/// </remarks>
[DataContract("PityPolicy")]
public sealed class PityPolicyDefinition {
    /// <summary>How many failed attempts pass before the chance starts rising.</summary>
    public int AttemptsBefore { get; set; }

    /// <summary>How much is added to the chance for each attempt past that.</summary>
    public float RampPerAttempt { get; set; }

    /// <summary>How many failed attempts make the next one certain. Zero is never.</summary>
    public int GuaranteedAt { get; set; }
}

/// <summary>One row of a loot table: an item, or another table.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A row is <em>either</em> weighted or independent, and the two are different
///         mechanisms.</b> A <see cref="Weight" /> above zero puts the row in the table's pick — one
///         of the weighted rows wins per roll. A <see cref="Chance" /> above zero is rolled on its
///         own, in addition to the pick, which is what "and always drops five silver" and "and has a
///         2 % chance of the mount" are. A row that sets both is a row that means two things, so it
///         is refused at compile time.
///     </para>
/// </remarks>
[DataContract("LootEntry")]
public sealed class LootEntryDefinition {
    /// <summary>The address of the item it drops, or empty when it drops a nested table.</summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>The address of the nested table it rolls, or empty when it drops an item.</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>How likely it is against the other weighted rows. Zero keeps it out of the pick.</summary>
    public float Weight { get; set; }

    /// <summary>Its own chance, from zero to one, rolled independently of the pick.</summary>
    public float Chance { get; set; }

    /// <summary>Whether the table's pity policy applies to this row.</summary>
    /// <remarks>
    ///     Only meaningful with <see cref="Chance" />: pity raises a chance, and a weight has no
    ///     chance to raise.
    /// </remarks>
    public bool UsesPity { get; set; }

    /// <summary>The fewest it drops.</summary>
    public int Minimum { get; set; } = 1;

    /// <summary>The most it drops.</summary>
    public int Maximum { get; set; } = 1;

    /// <summary>What has to be true of the kill for this row to be in the table at all.</summary>
    /// <remarks>
    ///     The same requirement algebra as everything else, evaluated against a
    ///     <see cref="LootContext" /> — the killer's level, the zone's tags, the difficulty. A row
    ///     whose conditions fail is not merely skipped: it is absent, so the other rows' weights are
    ///     renormalised over what is left, which is what a designer writing "only on Heroic" means.
    /// </remarks>
    public List<RequirementDefinition> Conditions { get; set; } = [];
}

/// <summary>A tree of weighted rows, and the shape a designer actually authors.</summary>
[DataContract("LootTableDefinition")]
public sealed record LootTableDefinition : Definition {
    /// <summary>What it is called, in the editor and in a log line.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How many times the weighted pick runs. Zero means the table has no pick.</summary>
    public int Rolls { get; set; } = 1;

    /// <summary>Its rows.</summary>
    public List<LootEntryDefinition> Entries { get; set; } = [];

    /// <summary>How a run of bad luck becomes a guarantee, or null for no pity.</summary>
    public PityPolicyDefinition? Pity { get; set; }

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var entry in Entries) {
            foreach (var condition in entry.Conditions) {
                if (condition.Kind != RequirementKind.Value && condition.Subject.Length > 0) {
                    tags.Add(condition.Subject);
                }
            }
        }
    }
}

/// <summary>What breaking an item down gives back.</summary>
/// <remarks>
///     Salvage is a loot table with an item on the front of it, which is the whole of the mechanism:
///     the same weights, the same conditions and the same reproducible roll, so a disenchant and a
///     boss drop cannot disagree about how randomness works.
/// </remarks>
[DataContract("SalvageDefinition")]
public sealed record SalvageDefinition : Definition {
    /// <summary>The address of the item this salvages, or empty to match by tag.</summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>Tag prefixes an item must all match for this to be its recipe. Ignored when <see cref="Item" /> is set.</summary>
    public List<string> ItemTags { get; set; } = [];

    /// <summary>The address of the table rolled when it is broken down.</summary>
    public string Table { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in ItemTags) {
            tags.Add(tag);
        }
    }
}
