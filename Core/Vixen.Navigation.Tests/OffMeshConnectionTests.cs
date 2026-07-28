// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class OffMeshConnectionTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    /// <summary>Two floors with a gap between them that nothing can walk across.</summary>
    static NavTestGeometry SplitLevel() =>
        new NavTestGeometry()
            .Floor(0, 0, 10, 10)
            .Floor(16, 0, 26, 10);

    static NavOffMeshConnectionData Bridge(bool bidirectional = true, byte area = NavArea.Walkable, NavPolyFlags flags = NavPolyFlags.Jump | NavPolyFlags.Walk) =>
        new() {
            Start = new(9, 0, 5),
            End = new(17, 0, 5),
            Radius = 2f,
            Bidirectional = bidirectional,
            Area = area,
            Flags = flags,
            UserId = 42
        };

    static NavMesh Mesh(params NavOffMeshConnectionData[] connections) {
        var geometry = SplitLevel();
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings, [], connections)!);

        return mesh;
    }

    [Fact]
    public void WithoutAConnectionTheTwoFloorsAreNotConnected() {
        var query = new NavMeshQuery(Mesh());

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out _);

        Assert.Equal(NavPathStatus.Partial, status);
    }

    [Fact]
    public void AConnectionMakesAPathAcrossTheGap() {
        var mesh = Mesh(Bridge());
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        var crossings = 0;

        foreach (var reference in corridor[..count]) {
            if (mesh.IsOffMeshConnection(reference)) {
                crossings++;

                Assert.True(mesh.TryGetOffMeshConnection(reference, out var connection));
                Assert.Equal(42u, connection.UserId);
            }
        }

        Assert.Equal(1, crossings);
    }

    [Fact]
    public void TheStraightPathTurnsAtBothEndsOfTheConnection() {
        var mesh = Mesh(Bridge());
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[32];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        // The funnel cannot pull straight across a connection, because its portal is a single point.
        var atStart = false;
        var atEnd = false;

        foreach (var corner in corners[..cornerCount]) {
            atStart |= NavGeometry.Distance2D(corner.Position, new(9, 0, 5)) < 0.5f;
            atEnd |= NavGeometry.Distance2D(corner.Position, new(17, 0, 5)) < 0.5f;
        }

        Assert.True(atStart, "The path does not turn where the connection starts.");
        Assert.True(atEnd, "The path does not turn where the connection ends.");
    }

    [Fact]
    public void AFilterThatRefusesTheConnectionRefusesTheCrossing() {
        var mesh = Mesh(Bridge(flags: NavPolyFlags.Jump));
        var query = new NavMeshQuery(mesh);

        // An agent that cannot jump: everything except Jump.
        var filter = new NavQueryFilter { IncludeFlags = NavPolyFlags.Walk | NavPolyFlags.Swim | NavPolyFlags.Door };

        query.FindNearestPoly(new(5, 0, 5), Extents, filter, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, filter, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, filter, corridor, out _);

        Assert.Equal(NavPathStatus.Partial, status);

        // And the same mesh with the same connection is crossable by something that can jump.
        var jumper = new NavQueryFilter { IncludeFlags = NavPolyFlags.All, ExcludeFlags = NavPolyFlags.Disabled };
        var complete = query.FindPath(start, end, startPoint, endPoint, jumper, corridor, out _);

        Assert.Equal(NavPathStatus.Complete, complete);
    }

    [Fact]
    public void AOneWayConnectionIsOnlyCrossedInItsOwnDirection() {
        var mesh = Mesh(Bridge(bidirectional: false));
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var west, out var westPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var east, out var eastPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];

        Assert.Equal(
            NavPathStatus.Complete,
            query.FindPath(west, east, westPoint, eastPoint, NavQueryFilter.Default, corridor, out _)
        );

        Assert.Equal(
            NavPathStatus.Partial,
            query.FindPath(east, west, eastPoint, westPoint, NavQueryFilter.Default, corridor, out _)
        );
    }

    [Fact]
    public void AnEndpointWithNoGroundUnderItAttachesNothing() {
        var stranded = Bridge() with { End = new(60, 0, 60) };
        var mesh = Mesh(stranded);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out _);

        Assert.Equal(NavPathStatus.Partial, status);
    }

    [Fact]
    public void AConnectionIsNeverWhereAnAgentIsStanding() {
        var mesh = Mesh(Bridge());
        var query = new NavMeshQuery(mesh);

        // Right beside the connection's start, which is the case where a nearest-polygon search that
        // did not skip connections would return one.
        Assert.True(query.FindNearestPoly(new(9, 0, 5), Extents, NavQueryFilter.Default, out var poly, out _));
        Assert.False(mesh.IsOffMeshConnection(poly));
    }

    [Fact]
    public void ARaycastDoesNotSeeAcrossAConnection() {
        var mesh = Mesh(Bridge());
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);

        Assert.True(query.Raycast(start, startPoint, new(21, 0, 5), NavQueryFilter.Default, out var hit));
        Assert.True(hit.Hit, "A line of sight ran across a gap that has to be jumped.");
        Assert.True(hit.Position.X < 11f, $"It stopped at {hit.Position}, on the far side.");
    }

    [Fact]
    public void AnAgentWalksAcrossAConnectionAndArrives() {
        var crowd = new Crowd(Mesh(Bridge()));
        var agent = crowd.AddAgent(new(3, 0, 5), new() { Radius = 0.5f, MaxSpeed = 3f });

        Assert.False(agent.IsNull);
        Assert.True(crowd.SetTarget(agent, new(23, 0, 5)));

        var crossed = false;
        var wasOnTheFarSide = false;

        for (var step = 0; step < 1200; step++) {
            crowd.Update(1f / 60f);
            crowd.TryGetState(agent, out var state);

            if (state.OffMesh is { } traversal) {
                crossed = true;

                Assert.Equal(42u, traversal.UserId);
                Assert.InRange(traversal.Progress, 0f, 1f);

                // While crossing it is between the two ends and on neither floor.
                Assert.True(state.Position.X is > 8.5f and < 17.5f, $"Mid-crossing it is at {state.Position}.");
            }

            wasOnTheFarSide |= state.Position.X > 17f;
        }

        Assert.True(crossed, "The agent never used the connection.");
        Assert.True(wasOnTheFarSide, "The agent never reached the far floor.");

        crowd.TryGetState(agent, out var final);
        Assert.Equal(CrowdTargetState.Arrived, final.State);
        Assert.Null(final.OffMesh);
    }

    [Fact]
    public void AnAgentCannotReachTheFarFloorWithoutTheConnection() {
        var crowd = new Crowd(Mesh());
        var agent = crowd.AddAgent(new(3, 0, 5), new() { Radius = 0.5f, MaxSpeed = 3f });

        crowd.SetTarget(agent, new(23, 0, 5));

        for (var step = 0; step < 600; step++) {
            crowd.Update(1f / 60f);
        }

        crowd.TryGetState(agent, out var state);

        Assert.NotEqual(CrowdTargetState.Arrived, state.State);
        Assert.True(state.Position.X < 11f, $"It got to {state.Position} across a gap it cannot cross.");
    }

    [Fact]
    public void ConnectionsSurviveTheRoundTripToBytes() {
        var geometry = SplitLevel();
        var asset = NavMeshAsset.FromTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings, [], [Bridge()])!);

        var loaded = Serializer.Read<NavMeshAsset>(Serializer.ToBytes(asset))!;
        var connection = Assert.Single(loaded.Tiles[0].OffMeshConnections);

        Assert.Equal(new Vector3(9, 0, 5), connection.Start);
        Assert.Equal(new Vector3(17, 0, 5), connection.End);
        Assert.True(connection.Bidirectional);
        Assert.Equal(42u, connection.UserId);

        // And it still connects the two floors after being read back.
        var query = new NavMeshQuery(loaded.ToNavMesh());

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];

        Assert.Equal(
            NavPathStatus.Complete,
            query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out _)
        );
    }

    [Fact]
    public void RemovingTheTileTakesItsConnectionsWithIt() {
        var mesh = Mesh(Bridge());
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Assert.False(start.IsNull);
        Assert.False(end.IsNull);

        mesh.RemoveTile(0, 0);

        Assert.False(mesh.IsValid(start));
        Assert.Equal(0, mesh.TileCount);
    }
}
