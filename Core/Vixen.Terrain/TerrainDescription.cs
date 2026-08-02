// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>
///     The shape of a terrain: how many tiles, how big they are, and what a height means.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D2].</b> A value rather than an object, because it is the thing a create
///         dialog is bound to and the thing a derived-cost readout is computed from — and because
///         two terrains with the same shape should compare equal whatever their heights are.
///     </para>
///     <para>
///         ⚠ <b><see cref="TileSamples" /> is a count of samples and must be a power of two, so a
///         tile spans one less than a power of two quads.</b> That is not a stylistic choice. Jolt's
///         height field needs the sample count to be a multiple of its block size and needs the
///         resulting block count to be a power of two, and it reports a violation by returning
///         nothing at all — see <c>PhysicsShapes.HeightField</c>. Unreal states the same constraint
///         from the other end, as section sizes that are "a power of two value minus one" quads.
///     </para>
///     <para>
///         <b>The height range is the author's, not a constant.</b> Unreal spends the same sixteen
///         bits over a fixed −256…256 window whether the terrain is a dune field or the Himalayas; a
///         40 m landscape here gets 0.6 mm of vertical precision instead of 8 mm, for the same bytes.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct TerrainDescription {
    /// <summary>The smallest tile a terrain can be made of, in samples.</summary>
    /// <remarks>
    ///     Four, which is Jolt's floor — the smallest block is two samples across and it needs two
    ///     blocks per axis. Anything smaller has no collider.
    /// </remarks>
    public const int MinTileSamples = 4;

    /// <summary>The largest tile, in samples.</summary>
    /// <remarks>
    ///     1024, which is 1023 quads. Above that a single tile is a megabyte of heights and stops
    ///     being a useful unit of editing, streaming or collision — which is the whole reason tiles
    ///     exist.
    /// </remarks>
    public const int MaxTileSamples = 1024;

    /// <summary>How many samples a tile is along each axis. A power of two.</summary>
    public int TileSamples { get; init; }

    /// <summary>How many tiles along X.</summary>
    public int TilesX { get; init; }

    /// <summary>How many tiles along Z.</summary>
    public int TilesZ { get; init; }

    /// <summary>How far apart two samples are, in metres.</summary>
    public float MetresPerQuad { get; init; }

    /// <summary>The height a stored sample of zero means, in metres.</summary>
    public float MinHeight { get; init; }

    /// <summary>The height a stored sample of <see cref="TerrainSamples.MaxHeight" /> means.</summary>
    public float MaxHeight { get; init; }

    /// <summary>A default terrain: four tiles of 127 quads at a metre, 200 m of vertical range.</summary>
    public static TerrainDescription Default =>
        new() {
            TileSamples = 128,
            TilesX = 2,
            TilesZ = 2,
            MetresPerQuad = 1f,
            MinHeight = -100f,
            MaxHeight = 100f
        };

    /// <summary>How many quads a tile spans along each axis.</summary>
    public int TileQuads => TileSamples - 1;

    /// <summary>How many samples the whole terrain has along X.</summary>
    /// <remarks>
    ///     <b>One more than the quads, not one per tile per sample.</b> Adjacent tiles <em>share</em>
    ///     their boundary row rather than each owning a copy, so the grid is
    ///     <c>tiles × quads + 1</c> — see <see cref="TerrainSamples" />, where that sharing is
    ///     structural rather than a rule somebody has to remember.
    /// </remarks>
    public int SamplesX => (TilesX * TileQuads) + 1;

    /// <summary>How many samples the whole terrain has along Z.</summary>
    public int SamplesZ => (TilesZ * TileQuads) + 1;

    /// <summary>How many samples there are altogether.</summary>
    public long SampleCount => (long)SamplesX * SamplesZ;

    /// <summary>How many tiles there are.</summary>
    public int TileCount => TilesX * TilesZ;

    /// <summary>How wide the terrain is, in metres.</summary>
    public float WidthX => TilesX * TileQuads * MetresPerQuad;

    /// <summary>How deep the terrain is, in metres.</summary>
    public float WidthZ => TilesZ * TileQuads * MetresPerQuad;

    /// <summary>How much vertical range the sixteen bits are spread over, in metres.</summary>
    public float HeightRange => MaxHeight - MinHeight;

    /// <summary>How many metres one step of a stored sample is.</summary>
    /// <remarks>
    ///     The number the create dialog should show beside the height range, because it is what the
    ///     range actually buys. A 40 m range is 0.6 mm; Unreal's fixed 512 m window is 8 mm.
    /// </remarks>
    public float MetresPerStep => HeightRange / TerrainSamples.MaxHeight;

    /// <summary>How many bytes the heights occupy.</summary>
    public long HeightBytes => SampleCount * sizeof(ushort);

    /// <summary>How many bytes one weight layer occupies.</summary>
    /// <remarks>
    ///     One byte per sample per layer. Four layers share a texture on the device — see
    ///     [docs/plan/31 § D5] — but they do not share storage here, because a layer is added and
    ///     removed on its own.
    /// </remarks>
    public long WeightBytesPerLayer => SampleCount;

    /// <summary>Whether the description is one a terrain can be built from.</summary>
    public bool IsValid => Validate() is null;

    /// <summary>Where a sample sits, in the terrain's own space.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <param name="height">Its stored height.</param>
    /// <returns>The position, with Y in metres.</returns>
    public Vector3 PositionOf(int x, int z, ushort height) =>
        new(x * MetresPerQuad, HeightOf(height), z * MetresPerQuad);

    /// <summary>What a stored sample means in metres.</summary>
    /// <param name="height">The stored sample.</param>
    /// <returns>The height in metres.</returns>
    public float HeightOf(ushort height) =>
        MinHeight + (height / (float)TerrainSamples.MaxHeight * HeightRange);

    /// <summary>What a height in metres stores as.</summary>
    /// <param name="metres">The height.</param>
    /// <returns>The stored sample, clamped to the range.</returns>
    /// <remarks>
    ///     Rounded rather than truncated, so a round trip through
    ///     <see cref="HeightOf" /> is stable rather than drifting downwards every time a tool reads a
    ///     height and writes it back — which is what makes a flatten converge instead of sinking.
    /// </remarks>
    public ushort StoreHeight(float metres) {
        if (!(HeightRange > 0f) || float.IsNaN(metres)) {
            return 0;
        }

        var normalised = (metres - MinHeight) / HeightRange * TerrainSamples.MaxHeight;
        return (ushort)Math.Clamp(MathF.Round(normalised), 0f, TerrainSamples.MaxHeight);
    }

    /// <summary>Which tile a sample belongs to, and where in that tile it sits.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The tile's indices and the sample's indices within it.</returns>
    /// <remarks>
    ///     ⚠ <b>A boundary sample answers as the <em>upper</em> tile's sample zero, not the lower
    ///     tile's last.</b> Both are true — the sample is shared, which is the whole point of
    ///     <see cref="TerrainSamples" /> — and this has to pick one, because a caller iterating tiles
    ///     must visit each sample exactly once. Picking the upper makes ownership the half-open range
    ///     <c>[T·quads, (T+1)·quads)</c>, which partitions the grid cleanly; the very last row and
    ///     column of the terrain have no upper tile and are clamped back into the last one.
    ///     <see cref="SamplesOf" /> is the other question and includes the boundary from both sides.
    /// </remarks>
    public (int TileX, int TileZ, int LocalX, int LocalZ) TileOf(int x, int z) {
        var tileX = Math.Clamp(x / TileQuads, 0, TilesX - 1);
        var tileZ = Math.Clamp(z / TileQuads, 0, TilesZ - 1);
        return (tileX, tileZ, x - (tileX * TileQuads), z - (tileZ * TileQuads));
    }

    /// <summary>Which samples a tile covers, its last row included.</summary>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">The tile's Z index.</param>
    /// <returns>The inclusive sample range.</returns>
    public TerrainRect SamplesOf(int tileX, int tileZ) =>
        new(tileX * TileQuads, tileZ * TileQuads, TileSamples, TileSamples);

    /// <summary>Why this description cannot be built, or null if it can.</summary>
    /// <returns>The reason, in words a create dialog can show.</returns>
    public string? Validate() {
        if (TileSamples is < MinTileSamples or > MaxTileSamples || (TileSamples & (TileSamples - 1)) != 0) {
            return $"A tile must be a power of two between {MinTileSamples} and {MaxTileSamples} "
                + $"samples; it was {TileSamples}. A tile of N quads is N + 1 samples, so it is the "
                + "sample count that is the power of two.";
        }

        if (TilesX < 1 || TilesZ < 1) {
            return $"A terrain needs at least one tile along each axis; it was {TilesX} × {TilesZ}.";
        }

        if (!(MetresPerQuad > 0f) || !float.IsFinite(MetresPerQuad)) {
            return $"A quad must be a positive number of metres across; it was {MetresPerQuad}.";
        }

        if (!float.IsFinite(MinHeight) || !float.IsFinite(MaxHeight) || !(MaxHeight > MinHeight)) {
            return $"The height range must be finite and increasing; it was {MinHeight}…{MaxHeight}.";
        }

        // A guard on the create dialog rather than on physics: the arithmetic below overflows int
        // long before a machine could hold the result, and the message somebody needs at that point
        // names the size they asked for rather than an overflow.
        if (SampleCount > int.MaxValue) {
            return $"{TilesX} × {TilesZ} tiles of {TileSamples} samples is {SampleCount} samples, "
                + "which is more than one array can hold.";
        }

        return null;
    }

    /// <summary>Renders the shape and what it costs.</summary>
    /// <returns>The description in text.</returns>
    public override string ToString() =>
        $"{TilesX}×{TilesZ} tiles of {TileQuads} quads — {WidthX:0.#}×{WidthZ:0.#} m, "
        + $"{SampleCount} samples, {HeightBytes / 1024f / 1024f:0.##} MB of heights, "
        + $"{MetresPerStep * 1000f:0.##} mm per step";
}

/// <summary>A rectangle of samples, in sample indices.</summary>
/// <param name="X">The low X index.</param>
/// <param name="Z">The low Z index.</param>
/// <param name="Width">How many samples along X.</param>
/// <param name="Height">How many samples along Z.</param>
[DataContract]
public readonly record struct TerrainRect(int X, int Z, int Width, int Height) {
    /// <summary>The empty rectangle, which every operation on it does nothing to.</summary>
    public static TerrainRect Empty => new(0, 0, 0, 0);

    /// <summary>Whether it covers no samples.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>One past the last X index.</summary>
    public int EndX => X + Width;

    /// <summary>One past the last Z index.</summary>
    public int EndZ => Z + Height;

    /// <summary>How many samples it covers.</summary>
    public int Count => IsEmpty ? 0 : Width * Height;

    /// <summary>Whether a sample is inside.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>Whether it is inside.</returns>
    public bool Contains(int x, int z) => x >= X && x < EndX && z >= Z && z < EndZ;

    /// <summary>The part of this rectangle that is also inside another.</summary>
    /// <param name="other">The other.</param>
    /// <returns>The overlap, which may be empty.</returns>
    public TerrainRect Clip(TerrainRect other) {
        var x = Math.Max(X, other.X);
        var z = Math.Max(Z, other.Z);
        var endX = Math.Min(EndX, other.EndX);
        var endZ = Math.Min(EndZ, other.EndZ);

        return endX <= x || endZ <= z ? Empty : new(x, z, endX - x, endZ - z);
    }

    /// <summary>The smallest rectangle containing both.</summary>
    /// <param name="other">The other.</param>
    /// <returns>The union.</returns>
    /// <remarks>
    ///     An empty rectangle is the identity rather than a corner at the origin — otherwise the
    ///     first union of a stroke that has touched nothing yet drags the whole record back to
    ///     sample zero, and an undo record covers the entire terrain.
    /// </remarks>
    public TerrainRect Union(TerrainRect other) {
        if (IsEmpty) {
            return other;
        }

        if (other.IsEmpty) {
            return this;
        }

        var x = Math.Min(X, other.X);
        var z = Math.Min(Z, other.Z);
        return new(x, z, Math.Max(EndX, other.EndX) - x, Math.Max(EndZ, other.EndZ) - z);
    }

    /// <summary>Grown by a margin on every side.</summary>
    /// <param name="margin">How many samples to grow by.</param>
    /// <returns>The grown rectangle.</returns>
    /// <remarks>
    ///     What a smooth or an erosion needs: a kernel that reads its neighbours writes a rectangle
    ///     and <em>reads</em> a larger one, and an undo record sized to the write is a record that
    ///     cannot restore what the read changed on the next pass.
    /// </remarks>
    public TerrainRect Grow(int margin) =>
        IsEmpty ? this : new(X - margin, Z - margin, Width + (margin * 2), Height + (margin * 2));
}
