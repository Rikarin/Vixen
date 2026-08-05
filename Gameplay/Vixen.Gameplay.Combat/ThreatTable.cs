// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Gameplay.Combat;

/// <summary>How much one attacker has annoyed one creature.</summary>
/// <param name="Attacker">Who, as the caller numbers them.</param>
/// <param name="Threat">How much.</param>
public readonly record struct ThreatEntry(ulong Attacker, float Threat);

/// <summary>What one creature is angry about, and with whom.</summary>
/// <remarks>
///     <para>
///         <b>Here rather than in a game, because every game that adds threat later adds it wrong</b>
///         — doc 28 § Combat says so in as many words. The failure is always the same shape: threat
///         gets bolted on as "whoever hit hardest", and then a taunt has no meaning, a healer cannot
///         pull, and a tank swap is impossible to author.
///     </para>
///     <para>
///         ⚠ <b>A taunt is not a large threat number.</b> Giving the taunter the top score plus a
///         margin makes a taunt fail the moment somebody out-damages the margin, which is the bug
///         every homegrown threat table ships with. <see cref="Taunt" /> sets the taunter to the
///         current highest and marks them as forced until the duration runs out, so the table has an
///         answer to "who is the target" that damage cannot argue with.
///     </para>
/// </remarks>
public sealed class ThreatTable {
    readonly List<ThreatEntry> entries = [];

    ulong forced;
    float forcedRemaining;

    /// <summary>How many attackers are on it.</summary>
    public int Count => entries.Count;

    /// <summary>Whether a taunt is holding the target.</summary>
    public bool IsTaunted => forcedRemaining > 0f;

    /// <summary>Them, highest first.</summary>
    /// <remarks>
    ///     Sorted on write rather than on read, because a boss reads its target every frame and is
    ///     hit a few times a second.
    /// </remarks>
    public ReadOnlySpan<ThreatEntry> Entries => CollectionsMarshal.AsSpan(entries);

    /// <summary>Adds threat, putting the attacker on the table if they were not.</summary>
    /// <param name="attacker">Who.</param>
    /// <param name="threat">How much. Negative reduces, which is what a threat drop is.</param>
    /// <returns>Their total.</returns>
    public float Add(ulong attacker, float threat) {
        if (attacker == 0) {
            return 0f;
        }

        var span = CollectionsMarshal.AsSpan(entries);

        for (var index = 0; index < span.Length; index++) {
            if (span[index].Attacker != attacker) {
                continue;
            }

            var total = MathF.Max(0f, span[index].Threat + threat);
            entries[index] = new(attacker, total);
            Sort();

            return total;
        }

        var initial = MathF.Max(0f, threat);
        entries.Add(new(attacker, initial));
        Sort();

        return initial;
    }

    /// <summary>Multiplies one attacker's threat — a threat drop, or a tank's modifier.</summary>
    /// <param name="attacker">Who.</param>
    /// <param name="factor">By how much.</param>
    public void Multiply(ulong attacker, float factor) {
        var span = CollectionsMarshal.AsSpan(entries);

        for (var index = 0; index < span.Length; index++) {
            if (span[index].Attacker == attacker) {
                entries[index] = new(attacker, MathF.Max(0f, span[index].Threat * factor));
                Sort();

                return;
            }
        }
    }

    /// <summary>Forces the target for a while.</summary>
    /// <param name="attacker">Who taunted.</param>
    /// <param name="duration">For how long, in seconds.</param>
    /// <remarks>
    ///     Also lifts them to the current highest, so that when the taunt ends they are not
    ///     immediately dropped — a taunt that hands the boss straight back is worse than no taunt.
    /// </remarks>
    public void Taunt(ulong attacker, float duration) {
        if (attacker == 0) {
            return;
        }

        var highest = entries.Count > 0 ? entries[0].Threat : 0f;
        var current = ThreatOf(attacker);

        if (highest > current) {
            Add(attacker, highest - current);
        } else if (current == 0f) {
            Add(attacker, 0f);
        }

        forced = attacker;
        forcedRemaining = MathF.Max(0f, duration);
    }

    /// <summary>Takes an attacker off — they died, they left, they vanished.</summary>
    /// <param name="attacker">Who.</param>
    /// <returns>Whether they were on it.</returns>
    public bool Remove(ulong attacker) {
        for (var index = 0; index < entries.Count; index++) {
            if (entries[index].Attacker != attacker) {
                continue;
            }

            entries.RemoveAt(index);

            if (forced == attacker) {
                forced = 0;
                forcedRemaining = 0f;
            }

            return true;
        }

        return false;
    }

    /// <summary>Forgets everything. What a reset, a wipe and a leash all do.</summary>
    public void Clear() {
        entries.Clear();
        forced = 0;
        forcedRemaining = 0f;
    }

    /// <summary>How much one attacker has.</summary>
    /// <param name="attacker">Who.</param>
    /// <returns>Their threat, or zero.</returns>
    public float ThreatOf(ulong attacker) {
        foreach (var entry in CollectionsMarshal.AsSpan(entries)) {
            if (entry.Attacker == attacker) {
                return entry.Threat;
            }
        }

        return 0f;
    }

    /// <summary>Who the creature should be attacking.</summary>
    /// <returns>Them, or zero when nobody is on the table.</returns>
    public ulong Target() => IsTaunted ? forced : entries.Count > 0 ? entries[0].Attacker : 0;

    /// <summary>Advances the taunt clock.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <returns>Whether a taunt ended on this step.</returns>
    public bool Tick(float delta) {
        if (forcedRemaining <= 0f) {
            return false;
        }

        forcedRemaining -= delta;

        if (forcedRemaining > 0f) {
            return false;
        }

        forced = 0;
        forcedRemaining = 0f;

        return true;
    }

    void Sort() =>
        entries.Sort(
            static (left, right) => right.Threat != left.Threat
                ? right.Threat.CompareTo(left.Threat)
                // ⚠ Ties break on the attacker's number rather than on list order, so two players
                // who have done identical damage do not swap the boss back and forth every time
                // either of them lands a hit. Doc 28's per-object light churn has the same shape.
                : left.Attacker.CompareTo(right.Attacker)
        );
}
