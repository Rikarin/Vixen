// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Cuts a graph into balanced parts with as few edges between them as possible.</summary>
/// <remarks>
///     <para>
///         Used twice and for the same reason both times: to cut a mesh's triangles into clusters,
///         and to cut those clusters into groups. What is being minimised is the <b>edge cut</b> —
///         the number of mesh edges with a triangle on either side of a part boundary — because that
///         is exactly the set of edges a group has to lock, and every locked edge is detail a level
///         of detail cannot remove.
///     </para>
///     <para>
///         <b>Recursive bisection, not multilevel k-way.</b> METIS proper coarsens the graph,
///         partitions the small one and projects the result back, which is what makes it fast on
///         graphs of millions of nodes. This bisects directly: a pseudo-peripheral pair of seeds, two
///         fronts grown against each other by how much of each candidate is already inside, and a
///         boundary refinement afterwards. On the sizes a build hands it — a partition per group per
///         level, most of them under ten thousand nodes — the difference is milliseconds, and this is
///         two hundred lines rather than several thousand.
///     </para>
///     <para>
///         <b>Every tie is broken by node index.</b> Two machines have to produce the same DAG for
///         one mesh, so nothing here may depend on the order a dictionary enumerates or a heap
///         happens to pop equal keys in.
///     </para>
/// </remarks>
static class GraphPartitioner {
    /// <summary>How many refinement sweeps a bisection makes before it settles for what it has.</summary>
    const int RefinementPasses = 4;

    /// <summary>Cuts a graph into parts.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="partCount">How many parts to cut it into.</param>
    /// <returns>One part index per node.</returns>
    public static int[] Partition(Graph graph, int partCount) {
        var parts = new int[graph.NodeCount];

        if (partCount <= 1 || graph.NodeCount == 0) {
            return parts;
        }

        var nodes = new int[graph.NodeCount];

        for (var node = 0; node < nodes.Length; node++) {
            nodes[node] = node;
        }

        new Bisector(graph).Recurse(nodes, 0, partCount, parts);

        return parts;
    }

    /// <summary>The scratch a recursive bisection needs, allocated once for the whole recursion.</summary>
    /// <remarks>
    ///     Per-node arrays sized to the whole graph rather than to each subset: a subset is named by
    ///     a generation stamp, so entering one costs a write per node of that subset rather than an
    ///     allocation proportional to the graph. With a recursion that visits every node once per
    ///     level, the difference is the whole of the partitioner's memory traffic.
    /// </remarks>
    sealed class Bisector(Graph graph) {
        readonly int[] stamp = new int[graph.NodeCount];
        readonly int[] side = new int[graph.NodeCount];
        readonly int[] distance = new int[graph.NodeCount];
        readonly int[] visited = new int[graph.NodeCount];
        int generation;
        int walk;

        /// <summary>Splits a subset in two until it has one part left to fill, then fills it.</summary>
        /// <param name="nodes">The subset, ascending.</param>
        /// <param name="firstPart">The lowest part index this subset may be assigned.</param>
        /// <param name="partCount">How many parts the subset is to be cut into.</param>
        /// <param name="parts">Where to write the answer.</param>
        public void Recurse(int[] nodes, int firstPart, int partCount, int[] parts) {
            if (partCount <= 1 || nodes.Length <= 1) {
                foreach (var node in nodes) {
                    parts[node] = firstPart;
                }

                return;
            }

            // Split the parts as evenly as the count allows and give each half a share of the nodes
            // in proportion — so seven parts over a thousand nodes is three parts over about four
            // hundred and three, and never four parts over a hundred.
            var leftParts = partCount / 2;
            var leftTarget = Math.Clamp((int)Math.Round((double)nodes.Length * leftParts / partCount), 1, nodes.Length - 1);

            var (left, right) = Bisect(nodes, leftTarget);

            Recurse(left, firstPart, leftParts, parts);
            Recurse(right, firstPart + leftParts, partCount - leftParts, parts);
        }

        /// <summary>Cuts one subset in two.</summary>
        /// <param name="nodes">The subset, ascending.</param>
        /// <param name="leftTarget">How many nodes the left side should end up with.</param>
        /// <returns>The two sides, each ascending.</returns>
        (int[] Left, int[] Right) Bisect(int[] nodes, int leftTarget) {
            generation++;

            foreach (var node in nodes) {
                stamp[node] = generation;
                side[node] = -1;
            }

            // A pseudo-peripheral pair: the farthest node from an arbitrary one, and the farthest
            // from that. Two seeds at opposite ends of the subset is what makes the two fronts meet
            // in the middle; two seeds beside each other makes one front engulf the graph and the
            // other own a pocket, which is a balanced partition with a terrible cut.
            var first = Farthest(nodes[0]);
            var second = Farthest(first);

            if (second == first) {
                second = nodes[0] == first ? nodes[1] : nodes[0];
            }

            var rightTarget = nodes.Length - leftTarget;
            var counts = new int[2];
            var queues = new PriorityQueue<int, (int Cost, int Node)>[] { new(), new() };

            Assign(first, 0, counts, queues);
            Assign(second, 1, counts, queues);

            var scan = 0;

            while (counts[0] + counts[1] < nodes.Length) {
                // Whichever side is further behind its share, so the two fronts arrive at the middle
                // together rather than one of them finishing and the other inheriting the remainder.
                var growing = counts[0] >= leftTarget ? 1
                    : counts[1] >= rightTarget ? 0
                    : counts[0] * (long)rightTarget <= counts[1] * (long)leftTarget ? 0
                    : 1;

                var chosen = -1;

                while (queues[growing].TryDequeue(out var candidate, out _)) {
                    if (side[candidate] < 0) {
                        chosen = candidate;

                        break;
                    }
                }

                if (chosen < 0) {
                    // The side's front has run dry, which means the subset is disconnected. The
                    // lowest unassigned node seeds a new front rather than the whole remainder
                    // falling to the other side — which would be a bisection that ignored its target.
                    while (scan < nodes.Length && side[nodes[scan]] >= 0) {
                        scan++;
                    }

                    if (scan >= nodes.Length) {
                        break;
                    }

                    chosen = nodes[scan];
                }

                Assign(chosen, growing, counts, queues);
            }

            Refine(nodes);

            var left = new int[counts[0]];
            var right = new int[counts[1]];
            var leftCursor = 0;
            var rightCursor = 0;

            foreach (var node in nodes) {
                if (side[node] == 0) {
                    left[leftCursor++] = node;
                } else {
                    right[rightCursor++] = node;
                }
            }

            return (left, right);
        }

        /// <summary>Puts a node on a side and offers its neighbours to that side's front.</summary>
        /// <param name="node">The node.</param>
        /// <param name="which">Which side.</param>
        /// <param name="counts">How many each side holds.</param>
        /// <param name="queues">The two fronts.</param>
        /// <remarks>
        ///     A neighbour is pushed again every time one of <em>its</em> neighbours joins the side,
        ///     with its improved attachment, and the stale entry is skipped when it surfaces. That is
        ///     cheaper than finding and updating the old entry, and it is what makes the front always
        ///     hand back the best candidate it has rather than the best one it had.
        /// </remarks>
        void Assign(int node, int which, int[] counts, PriorityQueue<int, (int Cost, int Node)>[] queues) {
            side[node] = which;
            counts[which]++;

            for (var edge = graph.Offsets[node]; edge < graph.Offsets[node + 1]; edge++) {
                var neighbour = graph.Neighbours[edge];

                if (stamp[neighbour] != generation || side[neighbour] >= 0) {
                    continue;
                }

                queues[which].Enqueue(neighbour, (-Attachment(neighbour, which), neighbour));
            }
        }

        /// <summary>How strongly a node is already attached to a side.</summary>
        /// <param name="node">The node.</param>
        /// <param name="which">The side.</param>
        /// <returns>The total weight of its edges to nodes already on that side.</returns>
        int Attachment(int node, int which) {
            var total = 0;

            for (var edge = graph.Offsets[node]; edge < graph.Offsets[node + 1]; edge++) {
                var neighbour = graph.Neighbours[edge];

                if (stamp[neighbour] == generation && side[neighbour] == which) {
                    total += graph.Weights[edge];
                }
            }

            return total;
        }

        /// <summary>Swaps pairs of misplaced nodes across the cut while that makes it smaller.</summary>
        /// <param name="nodes">The subset, ascending.</param>
        /// <remarks>
        ///     <para>
        ///         Kernighan–Lin without the lookahead: a node whose edges to the far side outweigh
        ///         its edges to its own wants to move, and moving one from each side at once takes
        ///         the ragged edge off where the two fronts met. The lookahead — accepting a bad move
        ///         to reach a good pair of them — is what escapes a local minimum, and it costs a
        ///         factor this does not need at these sizes.
        ///     </para>
        ///     <para>
        ///         <b>Only in pairs, so the balance growth produced is exact afterwards.</b> A single
        ///         move is worth a little cut and costs one node of balance, and that debt compounds:
        ///         a partition into forty parts is five bisections deep, so five single moves put a
        ///         part five triangles over its budget — and a cluster budget that is exceeded is a
        ///         cluster split into a full one and a nearly empty one. Paying for the cut in whole
        ///         swaps keeps every part exactly the size it was asked to be.
        ///     </para>
        /// </remarks>
        void Refine(int[] nodes) {
            for (var pass = 0; pass < RefinementPasses; pass++) {
                var wanting = new List<int>[] { [], [] };

                foreach (var node in nodes) {
                    if (Attachment(node, 1 - side[node]) > Attachment(node, side[node])) {
                        wanting[side[node]].Add(node);
                    }
                }

                var cursors = new[] { 0, 0 };
                var moved = 0;

                while (true) {
                    var left = NextWanting(wanting[0], ref cursors[0], 0);

                    if (left < 0) {
                        break;
                    }

                    side[left] = 1;

                    var right = NextWanting(wanting[1], ref cursors[1], 1);

                    if (right < 0) {
                        // Nothing to trade with. The move goes back rather than standing, because a
                        // side that is one node over its target is a part that is one node over its
                        // budget somewhere below.
                        side[left] = 0;

                        break;
                    }

                    side[right] = 0;
                    moved += 2;
                }

                if (moved == 0) {
                    return;
                }
            }
        }

        /// <summary>The next node on a side that would still rather be on the other one.</summary>
        /// <param name="candidates">The nodes that wanted to move when the pass started.</param>
        /// <param name="cursor">How far through them this side has got.</param>
        /// <param name="from">The side they are on.</param>
        /// <returns>The node, or −1 if none is left.</returns>
        /// <remarks>
        ///     The want is re-asked rather than trusted: a swap earlier in the pass may have been a
        ///     node's only reason to leave, and moving it afterwards would undo the gain that was
        ///     just bought.
        /// </remarks>
        int NextWanting(List<int> candidates, ref int cursor, int from) {
            while (cursor < candidates.Count) {
                var node = candidates[cursor++];

                if (side[node] == from && Attachment(node, 1 - from) > Attachment(node, from)) {
                    return node;
                }
            }

            return -1;
        }

        /// <summary>The node of the current subset furthest from a given one.</summary>
        /// <param name="from">Where to start.</param>
        /// <returns>The farthest node, lowest index first among equals.</returns>
        /// <remarks>
        ///     A breadth-first walk that stays inside the subset. On a disconnected subset it returns
        ///     something from the starting node's component, which is the right answer for a seed:
        ///     the other components get their own seeds when a front runs dry.
        /// </remarks>
        int Farthest(int from) {
            var queue = new Queue<int>();
            var mark = ++walk;

            queue.Enqueue(from);
            distance[from] = 0;
            visited[from] = mark;

            var best = from;
            var bestDistance = 0;

            while (queue.TryDequeue(out var node)) {
                if (distance[node] > bestDistance || (distance[node] == bestDistance && node < best)) {
                    best = node;
                    bestDistance = distance[node];
                }

                for (var edge = graph.Offsets[node]; edge < graph.Offsets[node + 1]; edge++) {
                    var neighbour = graph.Neighbours[edge];

                    if (stamp[neighbour] != generation || visited[neighbour] == mark) {
                        continue;
                    }

                    visited[neighbour] = mark;
                    distance[neighbour] = distance[node] + 1;
                    queue.Enqueue(neighbour);
                }
            }

            return best;
        }
    }
}
