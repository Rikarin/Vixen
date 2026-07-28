// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class CrowdTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly CrowdAgentParams Walker = new() { Radius = 0.5f, MaxSpeed = 3f, MaxAcceleration = 12f };

    static NavMesh OpenFloor(float size = 20f) {
        var geometry = new NavTestGeometry().Floor(0, 0, size, size);
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return mesh;
    }

    static NavMesh DividedRoom() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 20, 20)
            .Box(new(0, 0, 9.5f), new(8, 2, 10.5f))
            .Box(new(12, 0, 9.5f), new(20, 2, 10.5f));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return mesh;
    }

    [Fact]
    public void AnAgentWalksToWhereItWasTold() {
        var crowd = new Crowd(OpenFloor());
        var agent = crowd.AddAgent(new(3, 0, 3), Walker);

        Assert.False(agent.IsNull);
        Assert.True(crowd.SetTarget(agent, new(16, 0, 16)));

        for (var step = 0; step < 600; step++) {
            crowd.Update(1f / 60f);
        }

        Assert.True(crowd.TryGetState(agent, out var state));
        Assert.Equal(CrowdTargetState.Arrived, state.State);
        Assert.True(NavGeometry.Distance2D(state.Position, new(16, 0, 16)) < 1f, $"It stopped at {state.Position}.");
    }

    [Fact]
    public void AnAgentIsOnTheMeshAtEveryStepOfTheWay() {
        var mesh = OpenFloor();
        var crowd = new Crowd(mesh);
        var query = new NavMeshQuery(mesh);
        var agent = crowd.AddAgent(new(3, 0, 3), Walker);

        crowd.SetTarget(agent, new(16, 0, 16));

        for (var step = 0; step < 400; step++) {
            crowd.Update(1f / 60f);

            Assert.True(crowd.TryGetState(agent, out var state));
            Assert.True(
                query.FindNearestPoly(state.Position, new(0.05f, 0.5f, 0.05f), NavQueryFilter.Default, out _, out _),
                $"After {step} steps the agent is at {state.Position}, which is off the mesh."
            );
        }
    }

    [Fact]
    public void AnAgentWalksAroundAWallToGetToTheOtherSide() {
        var crowd = new Crowd(DividedRoom());
        var agent = crowd.AddAgent(new(3, 0, 3), Walker);

        crowd.SetTarget(agent, new(3, 0, 17));

        var crossedTheGap = false;

        for (var step = 0; step < 900; step++) {
            crowd.Update(1f / 60f);
            crowd.TryGetState(agent, out var state);

            // The only way through is the gap between x = 8 and x = 12.
            if (state.Position.Z is > 9.4f and < 10.6f) {
                Assert.True(state.Position.X is > 7f and < 13f, $"The agent is at {state.Position}, which is inside the wall.");
                crossedTheGap = true;
            }
        }

        Assert.True(crossedTheGap, "The agent never went through the gap.");
        Assert.True(crowd.TryGetState(agent, out var final));
        Assert.Equal(CrowdTargetState.Arrived, final.State);
    }

    [Fact]
    public void TwoAgentsWalkingThroughEachOtherDoNotWalkThroughEachOther() {
        var crowd = new Crowd(OpenFloor());
        var first = crowd.AddAgent(new(5, 0, 10), Walker);
        var second = crowd.AddAgent(new(15, 0, 10), Walker);

        crowd.SetTarget(first, new(15, 0, 10));
        crowd.SetTarget(second, new(5, 0, 10));

        var closest = float.MaxValue;

        for (var step = 0; step < 900; step++) {
            crowd.Update(1f / 60f);

            crowd.TryGetState(first, out var a);
            crowd.TryGetState(second, out var b);

            closest = MathF.Min(closest, NavGeometry.Distance2D(a.Position, b.Position));
        }

        // Avoidance is a soft constraint and the separation pass is what recovers from the cases it
        // could not solve, so the bar is that they never seriously interpenetrate — not that they
        // never touch.
        Assert.True(closest > 0.7f, $"The two agents got within {closest} of each other, and they are half a metre wide each.");

        crowd.TryGetState(first, out var firstFinal);
        crowd.TryGetState(second, out var secondFinal);

        Assert.Equal(CrowdTargetState.Arrived, firstFinal.State);
        Assert.Equal(CrowdTargetState.Arrived, secondFinal.State);
    }

    [Fact]
    public void AgentsWithAvoidanceOffStillGetWhereTheyAreGoing() {
        var crowd = new Crowd(OpenFloor());
        var parameters = Walker with { AvoidanceEnabled = false };
        var agent = crowd.AddAgent(new(3, 0, 10), parameters);

        crowd.SetTarget(agent, new(16, 0, 10));

        for (var step = 0; step < 600; step++) {
            crowd.Update(1f / 60f);
        }

        crowd.TryGetState(agent, out var state);
        Assert.Equal(CrowdTargetState.Arrived, state.State);
    }

    [Fact]
    public void ACrowdOfAgentsHeadingForTheSamePlaceAllGetThere() {
        var crowd = new Crowd(OpenFloor());
        var handles = new List<CrowdAgentHandle>();

        for (var index = 0; index < 8; index++) {
            var handle = crowd.AddAgent(new(3 + (index % 4 * 1.5f), 0, 3 + (index / 4 * 1.5f)), Walker);
            Assert.False(handle.IsNull);
            handles.Add(handle);
        }

        foreach (var handle in handles) {
            crowd.SetTarget(handle, new(16, 0, 16));
        }

        for (var step = 0; step < 1800; step++) {
            crowd.Update(1f / 60f);
        }

        foreach (var handle in handles) {
            crowd.TryGetState(handle, out var state);

            Assert.True(
                NavGeometry.Distance2D(state.Position, new(16, 0, 16)) < 3f,
                $"An agent finished at {state.Position}, which is not near the destination everybody was given."
            );
        }
    }

    [Fact]
    public void AnAgentAskedForSomewhereUnreachableSaysSo() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 10, 10)
            .Floor(20, 0, 30, 10);

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        var crowd = new Crowd(mesh);
        var agent = crowd.AddAgent(new(5, 0, 5), Walker);

        crowd.SetTarget(agent, new(25, 0, 5));

        for (var step = 0; step < 600; step++) {
            crowd.Update(1f / 60f);
        }

        crowd.TryGetState(agent, out var state);

        // It walks as far as it can and stops there, on its own side of the gap.
        Assert.NotEqual(CrowdTargetState.Arrived, state.State);
        Assert.True(state.Position.X < 11f, $"The agent reached {state.Position}, on the far side of a gap it cannot cross.");
    }

    [Fact]
    public void AHandleStopsWorkingWhenItsAgentIsRemoved() {
        var crowd = new Crowd(OpenFloor());
        var agent = crowd.AddAgent(new(5, 0, 5), Walker);

        Assert.True(crowd.RemoveAgent(agent));
        Assert.False(crowd.RemoveAgent(agent));
        Assert.False(crowd.TryGetState(agent, out _));
        Assert.False(crowd.SetTarget(agent, new(6, 0, 6)));

        // The slot is reused, and the generation is what stops the old handle from naming its
        // occupant.
        var replacement = crowd.AddAgent(new(6, 0, 6), Walker);

        Assert.Equal(agent.Index, replacement.Index);
        Assert.False(crowd.TryGetState(agent, out _));
        Assert.True(crowd.TryGetState(replacement, out _));
    }

    [Fact]
    public void AnAgentOutsideTheMeshIsNotAdded() {
        var crowd = new Crowd(OpenFloor());

        Assert.True(crowd.AddAgent(new(100, 0, 100), Walker).IsNull);
        Assert.Equal(0, crowd.AgentCount);
    }
}
