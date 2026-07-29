// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>What a cluster-DAG build is being asked for: how big a cluster is, and how many of them
///     are simplified together.</summary>
/// <remarks>
///     <para>
///         <b>Two sizes, and they do different jobs.</b> <see cref="MaxTriangles" /> is the unit of
///         culling and of streaming — every cluster is accepted or rejected whole, so it is the
///         granularity of everything the device does. <see cref="GroupSize" /> is the unit of
///         <em>simplification</em>, and it is what decides how much of a mesh a level of detail can
///         actually remove: a group's shared boundary is locked, so a group of one cluster can
///         collapse nothing at all and a group of thirty-two collapses almost everything inside it.
///     </para>
///     <para>
///         <b>The defaults are Nanite's, and they are not arbitrary.</b> 128 triangles is about what
///         a workgroup can hold and about where the per-cluster overhead stops mattering; groups of
///         eight to thirty-two are where the locked boundary is a small fraction of the group's
///         edges. Both are dials rather than constants because the right answer depends on the
///         mesh — a terrain patch and a character do not want the same numbers.
///     </para>
/// </remarks>
public readonly record struct MeshletBuildSettings {
    /// <summary>The defaults: 128-triangle clusters simplified sixteen at a time.</summary>
    public MeshletBuildSettings() { }

    /// <summary>The most triangles one cluster may hold.</summary>
    /// <remarks>
    ///     The partition targets this and the packing enforces it, so a partition that would exceed
    ///     it becomes two clusters rather than one oversized one.
    /// </remarks>
    public int MaxTriangles { get; init; } = 128;

    /// <summary>The most distinct vertices one cluster may reference.</summary>
    /// <remarks>
    ///     At most 256, because a cluster's triangles index its own vertex list with a byte — which
    ///     is what makes the index data a quarter of what a 32-bit index would cost, in the buffer
    ///     that phase 2 pages in and out. A closed patch of 128 triangles carries about seventy
    ///     vertices, so this rarely binds; where it does, the cluster is split.
    /// </remarks>
    public int MaxVertices { get; init; } = 128;

    /// <summary>How many clusters are simplified together as a group.</summary>
    /// <remarks>
    ///     <para>
    ///         The single most consequential number here. Clusters in a group are simplified as one
    ///         unit with the group's <em>outer</em> boundary locked, which is what lets interior
    ///         detail — including every edge between two clusters of the group — collapse while any
    ///         cut through the DAG still meets along edges that were never moved.
    ///     </para>
    ///     <para>
    ///         Too small and there is nothing interior to collapse; too large and the group spans
    ///         parts of the mesh that have no business being simplified together, and the error
    ///         metric becomes the maximum over an area rather than over a neighbourhood.
    ///     </para>
    /// </remarks>
    public int GroupSize { get; init; } = 16;

    /// <summary>What fraction of a group's triangles a simplification aims to keep.</summary>
    /// <remarks>
    ///     A half, which makes the DAG's depth logarithmic in the triangle count and each level's
    ///     error roughly twice the one below it. Raising it makes a finer-grained chain of levels at
    ///     the cost of more of them; a value at or above one would never terminate and is refused.
    /// </remarks>
    public float SimplifyRatio { get; init; } = 0.5f;

    /// <summary>How many triangles the fallback mesh may have.</summary>
    /// <remarks>
    ///     The fallback is a cut through the finished DAG at a fixed budget, emitted as an ordinary
    ///     indexed mesh: it is what WebGL2 draws, what the physics cook reads, and what anything else
    ///     the virtualized path does not reach falls back to. Generated rather than authored, so a
    ///     mesh cannot ship with a fallback that disagrees with the geometry it stands in for.
    /// </remarks>
    public int FallbackTriangles { get; init; } = 4096;

    /// <summary>How many levels the build may produce before it gives up.</summary>
    /// <remarks>
    ///     A guard rather than a dial. Each level is meant to halve the cluster count, so even a
    ///     hundred-million-triangle mesh converges in about twenty; a build that reaches this has hit
    ///     a mesh whose topology defeats simplification, and stopping with a wide root is better than
    ///     looping.
    /// </remarks>
    public int MaxLevels { get; init; } = 32;

    /// <summary>Whether groups within a level are simplified in parallel.</summary>
    /// <remarks>
    ///     Groups within a level share no vertices they are allowed to move — the shared ones are
    ///     exactly the ones that are locked — so this changes the order the work happens in and
    ///     nothing about the result. A test asserts that rather than leaving it as a claim.
    /// </remarks>
    public bool Parallel { get; init; } = true;

    /// <summary>
    ///     Deliberately wrong: locks every <em>cluster's</em> boundary rather than the group's.
    /// </summary>
    /// <remarks>
    ///     The sabotage the phase-1 exit criterion names. Per-cluster locking is the obvious reading
    ///     of "do not move the shared edges", and it is wrong in a way that does not look wrong:
    ///     nothing cracks, because locking more than necessary never does, and no level reaches the
    ///     reduction it asked for, because every edge between two clusters of a group is also some
    ///     cluster's boundary. Internal because it exists for the test that measures what it costs.
    /// </remarks>
    internal bool LockClusterBoundaries { get; init; }

    /// <summary>Deliberately wrong: locks nothing at all.</summary>
    /// <remarks>
    ///     The sabotage that proves the boundary check is a check. With no lock a group's outer
    ///     boundary is simplified along with its interior, which is the crack the whole scheme exists
    ///     to prevent — and it is invisible in the asset, in the build and in any test that draws one
    ///     level at a time. <see cref="MeshletValidator" /> refuses a DAG built this way.
    /// </remarks>
    internal bool UnlockGroupBoundaries { get; init; }

    /// <summary>
    ///     Deliberately wrong: records a group's own simplification error without taking the maximum
    ///     against its children's.
    /// </summary>
    /// <remarks>
    ///     The other sabotage. A parent whose error is below one of its children's lets a cut pick
    ///     the parent on one side of a seam and the child on the other, which is the crack the
    ///     monotonicity check exists to refuse.
    /// </remarks>
    internal bool SkipErrorMonotonicity { get; init; }

    /// <summary>Refuses settings that cannot produce a DAG.</summary>
    /// <exception cref="ArgumentOutOfRangeException">One of them is out of range.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTriangles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxVertices, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxVertices, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(GroupSize, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SimplifyRatio, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(SimplifyRatio, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(FallbackTriangles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLevels, 1);
    }
}
