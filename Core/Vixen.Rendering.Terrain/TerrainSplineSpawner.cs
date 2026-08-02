// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;
using Vixen.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>What a spline-placed mesh entity is, so a regeneration can find the ones it made.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Without a tag, regenerating a road either duplicates every post along it or deletes
///         something an artist placed by hand.</b> A generated entity has to be distinguishable from
///         an authored one, and the only place that distinction can live is on the entity.
///     </para>
///     <para>
///         ⚠ <b>The spline's name rather than a handle</b>, for the reason every asset reference in a
///         scene is a name: a handle names a slot in a world that issued it, and a scene file is read
///         by a world that has not run yet.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct SplinePlacedComponent {
    /// <summary>Which spline placed it.</summary>
    public string Spline { get; set; }

    /// <summary>How far along that spline it stands, in metres.</summary>
    /// <remarks>
    ///     Kept so a regeneration can be compared rather than only replayed — and because it is the
    ///     number an artist reads when they want the third lamp post rather than the one at 43.7 m.
    /// </remarks>
    public float Distance { get; set; }
}

/// <summary>
///     Turning <see cref="TerrainSpline.PlaceAlong" />'s list into entities.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T8]'s owed item.</b> <c>PlaceAlong</c> returns a list of
///         <see cref="TerrainSplineMesh" /> and deliberately does not spawn anything: it lives in
///         <c>Vixen.Terrain</c>, which is device-free and world-free by [§ D1], and a kernel that
///         created entities would be a kernel that needs an ECS to test.
///     </para>
///     <para>
///         ⚠ <b>A regeneration removes what it made and nothing else.</b> Every entity carries the
///         spline that placed it, so laying a road down again deletes that road's posts and leaves
///         both the hand-placed ones and every other road's alone. Without the tag the choice is
///         between duplicating on every regeneration and deleting an artist's work.
///     </para>
///     <para>
///         ⚠ <b>Removed and re-created rather than moved, which is the opposite of what the terrain
///         deformation does.</b> A deformation is a rectangle of numbers and can be recomputed in
///         place; a placement's <em>count</em> changes with the spacing and the length, so matching
///         old entities to new ones would be an alignment problem to save an allocation. The undo
///         record is the same size either way.
///     </para>
/// </remarks>
public static class TerrainSplineSpawner {
    /// <summary>Spawns an entity per placed mesh, replacing whatever that spline placed before.</summary>
    /// <param name="world">The world.</param>
    /// <param name="spline">What the spline is called, which is what tags the entities.</param>
    /// <param name="meshes">What <see cref="TerrainSpline.PlaceAlong" /> produced.</param>
    /// <param name="resolve">
    ///     What turns a mesh's name into a reference, or <see langword="null" /> to spawn entities
    ///     that name nothing.
    /// </param>
    /// <returns>How many entities were created.</returns>
    /// <exception cref="ArgumentNullException">There is no world or no list.</exception>
    /// <exception cref="ArgumentException">The spline has no name to tag with.</exception>
    /// <remarks>
    ///     ⚠ <b>The resolver is a parameter for <see cref="ITerrainTextures" />'s reason.</b> A
    ///     <c>.vxspline</c> names its mesh as a string, because a reference in content is a name;
    ///     turning one into an <see cref="AssetReference" /> is the asset database's job, and a
    ///     spawner that did it would need one in a class whose job is three <c>world.Add</c> calls.
    ///     Null spawns the entities anyway — a post in the right place with no mesh is a placement
    ///     that can be inspected, and a missing one is nothing at all.
    /// </remarks>
    public static int Spawn(
        World world,
        string spline,
        IReadOnlyList<TerrainSplineMesh> meshes,
        Func<string, AssetReference>? resolve = null
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentException.ThrowIfNullOrWhiteSpace(spline);

        Clear(world, spline);

        foreach (var mesh in meshes) {
            var entity = world.Create();

            world.Add(
                entity,
                LocalTransform.Identity with { Position = mesh.Position, Rotation = mesh.Rotation }
            );

            world.Add(
                entity,
                new MeshRenderable {
                    Mesh = resolve?.Invoke(mesh.Mesh) ?? AssetReference.Null,
                    CastsShadows = true
                }
            );
            world.Add(entity, new SplinePlacedComponent { Spline = spline, Distance = mesh.Distance });
        }

        return meshes.Count;
    }

    /// <summary>Removes every entity a spline placed.</summary>
    /// <param name="world">The world.</param>
    /// <param name="spline">Which spline's, or empty for every spline's.</param>
    /// <returns>How many were removed.</returns>
    /// <exception cref="ArgumentNullException">There is no world.</exception>
    /// <remarks>
    ///     ⚠ <b>Gathered before anything is destroyed.</b> Destroying an entity while iterating the
    ///     archetype it is in moves the ones after it, which is the same trap
    ///     <c>FoliageVolume.Remove</c> documents — a loop that removes as it goes visits half of what
    ///     it meant to.
    /// </remarks>
    public static int Clear(World world, string spline = "") {
        ArgumentNullException.ThrowIfNull(world);

        var query = new QueryDescription().WithAll<SplinePlacedComponent>();
        var doomed = new List<Entity>();

        // ⚠ One entity at a time, because a component holding a `string` is *managed*: its values
        // live in the world's store and the chunk holds handles, so there is no contiguous span to
        // walk. `TrackedDollyBody` learned the same thing, and the error a span asks for is at least
        // an exception rather than the wrong bytes.
        foreach (var chunk in world.Query(query).Chunks()) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var placed = world.Read<SplinePlacedComponent>(entities[index]);
                var matches = spline.Length == 0
                    || string.Equals(placed.Spline, spline, StringComparison.Ordinal);

                if (matches) {
                    doomed.Add(entities[index]);
                }
            }
        }

        foreach (var entity in doomed) {
            world.Destroy(entity);
        }

        return doomed.Count;
    }

    /// <summary>Every entity a spline placed, in no particular order.</summary>
    /// <param name="world">The world.</param>
    /// <param name="spline">Which spline's, or empty for every spline's.</param>
    /// <returns>The entities.</returns>
    /// <exception cref="ArgumentNullException">There is no world.</exception>
    public static IReadOnlyList<Entity> Placed(World world, string spline = "") {
        ArgumentNullException.ThrowIfNull(world);

        var query = new QueryDescription().WithAll<SplinePlacedComponent>();
        var found = new List<Entity>();

        foreach (var chunk in world.Query(query).Chunks()) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var placed = world.Read<SplinePlacedComponent>(entities[index]);

                if (spline.Length == 0 || string.Equals(placed.Spline, spline, StringComparison.Ordinal)) {
                    found.Add(entities[index]);
                }
            }
        }

        return found;
    }
}
