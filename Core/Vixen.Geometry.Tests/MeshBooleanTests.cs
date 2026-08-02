// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's P6: union, difference and intersection, and the degenerate cases that are normal here.</summary>
/// <remarks>
///     ⚠ <b>The gate is no hole and no self-intersection, not no exception.</b> A boolean that throws is
///     a bug somebody can see; one that returns a surface with a gap in it is a level that renders
///     wrongly three weeks later, in a room nobody remembers making. So nearly every assertion here is
///     about the <i>report</i> — closed, consistent, no degenerate faces — rather than about a count.
/// </remarks>
public class MeshBooleanTests {
    static EditMesh Box(Vector3 size, Vector3 at = default) {
        var mesh = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = size });

        if (at != default) {
            for (var position = 0; position < mesh.PositionCount; position++) {
                mesh.MovePosition(position, mesh.Positions[position] + at);
            }
        }

        return mesh;
    }

    static void AssertSolid(EditMesh? mesh, string what) {
        Assert.NotNull(mesh);

        var report = mesh.Validate();

        Assert.True(report.IsClosed, what + ": " + (report.Describe() ?? "closed"));
        Assert.True(report.IsConsistent, what + ": " + (report.Describe() ?? "consistent"));
        Assert.Empty(report.Degenerate);
        Assert.True(Volume(mesh) > 0f, what + " is inside out");
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

    [Fact]
    public void Two_boxes_side_by_side_union_into_one_solid_of_both_their_volumes() {
        var left = Box(new(2f, 2f, 2f), new(-3f, 0f, 0f));
        var right = Box(new(2f, 2f, 2f), new(3f, 0f, 0f));

        var made = MeshBoolean.Apply(left, right, BooleanOperation.Union);

        AssertSolid(made, "union");
        Assert.Equal(16f, Volume(made!), 3);
    }

    [Fact]
    public void Two_overlapping_boxes_union_into_their_union_and_not_their_sum() {
        var left = Box(new(4f, 4f, 4f));
        var right = Box(new(4f, 4f, 4f), new(2f, 0f, 0f));

        var made = MeshBoolean.Apply(left, right, BooleanOperation.Union);

        AssertSolid(made, "union");

        // Two four-metre cubes two metres apart: six by four by four.
        Assert.Equal(6f * 4f * 4f, Volume(made!), 2);
    }

    [Fact]
    public void Subtracting_a_column_from_a_slab_leaves_a_hole_through_it() {
        var slab = Box(new(6f, 1f, 6f));
        var column = Box(new(2f, 5f, 2f), new(0f, -2f, 0f));

        var made = MeshBoolean.Apply(slab, column, BooleanOperation.Difference);

        AssertSolid(made, "difference");
        Assert.Equal((6f * 1f * 6f) - (2f * 1f * 2f), Volume(made!), 2);

        // ⚠ And the reveal is the *cutter's* faces, shifted into their own groups — which is what
        // makes "give the hole a different material" a selection rather than a rebuild.
        Assert.True(
            made!.Faces.Select(face => face.Group).Distinct().Count() > 6,
            "the cut brought its own groups"
        );
    }

    [Fact]
    public void Intersecting_two_boxes_gives_the_lens_they_share() {
        var left = Box(new(4f, 4f, 4f));
        var right = Box(new(4f, 4f, 4f), new(3f, 0f, 0f));

        var made = MeshBoolean.Apply(left, right, BooleanOperation.Intersection);

        AssertSolid(made, "intersection");
        Assert.Equal(1f * 4f * 4f, Volume(made!), 2);
    }

    [Fact]
    public void Two_boxes_that_do_not_touch_intersect_to_nothing_rather_than_to_an_empty_mesh() {
        var left = Box(new(2f, 2f, 2f), new(-10f, 0f, 0f));
        var right = Box(new(2f, 2f, 2f), new(10f, 0f, 0f));

        // ⚠ Null, and a caller has to be able to tell it from a failure: one deletes an entity and
        // the other must not.
        Assert.Null(MeshBoolean.Apply(left, right, BooleanOperation.Intersection));
    }

    [Fact]
    public void Subtracting_a_solid_from_itself_leaves_nothing() {
        var one = Box(new(3f, 3f, 3f));
        var other = Box(new(3f, 3f, 3f));

        Assert.Null(MeshBoolean.Apply(one, other, BooleanOperation.Difference));
    }

    [Fact]
    public void Identical_operands_union_and_intersect_to_themselves() {
        var one = Box(new(3f, 3f, 3f));
        var other = Box(new(3f, 3f, 3f));

        // ⚠ The case every point-based boolean gets wrong, because every classification is exactly
        // zero and a tolerance has to choose. Six coplanar pairs facing opposite ways, and the answer
        // is decided by direction rather than by distance.
        var united = MeshBoolean.Apply(one, other, BooleanOperation.Union);
        var shared = MeshBoolean.Apply(one, other, BooleanOperation.Intersection);

        AssertSolid(united, "union of identical operands");
        AssertSolid(shared, "intersection of identical operands");

        Assert.Equal(27f, Volume(united!), 3);
        Assert.Equal(27f, Volume(shared!), 3);
    }

    [Fact]
    public void Two_boxes_meeting_exactly_face_to_face_union_without_a_seam() {
        // A wall standing on a floor, flush. Doc 24: "every wall meets every floor coplanar with
        // something", which is why this is the case rather than an edge case.
        var floor = Box(new(6f, 1f, 6f));
        var wall = Box(new(6f, 3f, 0.5f), new(0f, 1f, 0f));

        var made = MeshBoolean.Apply(floor, wall, BooleanOperation.Union);

        AssertSolid(made, "flush union");
        Assert.Equal((6f * 1f * 6f) + (6f * 3f * 0.5f), Volume(made!), 2);
    }

    [Fact]
    public void An_operand_entirely_inside_another_disappears_from_a_union_and_is_the_intersection() {
        var outer = Box(new(6f, 6f, 6f));
        var inner = Box(new(2f, 2f, 2f), new(0f, 2f, 0f));

        var united = MeshBoolean.Apply(outer, inner, BooleanOperation.Union);
        var shared = MeshBoolean.Apply(outer, inner, BooleanOperation.Intersection);

        AssertSolid(united, "union with a contained operand");
        AssertSolid(shared, "intersection with a contained operand");

        Assert.Equal(216f, Volume(united!), 2);
        Assert.Equal(8f, Volume(shared!), 2);
    }

    [Fact]
    public void Subtracting_a_containing_solid_leaves_nothing_and_the_other_way_leaves_a_cavity() {
        var outer = Box(new(6f, 6f, 6f));
        var inner = Box(new(2f, 2f, 2f), new(0f, 2f, 0f));

        Assert.Null(MeshBoolean.Apply(inner, outer, BooleanOperation.Difference));

        var hollow = MeshBoolean.Apply(outer, inner, BooleanOperation.Difference);

        Assert.NotNull(hollow);

        // ⚠ A cavity is closed and consistent and has *two* shells, one of them inside out with
        // respect to the world — which is exactly right for a void and is why the assertion is the
        // report rather than the shell count.
        var report = hollow.Validate();

        Assert.True(report.IsClosed, report.Describe() ?? "closed");
        Assert.True(report.IsConsistent, report.Describe() ?? "consistent");
        Assert.Equal(216f - 8f, Volume(hollow), 2);
    }

    [Fact]
    public void A_boolean_over_a_transform_puts_the_second_operand_where_the_transform_says() {
        var slab = Box(new(6f, 1f, 6f));
        var column = Box(new(2f, 5f, 2f));

        var moved = MeshBoolean.Apply(
            slab,
            column,
            BooleanOperation.Difference,
            Matrix4x4.FromTranslation(new(0f, -2f, 0f))
        );

        AssertSolid(moved, "difference through a transform");
        Assert.Equal((6f * 1f * 6f) - (2f * 1f * 2f), Volume(moved!), 2);
    }

    [Fact]
    public void A_plane_cut_halves_a_box_and_caps_the_opening() {
        var box = Box(new(4f, 4f, 4f));
        var made = MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, -2f));

        AssertSolid(made, "plane cut");
        Assert.Equal(4f * 2f * 4f, Volume(made!), 2);

        // ⚠ And it is the *lower* half, which a volume cannot tell you when the solid is symmetrical
        // about the cut — the two halves of a box weigh the same, so a plane recorded facing the wrong
        // way passes every assertion but this one.
        Assert.Equal(0f, made!.Bounds.Minimum.Y, 3);
        Assert.Equal(2f, made.Bounds.Maximum.Y, 3);

        // The cap is one face in its own group, so "select the face the cut made" is one click.
        Assert.Equal(1, made!.Faces.Count(face => face.Group == MeshBoolean.CapGroup));
    }

    [Fact]
    public void A_plane_cut_can_keep_the_other_half_instead() {
        var box = Box(new(4f, 4f, 4f));

        var lower = MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, -2f));
        var upper = MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, -2f), keepFront: true);

        AssertSolid(lower, "lower half");
        AssertSolid(upper, "upper half");

        // The two halves together are the box, which is the one assertion that catches a cap wound
        // the wrong way as well as a half taken from the wrong side.
        Assert.Equal(64f, Volume(lower!) + Volume(upper!), 2);

        Assert.Equal(2f, lower!.Bounds.Maximum.Y, 3);
        Assert.Equal(2f, upper!.Bounds.Minimum.Y, 3);
    }

    [Fact]
    public void An_uncapped_cut_is_a_surface_rather_than_a_solid() {
        var box = Box(new(4f, 4f, 4f));
        var made = MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, -2f), cap: false);

        Assert.NotNull(made);
        Assert.False(made.Validate().IsClosed, "an uncapped cut has an opening");
    }

    [Fact]
    public void A_cut_that_misses_leaves_the_solid_alone() {
        var box = Box(new(4f, 4f, 4f));
        var made = MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, -100f));

        AssertSolid(made, "a cut above everything");
        Assert.Equal(64f, Volume(made!), 2);
    }

    [Fact]
    public void A_cut_exactly_along_a_face_keeps_the_whole_solid() {
        // ⚠ Every classification is zero on one whole face of the box, which is the case a tolerance
        // decides by coin flip. The solid is entirely on the plane's front side, so keeping the front
        // keeps all of it — including the coplanar face itself, which has to survive or the box loses
        // its floor.
        var box = Box(new(4f, 4f, 4f));
        var made = MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, 0f), keepFront: true);

        AssertSolid(made, "a cut along the floor");
        Assert.Equal(64f, Volume(made!), 2);

        // And the other half is nothing rather than a single face with no volume — which is what a
        // classification that kept the coplanar surface and called it a solid would have given.
        Assert.Null(MeshBoolean.PlaneCut(box, new Plane(Vector3.UnitY, 0f)));
    }

    [Fact]
    public void A_trim_takes_the_material_away_and_leaves_the_opening_bare() {
        var wall = Box(new(6f, 4f, 0.5f));
        var cutter = Box(new(2f, 2f, 4f), new(0f, 0f, 0f));

        var trimmed = MeshBoolean.Trim(wall, cutter);
        var subtracted = MeshBoolean.Apply(wall, cutter, BooleanOperation.Difference);

        Assert.NotNull(trimmed);
        Assert.NotNull(subtracted);

        // ⚠ The same material is gone either way and only one of them is a solid, which is why the
        // comparison is the face count rather than the volume: an open surface's signed volume is a
        // number, and it is not a volume.
        Assert.True(subtracted.FaceCount > trimmed.FaceCount, "a subtract lines the hole and a trim does not");
        Assert.False(trimmed.Validate().IsClosed, "a trim leaves the opening bare");
        Assert.True(subtracted.Validate().IsClosed, "a subtract does not");
    }

    [Fact]
    public void Face_groups_and_smoothing_groups_survive_a_boolean() {
        var cylinder = MeshShapes.Create(
            new ShapeParameters { Kind = ShapeKind.Cylinder, Size = new(4f, 6f, 4f), Sides = 24 }
        );

        MeshSurfaces.AutoSmooth(cylinder);

        var cutter = Box(new(2f, 2f, 10f), new(0f, 2f, 0f));
        var made = MeshBoolean.Apply(cylinder, cutter, BooleanOperation.Difference);

        Assert.NotNull(made);

        // ⚠ A verb that dropped the smoothing gives back a cylinder that is faceted, which reads as a
        // renderer bug rather than as the boolean that caused it.
        Assert.Contains(made.Faces, face => face.Smoothing != 0);
        Assert.Contains(made.Faces, face => face.Group == MeshShapes.GroupSide);
    }

    [Fact]
    public void A_boolean_with_an_empty_operand_is_the_other_one() {
        var box = Box(new(2f, 2f, 2f));
        var nothing = new EditMesh();

        Assert.Equal(8f, Volume(MeshBoolean.Apply(box, nothing, BooleanOperation.Union)!), 3);
        Assert.Equal(8f, Volume(MeshBoolean.Apply(box, nothing, BooleanOperation.Difference)!), 3);
        Assert.Null(MeshBoolean.Apply(box, nothing, BooleanOperation.Intersection));
        Assert.Equal(8f, Volume(MeshBoolean.Apply(nothing, box, BooleanOperation.Union)!), 3);
    }

    [Fact]
    public void Booleans_chain_without_the_result_degrading() {
        // ⚠ The case that separates a boolean that works from one that works once. Every operand after
        // the first is cutting geometry that a previous cut produced, so every vertex it classifies is
        // a derived one — and a boolean whose derived vertices drift is one whose second cut opens the
        // seam its first cut closed.
        var mesh = Box(new(8f, 4f, 8f));

        // ⚠ Spaced so the holes do not touch. Two voids meeting exactly along an edge is a
        // *non-manifold* result and it is the correct one — which is a different test from this one,
        // and mixing the two would make a real regression here look like the geometry.
        for (var step = 0; step < 6; step++) {
            var hole = Box(new(0.6f, 6f, 0.6f), new(-2.5f + step, 0f, -2.5f + step));
            var made = MeshBoolean.Apply(mesh, hole, BooleanOperation.Difference);

            Assert.NotNull(made);

            mesh = made;

            AssertSolid(mesh, "cut " + step);
        }

        Assert.Equal((8f * 4f * 8f) - (6f * 0.6f * 4f * 0.6f), Volume(mesh), 2);
    }
}
