// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #133's decal, #134's warp and #130's and #131's path, baked on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>The fixture is a flat sheet.</b> The <c>position</c> bake runs 0 to 1 across the columns and down the rows
///         at
///         a depth of a half, so a texel's place on the "model" is its own coordinate, and the <c>world</c> bake faces one
///         way everywhere. A decal or a path placed on that sheet then has a closed form: which texels it covers is a
///         rectangle or a band, and which side of the picture they read is a half-plane.
///     </para>
///     <para>
///         ⚠ <b>Every claim is made against a texel the fixture shows is not covered too</b>, so a kernel that covered
///         everything, or nothing, fails rather than passing the half of the test it happens to satisfy.
///     </para>
/// </remarks>
public class TextureSurfaceProjectionDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>The sheet: x across the columns, y down the rows, z a half.</summary>
    static readonly byte[] Sheet = Picture(Side, Side, static (x, y) => (x / 63f, y / 63f, 0.5f, 1f));

    /// <summary>A world normal everywhere, signed as the <c>world</c> bake writes it.</summary>
    static byte[] Facing(float x, float y, float z) =>
        Picture(Side, Side, (_, _) => (x * 0.5f + 0.5f, y * 0.5f + 0.5f, z * 0.5f + 0.5f, 1f));

    /// <summary>A picture whose left half is red and right half blue, so which side was read is a colour.</summary>
    static readonly byte[] Halves = Picture(
        Side,
        Side,
        static (x, _) => x < Side / 2 ? (1f, 0f, 0f, 1f) : (0f, 0f, 1f, 1f)
    );

    /// <summary>A picture whose top half is red and bottom half green, for a ribbon's two sides.</summary>
    static readonly byte[] TopAndBottom = Picture(
        Side,
        Side,
        static (_, y) => y < Side / 2 ? (1f, 0f, 0f, 1f) : (0f, 1f, 0f, 1f)
    );

    Lanes Decal(byte[] picture, byte[] normal, Action<GraphNode>? more = null, byte[]? position = null) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        return Bake(
            device,
            "Space/Decal",
            Side,
            Side,
            [new("Input", picture), new("Position", position ?? Sheet), new("Normal", normal)],
            node => {
                node.SetValue("Centre X", 0.5f);
                node.SetValue("Centre Y", 0.5f);
                node.SetValue("Centre Z", 0.5f);
                node.SetValue("Width", 0.5f);
                node.SetValue("Height", 0.5f);
                node.SetValue("Depth", 0.5f);
                node.SetValue("Fade", 0f);
                more?.Invoke(node);
            },
            tiling: false
        );
    }

    Lanes Path(byte[] picture, byte[] normal, string points, Action<GraphNode>? more = null) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        return Bake(
            device,
            "Space/Surface Path",
            Side,
            Side,
            [new("Input", picture), new("Position", Sheet), new("Normal", normal)],
            node => {
                node.SetText("Points", points);
                node.SetValue("Radius", 0.08f);
                node.SetValue("Hardness", 1f);
                more?.Invoke(node);
            },
            tiling: false
        );
    }

    /// <summary>Where a texel is on the sheet, which is what the decal and the path measure.</summary>
    static float On(int texel) => MathF.Round(texel / 63f * 255f) / 255f;

    /// <summary>A straight path across the middle row, facing out of the sheet.</summary>
    static readonly string Across = "0.1 0.5 0.5 0 0 1 1; 0.9 0.5 0.5 0 0 1 1";

    /// <summary>
    ///     ⚠ The decal covers its box and nothing else: inside, the picture opaque; outside, transparent. The box is a
    ///     half wide around the middle, so the columns and rows that land in it are a closed form.
    /// </summary>
    [Fact]
    public void A_decal_covers_its_box_and_nothing_else() {
        var baked = Decal(Solid(Side, Side, 1f, 0f, 0f), Facing(0f, 0f, 1f));
        int inside = 0, outside = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var covered = MathF.Abs(On(x) - 0.5f) < 0.24f && MathF.Abs(On(y) - 0.5f) < 0.24f;
                var clear = MathF.Abs(On(x) - 0.5f) > 0.26f || MathF.Abs(On(y) - 0.5f) > 0.26f;
                var alpha = baked.At(x, y, 3);

                if (covered) {
                    inside++;
                    Assert.True(
                        alpha > 0.99f && baked.At(x, y, 0) > 0.99f,
                        $"({x}, {y}) is in the box and baked {baked.Texel(x, y)}"
                    );
                } else if (clear) {
                    outside++;
                    Assert.True(alpha < 0.01f, $"({x}, {y}) is outside the box and baked {baked.Texel(x, y)}");
                }
            }
        }

        output.WriteLine($"{inside} texels inside the box, {outside} outside");
        Assert.True(inside > 400 && outside > 2000, "the sheet did not put texels on both sides of the box");
    }

    /// <summary>A face turned away from the decal takes nothing, though it is inside the box: the backface cull.</summary>
    [Fact]
    public void A_face_turned_away_takes_nothing() {
        var front = Decal(Solid(Side, Side, 1f, 0f, 0f), Facing(0f, 0f, 1f));
        var back = Decal(Solid(Side, Side, 1f, 0f, 0f), Facing(0f, 0f, -1f));

        Assert.True(front.At(32, 32, 3) > 0.99f, "the middle of the box faces the decal and took nothing");
        Assert.True(back.At(32, 32, 3) < 0.01f, $"a face turned away took {back.Texel(32, 32)}");
    }

    /// <summary>A texel further than half the depth from the box's middle plane takes nothing.</summary>
    [Fact]
    public void A_texel_past_the_boxs_depth_takes_nothing() {
        var near = Decal(
            Solid(Side, Side, 1f, 0f, 0f),
            Facing(0f, 0f, 1f),
            static node => node.SetValue("Centre Z", 0.7f)
        );
        var far = Decal(
            Solid(Side, Side, 1f, 0f, 0f),
            Facing(0f, 0f, 1f),
            static node => node.SetValue("Centre Z", 0.8f)
        );

        // The sheet is at a half and the box half a quarter deep: 0.2 away is inside it, 0.3 is not.
        Assert.True(near.At(32, 32, 3) > 0.99f, $"a texel 0.2 from the middle plane baked {near.Texel(32, 32)}");
        Assert.True(far.At(32, 32, 3) < 0.01f, $"a texel 0.3 from the middle plane baked {far.Texel(32, 32)}");
    }

    /// <summary>
    ///     ⚠ The picture reads the right way round: seen from the side it faces, its left is on the sheet's left. A decal
    ///     whose right axis came out backwards is a mirrored picture, which is a plausible decal and a wrong one.
    /// </summary>
    [Fact]
    public void A_decal_reads_its_picture_the_right_way_round() {
        var baked = Decal(Halves, Facing(0f, 0f, 1f));

        Assert.True(
            baked.At(24, 32, 0) > 0.99f && baked.At(24, 32, 2) < 0.01f,
            $"left of the middle baked {baked.Texel(24, 32)}"
        );
        Assert.True(
            baked.At(40, 32, 2) > 0.99f && baked.At(40, 32, 0) < 0.01f,
            $"right of the middle baked {baked.Texel(40, 32)}"
        );
    }

    /// <summary>
    ///     A warp whose grid is its own points changes nothing, and one that moves every point a quarter right moves
    ///     the picture a quarter of the decal's width right: the grid is where the picture lands, not where it is read.
    /// </summary>
    [Fact]
    public void A_warp_moves_the_picture_where_its_grid_lands() {
        var plain = Decal(Halves, Facing(0f, 0f, 1f));
        var identity = Decal(
            Halves,
            Facing(0f, 0f, 1f),
            static node => node.SetText("Warp", "0 0 0.5 0 1 0 0 0.5 0.5 0.5 1 0.5 0 1 0.5 1 1 1")
        );

        for (var x = 0; x < Side; x++) {
            Assert.Equal(plain.Texel(x, 32), identity.Texel(x, 32));
        }

        var moved = Decal(
            Halves,
            Facing(0f, 0f, 1f),
            static node => node.SetText(
                "Warp",
                string.Join(
                    ' ',
                    new[] { 0f, 0f, 0.5f, 0f, 1f, 0f, 0f, 0.5f, 0.5f, 0.5f, 1f, 0.5f, 0f, 1f, 0.5f, 1f, 1f, 1f }
                        .Select(static (value, at) => (at % 2 == 0 ? value + 0.25f : value).ToString(
                                "R",
                                CultureInfo.InvariantCulture
                            )
                        )
                )
            )
        );

        // The decal is half the sheet wide, so a quarter of it is an eighth of the sheet: the red-blue edge that was
        // at the middle column is now eight columns right of it, and the strip the picture left on the decal's left
        // is empty.
        Assert.True(
            moved.At(36, 32, 0) > 0.99f,
            $"a quarter of the decal right of the old edge is still red: {moved.Texel(36, 32)}"
        );
        Assert.True(plain.At(36, 32, 2) > 0.99f, "the fixture's edge was not where this test assumes it");
        Assert.True(moved.At(44, 32, 2) > 0.99f, $"past the moved edge is blue: {moved.Texel(44, 32)}");
        Assert.True(moved.At(20, 32, 3) < 0.01f, $"the strip the picture left takes nothing: {moved.Texel(20, 32)}");
        Assert.True(plain.At(20, 32, 3) > 0.99f, "without the warp the same texel was covered");
    }

    /// <summary>A path covers a band its radius wide around it, and nothing further.</summary>
    [Fact]
    public void A_path_covers_its_width_and_nothing_further() {
        var baked = Path(Solid(Side, Side, 1f, 0f, 0f), Facing(0f, 0f, 1f), Across);

        Assert.True(baked.At(32, 32, 3) > 0.99f, $"on the path baked {baked.Texel(32, 32)}");
        Assert.True(
            baked.At(32, 35, 3) > 0.99f,
            $"three rows off the path, inside its width, baked {baked.Texel(32, 35)}"
        );
        Assert.True(
            baked.At(32, 40, 3) < 0.01f,
            $"eight rows off the path, outside its width, baked {baked.Texel(32, 40)}"
        );
        Assert.True(baked.At(0, 32, 3) < 0.01f, $"past the round cap at the path's end baked {baked.Texel(0, 32)}");
    }

    /// <summary>⚠ A face turned away from the path takes nothing: what keeps a path off the far side of a thin wall.</summary>
    [Fact]
    public void A_path_stays_off_a_face_turned_away_from_it() {
        var baked = Path(Solid(Side, Side, 1f, 0f, 0f), Facing(0f, 0f, -1f), Across);

        Assert.True(baked.At(32, 32, 3) < 0.01f, $"a face turned away baked {baked.Texel(32, 32)}");
    }

    /// <summary>A stroke shows its source where the texel is on the atlas.</summary>
    [Fact]
    public void A_stroke_shows_its_source_where_it_lies() {
        var baked = Path(Halves, Facing(0f, 0f, 1f), Across);

        Assert.True(baked.At(20, 32, 0) > 0.99f, $"the left half of the atlas is red: {baked.Texel(20, 32)}");
        Assert.True(baked.At(44, 32, 2) > 0.99f, $"the right half is blue: {baked.Texel(44, 32)}");
    }

    /// <summary>
    ///     ⚠ A ribbon lays its picture across the path: its top on one side and its bottom on the other, whichever half of
    ///     the atlas the texel is in — which is what a stroke cannot do.
    /// </summary>
    [Fact]
    public void A_ribbon_lays_its_picture_across_the_path() {
        var baked = Path(TopAndBottom, Facing(0f, 0f, 1f), Across, static node => node.SetText("Mode", "Ribbon"));
        var above = baked.Texel(32, 35);
        var below = baked.Texel(32, 29);

        output.WriteLine($"rows 35 and 29: {above} and {below}");
        Assert.True(above.A > 0.99f && below.A > 0.99f, "both sides of the path are inside its width");
        Assert.NotEqual(above.R > 0.5f, below.R > 0.5f);
        Assert.NotEqual(above.G > 0.5f, below.G > 0.5f);
    }

    /// <summary>An empty path compiles and covers nothing, rather than refusing a layer that has not been drawn yet.</summary>
    [Fact]
    public void An_empty_path_covers_nothing() {
        var baked = Path(Solid(Side, Side, 1f, 0f, 0f), Facing(0f, 0f, 1f), string.Empty);

        Assert.True(baked.At(32, 32, 3) < 0.01f, $"an empty path baked {baked.Texel(32, 32)}");
    }
}
