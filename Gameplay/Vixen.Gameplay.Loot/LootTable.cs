// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Loot;

/// <summary>A compiled row.</summary>
public sealed class LootEntry {
    internal LootEntry(LootEntryDefinition definition, DefId item, DefId table, RequirementSet conditions) {
        Definition = definition;
        Item = item;
        Table = table;
        Conditions = conditions;
    }

    /// <summary>What it was compiled from.</summary>
    public LootEntryDefinition Definition { get; }

    /// <summary>What it drops, or <see cref="DefId.None" /> when it rolls a nested table.</summary>
    public DefId Item { get; }

    /// <summary>What it rolls, or <see cref="DefId.None" /> when it drops an item.</summary>
    public DefId Table { get; }

    /// <summary>What has to be true of the kill for it to be in the table.</summary>
    public RequirementSet Conditions { get; }

    /// <summary>How likely it is against the other weighted rows.</summary>
    public float Weight => MathF.Max(0f, Definition.Weight);

    /// <summary>Its own chance, rolled independently of the pick.</summary>
    public float Chance => Math.Clamp(Definition.Chance, 0f, 1f);

    /// <summary>Whether it is one of the weighted rows.</summary>
    public bool IsWeighted => Weight > 0f;

    /// <summary>Whether it is rolled on its own.</summary>
    public bool IsIndependent => Chance > 0f;

    /// <summary>Whether the table's pity policy raises its chance.</summary>
    public bool UsesPity => Definition.UsesPity && IsIndependent;

    /// <summary>The fewest it drops, never below one.</summary>
    public int Minimum => Math.Max(1, Definition.Minimum);

    /// <summary>The most it drops, never below <see cref="Minimum" />.</summary>
    public int Maximum => Math.Max(Minimum, Definition.Maximum);
}

/// <summary>A compiled table.</summary>
public sealed class LootTable {
    readonly LootEntry[] entries;

    internal LootTable(LootTableDefinition definition, LootEntry[] entries) {
        Definition = definition;
        this.entries = entries;
    }

    /// <summary>What it was compiled from.</summary>
    public LootTableDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>How many times the weighted pick runs.</summary>
    public int Rolls => Math.Max(0, Definition.Rolls);

    /// <summary>Its rows, in the order they were authored.</summary>
    public ReadOnlySpan<LootEntry> Entries => entries;

    /// <summary>How a run of bad luck becomes a guarantee, or null.</summary>
    public PityPolicyDefinition? Pity => Definition.Pity;

    /// <summary>Whether anything here is subject to pity.</summary>
    public bool HasPity {
        get {
            if (Pity is null) {
                return false;
            }

            foreach (var entry in entries) {
                if (entry.UsesPity) {
                    return true;
                }
            }

            return false;
        }
    }
}
