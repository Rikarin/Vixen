// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Vixen.Navigation.Diagnostics;
using Vixen.Testing;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavMeshDebugDrawTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavMesh Room() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 20, 20)
            .Box(new(8, 0, 8), new(12, 2, 12));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return mesh;
    }

    [Fact]
    public void EveryEdgeOfEveryPolygonIsDrawnExactlyOnce() {
        var mesh = Room();
        var draw = new DebugDraw();

        NavMeshDebugDraw.DrawMesh(draw, mesh);

        var edges = 0;

        foreach (var tile in mesh.Tiles) {
            for (var poly = 0; poly < tile.PolyCount; poly++) {
                edges += tile.Data.Polys[poly].VertexCount;
            }
        }

        // Shared edges are drawn from one side only, so the line count is the edge count less one
        // per interior adjacency — never more than the edge count, and never zero.
        Assert.True(draw.Count > 0, "A baked room drew nothing.");
        Assert.True(draw.Count < edges, $"{draw.Count} lines for {edges} edges — shared edges are being drawn twice.");
    }

    [Fact]
    public void TheDetailSurfaceDrawsOneTriangleAtATime() {
        var geometry = new NavTestGeometry()
            .Terrain(0, 0, 24, 24, 48, static (x, z) => 1.5f * MathF.Sin(x / 6f) * MathF.Sin(z / 6f));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        var draw = new DebugDraw();
        NavMeshDebugDraw.DrawDetail(draw, mesh, Color4.Green);

        var triangles = 0;

        foreach (var tile in mesh.Tiles) {
            foreach (var detail in tile.Data.Detail) {
                triangles += detail.TriangleCount;
            }
        }

        Assert.True(triangles > 0, "A hill baked no detail triangles to draw.");
        Assert.Equal(triangles * 3, draw.Count);
    }

    [Fact]
    public void ADisabledDrawIsNotWrittenTo() {
        var draw = new DebugDraw { Enabled = false };

        NavMeshDebugDraw.DrawMesh(draw, Room());

        Assert.Equal(0, draw.Count);
    }

    [Fact]
    public void TheLinesSitAboveTheSurfaceRatherThanInsideIt() {
        var mesh = Room();
        var draw = new DebugDraw();
        var style = NavMeshDrawStyle.Default with { Lift = 0.25f };

        NavMeshDebugDraw.DrawMesh(draw, mesh, style);

        var lowest = float.MaxValue;

        foreach (var line in draw.Lines) {
            lowest = MathF.Min(lowest, line.From.Y);
        }

        // The lift, and nothing else. This used to allow anything above the lift because the floor
        // itself baked a voxel above zero; now that a surface is reported where the geometry is, the
        // lowest line over a floor at y=0 is the lift exactly, and the test can say so.
        Assert.True(
            MathF.Abs(lowest - 0.25f) < 0.02f,
            $"The lowest line is at {lowest}, and the floor it is drawn over is at zero with a lift of 0.25."
        );
    }

    [Fact]
    public void APathIsDrawnAsItsSegmentsPlusATickPerCorner() {
        var mesh = Room();
        var query = new NavMeshQuery(mesh);
        var draw = new DebugDraw();

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(18, 0, 18), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[32];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        NavMeshDebugDraw.DrawPath(draw, corners[..cornerCount], Color4.Green);

        Assert.Equal(cornerCount - 1 + cornerCount, draw.Count);
    }

    [Fact]
    public void ACorridorDrawsTheOutlineOfEveryPolygonInIt() {
        var mesh = Room();
        var query = new NavMeshQuery(mesh);
        var draw = new DebugDraw();

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(18, 0, 18), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        NavMeshDebugDraw.DrawCorridor(draw, mesh, corridor[..count], Color4.Red);

        var expected = 0;
        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        foreach (var reference in corridor[..count]) {
            expected += mesh.GetPolyVertices(reference, vertices);
        }

        Assert.Equal(expected, draw.Count);
    }

    [Fact]
    public void ACrowdDrawsACircleAndTwoVelocitiesPerAgent() {
        var crowd = new Crowd(Room());
        var draw = new DebugDraw();

        var first = crowd.AddAgent(new(3, 0, 3), new() { Radius = 0.5f });
        var second = crowd.AddAgent(new(5, 0, 3), new() { Radius = 0.5f });

        Assert.False(first.IsNull);
        Assert.False(second.IsNull);

        crowd.SetTarget(first, new(17, 0, 17));
        crowd.Update(1f / 60f);

        NavMeshDebugDraw.DrawCrowd(draw, crowd);

        // Twelve segments of circle plus the achieved and desired velocities, twice over.
        Assert.Equal(2 * (12 + 2), draw.Count);
    }

    [Fact]
    public void WalkingACrowdsAgentsDoesNotAllocate() {
        var crowd = new Crowd(Room());

        for (var index = 0; index < 8; index++) {
            crowd.AddAgent(new(3f + index, 0, 3), new() { Radius = 0.5f });
        }

        // Warm the enumerator's first walk, then measure the next thousand.
        var seen = 0;

        Assert.Equal(0, Measured.Bytes(Walk, warmUp: 1));
        Assert.True(seen > 0);

        return;

        void Walk() {
            foreach (var handle in crowd.Agents) {
                seen += handle.Index;
            }
        }
    }
}
