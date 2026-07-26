// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     Rectangles and viewports — the 2D half, where the top-left origin and the Y flip between
///     device and screen coordinates live.
/// </summary>
public class RectangleAndViewportTests {
    static readonly Matrix4x4 View = Matrix4x4.LookAt(new(0f, 0f, 10f), Vector3.Zero, Vector3.Up);
    static readonly Matrix4x4 Projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverTwo, 16f / 9f, 1f, 500f);

    [Fact]
    public void A_rectangle_reports_its_edges_from_position_and_size() {
        var rectangle = new Rectangle(10f, 20f, 100f, 50f);

        Assert.Equal(10f, rectangle.Left);
        Assert.Equal(20f, rectangle.Top);
        Assert.Equal(110f, rectangle.Right);
        Assert.Equal(70f, rectangle.Bottom);
        Assert.Equal(new(60f, 45f), rectangle.Center);
        Assert.False(rectangle.IsEmpty);
        Assert.True(new Rectangle(0f, 0f, 0f, 10f).IsEmpty);
    }

    [Fact]
    public void Containment_is_half_open_so_adjacent_rectangles_tile() {
        var rectangle = new Rectangle(0f, 0f, 10f, 10f);

        // Top and left edges are inside; bottom and right are not, so a point on the seam between
        // two tiles belongs to exactly one of them.
        Assert.True(rectangle.Contains(new Vector2(0f, 0f)));
        Assert.True(rectangle.Contains(new Vector2(9.999f, 9.999f)));
        Assert.False(rectangle.Contains(new Vector2(10f, 5f)));
        Assert.False(rectangle.Contains(new Vector2(5f, 10f)));
    }

    [Fact]
    public void Rectangles_intersect_union_and_inflate() {
        var a = new Rectangle(0f, 0f, 10f, 10f);
        var b = new Rectangle(5f, 5f, 10f, 10f);

        Assert.True(a.Intersects(b));
        Assert.Equal(new(5f, 5f, 5f, 5f), Rectangle.Intersect(a, b));
        Assert.Equal(new(0f, 0f, 15f, 15f), Rectangle.Union(a, b));
        Assert.Equal(new(-2f, -2f, 14f, 14f), Rectangle.Inflate(a, 2f, 2f));
        Assert.Equal(new(1f, 2f, 10f, 10f), Rectangle.Offset(a, new(1f, 2f)));

        // No overlap at all.
        var apart = new Rectangle(100f, 100f, 5f, 5f);
        Assert.False(a.Intersects(apart));
        Assert.Equal(Rectangle.Empty, Rectangle.Intersect(a, apart));
    }

    [Fact]
    public void A_rectangle_from_two_corners_normalises_their_order() {
        Assert.Equal(
            Rectangle.FromCorners(new(10f, 10f), new(0f, 0f)),
            Rectangle.FromCorners(new(0f, 0f), new(10f, 10f))
        );

        Assert.Equal(new(0f, 0f, 10f, 10f), Rectangle.FromCorners(new(10f, 0f), new(0f, 10f)));
    }

    [Fact]
    public void Union_with_an_empty_rectangle_is_the_other_one() {
        var rectangle = new Rectangle(3f, 4f, 5f, 6f);

        Assert.Equal(rectangle, Rectangle.Union(Rectangle.Empty, rectangle));
        Assert.Equal(rectangle, Rectangle.Union(rectangle, Rectangle.Empty));
    }

    [Fact]
    public void A_viewport_reports_its_bounds_and_aspect_ratio() {
        var viewport = new Viewport(0f, 0f, 1920f, 1080f);

        Assert.Equal(new(0f, 0f, 1920f, 1080f), viewport.Bounds);
        Assert.Equal(16f / 9f, viewport.AspectRatio, 5);
        Assert.Equal(0f, viewport.MinDepth);
        Assert.Equal(1f, viewport.MaxDepth);
        Assert.Equal(0f, new Viewport(0f, 0f, 100f, 0f).AspectRatio);
    }

    [Fact]
    public void Projection_puts_the_scene_centre_at_the_screen_centre_and_flips_Y() {
        var viewport = new Viewport(0f, 0f, 1920f, 1080f);
        var viewProjection = View * Projection;

        var centre = viewport.Project(Vector3.Zero, viewProjection);
        Assert.Equal(960f, centre.X, 2);
        Assert.Equal(540f, centre.Y, 2);

        // Up in the world is up the screen, which is a *smaller* Y — the flip this method exists to
        // apply. Getting it wrong renders everything upside down and looks like a texture problem.
        var above = viewport.Project(new(0f, 1f, 0f), viewProjection);
        Assert.True(above.Y < centre.Y);

        // And right in the world is a larger X.
        var right = viewport.Project(new(1f, 0f, 0f), viewProjection);
        Assert.True(right.X > centre.X);
    }

    [Fact]
    public void Project_and_Unproject_are_inverses() {
        var viewport = new Viewport(0f, 0f, 1280f, 720f);
        var viewProjection = View * Projection;

        foreach (var world in new[] { Vector3.Zero, new Vector3(2f, -3f, 4f), new Vector3(-5f, 1f, -20f) }) {
            var screen = viewport.Project(world, viewProjection);
            Assert.True(Vector3.NearEqual(world, viewport.Unproject(screen, viewProjection), 1e-2f));
        }
    }

    [Fact]
    public void A_picking_ray_through_the_screen_centre_looks_where_the_camera_does() {
        var viewport = new Viewport(0f, 0f, 1280f, 720f);
        var ray = viewport.GetPickingRay(new(640f, 360f), View * Projection);

        // The camera sits at +10 on Z looking at the origin, so the centre ray runs down -Z and
        // starts on the near plane, one unit in front of the eye.
        Assert.True(Vector3.NearEqual(new(0f, 0f, -1f), ray.Direction, 1e-3f));
        Assert.True(Vector3.NearEqual(new(0f, 0f, 9f), ray.Origin, 1e-2f));
    }

    [Fact]
    public void A_picking_ray_off_centre_leans_the_way_it_should() {
        var viewport = new Viewport(0f, 0f, 1280f, 720f);
        var ray = viewport.GetPickingRay(new(1000f, 200f), View * Projection);

        // Right of centre and above it: +X, +Y, still into the screen.
        Assert.True(ray.Direction.X > 0f);
        Assert.True(ray.Direction.Y > 0f);
        Assert.True(ray.Direction.Z < 0f);
    }

    [Fact]
    public void An_offset_viewport_shifts_where_things_land() {
        var full = new Viewport(0f, 0f, 800f, 600f);
        var offset = new Viewport(100f, 50f, 800f, 600f);
        var viewProjection = View * Projection;

        var a = full.Project(Vector3.Zero, viewProjection);
        var b = offset.Project(Vector3.Zero, viewProjection);

        Assert.Equal(a.X + 100f, b.X, 3);
        Assert.Equal(a.Y + 50f, b.Y, 3);
    }
}
