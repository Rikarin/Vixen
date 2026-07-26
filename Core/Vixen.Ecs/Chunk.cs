// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>
///     One block of entities that all have exactly the same components, stored column by column.
/// </summary>
/// <remarks>
///     <para>
///         Sixteen kilobytes by default, which is what fits comfortably in L1 on every target after
///         the entity column is accounted for, and what Arch and DOTS both converged on. A system
///         that reads two of an archetype's eight components touches only those two columns, so the
///         bytes the cache pulls in are bytes the loop uses — which is the entire reason this
///         layout exists.
///     </para>
///     <para>
///         The whole chunk is one <c>byte[]</c>: the entity column first, then one column per
///         non-tag component, each aligned. Tags occupy no column, only a bit in the archetype's
///         mask.
///     </para>
/// </remarks>
public sealed class Chunk {
    internal readonly byte[] Buffer;

    /// <summary>The archetype every entity in here belongs to.</summary>
    public Archetype Archetype { get; }

    /// <summary>How many entities are in the chunk.</summary>
    public int Count { get; internal set; }

    /// <summary>How many it has room for.</summary>
    public int Capacity => Archetype.ChunkCapacity;

    /// <summary>Whether there is no room for another entity.</summary>
    public bool IsFull => Count >= Capacity;

    /// <summary>
    ///     The world version at which each column was last written, indexed the same way as
    ///     <see cref="Archetype.ColumnOf" />.
    /// </summary>
    internal readonly uint[] Versions;

    internal Chunk(Archetype archetype) {
        Archetype = archetype;
        Buffer = GC.AllocateUninitializedArray<byte>(archetype.ChunkBytes);
        Versions = new uint[archetype.ColumnCount];

        // Uninitialised because every row is written before it is read: Allocate writes the entity
        // and the caller writes or zeroes each component. Handing out uninitialised component
        // memory would be the bug this comment exists to warn about, so `Allocate` clears the row.
    }

    /// <summary>The entities in the chunk, in row order.</summary>
    public ReadOnlySpan<Entity> Entities =>
        MemoryMarshal.Cast<byte, Entity>(Buffer.AsSpan(0, Count * Unsafe.SizeOf<Entity>()));

    internal Span<Entity> EntitySlots =>
        MemoryMarshal.Cast<byte, Entity>(Buffer.AsSpan(0, Capacity * Unsafe.SizeOf<Entity>()));

    /// <summary>
    ///     The live part of one component column, typed. The caller has already checked that the
    ///     archetype has the component and that <typeparamref name="T" /> is what lives there; this
    ///     does neither.
    /// </summary>
    /// <typeparam name="T">The component type, or <see cref="int" /> for a managed column's handles.</typeparam>
    /// <param name="column">Its column index in this archetype.</param>
    /// <returns>One element per entity in the chunk.</returns>
    /// <remarks>
    ///     Reinterpreted rather than <c>MemoryMarshal.Cast</c>'d because the component type
    ///     parameter cannot be constrained to <c>struct</c>: a managed component is often a class,
    ///     and the same generic method has to reach both. The branch that decides which is a test on
    ///     a <c>static readonly bool</c>, so the JIT folds it away and the unsound arm is never
    ///     reached with a reference type. The assert is what keeps that true while it is being
    ///     edited.
    /// </remarks>
    internal Span<T> Column<T>(int column) {
        Debug.Assert(
            !RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
            "A chunk column holds no references; a managed component's column holds int handles."
        );

        ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Buffer), Archetype.Offsets[column]);
        return MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref start), Count);
    }

    /// <summary>A reference to one component, without materialising a span for the column.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="column">Its column index.</param>
    /// <param name="row">The entity's row.</param>
    /// <returns>A reference into the chunk.</returns>
    internal ref T At<T>(int column, int row) {
        Debug.Assert(
            !RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
            "A chunk column holds no references; a managed component's column holds int handles."
        );

        ref var start = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(Buffer),
            Archetype.Offsets[column] + (row * Archetype.Sizes[column])
        );

        return ref Unsafe.As<byte, T>(ref start);
    }

    internal Span<byte> RawRow(int column, int row) {
        var size = Archetype.Sizes[column];
        return Buffer.AsSpan(Archetype.Offsets[column] + (row * size), size);
    }

    /// <summary>The version at which a column was last handed out for writing.</summary>
    /// <param name="column">The column index.</param>
    /// <returns>The world version.</returns>
    public uint VersionOf(int column) => Versions[column];

    internal void MarkWritten(int column, uint version) => Versions[column] = version;

    /// <summary>Takes the next free row, clears its components and writes the entity handle.</summary>
    internal int Allocate(Entity entity) {
        var row = Count++;
        EntitySlots[row] = entity;
        ClearRow(row);
        return row;
    }

    internal void ClearRow(int row) {
        for (var column = 0; column < Archetype.ColumnCount; column++) {
            RawRow(column, row).Clear();
        }
    }

    /// <summary>
    ///     Removes a row by moving the last one into it.
    /// </summary>
    /// <param name="row">The row to free.</param>
    /// <returns>
    ///     The entity that moved into <paramref name="row" />, or <see cref="Entity.Null" /> if the
    ///     removed row was the last one and nothing moved.
    /// </returns>
    /// <remarks>
    ///     Swap-back rather than shift-down, so removal is O(components) instead of O(entities).
    ///     The cost is that row order within a chunk is not creation order — which is why nothing in
    ///     the engine may depend on it, and why iteration order is documented as unspecified.
    /// </remarks>
    internal Entity RemoveRow(int row) {
        var last = --Count;

        if (row == last) {
            return Entity.Null;
        }

        var moved = EntitySlots[last];
        EntitySlots[row] = moved;

        for (var column = 0; column < Archetype.ColumnCount; column++) {
            RawRow(column, last).CopyTo(RawRow(column, row));
        }

        return moved;
    }

    /// <summary>Copies the columns the two archetypes share; the rest of the target row is untouched.</summary>
    internal void CopySharedColumnsTo(int row, Chunk target, int targetRow) {
        var source = Archetype;
        var destination = target.Archetype;

        for (var column = 0; column < source.ColumnCount; column++) {
            var targetColumn = destination.ColumnOf(source.ColumnIds[column]);

            if (targetColumn >= 0) {
                RawRow(column, row).CopyTo(target.RawRow(targetColumn, targetRow));
            }
        }
    }
}
