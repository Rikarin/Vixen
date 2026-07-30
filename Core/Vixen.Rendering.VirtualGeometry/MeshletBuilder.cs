// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Turns a mesh into a cluster DAG: every level of detail at once, and no cracks between them.</summary>
/// <remarks>
///     <para>
///         Phase 1 of <c>docs/virtualized-geometry.md</c>, and the phase that decides whether the
///         result has cracks. The loop is four steps and the third is the whole trick:
///     </para>
///     <para>
///         <b>Cluster</b> the triangles into groups of about
///         <see cref="MeshletBuildSettings.MaxTriangles" /> by an edge-cut partition of the triangle
///         adjacency graph, so a cluster is a patch of surface rather than a run of the index buffer.
///         <b>Group</b> neighbouring clusters, about
///         <see cref="MeshletBuildSettings.GroupSize" /> of them, by the same partition one level up.
///         <b>Simplify the group as a unit with its shared outer boundary locked</b> — which lets
///         every edge <em>interior</em> to the group collapse, including the edges between its
///         clusters, while guaranteeing that any cut through the finished DAG meets along edges that
///         were never moved. <b>Split</b> the simplified result back into clusters, and repeat.
///     </para>
///     <para>
///         Locking the group's boundary rather than each cluster's is what makes the hierarchy
///         coarsen. Every edge <em>between</em> two clusters of a group is some cluster's boundary,
///         so the per-cluster lock — which is the obvious reading — leaves only cluster interiors to
///         collapse: a level then reaches about a third off rather than the half it asked for, and
///         the DAG needs more levels and carries more error at every one of them. It is not a crack,
///         because locking more than necessary never is. It is measured in
///         <c>MeshletValidatorTests</c> as the quality failure it actually is.
///     </para>
///     <para>
///         <b>Nothing here is heuristic about correctness.</b> The error metric may be pessimistic and
///         the partition may be a few edges off optimal; what may not happen is a parent whose error
///         is not strictly above every child's, or a group boundary that moved. Both are checked by
///         <see cref="MeshletValidator" /> against the finished DAG rather than trusted from here.
///     </para>
/// </remarks>
public static class MeshletBuilder {
    /// <summary>Builds the DAG.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="options">How big clusters and groups are. Omitted takes the defaults.</param>
    /// <returns>The DAG, with the fallback mesh already cut from it.</returns>
    /// <exception cref="ArgumentException">The mesh is not whole triangles, or an index is out of range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    public static MeshletMesh Build(MeshletBuildInput mesh, MeshletBuildSettings? options = null) {
        ArgumentNullException.ThrowIfNull(mesh);

        var settings = options ?? new MeshletBuildSettings();
        settings.Validate();
        mesh.Validate();

        if (mesh.TriangleCount == 0) {
            return new();
        }

        return new Session(mesh, settings).Run();
    }

    /// <summary>One cluster while the DAG is still being built.</summary>
    /// <param name="Index">Which meshlet it is.</param>
    /// <param name="Corners">Its triangles, as three source-vertex indices each.</param>
    /// <param name="Error">What it deviates from the original mesh by.</param>
    readonly record struct Cluster(int Index, int[] Corners, float Error);

    /// <summary>The state of one mesh's build.</summary>
    sealed class Session {
        readonly MeshletBuildInput mesh;
        readonly MeshletBuildSettings settings;
        readonly int[] welded;
        readonly bool[] seam;
        readonly float[] vertexError;

        readonly List<Meshlet> meshlets = [];
        readonly List<MeshletGroup> groups = [];
        readonly List<int> vertices = [];
        readonly List<byte> triangles = [];

        public Session(MeshletBuildInput mesh, MeshletBuildSettings settings) {
            this.mesh = mesh;
            this.settings = settings;

            welded = Topology.Weld(mesh.Positions);
            seam = Topology.FindSeams(mesh, welded);
            vertexError = new float[mesh.VertexCount];
        }

        /// <summary>Runs the build to a root and cuts the fallback out of what it produced.</summary>
        /// <returns>The DAG.</returns>
        public MeshletMesh Run() {
            var current = Split(mesh.Indices, 0, -1, 0f);
            var levels = 1;

            for (var level = 1; level < settings.MaxLevels && current.Count > 1; level++) {
                var next = Simplify(current, level);

                if (next.Count == 0) {
                    // Every group refused to reduce — a mesh whose topology defeats the simplifier,
                    // which is a wide root and not a failure. Stopping here leaves a valid DAG that
                    // simply does not go as coarse as it might.
                    break;
                }

                current = next;
                levels = level + 1;
            }

            var roots = new List<int>();

            for (var index = 0; index < meshlets.Count; index++) {
                if (meshlets[index].Group < 0) {
                    roots.Add(index);
                }
            }

            var built = new MeshletMesh {
                Meshlets = [.. meshlets],
                Groups = [.. groups],
                Vertices = [.. vertices],
                Triangles = [.. triangles],
                Roots = [.. roots],
                LevelCount = levels
            };

            return built with { Fallback = MeshletCut.Flatten(built, MeshletCut.SelectByBudget(built, settings.FallbackTriangles)) };
        }

        /// <summary>Simplifies one level's clusters in groups and splits the results back into clusters.</summary>
        /// <param name="current">The level's clusters.</param>
        /// <param name="level">Which level the parents will be at.</param>
        /// <returns>The parents, which are the next level's clusters.</returns>
        List<Cluster> Simplify(List<Cluster> current, int level) {
            var members = GroupClusters(current);
            var results = new SimplifyResult?[members.Count];

            void One(int index) {
                var group = members[index];
                var corners = Gather(current, group);
                var locked = LockedEdges(current, group, corners);
                var target = Math.Max(1, (int)(corners.Length / 3 * settings.SimplifyRatio));
                var result = MeshSimplifier.Simplify(mesh.Positions, welded, seam, vertexError, corners, locked, target);

                // A group that did not get smaller is a group that would be simplified again next
                // level, with the same locks and the same answer. Its clusters become roots instead.
                results[index] = result.Corners.Length < corners.Length ? result : null;
            }

            if (settings.Parallel && members.Count > 1) {
                System.Threading.Tasks.Parallel.For(0, members.Count, One);
            } else {
                for (var index = 0; index < members.Count; index++) {
                    One(index);
                }
            }

            var parents = new List<Cluster>();

            // Emission is sequential whatever the simplification did, because the meshlet indices,
            // the group indices and the shared vertex and triangle arrays are all positional — and a
            // DAG whose numbering depended on which group finished first would not be the same DAG
            // twice.
            for (var index = 0; index < members.Count; index++) {
                if (results[index] is not { } result) {
                    continue;
                }

                foreach (var update in result.Updates) {
                    vertexError[update.Vertex] = Math.Max(vertexError[update.Vertex], update.Error);
                }

                var group = members[index];
                var childError = 0f;

                foreach (var child in group) {
                    childError = Math.Max(childError, current[child].Error);
                }

                var error = settings.SkipErrorMonotonicity ? result.Error : Math.Max(result.Error, childError);

                // Strictly above every child, never merely equal. A cluster is drawn when the
                // threshold is at or above its own error and below its parent's, so a parent that
                // matched its child would leave a band of thresholds where neither is drawn — a hole
                // that opens at exactly one distance, which is the worst kind to find.
                if (!settings.SkipErrorMonotonicity && error <= childError) {
                    error = MathF.BitIncrement(childError);
                }

                var groupIndex = groups.Count;

                groups.Add(
                    new() {
                        Children = [.. group.Select(child => current[child].Index)],
                        Level = level - 1,
                        Error = error
                    }
                );

                foreach (var child in group) {
                    var meshlet = meshlets[current[child].Index];
                    meshlets[current[child].Index] = meshlet with { Group = groupIndex, ParentError = error };
                }

                var produced = Split(result.Corners, level, groupIndex, error);
                groups[groupIndex] = groups[groupIndex] with { Parents = [.. produced.Select(parent => parent.Index)] };
                parents.AddRange(produced);
            }

            return parents;
        }

        /// <summary>Cuts a level's clusters into groups of about <see cref="MeshletBuildSettings.GroupSize" />.</summary>
        /// <param name="current">The level's clusters.</param>
        /// <returns>The groups, each a list of indices into <paramref name="current" />.</returns>
        /// <remarks>
        ///     The same partitioner as the triangles, one level up: nodes are clusters and an edge's
        ///     weight is how many mesh edges the two clusters share. Minimising that cut is
        ///     minimising the boundary each group has to lock, which is the only thing standing
        ///     between a group and the detail it is allowed to remove.
        /// </remarks>
        List<List<int>> GroupClusters(List<Cluster> current) {
            var pairs = new Dictionary<long, int>();
            var owners = new Dictionary<long, List<int>>();

            for (var cluster = 0; cluster < current.Count; cluster++) {
                foreach (var edge in Edges(current[cluster].Corners)) {
                    if (!owners.TryGetValue(edge, out var sharing)) {
                        owners[edge] = sharing = [];
                    }

                    if (!sharing.Contains(cluster)) {
                        sharing.Add(cluster);
                    }
                }
            }

            foreach (var sharing in owners.Values) {
                for (var i = 0; i < sharing.Count; i++) {
                    for (var j = i + 1; j < sharing.Count; j++) {
                        var key = Topology.Edge(sharing[i], sharing[j]);
                        pairs[key] = pairs.GetValueOrDefault(key) + 1;
                    }
                }
            }

            var partCount = Math.Max(1, (current.Count + settings.GroupSize - 1) / settings.GroupSize);
            var parts = GraphPartitioner.Partition(Topology.FromPairs(current.Count, pairs), partCount);
            var members = new List<List<int>>();

            for (var part = 0; part < partCount; part++) {
                members.Add([]);
            }

            for (var cluster = 0; cluster < current.Count; cluster++) {
                members[parts[cluster]].Add(cluster);
            }

            members.RemoveAll(group => group.Count == 0);

            return members;
        }

        /// <summary>Every welded edge a set of triangles uses.</summary>
        /// <param name="corners">Three source-vertex indices per triangle.</param>
        /// <returns>The edge keys, without duplicates.</returns>
        HashSet<long> Edges(int[] corners) {
            var edges = new HashSet<long>();

            for (var triangle = 0; triangle < corners.Length / 3; triangle++) {
                for (var corner = 0; corner < 3; corner++) {
                    var a = welded[corners[(triangle * 3) + corner]];
                    var b = welded[corners[(triangle * 3) + ((corner + 1) % 3)]];

                    if (a != b) {
                        edges.Add(Topology.Edge(a, b));
                    }
                }
            }

            return edges;
        }

        /// <summary>The triangles of every cluster in a group, end to end.</summary>
        /// <param name="current">The level's clusters.</param>
        /// <param name="group">Which of them are in the group.</param>
        /// <returns>Three source-vertex indices per triangle.</returns>
        static int[] Gather(List<Cluster> current, List<int> group) {
            var total = 0;

            foreach (var cluster in group) {
                total += current[cluster].Corners.Length;
            }

            var corners = new int[total];
            var cursor = 0;

            foreach (var cluster in group) {
                current[cluster].Corners.CopyTo(corners, cursor);
                cursor += current[cluster].Corners.Length;
            }

            return corners;
        }

        /// <summary>The edges a group's simplification may not move.</summary>
        /// <param name="current">The level's clusters.</param>
        /// <param name="group">Which of them are in the group.</param>
        /// <param name="corners">What <see cref="Gather" /> produced.</param>
        /// <returns>The locked edge keys.</returns>
        /// <remarks>
        ///     The group's outer boundary: the edges used by exactly one of its triangles, which are
        ///     exactly the edges shared with geometry outside the group. Under
        ///     <see cref="MeshletBuildSettings.LockClusterBoundaries" /> this locks each cluster's own
        ///     boundary instead, which is the sabotage the exit criterion asks for — and which locks
        ///     so much that no group ever reduces.
        /// </remarks>
        HashSet<long> LockedEdges(List<Cluster> current, List<int> group, int[] corners) {
            if (settings.UnlockGroupBoundaries) {
                return [];
            }

            if (!settings.LockClusterBoundaries) {
                var all = new int[corners.Length / 3];

                for (var triangle = 0; triangle < all.Length; triangle++) {
                    all[triangle] = triangle;
                }

                return Topology.BoundaryEdges(corners, welded, all);
            }

            var locked = new HashSet<long>();
            var offset = 0;

            foreach (var cluster in group) {
                var count = current[cluster].Corners.Length / 3;
                var own = new int[count];

                for (var triangle = 0; triangle < count; triangle++) {
                    own[triangle] = offset + triangle;
                }

                locked.UnionWith(Topology.BoundaryEdges(corners, welded, own));
                offset += count;
            }

            return locked;
        }

        /// <summary>Cuts a triangle list into clusters and emits them as meshlets.</summary>
        /// <param name="corners">Three source-vertex indices per triangle.</param>
        /// <param name="level">Which level they are at.</param>
        /// <param name="source">The group whose simplification produced them, or −1.</param>
        /// <param name="error">What they deviate from the original mesh by.</param>
        /// <returns>The clusters.</returns>
        List<Cluster> Split(int[] corners, int level, int source, float error) {
            var count = corners.Length / 3;
            var graph = Topology.BuildTriangleGraph(corners, welded);
            var partCount = Math.Max(1, (count + settings.MaxTriangles - 1) / settings.MaxTriangles);
            var parts = GraphPartitioner.Partition(graph, partCount);
            var byPart = new List<int>[partCount];

            for (var part = 0; part < partCount; part++) {
                byPart[part] = [];
            }

            for (var triangle = 0; triangle < count; triangle++) {
                byPart[parts[triangle]].Add(triangle);
            }

            var clusters = new List<Cluster>();

            foreach (var part in byPart) {
                if (part.Count == 0) {
                    continue;
                }

                foreach (var packed in Pack(Order(graph, parts, part), corners)) {
                    clusters.Add(Emit(packed, corners, level, source, error));
                }
            }

            return clusters;
        }

        /// <summary>Puts a part's triangles in an order a neighbour follows a neighbour in.</summary>
        /// <param name="graph">The triangle adjacency.</param>
        /// <param name="parts">Which part each triangle is in.</param>
        /// <param name="part">The part's triangles, ascending.</param>
        /// <returns>The same triangles, breadth-first from the lowest-numbered one.</returns>
        /// <remarks>
        ///     The packing below fills a cluster until it runs out of either budget, so the order it
        ///     sees decides how much of a vertex list two consecutive triangles share. Index order
        ///     within a part is arbitrary — the partitioner did not sort it — and a breadth-first walk
        ///     is what makes the split of an over-large part fall along the surface rather than across
        ///     it.
        /// </remarks>
        static List<int> Order(Graph graph, int[] parts, List<int> part) {
            var ordered = new List<int>(part.Count);
            var queued = new HashSet<int>();
            var queue = new Queue<int>();

            foreach (var seed in part) {
                if (!queued.Add(seed)) {
                    continue;
                }

                queue.Enqueue(seed);

                while (queue.TryDequeue(out var triangle)) {
                    ordered.Add(triangle);

                    for (var edge = graph.Offsets[triangle]; edge < graph.Offsets[triangle + 1]; edge++) {
                        var neighbour = graph.Neighbours[edge];

                        if (parts[neighbour] == parts[triangle] && queued.Add(neighbour)) {
                            queue.Enqueue(neighbour);
                        }
                    }
                }
            }

            return ordered;
        }

        /// <summary>Fills clusters from an ordered run of triangles until a budget runs out.</summary>
        /// <param name="ordered">The triangles, in the order they should be taken.</param>
        /// <param name="corners">Three source-vertex indices per triangle.</param>
        /// <returns>One list of triangles per cluster.</returns>
        List<List<int>> Pack(List<int> ordered, int[] corners) {
            var packed = new List<List<int>>();
            var current = new List<int>();
            var distinct = new HashSet<int>();

            foreach (var triangle in ordered) {
                var added = 0;

                for (var corner = 0; corner < 3; corner++) {
                    if (!distinct.Contains(corners[(triangle * 3) + corner])) {
                        added++;
                    }
                }

                if (current.Count > 0
                    && (current.Count >= settings.MaxTriangles || distinct.Count + added > settings.MaxVertices)) {
                    packed.Add(current);
                    current = [];
                    distinct.Clear();
                }

                current.Add(triangle);

                for (var corner = 0; corner < 3; corner++) {
                    distinct.Add(corners[(triangle * 3) + corner]);
                }
            }

            if (current.Count > 0) {
                packed.Add(current);
            }

            return packed;
        }

        /// <summary>Writes one cluster into the shared arrays and records what is true about it.</summary>
        /// <param name="cluster">Its triangles.</param>
        /// <param name="corners">Three source-vertex indices per triangle.</param>
        /// <param name="level">Which level it is at.</param>
        /// <param name="source">The group whose simplification produced it, or −1.</param>
        /// <param name="error">What it deviates from the original mesh by.</param>
        /// <returns>The cluster.</returns>
        Cluster Emit(List<int> cluster, int[] corners, int level, int source, float error) {
            var vertexOffset = vertices.Count;
            var triangleOffset = triangles.Count / 3;
            var local = new Dictionary<int, byte>();
            var bounds = BoundingBox.Empty;
            var axis = Vector3.Zero;
            var firstBone = int.MaxValue;
            var lastBone = int.MinValue;

            foreach (var triangle in cluster) {
                for (var corner = 0; corner < 3; corner++) {
                    var vertex = corners[(triangle * 3) + corner];

                    if (!local.TryGetValue(vertex, out var index)) {
                        index = (byte)local.Count;
                        local[vertex] = index;
                        vertices.Add(vertex);
                        bounds = BoundingBox.Merge(bounds, mesh.Positions[vertex]);
                        Bones(vertex, ref firstBone, ref lastBone);
                    }

                    triangles.Add(index);
                }

                axis += Normal(corners, triangle);
            }

            var cone = Vector3.Zero;
            var cosine = -1f;

            if (axis.LengthSquared() > 0) {
                cone = Vector3.Normalize(axis);
                cosine = 1f;

                foreach (var triangle in cluster) {
                    var normal = Normal(corners, triangle);

                    if (normal.LengthSquared() > 0) {
                        cosine = MathF.Min(cosine, Vector3.Dot(cone, Vector3.Normalize(normal)));
                    }
                }
            }

            meshlets.Add(
                new() {
                    VertexOffset = vertexOffset,
                    VertexCount = local.Count,
                    TriangleOffset = triangleOffset,
                    TriangleCount = cluster.Count,
                    Bounds = bounds,
                    ConeAxis = cone,
                    ConeCosine = cosine,
                    Error = error,
                    Level = level,
                    Source = source,
                    MaterialIndex = mesh.MaterialIndex,
                    FirstBone = firstBone <= lastBone ? firstBone : -1,
                    BoneCount = firstBone <= lastBone ? lastBone - firstBone + 1 : 0
                }
            );

            var own = new int[cluster.Count * 3];

            for (var index = 0; index < cluster.Count; index++) {
                for (var corner = 0; corner < 3; corner++) {
                    own[(index * 3) + corner] = corners[(cluster[index] * 3) + corner];
                }
            }

            return new(meshlets.Count - 1, own, error);
        }

        /// <summary>A triangle's normal, scaled by twice its area.</summary>
        /// <param name="corners">Three source-vertex indices per triangle.</param>
        /// <param name="triangle">Which triangle.</param>
        /// <returns>The unnormalised normal, so that summing weights each triangle by its area.</returns>
        Vector3 Normal(int[] corners, int triangle) {
            var a = mesh.Positions[corners[triangle * 3]];
            var b = mesh.Positions[corners[(triangle * 3) + 1]];
            var c = mesh.Positions[corners[(triangle * 3) + 2]];

            return Vector3.Cross(b - a, c - a);
        }

        /// <summary>Widens a bone range to cover one vertex's influences.</summary>
        /// <param name="vertex">The vertex.</param>
        /// <param name="first">The lowest bone so far.</param>
        /// <param name="last">The highest bone so far.</param>
        /// <remarks>
        ///     An influence with no weight is not an influence. Exporters pad to four and leave the
        ///     index at whatever was in the slot, so counting those would widen every cluster's range
        ///     to whatever bone happened to be there — usually zero, which is the root, which is every
        ///     bone's ancestor and therefore the widest possible answer.
        /// </remarks>
        void Bones(int vertex, ref int first, ref int last) {
            if (!mesh.IsSkinned) {
                return;
            }

            for (var influence = 0; influence < 4; influence++) {
                if (mesh.BoneWeights[(vertex * 4) + influence] <= 0) {
                    continue;
                }

                var bone = mesh.BoneIndices[(vertex * 4) + influence];
                first = Math.Min(first, bone);
                last = Math.Max(last, bone);
            }
        }
    }
}
