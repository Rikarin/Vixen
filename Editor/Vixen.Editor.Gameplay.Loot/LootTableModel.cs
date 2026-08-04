// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Loot;

namespace Vixen.Editor.Gameplay.Loot;

/// <summary>Something wrong with a table, said the way a row can be highlighted.</summary>
/// <param name="Row">Which row, or −1 for the table itself.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public readonly record struct LootProblem(int Row, string Message);

/// <summary>One loot table, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>The document <em>is</em> a <see cref="LootTableDefinition" />.</b> There is no
///         editor-side model of a table to keep in step with the runtime's — the same bargain
///         <c>Vixen.Editor.Ai</c> makes, and for the same reason: a second representation is a second
///         thing to migrate and a second place for the odds to differ.
///     </para>
///     <para>
///         Every gesture is one operation and one <see cref="Changed" />, so an undo stack is
///         snapshot-in, snapshot-out over <see cref="Snapshot" /> and <see cref="Restore" /> rather
///         than a per-field diff.
///     </para>
/// </remarks>
public sealed class LootTableModel {
    /// <summary>Opens a table.</summary>
    /// <param name="table">The definition, which the model edits in place.</param>
    public LootTableModel(LootTableDefinition table) {
        ArgumentNullException.ThrowIfNull(table);

        Table = table;
    }

    /// <summary>The table being edited.</summary>
    public LootTableDefinition Table { get; private set; }

    /// <summary>How many rows it has.</summary>
    public int Count => Table.Entries.Count;

    /// <summary>Raised after anything changes the table.</summary>
    public event Action<LootTableModel>? Changed;

    /// <summary>Adds a row.</summary>
    /// <param name="entry">The row, or null for an empty weighted one.</param>
    /// <returns>Where it went.</returns>
    public int AddEntry(LootEntryDefinition? entry = null) {
        Table.Entries.Add(entry ?? new() { Weight = 1f });
        Raise();

        return Table.Entries.Count - 1;
    }

    /// <summary>Removes a row.</summary>
    /// <param name="row">Which one.</param>
    /// <returns>Whether there was one.</returns>
    public bool RemoveEntry(int row) {
        if (row < 0 || row >= Table.Entries.Count) {
            return false;
        }

        Table.Entries.RemoveAt(row);
        Raise();

        return true;
    }

    /// <summary>Moves a row.</summary>
    /// <param name="from">Which one.</param>
    /// <param name="to">Where to.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     ⚠ <b>Row order is not cosmetic.</b> The evaluator runs independent rows in the order they
    ///     were authored, so reordering them changes what every recorded event id produces — which is
    ///     a real edit rather than a tidy-up, and an editor that presented it as a sort would be
    ///     lying.
    /// </remarks>
    public bool MoveEntry(int from, int to) {
        if (from < 0 || from >= Table.Entries.Count || to < 0 || to >= Table.Entries.Count || from == to) {
            return false;
        }

        var entry = Table.Entries[from];
        Table.Entries.RemoveAt(from);
        Table.Entries.Insert(to, entry);
        Raise();

        return true;
    }

    /// <summary>Changes a row, whatever the gesture touched.</summary>
    /// <param name="row">Which one.</param>
    /// <param name="edit">What to do to it.</param>
    /// <returns>Whether there was one.</returns>
    public bool Edit(int row, Action<LootEntryDefinition> edit) {
        ArgumentNullException.ThrowIfNull(edit);

        if (row < 0 || row >= Table.Entries.Count) {
            return false;
        }

        edit(Table.Entries[row]);
        Raise();

        return true;
    }

    /// <summary>Sets how many times the weighted pick runs.</summary>
    /// <param name="rolls">How many.</param>
    public void SetRolls(int rolls) {
        Table.Rolls = Math.Max(0, rolls);
        Raise();
    }

    /// <summary>Sets the pity policy, or clears it.</summary>
    /// <param name="pity">The policy, or null.</param>
    public void SetPity(PityPolicyDefinition? pity) {
        Table.Pity = pity;
        Raise();
    }

    /// <summary>A copy of the table, for an undo stack to hold.</summary>
    /// <returns>The copy.</returns>
    /// <remarks>
    ///     A deep copy, because the rows are mutable classes the YAML binder needs settable — a
    ///     record's shallow <c>with</c> would hand the undo stack the very list the next edit
    ///     mutates.
    /// </remarks>
    public LootTableDefinition Snapshot() {
        var copy = Table with {
            Entries = [.. Table.Entries.Select(Copy)],
            Pity = Table.Pity is { } pity
                ? new() {
                    AttemptsBefore = pity.AttemptsBefore,
                    RampPerAttempt = pity.RampPerAttempt,
                    GuaranteedAt = pity.GuaranteedAt
                }
                : null
        };

        return copy;
    }

    /// <summary>Puts a snapshot back.</summary>
    /// <param name="snapshot">What <see cref="Snapshot" /> produced.</param>
    public void Restore(LootTableDefinition snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        Table = snapshot;
        Raise();
    }

    /// <summary>What is wrong with the table, as rows an editor can highlight.</summary>
    /// <returns>The problems, in row order.</returns>
    /// <remarks>
    ///     ⚠ <b>The same rules <c>LootLibrary.Compile</c> enforces, checked here so a designer sees
    ///     them while typing rather than at the content build.</b> They are duplicated deliberately
    ///     and narrowly: the build's copy is the one that fails, this one only decorates, and
    ///     <c>EveryRuleTheContentBuildEnforcesIsShownWhileTyping</c> asserts the two agree on a table
    ///     that breaks all of them.
    /// </remarks>
    public IReadOnlyList<LootProblem> Validate() {
        var problems = new List<LootProblem>();

        if (Table.Entries.Count == 0) {
            problems.Add(new(-1, "The table has no rows, so it drops nothing."));
        }

        var weighted = 0;

        for (var row = 0; row < Table.Entries.Count; row++) {
            var entry = Table.Entries[row];

            if (entry.Weight > 0f && entry.Chance > 0f) {
                problems.Add(new(row, "A row is either one of the weighted picks or an independent roll, not both."));
            }

            if (entry.Weight <= 0f && entry.Chance <= 0f) {
                problems.Add(new(row, "A row with neither a weight nor a chance can never drop."));
            }

            if (entry.Item.Length > 0 == entry.Table.Length > 0) {
                problems.Add(
                    new(
                        row,
                        entry.Item.Length > 0
                            ? "A row names both an item and a table."
                            : "A row names neither an item nor a table."
                    )
                );
            }

            if (entry.Maximum < entry.Minimum) {
                problems.Add(new(row, $"A row drops at least {entry.Minimum} and at most {entry.Maximum}."));
            }

            if (entry.UsesPity && entry.Chance <= 0f) {
                problems.Add(new(row, "Pity raises a chance, and a weighted row has no chance to raise."));
            }

            if (entry.UsesPity && Table.Pity is null) {
                problems.Add(new(row, "A row uses pity and the table has no pity policy."));
            }

            if (entry.Weight > 0f) {
                weighted++;
            }
        }

        if (Table.Rolls > 0 && weighted == 0) {
            problems.Add(new(-1, $"The table rolls {Table.Rolls} times and has no weighted rows to pick from."));
        }

        if (Table.Pity is { GuaranteedAt: > 0 } policy && policy.GuaranteedAt < policy.AttemptsBefore) {
            problems.Add(
                new(
                    -1,
                    $"Pity is guaranteed at {policy.GuaranteedAt} attempts and does not start ramping until "
                    + $"{policy.AttemptsBefore}, so the ramp never happens."
                )
            );
        }

        return problems;
    }

    static LootEntryDefinition Copy(LootEntryDefinition entry) =>
        new() {
            Item = entry.Item,
            Table = entry.Table,
            Weight = entry.Weight,
            Chance = entry.Chance,
            UsesPity = entry.UsesPity,
            Minimum = entry.Minimum,
            Maximum = entry.Maximum,
            Conditions = [.. entry.Conditions.Select(
                condition => new RequirementDefinition {
                    Kind = condition.Kind,
                    Subject = condition.Subject,
                    Comparison = condition.Comparison,
                    Value = condition.Value
                }
            )]
        };

    void Raise() => Changed?.Invoke(this);
}
