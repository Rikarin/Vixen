// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Vixen.Navigation.Ecs;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavigationSystemTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };

    static Crowd Crowd() {
        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return new(mesh);
    }

    /// <summary>What the system runner does between phases, which a hand-stepped test has to do too.</summary>
    static void Step(NavigationSystem system, World world, int steps) {
        for (var index = 0; index < steps; index++) {
            world.AdvanceVersion();
            system.Step(world, 1f / 60f);
        }
    }

    [Fact]
    public void AnEntityWithAnAgentComponentJoinsTheCrowd() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());

        var entity = world.Create(LocalTransform.At(new(5, 0, 5)), NavigationAgent.Default());

        Step(system, world, 1);

        Assert.Equal(1, system.Crowd.AgentCount);
        Assert.False(world.Read<NavigationAgent>(entity).Handle.IsNull);
    }

    [Fact]
    public void AnAgentWithADestinationWalksToIt() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());

        var entity = world.Create(
            LocalTransform.At(new(3, 0, 3)),
            NavigationAgent.Default(),
            new NavigationDestination { Value = new(15, 0, 15) },
            new NavigationState()
        );

        Step(system, world, 600);

        var transform = world.Read<LocalTransform>(entity);
        var state = world.Read<NavigationState>(entity);

        Assert.Equal(CrowdTargetState.Arrived, state.Target);
        Assert.True(NavGeometry.Distance2D(transform.Position, new(15, 0, 15)) < 1f, $"It stopped at {transform.Position}.");
        Assert.Equal(state.Position, transform.Position);
    }

    [Fact]
    public void MovingTheDestinationSendsTheAgentSomewhereElse() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());

        var entity = world.Create(
            LocalTransform.At(new(3, 0, 3)),
            NavigationAgent.Default(),
            new NavigationDestination { Value = new(15, 0, 3) },
            new NavigationState()
        );

        Step(system, world, 60);

        world.Set(entity, new NavigationDestination { Value = new(3, 0, 15), Version = 1 });

        Step(system, world, 600);

        var transform = world.Read<LocalTransform>(entity);

        Assert.True(
            NavGeometry.Distance2D(transform.Position, new(3, 0, 15)) < 1f,
            $"It went to {transform.Position} rather than to where it was redirected."
        );
    }

    [Fact]
    public void DestroyingAnEntityTakesItsAgentOutOfTheCrowd() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());

        var entity = world.Create(LocalTransform.At(new(5, 0, 5)), NavigationAgent.Default());

        Step(system, world, 1);
        Assert.Equal(1, system.Crowd.AgentCount);

        world.Destroy(entity);

        Step(system, world, 1);
        Assert.Equal(0, system.Crowd.AgentCount);
    }

    [Fact]
    public void RemovingTheComponentTakesItsAgentOutOfTheCrowdToo() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());

        var entity = world.Create(LocalTransform.At(new(5, 0, 5)), NavigationAgent.Default());

        Step(system, world, 1);
        Assert.Equal(1, system.Crowd.AgentCount);

        world.Remove<NavigationAgent>(entity);

        Step(system, world, 1);
        Assert.Equal(0, system.Crowd.AgentCount);
    }

    [Fact]
    public void AnEntitySpawnedOffTheMeshDoesNotJoinAndDoesNotThrow() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());

        var entity = world.Create(LocalTransform.At(new(500, 0, 500)), NavigationAgent.Default());

        Step(system, world, 10);

        Assert.Equal(0, system.Crowd.AgentCount);
        Assert.True(world.Read<NavigationAgent>(entity).Handle.IsNull);
    }

    [Fact]
    public void SeveralAgentsAreDrivenTogether() {
        using var world = new World();
        var system = new NavigationSystem(Crowd());
        var entities = new List<Entity>();

        for (var index = 0; index < 5; index++) {
            entities.Add(
                world.Create(
                    LocalTransform.At(new(3 + (index * 1.4f), 0, 3)),
                    NavigationAgent.Default(),
                    new NavigationDestination { Value = new(15, 0, 15) },
                    new NavigationState()
                )
            );
        }

        Step(system, world, 900);

        Assert.Equal(5, system.Crowd.AgentCount);

        foreach (var entity in entities) {
            var position = world.Read<LocalTransform>(entity).Position;

            Assert.True(
                NavGeometry.Distance2D(position, new(15, 0, 15)) < 3f,
                $"An agent finished at {position}, which is not near where they were all sent."
            );
        }
    }
}
