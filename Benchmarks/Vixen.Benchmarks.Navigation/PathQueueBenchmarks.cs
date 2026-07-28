// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Navigation;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;

namespace Vixen.Benchmarks.Navigation;

/// <summary>
///     A batch of searches through the path queue, on the caller's thread and on the job system.
/// </summary>
/// <remarks>
///     <para>
///         Thirty-two routes across an eighty-metre level, all submitted at once and driven to
///         completion — the shape of a retarget storm with the per-agent endpoint lookups taken out,
///         so what is left is the searching.
///     </para>
///     <para>
///         <b>The interesting number is not the ratio but its ceiling.</b> An update is a sequence of
///         rounds and a round is a barrier: every assigned query advances by its share, and only then
///         is anything collected. So a round costs its <i>longest</i> search, and the routes here vary
///         from a few polygons to fifty. That barrier is the price of the queue giving the same
///         answers in the same updates whether or not there is a scheduler, which is a property worth
///         more than the throughput it costs.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class PathQueueBenchmarks {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    readonly List<(NavPolyRef Poly, Vector3 Point)> starts = [];
    readonly List<(NavPolyRef Poly, Vector3 Point)> ends = [];
    readonly List<NavPathRequest> requests = [];
    readonly NavPolyRef[] path = new NavPolyRef[512];

    JobScheduler? scheduler;
    NavPathQueue queue = null!;

    /// <summary>How many searches may be part-way at once, which is how many can be on jobs at once.</summary>
    [Params(2, 4, 8)]
    public int ParallelSearches { get; set; }

    /// <summary>Whether the slices run on the job system or on the calling thread.</summary>
    [Params(true, false)]
    public bool UseJobs { get; set; }

    [GlobalSetup]
    public void Setup() {
        const float Size = 80f;

        var (vertices, indices) = Level.Build(Size);
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(vertices, indices, Settings)!);

        scheduler = UseJobs ? new JobScheduler() : null;
        queue = new(mesh, capacity: 64, parallelSearches: ParallelSearches) { Scheduler = scheduler };

        var query = new NavMeshQuery(mesh);

        for (var index = 0; index < 32; index++) {
            var from = new Vector3(3f + (index % 8 * 3f), 0, 3f + (index / 8 * 3f));
            var to = new Vector3(Size - 4f - (index % 7 * 4f), 0, Size - 4f - (index / 7 * 4f));

            if (query.FindNearestPoly(from, Extents, NavQueryFilter.Default, out var start, out var startPoint) &&
                query.FindNearestPoly(to, Extents, NavQueryFilter.Default, out var end, out var endPoint)) {
                starts.Add((start, startPoint));
                ends.Add((end, endPoint));
            }
        }

        // The node pools and the scheduler's payload array for this job type both settle over the
        // first few rounds, and neither settles in a way BenchmarkDotNet's warm-up would reach —
        // it measures one invocation, and the pools grow across invocations.
        for (var warm = 0; warm < 40; warm++) {
            Round();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => scheduler?.Dispose();

    /// <summary>Every route submitted at once and searched to completion.</summary>
    [Benchmark]
    public void Batch() => Round();

    void Round() {
        requests.Clear();

        for (var index = 0; index < starts.Count; index++) {
            requests.Add(queue.Submit(starts[index].Poly, ends[index].Poly, starts[index].Point, ends[index].Point, NavQueryFilter.Default));
        }

        while (queue.PendingCount > 0) {
            queue.Update(1_000_000);
        }

        foreach (var request in requests) {
            queue.TryTakeResult(request, path, out _, out _);
        }
    }
}
