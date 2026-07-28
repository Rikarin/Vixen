// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;

namespace Vixen.Benchmarks.Navigation;

/// <summary>
///     What a bake costs, which is the number that decides whether rebaking a tile is something an
///     editor can do while somebody drags a crate around.
/// </summary>
/// <remarks>
///     <para>
///         Allocation is measured and is expected to be large. Baking is a tool operation, and buying
///         a slower bake to avoid a collection nobody is present for would be the wrong trade — what
///         the number is for is comparing one bake against another, not against a frame budget.
///     </para>
///     <para>
///         Cell size is the parameter because it is the one that matters: it is quadratic in the
///         voxel count and it is the dial a project actually turns.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class BakeBenchmarks {
    Vector3[] vertices = [];
    int[] indices = [];
    BoundingBox bounds;

    /// <summary>How wide the level is, in metres.</summary>
    [Params(40f, 80f)]
    public float Size { get; set; }

    /// <summary>How wide a voxel column is.</summary>
    [Params(0.2f, 0.3f)]
    public float CellSize { get; set; }

    /// <summary>How the surface is cut into regions — the distance-field flood, or the row sweep.</summary>
    /// <remarks>
    ///     Here because it is the one bake setting with a real trade in it rather than a quality dial:
    ///     watershed costs bake time and buys polygon shape, and which way that comes out depends on
    ///     whether the level is built out of axis-aligned boxes. The navigation README has the polygon
    ///     counts to put beside these times.
    /// </remarks>
    [Params(NavMeshPartitioning.Watershed, NavMeshPartitioning.Monotone)]
    public NavMeshPartitioning Partitioning { get; set; }

    NavMeshBuildSettings Settings => new() {
        CellSize = CellSize,
        CellHeight = CellSize * 0.5f,
        AgentRadius = 0.6f,
        Partitioning = Partitioning
    };

    [GlobalSetup]
    public void Setup() {
        (vertices, indices) = Level.Build(Size);
        bounds = NavMeshBaker.Volume(vertices, Settings);
    }

    /// <summary>The whole level in one tile — what a small level's build does once.</summary>
    [Benchmark(Baseline = true)]
    public int WholeLevel() => NavMeshBaker.Bake(vertices, indices, bounds, Settings)?.Polys.Length ?? 0;

    /// <summary>
    ///     The same level in tiles, which is more total work — every tile rasterises the geometry
    ///     overlapping its margin as well as its own — and is what makes a rebake local.
    /// </summary>
    [Benchmark]
    public int Tiled() => NavMeshBaker.BakeTiles(vertices, indices, bounds, Settings, 64).Tiles.Count;
}
