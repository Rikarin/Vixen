// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>That the partition is balanced, local, and the same twice.</summary>
public class GraphPartitionerTests {
    [Fact]
    public void EveryNodeLandsInExactlyOnePart() {
        var graph = Grid(16);
        var parts = GraphPartitioner.Partition(graph, 8);

        Assert.Equal(graph.NodeCount, parts.Length);
        Assert.All(parts, part => Assert.InRange(part, 0, 7));
    }

    [Fact]
    public void ThePartsAreTheSameSize() {
        var graph = Grid(16);
        var parts = GraphPartitioner.Partition(graph, 8);
        var sizes = parts.GroupBy(part => part).Select(group => group.Count()).ToList();

        // Exactly, not approximately. A part one node over its budget is a cluster one triangle over
        // its budget, which is a full cluster and a cluster holding one triangle.
        Assert.All(sizes, size => Assert.Equal(graph.NodeCount / 8, size));
    }

    [Fact]
    public void ThePartitionCutsFarLessThanASplitByIndexWould() {
        var graph = Grid(24);
        var partitioned = Cut(graph, GraphPartitioner.Partition(graph, 16));

        var byIndex = new int[graph.NodeCount];

        for (var node = 0; node < graph.NodeCount; node++) {
            byIndex[node] = node * 16 / graph.NodeCount;
        }

        // The whole reason a partitioner exists rather than a loop: a cluster is meant to be a patch
        // of surface, and cutting the index buffer into equal runs gives sixteen strips.
        //
        // A third better rather than the two thirds an optimal partition of a square lattice would
        // manage. Growing two fronts from opposite ends cuts a lattice diagonally where a straight
        // line would do, and the diagonal compounds down the recursion; closing that gap is what the
        // multilevel coarsening this deliberately does not implement is for. The number is stated
        // here so that a change to it is a decision rather than a surprise.
        Assert.True(
            partitioned < Cut(graph, byIndex) * 0.8,
            $"The partition cut {partitioned} edges against an index split's {Cut(graph, byIndex)}."
        );
    }

    [Fact]
    public void ADisconnectedGraphStillFillsBothSides() {
        // Two squares that share nothing. A bisection that grew one front until it ran dry and gave
        // the rest to the other side would answer with one part of four and one of nothing.
        var graph = Topology.FromPairs(
            8,
            new() {
                [Topology.Edge(0, 1)] = 1, [Topology.Edge(1, 2)] = 1, [Topology.Edge(2, 3)] = 1, [Topology.Edge(3, 0)] = 1,
                [Topology.Edge(4, 5)] = 1, [Topology.Edge(5, 6)] = 1, [Topology.Edge(6, 7)] = 1, [Topology.Edge(7, 4)] = 1
            }
        );

        var parts = GraphPartitioner.Partition(graph, 2);

        Assert.Equal(4, parts.Count(part => part == 0));
        Assert.Equal(4, parts.Count(part => part == 1));
    }

    [Fact]
    public void APartitionOfOneIsEverything() =>
        Assert.All(GraphPartitioner.Partition(Grid(4), 1), part => Assert.Equal(0, part));

    [Fact]
    public void TwoPartitionsOfOneGraphAgree() {
        var graph = Grid(20);

        Assert.Equal(GraphPartitioner.Partition(graph, 13), GraphPartitioner.Partition(graph, 13));
    }

    /// <summary>How many edges have their ends in different parts.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="parts">Which part each node is in.</param>
    /// <returns>The total weight cut.</returns>
    static int Cut(Graph graph, int[] parts) {
        var total = 0;

        for (var node = 0; node < graph.NodeCount; node++) {
            for (var edge = graph.Offsets[node]; edge < graph.Offsets[node + 1]; edge++) {
                if (parts[graph.Neighbours[edge]] != parts[node]) {
                    total += graph.Weights[edge];
                }
            }
        }

        return total / 2;
    }

    /// <summary>A square lattice, which is the shape a triangle adjacency graph roughly has.</summary>
    /// <param name="side">How many nodes along each side.</param>
    /// <returns>The graph.</returns>
    static Graph Grid(int side) {
        var pairs = new Dictionary<long, int>();

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var node = (y * side) + x;

                if (x + 1 < side) {
                    pairs[Topology.Edge(node, node + 1)] = 1;
                }

                if (y + 1 < side) {
                    pairs[Topology.Edge(node, node + side)] = 1;
                }
            }
        }

        return Topology.FromPairs(side * side, pairs);
    }
}
