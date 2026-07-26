// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     Planes, rays and bounding volumes. The frustum tests carry the most weight: reverse-Z swaps
///     which clip-space inequality bounds the near plane, and getting it backwards produces a
///     frustum that culls everything close to the camera — a bug that looks like it lives in the
///     renderer.
/// </summary>
public class VolumeTests {
    const float Tolerance = 1e-3f;

    static readonly Matrix4x4 View = Matrix4x4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.Up);
    static readonly Matrix4x4 Projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverTwo, 1f, 1f, 100f);
    static readonly Matrix4x4 ViewProjection = View * Projection;

    [Fact]
    public void A_plane_reports_signed_distance_and_side() {
        var floor = Plane.FromPointNormal(Vector3.Zero, Vector3.Up);

        Assert.Equal(5f, floor.DotCoordinate(new(0f, 5f, 0f)), 4);
        Assert.Equal(-5f, floor.DotCoordinate(new(0f, -5f, 0f)), 4);
        Assert.Equal(PlaneIntersectionType.Front, floor.Classify(new(3f, 1f, 2f)));
        Assert.Equal(PlaneIntersectionType.Back, floor.Classify(new(3f, -1f, 2f)));
        Assert.Equal(PlaneIntersectionType.Intersecting, floor.Classify(new(3f, 0f, 2f)));
    }

    [Fact]
    public void A_plane_through_three_points_faces_the_winding() {
        // Counter-clockwise seen from +Y, so the normal points up.
        var plane = Plane.FromPoints(Vector3.Zero, new(1f, 0f, 0f), new(0f, 0f, -1f));

        Assert.True(Vector3.NearEqual(Vector3.Up, plane.Normal, Tolerance));
        Assert.Equal(0f, plane.D, 4);

        // Reversing the winding reverses the normal, which is the whole convention.
        var flipped = Plane.FromPoints(Vector3.Zero, new(0f, 0f, -1f), new(1f, 0f, 0f));
        Assert.True(Vector3.NearEqual(Vector3.Down, flipped.Normal, Tolerance));
    }

    [Fact]
    public void A_plane_transformed_by_a_non_uniform_scale_stays_perpendicular_to_its_surface() {
        // The case that catches transforming the coefficients by the matrix instead of its inverse
        // transpose: a 45-degree plane through the origin, squashed on one axis.
        var plane = Plane.FromPointNormal(Vector3.Zero, new(1f, 1f, 0f));
        var squash = Matrix4x4.FromScale(new(1f, 4f, 1f));
        var transformed = Plane.Transform(plane, squash);

        // A point that was on the plane is still on it after both are transformed.
        var onPlane = Matrix4x4.TransformPosition(new(1f, -1f, 0f), squash);
        Assert.Equal(0f, transformed.DotCoordinate(onPlane), 3);
    }

    [Fact]
    public void An_empty_box_is_the_identity_for_merging() {
        // Which is what lets a bound be accumulated over a loop with no special case for the first
        // item — the reason Empty is inverted rather than zero-sized.
        var box = new BoundingBox(new(-1f), new(1f));

        Assert.True(BoundingBox.Empty.IsEmpty);
        Assert.Equal(box, BoundingBox.Merge(BoundingBox.Empty, box));
        Assert.Equal(box, BoundingBox.Merge(box, BoundingBox.Empty));
        Assert.True(BoundingBox.FromPoints([]).IsEmpty);
    }

    [Fact]
    public void A_box_fits_the_points_it_was_built_from() {
        Vector3[] points = [new(1f, 2f, 3f), new(-4f, 0f, 5f), new(0f, -6f, -7f)];
        var box = BoundingBox.FromPoints(points);

        Assert.Equal(new(-4f, -6f, -7f), box.Minimum);
        Assert.Equal(new(1f, 2f, 5f), box.Maximum);

        foreach (var point in points) {
            Assert.True(box.Contains(point));
        }
    }

    [Fact]
    public void Box_containment_distinguishes_inside_from_straddling() {
        var outer = new BoundingBox(new(-10f), new(10f));

        Assert.Equal(ContainmentType.Contains, outer.Contains(new BoundingBox(new(-1f), new(1f))));
        Assert.Equal(ContainmentType.Intersects, outer.Contains(new BoundingBox(new(5f), new(15f))));
        Assert.Equal(ContainmentType.Disjoint, outer.Contains(new BoundingBox(new(20f), new(30f))));
    }

    [Fact]
    public void A_rotated_box_grows_to_stay_axis_aligned() {
        // Unavoidable: the rotated box is not axis-aligned, so the result is its bound. A unit cube
        // turned 45 degrees about Y needs sqrt(2) of width.
        var cube = new BoundingBox(new(-0.5f), new(0.5f));
        var turned = BoundingBox.Transform(cube, Matrix4x4.FromRotationY(MathUtil.PiOverFour));

        Assert.Equal(MathF.Sqrt(2f) / 2f, turned.Maximum.X, 3);
        Assert.Equal(0.5f, turned.Maximum.Y, 3);

        // An empty box stays empty rather than becoming a point at the transformed origin.
        Assert.True(BoundingBox.Transform(BoundingBox.Empty, Matrix4x4.FromRotationY(1f)).IsEmpty);
    }

    [Fact]
    public void Merging_spheres_never_produces_one_bigger_than_it_needs() {
        var inner = new BoundingSphere(Vector3.Zero, 1f);
        var outer = new BoundingSphere(Vector3.Zero, 5f);

        // Already swallowed: the answer is the larger sphere, not something bigger than both.
        Assert.Equal(outer, BoundingSphere.Merge(inner, outer));
        Assert.Equal(outer, BoundingSphere.Merge(outer, inner));

        var apart = BoundingSphere.Merge(new(new(-2f, 0f, 0f), 1f), new(new(2f, 0f, 0f), 1f));
        Assert.Equal(3f, apart.Radius, 4);
        Assert.True(Vector3.NearEqual(Vector3.Zero, apart.Center, Tolerance));
    }

    [Fact]
    public void A_sphere_bounds_the_points_it_was_built_from() {
        Vector3[] points = [new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 2f, 0f)];
        var sphere = BoundingSphere.FromPoints(points);

        foreach (var point in points) {
            Assert.True(sphere.Contains(point));
        }

        Assert.True(BoundingSphere.FromPoints([]).IsEmpty);
    }

    [Fact]
    public void A_ray_hits_a_box_from_outside_and_from_within() {
        var box = new BoundingBox(new(-1f), new(1f));

        Assert.True(new Ray(new(0f, 0f, -5f), Vector3.UnitZ).Intersects(box, out var distance));
        Assert.Equal(4f, distance, 3);

        // Starting inside is a hit at zero, which is what a picking query wants.
        Assert.True(new Ray(Vector3.Zero, Vector3.UnitX).Intersects(box, out var inside));
        Assert.Equal(0f, inside, 4);

        // Pointing away.
        Assert.False(new Ray(new(0f, 0f, -5f), -Vector3.UnitZ).Intersects(box, out _));

        // Parallel to a face and beside it — the case a zero direction component makes interesting.
        Assert.False(new Ray(new(0f, 5f, -5f), Vector3.UnitZ).Intersects(box, out _));
    }

    [Fact]
    public void A_ray_hits_a_sphere_a_plane_and_a_triangle() {
        Assert.True(new Ray(new(0f, 0f, -5f), Vector3.UnitZ).Intersects(new BoundingSphere(Vector3.Zero, 1f), out var toSphere));
        Assert.Equal(4f, toSphere, 3);

        Assert.True(new Ray(new(0f, 5f, 0f), Vector3.Down).Intersects(Plane.FromPointNormal(Vector3.Zero, Vector3.Up), out var toPlane));
        Assert.Equal(5f, toPlane, 3);

        // A ray straight down through a triangle lying in the XZ plane.
        Assert.True(
            new Ray(new(0.25f, 5f, -0.25f), Vector3.Down)
                .Intersects(Vector3.Zero, new(0f, 0f, -1f), new(1f, 0f, 0f), out var toTriangle)
        );
        Assert.Equal(5f, toTriangle, 3);

        // And one that passes beside it.
        Assert.False(
            new Ray(new(5f, 5f, 5f), Vector3.Down)
                .Intersects(Vector3.Zero, new(0f, 0f, -1f), new(1f, 0f, 0f), out _)
        );
    }

    [Fact]
    public void A_ray_misses_a_plane_it_is_parallel_to_or_points_away_from() {
        var floor = Plane.FromPointNormal(Vector3.Zero, Vector3.Up);

        Assert.False(new Ray(new(0f, 5f, 0f), Vector3.UnitX).Intersects(floor, out _));
        Assert.False(new Ray(new(0f, 5f, 0f), Vector3.Up).Intersects(floor, out _));
    }

    [Fact]
    public void The_frustum_near_and_far_planes_are_the_reverse_Z_way_round() {
        var frustum = new BoundingFrustum(ViewProjection);

        // Depth runs near -> 1, far -> 0, so `clip.z <= clip.w` bounds the near plane. Swapping the
        // two would put the near plane at 100 units and cull the whole scene.
        Assert.True(Vector3.NearEqual(new(0f, 0f, -1f), frustum.Near.Normal, Tolerance));
        Assert.Equal(-1f, frustum.Near.D, 3);

        Assert.True(Vector3.NearEqual(Vector3.UnitZ, frustum.Far.Normal, Tolerance));
        Assert.Equal(100f, frustum.Far.D, 2);
    }

    [Fact]
    public void The_frustum_contains_what_the_camera_can_see_and_nothing_else() {
        var frustum = new BoundingFrustum(ViewProjection);

        Assert.True(frustum.Contains(new Vector3(0f, 0f, -50f)));
        Assert.False(frustum.Contains(new Vector3(0f, 0f, -0.5f)));   // in front of the near plane
        Assert.False(frustum.Contains(new Vector3(0f, 0f, -200f)));   // past the far plane
        Assert.False(frustum.Contains(new Vector3(0f, 0f, 10f)));     // behind the camera
        Assert.False(frustum.Contains(new Vector3(100f, 0f, -50f)));  // off to the side
    }

    [Fact]
    public void Frustum_containment_distinguishes_inside_from_straddling() {
        var frustum = new BoundingFrustum(ViewProjection);

        Assert.Equal(ContainmentType.Contains, frustum.Contains(new BoundingSphere(new(0f, 0f, -50f), 1f)));
        Assert.Equal(ContainmentType.Intersects, frustum.Contains(new BoundingSphere(new(0f, 0f, -1f), 5f)));
        Assert.Equal(ContainmentType.Disjoint, frustum.Contains(new BoundingSphere(new(0f, 0f, 50f), 1f)));

        Assert.Equal(ContainmentType.Contains, frustum.Contains(new BoundingBox(new(-1f, -1f, -51f), new(1f, 1f, -49f))));
        Assert.Equal(ContainmentType.Disjoint, frustum.Contains(new BoundingBox(new(-1f, -1f, 49f), new(1f, 1f, 51f))));
    }

    [Fact]
    public void The_frustum_corners_are_where_its_planes_meet() {
        var frustum = new BoundingFrustum(ViewProjection);
        Span<Vector3> corners = stackalloc Vector3[BoundingFrustum.CornerCount];
        frustum.GetCorners(corners);

        // 90-degree vertical field of view at aspect 1: the near face is 2x2 at z = -1.
        Assert.True(Vector3.NearEqual(new(-1f, -1f, -1f), corners[0], 1e-2f));
        Assert.True(Vector3.NearEqual(new(1f, 1f, -1f), corners[2], 1e-2f));

        // And the far face is 200x200 at z = -100.
        Assert.True(Vector3.NearEqual(new(-100f, -100f, -100f), corners[4], 1e-1f));
    }

    [Fact]
    public void Frustum_containment_agrees_with_the_clip_space_inequalities() {
        // The oracle: a point is visible exactly when its clip coordinates satisfy -w <= x,y <= w
        // and 0 <= z <= w. The plane form should agree everywhere, and this compares them over
        // generated points rather than the handful anybody would write out.
        var points = Gen.Select(
            Gen.Float[-150f, 150f],
            Gen.Float[-150f, 150f],
            Gen.Float[-150f, 150f],
            (x, y, z) => new Vector3(x, y, z)
        );

        var frustum = new BoundingFrustum(ViewProjection);

        points.Sample(point => {
                // Skip points sitting on a plane, where the two formulations round differently and
                // disagreeing means nothing.
                var margin = float.PositiveInfinity;
                foreach (var plane in frustum.AsSpan()) {
                    margin = MathF.Min(margin, MathF.Abs(plane.DotCoordinate(point)));
                }

                if (margin < 1e-2f) {
                    return;
                }

                var clip = new Vector4(point, 1f) * ViewProjection;
                var visible = clip.X >= -clip.W && clip.X <= clip.W
                    && clip.Y >= -clip.W && clip.Y <= clip.W
                    && clip.Z >= 0f && clip.Z <= clip.W;

                Assert.Equal(visible, frustum.Contains(point));
            }
        );
    }

    [Fact]
    public void A_frustum_never_culls_something_it_contains() {
        // The property that actually matters for a cull: false positives cost a draw call, false
        // negatives lose an object. Any box holding a visible point must not be Disjoint.
        var frustum = new BoundingFrustum(ViewProjection);
        var boxes = Gen.Select(
            Gen.Float[-60f, 60f],
            Gen.Float[-60f, 60f],
            Gen.Float[-120f, 20f],
            Gen.Float[0.1f, 20f],
            (x, y, z, size) => new BoundingBox(new Vector3(x, y, z) - new Vector3(size), new Vector3(x, y, z) + new Vector3(size))
        );

        boxes.Sample(box => {
                Span<Vector3> corners = stackalloc Vector3[BoundingBox.CornerCount];
                box.GetCorners(corners);

                var anyVisible = false;
                foreach (var corner in corners) {
                    anyVisible |= frustum.Contains(corner);
                }

                if (anyVisible) {
                    Assert.NotEqual(ContainmentType.Disjoint, frustum.Contains(box));
                }
            }
        );
    }
}
