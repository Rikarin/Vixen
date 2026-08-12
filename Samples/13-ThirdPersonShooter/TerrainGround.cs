// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Renderer;
using Vixen.Terrain;
using Vixen.Water;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>
///     Tells the lake how deep it is: the bridge between the terrain the renderer draws and the water
///     stack, which has no other way to find the ground.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This used to build the terrain's collision as well, and that half has moved into the
///         engine.</b> <c>Vixen.Terrain.Physics.TerrainColliderSystem</c> is the call that was missing
///         — every piece of the path existed and nothing joined them — and it belongs there rather
///         than here because it is not a fact about this level. <c>Arena.Register</c> adds it, over
///         <c>WorldRenderer.TerrainScene</c>, which is the same list the frame draws from. This class
///         kept the half that genuinely is a fact about this level: where the lake's bed is.
///     </para>
///     <para>
///         ⚠ <b>It is a system rather than a line in <c>Arena.Load</c> because the terrain arrives
///         late.</b> <c>AssetTerrainSource.Terrain</c> starts a load and returns <see langword="null" />
///         until it finishes, which is several frames after the level is up. A one-shot call at load
///         time gets null, silently, and holds it — so this asks every frame until it has the map.
///     </para>
/// </remarks>
/// <param name="terrains">Where the <c>.vxterrain</c> comes from.</param>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class TerrainGroundSystem(AssetTerrainSource terrains) : SystemBase, IWaterGround {
    readonly AssetTerrainSource terrains = terrains ?? throw new ArgumentNullException(nameof(terrains));

    /// <summary>Where the terrain grid's own origin sits in the world.</summary>
    /// <remarks>
    ///     <c>Arena.SpawnGround</c>'s translation, repeated here because a bed and a mesh in two
    ///     different places is the failure this class is built to avoid, and because the ground query
    ///     below has to undo it. Both read <c>TerrainSeed.HalfExtent</c> rather than a literal.
    /// </remarks>
    public static Vector3 Origin => new(-TerrainSeed.HalfExtent, 0f, -TerrainSeed.HalfExtent);

    /// <summary>The heightfield, once it has loaded. Null before that.</summary>
    public TerrainMap? Terrain { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>What the water's depth is measured against, and without it every lake in the level is
    ///     as deep as its own surface height above y = 0.</b> <c>WaterZoneSystem.Ground</c> defaults to
    ///     <c>FlatWaterGround(0)</c> and <em>nothing in the engine ever sets it</em> outside the
    ///     editor's own presenter — the same shape of gap the collider was, one subsystem over.
    ///     Doc 35 § D3 is explicit that the terrain is a first-class producer of the ground channel;
    ///     this is a game being that producer.
    ///
    ///     ⚠ Before the map has loaded this answers the apron height rather than zero. Zero is the
    ///     lake's own surface, so a frame answered with it is a lake of exactly no depth — which draws
    ///     as nothing at all and reads as the water stack being unwired.
    /// </remarks>
    public float HeightAt(Vector2 ground) =>
        Terrain is { } map
            ? TerrainPick.HeightAt(map, ground.X - Origin.X, ground.Y - Origin.Z) + Origin.Y
            : TerrainSeed.LakeSurface - 4f;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Terrain ??= terrains.Terrain(TerrainSeed.TerrainPath);

        return dependency;
    }
}
