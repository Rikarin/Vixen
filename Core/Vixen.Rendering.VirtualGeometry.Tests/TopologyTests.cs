// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Welding, adjacency and boundaries — the facts everything above them is derived from.</summary>
public class TopologyTests {
    [Fact]
    public void VerticesAtOnePointWeldToTheLowestOfThem() {
        Vector3[] positions = [new(1, 2, 3), new(0, 0, 0), new(1, 2, 3), new(1, 2, 3.0001f)];

        Assert.Equal([0, 1, 0, 3], Topology.Weld(positions));
    }

    [Fact]
    public void ACopyIsNotASeamAndADifferentUvIs() {
        var mesh = new MeshletBuildInput {
            Positions = [new(0, 0, 0), new(0, 0, 0), new(1, 0, 0), new(1, 0, 0)],
            TexCoords = [new(0, 0), new(0, 0), new(0, 0), new(1, 0)]
        };

        var seam = Topology.FindSeams(mesh, Topology.Weld(mesh.Positions));

        Assert.False(seam[0]);
        Assert.True(seam[2]);
    }

    [Fact]
    public void TwoTrianglesSharingAnEdgeAreNeighboursEvenWhenTheirVerticesAreSplit() {
        // The case that matters: an exporter has split the shared edge because the UVs differ across
        // it, so the two triangles have no index in common at all. An adjacency built on indices
        // would call this two separate surfaces and cluster them apart, and every group boundary
        // would then run along every seam in the model.
        Vector3[] positions = [
            new(0, 0, 0), new(1, 0, 0), new(0, 0, 1),
            new(1, 0, 0), new(0, 0, 1), new(1, 0, 1)
        ];

        int[] indices = [0, 1, 2, 3, 5, 4];

        var graph = Topology.BuildTriangleGraph(indices, Topology.Weld(positions));

        Assert.Equal(2, graph.NodeCount);
        Assert.Equal([1], graph.Neighbours[graph.Offsets[0]..graph.Offsets[1]]);
    }

    [Fact]
    public void TheBoundaryIsWhatOnlyOneTriangleUses() {
        var grid = Shapes.Grid(4);
        var welded = Topology.Weld(grid.Positions);
        var all = Enumerable.Range(0, grid.TriangleCount).ToArray();

        // Four sides of four cells each, and the diagonal of a quad is used twice and is not one.
        Assert.Equal(16, Topology.BoundaryEdges(grid.Indices, welded, all).Count);
    }

    [Fact]
    public void AClosedSurfaceHasNoBoundary() {
        var sphere = Shapes.Sphere(2);
        var welded = Topology.Weld(sphere.Positions);
        var all = Enumerable.Range(0, sphere.TriangleCount).ToArray();

        Assert.Empty(Topology.BoundaryEdges(sphere.Indices, welded, all));
    }

    [Fact]
    public void RowsComeOutAscendingWhateverOrderThePairsArrivedIn() {
        var forwards = Topology.FromPairs(4, new() { [Topology.Edge(0, 3)] = 1, [Topology.Edge(0, 1)] = 2 });
        var backwards = Topology.FromPairs(4, new() { [Topology.Edge(1, 0)] = 2, [Topology.Edge(3, 0)] = 1 });

        Assert.Equal([1, 3], forwards.Neighbours[forwards.Offsets[0]..forwards.Offsets[1]]);
        Assert.Equal([2, 1], forwards.Weights[forwards.Offsets[0]..forwards.Offsets[1]]);
        Assert.Equal(forwards.Neighbours, backwards.Neighbours);
    }
}
