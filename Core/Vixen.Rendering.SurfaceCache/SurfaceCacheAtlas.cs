// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>Where each card's texels live — shelf allocation over one fixed rectangle.</summary>
/// <remarks>
///     <para>
///         Doc 19 § 6's "texture atlas allocation and residency", CPU half. Shelves because cards
///         are rectangles of a handful of quantised sizes: each shelf is a row as tall as the first
///         card that opened it, filled left to right. <b>Released rectangles go to a free list and
///         are reused on exact size match only</b> — cards recur at the sizes their meshes generate,
///         so the exact match is the common case, and a general best-fit packer is an optimisation
///         with this as its baseline.
///     </para>
///     <para>
///         Running out is not an error, for the brick pool's reason: a scene with more cards than
///         atlas is a quality reduction — the refused card simply stays uncached and its surfaces
///         answer black through the tracers — not a failure.
///     </para>
/// </remarks>
public sealed class SurfaceCacheAtlas {
    readonly List<(Int2 Origin, Int2 Size)> free = [];
    readonly List<(int Y, int Height, int Cursor)> shelves = [];

    int nextShelfY;

    /// <summary>Builds an empty atlas.</summary>
    /// <param name="size">Its size, in texels.</param>
    /// <exception cref="ArgumentOutOfRangeException">An empty atlas.</exception>
    public SurfaceCacheAtlas(Int2 size) {
        ArgumentOutOfRangeException.ThrowIfLessThan(size.X, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(size.Y, 1);

        Size = size;
    }

    /// <summary>The atlas's size, in texels.</summary>
    public Int2 Size { get; }

    /// <summary>How many texels have been handed out and not released.</summary>
    public int Occupied { get; private set; }

    /// <summary>Finds room for one card's texels.</summary>
    /// <param name="size">The card's resolution.</param>
    /// <param name="origin">Where its texels start.</param>
    /// <returns>False when the atlas has no room, which is a budget and not an error.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An empty or oversized request.</exception>
    public bool TryAllocate(Int2 size, out Int2 origin) {
        ArgumentOutOfRangeException.ThrowIfLessThan(size.X, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(size.Y, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size.X, Size.X);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size.Y, Size.Y);

        for (var index = 0; index < free.Count; index++) {
            if (free[index].Size == size) {
                origin = free[index].Origin;
                free.RemoveAt(index);
                Occupied += size.X * size.Y;

                return true;
            }
        }

        for (var index = 0; index < shelves.Count; index++) {
            var shelf = shelves[index];

            if (shelf.Height >= size.Y && shelf.Cursor + size.X <= Size.X) {
                origin = new(shelf.Cursor, shelf.Y);
                shelves[index] = (shelf.Y, shelf.Height, shelf.Cursor + size.X);
                Occupied += size.X * size.Y;

                return true;
            }
        }

        if (nextShelfY + size.Y <= Size.Y) {
            origin = new(0, nextShelfY);
            shelves.Add((nextShelfY, size.Y, size.X));
            nextShelfY += size.Y;
            Occupied += size.X * size.Y;

            return true;
        }

        origin = default;

        return false;
    }

    /// <summary>Gives a card's texels back for a same-sized card to reuse.</summary>
    /// <param name="origin">Where they started.</param>
    /// <param name="size">The card's resolution.</param>
    public void Release(Int2 origin, Int2 size) {
        free.Add((origin, size));
        Occupied -= size.X * size.Y;
    }

    /// <summary>Empties the atlas entirely.</summary>
    public void Clear() {
        free.Clear();
        shelves.Clear();
        nextShelfY = 0;
        Occupied = 0;
    }
}
