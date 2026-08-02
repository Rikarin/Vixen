// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's P4 generators: every shape a closed solid, the right way out, and the right size.</summary>
/// <remarks>
///     ⚠ <b>The winding is what these are really for.</b> A generator that produces an inside-out solid
///     validates as closed, draws as a hole under back-face culling and is invisible in a screenshot
///     test that renders it against a dark background — and the failure shows up three phases later as
///     "extrude goes the wrong way on cylinders". The signed volume is one number that catches all of
///     it.
/// </remarks>
public class MeshShapeTests {
    /// <summary>Every shape, so that a kind added without a generator fails here rather than in a viewport.</summary>
    public static TheoryData<ShapeKind> Kinds {
        get {
            var data = new TheoryData<ShapeKind>();

            foreach (var kind in Enum.GetValues<ShapeKind>()) {
                data.Add(kind);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_shape_is_a_sound_mesh_that_fits_the_size_it_was_asked_for(ShapeKind kind) {
        var parameters = ShapeParameters.Default(kind);
        var mesh = MeshShapes.Create(parameters);

        Assert.False(mesh.IsEmpty);

        var report = mesh.Validate();

        // ⚠ No orphans, unlike the operations' own tests, and a generator is exactly where that is
        // affordable: nothing here renumbers anything, so a position nobody names is a bug in the
        // routine rather than a corner of a gesture that has not finished.
        Assert.Equal(0, report.Orphans);
        Assert.Empty(report.Degenerate);
        Assert.True(report.IsConsistent, report.Describe() ?? "consistent");

        var bounds = mesh.Bounds;

        // ⚠ Within the size rather than exactly it, because a shape is allowed to be smaller than its
        // box and one of them is: a torus whose tube is a quarter of its ring does not reach the
        // corners. What no shape may do is escape the extent it was asked for, which is what makes a
        // parameter mean something to somebody dragging it.
        Assert.True(bounds.Maximum.X <= (parameters.Size.X * 0.5f) + Tolerance, "inside its width");
        Assert.True(bounds.Minimum.X >= (-parameters.Size.X * 0.5f) - Tolerance, "inside its width");
        Assert.True(bounds.Maximum.Z <= (parameters.Size.Z * 0.5f) + Tolerance, "inside its depth");
        Assert.True(bounds.Minimum.Z >= (-parameters.Size.Z * 0.5f) - Tolerance, "inside its depth");

        // Sitting on the origin rather than centred on it, which is the whole of why a shape dropped
        // on the work plane is not half-buried in it.
        Assert.Equal(0f, bounds.Minimum.Y, 3);
        Assert.True(bounds.Maximum.Y <= parameters.Size.Y + Tolerance, "inside its height");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_shape_but_the_plane_is_a_closed_solid_the_right_way_out(ShapeKind kind) {
        if (kind == ShapeKind.Plane) {
            // The one shape that is deliberately a surface rather than a solid: a floor with a
            // thickness is a box, and doc 24's table lists both.
            return;
        }

        var mesh = MeshShapes.Create(kind);
        var report = mesh.Validate();

        Assert.True(report.IsClosed, report.Describe() ?? "closed");

        // ⚠ Positive, and that is the assertion. The divergence theorem gives a negative signed volume
        // for a solid whose faces all point inwards, which is exactly the mistake a generator makes
        // and exactly the one nothing else notices.
        Assert.True(Volume(mesh) > 0f, $"{kind} is inside out");
    }

    [Fact]
    public void A_box_is_six_quads_in_six_groups_and_holds_its_own_volume() {
        var mesh = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(2f, 3f, 4f) });

        Assert.Equal(6, mesh.FaceCount);
        Assert.All(mesh.Faces, face => Assert.Equal(4, face.Count));
        Assert.Equal(6, mesh.Faces.Select(face => face.Group).Distinct().Count());

        Assert.Equal(24f, Volume(mesh), 3);
    }

    [Fact]
    public void A_flight_of_stairs_is_one_solid_whose_treads_are_a_group() {
        var shape = new ShapeParameters { Kind = ShapeKind.Stairs, Size = new(2f, 3f, 6f), Steps = 6 };
        var mesh = MeshShapes.Create(shape);

        // Six steps of half a metre rise and a metre of run, so the profile is 0.5 × (1+2+⋯+6) square
        // metres — more than the triangle under the diagonal by half a step, which is the difference
        // between a staircase and a ramp and is what the volume is asserted to notice.
        Assert.Equal(2f * 10.5f, Volume(mesh), 3);

        // ⚠ Six treads and six risers, grouped by what they *are*. The floor and the tall back end are
        // their own groups, so "select every tread" is one click at any angle — which is what a
        // material assigned to the treads has to survive the flight being made steeper.
        var treads = mesh.Faces.Count(face => face.Group == MeshShapes.GroupTop);
        var risers = mesh.Faces.Count(face => face.Group == MeshShapes.GroupFront);

        Assert.Equal(6, treads);
        Assert.Equal(6, risers);

        Assert.All(
            mesh.Faces.Index().Where(entry => entry.Item.Group == MeshShapes.GroupTop),
            entry => Assert.Equal(1f, mesh.Normal(entry.Index).Y, 3)
        );

        var report = mesh.Validate();

        Assert.True(report.IsClosed, report.Describe() ?? "closed");
    }

    [Fact]
    public void A_door_frame_has_a_hole_through_it_and_a_header_of_the_thickness_asked_for() {
        var shape = new ShapeParameters {
            Kind = ShapeKind.DoorFrame, Size = new(4f, 3f, 0.4f), Thickness = 0.5f, Inner = 0.5f
        };

        var mesh = MeshShapes.Create(shape);
        var report = mesh.Validate();

        Assert.True(report.IsClosed, report.Describe() ?? "closed");

        // The wall, less the opening: two metres wide by two and a half tall by the wall's depth.
        Assert.Equal((4f * 3f * 0.4f) - (2f * 2.5f * 0.4f), Volume(mesh), 3);

        // And the reveal is its own group, because a doorway's lining is nearly always different
        // material from the wall it is cut through.
        Assert.Equal(3, mesh.Faces.Count(face => face.Group == MeshShapes.GroupBore));
    }

    [Fact]
    public void An_arch_is_a_door_frame_with_a_curved_head_and_less_of_it_missing() {
        var square = MeshShapes.Create(
            new ShapeParameters {
                Kind = ShapeKind.DoorFrame, Size = new(4f, 3f, 0.4f), Sides = 8, Thickness = 0.5f, Inner = 0.5f
            }
        );

        var round = MeshShapes.Create(
            new ShapeParameters {
                Kind = ShapeKind.Arch, Size = new(4f, 3f, 0.4f), Sides = 8, Thickness = 0.5f, Inner = 0.5f
            }
        );

        Assert.True(round.Validate().IsClosed, "an arch is closed");

        // The arc takes the corners off the top of the opening, so an arch is more wall than a square
        // opening of the same extents — which is the one thing an arch has to be for the parameter to
        // read the way it looks.
        Assert.True(Volume(round) > Volume(square), "an arch leaves more wall than a square head");

        // Eight segments in the curve rather than one flat lintel, and the reveal follows it.
        Assert.True(
            round.Faces.Count(face => face.Group == MeshShapes.GroupBore)
            > square.Faces.Count(face => face.Group == MeshShapes.GroupBore),
            "the arc is segmented"
        );
    }

    [Fact]
    public void A_pipe_has_a_bore_and_the_bore_faces_the_axis() {
        var shape = new ShapeParameters {
            Kind = ShapeKind.Pipe, Size = new(2f, 4f, 2f), Sides = 24, Inner = 0.5f
        };

        var mesh = MeshShapes.Create(shape);

        Assert.True(mesh.Validate().IsClosed, "a pipe is closed");

        // π(R² − r²)h, to the accuracy twenty-four sides gives — which is about a per cent low,
        // because a polygon inscribes its circle.
        Assert.Equal(MathF.PI * (1f - 0.25f) * 4f, Volume(mesh), 0);

        // Every bore face points at the axis rather than away from it, which is the whole difference
        // between a pipe and a cylinder somebody drew a second cylinder inside.
        foreach (var (index, face) in mesh.Faces.Index()) {
            if (face.Group != MeshShapes.GroupBore) {
                continue;
            }

            var centre = Centre(mesh, index);
            var normal = mesh.Normal(index);

            Assert.True(Vector3.Dot(normal, new Vector3(centre.X, 0f, centre.Z)) < 0f, "the bore faces inwards");
        }
    }

    [Fact]
    public void A_swept_outline_is_the_poly_shape_tool_and_holds_its_area_times_its_height() {
        // An L-shaped room's footprint, anticlockwise in the ground plane, pulled up three metres —
        // which is doc 24's poly shape exactly: click a polygon, then drag the height.
        Span<Vector2> footprint = [
            new(0f, 0f),
            new(4f, 0f),
            new(4f, 2f),
            new(2f, 2f),
            new(2f, 5f),
            new(0f, 5f)
        ];

        var mesh = MeshShapes.Sweep(
            footprint,
            Vector3.Zero,
            Vector3.UnitZ,
            Vector3.UnitX,
            new(0f, 3f, 0f)
        );

        Assert.True(mesh.Validate().IsClosed, "a swept outline is closed");

        // The L is 4×2 plus 2×3, so fourteen square metres three metres tall.
        Assert.Equal(14f * 3f, Volume(mesh), 3);

        // Two caps and one wall per edge.
        Assert.Equal(footprint.Length + 2, mesh.FaceCount);
    }

    [Fact]
    public void Parameters_out_of_range_are_clamped_rather_than_refused() {
        var mesh = MeshShapes.Create(
            new ShapeParameters { Kind = ShapeKind.Cylinder, Size = new(-4f, 0f, 2f), Sides = 1, Steps = -3 }
        );

        // ⚠ Geometry rather than an exception, because the caller is a number field somebody is
        // scrubbing through zero — see `ShapeParameters.Clamped`.
        Assert.False(mesh.IsEmpty);
        Assert.True(mesh.Validate().IsClosed, "a clamped cylinder is still a cylinder");
    }

    [Fact]
    public void A_sphere_encloses_very_nearly_the_volume_of_a_ball() {
        var mesh = MeshShapes.Create(
            new ShapeParameters { Kind = ShapeKind.Sphere, Size = new(2f, 2f, 2f), Sides = 48, Steps = 24 }
        );

        Assert.Equal(4f / 3f * MathF.PI, Volume(mesh), 1);
        Assert.True(mesh.Validate().IsClosed, "a sphere is closed");
    }

    const float Tolerance = 1e-3f;

    static Vector3 Centre(EditMesh mesh, int face) {
        var entry = mesh.Faces[face];
        var total = Vector3.Zero;

        for (var corner = 0; corner < entry.Count; corner++) {
            total += mesh.Positions[mesh.Corners[entry.Start + corner]];
        }

        return total / entry.Count;
    }

    static float Volume(EditMesh mesh) {
        var triangles = mesh.Triangulate();
        var total = 0f;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            total += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
        }

        return total;
    }
}
