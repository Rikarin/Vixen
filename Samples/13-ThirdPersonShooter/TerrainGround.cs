// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Renderer;
using Vixen.Engine.Transforms;
using Vixen.Physics;
using Vixen.Physics.Ecs;
using Vixen.Physics.Shapes;
using Vixen.Terrain;
using Vixen.Water;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>
///     Gives the outskirts a collider and the lake a bed: the one bridge between the terrain the
///     renderer draws and the two subsystems that have to know where it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This closes a gap in the engine, and the gap is worth naming before the code is
///         read.</b> Every piece of the terrain-collision path exists and is tested, and nothing
///         anywhere joined them up:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>PhysicsShapes.HeightField</c> registers a Jolt height-field shape and
///             <c>Vixen.Physics.Tests/HeightFieldShapeTests</c> exercises it thoroughly.
///         </item>
///         <item>
///             <c>TerrainSamples.FillCollisionSamples</c> produces exactly the span that shape
///             consumes — metres, row-major, holes as the caller's sentinel — and its only callers
///             were two assertions in <c>TerrainSculptTests</c>.
///         </item>
///         <item>
///             <c>Editor/Vixen.Editor.Terrain/ITerrainColliders</c> is the seam the sculpt tools call
///             after every stroke, and the only implementation of it in the tree is a test double that
///             records tile indices.
///         </item>
///     </list>
///     <para>
///         So a terrain in a <em>game</em> had no collision at all, in any project, and the symptom
///         was not an error: a character walked off the arena floor, fell through the ground, and
///         <c>RespawnWhenBelow</c> put them back at the spawn point. <c>Content/README.md</c> recorded
///         that as a known limitation of taking pictures of the grass. It is a limitation of the
///         engine, and it is a
///         <b>finished consumer nothing feeds</b>: the shape, the sample fill and the editor seam were
///         all built and the one call joining them was never written.
///     </para>
///     <para>
///         ⚠ <b>Why the join is in a sample and not in <c>Vixen.Rendering.Terrain</c>.</b> That
///         assembly does not reference <c>Vixen.Physics</c> and must not — nothing in the rendering
///         stack does, and adding it would drag Jolt into every project that draws a hill. The join
///         belongs in a small assembly of its own, on <c>Vixen.Water.Physics</c>' precedent, and the
///         sample is where it can be demonstrated without deciding that. See the report accompanying
///         this change.
///     </para>
///     <para>
///         <b>One shape per tile, and the tile is the unit for the reason the editor's seam already
///         says so:</b> a sculpt stroke dirties tiles, so a rebuild is per tile. This terrain is 2 × 2
///         tiles of 64 samples, which is four static bodies over 252 m.
///     </para>
///     <para>
///         ⚠ <b>It is a system rather than a line in <c>Arena.Load</c> because the terrain arrives
///         late.</b> <c>AssetTerrainSource.Terrain</c> starts a load and returns <see langword="null" />
///         until it finishes, which is several frames after the level is up. A one-shot call at load
///         time gets null, silently, and builds nothing — so this asks every frame until it has the
///         map, builds once, and then does nothing for the rest of the run.
///     </para>
/// </remarks>
/// <param name="physics">The scene the bodies are created in.</param>
/// <param name="terrains">Where the <c>.vxterrain</c> comes from.</param>
/// <param name="logger">Where the one line it writes goes.</param>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class TerrainGroundSystem(
    PhysicsScene physics,
    AssetTerrainSource terrains,
    ILogger logger
) : SystemBase, IWaterGround {
    readonly PhysicsScene physics = physics ?? throw new ArgumentNullException(nameof(physics));
    readonly AssetTerrainSource terrains = terrains ?? throw new ArgumentNullException(nameof(terrains));
    readonly ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Where the terrain grid's own origin sits in the world.</summary>
    /// <remarks>
    ///     <c>Arena.SpawnGround</c>'s translation, repeated here because a collider and a mesh in two
    ///     different places is the failure this whole class is built to avoid, and because the ground
    ///     query below has to undo it. Both read <c>TerrainSeed.HalfExtent</c> rather than a literal.
    /// </remarks>
    public static Vector3 Origin => new(-TerrainSeed.HalfExtent, 0f, -TerrainSeed.HalfExtent);

    /// <summary>The heightfield, once it has loaded. Null before that.</summary>
    public TerrainMap? Terrain { get; private set; }

    /// <summary>How many tiles have a collider.</summary>
    public int TileCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>What the water's depth is measured against, and without it every lake in the level is
    ///     as deep as its own surface height above y = 0.</b> <c>WaterZoneSystem.Ground</c> defaults to
    ///     <c>FlatWaterGround(0)</c> and <em>nothing in the engine ever sets it</em> outside the
    ///     editor's own presenter — the same shape of gap as the collider above, one subsystem over.
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
        if (Terrain is not null) {
            return dependency;
        }

        if (terrains.Terrain(TerrainSeed.TerrainPath) is not { } map) {
            return dependency;
        }

        Terrain = map;
        Build(context.World, map);

        return dependency;
    }

    /// <summary>One static body per tile, each carrying that tile's height-field shape.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An entity per tile with a <c>Collider</c> and a <c>LocalTransform</c> and no
    ///         <c>RigidBody</c>,</b> which is how <c>PhysicsScene</c> is told "static": its query is
    ///         <c>WithAll&lt;Collider, LocalTransform&gt;().WithNone&lt;PhysicsBody&gt;()</c> and the
    ///         motion falls back to <see cref="BodyMotion.Static" /> when there is no rigid body
    ///         beside it. A height field cannot be dynamic — <c>ShapeDescription.CanBeDynamic</c> says
    ///         so — so anything else would throw.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tile's own corner goes in the shape's offset, not in the entity's
    ///         transform.</b> Both would work and only one survives the terrain entity being moved:
    ///         a shape is interned by its description, so four tiles at four transforms over one
    ///         description would be one shape drawn four times in the same place. The offset is part
    ///         of the description, so the four are four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Tiles share their boundary samples and the colliders therefore overlap by one
    ///         quad.</b> That is the terrain's own arithmetic — 2 × 2 tiles of 64 samples is 127
    ///         samples across, not 128 — and it is right: a gap of one quad between two height fields
    ///         is a seam a character falls through, and an overlap of one quad is two surfaces at
    ///         identical heights, which Jolt resolves as one.
    ///     </para>
    /// </remarks>
    void Build(World world, TerrainMap map) {
        var description = map.Description;
        var samples = description.TileSamples;
        var heights = new float[samples * samples];

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                // Metres, row-major, holes written as Jolt's own sentinel — so the quarry north-east
                // of the arena is a pit a character falls into rather than a floor at its rim.
                map.Composite.FillCollisionSamples(
                    tileX,
                    tileZ,
                    map.Holes,
                    PhysicsShapes.NoCollisionHeight,
                    heights
                );

                var corner = new Vector3(
                    tileX * description.TileQuads * description.MetresPerQuad,
                    0f,
                    tileZ * description.TileQuads * description.MetresPerQuad
                );

                var shape = physics.Shapes.HeightField(
                    heights,
                    samples,
                    corner,
                    new(description.MetresPerQuad, 1f, description.MetresPerQuad)
                );

                var placement = LocalTransform.At(Origin);
                var tile = world.Create(placement, new WorldTransform { Value = placement.ToMatrix() });

                // The grass's friction, which is the apron's number in Arena.vxscene rather than a
                // new one: the ground outside the walls should not be slipperier than the floor
                // inside them.
                world.Add(tile, Collider.Of(shape) with { Friction = 0.8f });

                TileCount++;
            }
        }

        SampleLog.TerrainCollisionBuilt(logger, TileCount, samples, description.MetresPerQuad);
    }
}
