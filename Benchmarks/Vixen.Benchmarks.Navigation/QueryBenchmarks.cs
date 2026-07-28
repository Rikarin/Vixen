// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Mathematics;
using Vixen.Navigation;
using Vixen.Navigation.Baking;

namespace Vixen.Benchmarks.Navigation;

/// <summary>
///     What one agent's worth of thinking costs: finding the polygon it is on, searching for a path,
///     pulling that path straight, and asking what is between it and somewhere else.
/// </summary>
/// <remarks>
///     The path is corner to corner across a pillared level, so the search is a real one — a
///     benchmark that pathfinds between two points in the same polygon measures the function call.
///     Allocation should be zero on every case here; <c>NavigationAllocationTests</c> is the gate,
///     and this is where the number is visible next to a time.
/// </remarks>
[MemoryDiagnoser]
public class QueryBenchmarks {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    readonly NavPolyRef[] corridor = new NavPolyRef[512];
    readonly NavPathPoint[] corners = new NavPathPoint[64];

    NavMeshQuery query = null!;
    NavPolyRef start;
    NavPolyRef end;
    Vector3 startPoint;
    Vector3 endPoint;

    /// <summary>How wide the level is, in metres.</summary>
    [Params(40f, 80f)]
    public float Size { get; set; }

    [GlobalSetup]
    public void Setup() {
        var (vertices, indices) = Level.Build(Size);
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(vertices, indices, Settings)!);
        query = new(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out start, out startPoint);
        query.FindNearestPoly(new(Size - 2, 0, Size - 2), Extents, NavQueryFilter.Default, out end, out endPoint);
    }

    /// <summary>Where am I standing.</summary>
    [Benchmark]
    public bool FindNearestPoly() => query.FindNearestPoly(new(Size * 0.5f, 0, Size * 0.5f), Extents, NavQueryFilter.Default, out _, out _);

    /// <summary>The search. This is what a destination change costs.</summary>
    [Benchmark(Baseline = true)]
    public int FindPath() {
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        return count;
    }

    /// <summary>The string pull. This is what a step of following one costs.</summary>
    [Benchmark]
    public int FindStraightPath() {
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        return query.FindStraightPath(startPoint, endPoint, corridor.AsSpan(0, count), corners);
    }

    /// <summary>Visibility across the level, which is also the movement primitive.</summary>
    [Benchmark]
    public float Raycast() {
        query.Raycast(start, startPoint, endPoint, NavQueryFilter.Default, out var hit);

        return hit.Distance;
    }
}
