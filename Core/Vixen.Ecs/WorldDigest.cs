// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>
///     A hash of everything in a world, computed in an order that does not depend on how the world
///     got that way.
/// </summary>
/// <remarks>
///     <para>
///         Two worlds fed the same input must end up the same, and "the same" has to mean something
///         checkable. Entity rows move between chunks on every structural change and archetypes are
///         created in first-use order, so a naive walk hashes the history as much as the state. This
///         walks archetypes ordered by their component type <em>names</em> and rows ordered by entity
///         id, both of which are properties of the state alone.
///     </para>
///     <para>
///         Names, not <see cref="ComponentTypeId" />s: ids are handed out in first-touch order, so a
///         world that reached the same state by a different route would order its archetypes
///         differently and hash differently. That is the same reason a serialised world names its
///         component types by alias.
///     </para>
///     <para>
///         <b>Managed components are not hashed, and the count of them is.</b> Their bytes are a
///         handle into a per-world store, so hashing those would say two identical worlds differ;
///         hashing the values needs a serialiser per type, which is
///         [08](../../../docs/plan/08-asset-pipeline-and-addressables.md)'s problem. Including how
///         many there are keeps a difference in structure visible even when the contents are not.
///     </para>
/// </remarks>
public static class WorldDigest {
    const ulong Offset = 14695981039346656037;
    const ulong Prime = 1099511628211;

    /// <summary>Hashes a world's state.</summary>
    /// <param name="world">The world.</param>
    /// <returns>A 64-bit digest that is equal for equal states.</returns>
    public static ulong Compute(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var ordered = world.Archetypes
            .Where(archetype => archetype.EntityCount > 0)
            .OrderBy(Name, StringComparer.Ordinal)
            .ToArray();

        var hash = Offset;
        hash = Mix(hash, (ulong)world.EntityCount);
        hash = Mix(hash, (ulong)ordered.Length);

        var rows = new List<(int Id, Chunk Chunk, int Row)>();

        foreach (var archetype in ordered) {
            hash = MixText(hash, Name(archetype));
            hash = Mix(hash, (ulong)archetype.EntityCount);

            rows.Clear();

            foreach (var chunk in archetype.Chunks) {
                var entities = chunk.Entities;

                for (var row = 0; row < chunk.Count; row++) {
                    rows.Add((entities[row].Id, chunk, row));
                }
            }

            // By id, because a row's position within a chunk is a fact about the history of removals
            // and nothing about the state.
            rows.Sort(static (left, right) => left.Id.CompareTo(right.Id));

            foreach (var (id, chunk, row) in rows) {
                hash = Mix(hash, (ulong)id);

                for (var column = 0; column < archetype.ColumnCount; column++) {
                    if (ComponentRegistry.Get(archetype.ColumnIds[column]).IsManaged) {
                        hash = Mix(hash, 1);
                        continue;
                    }

                    foreach (var value in chunk.RawRow(column, row)) {
                        hash = Mix(hash, value);
                    }
                }
            }
        }

        return hash;
    }

    /// <summary>Whether two worlds are in the same state.</summary>
    /// <param name="left">One world.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether their digests agree.</returns>
    public static bool Agree(World left, World right) => Compute(left) == Compute(right);

    static string Name(Archetype archetype) =>
        string.Join(
            ',',
            archetype.Signature.Ids.ToArray().Select(id => ComponentRegistry.Get(id).Type.FullName).Order(StringComparer.Ordinal)
        );

    static ulong Mix(ulong hash, ulong value) => (hash ^ value) * Prime;

    static ulong MixText(ulong hash, string text) {
        foreach (var character in text) {
            hash = Mix(hash, character);
        }

        return hash;
    }
}
