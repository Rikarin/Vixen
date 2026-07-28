// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavPathQueueTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavMesh Room(float size = 40f) {
        var geometry = new NavTestGeometry().Floor(0, 0, size, size);

        for (var z = 8f; z < size - 8f; z += 8f) {
            for (var x = 8f; x < size - 8f; x += 8f) {
                geometry.Box(new(x - 0.75f, 0, z - 0.75f), new(x + 0.75f, 3, z + 0.75f));
            }
        }

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return mesh;
    }

    [Fact]
    public void ASlicedSearchFindsWhatTheWholeOneDoes() {
        var mesh = Room();
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> whole = stackalloc NavPolyRef[512];
        var wholeStatus = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, whole, out var wholeCount);

        // The same search, four expansions at a time.
        Assert.NotEqual(NavPathStatus.Failed, query.InitSlicedFindPath(start, end, startPoint, endPoint, NavQueryFilter.Default));

        var slices = 0;

        while (query.UpdateSlicedFindPath(4, out _) == NavPathStatus.Partial) {
            slices++;

            Assert.True(slices < 10_000, "The sliced search never finished.");
        }

        Span<NavPolyRef> sliced = stackalloc NavPolyRef[512];
        var slicedStatus = query.FinalizeSlicedFindPath(sliced, out var slicedCount);

        Assert.True(slices > 1, "The search finished in one slice, so slicing was not exercised.");
        Assert.Equal(wholeStatus, slicedStatus);
        Assert.Equal(wholeCount, slicedCount);

        for (var index = 0; index < wholeCount; index++) {
            Assert.Equal(whole[index], sliced[index]);
        }
    }

    [Fact]
    public void ARequestIsAnsweredAfterEnoughUpdates() {
        var mesh = Room();
        var queue = new NavPathQueue(mesh);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        var request = queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default);

        Assert.False(request.IsNull);
        Assert.Equal(NavPathRequestState.Queued, queue.GetState(request));

        var path = new NavPolyRef[512];
        var updates = 0;

        while (!queue.TryTakeResult(request, path, out var count, out var status)) {
            queue.Update(8);
            updates++;

            Assert.True(updates < 1_000, "The request was never answered.");

            if (queue.GetState(request) == NavPathRequestState.Ready) {
                Assert.True(queue.TryTakeResult(request, path, out count, out status));
                Assert.Equal(NavPathStatus.Complete, status);
                Assert.True(count > 1);

                break;
            }
        }

        Assert.True(updates > 1, "It finished in a single update, so the budget did nothing.");
        Assert.Equal(NavPathRequestState.Unknown, queue.GetState(request));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void TheBudgetIsWhatBoundsAnUpdate() {
        var mesh = Room();
        var queue = new NavPathQueue(mesh);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        // Far more requests than a frame could ever afford to answer.
        for (var index = 0; index < 64; index++) {
            Assert.False(queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default).IsNull);
        }

        queue.Update(32);

        // This is the whole point: sixty-four searches, and the update costs thirty-two expansions.
        Assert.True(queue.LastIterations <= 32, $"The update did {queue.LastIterations} expansions against a budget of 32.");
        Assert.True(queue.PendingCount > 32, "Most of the requests should still be waiting.");
    }

    [Fact]
    public void AFullQueueRefusesRatherThanGrows() {
        var mesh = Room();
        var queue = new NavPathQueue(mesh, capacity: 4);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        for (var index = 0; index < 4; index++) {
            Assert.False(queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default).IsNull);
        }

        Assert.True(queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default).IsNull);
    }

    [Fact]
    public void ACancelledRequestGivesItsSlotBack() {
        var mesh = Room();
        var queue = new NavPathQueue(mesh, capacity: 1);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        var request = queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default);
        Assert.True(queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default).IsNull);

        queue.Update(4);

        Assert.True(queue.Cancel(request));
        Assert.False(queue.Cancel(request));
        Assert.Equal(NavPathRequestState.Unknown, queue.GetState(request));
        Assert.Equal(0, queue.PendingCount);

        // And the slot really is free.
        Assert.False(queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default).IsNull);
    }

    [Fact]
    public void ARequestForNowhereComesBackFailedRatherThanNeverComingBack() {
        var mesh = Room();
        var queue = new NavPathQueue(mesh);

        var request = queue.Submit(NavPolyRef.Null, NavPolyRef.Null, Vector3.Zero, Vector3.One, NavQueryFilter.Default);

        Assert.False(request.IsNull);

        queue.Update();

        var path = new NavPolyRef[8];

        Assert.True(queue.TryTakeResult(request, path, out var count, out var status));
        Assert.Equal(NavPathStatus.Failed, status);
        Assert.Equal(0, count);
    }

    [Fact]
    public void AStaleRequestNamesNothingAfterItsSlotIsReused() {
        var mesh = Room();
        var queue = new NavPathQueue(mesh, capacity: 1);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        var first = queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default);
        queue.Cancel(first);

        var second = queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default);

        Assert.Equal(first.Index, second.Index);
        Assert.Equal(NavPathRequestState.Unknown, queue.GetState(first));
        Assert.Equal(NavPathRequestState.Queued, queue.GetState(second));
    }

    [Fact]
    public void ACrowdOfAgentsAllRetargetingStillGetsWhereItIsGoing() {
        var mesh = Room();
        var crowd = new Crowd(mesh);
        var handles = new List<CrowdAgentHandle>();

        for (var index = 0; index < 24; index++) {
            var handle = crowd.AddAgent(new(3f + (index % 6 * 1.2f), 0, 3f + (index / 6 * 1.2f)), new() { Radius = 0.4f, MaxSpeed = 3f });

            Assert.False(handle.IsNull);
            handles.Add(handle);
        }

        // Everybody, in the same update — the case the queue exists for.
        foreach (var handle in handles) {
            crowd.SetTarget(handle, new(36, 0, 36));
        }

        for (var step = 0; step < 2_400; step++) {
            crowd.Update(1f / 60f);
        }

        foreach (var handle in handles) {
            crowd.TryGetState(handle, out var state);

            Assert.True(
                NavGeometry.Distance2D(state.Position, new(36, 0, 36)) < 4f,
                $"An agent finished at {state.Position}, which is not near where the crowd was sent."
            );
        }

        Assert.Equal(0, crowd.Paths.PendingCount);
    }

    [Fact]
    public void AnAgentKeepsWalkingWhileItsNewPathIsBeingWorkedOut() {
        var mesh = Room();
        var crowd = new Crowd(mesh) { PathIterationsPerUpdate = 1 };
        var agent = crowd.AddAgent(new(3, 0, 3), new() { Radius = 0.4f, MaxSpeed = 3f });

        crowd.SetTarget(agent, new(36, 0, 36));

        // Let it get going on the first path.
        for (var step = 0; step < 600; step++) {
            crowd.Update(1f / 60f);
        }

        crowd.TryGetState(agent, out var moving);
        Assert.Equal(CrowdTargetState.Following, moving.State);

        // Now change its mind, and starve the queue so the new search takes many updates.
        crowd.SetTarget(agent, new(3, 0, 36));

        var before = moving.Position;
        var kept = false;

        for (var step = 0; step < 30; step++) {
            crowd.Update(1f / 60f);
            crowd.TryGetState(agent, out var state);

            kept |= NavGeometry.Distance2D(state.Position, before) > 0.1f;
        }

        Assert.True(kept, "The agent stopped dead while it was thinking, rather than walking on.");
    }
}
