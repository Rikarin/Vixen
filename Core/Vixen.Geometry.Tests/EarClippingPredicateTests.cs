// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>That a triangulation of a near-degenerate face still covers the face.</summary>
/// <remarks>
///     <para>
///         <b>The scale-invariance half of this belongs to <c>EditMeshTests</c>, which asserts every
///         one of <see cref="MeshShapes" />' twelve primitives triangulates to identical indices from
///         1e-6 to 1e+6.</b> This is the other question those tests do not ask: whether the indices
///         that come out still <i>partition the face</i> when its corners are very nearly collinear.
///     </para>
///     <para>
///         ⚠ <b>Index equality and area conservation are different failures and a triangulator can
///         pass one while failing the other.</b> Ear clipping that cuts a reflex corner emits
///         overlapping triangles — stably, and identically at every scale — so a test comparing two
///         scales' indices sees nothing wrong. What catches it is summing what was emitted and
///         asking whether it is the face.
///     </para>
/// </remarks>
public class EarClippingPredicateTests {
    /// <summary>
    ///     A five-sided face that is a rectangle with one edge subdivided a hair off the line — the
    ///     shape a boolean produces where a cut passes almost exactly through a vertex, and the one
    ///     where the sign of a corner's turn is smaller than the arithmetic under it.
    /// </summary>
    [Fact]
    public void ANearlyCollinearNgonTriangulatesIntoItsOwnCornersAndCoversItsOwnArea() {
        var mesh = new EditMesh();

        var corners = new[] {
            mesh.AddPosition(new(0f, 0f, 0f)),
            mesh.AddPosition(new(1f, 0f, 0f)),
            mesh.AddPosition(new(1f, 1f, 0f)),
            mesh.AddPosition(new(0.5f, 1.0000001f, 0f)),
            mesh.AddPosition(new(0f, 1f, 0f))
        };

        mesh.AddFace(corners);

        var indices = mesh.Triangulate();

        // Three triangles for five corners, and every index one the face owns — a fan fallback that
        // silently emitted a different count would be caught here rather than downstream.
        Assert.Equal(9, indices.Length);
        Assert.All(indices, index => Assert.Contains(index, corners));

        var area = 0f;

        for (var triangle = 0; triangle < indices.Length / 3; triangle++) {
            var p = mesh.Positions[indices[triangle * 3]];
            var q = mesh.Positions[indices[(triangle * 3) + 1]];
            var r = mesh.Positions[indices[(triangle * 3) + 2]];

            area += Vector3.Cross(q - p, r - p).Length() * 0.5f;
        }

        // ⚠ The sum, not each triangle: three triangles that overlap sum to more than the face, and
        // three that leave a gap sum to less. Only a partition sums to exactly it.
        Assert.InRange(area, 0.99f, 1.01f);
    }
}
