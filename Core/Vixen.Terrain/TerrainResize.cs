// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     Changing a terrain's shape without losing what is on it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The manage half of [docs/plan/31 § The terrain panel]: resize, add and remove
///         tiles.</b> Every one of those is the same operation — the terrain is rebuilt against a new
///         <see cref="TerrainDescription" /> and everything that overlaps is carried across.
///     </para>
///     <para>
///         ⚠ <b>By sample index, not by world position.</b> Sample <c>(x, z)</c> of the old terrain
///         becomes sample <c>(x, z)</c> of the new one, so changing
///         <see cref="TerrainDescription.MetresPerQuad" /> makes the same landscape physically larger
///         rather than resampling it onto a finer grid. Resampling is what
///         <see cref="TerrainHeightmap.Import" /> does and it is a different operation with a
///         different loss: it costs a bilinear filter over every sample and it cannot preserve an edit
///         layer's deltas, because a delta between two samples is not a delta at either of them.
///     </para>
///     <para>
///         ⚠ <b>Changing the height range rescales, and that is the one thing here that is not a
///         copy.</b> Heights are stored as a fraction of
///         <see cref="TerrainDescription.MinHeight" />…<see cref="TerrainDescription.MaxHeight" />, so
///         carrying the stored numbers across a range change would silently move every hill. What is
///         preserved is metres — a 40 m peak is still 40 m — and what is lost is precision, in
///         whichever direction the range moved. The dialog says so, which is [§ D2]'s requirement.
///     </para>
/// </remarks>
public static class TerrainResize {
    /// <summary>Rebuilds a terrain against a new shape.</summary>
    /// <param name="source">The terrain.</param>
    /// <param name="target">Its new shape.</param>
    /// <param name="fill">What new ground outside the old extent is, in metres.</param>
    /// <returns>The new terrain. The source is left alone.</returns>
    /// <exception cref="ArgumentException">The new shape is not one a terrain can have.</exception>
    /// <remarks>
    ///     A new object rather than a mutation, because every array in a terrain is sized by its
    ///     description — and because that is what makes the whole operation one undo entry holding
    ///     two references rather than a diff nobody can invert.
    /// </remarks>
    public static Terrain To(Terrain source, TerrainDescription target, float fill = 0f) {
        ArgumentNullException.ThrowIfNull(source);

        if (target.Validate() is { } refusal) {
            throw new ArgumentException(refusal, nameof(target));
        }

        var from = source.Description;
        var result = new Terrain(target, fill);

        var width = Math.Min(from.SamplesX, target.SamplesX);
        var height = Math.Min(from.SamplesZ, target.SamplesZ);
        var overlap = new TerrainRect(0, 0, width, height);

        // The heights, in metres, so a range change rescales rather than reinterprets.
        var rescales = from.MinHeight != target.MinHeight || from.MaxHeight != target.MaxHeight;

        for (var z = 0; z < height; z++) {
            for (var x = 0; x < width; x++) {
                var stored = source.Base[x, z];

                result.Base[x, z] = rescales ? target.StoreHeight(from.HeightOf(stored)) : stored;
            }
        }

        foreach (var layer in source.Layers) {
            var copy = result.AddLayer(layer.Name, layer.Kind);

            copy.HeightAlpha = layer.HeightAlpha;
            copy.WeightAlpha = layer.WeightAlpha;
            copy.IsVisible = layer.IsVisible;
            copy.IsLocked = layer.IsLocked;

            CopyDeltas(layer, copy, overlap, from, target, rescales);
        }

        for (var index = 0; index < source.Weights.LayerCount; index++) {
            result.Weights.AddLayer(source.Weights.Names[index], source.Weights.BlendOf(index));
        }

        for (var z = 0; z < height; z++) {
            for (var x = 0; x < width; x++) {
                for (var index = 0; index < source.Weights.LayerCount; index++) {
                    result.Weights.SetWeight(index, x, z, source.Weights.WeightAt(index, x, z));
                }

                if (source.Holes.IsHole(x, z)) {
                    result.Holes.SetHole(x, z, true);
                }
            }
        }

        result.InvalidateAll();
        result.Resolve();

        return result;
    }

    /// <summary>Grows or shrinks a terrain by whole tiles, keeping its origin.</summary>
    /// <param name="source">The terrain.</param>
    /// <param name="tilesX">How many tiles it should be across.</param>
    /// <param name="tilesZ">And deep.</param>
    /// <param name="fill">What new ground is, in metres.</param>
    /// <returns>The new terrain.</returns>
    public static Terrain WithTiles(Terrain source, int tilesX, int tilesZ, float fill = 0f) {
        ArgumentNullException.ThrowIfNull(source);

        return To(source, source.Description with { TilesX = tilesX, TilesZ = tilesZ }, fill);
    }

    /// <summary>Copies one layer's deltas, in metres if the range moved and raw if it did not.</summary>
    /// <remarks>
    ///     ⚠ <b>A delta is a difference of two stored values, so a range change scales it by the ratio
    ///     of the two ranges rather than by <see cref="TerrainDescription.StoreHeight" />.</b> Putting
    ///     a delta through the absolute conversion would add the old minimum and subtract the new one,
    ///     which for a terrain whose floor moved turns every edit layer into a uniform offset of the
    ///     whole terrain.
    /// </remarks>
    static void CopyDeltas(
        TerrainEditLayer layer,
        TerrainEditLayer into,
        TerrainRect overlap,
        in TerrainDescription from,
        in TerrainDescription target,
        bool rescales
    ) {
        var ratio = target.HeightRange > 0f ? from.HeightRange / target.HeightRange : 0f;

        foreach (var (chunkX, chunkZ) in layer.OccupiedChunks()) {
            var rect = new TerrainRect(
                chunkX * TerrainEditLayer.ChunkSize,
                chunkZ * TerrainEditLayer.ChunkSize,
                TerrainEditLayer.ChunkSize,
                TerrainEditLayer.ChunkSize
            ).Clip(overlap);

            for (var z = rect.Z; z < rect.EndZ; z++) {
                for (var x = rect.X; x < rect.EndX; x++) {
                    var delta = layer.DeltaAt(x, z);

                    if (delta == 0) {
                        continue;
                    }

                    into.SetDelta(
                        x,
                        z,
                        rescales
                            ? (short)Math.Clamp(MathF.Round(delta * ratio), short.MinValue, short.MaxValue)
                            : delta
                    );
                }
            }
        }
    }
}
