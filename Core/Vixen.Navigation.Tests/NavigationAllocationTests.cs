// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Vixen.Testing;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     The half of the navigation design that is a number rather than a behaviour.
/// </summary>
/// <remarks>
///     <para>
///         Zero steady-state allocation in the frame loop is a stated non-negotiable
///         ([00](../../docs/plan/00-vision-and-principles.md) § Non-negotiables), and navigation is
///         one of the subsystems most able to break it: a path is a variable-length answer, a search
///         needs a node pool and an open list, and a crowd asks who is nearby sixty times a second.
///         Every one of those is a place where the obvious implementation allocates per frame and the
///         cost only shows up as a gen-0 collection in somebody else's profile.
///     </para>
///     <para>
///         So the buffers are owned and reused, and that claim is measured here rather than asserted
///         in a comment. What is measured is the <i>steady state</i>: the first search grows the node
///         pool and the first frame of a crowd fills the proximity grid, and neither is a per-frame
///         cost — so the warm-up runs the same work before the measurement starts.
///     </para>
///     <para>
///         <b>Baking is deliberately not measured.</b> It allocates freely and should: it is a
///         content-build step that runs in a tool, and buying a slower bake to save a garbage
///         collection nobody is present for is the wrong trade.
///     </para>
/// </remarks>
public sealed class NavigationAllocationTests {
    const int WarmUpFrames = 200;
    const int MeasuredFrames = 1_000;

    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavMesh Room() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 20, 20)
            .Box(new(0, 0, 9.5f), new(8, 2, 10.5f))
            .Box(new(12, 0, 9.5f), new(20, 2, 10.5f));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return mesh;
    }

    [Fact]
    public void ASearchThatHasRunOnceAllocatesNothingToRunAgain() {
        var query = new NavMeshQuery(Room());

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(17, 0, 17), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Assert.False(start.IsNull);
        Assert.False(end.IsNull);

        var corridor = new NavPolyRef[256];
        var corners = new NavPathPoint[16];
        var found = 0;

        Assert.Equal(0, Measure(Search));
        Assert.True(found > 0, "The search found nothing, so the measurement is of nothing.");

        return;

        void Search() {
            var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

            if (status != NavPathStatus.Failed) {
                found = query.FindStraightPath(startPoint, endPoint, corridor.AsSpan(0, count), corners);
            }
        }
    }

    [Fact]
    public void ARaycastAllocatesNothing() {
        var query = new NavMeshQuery(Room());

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);

        var visited = new NavPolyRef[32];
        var hits = 0;

        Assert.Equal(0, Measure(Cast));
        Assert.True(hits > 0, "Nothing was hit, and this test is about the cast that hits a wall.");

        return;

        void Cast() {
            query.Raycast(start, startPoint, new(3, 0, 17), NavQueryFilter.Default, out var hit, visited, out _);

            if (hit.Hit) {
                hits++;
            }
        }
    }

    [Fact]
    public void MovingAcrossTheSurfaceAllocatesNothing() {
        var query = new NavMeshQuery(Room());

        query.FindNearestPoly(new(3, 0, 8), Extents, NavQueryFilter.Default, out var start, out var startPoint);

        Assert.Equal(0, Measure(Move));

        return;

        // Into the wall, so the slide path is the one being measured rather than the easy case.
        void Move() => query.MoveAlongSurface(start, startPoint, new(3, 0, 12), NavQueryFilter.Default, out _, out _);
    }

    /// <summary>
    ///     Sixteen agents walking back and forth across a room with a wall in it, which is every
    ///     per-frame code path the crowd has: replanning, string-pulling, neighbour lookup, avoidance
    ///     and the move across the surface.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         They walk a <i>route</i>, turning round at each end, rather than walking somewhere once.
    ///         That is what makes the warm-up mean anything: a crowd sent somewhere new is still
    ///         growing its node pool for as long as the paths keep getting longer, and measuring that
    ///         would be measuring the warm-up rather than the steady state it settles into.
    /// </para>
    ///     <para>
    ///         The warm-up is long because the crowd now plans through <c>NavPathQueue</c>, which holds
    ///         four queries and hands each of them whichever request is next. Each has a node pool of
    ///         its own, and the pools only stop growing once every query has run the longest search the
    ///         route produces — which takes rather more than a few hundred frames to happen by
    ///         rotation. Twelve thousand frames of warm-up reports zero; four thousand reported 72
    ///         bytes, which was four pools still settling rather than anything leaking.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ACrowdWalkingAllocatesNothingPerUpdate() {
        var crowd = new Crowd(Room());
        var walker = new CrowdAgentParams { Radius = 0.4f, MaxSpeed = 3f, MaxAcceleration = 8f };
        var handles = new List<CrowdAgentHandle>();

        for (var index = 0; index < 16; index++) {
            var handle = crowd.AddAgent(new(2f + (index % 4 * 1.2f), 0, 2f + (index / 4 * 1.2f)), walker);
            Assert.False(handle.IsNull);
            handles.Add(handle);
        }

        var north = new Vector3(17, 0, 17);
        var south = new Vector3(3, 0, 3);
        var arrivals = 0;

        foreach (var handle in handles) {
            crowd.SetTarget(handle, north);
        }

        Assert.Equal(0, Measure(Frame, warmUp: 12_000));
        Assert.True(arrivals > 0, "Nobody finished a leg, so no replan was measured.");

        return;

        void Frame() {
            crowd.Update(1f / 60f);

            foreach (var handle in handles) {
                if (crowd.TryGetState(handle, out var state) && state.State == CrowdTargetState.Arrived) {
                    crowd.SetTarget(handle, state.Target == north ? south : north);
                    arrivals++;
                }
            }
        }
    }

    /// <summary>
    ///     Runs the work until whatever it grows has stopped growing, then measures the same work
    ///     again. See <see cref="Measured" /> for why the measurement is more than a subtraction.
    /// </summary>
    static long Measure(Action frame, int warmUp = WarmUpFrames)
        => Measured.Bytes(frame, warmUp, MeasuredFrames);
}
