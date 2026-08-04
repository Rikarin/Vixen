// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Animation.Moves;

/// <summary>A character's whole movement vocabulary: a flat list, and no hierarchy at all.</summary>
/// <remarks>
///     <para>
///         <b>Flat because a hierarchy forces an author to pick an axis to be primary, and there is
///         never one.</b> The moment a style is a container, every question crossing two axes needs a
///         nesting order — is the injured walk in snow inside <c>injured</c> or inside <c>snow</c>? —
///         and no answer is right. Facets have no order, so the question does not arise.
///     </para>
///     <para>
///         <b>One allocation, walked in order.</b> Selection touches every candidate, so the entries
///         are one array and the scan is a straight loop over it. That is worth more than any index
///         at the sizes a real set reaches, and it is why <see cref="Entries" /> is a span.
///     </para>
///     <para>
///         ⚠ <b>Overlays are resolved here, at build time, not at selection time.</b> What a frame
///         sees is one flat table with every override already applied. There is no inheritance chain
///         in memory, no fallback walk per query, and no way for a runtime bug to resolve an override
///         differently from the editor's preview of it.
///     </para>
/// </remarks>
public sealed class MoveSet {
    readonly MoveEntry[] entries;

    // The scan table: everything selection reads, flat and contiguous, so a pass over five hundred
    // candidates walks four arrays in order instead of chasing a pointer per entry into a FacetSet
    // and another into its facets. See the remark on Candidate.
    readonly ulong[] facetData;
    readonly int[] facetStart;
    readonly MoveTraits[] traits;
    readonly MoveKey[] keys;

    MoveSet(string name, MoveEntry[] entries) {
        Name = name;
        this.entries = entries;

        facetStart = new int[entries.Length + 1];
        traits = new MoveTraits[entries.Length];
        keys = new MoveKey[entries.Length];

        var total = 0;

        for (var index = 0; index < entries.Length; index++) {
            facetStart[index] = total;
            total += entries[index].Facets.Count;
            traits[index] = entries[index].Traits;
            keys[index] = entries[index].Key;
        }

        facetStart[entries.Length] = total;
        facetData = new ulong[total];

        var written = 0;

        foreach (var entry in entries) {
            foreach (var facet in entry.Facets.Facets) {
                facetData[written++] = facet.Packed;
            }
        }
    }

    /// <summary>What the set is called.</summary>
    public string Name { get; }

    /// <summary>How many moves it holds.</summary>
    public int Count => entries.Length;

    /// <summary>The moves, in key order.</summary>
    /// <returns>The moves.</returns>
    public ReadOnlySpan<MoveEntry> Entries => entries;

    /// <summary>One move by index.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The move.</returns>
    public MoveEntry this[int index] => entries[index];

    /// <summary>One candidate, as the selection pass reads it.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The candidate.</returns>
    /// <remarks>
    ///     ⚠ <b>This is what made the pass hit its budget, and the difference was 3×.</b> Reading a
    ///     candidate through <see cref="MoveEntry" /> means a dereference for the entry, one for its
    ///     <see cref="FacetSet" /> and one for that set's array — three chances to miss cache, five
    ///     hundred times, before any comparison happens. The flat table makes it four sequential
    ///     reads with the prefetcher on side.
    /// </remarks>
    public MoveCandidate Candidate(int index) =>
        new(
            index,
            keys[index],
            traits[index],
            facetData.AsSpan(facetStart[index], facetStart[index + 1] - facetStart[index]),
            entries[index]
        );

    /// <summary>Whether the move at an index says everything a query requires.</summary>
    /// <param name="index">Its position.</param>
    /// <param name="required">What it has to say.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Candidate" /> because most candidates fail it</b>, and
    ///     building the whole view first means paying for a window into the facet table, a traits
    ///     copy and a key for a move that is about to be thrown away. Measured: filtering through the
    ///     view cost 60 % more than filtering before it on a set where four in five are rejected.
    /// </remarks>
    public bool Matches(int index, FacetSet required) {
        ArgumentNullException.ThrowIfNull(required);

        if (required.Count == 0) {
            return true;
        }

        var from = facetStart[index];
        var to = facetStart[index + 1];

        if (required.Count > to - from) {
            return false;
        }

        foreach (var want in required.Packed) {
            while (from < to && facetData[from] < want) {
                from++;
            }

            if (from >= to || facetData[from] != want) {
                return false;
            }

            from++;
        }

        return true;
    }

    /// <summary>Builds a set from entries, and from the sets it overlays.</summary>
    /// <param name="name">What the set is called.</param>
    /// <param name="bases">
    ///     The sets it overlays, least specific first. Later ones win, and the entries passed in
    ///     <paramref name="own" /> win over all of them.
    /// </param>
    /// <param name="own">This set's own entries.</param>
    /// <returns>The composed set.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Two distinct words in the composed vocabulary hash alike.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>A list of bases, not a parent pointer.</b> A set can overlay a body-type set and a
    ///         personality set without either knowing the other exists, and the diamond has a defined
    ///         answer — later wins — rather than an error or a coin toss.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The symbol collision check runs over the composed vocabulary and nowhere else.</b>
    ///         Two words colliding matters only if they can meet, and composition is where they meet.
    ///         Checking a base on its own would pass and the composition would still be wrong.
    ///     </para>
    /// </remarks>
    public static MoveSet Compose(string name, IEnumerable<MoveSet>? bases, params ReadOnlySpan<MoveEntry> own) {
        ArgumentNullException.ThrowIfNull(name);

        Dictionary<MoveKey, MoveEntry> composed = [];

        if (bases is not null) {
            foreach (var layer in bases) {
                ArgumentNullException.ThrowIfNull(layer);

                foreach (var entry in layer.entries) {
                    composed[entry.Key] = entry;
                }
            }
        }

        foreach (var entry in own) {
            ArgumentNullException.ThrowIfNull(entry);
            composed[entry.Key] = entry;
        }

        var entries = composed.Values.ToArray();

        // Sorted by key, so two composed sets built from the same inputs in a different enumeration
        // order are byte-for-byte the same set — and so a tie broken by key order is stable.
        Array.Sort(entries, static (left, right) => left.Key.CompareTo(right.Key));

        Verify(name, entries);
        return new(name, entries);
    }

    /// <summary>Builds a set with no bases.</summary>
    /// <param name="name">What the set is called.</param>
    /// <param name="own">Its entries.</param>
    /// <returns>The set.</returns>
    public static MoveSet Of(string name, params ReadOnlySpan<MoveEntry> own) => Compose(name, null, own);

    /// <summary>The entry with a key, if the set has one.</summary>
    /// <param name="key">The key.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>Whether it is there.</returns>
    public bool TryGet(MoveKey key, out MoveEntry? entry) {
        var low = 0;
        var high = entries.Length - 1;

        while (low <= high) {
            var middle = low + ((high - low) / 2);
            var order = entries[middle].Key.CompareTo(key);

            if (order == 0) {
                entry = entries[middle];
                return true;
            }

            if (order < 0) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    ///     ⚠ Two different words hashing alike would silently become one word, so the composed
    ///     vocabulary is checked once and the build refuses rather than the game misbehaving.
    /// </summary>
    static void Verify(string name, MoveEntry[] entries) {
        foreach (var entry in entries) {
            foreach (var facet in entry.Facets.Facets) {
                Check(name, facet.Key);
                Check(name, facet.Value);
            }
        }
    }

    static void Check(string set, Symbol symbol) {
        if (!symbol.IsSome || !symbol.TryGetCollision(out var first, out var second)) {
            return;
        }

        throw new InvalidOperationException(
            $"In the move set '{set}', '{first}' and '{second}' hash to the same symbol "
            + $"(0x{symbol.Id:x8}), so every facet using one would also match the other. Rename one of them."
        );
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({entries.Length} moves)";
}
