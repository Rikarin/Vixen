// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #175's transform nodes, each through its node on a device, against closed forms.</summary>
/// <remarks>
///     The oracles are symmetries a remap has to keep and positions it has to land on: a quarter turn about
///     the middle of an even-sided image takes texel centres onto texel centres, so it can be asserted
///     exactly, and a round trip through polar coordinates has to come home.
/// </remarks>
public class TextureRemapDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    static void Each(Action<int, int> check) {
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                check(x, y);
            }
        }
    }

    /// <summary>Every texel different, in all three colour lanes, so a misplaced read shows.</summary>
    static readonly byte[] Unique = Picture(
        Side,
        Side,
        static (x, y) => (x / 63f, y / 63f, (x * 5 + y * 11) % 64 / 63f, 1f)
    );

    static (float R, float G, float B, float A) UniqueAt(int x, int y) =>
        (Unit(x / 63f), Unit(y / 63f), Unit((x * 5 + y * 11) % 64 / 63f), 1f);

    static void Near(
        (float R, float G, float B, float A) expected,
        (float R, float G, float B, float A) actual,
        float tolerance,
        string where
    ) =>
        Assert.True(
            MathF.Abs(expected.R - actual.R) < tolerance
            && MathF.Abs(expected.G - actual.G) < tolerance
            && MathF.Abs(expected.B - actual.B) < tolerance
            && MathF.Abs(expected.A - actual.A) < tolerance,
            $"{where}: expected {expected} and baked {actual}"
        );

    [Fact]
    public void Polar_and_back_returns_the_inside_of_the_inscribed_circle() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Wide = 256;

        // Smooth, so that two bilinear resamplings cost little, and not radial, so a wrong angle shows.
        static float Field(int x, int y) => 0.5f + 0.3f * MathF.Sin(x * 0.05f) + 0.15f * MathF.Cos(y * 0.07f);

        var picture = Grey(Wide, Wide, Field);
        var polar = Bake(device, "Space/Cartesian to Polar", Wide, Wide, [new("Input", picture)]);

        // Re-encode the polar picture and roll it back up.
        var bytes = Grey(Wide, Wide, (x, y) => polar.At(x, y, 0));
        var back = Bake(device, "Space/Polar to Cartesian", Wide, Wide, [new("Input", bytes)]);
        var checkedTexels = 0;

        for (var y = 0; y < Wide; y++) {
            for (var x = 0; x < Wide; x++) {
                var dx = x + 0.5f - Wide / 2f;
                var dy = y + 0.5f - Wide / 2f;
                var r = MathF.Sqrt(dx * dx + dy * dy);

                // Away from the centre, where the angle is undersampled, and from the rim.
                if (r is < 12f or > 110f) {
                    continue;
                }

                checkedTexels++;
                Assert.True(
                    MathF.Abs(Unit(Field(x, y)) - back.At(x, y, 0)) < 0.03f,
                    $"({x}, {y}) came back {back.At(x, y, 0)} for {Unit(Field(x, y))}"
                );
            }
        }

        Assert.True(checkedTexels > 30000);

        // And a ring about the centre unrolls into a horizontal band: every row of the polar image is one value.
        static float Rings(int x, int y) {
            var dx = x + 0.5f - Wide / 2f;
            var dy = y + 0.5f - Wide / 2f;

            return 0.5f + 0.4f * MathF.Cos(MathF.Sqrt(dx * dx + dy * dy) * 0.2f);
        }

        var bands = Bake(device, "Space/Cartesian to Polar", Wide, Wide, [new("Input", Grey(Wide, Wide, Rings))]);

        for (var y = 8; y < Wide; y += 16) {
            for (var x = 0; x < Wide; x++) {
                Assert.Equal(bands.At(0, y, 0), bands.At(x, y, 0), 0.03f);
            }
        }
    }

    [Fact]
    public void A_four_corner_warp_is_the_identity_at_rest_and_maps_the_image_into_its_quad() {
        using var device = TextureKernelHarness.Open();

        var rest = Bake(device, "Space/Four-Corner Warp", Side, Side, [new("Input", Unique)]);

        Each((x, y) => Near(UniqueAt(x, y), rest.Texel(x, y), 0.0005f, $"({x}, {y}) at rest"));

        // The middle half: the source's whole square lands on [0.25, 0.75]², twice as small.
        var middle = Bake(
            device,
            "Space/Four-Corner Warp",
            Side,
            Side,
            [new("Input", Grey(Side, Side, static (x, _) => x / 63f))],
            static node => {
                node.SetValue("Top Left X", 0.25f);
                node.SetValue("Top Left Y", 0.25f);
                node.SetValue("Top Right X", 0.75f);
                node.SetValue("Top Right Y", 0.25f);
                node.SetValue("Bottom Right X", 0.75f);
                node.SetValue("Bottom Right Y", 0.75f);
                node.SetValue("Bottom Left X", 0.25f);
                node.SetValue("Bottom Left Y", 0.75f);
            }
        );

        Near((0f, 0f, 0f, 0f), middle.Texel(4, 32), 0.0005f, "outside the quad");
        Near((0f, 0f, 0f, 0f), middle.Texel(32, 60), 0.0005f, "below the quad");

        // Output texel 32 is at u = 0.5078, which the quad sends to s = 0.5156, source texel 32.5.
        Assert.Equal((Unit(32f / 63f) + Unit(33f / 63f)) / 2f, middle.At(32, 32, 0), 0.002f);
        Assert.Equal(1f, middle.At(32, 32, 3), 0.0005f);
    }

    [Fact]
    public void A_rotation_map_of_one_turns_the_image_by_its_angle_and_a_map_of_nought_leaves_it() {
        using var device = TextureKernelHarness.Open();

        var turned = Bake(
            device,
            "Space/Rotate by Map",
            Side,
            Side,
            [new("Input", Unique), new("Rotation", Solid(Side, Side, 1f, 1f, 1f), true)],
            static node => node.SetValue("Angle", MathF.PI / 2f)
        );

        // A quarter turn about the middle of an even-sided image takes texel centres to texel centres:
        // the output at (x, y) is the source at (y, 63 − x).
        Each((x, y) => Near(UniqueAt(y, Side - 1 - x), turned.Texel(x, y), 0.001f, $"({x}, {y}) turned"));

        var still = Bake(
            device,
            "Space/Rotate by Map",
            Side,
            Side,
            [new("Input", Unique), new("Rotation", Solid(Side, Side, 0f, 0f, 0f), true)],
            static node => node.SetValue("Angle", MathF.PI / 2f)
        );

        Each((x, y) => Near(UniqueAt(x, y), still.Texel(x, y), 0.0005f, $"({x}, {y}) unturned"));
    }

    [Fact]
    public void A_swirl_leaves_everything_past_its_radius_and_turns_what_is_inside() {
        using var device = TextureKernelHarness.Open();

        var out_ = Bake(
            device,
            "Space/Swirl",
            Side,
            Side,
            [new("Input", Unique)],
            static node => {
                node.SetValue("Radius", 0.25f);
                node.SetValue("Angle", 2f);
            }
        );
        var inside = 0;

        Each((x, y) => {
                var dx = x + 0.5f - 32f;
                var dy = y + 0.5f - 32f;

                if (MathF.Sqrt(dx * dx + dy * dy) >= 16f) {
                    Near(UniqueAt(x, y), out_.Texel(x, y), 0.0005f, $"({x}, {y}) outside the swirl");
                } else if (MathF.Abs(out_.At(x, y, 0) - UniqueAt(x, y).R) > 0.02f) {
                    inside++;
                }
            }
        );

        Assert.True(inside > 300, $"only {inside} texels inside the swirl moved");
    }

    /// <summary>
    ///     Patina #175's 1:1 crop: the rect's texels copied exactly, its middle on the image's middle, and nothing
    ///     around it — where Stretch spreads the same quarter over the whole image.
    /// </summary>
    [Fact]
    public void An_unscaled_crop_copies_the_rect_texel_for_texel_into_the_middle() {
        using var device = TextureKernelHarness.Open();

        static void Rect(Vixen.NodeGraph.GraphNode node, string fit) {
            node.SetText("Fit", fit);
            node.SetValue("Rect X", 0.125f);
            node.SetValue("Rect Y", 0.25f);
            node.SetValue("Rect W", 0.25f);
            node.SetValue("Rect H", 0.5f);
        }

        var unscaled = Bake(device, "Space/Crop", Side, Side, [new("Input", Unique)], node => Rect(node, "Unscaled"));
        var stretched = Bake(device, "Space/Crop", Side, Side, [new("Input", Unique)], node => Rect(node, "Stretch"));

        // The rect is columns 8 to 23 and rows 16 to 47; its middle (16, 32) lands on the image's (32, 32).
        Each((x, y) => {
                var inside = x is >= 24 and < 40 && y is >= 16 and < 48;

                if (inside) {
                    Near(UniqueAt(x - 16, y), unscaled.Texel(x, y), 0.0005f, $"({x}, {y}) inside the cut-out");
                } else {
                    Near((0f, 0f, 0f, 0f), unscaled.Texel(x, y), 0.0005f, $"({x}, {y}) around the cut-out");
                }
            }
        );

        // Stretched, the same rect fills the image, so its corner texel is the rect's corner.
        Near(UniqueAt(8, 16), stretched.Texel(0, 0), 0.0005f, "the stretched corner");
        Assert.Equal(1f, stretched.Texel(Side - 1, Side - 1).A, 3);
    }

    [Fact]
    public void Wedge_symmetry_repeats_a_quarter_turn_and_mirrors_about_its_first_edge() {
        using var device = TextureKernelHarness.Open();

        var out_ = Bake(
            device,
            "Space/Wedge Symmetry",
            Side,
            Side,
            [new("Input", Unique)],
            static node => node.SetValue("Wedges", 4f)
        );

        // Four wedges about the middle of an even-sided image: a quarter turn is a symmetry, and so is the
        // reflection in the horizontal through the centre, both of which take texels onto texels.
        Each((x, y) => {
                Near(out_.Texel(x, y), out_.Texel(Side - 1 - y, x), 0.002f, $"({x}, {y}) against its quarter turn");
                Near(out_.Texel(x, y), out_.Texel(x, Side - 1 - y), 0.002f, $"({x}, {y}) against its mirror");
            }
        );

        // And the first half-wedge — just below the +x axis — is the source itself.
        Near(UniqueAt(50, 40), out_.Texel(50, 40), 0.002f, "inside the first half-wedge");
    }

    [Fact]
    public void Trimming_finds_the_contents_box_and_stretches_it_or_keeps_its_proportions() {
        using var device = TextureKernelHarness.Open();

        // A white box 16 wide and 32 tall, from (16, 8).
        var box = Grey(Side, Side, static (x, y) => x is >= 16 and < 32 && y is >= 8 and < 40 ? 1f : 0f);
        var stretched = Bake(
            device,
            "Space/Trim to Content",
            Side,
            Side,
            [new("Input", box)],
            static node => node.SetValue("Stretch", 1f)
        );

        for (var y = 2; y < Side - 2; y++) {
            for (var x = 2; x < Side - 2; x++) {
                Assert.Equal(1f, stretched.At(x, y, 0), 0.0005f);
            }
        }

        // Kept in proportion it is grown to 32 wide about its middle, so the outer quarters are black.
        var kept = Bake(device, "Space/Trim to Content", Side, Side, [new("Input", box)]);

        Assert.Equal(0f, kept.At(4, 32, 0), 0.0005f);
        Assert.Equal(1f, kept.At(32, 32, 0), 0.0005f);
        Assert.Equal(0f, kept.At(60, 32, 0), 0.0005f);

        // An image with no content is passed through rather than blown up from nothing.
        var empty = Bake(
            device,
            "Space/Trim to Content",
            Side,
            Side,
            [new("Input", Grey(Side, Side, static (x, _) => x / 63f))],
            static node => node.SetValue("Threshold", 1f)
        );

        Each((x, y) => Assert.Equal(Unit(x / 63f), empty.At(x, y, 0), 0.0005f));
    }

    [Fact]
    public void A_quarter_turn_of_an_image_twice_as_wide_as_tall_keeps_a_circle_round() {
        using var device = TextureKernelHarness.Open();

        const int Wide = 128;
        const int Tall = 64;

        // A disc of radius 20 texels about the middle: turned about its own centre it is itself, if and
        // only if the turn is taken in texels. Turned in the unit square it would come out an ellipse 40
        // texels wide and 10 tall.
        static float Disc(int x, int y) {
            var dx = x + 0.5f - Wide / 2f;
            var dy = y + 0.5f - Tall / 2f;

            return dx * dx + dy * dy < 400f ? 1f : 0f;
        }

        var out_ = Bake(
            device,
            "Space/Transform 2D",
            Wide,
            Tall,
            [new("Input", Grey(Wide, Tall, Disc))],
            static node => {
                node.SetValue("Rotation", MathF.PI / 2f);
                node.SetText("Filter", "Point");

                // Clamped, because a turned 128 × 64 reaches past its own top and bottom, and wrapped those
                // reads come back round into the disc.
                node.SetText("Tiling", "Clamp");
            }
        );

        for (var y = 0; y < Tall; y++) {
            for (var x = 0; x < Wide; x++) {
                Assert.True(
                    MathF.Abs(Disc(x, y) - out_.At(x, y, 0)) < 0.0005f,
                    $"({x}, {y}) is {out_.At(x, y, 0)} and the disc is {Disc(x, y)}"
                );
            }
        }
    }

    /// <summary>
    ///     Patina #174's UV remap: a map of each texel's own centre is the identity, a map holding one coordinate
    ///     everywhere reads that one texel everywhere, and an amount of nought ignores the map.
    /// </summary>
    [Fact]
    public void A_uv_map_says_where_each_texel_reads() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // ⚠ Centres, not corners: (x + ½) / w is where texel x is, and a byte holds 1/255 steps, so the map is a
        // float source rather than a bitmap — a Source/Gradient sweep is exactly (x + ½) / w on red.
        var identity = Bake(
            device,
            "Space/UV Remap",
            Side,
            Side,
            [new("Input", Unique), new("UV", [], Source: "Source/Gradient")],
            tiling: false
        );
        var one = Bake(
            device,
            "Space/UV Remap",
            Side,
            Side,
            [new("Input", Unique), new("UV", Solid(Side, Side, 20.5f / Side, 41.5f / Side, 0f))],
            tiling: false
        );
        var none = Bake(
            device,
            "Space/UV Remap",
            Side,
            Side,
            [new("Input", Unique), new("UV", Solid(Side, Side, 20.5f / Side, 41.5f / Side, 0f))],
            static node => node.SetValue("Amount", 0f),
            tiling: false
        );

        var unique = Bake(device, "Colour/Levels", Side, Side, [new("Input", Unique)], tiling: false);
        // The solid map is bytes, so its coordinate is the nearest 1/255 to the one written; read where that lands.
        var (u, v) = (MathF.Round(20.5f / Side * 255f) / 255f, MathF.Round(41.5f / Side * 255f) / 255f);
        var (tx, ty) = ((int)MathF.Floor(u * Side - 0.5f), (int)MathF.Floor(v * Side - 0.5f));
        var (fx, fy) = (u * Side - 0.5f - tx, v * Side - 0.5f - ty);

        float Expected(int lane) {
            float At(int x, int y) => unique.At(x, y, lane);

            var top = At(tx, ty) + (At(tx + 1, ty) - At(tx, ty)) * fx;
            var bottom = At(tx, ty + 1) + (At(tx + 1, ty + 1) - At(tx, ty + 1)) * fx;

            return top + (bottom - top) * fy;
        }

        for (var y = 0; y < Side; y += 7) {
            for (var x = 0; x < Side; x += 5) {
                for (var lane = 0; lane < 3; lane++) {
                    // The gradient is a grey, read as the same number across and down: the diagonal.
                    Assert.Equal(unique.At(x, x, lane), identity.At(x, y, lane), 0.002f);
                    Assert.Equal(Expected(lane), one.At(x, y, lane), 0.002f);
                    Assert.Equal(unique.At(x, y, lane), none.At(x, y, lane), 0.0005f);
                }
            }
        }
    }
}
