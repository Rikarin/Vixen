// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Charting;

/// <summary>The built-in decomposition: an approximate convex split whose boundary is a walk on the graph.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D3's default — <i>"approximate convex decomposition over the dual graph,
///         weighted by dihedral concavity and the shape diameter function, which is the same family
///         Seamster drew on to find inconspicuous cuts"</i> — and § D4's rule that the cut it produces is
///         a set of <b>existing mesh edges</b> found by search under an edge cost.
///     </para>
///     <para>
///         <b>Two seeds, two Dijkstras, and the cut is where the frontiers meet.</b> A good seam is a
///         <i>barrier</i> in <see cref="SeamGraph.Barrier" />, so a growth that pays to cross creases,
///         hard edges, material boundaries and occluded regions stalls at exactly the places a seam
///         belongs — and the boundary between the two territories is then a set of shared edges, with no
///         curve ever placed in space and nothing to snap. That is the whole of MeshTailor's
///         representational argument, applied to a hand-written tracer.
///     </para>
///     <para>
///         ⚠ <b>A partition of the <i>faces</i> rather than a path across the surface, and that is what
///         makes it total.</b> A cut described as a path has to answer a different topological question
///         for every input — a disk wants a boundary-to-boundary path, an annulus wants a path joining
///         its two loops, a closed surface wants a separating loop, a handle wants two — and each of
///         those is a case that can fail. Splitting the face set can fail at nothing: any partition of a
///         closed sphere into two dual-connected halves is two disks, and docs/plan/42's exit criterion 2
///         asks for no exceptions and no hangs before it asks for anything else.
///     </para>
///     <para>
///         ⚠ <b>Four candidate splits are proposed and the seven-term cost picks one.</b> The growth
///         metric deliberately leaves <see cref="SeamCost.Length" /> out — an edge's length says nothing
///         about whether it is a wall — so length is paid for here, where whole cuts are compared by
///         their summed <see cref="SeamGraph.Cut" />. That is what makes <see cref="SeamCost.Length" />
///         the term the other six are traded against rather than a number in a table.
///     </para>
/// </remarks>
static class Bisection {
    /// <summary>Splits a region in two, or returns fewer parts when it cannot be split at all.</summary>
    /// <param name="graph">The mesh graph.</param>
    /// <param name="faces">The region, ascending.</param>
    /// <returns>The parts, each ascending, ordered by their lowest face index.</returns>
    /// <remarks>
    ///     ⚠ <b>A region that falls into dual-connected pieces is returned as those pieces, before any
    ///     geometry is looked at.</b> That is the answer to three of <c>ChartRefusal</c>'s reasons at
    ///     once — a disconnected chart, a bowtie pinch, and a chart reached through a non-manifold edge
    ///     — because none of the three is a shape problem and a bisection would be answering the wrong
    ///     question about all of them.
    /// </remarks>
    public static List<int[]> Split(SeamGraph graph, int[] faces) {
        if (faces.Length < 2) {
            return [faces];
        }

        var slot = Slots(graph, faces);
        var components = Components(graph, faces, slot);

        if (components.Count > 1) {
            Release(slot, faces);

            return components;
        }

        var best = default(List<int[]>);
        var bestCost = double.PositiveInfinity;

        foreach (var (from, to) in Seeds(graph, faces, slot)) {
            var parts = Grow(graph, faces, slot, from, to);

            if (parts is null) {
                continue;
            }

            var cost = Score(graph, faces, slot, parts);

            // Strictly better, so the first candidate wins a tie and the candidate order — which is a
            // function of the mesh — is what breaks it.
            if (cost < bestCost) {
                bestCost = cost;
                best = parts;
            }
        }

        Release(slot, faces);

        return best ?? [faces];
    }

    /// <summary>Where each face of the region sits in it, indexed by face, or <c>-1</c> outside it.</summary>
    static int[] Slots(SeamGraph graph, int[] faces) {
        var slot = new int[graph.FaceCount];

        Array.Fill(slot, -1);

        for (var index = 0; index < faces.Length; index++) {
            slot[faces[index]] = index;
        }

        return slot;
    }

    static void Release(int[] slot, int[] faces) {
        foreach (var face in faces) {
            slot[face] = -1;
        }
    }

    /// <summary>The region's dual-connected pieces, each ascending.</summary>
    static List<int[]> Components(SeamGraph graph, int[] faces, int[] slot) {
        var seen = new bool[faces.Length];
        var components = new List<int[]>();
        var stack = new Stack<int>();

        for (var start = 0; start < faces.Length; start++) {
            if (seen[start]) {
                continue;
            }

            var found = new List<int>();

            stack.Push(start);
            seen[start] = true;

            while (stack.Count > 0) {
                var index = stack.Pop();

                found.Add(faces[index]);

                var face = faces[index];

                for (var link = graph.LinkStart[face]; link < graph.LinkStart[face + 1]; link++) {
                    var neighbour = slot[graph.Links[link]];

                    if (neighbour >= 0 && !seen[neighbour]) {
                        seen[neighbour] = true;
                        stack.Push(neighbour);
                    }
                }
            }

            found.Sort();
            components.Add([.. found]);
        }

        return components;
    }

    /// <summary>The candidate seed pairs, in the order the tie-break resolves them.</summary>
    /// <remarks>
    ///     <para>
    ///         The dual-graph diameter first, because it is the only one of the four that knows nothing
    ///         about the world axes and is therefore the one that survives a rotated model. Then the
    ///         extremes along each axis, which are what actually finds the split a person would make on
    ///         an axis-aligned asset — a character's arm from its torso, a bracket's two legs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every tie is broken on the centroid and only then on the index.</b>
    ///         <c>Pins.Choose</c> records why: an index tie-break is a determinism hole that passes every
    ///         test run on the mesh it was written against, because a renumbering is not a change to the
    ///         surface and an importer, a weld and a boolean all do one routinely.
    ///     </para>
    /// </remarks>
    static List<(int From, int To)> Seeds(SeamGraph graph, int[] faces, int[] slot) {
        var seeds = new List<(int, int)>();
        var start = Lowest(graph, faces);
        var first = Farthest(graph, faces, slot, start);
        var second = Farthest(graph, faces, slot, first);

        if (first != second) {
            seeds.Add((Math.Min(first, second), Math.Max(first, second)));
        }

        for (var axis = 0; axis < 3; axis++) {
            var low = faces[0];
            var high = faces[0];

            foreach (var face in faces) {
                if (Before(graph, face, low, axis)) {
                    low = face;
                }

                if (Before(graph, high, face, axis)) {
                    high = face;
                }
            }

            if (low != high) {
                seeds.Add((Math.Min(low, high), Math.Max(low, high)));
            }
        }

        return seeds;
    }

    /// <summary>Whether one face's centroid comes before another's along an axis, ties on the other two.</summary>
    static bool Before(SeamGraph graph, int left, int right, int axis) {
        var a = graph.Centroids[left];
        var b = graph.Centroids[right];

        var one = axis switch { 0 => a.X, 1 => a.Y, _ => a.Z };
        var other = axis switch { 0 => b.X, 1 => b.Y, _ => b.Z };

        if (one != other) {
            return one < other;
        }

        if (a.X != b.X) {
            return a.X < b.X;
        }

        return a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
    }

    /// <summary>The face whose centroid is lexicographically smallest — a seed the numbering cannot move.</summary>
    static int Lowest(SeamGraph graph, int[] faces) {
        var best = faces[0];

        foreach (var face in faces) {
            if (Before(graph, face, best, 0)) {
                best = face;
            }
        }

        return best;
    }

    /// <summary>The face farthest from one, under the barrier metric.</summary>
    static int Farthest(SeamGraph graph, int[] faces, int[] slot, int from) {
        var distance = Distances(graph, faces, slot, from);
        var best = from;
        var furthest = -1d;

        for (var index = 0; index < faces.Length; index++) {
            var reach = distance[index];

            if (double.IsPositiveInfinity(reach)) {
                continue;
            }

            if (reach > furthest || (reach == furthest && Before(graph, faces[index], best, 0))) {
                furthest = reach;
                best = faces[index];
            }
        }

        return best;
    }

    /// <summary>Dijkstra over the region's dual subgraph under <see cref="SeamGraph.Barrier" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the distances are used, never the paths, and that is what makes the queue's tie
    ///     order irrelevant.</b> A shortest distance is a well-defined function of the graph however the
    ///     search reached it; a shortest <i>path</i> is not, when two of them tie. Taking the first and
    ///     comparing distances afterwards is what keeps this deterministic without a comparer whose
    ///     ordering would then itself be part of the contract.
    /// </remarks>
    static double[] Distances(SeamGraph graph, int[] faces, int[] slot, int from) {
        var distance = new double[faces.Length];

        Array.Fill(distance, double.PositiveInfinity);

        var source = slot[from];

        if (source < 0) {
            return distance;
        }

        distance[source] = 0d;

        var queue = new PriorityQueue<int, double>();

        queue.Enqueue(source, 0d);

        while (queue.TryDequeue(out var index, out var reached)) {
            if (reached > distance[index]) {
                continue;
            }

            var face = faces[index];

            for (var link = graph.LinkStart[face]; link < graph.LinkStart[face + 1]; link++) {
                var neighbour = slot[graph.Links[link]];

                if (neighbour < 0) {
                    continue;
                }

                var step = reached + graph.Barrier[graph.LinkEdges[link]];

                if (step < distance[neighbour]) {
                    distance[neighbour] = step;
                    queue.Enqueue(neighbour, step);
                }
            }
        }

        return distance;
    }

    /// <summary>Grows two territories from two seeds and returns them, or null when one came out empty.</summary>
    static List<int[]>? Grow(SeamGraph graph, int[] faces, int[] slot, int from, int to) {
        var here = Distances(graph, faces, slot, from);
        var there = Distances(graph, faces, slot, to);

        var mine = new List<int>();
        var theirs = new List<int>();

        for (var index = 0; index < faces.Length; index++) {
            var a = here[index];
            var b = there[index];

            // Equal reach goes to the lower-numbered seed, so a face exactly between the two is not
            // decided by which Dijkstra happened to be run first. An unreachable face — which cannot
            // happen on a connected region and is cheap to be right about — goes with the first seed.
            if (double.IsPositiveInfinity(a) && double.IsPositiveInfinity(b)) {
                mine.Add(faces[index]);
            } else if (a < b || (a == b && from < to)) {
                mine.Add(faces[index]);
            } else {
                theirs.Add(faces[index]);
            }
        }

        if (mine.Count == 0 || theirs.Count == 0) {
            return null;
        }

        return mine[0] < theirs[0] ? [[.. mine], [.. theirs]] : [[.. theirs], [.. mine]];
    }

    /// <summary>What a proposed split would cost to cut, summed over the edges it crosses.</summary>
    static double Score(SeamGraph graph, int[] faces, int[] slot, List<int[]> parts) {
        var part = new int[faces.Length];

        for (var index = 0; index < parts.Count; index++) {
            foreach (var face in parts[index]) {
                part[slot[face]] = index;
            }
        }

        var cost = 0d;

        foreach (var face in faces) {
            for (var link = graph.LinkStart[face]; link < graph.LinkStart[face + 1]; link++) {
                var neighbour = slot[graph.Links[link]];

                // Each crossed edge is visited from both sides, so only the lower face pays for it.
                if (neighbour >= 0 && graph.Links[link] > face && part[slot[face]] != part[neighbour]) {
                    cost += graph.Cut[graph.LinkEdges[link]];
                }
            }
        }

        return cost;
    }
}
