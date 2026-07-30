// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>An undirected weighted graph in compressed rows, which is what the partitioner eats.</summary>
/// <remarks>
///     Rows rather than adjacency lists because every consumer here walks a node's neighbours and
///     none of them inserts, and because a partition of a hundred thousand triangles allocating a
///     list per triangle is most of the build's memory traffic for none of its work.
/// </remarks>
sealed class Graph {
    /// <summary>Where each node's neighbours start in <see cref="Neighbours" />. One longer than the node count.</summary>
    public required int[] Offsets { get; init; }

    /// <summary>Every node's neighbours, ascending within a node.</summary>
    public required int[] Neighbours { get; init; }

    /// <summary>How strongly each neighbour is attached — for triangles, how many edges they share.</summary>
    public required int[] Weights { get; init; }

    /// <summary>How many nodes there are.</summary>
    public int NodeCount => Offsets.Length - 1;
}

/// <summary>Welding, adjacency and boundaries — everything the build needs to know about who touches whom.</summary>
/// <remarks>
///     <para>
///         <b>Topology is decided by position, not by index.</b> An exporter splits a vertex wherever
///         an attribute is discontinuous, so a seam in the UVs, a hard edge in the normals or a
///         change of material leaves two vertices at one point — and an adjacency built on indices
///         reads that as a hole. Clustering across it would then cut every seam into its own
///         component, and a group's "boundary" would include seams that are not boundaries at all,
///         which is the difference between a mesh that simplifies and one that does not.
///     </para>
///     <para>
///         Positions are compared exactly rather than within a tolerance. A split vertex is a
///         <em>copy</em> — the exporter wrote the same three floats twice — so exact comparison finds
///         every one of them, and a tolerance would additionally weld two surfaces that an artist
///         placed a hair apart on purpose.
///     </para>
/// </remarks>
static class Topology {
    /// <summary>Maps every vertex to the first vertex sharing its position.</summary>
    /// <param name="positions">The positions.</param>
    /// <returns>One representative index per vertex. A vertex nothing shares a position with maps to itself.</returns>
    /// <remarks>
    ///     The representative is the <em>lowest</em> index of the group, which is what makes the
    ///     whole build independent of dictionary iteration order: everything downstream keys off
    ///     these ids, and an id that depended on hashing would give two machines two different DAGs
    ///     for one mesh.
    /// </remarks>
    public static int[] Weld(ReadOnlySpan<Vector3> positions) {
        var welded = new int[positions.Length];
        var first = new Dictionary<Vector3, int>(positions.Length);

        for (var vertex = 0; vertex < positions.Length; vertex++) {
            if (!first.TryGetValue(positions[vertex], out var representative)) {
                first[positions[vertex]] = representative = vertex;
            }

            welded[vertex] = representative;
        }

        return welded;
    }

    /// <summary>Which welded vertices carry more than one set of attributes.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="welded">What <see cref="Weld" /> produced.</param>
    /// <returns>One flag per vertex, meaningful at the representative's index.</returns>
    /// <remarks>
    ///     A seam vertex neither moves nor is moved onto — see <see cref="MeshSimplifier" /> for why
    ///     both halves are needed. What is <em>not</em> a seam is a vertex an exporter split for no
    ///     reason, which happens constantly: two copies with identical attributes are the same vertex
    ///     and are welded into one, and the simplification is free to collapse it like any other.
    /// </remarks>
    public static bool[] FindSeams(MeshletBuildInput mesh, int[] welded) {
        var seam = new bool[mesh.VertexCount];

        for (var vertex = 0; vertex < welded.Length; vertex++) {
            var representative = welded[vertex];

            if (representative != vertex && !SameAttributes(mesh, vertex, representative)) {
                seam[representative] = true;
            }
        }

        return seam;
    }

    /// <summary>Whether two vertices carry identical attributes.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="left">One vertex.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether every attribute the mesh has agrees between them.</returns>
    /// <remarks>
    ///     Exact equality again, and for the same reason: these are copies of one another or they are
    ///     a seam. There is no third case where an exporter wrote <em>nearly</em> the same normal.
    /// </remarks>
    static bool SameAttributes(MeshletBuildInput mesh, int left, int right) {
        if (mesh.Normals.Length != 0 && mesh.Normals[left] != mesh.Normals[right]) {
            return false;
        }

        if (mesh.Tangents.Length != 0 && mesh.Tangents[left] != mesh.Tangents[right]) {
            return false;
        }

        if (mesh.TexCoords.Length != 0 && mesh.TexCoords[left] != mesh.TexCoords[right]) {
            return false;
        }

        for (var influence = 0; influence < 4; influence++) {
            if (mesh.BoneIndices.Length != 0 &&
                mesh.BoneIndices[(left * 4) + influence] != mesh.BoneIndices[(right * 4) + influence]) {
                return false;
            }

            if (mesh.BoneWeights.Length != 0 &&
                mesh.BoneWeights[(left * 4) + influence] != mesh.BoneWeights[(right * 4) + influence]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>One key for the undirected edge between two welded vertices.</summary>
    /// <param name="left">One end.</param>
    /// <param name="right">The other.</param>
    /// <returns>A key that does not depend on which end was given first.</returns>
    public static long Edge(int left, int right) =>
        left < right ? ((long)left << 32) | (uint)right : ((long)right << 32) | (uint)left;

    /// <summary>The graph whose nodes are triangles and whose edges are shared mesh edges.</summary>
    /// <param name="corners">Three vertex indices per triangle.</param>
    /// <param name="welded">What <see cref="Weld" /> produced.</param>
    /// <returns>The adjacency, weighted by how many edges each pair shares.</returns>
    /// <remarks>
    ///     Two triangles sharing two edges — which happens on a folded-over quad and on any
    ///     degenerate an exporter left behind — count twice, so the partitioner keeps them together
    ///     rather than treating the pair as an ordinary neighbour.
    /// </remarks>
    public static Graph BuildTriangleGraph(ReadOnlySpan<int> corners, int[] welded) {
        var triangles = corners.Length / 3;

        // Chained through two flat arrays rather than a list per edge. The obvious shape — a
        // dictionary of edge to the list of triangles on it — allocates one list per edge of the
        // mesh, which on a mesh of any size is hundreds of thousands of small objects live at once,
        // and the cost of that is not the allocation but every subsequent collection having to walk
        // them. It measured as the single largest cost of a build.
        var head = new Dictionary<long, int>(corners.Length);
        var next = new int[corners.Length];
        var owner = new int[corners.Length];
        var entries = 0;

        for (var triangle = 0; triangle < triangles; triangle++) {
            for (var corner = 0; corner < 3; corner++) {
                var a = welded[corners[(triangle * 3) + corner]];
                var b = welded[corners[(triangle * 3) + ((corner + 1) % 3)]];

                if (a == b) {
                    continue;
                }

                var key = Edge(a, b);
                owner[entries] = triangle;
                next[entries] = head.TryGetValue(key, out var previous) ? previous : -1;
                head[key] = entries++;
            }
        }

        var pairs = new List<long>(entries);

        foreach (var start in head.Values) {
            for (var left = start; left >= 0; left = next[left]) {
                for (var right = next[left]; right >= 0; right = next[right]) {
                    if (owner[left] != owner[right]) {
                        pairs.Add(Edge(owner[left], owner[right]));
                    }
                }
            }
        }

        return FromSortedPairs(triangles, pairs);
    }

    /// <summary>Turns a bag of undirected pairs into rows, counting repeats as weight.</summary>
    /// <param name="nodeCount">How many nodes there are.</param>
    /// <param name="pairs">The pairs, keyed by <see cref="Edge" />, in any order and with repeats.</param>
    /// <returns>The graph.</returns>
    static Graph FromSortedPairs(int nodeCount, List<long> pairs) {
        // The list's own storage, sorted and then compacted in place. A mesh of a million triangles
        // puts three million longs through here, and every copy of that is twenty-four megabytes on
        // the large-object heap for a value that is about to be thrown away.
        var keys = CollectionsMarshal.AsSpan(pairs);
        keys.Sort();

        var weights = new int[keys.Length];
        var count = 0;

        foreach (var key in keys) {
            if (count > 0 && keys[count - 1] == key) {
                weights[count - 1]++;
            } else {
                keys[count] = key;
                weights[count++] = 1;
            }
        }

        return FromSorted(nodeCount, keys[..count], weights.AsSpan(0, count));
    }

    /// <summary>Turns a set of weighted undirected pairs into rows.</summary>
    /// <param name="nodeCount">How many nodes there are.</param>
    /// <param name="pairs">The pairs, keyed by <see cref="Edge" />, valued by weight.</param>
    /// <returns>The graph.</returns>
    /// <remarks>
    ///     The rows come out ascending because the pairs are sorted first. Dictionary order is not
    ///     stable across runs of a process, and a partition that walked its neighbours in that order
    ///     would produce a different — equally good, entirely incomparable — DAG on every build.
    /// </remarks>
    public static Graph FromPairs(int nodeCount, Dictionary<long, int> pairs) {
        var keys = pairs.Keys.ToArray();
        Array.Sort(keys);

        var weights = new int[keys.Length];

        for (var index = 0; index < keys.Length; index++) {
            weights[index] = pairs[keys[index]];
        }

        return FromSorted(nodeCount, keys, weights);
    }

    /// <summary>Turns sorted, distinct weighted pairs into rows.</summary>
    /// <param name="nodeCount">How many nodes there are.</param>
    /// <param name="keys">The pairs, keyed by <see cref="Edge" />, ascending and without repeats.</param>
    /// <param name="pairWeights">What each is worth.</param>
    /// <returns>The graph.</returns>
    static Graph FromSorted(int nodeCount, ReadOnlySpan<long> keys, ReadOnlySpan<int> pairWeights) {
        var offsets = new int[nodeCount + 1];

        foreach (var key in keys) {
            offsets[(int)(key >> 32) + 1]++;
            offsets[(int)(key & 0xFFFFFFFF) + 1]++;
        }

        for (var node = 0; node < nodeCount; node++) {
            offsets[node + 1] += offsets[node];
        }

        var neighbours = new int[offsets[nodeCount]];
        var weights = new int[offsets[nodeCount]];
        var cursor = (int[])offsets.Clone();

        for (var index = 0; index < keys.Length; index++) {
            var left = (int)(keys[index] >> 32);
            var right = (int)(keys[index] & 0xFFFFFFFF);

            neighbours[cursor[left]] = right;
            weights[cursor[left]++] = pairWeights[index];
            neighbours[cursor[right]] = left;
            weights[cursor[right]++] = pairWeights[index];
        }

        // Sorted keys put each row's higher-numbered neighbours in order but leave the ones that
        // arrived from the other side of the pair wherever they landed, so each row is sorted once
        // more. Rows are short — a triangle has three neighbours — so this is cheaper than it reads.
        for (var node = 0; node < nodeCount; node++) {
            Array.Sort(neighbours, weights, offsets[node], offsets[node + 1] - offsets[node]);
        }

        return new() { Offsets = offsets, Neighbours = neighbours, Weights = weights };
    }

    /// <summary>The edges a set of triangles has on its outside.</summary>
    /// <param name="corners">Three vertex indices per triangle.</param>
    /// <param name="welded">What <see cref="Weld" /> produced.</param>
    /// <param name="triangles">Which triangles are in the set.</param>
    /// <returns>The keys of the edges used by exactly one triangle of the set.</returns>
    /// <remarks>
    ///     <b>This is the definition the whole scheme rests on.</b> An edge inside the set is used
    ///     twice and may be collapsed; an edge on the outside is used once, is shared with a triangle
    ///     somebody else will draw, and may not move by so much as a float. An edge used three times
    ///     — non-manifold geometry, two surfaces meeting at a fin — counts as interior, which is the
    ///     conservative reading in the direction that matters: it stays where it is only if some
    ///     other rule keeps it there.
    /// </remarks>
    public static HashSet<long> BoundaryEdges(ReadOnlySpan<int> corners, int[] welded, ReadOnlySpan<int> triangles) {
        var counts = new Dictionary<long, int>();

        foreach (var triangle in triangles) {
            for (var corner = 0; corner < 3; corner++) {
                var a = welded[corners[(triangle * 3) + corner]];
                var b = welded[corners[(triangle * 3) + ((corner + 1) % 3)]];

                if (a == b) {
                    continue;
                }

                var key = Edge(a, b);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var boundary = new HashSet<long>();

        foreach (var (key, count) in counts) {
            if (count == 1) {
                boundary.Add(key);
            }
        }

        return boundary;
    }
}
