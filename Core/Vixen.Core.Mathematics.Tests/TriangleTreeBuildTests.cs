// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>The tree a mesh builds is one tree, whatever the build does to get there.</summary>
/// <remarks>
///     ⚠ <b>The order is the whole tree.</b> Every split takes half of its range, so the triangle order alone says
///     which triangles each node holds, and with them its box. The reference here is the build as it was first
///     written, a full sort of each range by centroid with the id as the tie-break, leaves keeping their parent's
///     order. A faster build that put one triangle elsewhere would change which of two equal hits a ray reports, and
///     every bake and brush reading the tree with it.
/// </remarks>
public class TriangleTreeBuildTests {
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 2)]
    [InlineData(37, 3)]
    [InlineData(1000, 4)]
    [InlineData(20_000, 5)]
    public void A_tree_orders_its_triangles_as_a_full_sort_of_every_range_would(int triangles, int seed) {
        var random = new Random(seed);
        var vertices = new Vector3[triangles * 3];

        for (var at = 0; at < vertices.Length; at++) {
            // A coarse grid, so that many centroids tie and the id has to break them.
            vertices[at] = new Vector3(random.Next(8), random.Next(8), random.Next(3)) * 0.5f;
        }

        var indices = Enumerable.Range(0, vertices.Length).ToArray();

        var tree = new TriangleTree(vertices, indices);

        Assert.Equal(Reference(vertices, indices), tree.Order.ToArray());
    }

    [Fact]
    public void A_tree_of_one_point_repeated_keeps_the_ids_in_order() {
        var vertices = Enumerable.Repeat(new Vector3(1f, 2f, 3f), 300).ToArray();
        var indices = Enumerable.Range(0, vertices.Length).ToArray();

        Assert.Equal(Reference(vertices, indices), new TriangleTree(vertices, indices).Order.ToArray());
    }

    /// <summary>The build as first written: each range sorted whole by centroid along its box's longest axis.</summary>
    static int[] Reference(Vector3[] vertices, int[] indices) {
        var count = indices.Length / 3;
        var order = Enumerable.Range(0, count).ToArray();
        var centroids = new Vector3[count];
        var boxes = new BoundingBox[count];

        for (var triangle = 0; triangle < count; triangle++) {
            var (a, b, c) = (vertices[indices[triangle * 3]], vertices[indices[triangle * 3 + 1]],
                vertices[indices[triangle * 3 + 2]]);
            centroids[triangle] = (a + b + c) / 3f;
            boxes[triangle] = new(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
        }

        Build(0, count, 0);

        return order;

        void Build(int start, int length, int depth) {
            if (length <= 4 || depth >= 62) {
                return;
            }

            var bounds = BoundingBox.Empty;

            for (var slot = start; slot < start + length; slot++) {
                bounds = BoundingBox.Merge(bounds, boxes[order[slot]]);
            }

            var size = bounds.Size;
            var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            var ids = order.AsSpan(start, length).ToArray();
            var keys = ids.Select(id => axis == 0 ? centroids[id].X : axis == 1 ? centroids[id].Y : centroids[id].Z)
                .ToArray();
            var sorted = ids.Zip(keys).OrderBy(static pair => pair.Second).ThenBy(static pair => pair.First);
            sorted.Select(static pair => pair.First).ToArray().CopyTo(order, start);

            Build(start, length / 2, depth + 1);
            Build(start + length / 2, length - length / 2, depth + 1);
        }
    }
}
