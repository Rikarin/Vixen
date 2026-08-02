// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Tests;

/// <summary>Quad meshes to ask topological questions of.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Built as quads rather than welded from a triangle soup, which the other fixtures
///         are.</b> A loop, a ring, a loop cut and a bridge are all statements about four-sided faces
///         — <c>MeshTopology.EdgeRing</c>'s own remarks say why "the opposite edge" is a phrase about
///         a quad — so testing them against a triangulated cube would be testing that they decline.
///     </para>
///     <para>
///         These are the shapes a block-out is actually made of: a tube is every extruded corridor, a
///         grid is every floor, and a box of six quads is what the shape tool will produce before
///         anybody has cut it.
///     </para>
/// </remarks>
static class TestShapes {
    /// <summary>An open tube of quads: a ring of sides, repeated along its axis.</summary>
    /// <param name="sides">How many quads round it. Three or more.</param>
    /// <param name="bands">How many quads along it. One or more.</param>
    /// <returns>The mesh, with two open ends.</returns>
    /// <remarks>
    ///     ⚠ <b>The ends are left open on purpose.</b> Capping them would put a fan of triangles at
    ///     each, and the vertices round the rim would stop having four edges — which is exactly what
    ///     an edge loop walk stops on, so a capped tube tests the stopping rule rather than the walk.
    /// </remarks>
    public static EditMesh Tube(int sides = 8, int bands = 3) {
        var mesh = new EditMesh();

        for (var band = 0; band <= bands; band++) {
            for (var side = 0; side < sides; side++) {
                var angle = side / (float) sides * MathF.Tau;

                mesh.AddPosition(new(MathF.Cos(angle), band - (bands * 0.5f), MathF.Sin(angle)));
            }
        }

        Span<int> loop = stackalloc int[4];

        for (var band = 0; band < bands; band++) {
            for (var side = 0; side < sides; side++) {
                var next = (side + 1) % sides;

                loop[0] = (band * sides) + side;
                loop[1] = (band * sides) + next;
                loop[2] = ((band + 1) * sides) + next;
                loop[3] = ((band + 1) * sides) + side;

                mesh.AddFace(loop, band);
            }
        }

        return mesh;
    }

    /// <summary>A flat grid of quads in the XZ plane, one unit apart.</summary>
    /// <param name="width">How many quads across.</param>
    /// <param name="depth">How many along.</param>
    /// <returns>The mesh, whose rim is one boundary loop.</returns>
    public static EditMesh Grid(int width = 3, int depth = 3) {
        var mesh = new EditMesh();

        for (var z = 0; z <= depth; z++) {
            for (var x = 0; x <= width; x++) {
                mesh.AddPosition(new(x, 0f, z));
            }
        }

        Span<int> loop = stackalloc int[4];

        for (var z = 0; z < depth; z++) {
            for (var x = 0; x < width; x++) {
                loop[0] = (z * (width + 1)) + x;
                loop[1] = (z * (width + 1)) + x + 1;
                loop[2] = ((z + 1) * (width + 1)) + x + 1;
                loop[3] = ((z + 1) * (width + 1)) + x;

                mesh.AddFace(loop);
            }
        }

        return mesh;
    }

    /// <summary>A closed box of six four-sided faces, one group each.</summary>
    /// <param name="size">How big, across.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>Wound so that every face looks outwards, which is what makes it pass the invariant
    ///     helper — an inside-out face is a reported inconsistency, not a mesh nobody can build.</remarks>
    public static EditMesh Box(float size = 1f) {
        var mesh = new EditMesh();
        var half = size * 0.5f;

        for (var corner = 0; corner < 8; corner++) {
            mesh.AddPosition(
                new(
                    (corner & 1) == 0 ? -half : half,
                    (corner & 2) == 0 ? -half : half,
                    (corner & 4) == 0 ? -half : half
                )
            );
        }

        // Each side named by its four corners in the order that walks it anticlockwise seen from
        // outside, which is the winding every primitive in the engine uses.
        Quad(1, 3, 7, 5);
        Quad(0, 4, 6, 2);
        Quad(2, 6, 7, 3);
        Quad(0, 1, 5, 4);
        Quad(4, 5, 7, 6);
        Quad(0, 2, 3, 1);

        return mesh;

        void Quad(int a, int b, int c, int d) {
            Span<int> loop = [a, b, c, d];

            mesh.AddFace(loop, mesh.FaceCount);
        }
    }
}
