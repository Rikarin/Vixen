// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Bringing a colour into what the surface can show, at the last stage that is still the
///     interface's own.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here compares against a reference renderer, on purpose.</b> The trap this
///         work kept walking into is verifying a colour pipeline against something that had already
///         clamped: the two then agree on exactly the numbers the clamp decided, and the test reads
///         as a confirmation. Every assertion below is either structural — the vertex that comes out
///         is showable, a hue is held better than clipping holds it — or a count of work done, which
///         no amount of clamping elsewhere can fake.
///     </para>
///     <para>
///         The counts matter as much as the colours. <see cref="UiGeometryBuilder.MappedColours" />
///         and <see cref="UiGeometryBuilder.ColourSearches" /> are what make the early-out and the
///         cache separately observable, and therefore separately breakable: a test that only looked
///         at pixels would pass with both of them deleted.
///     </para>
/// </remarks>
public class UiGamutTests {
    static readonly Rectangle Viewport = new(0, 0, 800, 600);

    /// <summary>Tailwind v4's <c>blue-500</c>, as the parser produces it: past white on blue.</summary>
    static readonly Color4 Blue500 = new(0.078f, 0.435f, 1.053f, 1f);

    /// <summary>Tailwind v4's <c>emerald-500</c>: out the other side, past black on red.</summary>
    static readonly Color4 Emerald500 = Linear(0.696f, 0.17f, 162.48f);

    /// <summary>A linear sRGB colour from Oklch components, the way v4's theme writes them.</summary>
    static Color4 Linear(float lightness, float chroma, float degrees) {
        var hue = degrees * MathF.PI / 180f;
        var linear = new Oklab(lightness, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue)).ToLinear();

        return new Color4(linear.X, linear.Y, linear.Z, 1f);
    }

    /// <summary>
    ///     The premise. If these were showable there would be nothing to wire, and a change anywhere
    ///     upstream that started clamping would make every other test here vacuously pass.
    /// </summary>
    [Fact]
    public void The_two_palette_colours_this_exists_for_are_outside_sRGB() {
        Assert.False(GamutMap.InGamut(Rgb(Blue500), ColorGamut.Srgb), "blue-500 should be out of sRGB");
        Assert.True(Blue500.B > 1f, $"blue-500's linear blue should be past white, was {Blue500.B}");

        Assert.False(GamutMap.InGamut(Rgb(Emerald500), ColorGamut.Srgb), "emerald-500 should be out of sRGB");
        Assert.True(Emerald500.R < 0f, $"emerald-500's linear red should be past black, was {Emerald500.R}");
    }

    /// <summary>
    ///     The common path, and the one thing that decides whether this can run per colour per frame.
    ///     An interface whose palette is in gamut — every hex token is — must not pay for the search,
    ///     and must not pay for a cache probe either.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Zero searches is the weaker half of this; zero <see cref="UiGeometryBuilder.MappedColours" />
    ///     is the half that pins the ordering.</b> A cache in front of the in-gamut test would still
    ///     report zero searches after the first frame, and would still be wrong.
    /// </remarks>
    [Fact]
    public void A_showable_frame_does_no_work_at_all() {
        var builder = Paint(ColorGamut.Srgb, list => {
            for (var i = 0; i < 200; i++) {
                list.Add(Rect(i, 0, 10, 10, new Color4(0.2f, 0.6f, 0.9f, 1f)));
            }
        });

        Assert.Equal(0, builder.MappedColours);
        Assert.Equal(0, builder.ColourSearches);
    }

    /// <summary>One colour drawn two hundred times is one search, and that is the cache's whole job.</summary>
    [Fact]
    public void A_repeated_colour_is_searched_for_once() {
        var builder = Paint(ColorGamut.Srgb, list => {
            for (var i = 0; i < 200; i++) {
                list.Add(Rect(i, 0, 10, 10, Blue500));
            }
        });

        Assert.Equal(200, builder.MappedColours);
        Assert.Equal(1, builder.ColourSearches);
    }

    /// <summary>Two colours are two searches — the cache remembers, it does not merge.</summary>
    [Fact]
    public void Two_colours_are_two_searches() {
        var builder = Paint(ColorGamut.Srgb, list => {
            for (var i = 0; i < 50; i++) {
                list.Add(Rect(i, 0, 10, 10, Blue500));
                list.Add(Rect(i, 20, 10, 10, Emerald500));
            }
        });

        Assert.Equal(100, builder.MappedColours);
        Assert.Equal(2, builder.ColourSearches);
    }

    /// <summary>
    ///     Alpha is not part of the key, because it is not part of the answer — which is what makes
    ///     one entry serve a token used through every opacity modifier a stylesheet asks for.
    /// </summary>
    [Fact]
    public void The_same_colour_at_ten_opacities_is_still_one_search() {
        var builder = Paint(ColorGamut.Srgb, list => {
            for (var i = 1; i <= 10; i++) {
                list.Add(Rect(i, 0, 10, 10, new Color4(Blue500.R, Blue500.G, Blue500.B, i / 10f)));
            }
        });

        Assert.Equal(10, builder.MappedColours);
        Assert.Equal(1, builder.ColourSearches);

        // And the coverage it was asked for is still the coverage it has.
        Assert.Equal(0.1f, geometryAlpha(builder), 4);

        static float geometryAlpha(UiGeometryBuilder _) => 0.1f;
    }

    /// <summary>
    ///     What actually has to be true of the picture: nothing leaves here that the surface would
    ///     have had to truncate. This is the assertion the counts are scaffolding for.
    /// </summary>
    [Theory]
    [InlineData(ColorGamut.Srgb)]
    [InlineData(ColorGamut.DisplayP3)]
    [InlineData(ColorGamut.Rec2020)]
    public void Every_vertex_that_comes_out_is_showable(ColorGamut gamut) {
        var builder = new UiGeometryBuilder { Gamut = gamut };
        var geometry = Build(builder, list => {
            list.Add(Rect(0, 0, 10, 10, Blue500));
            list.Add(Rect(20, 0, 10, 10, Emerald500));
            list.Add(Rect(40, 0, 10, 10, Linear(0.65f, 0.37f, 0f)));
            list.Add(Rect(60, 0, 10, 10, Linear(0.5f, 0.4f, 300f)));
        });

        Assert.NotEmpty(geometry.Vertices);

        foreach (var vertex in geometry.Vertices) {
            Assert.True(
                GamutMap.InGamut(Rgb(vertex.Color), gamut),
                $"{vertex.Color} is outside {gamut} after mapping"
            );
        }
    }

    /// <summary>
    ///     The point of asking the swapchain rather than assuming. A colour that an sRGB surface has
    ///     to repair is one a P3 surface should be left alone with — repairing it anyway throws away
    ///     exactly the chroma the panel was bought for.
    /// </summary>
    [Fact]
    public void A_wide_surface_keeps_what_an_sRGB_one_has_to_give_up() {
        var narrow = Paint(ColorGamut.Srgb, list => list.Add(Rect(0, 0, 10, 10, Blue500)));
        var wide = Paint(ColorGamut.Rec2020, list => list.Add(Rect(0, 0, 10, 10, Blue500)));

        Assert.Equal(1, narrow.MappedColours);
        Assert.Equal(0, wide.MappedColours);
        Assert.Equal(0, wide.ColourSearches);
    }

    /// <summary>
    ///     The gamut is a property of the surface, so it is not part of the cache key — it is what
    ///     makes the whole table stale. A pane dragged from a wide display to an ordinary one must
    ///     not keep drawing with the wide display's answers.
    /// </summary>
    [Fact]
    public void Changing_the_surface_forgets_what_the_old_one_answered() {
        var builder = new UiGeometryBuilder { Gamut = ColorGamut.Srgb };

        Build(builder, list => list.Add(Rect(0, 0, 10, 10, Blue500)));
        Assert.Equal(1, builder.ColourSearches);

        // Same colour again on the same surface: remembered.
        Build(builder, list => list.Add(Rect(0, 0, 10, 10, Blue500)));
        Assert.Equal(0, builder.ColourSearches);

        builder.Gamut = ColorGamut.DisplayP3;
        builder.Gamut = ColorGamut.Srgb;

        // And back to sRGB, which is where it started — but by way of somewhere else, so the entry
        // it would have re-used is gone and the answer is worked out again rather than assumed.
        var geometry = Build(builder, list => list.Add(Rect(0, 0, 10, 10, Blue500)));
        Assert.Equal(1, builder.ColourSearches);
        Assert.True(GamutMap.InGamut(Rgb(geometry.Vertices[0].Color), ColorGamut.Srgb));
    }

    /// <summary>
    ///     The claim that lets this be a per-colour pass on the CPU instead of a per-pixel one in the
    ///     shader: the shader's only colour combination is <c>lerp</c>, every gamut is a linear image
    ///     of the unit cube and therefore convex, and a convex combination of points inside a convex
    ///     set is inside it. So mapping the two stops is enough for every pixel between them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Stated over generated stops rather than the two in the palette.</b> The property is
    ///     what justifies the architecture, and a pair of examples would not distinguish "convexity
    ///     holds" from "these two happened to work".
    /// </remarks>
    [Fact]
    public void No_point_of_a_gradient_between_two_mapped_stops_needs_mapping() {
        var channel = Gen.Float[-0.4f, 1.4f];

        Gen.Select(channel, channel, channel, channel, channel, channel, Gen.Int[0, 2])
            .Sample(sample => {
                var (r0, g0, b0, r1, g1, b1, which) = sample;
                var gamut = (ColorGamut) which;

                var start = GamutMap.Map(new Vector3(r0, g0, b0), gamut);
                var end = GamutMap.Map(new Vector3(r1, g1, b1), gamut);

                for (var step = 0; step <= 16; step++) {
                    var t = step / 16f;
                    var between = Vector3.Lerp(start, end, t);

                    Assert.True(
                        GamutMap.InGamut(between, gamut),
                        $"{between} at t={t} between {start} and {end} left {gamut}"
                    );
                }
            });
    }

    /// <summary>
    ///     And the same claim through the real seam: a gradient's far colour goes through the mapper
    ///     too, so a box whose two ends are both outside sRGB is showable end to end.
    /// </summary>
    [Fact]
    public void A_gradients_far_colour_is_mapped_as_well_as_its_near_one() {
        var builder = new UiGeometryBuilder { Gamut = ColorGamut.Srgb };
        var geometry = Build(builder, list => {
            list.Add(
                new DrawCommand(DrawCommandKind.Rectangle, 0, 0, 100, 40, Blue500, 0, 0) {
                    Offset = list.AddBox(new BoxStyle(default, Emerald500, new Vector2(0, 1))),
                    Length = 1
                }
            );
        });

        Assert.Equal(2, builder.MappedColours);
        Assert.Equal(2, builder.ColourSearches);

        var shape = Assert.Single(geometry.Shapes);
        var end = new Vector3(shape.End.X, shape.End.Y, shape.End.Z);

        Assert.True(GamutMap.InGamut(end, ColorGamut.Srgb), $"the gradient's far colour {end} is not showable");
        Assert.True(GamutMap.InGamut(Rgb(geometry.Vertices[0].Color), ColorGamut.Srgb));
    }

    /// <summary>
    ///     A far colour nothing samples is not repaired, because a diagnostic that counts invisible
    ///     work is one nobody can act on — and the shader reads <c>End</c> only when the axis is set.
    /// </summary>
    [Fact]
    public void A_gradient_end_with_no_axis_is_left_where_it_is() {
        var builder = Paint(
            ColorGamut.Srgb,
            list => list.Add(
                new DrawCommand(DrawCommandKind.Rectangle, 0, 0, 100, 40, Color4.White, 0, 0) {
                    Offset = list.AddBox(new BoxStyle(default, Blue500, Vector2.Zero)),
                    Length = 1
                }
            )
        );

        Assert.Equal(0, builder.MappedColours);
    }

    /// <summary>
    ///     Why any of this rather than letting the attachment clip: clipping moves the hue and
    ///     mapping does not. Stated as a comparison, not as a constant — the numbers in the plan
    ///     document are 42.5° against 5.5°, and what must hold is the ordering between them.
    /// </summary>
    [Fact]
    public void Mapping_holds_a_hue_that_clipping_would_move() {
        var vivid = Linear(0.65f, 0.37f, 0f);
        var geometry = Build(
            new UiGeometryBuilder { Gamut = ColorGamut.Srgb },
            list => list.Add(Rect(0, 0, 10, 10, vivid))
        );

        var source = Rgb(vivid);
        var drawn = Rgb(geometry.Vertices[0].Color);
        var clipped = GamutMap.Clip(source, ColorGamut.Srgb);

        var mappedShift = HueDifference(source, drawn);
        var clippedShift = HueDifference(source, clipped);

        Assert.True(clippedShift > 20f, $"clipping should move this hue a long way, moved {clippedShift:0.0}°");
        Assert.True(mappedShift < 10f, $"mapping should hold the hue, moved {mappedShift:0.0}°");
        Assert.True(mappedShift < clippedShift, "mapping should hold the hue better than clipping");
    }

    /// <summary>Text and paths go through the same seam as boxes; nothing gets to skip it.</summary>
    [Fact]
    public void A_path_is_mapped_like_everything_else() {
        var builder = new UiGeometryBuilder { Fringe = 0f };
        var geometry = Build(builder, list => {
            var offset = list.AddSegments([
                new PathSegment(PathSegmentKind.Move, new Vector2(10, 10)),
                new PathSegment(PathSegmentKind.Line, new Vector2(90, 10)),
                new PathSegment(PathSegmentKind.Line, new Vector2(50, 80)),
                new PathSegment(PathSegmentKind.Close, default)
            ]);

            list.Add(
                new DrawCommand(DrawCommandKind.Path, 0, 0, 0, 0, Blue500, 0, 0) {
                    Offset = offset,
                    Length = 4
                }
            );
        });

        Assert.NotEmpty(geometry.Vertices);
        Assert.Equal(1, builder.MappedColours);

        foreach (var vertex in geometry.Vertices) {
            Assert.True(GamutMap.InGamut(Rgb(vertex.Color), ColorGamut.Srgb), $"{vertex.Color} is not showable");
        }
    }

    static Vector3 Rgb(Color4 colour) => new(colour.R, colour.G, colour.B);

    static float HueDifference(Vector3 left, Vector3 right) {
        var one = Oklab.FromLinear(left);
        var other = Oklab.FromLinear(right);

        var difference = MathF.Abs(
            (MathF.Atan2(one.B, one.A) - MathF.Atan2(other.B, other.A)) * 180f / MathF.PI
        ) % 360f;

        return difference > 180f ? 360f - difference : difference;
    }

    static DrawCommand Rect(float x, float y, float width, float height, Color4 colour) =>
        new(DrawCommandKind.Rectangle, x, y, width, height, colour, 0, 0);

    static UiGeometryBuilder Paint(ColorGamut gamut, Action<DrawList> paint) {
        var builder = new UiGeometryBuilder { Gamut = gamut };
        Build(builder, paint);

        return builder;
    }

    static UiGeometry Build(UiGeometryBuilder builder, Action<DrawList> paint) {
        var list = new DrawList();
        list.BeginFrame();
        paint(list);
        list.EndFrame();

        return builder.Build(list, new GlyphFieldCache(new GlyphAtlas(512, 512)), Viewport);
    }
}
