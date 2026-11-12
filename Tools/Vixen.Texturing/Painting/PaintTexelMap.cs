// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Texturing.Painting;

/// <summary>A number per texel for the few texels a stroke touches, by tile.</summary>
/// <typeparam name="T">What each texel holds.</typeparam>
/// <remarks>
///     <para>
///         ⚠ <b>Not a dictionary, because this is read several times per texel per stamp.</b> A stroke at 4K
///         holds a few million entries, and a hash table of that size is a cache miss per lookup: a stamp of a
///         few thousand texels took 30 ms, of which the brush was a small part. Here a lookup is two array
///         reads, and the tiles a stroke never reaches cost nothing, so the shape of what is kept stays sparse.
///     </para>
///     <para>
///         ⚠ <b><see cref="Clear" /> is one increment.</b> A stamp's own map is emptied for every stamp, and it
///         must not cost what the stamp cost.
///     </para>
/// </remarks>
public sealed class PaintTexelMap<T> where T : unmanaged {
    const int TileBits = 6;
    const int TileSide = 1 << TileBits;
    const int TileMask = TileSide - 1;

    readonly int width;
    readonly int tilesWide;
    readonly Tile?[] tiles;
    int generation = 1;

    /// <summary>A map for an atlas of this size.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height.</param>
    public PaintTexelMap(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        this.width = width;
        tilesWide = ((width - 1) >> TileBits) + 1;
        tiles = new Tile?[tilesWide * (((height - 1) >> TileBits) + 1)];
    }

    /// <summary>What a texel holds, if anything.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="value">What it holds, or the default.</param>
    /// <returns>Whether it holds anything.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(int index, out T value) {
        var (row, column) = Math.DivRem(index, width);

        if (tiles[(row >> TileBits) * tilesWide + (column >> TileBits)] is { } tile) {
            var at = ((row & TileMask) << TileBits) | (column & TileMask);

            if (tile.Marks[at] == generation) {
                value = tile.Values[at];

                return true;
            }
        }

        value = default;

        return false;
    }

    /// <summary>What a texel holds, for a caller that already has its row and column.</summary>
    /// <param name="column">Its column.</param>
    /// <param name="row">Its row.</param>
    /// <param name="value">What it holds, or the default.</param>
    /// <returns>Whether it holds anything.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(int column, int row, out T value) {
        if (tiles[(row >> TileBits) * tilesWide + (column >> TileBits)] is { } tile) {
            var at = ((row & TileMask) << TileBits) | (column & TileMask);

            if (tile.Marks[at] == generation) {
                value = tile.Values[at];

                return true;
            }
        }

        value = default;

        return false;
    }

    /// <summary>Whether a texel holds anything.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <returns>Whether it does.</returns>
    public bool Contains(int index) => TryGet(index, out _);

    /// <summary>Gives a texel a value.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, T value) {
        var (row, column) = Math.DivRem(index, width);
        var slot = (row >> TileBits) * tilesWide + (column >> TileBits);
        var tile = tiles[slot] ??= new();
        var at = ((row & TileMask) << TileBits) | (column & TileMask);

        tile.Marks[at] = generation;
        tile.Values[at] = value;
    }

    /// <summary>Gives a texel a value unless it holds one.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether it was given it.</returns>
    public bool TryAdd(int index, T value) {
        if (Contains(index)) {
            return false;
        }

        Set(index, value);

        return true;
    }

    /// <summary>Forgets every value and keeps the room.</summary>
    public void Clear() {
        generation++;

        if (generation == int.MaxValue) {
            Release();
        }
    }

    /// <summary>Forgets every value and gives the room back.</summary>
    public void Release() {
        Array.Clear(tiles);
        generation = 1;
    }

    sealed class Tile {
        public int[] Marks { get; } = new int[TileSide * TileSide];

        public T[] Values { get; } = new T[TileSide * TileSide];
    }
}
