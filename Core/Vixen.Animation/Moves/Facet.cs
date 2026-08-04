// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Animation.Moves;

/// <summary>One descriptive fact about a move: <c>gait=walk</c>, <c>condition=injured</c>.</summary>
/// <param name="Key">What is being said about it.</param>
/// <param name="Value">What is said.</param>
/// <remarks>
///     <b>A pair rather than a bare tag</b>, because almost every fact has an axis and a value, and a
///     flat tag set forces the axis into the spelling — <c>gait_walk</c>, <c>gait_run</c> — where
///     nothing can ask "what gait is this?" without string surgery. The pair makes that a lookup.
/// </remarks>
public readonly record struct Facet(Symbol Key, Symbol Value) : IComparable<Facet> {
    /// <summary>Interns both halves.</summary>
    /// <param name="key">The axis.</param>
    /// <param name="value">The value.</param>
    /// <returns>The facet.</returns>
    public static Facet Of(string key, string value) => new(Symbol.Intern(key), Symbol.Intern(value));

    /// <summary>The two halves in one number, ordered key-major.</summary>
    /// <remarks>
    ///     ⚠ <b>Composed explicitly rather than by reinterpreting the struct.</b> Casting the layout
    ///     to a <c>ulong</c> would be free and would order correctly on a little-endian machine and
    ///     backwards on a big-endian one — and a set sorted one way and searched the other finds
    ///     nothing, which is the kind of failure that only appears on the one platform nobody tests.
    /// </remarks>
    public ulong Packed => ((ulong)Key.Id << 32) | Value.Id;

    /// <inheritdoc />
    public int CompareTo(Facet other) => Packed.CompareTo(other.Packed);

    /// <inheritdoc />
    public override string ToString() => $"{Key}={Value}";

    /// <summary>Orders two facets.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts first.</returns>
    public static bool operator <(Facet left, Facet right) => left.CompareTo(right) < 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator <=(Facet left, Facet right) => left.CompareTo(right) <= 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >(Facet left, Facet right) => left.CompareTo(right) > 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >=(Facet left, Facet right) => left.CompareTo(right) >= 0;
}

/// <summary>A facet a query would like, and how much it is worth.</summary>
/// <param name="Facet">The fact.</param>
/// <param name="Weight">What matching it contributes to the score.</param>
/// <remarks>
///     ⚠ <b>A negative weight is legal and useful.</b> "Anything but a crouch" is a preference, not a
///     prohibition, and expressing it as a hard requirement on the absence of a facet would make the
///     query fail entirely on a set where every candidate crouches.
/// </remarks>
public readonly record struct WeightedFacet(Facet Facet, float Weight);

/// <summary>What a move says about itself: a sorted run of facets, matched by walking it.</summary>
/// <remarks>
///     <para>
///         <b>Sorted, so matching two sets is a merge and not a search.</b> A query's required set and
///         a candidate's set are both ordered on <c>(key, value)</c>, so testing containment walks
///         both once — linear in the smaller, no hashing, no allocation, and entirely predictable,
///         which matters because selection touches every candidate in the set.
///     </para>
///     <para>
///         <b>Immutable.</b> A set is shared by every character playing the move it describes, and
///         building one is a bake-time cost nobody should pay in a frame.
///     </para>
/// </remarks>
public sealed class FacetSet {
    readonly Facet[] facets;

    /// <summary>
    ///     The same facets as one number each, which is what the hot paths actually scan.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A second array rather than a binary search, and it was worth 3× on the selection
    ///     pass.</b> <c>Array.BinarySearch</c> goes through <c>Comparer&lt;Facet&gt;</c>, which is an
    ///     interface call per comparison that does not inline — about forty nanoseconds a candidate
    ///     against a budget of ten. A set holds two to six facets, so a straight scan over packed
    ///     integers beats a search outright: no indirection, no mispredicted branch, one cache line.
    /// </remarks>
    readonly ulong[] packed;

    FacetSet(Facet[] sorted) {
        facets = sorted;
        packed = new ulong[sorted.Length];

        for (var index = 0; index < sorted.Length; index++) {
            packed[index] = sorted[index].Packed;
        }
    }

    /// <summary>The set that says nothing.</summary>
    public static FacetSet Empty { get; } = new([]);

    /// <summary>How many facets it holds.</summary>
    public int Count => facets.Length;

    /// <summary>The facets, in order.</summary>
    /// <returns>The facets.</returns>
    public ReadOnlySpan<Facet> Facets => facets;

    /// <summary>The same facets as one number each, sorted. What a matching pass reads.</summary>
    /// <returns>The packed facets.</returns>
    public ReadOnlySpan<ulong> Packed => packed;

    /// <summary>Builds a set, sorting and de-duplicating.</summary>
    /// <param name="source">The facets, in any order.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     ⚠ <b>Two values on one key are both kept.</b> <c>surface=ice</c> and <c>surface=snow</c>
    ///     on the same move is a move that suits either, which is a thing an author means often
    ///     enough that collapsing it to the last one would be silently wrong.
    /// </remarks>
    public static FacetSet Of(params ReadOnlySpan<Facet> source) {
        if (source.Length == 0) {
            return Empty;
        }

        var sorted = source.ToArray();
        Array.Sort(sorted);

        var written = 0;

        for (var index = 0; index < sorted.Length; index++) {
            if (index > 0 && sorted[index] == sorted[index - 1]) {
                continue;
            }

            sorted[written++] = sorted[index];
        }

        return new(written == sorted.Length ? sorted : sorted[..written]);
    }

    /// <summary>Builds a set from <c>key=value</c> pairs.</summary>
    /// <param name="pairs">The pairs.</param>
    /// <returns>The set.</returns>
    public static FacetSet Of(params ReadOnlySpan<(string Key, string Value)> pairs) {
        if (pairs.Length == 0) {
            return Empty;
        }

        var facets = new Facet[pairs.Length];

        for (var index = 0; index < pairs.Length; index++) {
            facets[index] = Facet.Of(pairs[index].Key, pairs[index].Value);
        }

        return Of(facets.AsSpan());
    }

    /// <summary>Whether the set says this.</summary>
    /// <param name="facet">The fact.</param>
    /// <returns>Whether it is there.</returns>
    public bool Contains(Facet facet) {
        var wanted = facet.Packed;

        foreach (var held in packed) {
            if (held == wanted) {
                return true;
            }

            // Sorted, so anything past the target settles it without reading the rest.
            if (held > wanted) {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether the set says everything another one does.</summary>
    /// <param name="required">The facts that have to be there.</param>
    /// <returns>Whether they all are.</returns>
    /// <remarks>
    ///     A merge over two sorted runs. The early exit matters: a candidate missing the first
    ///     required facet costs one comparison, and most candidates in a real set miss it.
    /// </remarks>
    public bool ContainsAll(FacetSet required) {
        ArgumentNullException.ThrowIfNull(required);

        if (required.Count == 0) {
            return true;
        }

        if (required.Count > facets.Length) {
            return false;
        }

        var mine = 0;
        var ours = packed;

        foreach (var want in required.packed) {
            while (mine < ours.Length && ours[mine] < want) {
                mine++;
            }

            if (mine >= ours.Length || ours[mine] != want) {
                return false;
            }

            mine++;
        }

        return true;
    }

    /// <summary>The value this set gives a key, if it gives one.</summary>
    /// <param name="key">The axis.</param>
    /// <param name="value">The first value on that axis, in symbol order.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryGet(Symbol key, out Symbol value) {
        foreach (var facet in facets) {
            if (facet.Key != key) {
                continue;
            }

            value = facet.Value;
            return true;
        }

        value = Symbol.None;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => facets.Length == 0 ? "{}" : $"{{{string.Join(", ", facets)}}}";
}
