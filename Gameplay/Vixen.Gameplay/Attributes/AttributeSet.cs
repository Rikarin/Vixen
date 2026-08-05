// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Gameplay;

/// <summary>One thing's stats: base values, the modifiers acting on them, and the results.</summary>
/// <remarks>
///     <para>
///         <b>The evaluation order is fixed and this class is where it is written down once</b>
///         (doc 28 § Attributes):
///     </para>
///     <code>base  →  +flat  →  ×(1 + Σ additive%)  →  ×Π(1 + multiplicative%)  →  clamp  →  round</code>
///     <para>
///         <b>Removal is by source, and the value is recomputed rather than subtracted.</b> Undoing a
///         buff by adding its negation back is how a stat ends up at 99.9997 after ten cycles of a
///         proc that grants and removes 15 %; recomputing from the survivors cannot drift, because
///         the same set of modifiers always produces the same number.
///     </para>
///     <para>
///         <b>Modifiers are held in a canonical order, not in the order they arrived.</b> Float
///         addition is not associative, so a client that applied a trinket before a raid buff and a
///         realm that applied them the other way round would compute numbers that differ in the last
///         bit — which prediction reports as a mismatch and a player sees as jitter. Sorting on
///         (stat, bucket, source, value) costs an insertion memmove and removes the whole class.
///     </para>
///     <para>
///         <b>Recomputation is dirty-flagged per stat and batched.</b> Applying twelve modifiers
///         marks the stats they touch and computes nothing; the first read, or the frame's
///         <see cref="Recompute" />, does the arithmetic once. What replicates is the
///         <em>result</em> — a client is told a number, not a list of modifiers it would have to
///         re-derive.
///     </para>
/// </remarks>
public sealed class AttributeSet {
    readonly AttributeLayout layout;
    readonly float[] bases;
    readonly float[] values;
    readonly ulong[] dirty;
    readonly ulong[] changed;
    readonly List<Modifier> modifiers = [];

    /// <summary>Makes a set over a layout, every stat at its declared default.</summary>
    /// <param name="layout">The stats it has.</param>
    public AttributeSet(AttributeLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);

        this.layout = layout;
        bases = new float[layout.Count];
        values = new float[layout.Count];

        var words = (layout.Count + 63) / 64;
        dirty = new ulong[words];
        changed = new ulong[words];

        for (var slot = 0; slot < layout.Count; slot++) {
            bases[slot] = layout[slot].Default;
        }

        MarkAllDirty();
    }

    /// <summary>The stats this set has.</summary>
    public AttributeLayout Layout => layout;

    /// <summary>Every modifier currently applied, in canonical order.</summary>
    public IReadOnlyList<Modifier> Modifiers => modifiers;

    /// <summary>
    ///     How many modifiers have been handed to <see cref="Add" /> for a stat this layout does not
    ///     declare, and therefore dropped.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Dropped rather than thrown, and counted rather than ignored.</b> A raid boss whose
    ///     layout has no <c>DodgeChance</c> being handed a dodge buff is ordinary — a game's layouts
    ///     differ per archetype — so throwing would take a realm down over a content combination
    ///     nobody thought about. Silently dropping it, though, is how a whole stat turns out to have
    ///     done nothing for a month. This is the number a diagnostic reports.
    /// </remarks>
    public int DroppedModifiers { get; private set; }

    /// <summary>The unmodified value of a stat.</summary>
    /// <param name="attribute">The stat.</param>
    /// <returns>Its base, or zero when the layout does not declare it.</returns>
    public float BaseOf(AttributeId attribute) {
        var slot = layout.SlotOf(attribute);

        return slot < 0 ? 0f : bases[slot];
    }

    /// <summary>Sets the unmodified value of a stat.</summary>
    /// <param name="attribute">The stat.</param>
    /// <param name="value">Its new base.</param>
    /// <returns>Whether the layout declares the stat.</returns>
    public bool SetBase(AttributeId attribute, float value) {
        var slot = layout.SlotOf(attribute);

        if (slot < 0) {
            return false;
        }

        if (bases[slot].Equals(value)) {
            return true;
        }

        bases[slot] = value;
        MarkDirty(slot);

        return true;
    }

    /// <summary>The value of a stat with every modifier applied.</summary>
    /// <param name="attribute">The stat.</param>
    /// <returns>The value, or zero when the layout does not declare it.</returns>
    /// <remarks>
    ///     Recomputes this one stat if it is dirty. Reading is therefore never stale, and a caller
    ///     that reads one stat does not pay for the eleven others a buff also touched.
    /// </remarks>
    public float ValueOf(AttributeId attribute) {
        var slot = layout.SlotOf(attribute);

        if (slot < 0) {
            return 0f;
        }

        if (IsDirty(slot)) {
            Evaluate(slot);
        }

        return values[slot];
    }

    /// <summary>Applies a modifier.</summary>
    /// <param name="modifier">What to apply.</param>
    /// <returns>Whether the layout declares the stat it acts on.</returns>
    public bool Add(in Modifier modifier) {
        var slot = layout.SlotOf(modifier.Attribute);

        if (slot < 0) {
            DroppedModifiers++;

            return false;
        }

        modifiers.Insert(~Search(modifier), modifier);
        MarkDirty(slot);

        return true;
    }

    /// <summary>Applies several modifiers.</summary>
    /// <param name="span">What to apply.</param>
    /// <returns>How many were applied.</returns>
    public int AddRange(ReadOnlySpan<Modifier> span) {
        var applied = 0;

        foreach (ref readonly var modifier in span) {
            if (Add(modifier)) {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>Removes every modifier one source granted.</summary>
    /// <param name="source">The source.</param>
    /// <returns>How many were removed.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="ModifierSource.None" /> removes nothing.</b> Unowned modifiers are the ones
    ///     a game applied deliberately without a handle to take them off by, and matching them here
    ///     would make every effect that expires take them with it. <see cref="ClearModifiers" /> is
    ///     the way to remove those.
    /// </remarks>
    public int RemoveBySource(ModifierSource source) {
        if (!source.IsSome) {
            return 0;
        }

        var removed = 0;

        for (var index = modifiers.Count - 1; index >= 0; index--) {
            if (modifiers[index].Source != source) {
                continue;
            }

            MarkDirty(layout.SlotOf(modifiers[index].Attribute));
            modifiers.RemoveAt(index);
            removed++;
        }

        return removed;
    }

    /// <summary>Removes every modifier, whatever granted it.</summary>
    public void ClearModifiers() {
        if (modifiers.Count == 0) {
            return;
        }

        foreach (var modifier in modifiers) {
            MarkDirty(layout.SlotOf(modifier.Attribute));
        }

        modifiers.Clear();
    }

    /// <summary>Recomputes every stat a change has touched.</summary>
    /// <returns>How many stats were recomputed.</returns>
    /// <remarks>
    ///     The batched form, meant to be called once per frame by whatever owns the set. Reading a
    ///     stat recomputes it on demand anyway, so this is an optimisation and never a correctness
    ///     requirement — which is what keeps a system that forgets to call it merely slower.
    /// </remarks>
    public int Recompute() {
        var count = 0;

        for (var word = 0; word < dirty.Length; word++) {
            var bits = dirty[word];

            while (bits != 0) {
                var bit = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                Evaluate((word * 64) + bit);
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether a stat's value has moved since <see cref="ClearChanges" /> was last called.</summary>
    /// <param name="attribute">The stat.</param>
    /// <returns>Whether it changed.</returns>
    /// <remarks>
    ///     What replication and the UI read. A stat whose modifiers changed but whose value did not —
    ///     two buffs that cancel, a clamp that was already saturated — has <em>not</em> changed, and
    ///     saying so is what stops a permanently capped resistance sending a packet every frame.
    /// </remarks>
    public bool HasChanged(AttributeId attribute) {
        var slot = layout.SlotOf(attribute);

        return slot >= 0 && (changed[slot >> 6] & (1UL << (slot & 63))) != 0;
    }

    /// <summary>Whether anything at all has moved since <see cref="ClearChanges" /> was last called.</summary>
    public bool HasChanges {
        get {
            foreach (var word in changed) {
                if (word != 0) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Forgets which stats moved. Called after the changes have been sent or drawn.</summary>
    public void ClearChanges() => Array.Clear(changed);

    void Evaluate(int slot) {
        ref readonly var schema = ref layout[slot];

        var flat = 0f;
        var additive = 0f;
        var multiplicative = 1f;

        // The list is sorted by (stat, bucket, source, value), so this walk is already the canonical
        // order and the three buckets arrive in evaluation order. Binary search for the first
        // modifier on this stat, then scan while it is still this stat.
        for (var index = FirstOf(schema.Attribute); index < modifiers.Count; index++) {
            var modifier = modifiers[index];

            if (modifier.Attribute != schema.Attribute) {
                break;
            }

            switch (modifier.Op) {
                case ModifierOp.Add:
                    flat += modifier.Value;

                    break;

                case ModifierOp.AddPercent:
                    additive += modifier.Value;

                    break;

                case ModifierOp.MultiplyPercent:
                    multiplicative *= 1f + modifier.Value;

                    break;

                default:
                    break;
            }
        }

        var value = (bases[slot] + flat) * (1f + additive) * multiplicative;

        value = Math.Clamp(value, schema.Minimum, schema.Maximum);

        value = schema.Rounding switch {
            AttributeRounding.Nearest => MathF.Round(value, MidpointRounding.AwayFromZero),
            AttributeRounding.Down => MathF.Floor(value),
            AttributeRounding.Up => MathF.Ceiling(value),
            _ => value
        };

        dirty[slot >> 6] &= ~(1UL << (slot & 63));

        if (values[slot].Equals(value)) {
            return;
        }

        values[slot] = value;
        changed[slot >> 6] |= 1UL << (slot & 63);
    }

    /// <summary>The index of the first modifier acting on a stat, or the count when there is none.</summary>
    int FirstOf(AttributeId attribute) {
        var low = 0;
        var high = modifiers.Count;

        while (low < high) {
            var middle = low + ((high - low) >> 1);

            if (modifiers[middle].Attribute.Value < attribute.Value) {
                low = middle + 1;
            } else {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Where a modifier sorts, as the bitwise complement of its insertion point.</summary>
    int Search(in Modifier modifier) {
        var low = 0;
        var high = modifiers.Count - 1;

        while (low <= high) {
            var middle = low + ((high - low) >> 1);

            if (Compare(modifiers[middle], modifier) < 0) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return ~low;
    }

    /// <summary>
    ///     The canonical order: stat, then bucket, then source, then the value's bits. Every field, so
    ///     that two hosts holding the same multiset of modifiers hold them in the same sequence and
    ///     therefore sum them identically.
    /// </summary>
    static int Compare(in Modifier left, in Modifier right) {
        var order = left.Attribute.Value.CompareTo(right.Attribute.Value);

        if (order != 0) {
            return order;
        }

        order = ((int)left.Op).CompareTo((int)right.Op);

        if (order != 0) {
            return order;
        }

        order = left.Source.Value.CompareTo(right.Source.Value);

        return order != 0
            ? order
            : BitConverter.SingleToUInt32Bits(left.Value).CompareTo(BitConverter.SingleToUInt32Bits(right.Value));
    }

    bool IsDirty(int slot) => (dirty[slot >> 6] & (1UL << (slot & 63))) != 0;

    void MarkDirty(int slot) {
        if (slot >= 0) {
            dirty[slot >> 6] |= 1UL << (slot & 63);
        }
    }

    void MarkAllDirty() {
        for (var slot = 0; slot < layout.Count; slot++) {
            dirty[slot >> 6] |= 1UL << (slot & 63);
        }
    }
}
