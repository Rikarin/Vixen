// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>Where one instance is, as a thing that can be selected and moved.</summary>
/// <param name="Type">Which type it is, by palette index.</param>
/// <param name="Cell">Which cell holds it.</param>
/// <param name="Index">Where in that cell's chunk it sits.</param>
/// <remarks>
///     ⚠ <b>An address, not a reference, and it is only valid until the chunk changes.</b> Removing
///     an instance shifts the ones after it, so a selection held across an erase stroke points at
///     somebody else's tree. <see cref="FoliageVolume.Remove" /> takes addresses in descending index
///     order for exactly this reason, and the editor re-resolves a selection after any edit.
/// </remarks>
public readonly record struct FoliageAddress(int Type, FoliageCellKey Cell, int Index);

/// <summary>
///     Every instance in a scene, in its cells, with the palette they are instances of.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a <c>FoliageVolume</c> component names — [docs/plan/31 § What the scene sees].</b>
///         Stored beside the scene rather than inside it, for the merge-conflict reason a heightfield
///         is: a <c>.vxscene</c> is the file two people touch every day, and fifty thousand
///         transforms in it is a file nobody can merge.
///     </para>
///     <para>
///         ⚠ <b>Cells are created on demand and destroyed when they empty.</b> A world of a hundred
///         square kilometres has a hundred thousand cells and instances in a few hundred of them; a
///         dense grid would be the memory cost of a forest for a clearing.
///     </para>
/// </remarks>
public sealed class FoliageVolume {
    readonly Dictionary<(int Type, FoliageCellKey Cell), FoliageChunk> chunks = [];
    readonly List<FoliageType> palette = [];

    /// <summary>Creates an empty volume.</summary>
    /// <param name="grid">How big its cells are.</param>
    public FoliageVolume(FoliageCellGrid grid = default) {
        Grid = grid.Size > 0f ? grid : new();
    }

    /// <summary>How the world is divided.</summary>
    public FoliageCellGrid Grid { get; }

    /// <summary>The types instances here are of, in palette order.</summary>
    public IReadOnlyList<FoliageType> Palette => palette;

    /// <summary>Every chunk that holds anything.</summary>
    public IEnumerable<FoliageChunk> Chunks => chunks.Values;

    /// <summary>How many instances there are in total.</summary>
    public int InstanceCount {
        get {
            var total = 0;

            foreach (var chunk in chunks.Values) {
                total += chunk.Count;
            }

            return total;
        }
    }

    /// <summary>How many cells hold anything.</summary>
    public int CellCount => chunks.Count;

    /// <summary>Adds a type to the palette.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its index.</returns>
    public int AddType(FoliageType type) {
        palette.Add(type);
        return palette.Count - 1;
    }

    /// <summary>Changes what a palette entry is, keeping every instance of it.</summary>
    /// <param name="type">Which entry.</param>
    /// <param name="value">What it becomes.</param>
    /// <remarks>
    ///     What Reapply edits and what the palette's settings panel writes. The instances are not
    ///     touched: changing a type's scale range does not re-roll anything until somebody asks it to,
    ///     which is [§ The foliage tools]'s whole point about Reapply.
    /// </remarks>
    public void SetType(int type, FoliageType value) {
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, palette.Count);

        palette[type] = value;
    }

    /// <summary>How many instances of one type there are.</summary>
    /// <param name="type">Which type.</param>
    /// <returns>The count.</returns>
    public int CountOf(int type) {
        var total = 0;

        foreach (var ((chunkType, _), chunk) in chunks) {
            if (chunkType == type) {
                total += chunk.Count;
            }
        }

        return total;
    }

    /// <summary>The chunk holding a type's instances in a cell, or null if there are none.</summary>
    /// <param name="type">Which type.</param>
    /// <param name="cell">Which cell.</param>
    /// <returns>The chunk, or null.</returns>
    public FoliageChunk? ChunkOf(int type, FoliageCellKey cell) => chunks.GetValueOrDefault((type, cell));

    /// <summary>Places an instance, in whichever cell its position falls in.</summary>
    /// <param name="type">Which type.</param>
    /// <param name="instance">The instance.</param>
    /// <returns>Where it went.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such type.</exception>
    public FoliageAddress Add(int type, in FoliageInstance instance) {
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, palette.Count);

        var cell = Grid.CellOf(instance.Position);
        var key = (type, cell);

        if (!chunks.TryGetValue(key, out var chunk)) {
            chunk = new(cell, type);
            chunks[key] = chunk;
        }

        chunk.Add(instance, ReachOf(type));

        return new(type, cell, chunk.Count - 1);
    }

    /// <summary>Takes instances back out.</summary>
    /// <param name="addresses">Which ones.</param>
    /// <returns>How many went.</returns>
    /// <remarks>
    ///     ⚠ <b>Sorted descending within each chunk before anything is removed.</b> Removing index 3
    ///     shifts 4 and 5 down, so a caller handing over three ascending addresses would delete one
    ///     of them and two of somebody else's. Doing it here means the caller cannot get it wrong.
    /// </remarks>
    public int Remove(IEnumerable<FoliageAddress> addresses) {
        ArgumentNullException.ThrowIfNull(addresses);

        var removed = 0;

        foreach (var group in addresses.GroupBy(address => (address.Type, address.Cell))) {
            if (!chunks.TryGetValue(group.Key, out var chunk)) {
                continue;
            }

            foreach (var address in group.OrderByDescending(entry => entry.Index)) {
                if ((uint)address.Index < (uint)chunk.Count) {
                    chunk.RemoveAt(address.Index, ReachOf(group.Key.Type));
                    removed++;
                }
            }

            if (chunk.IsEmpty) {
                chunks.Remove(group.Key);
            }
        }

        return removed;
    }

    /// <summary>Moves, turns or resizes one instance.</summary>
    /// <param name="address">Which one.</param>
    /// <param name="instance">What it becomes.</param>
    /// <returns>Where it ended up, which is a different cell if it crossed one.</returns>
    /// <remarks>
    ///     ⚠ <b>A move across a cell boundary is a remove and an add, and the address changes.</b> A
    ///     gizmo drag that left the instance in its old cell would put a tree outside the bounds
    ///     everything culls that cell by — it would vanish when the cell went off screen and reappear
    ///     from nowhere.
    /// </remarks>
    public FoliageAddress Move(FoliageAddress address, in FoliageInstance instance) {
        var chunk = chunks.GetValueOrDefault((address.Type, address.Cell));

        if (chunk is null || (uint)address.Index >= (uint)chunk.Count) {
            return address;
        }

        var destination = Grid.CellOf(instance.Position);

        if (destination == address.Cell) {
            chunk.Replace(address.Index, instance, ReachOf(address.Type));
            return address;
        }

        chunk.RemoveAt(address.Index, ReachOf(address.Type));

        if (chunk.IsEmpty) {
            chunks.Remove((address.Type, address.Cell));
        }

        return Add(address.Type, instance);
    }

    /// <summary>What is at an address, or null if nothing is.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The instance.</returns>
    public FoliageInstance? At(FoliageAddress address) {
        var chunk = chunks.GetValueOrDefault((address.Type, address.Cell));

        return chunk is null || (uint)address.Index >= (uint)chunk.Count
            ? null
            : chunk.Instances[address.Index];
    }

    /// <summary>Every instance within a radius of a point, in world XZ.</summary>
    /// <param name="centre">The point.</param>
    /// <param name="radius">How far.</param>
    /// <param name="types">Which types count, or null for all of them.</param>
    /// <returns>Their addresses.</returns>
    /// <remarks>
    ///     What an erase stroke and a lasso both ask. The cell grid is what makes it cheap: a
    ///     four-metre brush over a hundred-square-kilometre world visits one cell.
    /// </remarks>
    public IEnumerable<FoliageAddress> Within(Vector2 centre, float radius, IReadOnlySet<int>? types = null) {
        var squared = radius * radius;

        foreach (var cell in Grid.Touching(centre, radius)) {
            for (var type = 0; type < palette.Count; type++) {
                if (types is not null && !types.Contains(type)) {
                    continue;
                }

                if (!chunks.TryGetValue((type, cell), out var chunk)) {
                    continue;
                }

                for (var index = 0; index < chunk.Count; index++) {
                    var position = chunk.Instances[index].Position;
                    var offset = new Vector2(position.X - centre.X, position.Z - centre.Y);

                    if (offset.LengthSquared() <= squared) {
                        yield return new(type, cell, index);
                    }
                }
            }
        }
    }

    /// <summary>Empties every cell, keeping the palette.</summary>
    public void Clear() => chunks.Clear();

    /// <summary>Empties one type's cells.</summary>
    /// <param name="type">Which type.</param>
    /// <returns>How many instances went.</returns>
    public int ClearType(int type) {
        var removed = 0;

        foreach (var key in chunks.Keys.Where(key => key.Type == type).ToArray()) {
            removed += chunks[key].Count;
            chunks.Remove(key);
        }

        return removed;
    }

    /// <summary>Removes a palette entry, and every instance of it, renumbering the rest.</summary>
    /// <param name="type">Which entry.</param>
    /// <returns>How many instances went with it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such entry.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>[docs/plan/31 § T5] registered this as unavailable with a reason, and the reason is
    ///         what this method has to do about it.</b> A palette index is not a name: it is a
    ///         position, and removing the second of six shifts the four above it down by one. Every
    ///         chunk is filed under one, so every chunk above the removal has to be re-filed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this cannot renumber is what it cannot see.</b> A
    ///         <see cref="FoliageAddress" /> a caller is holding, a selection, and every undo entry on
    ///         a stack all name a type by index and none of them is here. A caller with any of those
    ///         has to rebuild them — which is why the editor's version of this is a command that
    ///         clears the selection and why it does not merge with anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Re-filed into a fresh dictionary rather than edited in place.</b> Shifting a key
    ///         from 3 to 2 while 2 still exists collides; doing it in ascending order collides with
    ///         the entry being moved next, and in descending order collides with the one just moved.
    ///         There is no safe order, which is the same shape as the address trap one level down.
    ///     </para>
    /// </remarks>
    public int RemoveType(int type) {
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, palette.Count);

        var removed = 0;
        var moved = new Dictionary<(int Type, FoliageCellKey Cell), FoliageChunk>(chunks.Count);

        foreach (var (key, chunk) in chunks) {
            if (key.Type == type) {
                removed += chunk.Count;
                continue;
            }

            var renumbered = key.Type > type ? key.Type - 1 : key.Type;
            var replacement = new FoliageChunk(key.Cell, renumbered);

            foreach (var instance in chunk.Instances) {
                replacement.Add(instance, ReachOf(key.Type));
            }

            moved[(renumbered, key.Cell)] = replacement;
        }

        palette.RemoveAt(type);
        chunks.Clear();

        foreach (var (key, chunk) in moved) {
            chunks[key] = chunk;
        }

        return removed;
    }

    /// <summary>How far a type's mesh reaches from its origin at unit scale.</summary>
    /// <remarks>
    ///     Taken from the spacing radius, because this assembly has no mesh and cannot ask one how
    ///     big it is. It is the honest approximation: a type whose instances may not stand closer
    ///     than two metres is a type roughly two metres across, and a cell's bounds only have to be
    ///     conservative rather than tight.
    /// </remarks>
    float ReachOf(int type) => type < palette.Count ? MathF.Max(palette[type].Radius, 0.5f) : 1f;
}
