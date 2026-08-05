// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Writes the arena's surrounding ground — the generation step behind the committed
///     <c>Assets/Terrain/Outskirts.vxterrain</c>.</summary>
/// <remarks>
///     <para>
///         <b>Around the arena, not under it.</b> The play space has an authored floor —
///         <c>arena-floor.obj</c>, a slab whose top is y = 0 — and a heightfield under it would be
///         two surfaces competing for the same depth at every pixel. So the apron sits at −1.2 m
///         inside the walls, where nothing can see it, and everything this terrain is *for* happens
///         outside the 32 m perimeter: the ground the arena stands on, which until now was the
///         skybox meeting nothing.
///     </para>
///     <para>
///         ⚠ <b>Deliberately multi-tile, on <c>03-PbrShowcase</c>'s argument.</b> Four tiles rather
///         than one, because a single-tile terrain exercises none of the tile addressing and the
///         grass scatter's atlas fold hid behind single-tile fixtures for months. Two metres a quad
///         rather than that sample's one, because this ground is 252 m a side and an arena needs a
///         horizon rather than a lawn.
///     </para>
///     <para>
///         ⚠ <b>The layers name albedo and surface maps and deliberately name no normal map.</b>
///         <see cref="TerrainLayerDescription.Normal" /> is stored by <c>TerrainStore</c> and read
///         back by it, and that is the whole of its life: <c>TerrainRenderer.ResolveLayerTextures</c>
///         resolves <c>Albedo</c> and <c>Surface</c> and nothing else, and <c>Terrain.rvn</c>
///         declares <c>layerMaps</c> and <c>surfaceMaps</c> and no third array. A layer normal has
///         nowhere to arrive, so the sample ships no terrain normal maps to send there.
///     </para>
///     <para>
///         The <c>Surface</c> maps *are* bound — into <c>surfaceMaps</c>, one per layer — and the
///         shader reads exactly one channel of them, <c>.a</c>, and only under a height blend
///         (<c>TerrainBase.LayerHeight</c>). These layers blend by weight, so today those bytes are
///         resident and unread. They are named anyway because the binding exists and the read is one
///         <see cref="TerrainLayerBlend" /> away; the normal maps were deleted because no binding
///         exists at all. That is the line between the two.
///     </para>
/// </remarks>
internal static class TerrainSeed {
    /// <summary>Where the terrain file lives, relative to the sample's directory.</summary>
    public const string TerrainPath = "Assets/Terrain/Outskirts.vxterrain";

    /// <summary>The grass rule painted onto the terrain's own Grass layer.</summary>
    public const string GrassPath = "Assets/Terrain/Outskirts.vxgrass";

    /// <summary>Where the bushes' instances live — the <c>.vxfol</c> the volume component names.</summary>
    public const string FoliagePath = "Assets/Terrain/Outskirts.vxfol";

    /// <summary>And the palette entry describing what they are — the bushes' <c>.vxfoliage</c>.</summary>
    public const string FoliageTypePath = "Assets/Terrain/Outskirts.vxfoliage";

    /// <summary>Half the terrain's span in metres — the entity is translated by this, so the grid's
    ///     centre lands on the arena's origin.</summary>
    public const float HalfExtent = 126f;

    /// <summary>How far from the centre the arena's own geometry reaches.</summary>
    /// <remarks>
    ///     The perimeter walls stand at ±32 m and the floor slab matches them. The apron is flat and
    ///     buried inside this radius and the ground only starts to be ground beyond it.
    /// </remarks>
    const float ArenaReach = 34f;

    /// <summary>Writes the terrain beside a project directory.</summary>
    /// <param name="projectDirectory">The sample's directory — the one holding <c>Assets/</c>.</param>
    /// <returns>The path it wrote to.</returns>
    public static string Write(string projectDirectory) {
        var path = Path.Combine(projectDirectory, TerrainPath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TerrainStore.Write(Build()));

        var foliage = BuildFoliage();
        var bytes = new byte[FoliageStore.ByteCount(foliage)];

        FoliageStore.Write(foliage, bytes);
        File.WriteAllBytes(Path.Combine(projectDirectory, FoliagePath), bytes);

        return path;
    }

    /// <summary>The bushes standing on the slopes outside the walls — the committed <c>.vxfol</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What finally gives <c>leaves-albedo.png</c> somewhere to sit.</b>
    ///         <c>FoliageType.Albedo</c> has had a seat since the field was added and this sample
    ///         placed no volume at all, so the map was committed, imported, made resident and
    ///         sampled by nothing. A cutout card cross is the mesh that shows what the alpha test
    ///         bought: without it the bush is three intersecting slabs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One type, and that is a limit rather than a choice.</b> A stand binds one albedo
    ///         for its whole palette — <c>FoliageDrawPass.Albedo</c> carries the shape that would
    ///         fix it — so a volume mixing <c>bark</c> and <c>leaves</c> would draw both through
    ///         whichever map <c>TerrainSceneRenderer.AlbedoOf</c> found first. The trunk the bark map
    ///         is for waits on that, and adding it now would be a stand that draws visibly wrong.
    ///     </para>
    ///     <para>
    ///         <b>Placed in world space by arithmetic</b>, on <c>03-PbrShowcase</c>'s terms: the
    ///         terrain entity is translated by <see cref="HalfExtent" />, and so is the volume's,
    ///         so a bush at world <c>(x, z)</c> reads its height from sample
    ///         <c>(x / 2 + 63, z / 2 + 63)</c>. A golden-angle spiral over the ring between the
    ///         walls and the first hills, which is where the Grass layer is painted and therefore
    ///         where the field the bushes stand in already is.
    ///     </para>
    /// </remarks>
    public static FoliageVolume BuildFoliage() {
        var volume = new FoliageVolume(new(32f));

        var bush = volume.AddType(
            FoliageType.Of("Bush") with {
                Mesh = "vx:9a745bce11d8456db1f67ba97a59b796#b5761ecb",
                Albedo = "vx:b52ac26f9cda4406b1c667297a987cbd",
                Radius = 1.2f,
                MinScale = 0.8f,
                MaxScale = 1.6f,
                StartCullDistance = 70f,
                EndCullDistance = 90f
            }
        );

        var placed = 0;

        for (var candidate = 0; placed < 64 && candidate < 400; candidate++) {
            // The ring the Grass layer is painted over: outside the walls, inside the first crest.
            var angle = candidate * 2.399963f;
            var radius = ArenaReach + 6f + ((candidate % 89) / 89f * 34f);
            var x = radius * MathF.Cos(angle);
            var z = radius * MathF.Sin(angle);

            // Out of the quarry, which is a hole — an instance standing over one has no ground.
            if (MathF.Abs(x - 50f) < 12f && MathF.Abs(z + 46f) < 12f) {
                continue;
            }

            var height = HeightAt((int)((x / 2f) + (HalfExtent / 2f)), (int)((z / 2f) + (HalfExtent / 2f)));

            volume.Add(
                bush,
                new(
                    new(x, height, z),
                    Quaternion.FromYawPitchRoll(candidate * 1.7f, 0f, 0f),
                    0.8f + ((candidate * 37 % 100) / 100f * 0.8f)
                )
            );

            placed++;
        }

        return volume;
    }

    /// <summary>The terrain itself, every section of the store exercised.</summary>
    public static TerrainMap Build() {
        // 2×2 tiles of 64 samples at two metres a quad: 127×127 samples, 252 m a side. ±24 m of
        // height range keeps a stored step under a millimetre — TerrainDescription.MetresPerStep.
        var map = new TerrainMap(
            new TerrainDescription {
                TileSamples = 64,
                TilesX = 2,
                TilesZ = 2,
                MetresPerQuad = 2f,
                MinHeight = -24f,
                MaxHeight = 24f
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

        // A quarry cut into the rise north-east of the arena. A hole is a quad the surface skips
        // entirely — the one terrain feature no height can express, and the reason this sample
        // punches one rather than trusting the fixtures.
        PunchHole(map, centreX: 88, centreZ: 40, radius: 4);

        map.InvalidateAll();
        map.Resolve();

        return map;
    }

    /// <summary>The height at a sample, in metres, in the terrain's own space.</summary>
    /// <remarks>
    ///     Flat at −1.2 m under the arena and for a few metres past its walls, then rising into two
    ///     octaves of hills. The rise starts outside <see cref="ArenaReach" /> so no slope ever
    ///     climbs into the play space, and the far hills crest around 12 m — high enough to close the
    ///     horizon behind the six-metre walls, which is what the sky used to meet on its own.
    /// </remarks>
    static float HeightAt(int x, int z) {
        var dx = (x - (HalfExtent / 2f)) * 2f;
        var dz = (z - (HalfExtent / 2f)) * 2f;
        var radius = MathF.Sqrt((dx * dx) + (dz * dz));

        var hills = (6.2f * MathF.Sin(x * 0.048f) * MathF.Cos(z * 0.041f))
            + (2.8f * MathF.Sin((x * 0.101f) + 1.3f) * MathF.Sin((z * 0.087f) + 0.4f))
            + (0.6f * MathF.Sin(x * 0.29f) * MathF.Cos(z * 0.24f));

        var t = SmoothStep(ArenaReach, ArenaReach + 40f, radius);

        return ((1f - t) * -1.2f) + (t * (hills + 5.5f));
    }

    /// <summary>Paints the three weight layers from the shape: grass on the gentle, rock on the
    ///     steep, dirt on the apron — summing to exactly the weight budget everywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The three albedo references are what make this terrain part of the streaming
    ///     survey.</b> They resolve through the same <c>AssetTextureSource</c> the mesh materials
    ///     use — <c>AssetTerrainTextures</c> exists to make that one source rather than two — so a
    ///     rock texture shared by a layer and a wall is one upload and one residency.
    /// </remarks>
    static void Paint(TerrainMap map, float[,] heights) {
        var grass = map.Weights.AddLayer(
            new TerrainLayerDescription(
                "Grass",
                Albedo: "vx:819e2b88b1254624aa86a853b602af0b",
                Surface: "vx:60a269d4262b4f45a6a94f017e85a528",
                TilingMetres: 3f
            )
        );

        var rock = map.Weights.AddLayer(
            new TerrainLayerDescription(
                "Rock",
                Albedo: "vx:d32469ecfa9a40c5a9614d863d0e19c2",
                Surface: "vx:b4d8f55dfd014a36a22f91b628360a4a",
                TilingMetres: 5f
            )
        );

        var dirt = map.Weights.AddLayer(
            new TerrainLayerDescription(
                "Dirt",
                Albedo: "vx:b907e0576c254a8da80df2c06208f9cc",
                Surface: "vx:2113c0329b5b43baaeb9e137d7f414ea",
                TilingMetres: 2.5f
            )
        );

        var description = map.Description;

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                var slope = SlopeAt(heights, x, z, description);
                var dx = (x - (HalfExtent / 2f)) * 2f;
                var dz = (z - (HalfExtent / 2f)) * 2f;
                var apron = 1f - SmoothStep(ArenaReach, ArenaReach + 18f, MathF.Sqrt((dx * dx) + (dz * dz)));

                var rockShare = SmoothStep(0.35f, 0.75f, slope);
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
