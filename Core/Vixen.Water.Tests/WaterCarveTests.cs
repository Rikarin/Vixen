// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Vixen.Water;
using Xunit;

using TerrainAsset = Vixen.Terrain.Terrain;

namespace Vixen.Water.Tests;

/// <summary>
///     Bodies cutting their beds into the reserved layer — [docs/plan/35 § D5], and W5's exit criteria.
/// </summary>
/// <remarks>
///     <para>
///         Three claims, and the second and third are the ones that make it non-destructive: a body
///         laid across sculpted ground cuts a bed and a bank; <em>moving</em> it restores the old
///         ground and cuts the new; and a shoreline sculpted by hand in a layer above survives both.
///     </para>
///     <para>
///         ⚠ <b>The third is § D5's own stated gate</b>, and the reason the terracing, the curl octaves
///         and the shape blur were left out of the carve profile. If a hand-sculpted shoreline did not
///         survive a body being moved, the procedural version would be mandatory rather than optional.
///     </para>
/// </remarks>
public sealed class WaterCarveTests {
    static TerrainDescription Shape() =>
        TerrainDescription.Default with {
            TileSamples = 128,
            TilesX = 1,
            TilesZ = 1,
            MetresPerQuad = 1f,
            MinHeight = -100f,
            MaxHeight = 100f
        };

    /// <summary>Flat ground at ten metres, so a bed at seven is visibly a cut.</summary>
    static TerrainAsset Ground(float height = 10f) => new(Shape(), height);

    /// <summary>A square lake with its low corner at a place, three metres deep.</summary>
    static WaterBody Lake(Vector2 low, float side = 24f, float surface = 10f, float depth = 3f) {
        var spline = new Spline(
            Spline.SmoothTangents(
                [
                    new(low.X, surface, low.Y),
                    new(low.X + side, surface, low.Y),
                    new(low.X + side, surface, low.Y + side),
                    new(low.X, surface, low.Y + side)
                ],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        return new(WaterBodyKind.Lake, spline, defaults: new() { Depth = depth }) {
            SurfaceHeight = surface,
            ShoreFalloff = 2f,
            BedRamp = 4f
        };
    }

    /// <summary>How high the composited ground is at a sample, in metres.</summary>
    static float HeightAt(TerrainAsset terrain, int x, int z) =>
        terrain.Description.HeightOf(terrain.CompositeAt(x, z));

    /// <summary>What the untouched ground reads at, which is not exactly the metres asked for.</summary>
    /// <remarks>
    ///     ⚠ Heights are stored in 16 bits over the terrain's whole range — three millimetres a step
    ///     across 200 m here — so "flat at ten metres" composites at 9.99924. Asserting against the
    ///     literal would be asserting the quantisation away, and the claim being made is that the
    ///     ground is *what it was*, which is this.
    /// </remarks>
    static float Untouched => HeightAt(Ground(), 5, 5);

    // --- The bed and the bank -----------------------------------------------

    /// <summary>A lake cuts a bed at its full depth and a bank that runs out to nothing.</summary>
    [Fact]
    public void A_body_cuts_a_bed_and_a_bank() {
        var terrain = Ground();
        var layer = WaterCarve.LayerOf(terrain);

        WaterCarve.Carve(terrain, layer, Lake(new(40f, 40f)), WaterCarveProfile.Default);

        // The middle, which is a full bed ramp inside the boundary: the full three metres.
        Assert.Equal(7f, HeightAt(terrain, 52, 52), 1);

        // The bank, which runs from the bed up to the untouched ground over the ramp and the
        // falloff — monotone, because a bank with a step in it is a cliff nobody authored.
        var previous = HeightAt(terrain, 52, 52);

        for (var z = 52; z <= 70; z++) {
            var here = HeightAt(terrain, 52, z);

            Assert.True(here >= previous - 1e-3f, $"the bank went back down at z = {z}.");
            previous = here;
        }

        // And well outside it, the ground is exactly what it was.
        Assert.Equal(Untouched, HeightAt(terrain, 52, 90), 4);
        Assert.Equal(Untouched, HeightAt(terrain, 5, 5), 4);
    }

    /// <summary>An island raises instead of lowering — the same mechanism with the sign flipped.</summary>
    [Fact]
    public void An_island_raises_the_ground_it_displaces() {
        var terrain = Ground();
        var layer = WaterCarve.LayerOf(terrain);

        var island = new WaterBody(
            WaterBodyKind.Island,
            Lake(new(40f, 40f)).Spline,
            defaults: new() { Depth = 4f }
        ) { SurfaceHeight = 10f, ShoreFalloff = 2f, BedRamp = 4f };

        WaterCarve.Carve(terrain, layer, island, WaterCarveProfile.Default);

        Assert.Equal(14f, HeightAt(terrain, 52, 52), 1);
        Assert.Equal(Untouched, HeightAt(terrain, 5, 5), 4);
    }

    /// <summary>A carve only cuts. A lake over a valley does not fill the valley in.</summary>
    /// <remarks>
    ///     ⚠ The bed is where a body <em>wants</em> the ground, not where it insists on it. Ground
    ///     already deeper than the bed is a trench somebody dug on purpose, and a carve that raised it
    ///     would be a body silently undoing a sculpt — which is exactly what edit layers exist to
    ///     prevent.
    /// </remarks>
    [Fact]
    public void A_carve_never_raises_ground_that_is_already_deeper() {
        var terrain = Ground();

        // A trench through the middle of where the lake will be, cut into the base.
        for (var z = 48; z < 56; z++) {
            for (var x = 40; x < 70; x++) {
                terrain.Base[x, z] = terrain.Description.StoreHeight(2f);
            }
        }

        terrain.Invalidate(new(40, 48, 30, 8));

        var layer = WaterCarve.LayerOf(terrain);
        WaterCarve.Carve(terrain, layer, Lake(new(40f, 40f)), WaterCarveProfile.Default);

        Assert.Equal(2f, HeightAt(terrain, 52, 52), 1);
    }

    // --- Non-destructiveness, which is the whole point ----------------------

    /// <summary>Moving a body restores the ground it left and cuts the ground it arrived at.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing is undone.</b> The layer is deltas over ground it never touched, so emptying
    ///     it restores exactly what was sculpted — which is why <see cref="WaterCarve.Regenerate" />
    ///     is one operation rather than an inverse carve followed by a new one.
    /// </remarks>
    [Fact]
    public void Moving_a_body_restores_the_old_ground_and_cuts_the_new() {
        var terrain = Ground();
        var layer = WaterCarve.LayerOf(terrain);

        WaterCarve.Regenerate(terrain, layer, [(Lake(new(20f, 20f)), WaterCarveProfile.Default)]);

        Assert.Equal(7f, HeightAt(terrain, 32, 32), 1);
        Assert.Equal(Untouched, HeightAt(terrain, 82, 82), 1);

        WaterCarve.Regenerate(terrain, layer, [(Lake(new(70f, 70f)), WaterCarveProfile.Default)]);

        // Where it was: back to exactly the ground, not approximately.
        Assert.Equal(Untouched, HeightAt(terrain, 32, 32), 4);

        // Where it is now: cut.
        Assert.Equal(7f, HeightAt(terrain, 82, 82), 1);
    }

    /// <summary>
    ///     A shoreline sculpted by hand in a layer above survives a body being moved. § D5's gate.
    /// </summary>
    /// <remarks>
    ///     The claim doc 31's storage model makes, tested against the feature that most obviously
    ///     wants it: the water layer is regenerated wholesale, the sculpt is in a different layer, and
    ///     composition is addition — so the sculpt is untouched by construction rather than by care.
    /// </remarks>
    [Fact]
    public void A_hand_sculpted_shoreline_above_survives_the_body_moving() {
        var terrain = Ground();
        var water = WaterCarve.LayerOf(terrain);
        var byHand = terrain.AddLayer("Shoreline");

        // A dune along one edge of where the lake will be, in a layer above the water's.
        for (var z = 40; z < 48; z++) {
            for (var x = 20; x < 60; x++) {
                byHand.SetDelta(x, z, (short)(3f / terrain.Description.MetresPerStep));
            }
        }

        terrain.Invalidate(new(20, 40, 40, 8));

        var before = HeightAt(terrain, 30, 44);
        Assert.Equal(13f, before, 1);

        WaterCarve.Regenerate(terrain, water, [(Lake(new(20f, 20f)), WaterCarveProfile.Default)]);
        WaterCarve.Regenerate(terrain, water, [(Lake(new(70f, 70f)), WaterCarveProfile.Default)]);

        Assert.Equal(before, HeightAt(terrain, 30, 44), 3);
    }

    /// <summary>A strength of zero is Unreal's <c>Affects Landscape</c>, and it touches nothing.</summary>
    [Fact]
    public void A_body_that_does_not_carve_leaves_the_ground_alone() {
        var terrain = Ground();
        var layer = WaterCarve.LayerOf(terrain);

        Assert.True(WaterCarve.Carve(terrain, layer, Lake(new(40f, 40f)), WaterCarveProfile.None).IsEmpty);
        Assert.Equal(Untouched, HeightAt(terrain, 52, 52), 4);
    }

    /// <summary>The reserved layer is the reserved layer, however many times it is asked for.</summary>
    [Fact]
    public void The_water_layer_is_reserved_once() {
        var terrain = Ground();

        Assert.Same(WaterCarve.LayerOf(terrain), WaterCarve.LayerOf(terrain));
        Assert.Equal(TerrainLayerKind.Water, WaterCarve.LayerOf(terrain).Kind);
    }

    /// <summary>Where two bodies overlap, the deeper one's bed is what the ground takes.</summary>
    /// <remarks>
    ///     ⚠ Sorted inside <see cref="WaterCarve.Regenerate" /> rather than left to the caller, on
    ///     <c>WaterField.Rasterize</c>'s reasoning: a bed that depended on the order a scene walked
    ///     its entities in is one where moving an unrelated body changes the ground at a river mouth.
    /// </remarks>
    [Fact]
    public void Two_bodies_that_overlap_give_the_ground_the_deeper_bed() {
        var shallow = Lake(new(40f, 40f), depth: 2f);
        var deep = Lake(new(44f, 44f), side: 16f, depth: 6f);

        var forwards = Ground();
        var backwards = Ground();

        WaterCarve.Regenerate(
            forwards,
            WaterCarve.LayerOf(forwards),
            [(shallow, WaterCarveProfile.Default), (deep, WaterCarveProfile.Default)]
        );

        WaterCarve.Regenerate(
            backwards,
            WaterCarve.LayerOf(backwards),
            [(deep, WaterCarveProfile.Default), (shallow, WaterCarveProfile.Default)]
        );

        Assert.Equal(4f, HeightAt(forwards, 52, 52), 1);
        Assert.Equal(HeightAt(forwards, 52, 52), HeightAt(backwards, 52, 52), 3);
    }
}
