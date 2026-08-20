// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Physics.Ecs;
using Vixen.Physics.Shapes;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Terrain.Physics;

/// <summary>Gives every terrain in the world a collider: one height-field shape per tile.</summary>
/// <remarks>
///     <para>
///         <b>[31 § D10](../../docs/plan/31-terrain-grass-and-trees.md#d10-collision-is-the-heightfield-and-trees-are-collided-within-a-radius),
///         and the call that was missing.</b> Every piece of this path was built and tested and
///         nothing joined them: <c>PhysicsShapes.HeightField</c> registers the Jolt shape,
///         <c>TerrainSamples.FillCollisionSamples</c> produces exactly the span it consumes — metres,
///         row-major, holes as the caller's sentinel — and <c>ITerrainPlacements</c> says where the
///         ground is. So a terrain in a <em>game</em> had no collision at all, in any project, and the
///         symptom was not an error: a character walked onto it and fell through.
///     </para>
///     <para>
///         ⚠ <b>The tile is the unit, and that is [§ D2] rather than an optimisation.</b> One tile is
///         one shape, so a stroke that dirties one tile out of sixteen rebuilds one — and a terrain
///         whose collision was one shape would pause for the whole terrain every time somebody
///         smoothed a ridge.
///     </para>
///     <para>
///         ⚠ <b>Tiles share their boundary samples, so the shapes overlap by one quad.</b> That is
///         the terrain's own arithmetic — <c>TileQuads</c> is <c>TileSamples − 1</c> — and it is
///         right: a gap of one quad between two height fields is a seam a character falls through,
///         and an overlap is two surfaces at identical heights, which Jolt resolves as one.
///     </para>
///     <para>
///         ⚠ <b>It asks every frame rather than building once, and that is the whole reason it is a
///         system.</b> A <c>.vxterrain</c> is tens of megabytes: <c>ITerrainAssetSource</c> starts a
///         load and answers null for several frames, so a one-shot call at level load gets nothing
///         and builds nothing, silently. Once a terrain has bodies the per-frame cost is one integer
///         compare per tile.
///     </para>
///     <para>
///         ⚠ <b>A tile is rebuilt when its composite changes, without anybody having to say so.</b>
///         <see cref="TerrainMap.RevisionOf" /> counts recomposites, so a stroke that ran through
///         <see cref="TerrainMap.Resolve" /> moves the collider on the next frame. <c>ITerrainColliders</c>
///         — the editor's seam — is the synchronous form of the same job for a tool that cannot wait
///         a frame; <see cref="Rebuild(TerrainMap, TerrainRect)" /> is what such an adapter calls.
///     </para>
///     <para>
///         ⚠ <b>Holes are watched by count, which is coarse and is the honest bound.</b>
///         <see cref="TerrainHoles" /> carries no per-tile version and punching one moves no height,
///         so the poll cannot see <em>which</em> tile changed and rebuilds that terrain's tiles when
///         <see cref="TerrainHoles.HoleCount" /> moves. A tool that punches and fills within one frame
///         nets zero and is missed; that tool should call <see cref="Rebuild(TerrainMap, TerrainRect)" />,
///         which is precise.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="SystemPhase.EarlyUpdate" />, so the bodies exist before the step.</b>
///         <c>PhysicsSyncSystem</c> creates a Jolt body for every entity with a <see cref="Collider" />
///         and a <see cref="LocalTransform" /> and no <c>RigidBody</c> beside it, which is how a static
///         body is spelled — a height field cannot be dynamic and <c>ShapeDescription.CanBeDynamic</c>
///         says so, so anything else would throw.
///     </para>
/// </remarks>
/// <param name="scene">The physics world the bodies are created in.</param>
/// <param name="placements">Where the terrains are.</param>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class TerrainColliderSystem(PhysicsScene scene, ITerrainPlacements placements)
    : SystemBase, IDeclaredAccess {
    readonly PhysicsScene scene = scene ?? throw new ArgumentNullException(nameof(scene));
    readonly Dictionary<TerrainMap, Tiles> built = [];
    readonly List<TerrainMap> seen = [];
    readonly List<TerrainMap> stale = [];
    readonly List<Entity> retiring = [];

    float[] heights = [];

    /// <summary>Where the terrains are, or null to build nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Settable, because a dedicated server's answer is not a renderer.</b> On a client this
    ///     is <c>TerrainSceneSource</c>, which the extraction pass refills every frame; on a headless
    ///     build it is whatever placed the ground there. The interface is the kernel's for exactly
    ///     that reason — see <see cref="ITerrainPlacements" />.
    /// </remarks>
    public ITerrainPlacements? Placements { get; set; } =
        placements ?? throw new ArgumentNullException(nameof(placements));

    /// <summary>The friction every tile's collider is given.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="Collider.Of" />'s 0.2, which is nearly ice.</b> That default is for a
    ///     prop; ground a character walks up is the one surface where the number is felt, and a slope
    ///     a player slides down is the symptom. One number for the whole terrain, because a
    ///     per-material friction would need the paint weights, which is a decision this does not make.
    /// </remarks>
    public float Friction { get; set; } = 0.8f;

    /// <summary>How many terrains have colliders.</summary>
    public int TerrainCount => built.Count;

    /// <summary>How many tile bodies exist across all of them.</summary>
    /// <remarks>
    ///     Zero with a terrain in the scene is the failure this system exists to end, and it is silent
    ///     otherwise: it draws as ground a character falls through rather than as an error.
    /// </remarks>
    public int TileCount { get; private set; }

    /// <summary>How many tiles have been rebuilt since the world started, after their first build.</summary>
    /// <remarks>
    ///     A number that climbs every frame is a terrain being recomposited every frame, which is a
    ///     shape registered per frame in a table that never releases one — see <see cref="Build" />.
    /// </remarks>
    public int Rebuilds { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared at construction rather than with attributes, for the reason <c>TransformSystem</c>
    ///     gives: naming a component type in a generic call is what assigns it an id, and on the first
    ///     frame an attribute would have nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Write<Collider>()
        .Write<LocalTransform>()
        .Write<WorldTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Sync();

        return dependency;
    }

    /// <summary>Brings the colliders into step with the placements, once.</summary>
    /// <remarks>Public so a test or a tool can do it without standing up a frame loop.</remarks>
    public void Sync() {
        Bury();
        seen.Clear();

        if (Placements is { } source) {
            for (var index = 0; index < source.PlacementCount; index++) {
                var placement = source.PlacementAt(index);

                if (placement.Terrain is not { } terrain) {
                    continue;
                }

                seen.Add(terrain);
                Ensure(terrain, placement.Origin);
            }
        }

        // ⚠ A terrain that stopped being placed takes its bodies with it. Without this, unloading a
        // level leaves a landscape's worth of static collision standing in the world with nothing
        // drawing it — invisible, and the player walks into thin air.
        stale.Clear();

        foreach (var terrain in built.Keys) {
            if (!seen.Contains(terrain)) {
                stale.Add(terrain);
            }
        }

        foreach (var terrain in stale) {
            Forget(terrain);
        }
    }

    /// <summary>Rebuilds one tile's collision shape now.</summary>
    /// <param name="terrain">The terrain, already recomposited.</param>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>Whether the terrain had colliders to rebuild.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tile is not one of the terrain's.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Call it after <see cref="TerrainMap.Resolve" />, never before.</b> A collider built
    ///         from a stale composite is ground the player falls through in exactly the places that were
    ///         most recently edited.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An out-of-range tile throws, and it used to corrupt a different tile in silence.</b>
    ///         Everything downstream of here indexes flat — <c>tileZ * TilesX + tileX</c> for the
    ///         entity, the same arithmetic in <see cref="TerrainMap.RevisionOf" />, and
    ///         <c>TerrainSamples</c>' indexer <em>clamps</em> rather than throwing because every kernel
    ///         reads its neighbours. So <c>Rebuild(terrain, TilesX, 0)</c> was tile <c>(0, 1)</c>: it
    ///         overwrote that tile's shape with the edge row repeated, placed at an off-grid corner, and
    ///         then stamped <c>Revisions</c> with the aliased tile's real revision — which is what made
    ///         it permanent, because <see cref="Sync" />'s poll compares that stamp and never looks
    ///         again. The rect overload cannot reach this: <see cref="TerrainDescription.TilesOf" />
    ///         clamps. A caller naming a tile by hand can, and this is the seam
    ///         <c>ITerrainColliders</c> hands to any tool.
    ///     </para>
    /// </remarks>
    public bool Rebuild(TerrainMap terrain, int tileX, int tileZ) {
        ArgumentNullException.ThrowIfNull(terrain);

        // ⚠ Before the lookup, not after. The index is the caller's own error whether or not this
        // system has ever heard of the terrain, and validating only on the path that would corrupt
        // something hides the bug in exactly the hosts that have not wired physics up yet.
        var description = terrain.Description;

        ArgumentOutOfRangeException.ThrowIfNegative(tileX);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tileX, description.TilesX);
        ArgumentOutOfRangeException.ThrowIfNegative(tileZ);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tileZ, description.TilesZ);

        if (!built.TryGetValue(terrain, out var tiles)) {
            return false;
        }

        Build(terrain, tiles, tileX, tileZ);

        return true;
    }

    /// <summary>Rebuilds every tile a rectangle of samples touches.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="rect">Which samples moved.</param>
    /// <returns>Whether the terrain had colliders to rebuild.</returns>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="TerrainDescription.TilesOf" />, which answers with <em>both</em>
    ///     tiles for a sample on a boundary.</b> A stroke along a tile edge that rebuilt only one side
    ///     leaves a strip of collision that disagrees with the ground beside it by whatever the stroke
    ///     moved — a lip the player trips on, on a seam nothing draws.
    /// </remarks>
    public bool Rebuild(TerrainMap terrain, TerrainRect rect) {
        ArgumentNullException.ThrowIfNull(terrain);

        if (!built.TryGetValue(terrain, out var tiles)) {
            return false;
        }

        var touched = terrain.Description.TilesOf(rect);

        for (var tileZ = touched.Z; tileZ < touched.EndZ; tileZ++) {
            for (var tileX = touched.X; tileX < touched.EndX; tileX++) {
                Build(terrain, tiles, tileX, tileZ);
            }
        }

        return true;
    }

    /// <summary>Destroys a terrain's collision bodies.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <returns>Whether it had any.</returns>
    public bool Forget(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        if (!built.Remove(terrain, out var tiles)) {
            return false;
        }

        foreach (var tile in tiles.Entities) {
            if (tile == default || !scene.Entities.IsAlive(tile)) {
                continue;
            }

            // ⚠ Removing the Collider, not destroying the entity, and the order is not a preference.
            // `PhysicsScene` finds a body to destroy by querying for `PhysicsBody` with no `Collider`
            // beside it — so an entity destroyed outright is one that leaves that query without ever
            // appearing in it, and the Jolt body stays in the broad phase for the life of the world:
            // collision a level no longer contains, with nothing drawing it. The entity itself goes
            // in `Bury`, one sync later, once the body it owned has actually gone.
            if (scene.Entities.Has<Collider>(tile)) {
                scene.Entities.Remove<Collider>(tile);
            }

            retiring.Add(tile);
        }

        TileCount -= tiles.Entities.Length;

        return true;
    }

    /// <summary>Destroys the entities whose bodies the scene has now taken away.</summary>
    void Bury() {
        for (var index = retiring.Count - 1; index >= 0; index--) {
            var entity = retiring[index];

            if (!scene.Entities.IsAlive(entity)) {
                retiring.RemoveAt(index);

                continue;
            }

            if (scene.Entities.Has<PhysicsBody>(entity)) {
                continue;
            }

            scene.Entities.Destroy(entity);
            retiring.RemoveAt(index);
        }
    }

    void Ensure(TerrainMap terrain, Vector3 origin) {
        var description = terrain.Description;

        if (!built.TryGetValue(terrain, out var tiles)) {
            tiles = new Tiles(new Entity[description.TileCount], new int[description.TileCount], origin) {
                // ⚠ Deliberately not the terrain's real count, so the first pass builds every tile
                // whether or not the terrain has holes. Starting at the true value would be correct
                // and would also be a stamp that matches before anything has been built.
                Holes = -1
            };

            built[terrain] = tiles;
            TileCount += description.TileCount;

            for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
                for (var tileX = 0; tileX < description.TilesX; tileX++) {
                    Build(terrain, tiles, tileX, tileZ);
                }
            }

            tiles.Holes = terrain.Holes.HoleCount;

            return;
        }

        var moved = tiles.Origin != origin;
        var holed = tiles.Holes != terrain.Holes.HoleCount;

        tiles.Origin = origin;
        tiles.Holes = terrain.Holes.HoleCount;

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                var index = (tileZ * description.TilesX) + tileX;

                if (!holed && tiles.Revisions[index] == terrain.RevisionOf(tileX, tileZ)) {
                    if (moved) {
                        Place(tiles.Entities[index], origin);
                    }

                    continue;
                }

                Build(terrain, tiles, tileX, tileZ);
                Rebuilds++;
            }
        }
    }

    /// <summary>Builds or replaces one tile's shape, and the entity carrying it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tile's own corner goes in the shape's offset, not in the entity's
    ///         transform.</b> Both would place it and only one survives more than one tile: a shape is
    ///         interned by its description, so four flat tiles at four transforms over one description
    ///         are <em>one</em> shape drawn four times in the same place. The offset is part of the
    ///         description, so the four are four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A rebuilt tile registers a new shape and the old one stays interned.</b>
    ///         <c>PhysicsShapes</c> has no release — the table is a level's shapes and a level ends
    ///         with the world — so a terrain recomposited every frame grows it without bound. That is
    ///         what <see cref="Rebuilds" /> is for.
    ///     </para>
    /// </remarks>
    void Build(TerrainMap terrain, Tiles tiles, int tileX, int tileZ) {
        var description = terrain.Description;
        var samples = description.TileSamples;
        var needed = samples * samples;

        if (heights.Length < needed) {
            heights = new float[needed];
        }

        // Metres, row-major, holes written as Jolt's own sentinel — so a quarry is a pit a character
        // falls into rather than a floor at its rim.
        terrain.Composite.FillCollisionSamples(
            tileX,
            tileZ,
            terrain.Holes,
            PhysicsShapes.NoCollisionHeight,
            heights.AsSpan(0, needed)
        );

        var corner = new Vector3(
            tileX * description.TileQuads * description.MetresPerQuad,
            0f,
            tileZ * description.TileQuads * description.MetresPerQuad
        );

        var shape = scene.Shapes.HeightField(
            heights.AsSpan(0, needed),
            samples,
            corner,
            new(description.MetresPerQuad, 1f, description.MetresPerQuad)
        );

        var index = (tileZ * description.TilesX) + tileX;
        var entity = tiles.Entities[index];

        if (entity == default || !scene.Entities.IsAlive(entity)) {
            var placement = LocalTransform.At(tiles.Origin);

            entity = scene.Entities.Create(placement, new WorldTransform { Value = placement.ToMatrix() });
            scene.Entities.Add(entity, Collider.Of(shape) with { Friction = Friction });
            tiles.Entities[index] = entity;
        } else {
            // Writing the shape is the whole of a rebuild: PhysicsScene compares Collider.Shape
            // against PhysicsBody.BuiltShape and recreates the body when they differ.
            scene.Entities.Get<Collider>(entity) = Collider.Of(shape) with { Friction = Friction };
            Place(entity, tiles.Origin);
        }

        tiles.Revisions[index] = terrain.RevisionOf(tileX, tileZ);
    }

    void Place(Entity entity, Vector3 origin) {
        if (!scene.Entities.IsAlive(entity)) {
            return;
        }

        var placement = LocalTransform.At(origin);

        scene.Entities.Get<LocalTransform>(entity) = placement;
        scene.Entities.Get<WorldTransform>(entity) = new() { Value = placement.ToMatrix() };
    }

    /// <summary>One terrain's bodies, and what they were built from.</summary>
    sealed class Tiles(Entity[] entities, int[] revisions, Vector3 origin) {
        public Entity[] Entities { get; } = entities;

        public int[] Revisions { get; } = revisions;

        public Vector3 Origin { get; set; } = origin;

        public int Holes { get; set; }
    }
}
