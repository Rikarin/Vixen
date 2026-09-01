// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Vixen.Testing;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     The path queue with its slices on the job system.
/// </summary>
/// <remarks>
///     The claim being tested is stronger than "it still works". An update is a sequence of rounds
///     whose shape does not depend on which thread ran what or how fast, so a scheduler must change
///     where the work happens and <i>nothing else</i> — the same paths, ready in the same updates. If
///     that ever stops being true, the queue has grown a dependency on execution order and the
///     scheduler is no longer an implementation detail.
/// </remarks>
public sealed class NavPathQueueJobTests {
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

    /// <summary>Submits a spread of routes across the level, so several searches are in flight at once.</summary>
    static List<NavPathRequest> SubmitRoutes(NavPathQueue queue, NavMesh mesh, int count) {
        var query = new NavMeshQuery(mesh);
        var requests = new List<NavPathRequest>();

        for (var index = 0; index < count; index++) {
            var from = new Vector3(2f + (index % 6 * 6f), 0, 2f + (index / 6 * 6f));
            var to = new Vector3(38f - (index % 5 * 7f), 0, 38f - (index / 5 * 5f));

            if (!query.FindNearestPoly(from, Extents, NavQueryFilter.Default, out var start, out var startPoint) ||
                !query.FindNearestPoly(to, Extents, NavQueryFilter.Default, out var end, out var endPoint)) {
                continue;
            }

            requests.Add(queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default));
        }

        return requests;
    }

    [Fact]
    public void AScheduledQueueAnswersTheSameThingsInTheSameUpdates() {
        var mesh = Room();

        using var jobs = new JobScheduler(2);

        var alone = new NavPathQueue(mesh);
        var scheduled = new NavPathQueue(mesh) { Scheduler = jobs };

        var first = SubmitRoutes(alone, mesh, 24);
        var second = SubmitRoutes(scheduled, mesh, 24);

        Assert.Equal(first.Count, second.Count);

        Span<NavPolyRef> left = stackalloc NavPolyRef[256];
        Span<NavPolyRef> right = stackalloc NavPolyRef[256];

        for (var update = 0; update < 64; update++) {
            alone.Update(96);
            scheduled.Update(96);

            Assert.True(
                alone.LastIterations == scheduled.LastIterations,
                $"Update {update} expanded {alone.LastIterations} polygons on the caller's thread and "
                + $"{scheduled.LastIterations} on the workers. The rounds are meant to be the same rounds."
            );

            Assert.Equal(alone.PendingCount, scheduled.PendingCount);

            for (var index = 0; index < first.Count; index++) {
                var state = alone.GetState(first[index]);

                Assert.True(
                    state == scheduled.GetState(second[index]),
                    $"Request {index} is {state} on the caller's thread and {scheduled.GetState(second[index])} on the workers."
                );

                if (state != NavPathRequestState.Ready) {
                    continue;
                }

                Assert.True(alone.TryTakeResult(first[index], left, out var leftCount, out var leftStatus));
                Assert.True(scheduled.TryTakeResult(second[index], right, out var rightCount, out var rightStatus));

                Assert.Equal(leftStatus, rightStatus);
                Assert.Equal(leftCount, rightCount);

                for (var poly = 0; poly < leftCount; poly++) {
                    Assert.Equal(left[poly], right[poly]);
                }
            }
        }

        Assert.Equal(0, alone.PendingCount);
        Assert.Equal(0, scheduled.PendingCount);
    }

    [Fact]
    public void ACrowdPlanningOnJobsStillGetsWhereItIsGoing() {
        var mesh = Room();

        using var jobs = new JobScheduler(2);

        var crowd = new Crowd(mesh);
        crowd.Paths.Scheduler = jobs;

        var handles = new List<CrowdAgentHandle>();

        for (var index = 0; index < 24; index++) {
            handles.Add(crowd.AddAgent(new(3f + (index % 6 * 1.2f), 0, 3f + (index / 6 * 1.2f)), new() { Radius = 0.4f, MaxSpeed = 3f }));
        }

        foreach (var handle in handles) {
            crowd.SetTarget(handle, new(36, 0, 36));
        }

        for (var step = 0; step < 2_400; step++) {
            crowd.Update(1f / 60f);
        }

        foreach (var handle in handles) {
            crowd.TryGetState(handle, out var state);

            Assert.True(
                NavGeometry.Distance2D(state.Position, new(36, 0, 36)) < 5f,
                $"An agent finished at {state.Position}, which is not near where the crowd was sent."
            );
        }
    }

    [Fact]
    public void SchedulingASliceAllocatesNothing() {
        var mesh = Room();

        using var jobs = new JobScheduler(2);

        var queue = new NavPathQueue(mesh) { Scheduler = jobs };
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint));
        Assert.True(query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint));

        // Warmed until nothing is still settling: the node pools, and the scheduler's payload array
        // for this job type, which is allocated once per type and never again.
        Measured.NothingAllocated(
            Search,
            warmUp: 400,
            passes: 400,
            because: "A job is a struct in a preallocated array, so the only right answer is none."
        );

        return;

        void Search() {
            Span<NavPolyRef> path = stackalloc NavPolyRef[256];

            Step(queue, start, end, startPoint, endPoint, path);
        }
    }

    static void Step(NavPathQueue queue, NavPolyRef start, NavPolyRef end, Vector3 startPoint, Vector3 endPoint, Span<NavPolyRef> path) {
        var request = queue.Submit(start, end, startPoint, endPoint, NavQueryFilter.Default);

        while (queue.GetState(request) != NavPathRequestState.Ready) {
            queue.Update(1_000_000);
        }

        queue.TryTakeResult(request, path, out _, out _);
    }
}
