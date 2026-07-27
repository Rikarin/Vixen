// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>
///     A sorted, duplicate-free set of component type ids — what makes one archetype that archetype.
/// </summary>
/// <remarks>
///     Sorted so that two entities built by adding the same components in different orders land in
///     the same archetype, and so that a serialised world walks its columns in an order that does
///     not depend on how the entities were authored.
/// </remarks>
public readonly struct ComponentSignature : IEquatable<ComponentSignature> {
    readonly ComponentTypeId[] ids;
    readonly int hash;

    /// <summary>The ids, ascending.</summary>
    public ReadOnlySpan<ComponentTypeId> Ids => ids;

    /// <summary>How many component types are in the set.</summary>
    public int Count => ids?.Length ?? 0;

    /// <summary>The set with nothing in it — the archetype a bare entity starts in.</summary>
    public static ComponentSignature Empty { get; } = new([]);

    /// <summary>Wraps an already-sorted, duplicate-free array. The array is not copied.</summary>
    /// <param name="sorted">The ids, ascending.</param>
    internal ComponentSignature(ComponentTypeId[] sorted) {
        ids = sorted;

        var accumulator = new HashCode();

        foreach (var id in sorted) {
            accumulator.Add(id.Value);
        }

        hash = accumulator.ToHashCode();
    }

    /// <summary>Builds a signature from ids in any order, sorting and de-duplicating them.</summary>
    /// <param name="unsorted">The ids.</param>
    /// <returns>The signature.</returns>
    public static ComponentSignature Of(ReadOnlySpan<ComponentTypeId> unsorted) {
        var sorted = unsorted.ToArray();
        Array.Sort(sorted);

        var written = 0;

        for (var index = 0; index < sorted.Length; index++) {
            if (index == 0 || sorted[index] != sorted[index - 1]) {
                sorted[written++] = sorted[index];
            }
        }

        return new(written == sorted.Length ? sorted : sorted[..written]);
    }

    /// <summary>Whether the set contains an id.</summary>
    /// <param name="id">The id to look for.</param>
    /// <returns>Whether it is in the set.</returns>
    public bool Contains(ComponentTypeId id) => Array.BinarySearch(ids, id) >= 0;

    /// <summary>The set with one more id in it, or this set if it was already there.</summary>
    /// <param name="id">The id to add.</param>
    /// <returns>The resulting set.</returns>
    public ComponentSignature With(ComponentTypeId id) {
        var at = Array.BinarySearch(ids, id);

        if (at >= 0) {
            return this;
        }

        at = ~at;
        var grown = new ComponentTypeId[ids.Length + 1];
        Array.Copy(ids, grown, at);
        grown[at] = id;
        Array.Copy(ids, at, grown, at + 1, ids.Length - at);
        return new(grown);
    }

    /// <summary>The set with one id taken out, or this set if it was not there.</summary>
    /// <param name="id">The id to remove.</param>
    /// <returns>The resulting set.</returns>
    public ComponentSignature Without(ComponentTypeId id) {
        var at = Array.BinarySearch(ids, id);

        if (at < 0) {
            return this;
        }

        var shrunk = new ComponentTypeId[ids.Length - 1];
        Array.Copy(ids, shrunk, at);
        Array.Copy(ids, at + 1, shrunk, at, ids.Length - at - 1);
        return new(shrunk);
    }

    /// <inheritdoc />
    public bool Equals(ComponentSignature other) =>
        hash == other.hash && Ids.SequenceEqual(other.Ids);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ComponentSignature other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => hash;

    /// <summary>Whether two signatures name the same set.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(ComponentSignature left, ComponentSignature right) => left.Equals(right);

    /// <summary>Whether two signatures name different sets.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(ComponentSignature left, ComponentSignature right) => !left.Equals(right);

    /// <summary>Renders the set as the component type names it holds.</summary>
    /// <returns>The set in text.</returns>
    public override string ToString() =>
        Count == 0 ? "{}" : "{" + string.Join(", ", ids.Select(id => ComponentRegistry.Get(id).Type.Name)) + "}";
}
