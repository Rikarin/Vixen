// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Water;

/// <summary>
///     How the screen is divided for [35 § D8](../../docs/plan/35-water.md#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s)'s
///     tile classification — the arithmetic the host, the classifier and the water pass share.
/// </summary>
/// <remarks>
///     <para>
///         <b>Three things address a tile and they have to agree.</b> The host sizes the buffer and
///         picks the instance count from here, <c>WaterTiles.rvn</c> writes one word per tile from
///         <c>WaterTile.Index</c>, and <c>Water.rvn</c>'s vertex stage reads it back through
///         <c>WaterTile.Unindex</c>. This is the C# copy of that struct, and
///         <c>WaterTileTests</c> is what holds the two copies together.
///     </para>
///     <para>
///         ⚠ <b>Constants and not settings.</b> A tile size a host could choose would be a number in a
///         buffer's stride, in a dispatch's workgroup size and in a shader's <c>[ComputeShader(64)]</c>
///         — and the last of those is a compile-time property of the module. Changing it means changing
///         both files together, which is what a constant in each says.
///     </para>
/// </remarks>
public static class WaterTiles {
    /// <summary>How many pixels a tile is on a side. <c>WaterTile.Size</c>, and a workgroup of 64.</summary>
    public const int Size = 8;

    /// <summary>How many vertices one tile's draw is — two triangles over a rectangle.</summary>
    /// <remarks>
    ///     ⚠ <b>Six and not three, and it is the difference between saving work and creating it.</b> A
    ///     screen-covering triangle is twice the viewport, so scaled down to a tile it covers four of
    ///     them — and a water tile shaded up to four times costs more than the dry tiles the
    ///     classification skipped. See <c>WaterTile.Corner</c>.
    /// </remarks>
    public const int VerticesPerTile = 6;

    /// <summary>How many tiles cover a target that many pixels across.</summary>
    public static Int2 CountFor(Int2 target) =>
        new(
            (Math.Max(target.X, 0) + Size - 1) / Size,
            (Math.Max(target.Y, 0) + Size - 1) / Size
        );

    /// <summary>How many tiles that grid holds, which is the draw's instance count.</summary>
    public static int Total(Int2 count) => Math.Max(count.X, 0) * Math.Max(count.Y, 0);

    /// <summary>One tile's index in a row-major grid of them.</summary>
    public static int Index(Int2 tile, Int2 count) => (tile.Y * count.X) + tile.X;

    /// <summary>The tile a row-major index names — what an instance id means.</summary>
    public static Int2 Unindex(int index, Int2 count) =>
        count.X <= 0 ? default : new(index % count.X, index / count.X);

    /// <summary>How many bytes the flag buffer for that grid is.</summary>
    /// <remarks>
    ///     ⚠ <b>Never zero, because the binding exists whether or not the classification runs.</b> A
    ///     descriptor set is written wholly or not at all, so the untiled variant still binds this — see
    ///     <c>Water.rvn</c>'s <c>waterTiles</c> — and a buffer of no bytes is one no backend will make.
    /// </remarks>
    public static int Bytes(Int2 count) => Math.Max(Total(count), 1) * sizeof(uint);
}
