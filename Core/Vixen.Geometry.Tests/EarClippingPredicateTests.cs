// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>What ear clipping decides on a corner whose area is smaller than the arithmetic under it.</summary>
/// <remarks>
///     <para>
///         <b><see cref="EditMesh.Triangulate" /> asks three sign questions per candidate ear</b> — is
///         this corner convex, and does any other corner lie inside the triangle it would cut. Each is
///         the sign of a two-dimensional cross product, and each was a <c>float</c> expression compared
///         against exact zero.
///     </para>
///     <para>
///         ⚠ <b>That is the one arithmetic this repository has already decided not to trust.</b>
///         <c>MeshBoolean</c> classifies a point against a plane through
///         <see cref="ExactPredicates.Orient3D" /> rather than a tolerance, and doc 24 § D5 is the
///         argument. The two-dimensional case had no predicate to reach for until docs/plan/41's § D14
///         work added one; this is what it is for.
///     </para>
/// </remarks>
public class EarClippingPredicateTests {
    /// <summary>
    ///     Three points on <c>y = 3x</c>, chosen so every coordinate is an integer a <c>float</c>
    ///     holds exactly and the naive cross product is still wrong. If a triangulator believes this
    ///     corner has area, it will cut an ear that is a line.
    /// </summary>
    [Fact]
    public void TheCollinearityAnEarTestDependsOnIsNotAFloatQuestion() {
        Vector2 a = new(7f, 21f);
        Vector2 b = new(16777216f, 50331648f);
        Vector2 c = new(2f, 6f);

        var naive = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

        Assert.NotEqual(0f, naive);
        Assert.Equal(0, ExactPredicates.Orient2D(a, b, c));
    }

    /// <summary>
    ///     ⚠ And the consequence, on a mesh rather than on three numbers: a face whose corners are
    ///     very nearly collinear must still triangulate into triangles that partition it, and every
    ///     emitted index must be one of the face's own.
    /// </summary>
    [Fact]
    public void ANearlyCollinearNgonStillTriangulatesIntoItsOwnCorners() {
        var mesh = new EditMesh();

        // A five-sided face that is a rectangle with one edge subdivided a hair off the line — the
        // shape a boolean produces where a cut passes almost exactly through a vertex.
        var corners = new[] {
            mesh.AddPosition(new(0f, 0f, 0f)),
            mesh.AddPosition(new(1f, 0f, 0f)),
            mesh.AddPosition(new(1f, 1f, 0f)),
            mesh.AddPosition(new(0.5f, 1.0000001f, 0f)),
            mesh.AddPosition(new(0f, 1f, 0f))
        };

        mesh.AddFace(corners);

        var indices = mesh.Triangulate();

        Assert.Equal(9, indices.Length);
        Assert.All(indices, index => Assert.Contains(index, corners));

        // Three triangles covering a face of area ~1, whichever way the ears fell.
        var area = 0f;

        for (var triangle = 0; triangle < indices.Length / 3; triangle++) {
            var p = mesh.Positions[indices[triangle * 3]];
            var q = mesh.Positions[indices[(triangle * 3) + 1]];
            var r = mesh.Positions[indices[(triangle * 3) + 2]];

            area += Vector3.Cross(q - p, r - p).Length() * 0.5f;
        }

        Assert.InRange(area, 0.99f, 1.01f);
    }
}
