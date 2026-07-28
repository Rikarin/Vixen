// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Mathematics;
using Vixen.Navigation;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;

namespace Vixen.Benchmarks.Navigation;

/// <summary>
///     One frame of a crowd: steering, avoidance, separation and the move across the surface, for
///     everybody.
/// </summary>
/// <remarks>
///     <para>
///         The agents walk a route and turn round at each end, so the workload is the same on the
///         thousandth invocation as on the first — a crowd that arrives somewhere and stops would
///         measure a crowd standing still, which is a number this design already knows is small.
///     </para>
///     <para>
///         <b>Zero allocated bytes is the result to look for</b>, at every agent count. That is the
///         frame-loop non-negotiable, and the interesting failure is not a slow update but one whose
///         allocation grows with the crowd.
///     </para>
///     <para>
///         Avoidance is a parameter because it is the expensive half — a candidate velocity is scored
///         against every neighbour, so it is the term that scales with density rather than with
///         population.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class CrowdBenchmarks {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };

    readonly List<CrowdAgentHandle> handles = [];

    Crowd crowd = null!;
    Vector3 north;
    Vector3 south;
    NavPolyRef northPoly;
    NavPolyRef southPoly;
    Vector3 northPoint;
    Vector3 southPoint;
    bool sendNorth;

    /// <summary>How many agents are in the crowd.</summary>
    [Params(16, 64, 256)]
    public int Agents { get; set; }

    /// <summary>Whether the agents steer around each other.</summary>
    [Params(true, false)]
    public bool Avoidance { get; set; }

    /// <summary>
    ///     How many polygon expansions the path queue may do per update. The large value is what
    ///     planning inline used to cost, because it lets the queue answer everything at once.
    /// </summary>
    [Params(256, 1_000_000)]
    public int PathBudget { get; set; }

    /// <summary>
    ///     Whether the new destination is given as a polygon the caller already has, or as a point the
    ///     crowd has to find.
    /// </summary>
    /// <remarks>
    ///     The difference is one nearest-polygon search per agent per retarget, and once the start
    ///     lookup was removed that search was the largest single thing left in this frame. A patrol
    ///     point or a rally marker is resolved once when the level loads; a destination picked from
    ///     under the player's cursor is not.
    /// </remarks>
    [Params(true, false)]
    public bool ResolvedTarget { get; set; }

    [GlobalSetup]
    public void Setup() {
        const float Size = 80f;

        var (vertices, indices) = Level.Build(Size);
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(vertices, indices, Settings)!);

        crowd = new(mesh);
        north = new(Size - 4, 0, Size - 4);
        south = new(4, 0, 4);

        // Resolved once, which is what a patrol point or a rally marker is: the game knew where it
        // was sending everybody long before it decided to send them.
        crowd.Query.FindNearestPoly(north, crowd.SearchExtents, crowd.Filter, out northPoly, out northPoint);
        crowd.Query.FindNearestPoly(south, crowd.SearchExtents, crowd.Filter, out southPoly, out southPoint);

        var parameters = new CrowdAgentParams { Radius = 0.5f, MaxSpeed = 3f, AvoidanceEnabled = Avoidance };
        var columns = (int)MathF.Ceiling(MathF.Sqrt(Agents));

        handles.Clear();

        for (var index = 0; index < Agents; index++) {
            var handle = crowd.AddAgent(new(6f + (index % columns * 1.4f), 0, 6f + (index / columns * 1.4f)), parameters);

            if (!handle.IsNull) {
                handles.Add(handle);
                crowd.SetTarget(handle, north);
            }
        }

        // Warmed here rather than by BenchmarkDotNet's own warm-up, because what has to settle is the
        // node pool and the proximity grid, and those settle in simulated time rather than in
        // invocations.
        for (var frame = 0; frame < 240; frame++) {
            Step();
        }
    }

    /// <summary>One frame at sixty hertz.</summary>
    [Benchmark]
    public void Update() => Step();

    /// <summary>
    ///     The frame in which every agent is given a new destination at once — a door opening, an
    ///     alarm, a player being spotted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the frame `NavPathQueue` exists for, and the only one where it shows. Raise
    ///         <see cref="Crowd.PathIterationsPerUpdate" /> far enough and the queue answers everything
    ///         in the update that asked, which is what planning inline used to cost; leave it at the
    ///         default and the same storm costs a budget.
    ///     </para>
    ///     <para>
    ///         The direction alternates by invocation rather than by reading each agent's current
    ///         destination back. Reading it back compares against the point the caller passed, and a
    ///         destination given as a polygon carries the point <i>on</i> that polygon — so the
    ///         comparison would be false every time and half the benchmark would quietly stop
    ///         alternating.
    ///     </para>
    /// </remarks>
    [Benchmark]
    public void RetargetStorm() {
        crowd.PathIterationsPerUpdate = PathBudget;
        sendNorth = !sendNorth;

        foreach (var handle in handles) {
            if (ResolvedTarget) {
                crowd.SetTarget(handle, sendNorth ? northPoly : southPoly, sendNorth ? northPoint : southPoint);
            } else {
                crowd.SetTarget(handle, sendNorth ? north : south);
            }
        }

        crowd.Update(1f / 60f);
    }

    void Step() {
        crowd.Update(1f / 60f);

        foreach (var handle in handles) {
            if (crowd.TryGetState(handle, out var state) && state.State is CrowdTargetState.Arrived or CrowdTargetState.Failed) {
                crowd.SetTarget(handle, state.Target == north ? south : north);
            }
        }
    }
}
