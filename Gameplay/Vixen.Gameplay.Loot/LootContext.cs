// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay.Loot;

/// <summary>What a row's conditions are evaluated against: the kill, not the killer's stats.</summary>
/// <remarks>
///     <b>An <see cref="IRequirementContext" /> like everything else</b>, so "only on Heroic", "only
///     above level 60" and "only in Queensdale" are the same requirement algebra a vendor and an
///     ability use. The tags are whatever the caller thinks is true of the drop — the victim's tags,
///     the zone's, the difficulty's — and the values are named numbers it supplies.
/// </remarks>
public sealed class LootContext : IRequirementContext {
    readonly Dictionary<uint, float> values = [];

    /// <summary>Makes a context.</summary>
    /// <param name="tags">What is true of the kill, or null for nothing.</param>
    public LootContext(GameplayTagSet? tags = null) => Tags = tags;

    /// <summary>A context with no tags and no values. Every unconditional row is in.</summary>
    public static LootContext Empty { get; } = new();

    /// <inheritdoc />
    public GameplayTagSet? Tags { get; }

    /// <summary>Supplies a named number a condition can compare against.</summary>
    /// <param name="name">Its name — <c>Level</c>, <c>Difficulty</c>.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The context, so values chain.</returns>
    public LootContext With(string name, float value) {
        values[AttributeId.From(name).Value] = value;

        return this;
    }

    /// <inheritdoc />
    public bool TryGetValue(AttributeId subject, out float value) => values.TryGetValue(subject.Value, out value);
}

/// <summary>Whose run of bad luck, on which table.</summary>
/// <param name="Player">Whoever is unlucky, as the caller numbers them.</param>
/// <param name="Table">Which table.</param>
/// <remarks>
///     ⚠ <b>Per (player, table), which is doc 28's key and not the obvious one.</b> Keying per row
///     would let a player bank misses on a table they never intend to farm and cash them in
///     elsewhere; keying per player alone would make one unlucky raid night guarantee a drop from a
///     different boss.
/// </remarks>
public readonly record struct PityKey(ulong Player, DefId Table) {
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"player {Player} on {Table}");
}

/// <summary>Where a run of bad luck is remembered.</summary>
/// <remarks>
///     An interface, because doc 28 requires this to be durable — "a pity counter that resets on a
///     realm crash is a support ticket" — and durable means a grain, which
///     <c>Gameplay/</c> may not reference. The realm supplies one backed by
///     <c>Vixen.Live.Persistence</c>; a test and an editor simulation supply
///     <see cref="MemoryPityStore" />.
/// </remarks>
public interface IPityStore {
    /// <summary>How many attempts have failed in a row.</summary>
    /// <param name="key">Whose, on what.</param>
    /// <returns>The count.</returns>
    int AttemptsOf(PityKey key);

    /// <summary>Records an attempt.</summary>
    /// <param name="key">Whose, on what.</param>
    /// <param name="hit">Whether it dropped, which resets the count.</param>
    void Record(PityKey key, bool hit);
}

/// <summary>A pity store that forgets when the process does. For a test and an editor preview.</summary>
public sealed class MemoryPityStore : IPityStore {
    readonly Dictionary<PityKey, int> attempts = [];

    /// <inheritdoc />
    public int AttemptsOf(PityKey key) => attempts.GetValueOrDefault(key);

    /// <inheritdoc />
    public void Record(PityKey key, bool hit) {
        if (hit) {
            attempts.Remove(key);
        } else {
            attempts[key] = attempts.GetValueOrDefault(key) + 1;
        }
    }
}
