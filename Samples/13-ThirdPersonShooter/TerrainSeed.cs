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
///         two surfaces competing for the same depth at every pixel. So the apron sits just under the
///         slab — <see cref="ApronHeight" />, which is a doorstep rather than a drop now that the
///         walls have gates in them — and everything this terrain is *for* happens outside the 32 m
///         perimeter: the ground the arena stands on, which until now was the skybox meeting nothing,
///         and the lake a player can now walk out to.
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

    /// <summary>Where the flat ground around and under the arena sits, in metres.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This was −1.2 m, and the gates are why it is not any more.</b> The number only ever
    ///         had to be "below the floor slab and out of sight", because the perimeter was solid and
    ///         nobody could reach the apron; now every wall has an eight-metre gate in it and the strip
    ///         of ground on the far side of one is the first thing a player steps onto. A 1.2 m drop is
    ///         a one-way trip — <c>PlayerRig</c> asks for 1.1 m of jump — so the arena would have let
    ///         people out and not back in.
    ///     </para>
    ///     <para>
    ///         Twenty centimetres clears the slab, whose top is y = 0 and whose underside is −1 m, so
    ///         nothing inside the walls z-fights and nothing outside them is a cliff. It is a doorstep.
    ///     </para>
    /// </remarks>
    const float ApronHeight = -0.2f;

    /// <summary>Where the lake sits, in world metres on the ground plane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Ten metres past the north gate, which is the whole of why it is here and not
    ///         somewhere prettier.</b> The wall at <c>z = −32</c> has a gap in it
    ///         (<c>Arena.vxscene</c>'s <c>Wall0A</c>/<c>Wall0B</c>), and a player who walks through it
    ///         reaches the shore in about three seconds. Water nobody can get to is water nobody can
    ///         tell is broken.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>North rather than south, and that is arithmetic rather than taste.</b> The
    ///         level's sun travels along roughly <c>(−0.57, −0.14, 0.81)</c> — eight degrees up — so a
    ///         six-metre wall throws its shadow forty-three metres in the direction the light goes,
    ///         which is <em>+Z</em>. The first lake this sample had was centred at <c>(0, 62)</c> and
    ///         was therefore inside that shadow along its whole length: a correct frame and a broken
    ///         one were the same black band, which is exactly the trap this level's own README warns
    ///         about for the arena floor. Mirrored to <c>−Z</c> the wall's shadow falls away from the
    ///         water and the low sun rakes across it, which is both the honest picture and the good
    ///         one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>These four numbers are the terrain's and the water's at once, and they have to
    ///         be.</b> The bed below is carved here, into the committed heightfield; the surface above
    ///         is <c>Arena.vxscene</c>'s <c>!WaterBodyComponent</c> and the ring is
    ///         <c>Assets/Water/Lake.vxspline</c>. Depth is <em>surface minus ground</em> — doc 35 § D3
    ///         stores neither — so a bed dug here and a surface typed there that disagree do not
    ///         produce an error, they produce a lake that is dry, or one whose shoreline is nowhere
    ///         near its bank. <see cref="LakeSurface" /> is the number the scene repeats, and the
    ///         sample's own test asserts the two agree.
    ///     </para>
    /// </remarks>
    public static readonly Vector2 LakeCentre = new(0f, -62f);

    /// <summary>How far the ring in <c>Lake.vxspline</c> reaches from the centre, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Smaller than <see cref="LakeBowl" />, and the gap between them is not slack.</b> A
    ///     body's coverage comes from its spline and its depth comes from <em>surface minus
    ///     ground</em>, and the two have to run out in the same place or the shoreline is drawn twice:
    ///     a bed that rises above the surface inside the ring gives a band of coverage with negative
    ///     depth — water the field says is there and the shading says is not — which reads as a
    ///     flickering rim rather than as a beach. Digging the bowl six metres wider than the ring puts
    ///     roughly a hand's depth of water under the whole boundary, so coverage runs out first.
    /// </remarks>
    public const float LakeRadius = 20f;

    /// <summary>How far the bed is dug out to before the shelf begins, in metres.</summary>
    const float LakeBowl = 26f;

    /// <summary>How wide the shelf is that returns the dug bed to the hills, in metres.</summary>
    const float LakeShelf = 20f;

    /// <summary>Where the ground stands at the bowl's edge, in metres.</summary>
    const float LakeRim = 1.6f;

    /// <summary>How far below the rim the middle of the bed sits, in metres.</summary>
    const float LakeDepth = 4.2f;

    /// <summary>Where the lake's surface sits, in world metres.</summary>
    /// <remarks>
    ///     Forty centimetres below the rim, so the water runs out inside the dug bowl rather than
    ///     exactly at its edge — see <see cref="LakeRadius" />. The deepest water is then
    ///     <see cref="LakeDepth" /> − 0.4 = 3.8 m, which is over a 1.8 m capsule's swim threshold
    ///     with room to spare — see <c>WaterImmersionSystem</c>.
    /// </remarks>
    public const float LakeSurface = LakeRim - 0.4f;

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

            // And out of the lake, which is the quarry's problem upside down: there *is* ground, it
            // is simply under three metres of water, and a bush standing on it is a bush standing on
            // the bed with its leaves in the swell. Same predicate the paint uses, so a lake that
            // moves moves both.
            if (LakeShare(x, z) > 0.2f) {
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
    ///     Flat at <see cref="ApronHeight" /> under the arena and for a few metres past its walls,
    ///     then rising into two
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

        return Basin(dx, dz, ((1f - t) * ApronHeight) + (t * (hills + 5.5f)));
    }

    /// <summary>The lake's bed, carved into whatever the hills were doing there.</summary>
    /// <remarks>
    ///     <para>
    ///         A bowl inside <see cref="LakeRadius" /> that reaches <see cref="LakeRim" /> exactly at
    ///         the shoreline, and a shelf that blends the rim back into the hills over
    ///         <see cref="LakeShelf" /> metres. Both halves meet at the rim, so the surface is
    ///         continuous — which matters more here than the shape does, because a step in the ground
    ///         under water is a step in the *depth*, and depth is what the surface's colour, its foam
    ///         and its wave attenuation are all read from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Carved rather than found.</b> These hills have no basin anywhere: the generator is
    ///         two octaves of sine over a radial ramp, which produces saddles and no closed contour
    ///         below its surroundings. Dropping a water body onto the nearest dip would have given a
    ///         lake with one open side that drained across the map — visibly, because the shoreline is
    ///         where the field says depth reaches zero and the field reads this function.
    ///     </para>
    /// </remarks>
    static float Basin(float dx, float dz, float natural) {
        var toCentre = MathF.Sqrt(
            ((dx - LakeCentre.X) * (dx - LakeCentre.X)) + ((dz - LakeCentre.Y) * (dz - LakeCentre.Y))
        );

        if (toCentre >= LakeBowl + LakeShelf) {
            return natural;
        }

        // The rim at the bowl's edge and the full depth at the middle, smooth at both ends so the bed
        // has no crease a normal would catch the light on.
        var bowl = LakeRim - (LakeDepth * (1f - SmoothStep(0f, LakeBowl, toCentre)));
        var shelf = SmoothStep(LakeBowl, LakeBowl + LakeShelf, toCentre);

        return ((1f - shelf) * bowl) + (shelf * natural);
    }

    /// <summary>How far into the lake a position is: one at the middle, zero at the shelf's edge.</summary>
    /// <remarks>
    ///     The one predicate the paint and the bushes share, so "do not stand in the water" and "do
    ///     not paint grass on the bed" cannot drift apart. World metres, as
    ///     <see cref="LakeCentre" /> is.
    /// </remarks>
    static float LakeShare(float dx, float dz) {
        var toCentre = MathF.Sqrt(
            ((dx - LakeCentre.X) * (dx - LakeCentre.X)) + ((dz - LakeCentre.Y) * (dz - LakeCentre.Y))
        );

        return 1f - SmoothStep(LakeBowl * 0.5f, LakeBowl + (LakeShelf * 0.5f), toCentre);
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

                // ⚠ The lake bed is bare for the same reason the apron is, and the reason is not
                // taste: the Grass layer is what `Outskirts.vxgrass` scatters on, so painting grass
                // under the water grows a lawn on the bed — visible through a transparent surface,
                // and swaying, because the wind displacement does not know it is submerged.
                var bare = MathF.Max(apron, LakeShare(dx, dz));

                var rockShare = SmoothStep(0.35f, 0.75f, slope);
                var dirtShare = (1f - rockShare) * bare;
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
