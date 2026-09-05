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
    /// <remarks>
    ///     ⚠ <b>This used to be a coin toss, and reading it as an assertion about these two colours
    ///     is what hid that.</b> The table was direct-mapped, so a pair that shared a slot evicted
    ///     each other on every lookup and cost a hundred searches instead of two — and whether these
    ///     two shared one was decided by <c>HashCode</c>'s per-process seed, at about 1 in 256. It
    ///     failed once in five runs and then passed four re-runs, which reads as proof it was
    ///     nothing. The claim is now one the structure guarantees for <em>any</em> pair;
    ///     <see cref="Two_colours_that_share_a_slot_are_still_two_searches" /> is the same claim
    ///     asked of a pair that does collide, which is the half a palette of two nice colours cannot
    ///     reach.
    /// </remarks>
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
    ///     And two colours that land in the <em>same</em> slot are still two searches, because the
    ///     table probes one step rather than evicting.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The pair is constructed, not hoped for.</b> Two colours picked out of a palette sit
    ///     in distinct slots 255 times in 256, so every other count in this file is satisfied by a
    ///     table that throws its neighbour away — that is precisely how the direct-mapped version
    ///     survived here. Sweeping until two swept colours agree on a slot is what makes the
    ///     collision path something this suite actually executes, on every run rather than on the
    ///     unlucky one.
    /// </remarks>
    [Fact]
    public void Two_colours_that_share_a_slot_are_still_two_searches() {
        var (first, second) = ColoursThatShareASlot();

        Assert.Equal(UiGeometryBuilder.HomeSlot(first), UiGeometryBuilder.HomeSlot(second));

        var builder = Paint(ColorGamut.Srgb, list => {
            for (var i = 0; i < 50; i++) {
                list.Add(Rect(i, 0, 10, 10, first));
                list.Add(Rect(i, 20, 10, 10, second));
            }
        });

        Assert.Equal(100, builder.MappedColours);
        Assert.Equal(2, builder.ColourSearches);
    }

    /// <summary>
    ///     Which slot a colour lands in is decided by the colour, and by nothing about the process
    ///     that is drawing it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The number is not the point; that it is the same number tomorrow is.</b> The index
    ///     came from <see cref="HashCode" />, which mixes in a seed generated once per process by
    ///     design — so a table's collisions, and any count of searches that depended on them, were
    ///     re-rolled at every start and could not be reproduced on the run that went wrong. Nothing
    ///     here is reachable by untrusted input, so the seed bought this table no defence at all. If
    ///     a change to the mix makes this fail, update the constant; if no constant can be written
    ///     down, the index is seeded again and that is the defect.
    /// </remarks>
    [Fact]
    public void Which_slot_a_colour_lands_in_does_not_depend_on_the_process() {
        Assert.Equal(122, UiGeometryBuilder.HomeSlot(Blue500));
        Assert.Equal(117, UiGeometryBuilder.HomeSlot(new Color4(1f, 1f, 1f, 1f)));

        // And alpha is not in the key, so it cannot be in the index either.
        Assert.Equal(
            UiGeometryBuilder.HomeSlot(Blue500),
            UiGeometryBuilder.HomeSlot(new Color4(Blue500.R, Blue500.G, Blue500.B, 0.25f))
        );
    }

    /// <summary>
    ///     Alpha is not part of the key, because it is not part of the answer — which is what makes
    ///     one entry serve a token used through every opacity modifier a stylesheet asks for.
    /// </summary>
    [Fact]
    public void The_same_colour_at_ten_opacities_is_still_one_search() {
        var builder = new UiGeometryBuilder { Gamut = ColorGamut.Srgb };
        var geometry = Build(builder, list => {
            for (var i = 1; i <= 10; i++) {
                list.Add(Rect(i, 0, 10, 10, new Color4(Blue500.R, Blue500.G, Blue500.B, i / 10f)));
            }
        });

        Assert.Equal(10, builder.MappedColours);
        Assert.Equal(1, builder.ColourSearches);

        // ⚠ And each one still has the coverage it asked for. Sharing an entry across opacities is
        // only sound if alpha comes back off the *colour* and not off the entry, and a cache that
        // returned its stored alpha would pass every count above while painting ten identical boxes.
        for (var i = 1; i <= 10; i++) {
            Assert.Equal(i / 10f, geometry.Vertices[(i - 1) * 4].Color.A, 4);
        }
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
    ///     Why any of this rather than letting the attachment clip, asked of the vertices that
    ///     actually come out rather than of the mapper: what the surface used to do to an
    ///     out-of-gamut colour was a per-channel clip, and a per-channel clip moves the hue.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Swept over hues, because the worst case is not at a hue anyone would pick.</b> A red
    ///     that is out of gamut along the red axis clips almost straight back down it and barely
    ///     moves — 0.6° at hue 0 — so a single sample can make clipping look harmless. The damage is
    ///     in the hues that run out of one channel well before the others.
    ///     <c>GamutMapTests.Chroma_reduction_holds_hue_where_clipping_does_not</c> makes the same
    ///     measurement on the mapper directly; what is new here is that it survives the seam.
    /// </remarks>
    [Fact]
    public void The_vertices_that_come_out_hold_a_hue_that_clipping_would_move() {
        var worstDrawn = 0f;
        var worstClipped = 0f;

        for (var hue = 0f; hue < 360f; hue += 5f) {
            var vivid = Linear(0.65f, 0.37f, hue);
            var source = Rgb(vivid);

            if (GamutMap.InGamut(source, ColorGamut.Srgb)) {
                continue;
            }

            var geometry = Build(
                new UiGeometryBuilder { Gamut = ColorGamut.Srgb },
                list => list.Add(Rect(0, 0, 10, 10, vivid))
            );

            worstDrawn = MathF.Max(worstDrawn, HueDifference(source, Rgb(geometry.Vertices[0].Color)));
            worstClipped = MathF.Max(worstClipped, HueDifference(source, GamutMap.Clip(source, ColorGamut.Srgb)));
        }

        Assert.True(worstClipped > 20f, $"clipping should shift hue badly, worst was {worstClipped:0.0}°");
        Assert.True(worstDrawn < 10f, $"the drawn vertex should hold hue, worst was {worstDrawn:0.0}°");
        Assert.True(worstDrawn < worstClipped / 4f, $"drawn {worstDrawn:0.0}°, clipped {worstClipped:0.0}°");
    }

    /// <summary>
    ///     Every colour gets its own answer, including when there are far more of them than the
    ///     table has slots and they are landing on top of each other.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The test that says the cache compares its key, and it has to force collisions to do
    ///     it.</b> A fixed-size table only ever returns the wrong colour when colours share a slot,
    ///     so a handful of test colours will sit in distinct slots and a cache that skipped the key
    ///     comparison entirely would pass every other test in this file — it did, when that sabotage
    ///     was tried. A thousand colours over 256 slots makes collisions a certainty rather than a
    ///     hope, and each one is then checked against what the mapper says about <em>its own</em>
    ///     source rather than merely being showable.
    /// </remarks>
    [Fact]
    public void Every_colour_gets_its_own_answer_when_the_table_overflows() {
        var wanted = new List<Color4>();

        for (var lightness = 0.3f; lightness < 0.9f; lightness += 0.05f) {
            for (var hue = 0f; hue < 360f; hue += 4f) {
                var colour = Linear(lightness, 0.35f, hue);

                if (!GamutMap.InGamut(Rgb(colour), ColorGamut.Srgb)) {
                    wanted.Add(colour);
                }
            }
        }

        Assert.True(wanted.Count > 1000, $"needs to overflow 256 slots several times over, had {wanted.Count}");

        var builder = new UiGeometryBuilder { Gamut = ColorGamut.Srgb };
        var geometry = Build(builder, list => {
            for (var i = 0; i < wanted.Count; i++) {
                list.Add(Rect(i % 700, i / 700f, 1, 1, wanted[i]));
            }
        });

        Assert.Equal(wanted.Count, builder.MappedColours);

        for (var i = 0; i < wanted.Count; i++) {
            var expected = GamutMap.Map(Rgb(wanted[i]), ColorGamut.Srgb);
            var drawn = Rgb(geometry.Vertices[i * 4].Color);

            Assert.Equal(expected.X, drawn.X, 5);
            Assert.Equal(expected.Y, drawn.Y, 5);
            Assert.Equal(expected.Z, drawn.Z, 5);
        }
    }

    /// <summary>Text and paths go through the same seam as boxes; nothing gets to skip it.</summary>
    [Fact]
    public void A_path_is_mapped_like_everything_else() {
        var builder = new UiGeometryBuilder { Fringe = 0f };
        var path = new PathBuilder()
            .MoveTo(new Vector2(10, 10))
            .LineTo(new Vector2(90, 10))
            .LineTo(new Vector2(50, 80))
            .Close();

        var geometry = Build(builder, list => list.Add(
            new DrawCommand(DrawCommandKind.Path, 0, 0, 0, 0, Blue500, 0, 0) {
                Offset = list.AddPath(path),
                Length = path.Count,
                FillRule = PathFillRule.NonZero
            }
        ));

        Assert.NotEmpty(geometry.Vertices);
        Assert.Equal(1, builder.MappedColours);

        foreach (var vertex in geometry.Vertices) {
            Assert.True(GamutMap.InGamut(Rgb(vertex.Color), ColorGamut.Srgb), $"{vertex.Color} is not showable");
        }
    }

    static Vector3 Rgb(Color4 colour) => new(colour.R, colour.G, colour.B);

    /// <summary>
    ///     Two out-of-gamut colours that the cache indexes to the same slot, found by sweeping rather
    ///     than named — the mix may change, and what the test needs is a collision, not a constant.
    /// </summary>
    static (Color4 First, Color4 Second) ColoursThatShareASlot() {
        var seen = new Dictionary<int, Color4>();

        for (var lightness = 0.3f; lightness < 0.9f; lightness += 0.01f) {
            for (var hue = 0f; hue < 360f; hue += 0.25f) {
                var colour = Linear(lightness, 0.35f, hue);

                if (GamutMap.InGamut(Rgb(colour), ColorGamut.Srgb)) {
                    continue;
                }

                var slot = UiGeometryBuilder.HomeSlot(colour);

                if (seen.TryGetValue(slot, out var other) && Rgb(other) != Rgb(colour)) {
                    return (other, colour);
                }

                seen[slot] = colour;
            }
        }

        // A sweep this wide produces thousands of out-of-gamut colours over 256 slots, so failing to
        // find a pair does not mean the hash is good — it means this helper stopped looking.
        throw new InvalidOperationException("no two of the swept colours share a slot");
    }

    static float HueDifference(Vector3 left, Vector3 right) {
        var one = Oklab.FromLinear(left);
        var other = Oklab.FromLinear(right);

        var difference = MathF.Abs(
            (MathF.Atan2(one.B, one.A) - MathF.Atan2(other.B, other.A)) * 180f / MathF.PI
        ) % 360f;

        return difference > 180f ? 360f - difference : difference;
    }

    /// <summary>
    ///     The luminance half of the same handover: a colour leaves in the target's units, and its
    ///     coverage does not, because coverage is a fraction and not a luminance.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What this prints on the day the scale is dropped.</b> Nothing, if it only asserted
    ///     that the colour is showable or that the frame drew — a UI at one candela in a cd/m² pass
    ///     is pixel-identical to a pass that never ran, which is this repository's standing
    ///     photometric trap and the whole of #670. So the assertion is the product itself, and the
    ///     default is asserted beside it: one is what every SDR swapchain wants, so a change of
    ///     default would silently re-light every window in the tree.
    /// </remarks>
    [Fact]
    public void A_white_level_scales_what_the_surface_can_already_show_and_not_its_coverage() {
        Assert.Equal(1f, new UiGeometryBuilder().WhiteLevel);

        var colour = new Color4(0.25f, 0.5f, 0.75f, 0.5f);
        var builder = new UiGeometryBuilder { Gamut = ColorGamut.Srgb, WhiteLevel = 203f };
        var geometry = Build(builder, list => list.Add(Rect(0, 0, 10, 10, colour)));

        Assert.Equal(0, builder.MappedColours);

        var vertex = geometry.Vertices[0].Color;

        Assert.Equal(0.25f * 203f, vertex.R, 3);
        Assert.Equal(0.5f * 203f, vertex.G, 3);
        Assert.Equal(0.75f * 203f, vertex.B, 3);
        Assert.Equal(0.5f, vertex.A);
    }

    /// <summary>
    ///     A repaired colour is scaled too, and the answer the cache remembered is scaled on the way
    ///     out rather than stored scaled.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The second frame is the one that matters.</b> There are three ways out of
    ///     <c>Show</c> — in gamut, remembered, freshly mapped — and a scale applied to one of them
    ///     leaves an interface that is right until it stops changing, which is the worst shape a
    ///     rendering bug can take. This drives the same colour twice through one builder, so the
    ///     second build is the cached path by construction, and it changes the white level between
    ///     the two: a table that had stored the scaled value would return the first frame's
    ///     luminance for ever.
    /// </remarks>
    [Fact]
    public void A_repaired_colour_scales_on_every_path_out_including_the_remembered_one() {
        var builder = new UiGeometryBuilder { Gamut = ColorGamut.Srgb };
        var display = Build(builder, list => list.Add(Rect(0, 0, 10, 10, Blue500))).Vertices[0].Color;

        Assert.Equal(1, builder.MappedColours);
        Assert.Equal(1, builder.ColourSearches);

        builder.WhiteLevel = 203f;
        var lit = Build(builder, list => list.Add(Rect(0, 0, 10, 10, Blue500))).Vertices[0].Color;

        // The repair itself was not repeated — this is the remembered path — and the answer still
        // arrives in the new units.
        Assert.Equal(0, builder.ColourSearches);
        Assert.Equal(display.R * 203f, lit.R, 3);
        Assert.Equal(display.G * 203f, lit.G, 3);
        Assert.Equal(display.B * 203f, lit.B, 3);
        Assert.Equal(display.A, lit.A);
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
