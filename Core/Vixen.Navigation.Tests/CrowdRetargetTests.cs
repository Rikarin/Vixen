// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     What an agent already knows, and what it therefore does not have to look up.
/// </summary>
/// <remarks>
///     Planning used to begin with two nearest-polygon searches per agent — one for where the agent
///     was standing, one for where it was going — and on an eighty-metre level that was the whole cost
///     of a crowd retargeting at once. Both are avoidable, and the tests here are about the two ways
///     avoiding them could go wrong: the remembered polygon being stale, and the remembered polygon
///     being one the filter no longer accepts.
/// </remarks>
public sealed class CrowdRetargetTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly CrowdAgentParams Walker = new() { Radius = 0.5f, MaxSpeed = 3f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavTestGeometry Level() => new NavTestGeometry().Floor(0, 0, 30, 30);

    static NavMesh Room() {
        var geometry = Level();
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return mesh;
    }

    static void Walk(Crowd crowd, int frames = 900) {
        for (var frame = 0; frame < frames; frame++) {
            crowd.Update(1f / 60f);
        }
    }

    [Fact]
    public void ADestinationGivenAsAPolygonIsWalkedToTheSameWay() {
        var mesh = Room();
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(26, 0, 26), Extents, NavQueryFilter.Default, out var poly, out var point));

        var byPoint = new Crowd(mesh);
        var byPoly = new Crowd(mesh);

        var first = byPoint.AddAgent(new(4, 0, 4), Walker);
        var second = byPoly.AddAgent(new(4, 0, 4), Walker);

        Assert.True(byPoint.SetTarget(first, new(26, 0, 26)));
        Assert.True(byPoly.SetTarget(second, poly, point));

        Walk(byPoint);
        Walk(byPoly);

        byPoint.TryGetState(first, out var left);
        byPoly.TryGetState(second, out var right);

        Assert.Equal(CrowdTargetState.Arrived, left.State);
        Assert.Equal(CrowdTargetState.Arrived, right.State);

        // The same destination named two ways is the same destination. The overload exists to skip a
        // search, not to mean something slightly different.
        Assert.True(
            NavGeometry.Distance2D(left.Position, right.Position) < 0.1f,
            $"One agent finished at {left.Position} and the other at {right.Position}."
        );
    }

    [Fact]
    public void AStaleDestinationPolygonIsSearchedForAgainRatherThanUsed() {
        var geometry = Level();
        var cache = NavTileCache.Build(geometry.Vertices, geometry.Indices, NavMeshBaker.Volume(geometry.Vertices, Settings), Settings, tileSize: 32);
        var mesh = cache.CreateNavMesh();

        var crowd = new Crowd(mesh);
        var handle = crowd.AddAgent(new(4, 0, 4), Walker);

        Assert.True(crowd.SetTarget(handle, new(26, 0, 26)));
        crowd.Update(1f / 60f);

        // Rebuilding a tile replaces its polygons, and every reference to them changes salt — which is
        // exactly what the remembered destination is holding. Nothing in the crowd is told about it.
        cache.AddObstacle(NavAreaVolume.Cylinder(new(15, 0, 15), 1f, 2f, NavArea.Null));

        while (cache.PendingTiles > 0) {
            cache.Update(mesh, 4);
        }

        Assert.True(crowd.SetTarget(handle, new(26, 0, 26)));
        Walk(crowd);

        crowd.TryGetState(handle, out var state);

        Assert.True(
            state.State == CrowdTargetState.Arrived,
            $"The agent is {state.State} at {state.Position}. A destination whose tile was rebuilt has to be found again, not searched for from a reference that no longer resolves."
        );
    }

    [Fact]
    public void AnAgentStandingOnAPolygonTheFilterRefusesReplansFromSomewhereElse() {
        var mesh = Room();
        var crowd = new Crowd(mesh);
        var handle = crowd.AddAgent(new(4, 0, 4), Walker);

        Assert.True(crowd.SetTarget(handle, new(26, 0, 26)));
        crowd.Update(1f / 60f);

        crowd.TryGetState(handle, out var standing);

        // Closing the polygon under the agent's feet. Planning straight from `agent.Poly` without
        // asking the filter would search from a polygon the agent is not allowed to be on, and the
        // corridor's first step would be one no path is allowed to take.
        Assert.True(mesh.SetPolyFlags(standing.Poly, NavPolyFlags.None));

        Assert.True(crowd.SetTarget(handle, new(26, 0, 26)));
        Walk(crowd, 120);

        crowd.TryGetState(handle, out var after);

        // Either it found a way from the nearest polygon it is allowed to use, or there is none. What
        // it must not do is sit in Requested for ever, which is what happens when the plan is
        // submitted from a polygon the search then refuses to expand.
        Assert.True(
            after.State is CrowdTargetState.Following or CrowdTargetState.Arrived or CrowdTargetState.Failed,
            $"The agent is still {after.State} two seconds after being told to move off a polygon it may not stand on."
        );
    }

    [Fact]
    public void ARecycledSlotDoesNotInheritTheLastAgentsDestination() {
        var mesh = Room();
        var crowd = new Crowd(mesh);

        var first = crowd.AddAgent(new(4, 0, 4), Walker);
        Assert.True(crowd.SetTarget(first, new(26, 0, 26)));
        Assert.True(crowd.RemoveAgent(first));

        // The same slot, and the remembered destination in it is the dead agent's. A new agent has
        // been told to go nowhere, so it must stand still rather than walk somewhere it never heard of.
        var second = crowd.AddAgent(new(4, 0, 4), Walker);
        Assert.Equal(first.Index, second.Index);

        Walk(crowd, 300);

        crowd.TryGetState(second, out var state);

        Assert.Equal(CrowdTargetState.None, state.State);
        Assert.True(
            NavGeometry.Distance2D(state.Position, new(4, 0, 4)) < 0.5f,
            $"An agent with no destination walked to {state.Position}."
        );
    }

    [Fact]
    public void ATargetOffTheMeshStillFails() {
        var mesh = Room();
        var crowd = new Crowd(mesh);
        var handle = crowd.AddAgent(new(4, 0, 4), Walker);

        // Resolving the destination early must not turn "there is no mesh there" into silence. It is
        // reported the same way it always was, when the plan is attempted.
        Assert.True(crowd.SetTarget(handle, new(500, 0, 500)));
        crowd.Update(1f / 60f);

        crowd.TryGetState(handle, out var state);

        Assert.Equal(CrowdTargetState.Failed, state.State);
    }
}
