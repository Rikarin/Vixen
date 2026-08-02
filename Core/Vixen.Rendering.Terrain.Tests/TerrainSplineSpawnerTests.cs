// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>Mesh placement reaching the scene — [docs/plan/31 § T8]'s owed item.</summary>
public sealed class TerrainSplineSpawnerTests {
    static TerrainSplineMesh[] Posts(int count, string mesh = "Meshes/post") =>
        [.. Enumerable.Range(0, count)
            .Select(index => new TerrainSplineMesh(mesh, new(index * 10f, 0f, 0f), Quaternion.Identity, index * 10f))];

    [Fact]
    public void EveryPlacedMeshBecomesAnEntity() {
        using var world = new World();

        Assert.Equal(4, TerrainSplineSpawner.Spawn(world, "Main Road", Posts(4)));
        Assert.Equal(4, TerrainSplineSpawner.Placed(world, "Main Road").Count);
    }

    /// <summary>The transform is where the placement said, and the distance travels with it.</summary>
    [Fact]
    public void AnEntityStandsWhereThePlacementPutIt() {
        using var world = new World();

        TerrainSplineSpawner.Spawn(world, "Main Road", Posts(3));

        var placed = TerrainSplineSpawner.Placed(world, "Main Road");
        var distances = new List<float>();

        foreach (var entity in placed) {
            var transform = world.Read<LocalTransform>(entity);
            var tag = world.Read<SplinePlacedComponent>(entity);

            Assert.Equal(transform.Position.X, tag.Distance, 4);
            Assert.Equal("Main Road", tag.Spline);

            distances.Add(tag.Distance);
        }

        Assert.Equal([0f, 10f, 20f], distances.Order());
    }

    /// <summary>Regenerating replaces what that spline placed and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>Without the tag the choice is between duplicating on every regeneration and deleting
    ///     an artist's work.</b> A generated entity has to be distinguishable from an authored one,
    ///     and the only place that distinction can live is on the entity.
    /// </remarks>
    [Fact]
    public void RegeneratingReplacesOnlyThatSplinesEntities() {
        using var world = new World();

        TerrainSplineSpawner.Spawn(world, "Main Road", Posts(4));
        TerrainSplineSpawner.Spawn(world, "Side Road", Posts(2));

        // Something an artist placed by hand: a transform and a mesh, and no tag.
        var authored = world.Create();

        world.Add(authored, LocalTransform.Identity);

        Assert.Equal(3, TerrainSplineSpawner.Spawn(world, "Main Road", Posts(3)));

        Assert.Equal(3, TerrainSplineSpawner.Placed(world, "Main Road").Count);
        Assert.Equal(2, TerrainSplineSpawner.Placed(world, "Side Road").Count);
        Assert.True(world.IsAlive(authored), "regenerating a road deleted something it did not place.");
    }

    /// <summary>Clearing a spline takes its entities and leaves the others.</summary>
    [Fact]
    public void ClearingOneSplineLeavesTheOthers() {
        using var world = new World();

        TerrainSplineSpawner.Spawn(world, "Main Road", Posts(4));
        TerrainSplineSpawner.Spawn(world, "Side Road", Posts(2));

        Assert.Equal(4, TerrainSplineSpawner.Clear(world, "Main Road"));
        Assert.Empty(TerrainSplineSpawner.Placed(world, "Main Road"));
        Assert.Equal(2, TerrainSplineSpawner.Placed(world, "Side Road").Count);

        // And clearing everything takes what is left.
        Assert.Equal(2, TerrainSplineSpawner.Clear(world));
        Assert.Empty(TerrainSplineSpawner.Placed(world));
    }

    /// <summary>A spawn with no resolver still places the entities.</summary>
    /// <remarks>
    ///     ⚠ <b>A post in the right place with no mesh is a placement that can be inspected, and a
    ///     missing one is nothing at all.</b> Turning a name into a reference is the asset database's
    ///     job, and a spawner that did it would need one in a class whose job is three
    ///     <c>world.Add</c> calls.
    /// </remarks>
    [Fact]
    public void NoResolverStillPlacesThem() {
        using var world = new World();

        TerrainSplineSpawner.Spawn(world, "Main Road", Posts(2));

        foreach (var entity in TerrainSplineSpawner.Placed(world)) {
            Assert.True(world.Read<Vixen.Rendering.Ecs.MeshRenderable>(entity).Mesh.IsNull);
        }
    }

    /// <summary>And one with a resolver names what it resolved.</summary>
    [Fact]
    public void AResolverNamesTheMesh() {
        using var world = new World();
        var asked = new List<string>();
        var reference = new AssetReference(AssetId.New(), default);

        TerrainSplineSpawner.Spawn(
            world,
            "Main Road",
            Posts(2, "Meshes/lamp"),
            name => {
                asked.Add(name);

                return reference;
            }
        );

        Assert.Equal(["Meshes/lamp", "Meshes/lamp"], asked);

        foreach (var entity in TerrainSplineSpawner.Placed(world)) {
            Assert.Equal(reference, world.Read<Vixen.Rendering.Ecs.MeshRenderable>(entity).Mesh);
        }
    }

    [Fact]
    public void ASplineWithNoNameIsRefused() {
        using var world = new World();

        Assert.Throws<ArgumentException>(() => TerrainSplineSpawner.Spawn(world, "  ", Posts(1)));
    }
}
