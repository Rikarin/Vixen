// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>A terrain to sculpt, and the two questions every test here asks about one.</summary>
static class Ground {
    /// <summary>
    ///     Four tiles of 32 samples at a metre a quad: 63 × 63 samples over 62 × 62 metres.
    /// </summary>
    /// <remarks>
    ///     Small enough that a test walks every sample and large enough that a stroke in the middle
    ///     touches one tile and a stroke on the seam touches four — which is the property the collider
    ///     rebuild is checked against.
    /// </remarks>
    public static TerrainDescription Shape =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    /// <summary>A flat terrain with one manual layer on it.</summary>
    /// <param name="layer">What the layer is called.</param>
    /// <returns>The terrain.</returns>
    public static TerrainMap Terrain(string layer = "Sculpt") {
        var terrain = new TerrainMap(Shape);

        terrain.AddLayer(layer);
        terrain.Resolve();

        return terrain;
    }

    /// <summary>
    ///     What untouched ground reads back as, which is not zero.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A height is stored as one of 65 536 steps over the authored range, so "flat at zero"
    ///     is the nearest step to zero and reads back a millimetre and a half off.</b> Over
    ///     −100…100 m a step is 3 mm; asserting an untouched sample equals <c>0f</c> to three decimal
    ///     places is asserting a precision the storage does not have, and it fails on ground nothing
    ///     ever touched. Every "this was not changed" assertion here compares against this.
    /// </remarks>
    public static float Rest { get; } = Shape.HeightOf(Shape.StoreHeight(0f));

    /// <summary>What the ground is at a sample, in metres, after recompositing.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>The height.</returns>
    public static float HeightAt(TerrainMap terrain, int x, int z) {
        terrain.Resolve();
        return terrain.Composite.MetresAt(x, z);
    }

    /// <summary>Whether a sample is where an untouched one would be.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>Whether it is within one storage step of rest.</returns>
    public static bool IsUntouched(TerrainMap terrain, int x, int z) =>
        MathF.Abs(HeightAt(terrain, x, z) - Rest) <= Shape.MetresPerStep;

    /// <summary>An editing state over a fresh terrain, with a brush a test can aim.</summary>
    /// <param name="radius">How far the brush reaches, in metres.</param>
    /// <returns>The state and its terrain.</returns>
    public static (TerrainEdit Edit, TerrainMap Terrain) Editing(float radius = 6f) {
        var terrain = Terrain();
        var edit = new TerrainEdit { Terrain = terrain };

        edit.Brush.Radius = radius;
        edit.Brush.Strength = 1f;

        return (edit, terrain);
    }
}

/// <summary>A collider rebuilder that only remembers what it was asked to rebuild.</summary>
/// <remarks>
///     ⚠ <b>What is asserted is which tiles were named, not what shape came out.</b> Building the
///     Jolt height field is <c>Vixen.Physics</c>'s job and has its own tests — including the two
///     undocumented constraints that cost a day — and this assembly deliberately cannot reference it.
///     What T3 owes is that a stroke names the tiles it touched and no others, which is the half that
///     can go wrong here.
/// </remarks>
sealed class RecordingColliders : ITerrainColliders {
    readonly List<(int X, int Z)> rebuilt = [];

    /// <summary>Every tile that was rebuilt, in order, repeats included.</summary>
    public IReadOnlyList<(int X, int Z)> Rebuilt => rebuilt;

    /// <summary>Every distinct tile that was rebuilt.</summary>
    public IReadOnlySet<(int X, int Z)> Tiles => rebuilt.ToHashSet();

    /// <summary>Forgets everything, so a test can measure one operation.</summary>
    public void Clear() => rebuilt.Clear();

    /// <inheritdoc />
    public void Rebuild(TerrainMap terrain, int tileX, int tileZ) => rebuilt.Add((tileX, tileZ));
}
