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

    /// <summary>
    ///     The box that used to be culled despite touching the frustum, pinned so it cannot be
    ///     again.
    /// </summary>
    /// <remarks>
    ///     Its corner at (-32, -20.993805, -32) lies exactly on the left plane —
    ///     <see cref="Plane.DotCoordinate" /> returns exactly zero — so the box is tangent and must
    ///     survive the cull. It used not to: the centre distance and the extent-projected reach are
    ///     equal in exact arithmetic and missed each other by one ULP in <see cref="float" />, which
    ///     was enough for <see cref="BoundingBox.Intersects(Plane)" /> to answer
    ///     <see cref="PlaneIntersectionType.Back" /> and for the frustum to drop the object.
    ///     Kept as a fixed case because the property test above only rediscovers it by chance —
    ///     about one run in thirty, which is worse than never: it looks green for long enough to be
    ///     trusted.
    /// </remarks>
    [Fact]
    public void A_box_tangent_to_a_frustum_plane_is_not_culled() {
        var frustum = new BoundingFrustum(ViewProjection);
        var box = new BoundingBox(new(-48f, -20.993805f, -32f), new(-32f, -4.993805f, -16f));

        // The premise: that corner really is on the plane, not merely near it.
        Assert.Equal(0f, frustum.Left.DotCoordinate(new(-32f, -20.993805f, -32f)));
        Assert.True(frustum.Contains(new Vector3(-32f, -20.993805f, -32f)));

        Assert.Equal(PlaneIntersectionType.Intersecting, box.Intersects(frustum.Left));
        Assert.NotEqual(ContainmentType.Disjoint, frustum.Contains(box));

        // The sphere path had the same shape of bug. A radius of 8*sqrt(2) puts the sphere's surface
        // on the same left plane the box's corner touches, reached by a different arithmetic route —
        // and it too used to come back Back, one ULP short.
        var sphere = new BoundingSphere(box.Center, box.Extent.X * MathF.Sqrt(2f));
        Assert.Equal(PlaneIntersectionType.Intersecting, sphere.Intersects(frustum.Left));
        Assert.NotEqual(ContainmentType.Disjoint, frustum.Contains(sphere));
    }

    [Fact]
    public void A_box_touching_a_plane_straddles_it_at_every_scene_scale() {
        // The margin has to be relative: the same box a hundred kilometres out has the same shape
        // and a hundred thousand times the rounding error, so no single absolute epsilon is right at
        // both ends. Each box below touches the plane at its own Minimum corner, on a slanted plane
        // so that nothing rounds exactly, and the verdict must be Intersecting at every scale. The
        // unbiased comparison got two of these six wrong — as Front here, but which way it lands is
        // luck, and the Back case is the one that loses an object.
        foreach (var scale in new[] { 1f, 1e1f, 1e2f, 1e3f, 1e4f, 1e5f }) {
            var box = new BoundingBox(
                new(scale, -2f * scale, 0.5f * scale),
                new(3f * scale, scale, 2.5f * scale)
            );
            var plane = Plane.FromPointNormal(box.Minimum, new(1f, 1f, 1f));

            Assert.Equal(0f, plane.DotCoordinate(box.Minimum));
            Assert.Equal(PlaneIntersectionType.Intersecting, box.Intersects(plane));
        }
    }

    [Fact]
    public void The_margin_absorbs_rounding_and_not_geometry() {
        // The other half of the bargain. The margin is about eight ULPs of the coordinates involved,
        // so even at a hundred kilometres from the origin it is under a tenth of a unit — a box
        // clear of the plane by one whole unit is still definitely on its own side. Without this,
        // "be conservative" could quietly become "never cull anything".
        var floor = Plane.FromPointNormal(Vector3.Zero, Vector3.Up);

        foreach (var scale in new[] { 1f, 1e2f, 1e3f, 1e4f, 1e5f }) {
            var below = new BoundingBox(new(scale, (-2f * scale) - 1f, scale), new(2f * scale, -1f, 2f * scale));
            Assert.Equal(PlaneIntersectionType.Back, below.Intersects(floor));

            var above = new BoundingBox(new(scale, 1f, scale), new(2f * scale, (2f * scale) + 1f, 2f * scale));
            Assert.Equal(PlaneIntersectionType.Front, above.Intersects(floor));

            // And the sphere, whose margin is built from the same scale.
            var sphereBelow = new BoundingSphere(new(scale, -scale - 1f, scale), scale);
            Assert.Equal(PlaneIntersectionType.Back, sphereBelow.Intersects(floor));
        }
    }
}
