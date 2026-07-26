// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core;
using Vixen.Core.Collections;

namespace Vixen.Ecs;

/// <summary>
///     Every entity that has exactly one particular set of components, and the chunks holding them.
/// </summary>
/// <remarks>
///     <para>
///         An archetype is created the first time an entity needs it and lives as long as the world,
///         because a query caches the archetypes it matched and a mask taken before one disappeared
///         would start meaning something else.
///     </para>
///     <para>
///         The add/remove edges are the single biggest difference between a naive archetype ECS and
///         a good one: <c>Add&lt;Velocity&gt;</c> on an entity that already has <c>Position</c> is a
///         dictionary hit on this archetype, not a hash of a recomputed type set.
///     </para>
/// </remarks>
public sealed class Archetype {
    /// <summary>How large a chunk is, before an archetype too wide to fit one entity overrides it.</summary>
    public const int ChunkBudget = 16 * 1024;

    static readonly int EntitySize = Unsafe.SizeOf<Entity>();

    internal readonly int[] Offsets;
    internal readonly int[] Sizes;
    internal readonly ComponentTypeId[] ColumnIds;

    readonly int[] columnOf;
    readonly int columnOfBase;
    readonly List<Chunk> chunks = [];
    readonly Dictionary<ComponentTypeId, Archetype> addEdges = [];
    readonly Dictionary<ComponentTypeId, Archetype> removeEdges = [];

    /// <summary>The component types every entity here has, sorted.</summary>
    public ComponentSignature Signature { get; }

    /// <summary>The same set as a bit per component id, which is what a query tests against.</summary>
    public BitSet Mask { get; }

    /// <summary>How many entities are in the archetype.</summary>
    public int EntityCount { get; private set; }

    /// <summary>How many components have chunk storage. Tags do not, so this can be less than the signature.</summary>
    public int ColumnCount => ColumnIds.Length;

    /// <summary>How many entities one chunk holds.</summary>
    public int ChunkCapacity { get; }

    /// <summary>How many bytes one chunk is.</summary>
    public int ChunkBytes { get; }

    /// <summary>The chunks, in no meaningful order.</summary>
    public IReadOnlyList<Chunk> Chunks => chunks;

    /// <summary>The world this belongs to. An archetype is never shared between worlds.</summary>
    public World World { get; }

    internal Archetype(World world, ComponentSignature signature) {
        World = world;
        Signature = signature;
        Mask = new(64);

        var columns = new List<ComponentTypeId>(signature.Count);

        foreach (var id in signature.Ids) {
            Mask.Set(id.Value);

            if (!ComponentRegistry.Get(id).IsTag) {
                columns.Add(id);
            }
        }

        ColumnIds = [.. columns];
        Sizes = new int[ColumnIds.Length];
        Offsets = new int[ColumnIds.Length];

        var stride = EntitySize;
        var slack = 0;

        for (var column = 0; column < ColumnIds.Length; column++) {
            var type = ComponentRegistry.Get(ColumnIds[column]);
            Sizes[column] = type.Size;
            stride += type.Size;
            slack += type.Alignment - 1;
        }

        // Budget for the worst case the alignment padding can cost rather than searching for the
        // largest capacity that fits. The overshoot is at most fifteen bytes per column out of
        // sixteen kilobytes, and the alternative is a loop whose termination is a proof obligation.
        ChunkCapacity = Math.Max(1, (ChunkBudget - slack) / stride);

        var offset = EntitySize * ChunkCapacity;

        for (var column = 0; column < ColumnIds.Length; column++) {
            var alignment = ComponentRegistry.Get(ColumnIds[column]).Alignment;
            offset = (offset + alignment - 1) & ~(alignment - 1);
            Offsets[column] = offset;
            offset += Sizes[column] * ChunkCapacity;
        }

        // One entity that does not fit the budget gets a chunk that does fit it. A component large
        // enough to hit this is a design mistake in the caller's data, but silently refusing to
        // store it would be a worse one.
        ChunkBytes = offset;

        if (ColumnIds.Length == 0) {
            columnOf = [];
            columnOfBase = 0;
        } else {
            // Indexed by component id, offset by the smallest id present so an archetype holding
            // ids 300 and 301 costs two slots and not three hundred.
            columnOfBase = ColumnIds[0].Value;
            columnOf = new int[ColumnIds[^1].Value - columnOfBase + 1];
            Array.Fill(columnOf, -1);

            for (var column = 0; column < ColumnIds.Length; column++) {
                columnOf[ColumnIds[column].Value - columnOfBase] = column;
            }
        }
    }

    /// <summary>The chunk column a component occupies here, or -1 if it is a tag or not present.</summary>
    /// <param name="id">The component type id.</param>
    /// <returns>The column index, or -1.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnOf(ComponentTypeId id) {
        var index = id.Value - columnOfBase;
        return (uint)index < (uint)columnOf.Length ? columnOf[index] : -1;
    }

    /// <summary>Whether an entity here has the component, including tags.</summary>
    /// <param name="id">The component type id.</param>
    /// <returns>Whether the archetype includes it.</returns>
    public bool Has(ComponentTypeId id) => Mask[id.Value];

    internal Archetype? AddEdge(ComponentTypeId id) => addEdges.GetValueOrDefault(id);

    internal Archetype? RemoveEdge(ComponentTypeId id) => removeEdges.GetValueOrDefault(id);

    internal void LinkAdd(ComponentTypeId id, Archetype target) {
        addEdges[id] = target;
        target.removeEdges[id] = this;
    }

    internal void LinkRemove(ComponentTypeId id, Archetype target) {
        removeEdges[id] = target;
        target.addEdges[id] = this;
    }

    internal (Chunk Chunk, int Row) Allocate(Entity entity) {
        var chunk = chunks.Count > 0 && !chunks[^1].IsFull ? chunks[^1] : NewChunk();
        EntityCount++;
        return (chunk, chunk.Allocate(entity));
    }

    Chunk NewChunk() {
        // Appended last and always allocated from last, so the tail chunk is the only partly-filled
        // one until a removal punches a hole — see Release, which keeps that invariant.
        var chunk = new Chunk(this);
        chunks.Add(chunk);
        return chunk;
    }

    /// <summary>
    ///     Frees a row and reports which entity, if any, was moved into it.
    /// </summary>
    internal Entity Release(Chunk chunk, int row) {
        EntityCount--;

        // Fill from the tail chunk rather than only within this one, so the archetype stays packed
        // into as few chunks as it needs. Without this, a world that creates and destroys in waves
        // ends up iterating a long list of nearly-empty chunks for ever.
        var tail = chunks[^1];

        if (!ReferenceEquals(tail, chunk)) {
            var lastRow = tail.Count - 1;
            var moved = tail.Entities[lastRow];
            tail.CopySharedColumnsTo(lastRow, chunk, row);
            chunk.EntitySlots[row] = moved;
            tail.Count--;

            DropEmptyTail();
            return moved;
        }

        var swapped = chunk.RemoveRow(row);
        DropEmptyTail();
        return swapped;
    }

    /// <summary>Drops every entity, and every chunk but the first.</summary>
    internal void Clear() {
        EntityCount = 0;

        if (chunks.Count > 0) {
            chunks.RemoveRange(1, chunks.Count - 1);
            chunks[0].Count = 0;
        }
    }

    void DropEmptyTail() {
        // Keep one chunk once the archetype has had entities: an archetype that oscillates around a
        // chunk boundary would otherwise allocate and free sixteen kilobytes every frame.
        while (chunks.Count > 1 && chunks[^1].Count == 0) {
            chunks.RemoveAt(chunks.Count - 1);
        }
    }

    /// <summary>Renders the component set, which is what a diagnostic wants to see.</summary>
    /// <returns>The archetype in text.</returns>
    public override string ToString() => $"{Signature} ×{EntityCount}";
}
