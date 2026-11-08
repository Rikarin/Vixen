// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #168's seven shape kinds, each through <c>Source/Shape</c> on a device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>Two kinds of oracle.</b> A hard-edged boundary kind is held to its area: the share of texels
///         inside a regular polygon, a star, a ring, a capsule or a crescent is a formula in its parameters,
///         and the count misses it by about one texel's width of perimeter. The two profiles are held texel
///         by texel, because each is a function of where the texel is.
///     </para>
///     <para>
///         ⚠ <b>And one identity, which is the strongest of them</b>: a three-sided polygon is the triangle,
///         at any rotation and falloff, computed by a different formula in the same kernel.
///     </para>
/// </remarks>
public class TextureShapeFamilyDeviceTests(ITestOutputHelper output) {
    const int Side = 256;

    /// <summary>The diameter every shape here is drawn at, as a fraction of the image.</summary>
    const float Scale = 0.8f;

    /// <summary>The share of the image the unit circle covers at <see cref="Scale" />.</summary>
    const float Unit = Scale * Scale / 4f;

    static Lanes Shape(Vixen.Graphics.Vulkan.VulkanDevice device, string kind, Action<GraphNode>? numbers = null) =>
        Bake(device, "Source/Shape", Side, Side, [], node => {
            node.SetText("Kind", kind);
            node.SetValue("Scale", Scale);
            node.SetValue("Falloff", 0.0001f);
            numbers?.Invoke(node);
        });

    static float Covered(Lanes picture) {
        var inside = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                inside += picture.At(x, y, 0) > 0.5f ? 1 : 0;
            }
        }

        return inside / (float)(Side * Side);
    }

    /// <summary>Where a texel centre is in the shape's own frame: the radius is one, y is down.</summary>
    static (float X, float Y) Local(int x, int y) =>
        ((((x + 0.5f) / Side) - 0.5f) * 2f / Scale, (((y + 0.5f) / Side) - 0.5f) * 2f / Scale);

    /// <summary>The area of a lens between two unit circles whose centres are <paramref name="d" /> apart.</summary>
    static float Lens(float d) => (2f * MathF.Acos(d / 2f)) - ((d / 2f) * MathF.Sqrt(4f - (d * d)));

    [Fact]
    public void A_polygon_of_three_sides_is_the_triangle() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        foreach (var (rotation, falloff) in (ReadOnlySpan<(float, float)>)[(0f, 0.05f), (0.7f, 0.2f), (-2.1f, 0.01f)]) {
            void Numbers(GraphNode node) {
                node.SetValue("Rotation", rotation);
                node.SetValue("Falloff", falloff);
                node.SetValue("Sides", 3f);
            }

            var triangle = Shape(device, "Triangle", Numbers);
            var polygon = Shape(device, "Polygon", Numbers);

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    Assert.Equal(triangle.At(x, y, 0), polygon.At(x, y, 0), 0.002f);
                }
            }

            Assert.InRange(Covered(polygon), 0.05f, 0.5f);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    public void A_polygon_covers_the_area_of_a_regular_polygon_of_its_sides(int sides) {
        using var device = TextureKernelHarness.Open();

        var polygon = Shape(device, "Polygon", node => node.SetValue("Sides", sides));
        var expected = sides / 2f * MathF.Sin(MathF.Tau / sides) * Unit;

        Assert.Equal(expected, Covered(polygon), 0.004f);

        // One vertex points up: the texel just inside the top vertex is in, and the one just outside the
        // circumcircle above it is not.
        Assert.True(polygon.At(Side / 2, (int)(Side * (0.5f - (Scale / 2f))) + 2, 0) > 0.5f);
        Assert.True(polygon.At(Side / 2, (int)(Side * (0.5f - (Scale / 2f))) - 2, 0) < 0.5f);
    }

    [Fact]
    public void A_star_covers_the_area_of_its_points_and_valleys() {
        using var device = TextureKernelHarness.Open();

        foreach (var (points, inner) in (ReadOnlySpan<(int, float)>)[(5, 0.4f), (7, 0.6f), (4, 0.25f)]) {
            var star = Shape(device, "Star", node => {
                node.SetValue("Sides", points);
                node.SetValue("Inner", inner);
            });

            var expected = points * inner * MathF.Sin(MathF.PI / points) * Unit;

            Assert.Equal(expected, Covered(star), 0.004f);
            Assert.Equal(1f, star.At(Side / 2, Side / 2, 0), 0.001f);
        }
    }

    [Fact]
    public void A_ring_covers_the_band_between_its_hole_and_its_rim() {
        using var device = TextureKernelHarness.Open();

        foreach (var hole in (ReadOnlySpan<float>)[0.3f, 0.7f]) {
            var ring = Shape(device, "Ring", node => node.SetValue("Inner", hole));

            Assert.Equal(MathF.PI * (1f - (hole * hole)) * Unit, Covered(ring), 0.004f);
            Assert.Equal(0f, ring.At(Side / 2, Side / 2, 0), 0.001f);

            // The middle of the band, straight right of the centre.
            var middle = (int)((Side / 2f) + ((1f + hole) / 2f * Scale / 2f * Side));

            Assert.Equal(1f, ring.At(middle, Side / 2, 0), 0.001f);
        }
    }

    [Fact]
    public void A_capsule_is_a_rectangle_and_two_half_discs() {
        using var device = TextureKernelHarness.Open();

        foreach (var half in (ReadOnlySpan<float>)[0.25f, 0.5f]) {
            var capsule = Shape(device, "Capsule", node => node.SetValue("Inner", half));
            var expected = ((MathF.PI * half * half) + (2f * (1f - half) * 2f * half)) * Unit;

            Assert.Equal(expected, Covered(capsule), 0.004f);

            // Along x it reaches the rim; along y it stops at its half thickness.
            Each((x, y, local) => {
                if (MathF.Abs(local.Y) > half + 0.03f || MathF.Abs(local.X) > 1.03f) {
                    Assert.Equal(0f, capsule.At(x, y, 0), 0.001f);
                }
            });
        }
    }

    [Fact]
    public void A_crescent_is_a_disc_less_its_lens_with_the_cutting_disc() {
        using var device = TextureKernelHarness.Open();

        foreach (var cut in (ReadOnlySpan<float>)[0.25f, 0.5f, 0.8f]) {
            var crescent = Shape(device, "Crescent", node => node.SetValue("Inner", cut));
            var expected = (MathF.PI - Lens(2f * cut)) * Unit;

            Assert.Equal(expected, Covered(crescent), 0.004f);

            // Open to the right: the left rim is in, and the right of the centre is cut away.
            Assert.True(crescent.At((int)(Side * (0.5f - (Scale * 0.45f))), Side / 2, 0) > 0.5f);
            Assert.True(crescent.At((int)(Side * (0.5f + (Scale * 0.3f))), Side / 2, 0) < 0.5f);
        }
    }

    [Fact]
    public void A_pyramid_and_a_hemisphere_are_their_profiles_at_every_texel() {
        using var device = TextureKernelHarness.Open();

        var pyramid = Shape(device, "Pyramid", node => node.SetValue("Falloff", 0.3f));
        var hemisphere = Shape(device, "Hemisphere", node => node.SetValue("Falloff", 0.3f));

        Each((x, y, local) => {
            var square = MathF.Max(MathF.Abs(local.X), MathF.Abs(local.Y));
            var radius = MathF.Sqrt((local.X * local.X) + (local.Y * local.Y));

            Assert.Equal(Math.Clamp(1f - square, 0f, 1f), pyramid.At(x, y, 0), 0.001f);
            Assert.Equal(MathF.Sqrt(Math.Clamp(1f - (radius * radius), 0f, 1f)), hemisphere.At(x, y, 0), 0.002f);
        });
    }

    /// <summary>
    ///     Every new kind tiles: moved by half the image, the shape is the same picture rolled by half, which
    ///     only holds where it crosses the edge if it comes back on the other side.
    /// </summary>
    [Theory]
    [InlineData("Polygon")]
    [InlineData("Star")]
    [InlineData("Ring")]
    [InlineData("Capsule")]
    [InlineData("Crescent")]
    [InlineData("Pyramid")]
    [InlineData("Hemisphere")]
    public void A_kind_over_the_edge_comes_back_on_the_other_side(string kind) {
        using var device = TextureKernelHarness.Open();

        Lanes At(float x, float y) =>
            Shape(device, kind, node => {
                node.SetValue("Centre X", x);
                node.SetValue("Centre Y", y);
                node.SetValue("Falloff", 0.1f);
                node.SetValue("Rotation", 0.4f);
                node.SetValue("Sides", 5f);
                node.SetValue("Inner", 0.45f);
            });

        var middle = At(0.4f, 0.45f);
        var corner = At(0.9f, 0.95f);
        var crossing = 0;

        Each((x, y, _) => {
            var expected = middle.At((x + (Side / 2)) % Side, (y + (Side / 2)) % Side, 0);

            Assert.Equal(expected, corner.At(x, y, 0), 0.002f);
            crossing += (x == 0 || y == 0) && expected > 0.5f ? 1 : 0;
        });

        // ⚠ The instrument: the shape really did cross the edge, or a clamped one would pass as well.
        Assert.True(crossing > 4, $"{kind} never reached the left or top edge, so its wrap was not looked at.");
    }

    static void Each(Action<int, int, (float X, float Y)> check) {
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                check(x, y, Local(x, y));
            }
        }
    }
}
