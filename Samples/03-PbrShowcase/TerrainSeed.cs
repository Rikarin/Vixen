// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Samples.PbrShowcase;

/// <summary>Writes the showcase's terrain — the checked-in generation step behind the committed
///     <c>Assets/Terrain/Meadow.vxterrain</c>.</summary>
/// <remarks>
///     <para>
///         <b>The heights are arithmetic, so the file is generated rather than sculpted</b> — the
///         same argument <c>PbrShowcaseGame.Spawn</c> makes for the grid. The binary is committed
///         because a <c>.vxterrain</c> is what the content build imports, and asking every checkout
///         to run a generator before its first build is a step somebody skips;
///         <c>dotnet run -- --regenerate-terrain</c> from the sample's directory rewrites it after
///         any change here.
///     </para>
///     <para>
///         ⚠ <b>Deliberately multi-tile.</b> Four tiles of 63 quads rather than one big one,
///         because a single-tile terrain exercises none of the tile addressing — the grass
///         scatter's atlas fold hid behind single-tile fixtures for months — and a sample exists to
///         walk the paths a real level walks.
///     </para>
///     <para>
///         What the shape gives the picture: a flat apron under the showcase floor, kept just below
///         it so the two never fight for the same depth; rolling hills beyond, where the orbit
///         camera's background used to be empty sky; a <c>Grass</c> weight layer painted on the
///         gentle slopes, which is what the <c>.vxgrass</c> rule is bound to; <c>Rock</c> painted
///         where it is steep and <c>Dirt</c> under the apron; and one hole east of the grid, the
///         feature a heightfield cannot fake with height.
///     </para>
/// </remarks>
internal static class TerrainSeed {
    /// <summary>Where the terrain file lives, relative to the sample's directory.</summary>
    public const string TerrainPath = "Assets/Terrain/Meadow.vxterrain";

    /// <summary>Half the terrain's span, in metres — the entity is translated by this, so the
    ///     grid's origin lands at the world's.</summary>
    public const float HalfExtent = 63f;

    /// <summary>Writes the terrain beside a project directory.</summary>
    /// <param name="projectDirectory">The sample's directory — the one holding <c>Assets/</c>.</param>
    /// <returns>The path it wrote.</returns>
    public static string Write(string projectDirectory) {
        var path = Path.Combine(projectDirectory, TerrainPath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TerrainStore.Write(Build()));

        return path;
    }

    /// <summary>The terrain itself, every section of the store exercised.</summary>
    public static TerrainMap Build() {
        // 2×2 tiles of 64 samples: 127×127 samples, 126 m a side at a metre per quad. ±16 m of
        // height range keeps a stored step under half a millimetre — see TerrainDescription's
        // MetresPerStep remarks for why the range is worth keeping tight.
        var map = new TerrainMap(
            new TerrainDescription {
                TileSamples = 64,
                TilesX = 2,
                TilesZ = 2,
                MetresPerQuad = 1f,
                MinHeight = -16f,
                MaxHeight = 16f
            }
        );

        var description = map.Description;
        var heights = new float[description.SamplesX, description.SamplesZ];

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                heights[x, z] = HeightAt(x, z);
                map.Base[x, z] = Quantize(description, heights[x, z]);
            }
        }

        Paint(map, heights);

        // The hole: a small pit 18 m east of the grid, past the floor's edge. A hole is a quad the
        // surface skips entirely — the one terrain feature no height can express.
        PunchHole(map, centreX: (int)HalfExtent + 18, centreZ: (int)HalfExtent + 4, radius: 3);

        map.InvalidateAll();
        map.Resolve();

        return map;
    }

    /// <summary>The height at a sample, in metres, in the terrain's own space.</summary>
    /// <remarks>
    ///     Two octaves of sine hills plus a small ripple, faded to a flat apron of −0.35 m within
    ///     ~10 m of the centre so the showcase floor at y = 0 always sits above the ground. The
    ///     numbers are tuned for the orbit camera: the first hills crest inside the grass rule's
    ///     42 m cull distance.
    /// </remarks>
    static float HeightAt(int x, int z) {
        var dx = x - HalfExtent;
        var dz = z - HalfExtent;
        var radius = MathF.Sqrt((dx * dx) + (dz * dz));

        var hills = (2.6f * MathF.Sin(x * 0.055f) * MathF.Cos(z * 0.045f))
            + (1.4f * MathF.Sin((x * 0.115f) + 1.7f) * MathF.Sin((z * 0.09f) + 0.6f))
            + (0.35f * MathF.Sin(x * 0.31f) * MathF.Cos(z * 0.27f));

        // Raised a little so the far field reads as hills rather than a bowl.
        var t = SmoothStep(10f, 24f, radius);

        return ((1f - t) * -0.35f) + (t * (hills + 1.5f));
    }

    /// <summary>Paints the three weight layers from the shape: grass on the gentle, rock on the
    ///     steep, dirt on the apron — summing to exactly the weight budget everywhere.</summary>
    static void Paint(TerrainMap map, float[,] heights) {
        var grass = map.Weights.AddLayer("Grass");
        var rock = map.Weights.AddLayer("Rock");
        var dirt = map.Weights.AddLayer("Dirt");
        var description = map.Description;

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                var slope = SlopeAt(heights, x, z, description);
                var dx = x - HalfExtent;
                var dz = z - HalfExtent;
                var apron = 1f - SmoothStep(9f, 16f, MathF.Sqrt((dx * dx) + (dz * dz)));

                var rockShare = SmoothStep(0.45f, 0.8f, slope);
                var dirtShare = (1f - rockShare) * apron;
                var grassShare = 1f - rockShare - dirtShare;

                // Bytes summing to exactly the budget, dirt taking the rounding remainder — the
                // weights class verifies the sum, and drifting under it reads as thin paint.
                var grassByte = (byte)MathF.Round(grassShare * TerrainWeights.Total);
                var rockByte = (byte)MathF.Round(rockShare * TerrainWeights.Total);

                map.Weights.SetWeight(grass, x, z, grassByte);
                map.Weights.SetWeight(rock, x, z, rockByte);
                map.Weights.SetWeight(dirt, x, z, (byte)(TerrainWeights.Total - grassByte - rockByte));
            }
        }
    }

    /// <summary>The local gradient's magnitude, rise over run, from central differences.</summary>
    static float SlopeAt(float[,] heights, int x, int z, in TerrainDescription description) {
        var left = heights[Math.Max(x - 1, 0), z];
        var right = heights[Math.Min(x + 1, description.SamplesX - 1), z];
        var near = heights[x, Math.Max(z - 1, 0)];
        var far = heights[x, Math.Min(z + 1, description.SamplesZ - 1)];

        var gradientX = (right - left) / (2f * description.MetresPerQuad);
        var gradientZ = (far - near) / (2f * description.MetresPerQuad);

        return MathF.Sqrt((gradientX * gradientX) + (gradientZ * gradientZ));
    }

    static void PunchHole(TerrainMap map, int centreX, int centreZ, int radius) {
        for (var z = centreZ - radius; z <= centreZ + radius; z++) {
            for (var x = centreX - radius; x <= centreX + radius; x++) {
                var dx = x - centreX;
                var dz = z - centreZ;

                if ((dx * dx) + (dz * dz) <= radius * radius) {
                    map.Holes.SetHole(x, z, true);
                }
            }
        }
    }

    static ushort Quantize(in TerrainDescription description, float metres) {
        var normalized = (metres - description.MinHeight) / description.HeightRange;

        return (ushort)Math.Clamp(
            MathF.Round(normalized * TerrainSamples.MaxHeight),
            0f,
            TerrainSamples.MaxHeight
        );
    }

    static float SmoothStep(float edge0, float edge1, float value) {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);

        return t * t * (3f - (2f * t));
    }
}
