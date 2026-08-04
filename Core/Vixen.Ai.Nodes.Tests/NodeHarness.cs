// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Vixen.Navigation.Ecs;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>A world with a floor baked into a navmesh, a crowd on it, and agents that think.</summary>
/// <remarks>
///     The floor is forty metres square and flat, so a test that fails has failed about the node
///     rather than about the bake — <c>Vixen.Navigation.Tests</c> is where the mesh itself is checked.
/// </remarks>
sealed class Level {
    int frame;

    public Level(float extent = 40f) {
        var geometry = new NavTestFloor(extent);

        Mesh = new NavMesh(NavMeshParams.Single);
        Mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, new() { AgentRadius = 0.6f })!);

        Query = new NavMeshQuery(Mesh);
        World = new World("nodes-test");
        Navigation = new NavigationSystem(new Crowd(Mesh));
    }

    public World World { get; }

    public NavMesh Mesh { get; }

    public NavMeshQuery Query { get; }

    public NavigationSystem Navigation { get; }

    public AiSystem? Agents { get; set; }

    /// <summary>An entity that walks the mesh and can be told where to go.</summary>
    public Entity Walker(Vector3 at) {
        var entity = World.Create(LocalTransform.At(at), NavigationAgent.Default(), new NavigationDestination());

        World.Add(entity, new NavigationState { Position = at });

        return entity;
    }

    public ref LocalTransform Transform(Entity entity) => ref World.Get<LocalTransform>(entity);

    public Vector3 Where(Entity entity) => World.Get<LocalTransform>(entity).Position;

    /// <summary>One frame: think, then walk. ⚠ In that order — a destination written this frame is walked this frame.</summary>
    public void Step(int frames = 1, Action<int>? before = null) {
        for (var index = 0; index < frames; index++) {
            before?.Invoke(frame);
            World.AdvanceVersion();
            Agents?.Step(World, Frame(frame));
            Navigation.Step(World, 1f / 60f);
            frame++;
        }
    }

    public static GameTime Frame(int index) => new(
        TimeSpan.FromSeconds((index + 1) / 60.0),
        TimeSpan.FromSeconds(1 / 60.0),
        TimeSpan.FromSeconds(1 / 60.0),
        index,
        1f
    );
}

/// <summary>A flat floor, wound so that its upward face is the front one.</summary>
sealed class NavTestFloor {
    readonly Vector3[] vertices;
    readonly int[] indices;

    public NavTestFloor(float extent) {
        vertices = [
            new(0f, 0f, 0f),
            new(0f, 0f, extent),
            new(extent, 0f, extent),
            new(extent, 0f, 0f)
        ];

        indices = [0, 1, 2, 0, 2, 3];
    }

    public ReadOnlySpan<Vector3> Vertices => vertices;

    public ReadOnlySpan<int> Indices => indices;
}
