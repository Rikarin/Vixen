// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 24's B4: which face, edge or vertex of one mesh the pointer is on.</summary>
public class SubObjectTests {
    const int Width = 1000;
    const int Height = 800;

    /// <summary>A camera near enough that the unit cube is a few hundred pixels across.</summary>
    /// <remarks>
    ///     ⚠ <b>The distance is part of the fixture rather than an arbitrary number.</b> The tolerance
    ///     is in pixels, so how far away the mesh is decides how much of it is within one — at eight
    ///     metres a unit cube is under a hundred pixels wide and every point on it is within ten of
    ///     some edge, including the diagonals across its far side. That is correct behaviour and a
    ///     useless fixture: the assertions would be about the camera rather than about the picker.
    /// </remarks>
    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 3f };

    static Vector2 Screen(EditorCamera camera, Vector3 world) {
        var projected = camera.Project(world, Width, Height);

        return new Vector2(projected.X, projected.Y);
    }

    // ── The element tables ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_cubes_twenty_four_drawing_vertices_are_eight_things_you_can_drag() {
        var mesh = MeshPrimitives.Cube();
        var elements = MeshElements.From(mesh);

        // ⚠ The whole reason this type exists. `MeshData` splits a corner three ways because it
        // carries three normals and three texture coordinates; a corner you can drag is one thing,
        // and a picker over the drawing structure would hand back whichever of the three it met.
        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(8, elements.PositionCount);

        Assert.Equal(12, elements.FaceCount);

        // Twelve edges of the cube, and one diagonal across each of the six faces — because a face
        // here is a triangle. Doc 24's D2 is what turns those six back into nothing you can select,
        // by making a face an n-gon and giving it a group.
        Assert.Equal(18, elements.EdgeCount);
    }

    [Fact]
    public void Every_edge_is_named_once_however_many_triangles_walk_it() {
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var seen = new HashSet<MeshEdge>();

        foreach (var edge in elements.Edges) {
            Assert.True(edge.A < edge.B, "an edge should be stored low-to-high");
            Assert.True(seen.Add(edge), "an edge should appear once");
        }
    }

    [Fact]
    public void A_curved_primitives_seam_is_welded_and_exact_equality_would_not_have() {
        // ⚠ The case the tolerance exists for. A sphere's first ring of vertices and its last are
        // `cos 0` against `cos 2π`: the same point, arrived at by different arithmetic, differing in
        // the last bits. Welded exactly, every curved primitive keeps a line of doubled positions
        // down its seam — invisible until somebody drags one half of it.
        var sphere = MeshPrimitives.Sphere(0.5f, 24, 12);

        var welded = MeshElements.From(sphere);
        var exact = MeshElements.From(sphere, tolerance: 0f);

        Assert.True(
            welded.PositionCount < exact.PositionCount,
            $"welding should close the seam: {welded.PositionCount} against {exact.PositionCount}"
        );
    }

    [Fact]
    public void Welding_never_merges_two_positions_a_primitive_meant_to_be_apart() {
        foreach (var kind in Enum.GetValues<PrimitiveKind>()) {
            var elements = MeshElements.From(MeshPrimitives.Create(kind, 24, 12));
            var positions = elements.Positions;

            for (var index = 0; index < positions.Length; index++) {
                for (var other = index + 1; other < positions.Length; other++) {
                    Assert.True(
                        Vector3.DistanceSquared(positions[index], positions[other]) > 0f,
                        $"{kind} kept two identical positions"
                    );
                }
            }

            // And every triangle still names three positions that exist, which is the invariant a
            // remap gets wrong first.
            foreach (var corner in elements.Triangles) {
                Assert.InRange(corner, 0, elements.PositionCount - 1);
            }
        }
    }

    // ── Picking ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_middle_of_a_face_is_a_face() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        // ⚠ Off the diagonal, and the middle of the pane would not have been. A face here is a
        // triangle, so the +Z side of a cube is two of them with a real, selectable edge running
        // corner to corner through the middle — which is exactly the artefact doc 24's D2 removes by
        // making a face an n-gon with a group id.
        var hit = picker.Under(
            elements,
            Matrix4x4.Identity,
            camera,
            Width,
            Height,
            Screen(camera, new Vector3(0.25f, -0.1f, 0.5f))
        );

        Assert.Equal(SubObjectKind.Face, hit.Kind);

        // The camera looks down −Z from +Z, so the triangle under the middle of the pane is one of
        // the two on the +Z face — and it is the near one rather than the far one, which is the
        // assertion that a ray test rather than a projection is answering.
        var triangles = elements.Triangles;
        var positions = elements.Positions;

        for (var corner = 0; corner < 3; corner++) {
            Assert.Equal(0.5f, positions[triangles[(hit.Index * 3) + corner]].Z, 4);
        }
    }

    [Fact]
    public void A_corner_is_a_vertex_rather_than_the_edges_and_face_it_is_also_on() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var corner = new Vector3(0.5f, 0.5f, 0.5f);

        var hit = picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, Screen(camera, corner));

        // ⚠ Innermost wins. That point is on three faces and three edges as well, and a rule that
        // took the largest candidate would make a vertex unclickable at every tolerance.
        Assert.Equal(SubObjectKind.Vertex, hit.Kind);
        Assert.Equal(corner, elements.Positions[hit.Index]);
    }

    [Fact]
    public void The_middle_of_an_edge_is_an_edge() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        // The vertical edge nearest the camera on the right of the pane, half way up.
        var midpoint = new Vector3(0.5f, 0f, 0.5f);

        var hit = picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, Screen(camera, midpoint));

        Assert.Equal(SubObjectKind.Edge, hit.Kind);

        var edge = elements.Edges[hit.Index];
        var centre = (elements.Positions[edge.A] + elements.Positions[edge.B]) * 0.5f;

        Assert.True(Vector3.DistanceSquared(centre, midpoint) < 1e-6f, $"picked the edge centred at {centre}");
    }

    [Fact]
    public void The_diagonal_a_triangulation_puts_across_a_face_is_a_selectable_edge() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        // ⚠ Asserted rather than worked around, because it is the honest state of this phase. A face
        // is a triangle until doc 24's P1 gives the kernel n-gons and P2 gives it face groups, so the
        // middle of a cube's side is on an edge — and a designer in edge mode will find it. Writing
        // the limit down is what makes it a phase rather than a bug.
        var hit = picker.Under(
            elements,
            Matrix4x4.Identity,
            camera,
            Width,
            Height,
            new Vector2(Width * 0.5f, Height * 0.5f)
        );

        Assert.Equal(SubObjectKind.Edge, hit.Kind);

        var edge = elements.Edges[hit.Index];

        Assert.Equal(0.5f, elements.Positions[edge.A].Z, 4);
        Assert.Equal(0.5f, elements.Positions[edge.B].Z, 4);
    }

    [Fact]
    public void A_filter_is_what_an_element_mode_is() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var at = Screen(camera, new Vector3(0.5f, 0.5f, 0.5f));

        // The same pixel, three questions. This is object/vertex/edge/face on the digits: the mode
        // decides which table the answer comes out of, and nothing else changes.
        Assert.Equal(
            SubObjectKind.Vertex,
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at, SubObjectFilter.Vertex).Kind
        );

        Assert.Equal(
            SubObjectKind.Edge,
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at, SubObjectFilter.Edge).Kind
        );

        Assert.Equal(
            SubObjectKind.Face,
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at, SubObjectFilter.Face).Kind
        );

        Assert.False(
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at, SubObjectFilter.None).IsHit
        );
    }

    [Fact]
    public void A_vertex_out_of_reach_is_not_picked_and_the_face_behind_it_still_is() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        // Well inside the +Z face, far from every corner of it on screen, and off the diagonal the
        // triangulation puts across it.
        var at = Screen(camera, new Vector3(0.25f, -0.1f, 0.5f));

        Assert.False(picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at, SubObjectFilter.Vertex).IsHit);
        Assert.Equal(SubObjectKind.Face, picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at).Kind);
    }

    [Fact]
    public void The_tolerance_is_in_pixels_and_a_bigger_one_reaches_further() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var corner = Screen(camera, new Vector3(0.5f, 0.5f, 0.5f));
        var near = corner + new Vector2(20f, 0f);

        Assert.False(
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, near, SubObjectFilter.Vertex, 10f).IsHit
        );

        Assert.Equal(
            SubObjectKind.Vertex,
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, near, SubObjectFilter.Vertex, 40f).Kind
        );
    }

    [Fact]
    public void Two_corners_on_the_same_pixel_answer_with_the_nearer_one() {
        // Straight down the z axis in an orthographic view, so the front and back corners of every
        // vertical edge project to exactly the same point. Which one the loop met first is not an
        // answer — it changes when the mesh is rebuilt.
        var camera = new EditorCamera { Pivot = Vector3.Zero, Distance = 3f, IsOrthographic = true };

        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var front = new Vector3(0.5f, 0.5f, 0.5f);
        var hit = picker.Under(
            elements,
            Matrix4x4.Identity,
            camera,
            Width,
            Height,
            Screen(camera, front),
            SubObjectFilter.Vertex
        );

        Assert.Equal(SubObjectKind.Vertex, hit.Kind);
        Assert.Equal(0.5f, elements.Positions[hit.Index].Z, 4);
    }

    [Fact]
    public void A_transform_moves_what_is_under_the_pointer() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var transform = Matrix4x4.FromTranslation(new Vector3(2f, 0f, 0f));
        var corner = Matrix4x4.TransformPosition(new Vector3(0.5f, 0.5f, 0.5f), transform);

        // The pointer is where the moved corner is; the elements are still the shape's own, in its
        // own space, which is the arrangement that lets a hundred cubes share one table.
        var hit = picker.Under(
            elements,
            transform,
            camera,
            Width,
            Height,
            Screen(camera, corner),
            SubObjectFilter.Vertex
        );

        Assert.Equal(SubObjectKind.Vertex, hit.Kind);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), elements.Positions[hit.Index]);
    }

    [Fact]
    public void A_mesh_with_no_scale_at_all_answers_nothing_rather_than_throwing() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var flattened = Matrix4x4.FromScale(Vector3.Zero);

        // An entity can be scaled to nothing and scaled back, and a picker that threw would take the
        // editor with it.
        Assert.False(
            picker.Under(
                elements,
                flattened,
                camera,
                Width,
                Height,
                new Vector2(Width * 0.5f, Height * 0.5f),
                SubObjectFilter.Face
            ).IsHit
        );
    }

    [Fact]
    public void A_pane_with_no_size_answers_nothing() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        Assert.False(picker.Under(elements, Matrix4x4.Identity, camera, 0, 0, Vector2.Zero).IsHit);
    }

    [Fact]
    public void Nothing_is_allocated_once_the_buffers_have_grown() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Create(PrimitiveKind.Torus, 24, 12));
        var picker = new SubObjectPicker();

        var at = new Vector2(Width * 0.5f, Height * 0.5f);

        // The first query sizes the projection buffers.
        picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at);

        // ⚠ The bar doc 24's B4 sets is "hover feedback fast enough to survive a mouse move", which
        // is one query per move for as long as the pointer is over the pane. A per-query allocation
        // of a torus' worth of screen positions is what that cannot afford.
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 32; index++) {
            picker.Under(elements, Matrix4x4.Identity, camera, Width, Height, at + new Vector2(index, 0f));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── The band ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_band_round_the_whole_mesh_takes_every_element_of_the_mode() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();
        var band = new Marquee(new Vector2(0f, 0f), new Vector2(Width, Height), false);

        List<int> taken = [];

        picker.Within(elements, Matrix4x4.Identity, camera, Width, Height, band, MeshElementKind.Vertex, taken);
        Assert.Equal(elements.PositionCount, taken.Count);

        picker.Within(elements, Matrix4x4.Identity, camera, Width, Height, band, MeshElementKind.Edge, taken);
        Assert.Equal(elements.EdgeCount, taken.Count);

        picker.Within(elements, Matrix4x4.Identity, camera, Width, Height, band, MeshElementKind.Face, taken);
        Assert.Equal(elements.FaceCount, taken.Count);
    }

    [Fact]
    public void A_band_over_one_corner_takes_that_corner_and_no_edge() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var corner = Screen(camera, new Vector3(0.5f, 0.5f, 0.5f));
        var band = new Marquee(corner - new Vector2(12f), corner + new Vector2(12f), false);

        List<int> taken = [];

        picker.Within(elements, Matrix4x4.Identity, camera, Width, Height, band, MeshElementKind.Vertex, taken);

        Assert.Single(taken);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), elements.Positions[taken[0]]);

        // ⚠ Wholly inside, not touching. Every edge at that corner leaves the box, so a touch rule
        // would take three edges from a band drawn round one point — and the extrude that followed
        // would act on geometry nobody drew a box round.
        picker.Within(elements, Matrix4x4.Identity, camera, Width, Height, band, MeshElementKind.Edge, taken);
        Assert.Empty(taken);
    }

    [Fact]
    public void A_band_that_covers_nothing_takes_nothing() {
        var camera = Camera();
        var elements = MeshElements.From(MeshPrimitives.Cube());
        var picker = new SubObjectPicker();

        var band = new Marquee(new Vector2(2f, 2f), new Vector2(14f, 14f), false);

        List<int> taken = [];

        picker.Within(elements, Matrix4x4.Identity, camera, Width, Height, band, MeshElementKind.Vertex, taken);

        Assert.Empty(taken);
    }

    // ── Elements of an edited mesh ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_edit_meshs_quad_is_one_face_however_many_triangles_it_is_drawn_as() {
        // A cube out of the kernel is triangles, so give it a quad to have an opinion about.
        var quad = new EditMesh();

        quad.AddPosition(new Vector3(-1f, 0f, -1f));
        quad.AddPosition(new Vector3(1f, 0f, -1f));
        quad.AddPosition(new Vector3(1f, 0f, 1f));
        quad.AddPosition(new Vector3(-1f, 0f, 1f));
        quad.AddFace([0, 1, 2, 3]);

        var elements = MeshElements.From(quad);

        Assert.Equal(2, elements.TriangleCount);
        Assert.Equal(1, elements.FaceCount);
        Assert.Equal(0, elements.FaceOf(0));
        Assert.Equal(0, elements.FaceOf(1));

        // ⚠ The indices come straight across rather than being welded again: a pick has to answer
        // with the number the kernel, the selection and an extrude all use.
        Assert.Equal(4, elements.EdgeCount);
        Assert.Equal(4, elements.PositionCount);
    }
}
