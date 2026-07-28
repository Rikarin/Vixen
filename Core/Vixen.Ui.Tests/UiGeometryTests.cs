// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     The last step that is still the interface's own: a draw list, in; vertices, out.
/// </summary>
/// <remarks>
///     A pure function of a draw list, which is what lets all of it be checked without a device. What
///     a golden image would add is whether the shader agrees, and that is a separate gate.
/// </remarks>
public class UiGeometryTests {
    static readonly Rectangle Viewport = new(0, 0, 800, 600);

    [Fact]
    public void A_rectangle_becomes_one_quad() {
        var geometry = Build(list => list.Add(Rect(10, 20, 100, 40)));

        Assert.Equal(4, geometry.Vertices.Count);
        Assert.Equal(6, geometry.Indices.Count);

        var draw = Assert.Single(geometry.Draws);
        Assert.Equal(BatchKind.Geometry, draw.Kind);
        Assert.Equal(6, draw.Count);

        // The corners, in document pixels.
        Assert.Equal(new Vector2(10, 20), geometry.Vertices[0].Position);
        Assert.Equal(new Vector2(110, 60), geometry.Vertices[2].Position);
    }

    [Fact]
    public void A_shadows_quad_is_grown_so_the_blur_lands_inside_it() {
        // ⚠ Twice the blur, not once. The coverage reaches zero a blur radius out from the boundary,
        // but the falloff is centred on it — so the visible tail runs a blur *beyond* where the edge
        // itself sits. One blur of margin leaves a faint straight line where the quad ends, which
        // reads as a crease in the shadow rather than as a geometry mistake.
        var geometry = Build(list => list.Add(Shadow(100, 100, 80, 40, blur: 6)));

        Assert.Equal(4, geometry.Vertices.Count);
        Assert.Equal(new Vector2(88, 88), geometry.Vertices[0].Position);
        Assert.Equal(new Vector2(192, 152), geometry.Vertices[2].Position);

        // The texture coordinate is the offset from the centre, and it has to grow with the quad or
        // the distance field would be evaluated for a box the shader thinks is bigger than it is.
        Assert.Equal(new Vector2(-52, -32), geometry.Vertices[0].Texture);
    }

    [Fact]
    public void A_shadow_carries_its_blur_and_no_border() {
        var geometry = Build(list => list.Add(Shadow(0, 0, 80, 40, blur: 6, radius: 4)));
        var shape = Assert.Single(geometry.Shapes);

        // Half size is the *box's*, not the grown quad's — the shadow is the same shape as the thing
        // that cast it, drawn on a larger canvas.
        Assert.Equal(40f, shape.Size.X, 3);
        Assert.Equal(20f, shape.Size.Y, 3);

        // Thickness zero, or the border band would hollow the shadow out into a soft outline.
        Assert.Equal(0f, shape.Size.Z, 3);
        Assert.Equal(6f, shape.Axis.Z, 3);
    }

    [Fact]
    public void A_shadow_batches_with_the_boxes_around_it() {
        // Same pipeline, same distance field, a blur instead of a border — so a card's shadow and
        // its background are one draw rather than three.
        var geometry = Build(list => {
            list.Add(Shadow(0, 0, 80, 40, blur: 4));
            list.Add(Rect(0, 0, 80, 40));
        });

        Assert.Single(geometry.Draws);
        Assert.Equal(BatchKind.Geometry, geometry.Draws[0].Kind);
    }

    /// <summary>
    ///     ⚠ A rounded corner is a signed distance the shader evaluates, so the radius reaches it as a
    ///     parameter rather than turning into geometry. Tessellating one costs vertices in proportion
    ///     to the radius and is still faceted.
    /// </summary>
    [Fact]
    public void A_corner_radius_reaches_the_shader_rather_than_becoming_geometry() {
        var geometry = Build(list => list.Add(Rect(0, 0, 80, 40, radius: 12)));

        Assert.Equal(4, geometry.Vertices.Count);

        var shape = Assert.Single(geometry.Shapes);
        Assert.Equal(40, shape.Size.X, 3);      // half width
        Assert.Equal(20, shape.Size.Y, 3);      // half height
        Assert.Equal(0, shape.Size.Z, 3);       // a fill, not a border
        Assert.Equal(new Vector4(12, 12, 12, 12), shape.RadiiX);
    }

    [Fact]
    public void A_border_carries_its_thickness_and_shares_the_geometry_batch() {
        var geometry = Build(list => list.Add(Border(0, 0, 80, 40, thickness: 3)));

        Assert.Equal(BatchKind.Geometry, Assert.Single(geometry.Draws).Kind);
        Assert.Equal(3, Assert.Single(geometry.Shapes).Size.Z, 3);
    }

    /// <summary>
    ///     ⚠ The texture coordinate of a box is the offset from its centre, which is the space a
    ///     signed distance to a rounded box is written in — so the shader needs no uniform per box.
    /// </summary>
    [Fact]
    public void A_box_is_parameterised_from_its_own_centre() {
        var geometry = Build(list => list.Add(Rect(500, 300, 80, 40)));

        Assert.Equal(new Vector2(-40, -20), geometry.Vertices[0].Texture);
        Assert.Equal(new Vector2(40, 20), geometry.Vertices[2].Texture);
    }

    [Fact]
    public void A_zero_sized_rectangle_emits_nothing() {
        var geometry = Build(list => list.Add(Rect(10, 10, 0, 40)));

        Assert.Empty(geometry.Vertices);
        Assert.Empty(geometry.Draws);
    }

    // ------------------------------------------------------------ Clipping

    /// <summary>
    ///     ⚠ <b>Resolved here, not replayed.</b> A draw list pushes and pops; a renderer sets a
    ///     scissor. Carrying the rectangle on the draw means the renderer holds no stack and cannot
    ///     be caught out by a batch it skipped having left one behind.
    /// </summary>
    [Fact]
    public void A_clip_arrives_on_the_draw_rather_than_as_a_command() {
        var geometry = Build(list => {
            list.Add(ClipPush(100, 100, 200, 200));
            list.Add(Rect(0, 0, 800, 600));
            list.Add(ClipPop());
        });

        var draw = Assert.Single(geometry.Draws);
        Assert.Equal(new Rectangle(100, 100, 200, 200), draw.Clip);
        Assert.DoesNotContain(geometry.Draws, one => one.Kind == BatchKind.Clip);
    }

    /// <summary>
    ///     ⚠ A nested clip intersects rather than replaces. Setting the scissor outright would let a
    ///     child draw outside the panel that contains it, which is the one thing a clip prevents.
    /// </summary>
    [Fact]
    public void A_clip_inside_a_clip_is_the_smaller_of_the_two() {
        var geometry = Build(list => {
            list.Add(ClipPush(100, 100, 200, 200));
            list.Add(ClipPush(150, 50, 400, 400));
            list.Add(Rect(0, 0, 800, 600));
            list.Add(ClipPop());
            list.Add(ClipPop());
        });

        // x: [150, 300), y: [100, 300) — the overlap, not the inner rectangle.
        Assert.Equal(new Rectangle(150, 100, 150, 200), Assert.Single(geometry.Draws).Clip);
    }

    [Fact]
    public void A_clip_that_is_popped_stops_applying() {
        var geometry = Build(list => {
            list.Add(ClipPush(100, 100, 200, 200));
            list.Add(Rect(0, 0, 10, 10));
            list.Add(ClipPop());
            list.Add(Rect(20, 20, 10, 10));
        });

        Assert.Equal(2, geometry.Draws.Count);
        Assert.Equal(new Rectangle(100, 100, 200, 200), geometry.Draws[0].Clip);
        Assert.Equal(Viewport, geometry.Draws[1].Clip);
    }

    // ------------------------------------------------------------ Text

    [Fact]
    public void A_glyph_run_becomes_one_quad_per_drawn_glyph() {
        var (geometry, _) = BuildText("ab");

        var draw = Assert.Single(geometry.Draws);
        Assert.Equal(BatchKind.Text, draw.Kind);
        Assert.Equal(2 * 6, draw.Count);
        Assert.Equal(2 * 4, geometry.Vertices.Count);
    }

    /// <summary>
    ///     ⚠ <b>A glyph's quad grows with the font size and its atlas coordinates do not.</b> The
    ///     placement is in ems and the pen is in pixels, so the size multiplies one and not the
    ///     other — the mistake is invisible at whatever size somebody first tried.
    /// </summary>
    [Fact]
    public void Drawing_at_twice_the_size_doubles_the_quad_and_moves_no_texture_coordinate() {
        var (small, _) = BuildText("a", size: 16);
        var (large, _) = BuildText("a", size: 32);

        var smallWidth = small.Vertices[1].Position.X - small.Vertices[0].Position.X;
        var largeWidth = large.Vertices[1].Position.X - large.Vertices[0].Position.X;

        Assert.Equal(smallWidth * 2, largeWidth, 3);
        Assert.Equal(small.Vertices[0].Texture, large.Vertices[0].Texture);
        Assert.Equal(small.Vertices[2].Texture, large.Vertices[2].Texture);
    }

    /// <summary>
    ///     ⚠ The range a shader thresholds against scales with the size too, or text blurs as it
    ///     grows and aliases as it shrinks.
    /// </summary>
    [Fact]
    public void The_threshold_range_scales_with_the_size_as_well() {
        var (small, _) = BuildText("a", size: 16);
        var (large, _) = BuildText("a", size: 32);

        Assert.Equal(small.Vertices[0].Shape.X * 2, large.Vertices[0].Shape.X, 2);
    }

    /// <summary>
    ///     ⚠ A font's y runs up from the baseline and a surface's runs down, so a glyph's top edge is
    ///     a subtraction. Getting it the other way round draws every line of text upside down about
    ///     its own baseline, which reads as the text being in the wrong place rather than mirrored.
    /// </summary>
    [Fact]
    public void A_glyph_sits_above_its_baseline() {
        var (geometry, _) = BuildText("a", size: 32);

        // The baseline is at RunY in the fixture, and a letter's box is above it.
        Assert.True(geometry.Vertices[0].Position.Y < RunY, "the glyph's top is below its baseline");
    }

    [Fact]
    public void A_space_draws_nothing_and_is_not_counted_as_dropped() {
        var (geometry, builder) = BuildText(" ");

        Assert.Empty(geometry.Draws);
        Assert.Equal(0, builder.DroppedGlyphs);
    }

    /// <summary>
    ///     ⚠ A glyph the atlas cannot hold is counted rather than thrown on — a misconfigured atlas
    ///     is not a reason for a frame to fail — but it must not be silent, because the symptom is a
    ///     word with a hole in it.
    /// </summary>
    [Fact]
    public void A_glyph_the_atlas_cannot_hold_is_counted() {
        var atlas = new GlyphAtlas(8, 8);
        var (geometry, builder) = BuildText("a", cache: new GlyphFieldCache(atlas, resolution: 64));

        Assert.Empty(geometry.Draws);
        Assert.Equal(1, builder.DroppedGlyphs);
    }

    // ------------------------------------------------------- Resolving before emitting

    /// <summary>
    ///     ⚠ <b>Packing a glyph and reading a region out of the atlas cannot be interleaved.</b>
    ///     Adding one can repack the whole texture, and a repack changes <i>every</i> region — so a
    ///     glyph added partway through a run silently moves the glyphs whose quads are already
    ///     written, and what draws is the right letters read out of the wrong places. This is that
    ///     shape: an atlas too small to hold the run comfortably, so the packer is working while the
    ///     run is being emitted.
    /// </summary>
    /// <remarks>
    ///     The numbers are pinned rather than incidental. Sabotaged by removing the resolve pass,
    ///     this configuration and twelve others in the same sweep come out with quads pointing at
    ///     where their glyphs used to be.
    /// </remarks>
    [Fact]
    public void A_run_drawn_while_the_atlas_is_packing_still_points_at_where_its_glyphs_ended_up() {
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64), resolution: 32);
        var run = Drawable(6);

        var (geometry, _) = BuildGlyphs(run, cache);

        AssertCoordinatesAgree(geometry, cache, run);
    }

    /// <summary>
    ///     The contract the flag makes, over a sweep rather than one case: <b>a frame that did not
    ///     repack while it was being emitted has texture coordinates that can be believed.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The converse is not claimed and is not true.</b> A frame needing more distinct
    ///     glyphs at once than the atlas holds evicts, during resolving, what it is about to draw —
    ///     so emission puts them back and can repack while doing it. That is what
    ///     <c>AtlasChanged</c> is for, and it is why this asserts nothing about the frames it flags.
    /// </remarks>
    [Theory]
    [InlineData(48, 12)]
    [InlineData(64, 20)]
    [InlineData(96, 32)]
    [InlineData(128, 32)]
    [InlineData(192, 32)]
    public void A_frame_the_atlas_did_not_change_under_has_coordinates_that_agree_with_the_atlas(int side, int resolution) {
        foreach (var count in new[] { 2, 4, 6, 8, 10, 12 }) {
            var cache = new GlyphFieldCache(new GlyphAtlas(side, side), resolution);
            var run = Drawable(count);

            var (geometry, builder) = BuildGlyphs(run, cache);

            if (builder.AtlasChanged) {
                continue;
            }

            AssertCoordinatesAgree(geometry, cache, run);
        }
    }

    /// <summary>
    ///     ⚠ <b>An atlas with room does not repack at all</b>, which is what makes the flag a
    ///     report of a misconfiguration rather than a thing an ordinary frame trips over. A sabotage
    ///     that raised it unconditionally would be caught here rather than by the sweep above, which
    ///     skips the frames it flags and would pass with everything flagged.
    /// </summary>
    [Fact]
    public void A_frame_the_atlas_has_room_for_does_not_change_it() {
        var cache = new GlyphFieldCache(new GlyphAtlas(512, 512));
        var (_, builder) = BuildGlyphs(Drawable(12), cache);

        Assert.False(builder.AtlasChanged);
        Assert.Equal(0, cache.Atlas.Evictions);
        Assert.Equal(12, cache.Atlas.Count);
    }

    /// <summary>Every text quad reads the region the atlas holds for its glyph now.</summary>
    static void AssertCoordinatesAgree(UiGeometry geometry, GlyphFieldCache cache, List<ushort> run) {
        var font = Font();
        var vertex = 0;

        foreach (var glyph in run) {
            Assert.True(cache.TryGet(font, 0, glyph, out var entry), $"glyph {glyph} is not in the atlas");
            Assert.True(vertex < geometry.Vertices.Count, $"no quad was emitted for glyph {glyph}");

            // The top-left corner is enough: all four are derived from the one region.
            Assert.Equal((float) entry.Region.X / cache.Atlas.Width, geometry.Vertices[vertex].Texture.X, 5);
            Assert.Equal((float) entry.Region.Y / cache.Atlas.Height, geometry.Vertices[vertex].Texture.Y, 5);

            vertex += 4;
        }
    }

    /// <summary>The first <paramref name="wanted" /> glyphs of the test font that draw something.</summary>
    /// <remarks>
    ///     ⚠ Glyph ids rather than characters, because what these tests need is a known number of
    ///     <i>distinct fields</i> in the atlas — and a string is not that: the test font maps several
    ///     of the obvious letters to nothing, so "abcdef" is however many glyphs it happens to have.
    /// </remarks>
    static List<ushort> Drawable(int wanted) {
        var font = Font();
        var found = new List<ushort>();

        for (ushort glyph = 1; glyph < font.GlyphCount && found.Count < wanted; glyph++) {
            var outline = font.GetOutline(glyph);

            if (outline.IsEmpty) {
                continue;
            }

            var bounds = outline.Bounds();

            if (bounds.Width > 0 && bounds.Height > 0) {
                found.Add(glyph);
            }
        }

        Assert.Equal(wanted, found.Count);
        return found;
    }

    /// <summary>
    ///     ⚠ <b>A glyph's position is an offset along its run, not a place on the surface.</b> The
    ///     command carries where the line starts, so two identical labels in different places hold
    ///     identical glyph runs — which is what lets the batcher and the frame diff notice they are
    ///     the same. Reading the offset as absolute puts every label wherever the first one was, and
    ///     the fixture that found it had its run at the origin, where the two are the same thing.
    /// </summary>
    [Fact]
    public void A_run_is_drawn_where_its_command_says_and_not_at_the_origin() {
        var (here, _) = BuildText("a");
        var (there, _) = BuildText("a", originX: RunX + 250);

        Assert.Equal(250, there.Vertices[0].Position.X - here.Vertices[0].Position.X, 3);
        Assert.Equal(here.Vertices[0].Position.Y, there.Vertices[0].Position.Y, 3);

        // ...and the same glyph, so nothing about the atlas changed with it.
        Assert.Equal(here.Vertices[0].Texture, there.Vertices[0].Texture);
    }

    // ------------------------------------------------------------ Order

    /// <summary>
    ///     ⚠ <b>Painting order survives, because it is the only answer to what is in front.</b> A
    ///     user interface has no depth buffer, so a builder that grouped the two rectangles together
    ///     would draw them over the text that was meant to cover one of them.
    /// </summary>
    [Fact]
    public void Interleaved_kinds_keep_their_order() {
        var (geometry, _) = BuildText("a", before: list => list.Add(Rect(0, 0, 10, 10)),
            after: list => list.Add(Rect(20, 0, 10, 10)));

        Assert.Equal(3, geometry.Draws.Count);
        Assert.Equal(BatchKind.Geometry, geometry.Draws[0].Kind);
        Assert.Equal(BatchKind.Text, geometry.Draws[1].Kind);
        Assert.Equal(BatchKind.Geometry, geometry.Draws[2].Kind);

        // ...and each draw names its own slice of one index buffer, in order.
        Assert.Equal(0, geometry.Draws[0].First);
        Assert.Equal(6, geometry.Draws[1].First);
        Assert.Equal(12, geometry.Draws[2].First);
    }

    [Fact]
    public void Every_index_names_a_vertex_that_exists() {
        var (geometry, _) = BuildText("abc", before: list => list.Add(Rect(0, 0, 10, 10)));

        Assert.All(geometry.Indices, index => Assert.InRange(index, 0u, (uint)(geometry.Vertices.Count - 1)));

        // Two triangles per quad, and nothing left over.
        Assert.Equal(0, geometry.Indices.Count % 6);
        Assert.Equal(geometry.Vertices.Count / 4 * 6, geometry.Indices.Count);
    }

    /// <summary>
    ///     ⚠ Past what a 16-bit index reaches, and nothing is dropped and nothing wraps.
    /// </summary>
    /// <remarks>
    ///     This is the whole of what widening the index bought, so it is what the test asserts: at
    ///     20 000 quads — 80 000 vertices, comfortably past 65 535 — every quad is still there, and
    ///     the last one's indices name the last four vertices rather than four near the beginning.
    ///     Under a <c>ushort</c> the builder refused, and the count assertion is what catches that;
    ///     under a <c>ushort</c> that did not refuse, the last-quad assertion is what catches the
    ///     wrap, which draws the top of the frame in the middle of it.
    /// </remarks>
    [Fact]
    public void A_frame_past_sixteen_bits_of_vertices_keeps_all_of_them() {
        const int Quads = 20_000;

        var geometry = Build(list => {
                for (var i = 0; i < Quads; i++) {
                    list.Add(Rect(i % 200 * 4, i / 200 * 4, 3, 3));
                }
            }
        );

        Assert.Equal(Quads * 4, geometry.Vertices.Count);
        Assert.Equal(Quads * 6, geometry.Indices.Count);

        var last = (uint)((Quads - 1) * 4);
        Assert.Equal(last, geometry.Indices[^6]);
        Assert.Equal(last + 3, geometry.Indices[^1]);
    }

    /// <summary>
    ///     ⚠ The draws partition the index buffer: every index is in exactly one draw, in order, so
    ///     a renderer walks the draws alone and cannot miss anything.
    /// </summary>
    [Fact]
    public void The_draws_cover_the_index_buffer_without_gap_or_overlap() {
        var (geometry, _) = BuildText("ab", before: list => list.Add(Rect(0, 0, 10, 10)),
            after: list => list.Add(Rect(20, 0, 10, 10)));

        var next = 0;
        foreach (var draw in geometry.Draws) {
            Assert.Equal(next, draw.First);
            next += draw.Count;
        }

        Assert.Equal(geometry.Indices.Count, next);
    }

    // -------------------------------------------------------------- Shapes

    /// <summary>
    ///     ⚠ A box's parameters go in a record and the vertex carries its index. Four elliptical
    ///     corners and a gradient are fourteen floats; on the vertex they would take it from
    ///     forty-eight bytes to a hundred and four, and every glyph in the frame would carry fields
    ///     no shader reads on them.
    /// </summary>
    [Fact]
    public void A_box_gets_one_record_and_its_vertices_carry_the_index() {
        var geometry = Build(list => {
                list.Add(Rect(0, 0, 80, 40, radius: 12));
                list.Add(Rect(100, 0, 60, 30));
            }
        );

        Assert.Equal(2, geometry.Shapes.Count);

        // All four corners of a quad name the same record, and the second box names the second.
        Assert.All(geometry.Vertices.Take(4), vertex => Assert.Equal(0f, vertex.Shape.X));
        Assert.All(geometry.Vertices.Skip(4), vertex => Assert.Equal(1f, vertex.Shape.X));

        Assert.Equal(new Vector4(40, 20, 0, 0), geometry.Shapes[0].Size);
        Assert.Equal(new Vector4(30, 15, 0, 0), geometry.Shapes[1].Size);
    }

    /// <summary>
    ///     ⚠ A plain box's uniform radius is written out as four equal corners rather than kept as a
    ///     second path through the shader. One shape of parameters means one branch fewer per pixel
    ///     and one fewer thing that can disagree with itself.
    /// </summary>
    [Fact]
    public void A_uniform_radius_becomes_four_equal_corners() {
        var shape = Assert.Single(Build(list => list.Add(Rect(0, 0, 80, 40, radius: 12))).Shapes);

        Assert.Equal(new Vector4(12, 12, 12, 12), shape.RadiiX);
        Assert.Equal(new Vector4(12, 12, 12, 12), shape.RadiiY);
    }

    /// <summary>
    ///     ⚠ Clockwise from the top left, and elliptical. Anticlockwise, or starting elsewhere, still
    ///     draws a plausible rounded rectangle with the wrong corner rounded — which is a mistake that
    ///     survives a review, so the order is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void The_corners_reach_the_record_clockwise_from_the_top_left() {
        var corners = new CornerRadii(
            new Vector2(1, 2),
            new Vector2(3, 4),
            new Vector2(5, 6),
            new Vector2(7, 8)
        );

        var shape = Assert.Single(Build(list => Styled(list, corners)).Shapes);

        Assert.Equal(new Vector4(1, 3, 5, 7), shape.RadiiX);
        Assert.Equal(new Vector4(2, 4, 6, 8), shape.RadiiY);
    }

    /// <summary>
    ///     ⚠ A zero axis is the sentinel for "no gradient". A gradient along no direction is not one
    ///     anybody can mean, so the value is free — and a separate flag would be a second thing to
    ///     keep in step and a state, flag set and axis zero, that has no meaning.
    /// </summary>
    [Fact]
    public void A_gradient_is_flagged_by_its_axis_and_nothing_else() {
        var flat = Assert.Single(Build(list => Styled(list, CornerRadii.Uniform(4))).Shapes);
        Assert.Equal(0f, flat.Size.W);

        var graded = Assert.Single(
            Build(list => Styled(list, CornerRadii.Uniform(4), BoxStyle.Vertical(Color4.Red).GradientEnd, new Vector2(0, 1)))
                .Shapes
        );

        Assert.Equal(1f, graded.Size.W);
        Assert.Equal(new Vector4(0, 1, 0, 0), graded.Axis);
        Assert.Equal(new Vector4(Color4.Red.R, Color4.Red.G, Color4.Red.B, Color4.Red.A), graded.End);
    }

    [Fact]
    public void Text_and_paths_add_no_records() {
        var (text, _) = BuildText("ab");
        Assert.Empty(text.Shapes);

        Assert.Empty(Build(list => Fill(list, Circle(), Color4.Red)).Shapes);
    }

    /// <summary>
    ///     ⚠ The frame diff has to read the box side buffer too. A button whose gradient is being
    ///     animated emits the same command over the same range every frame and moves only the end
    ///     colour, so a diff that read the commands alone would report the frame unchanged and keep
    ///     drawing the old gradient.
    /// </summary>
    [Fact]
    public void A_gradient_that_changes_changes_the_frame() {
        var list = new DrawList();

        list.BeginFrame();
        Styled(list, CornerRadii.Uniform(4), Color4.Red, new Vector2(0, 1));
        Assert.True(list.EndFrame());

        list.BeginFrame();
        Styled(list, CornerRadii.Uniform(4), Color4.Red, new Vector2(0, 1));
        Assert.False(list.EndFrame());

        list.BeginFrame();
        Styled(list, CornerRadii.Uniform(4), Color4.Blue, new Vector2(0, 1));
        Assert.True(list.EndFrame());
    }

    // -------------------------------------------------------------- Paths

    /// <summary>
    ///     ⚠ A path is the one kind that becomes real geometry. A box and a glyph are a distance
    ///     function the shader evaluates, so they cost four vertices whatever their shape; a path has
    ///     no such function, so it is tessellated — and the vertex count follows the shape rather
    ///     than being one per primitive.
    /// </summary>
    [Fact]
    public void A_filled_path_becomes_triangles_rather_than_a_quad() {
        var geometry = Build(list => Fill(list, Circle(), Color4.Red));

        var draw = Assert.Single(geometry.Draws);
        Assert.Equal(BatchKind.PathFill, draw.Kind);

        // Loose triangles: three vertices and three indices each, and a great many more than four.
        Assert.Equal(0, geometry.Indices.Count % 3);
        Assert.Equal(geometry.Vertices.Count, geometry.Indices.Count);
        Assert.True(geometry.Vertices.Count > 40, $"only {geometry.Vertices.Count} vertices");

        Assert.All(geometry.Vertices, vertex => Assert.Equal(Color4.Red, vertex.Color));
    }

    /// <summary>
    ///     ⚠ A path's vertex carries a coverage where the other two kinds carry a distance, and
    ///     nothing else. Left stale, the box shader would read <c>Shape</c> as a half-size and a
    ///     radius, and a path drawn by the wrong pipeline would be a rounded rectangle somewhere else
    ///     rather than nothing at all — which is much harder to see.
    /// </summary>
    [Fact]
    public void A_path_vertex_carries_a_coverage_and_nothing_else() {
        var geometry = Build(list => {
                list.Add(Rect(0, 0, 80, 40, radius: 12));
                Fill(list, Circle(), Color4.Red);
            }
        );

        var path = geometry.Vertices.Skip(4).ToList();

        Assert.NotEmpty(path);
        Assert.All(path, vertex => Assert.Equal(Vector2.Zero, vertex.Texture));
        Assert.All(path, vertex => Assert.Equal(0f, vertex.Shape.Y));
        Assert.All(path, vertex => Assert.Equal(0f, vertex.Shape.Z));
        Assert.All(path, vertex => Assert.Equal(0f, vertex.Shape.W));
        Assert.All(path, vertex => Assert.InRange(vertex.Shape.X, 0f, 1f));

        // The interior is fully covered and the fringe runs out to nothing, so both ends are there.
        Assert.Contains(path, vertex => vertex.Shape.X == 1f);
        Assert.Contains(path, vertex => vertex.Shape.X == 0f);
    }

    /// <summary>
    ///     ⚠ The whole of what the fringe buys: an edge that is no longer whatever the rasteriser
    ///     gives it. Switched off, every vertex is fully covered and the outline is a hard step.
    /// </summary>
    [Fact]
    public void The_fringe_can_be_switched_off_for_a_pass_that_antialiases_itself() {
        var feathered = Build(list => Fill(list, Circle(), Color4.Red));
        var hard = BuildWith(builder => builder.Fringe = 0f, list => Fill(list, Circle(), Color4.Red));

        Assert.All(hard.Vertices, vertex => Assert.Equal(1f, vertex.Shape.X));
        Assert.True(feathered.Vertices.Count > hard.Vertices.Count);
    }

    /// <summary>
    ///     ⚠ A stroke is feathered too, and by the same number. A path filled and stroked in the same
    ///     frame with two different fringe widths would have one edge softer than the other.
    /// </summary>
    [Fact]
    public void A_stroke_is_feathered_as_well() {
        var feathered = Build(list => Stroke(list, Corner(), Color4.White, 6));
        var hard = Sharp(list => Stroke(list, Corner(), Color4.White, 6));

        Assert.True(feathered.Vertices.Count > hard.Vertices.Count);
        Assert.Contains(feathered.Vertices, vertex => vertex.Shape.X == 0f);
        Assert.All(hard.Vertices, vertex => Assert.Equal(1f, vertex.Shape.X));
    }

    [Fact]
    public void A_stroked_path_is_its_own_batch_and_its_own_geometry() {
        var geometry = Build(list => {
                Fill(list, Circle(), Color4.Red);
                Stroke(list, Circle(), Color4.White, thickness: 4);
            }
        );

        Assert.Equal(2, geometry.Draws.Count);
        Assert.Equal(BatchKind.PathFill, geometry.Draws[0].Kind);
        Assert.Equal(BatchKind.PathStroke, geometry.Draws[1].Kind);
        Assert.True(geometry.Draws[1].Count > 0);
    }

    /// <summary>
    ///     ⚠ The fill rule is part of the batch key, so two paths that differ only in it are two
    ///     draws — and they have to be, because they are not the same shape.
    /// </summary>
    [Fact]
    public void The_fill_rule_reaches_the_tessellator() {
        var nonZero = Build(list => Fill(list, Ring(), Color4.Red));
        var evenOdd = Build(list => Fill(list, Ring(), Color4.Red, PathFillRule.EvenOdd));

        Assert.True(
            nonZero.Vertices.Count != evenOdd.Vertices.Count,
            "the two fill rules produced identical geometry for a ring"
        );
    }

    /// <summary>
    ///     ⚠ A tighter tolerance is a finer curve, and it is the builder's to set: the geometry is in
    ///     document pixels, and how many device pixels one of those is depends on a surface scale the
    ///     builder is never handed.
    /// </summary>
    [Fact]
    public void The_tolerance_decides_how_finely_a_curve_is_flattened() {
        var coarse = BuildWith(builder => builder.Tolerance = 4f, list => Fill(list, Circle(), Color4.Red));
        var fine = BuildWith(builder => builder.Tolerance = 0.02f, list => Fill(list, Circle(), Color4.Red));

        Assert.True(fine.Vertices.Count > coarse.Vertices.Count * 2);
    }

    /// <summary>
    ///     ⚠ The join is a property of the stroke somebody asked for, so two strokes of the same path
    ///     at the same width are different geometry when their joins differ.
    /// </summary>
    /// <remarks>
    ///     ⚠ Built with the fringe off, so the counts are about the join. With it on, every piece
    ///     brings a strip of its own and the difference between a bevel and a miter is buried in it —
    ///     a test that counts vertices has to say which vertices it means.
    /// </remarks>
    [Fact]
    public void The_join_reaches_the_tessellator_from_the_command() {
        var miter = Sharp(list => Stroke(list, Corner(), Color4.White, 8, LineJoin.Miter));
        var bevel = Sharp(list => Stroke(list, Corner(), Color4.White, 8, LineJoin.Bevel));
        var round = Sharp(list => Stroke(list, Corner(), Color4.White, 8, LineJoin.Round));

        // A bevel is one triangle at the corner, a miter is two, a round one is a fan of many.
        Assert.Equal(bevel.Vertices.Count + 3, miter.Vertices.Count);
        Assert.True(round.Vertices.Count > miter.Vertices.Count);
    }

    [Fact]
    public void The_cap_reaches_the_tessellator_from_the_command() {
        var butt = Sharp(list => Stroke(list, Corner(), Color4.White, 8, cap: LineCap.Butt));
        var square = Sharp(list => Stroke(list, Corner(), Color4.White, 8, cap: LineCap.Square));

        // Two ends, two triangles each.
        Assert.Equal(butt.Vertices.Count + 12, square.Vertices.Count);
    }

    /// <summary>
    ///     ⚠ Zero is the default rather than a limit of zero. A struct's default is all-zeroes, so a
    ///     caller who set the thickness and nothing else would otherwise get a shape whose every
    ///     corner bevelled.
    /// </summary>
    [Fact]
    public void A_miter_limit_of_zero_means_the_default_rather_than_none() {
        var unset = Sharp(list => Stroke(list, Corner(), Color4.White, 8));
        var four = Sharp(list => Stroke(list, Corner(), Color4.White, 8, miterLimit: 4));
        var one = Sharp(list => Stroke(list, Corner(), Color4.White, 8, miterLimit: 1));

        Assert.Equal(four.Vertices.Count, unset.Vertices.Count);

        // A limit of one bevels the corner this path has, which the default does not.
        Assert.Equal(unset.Vertices.Count - 3, one.Vertices.Count);
    }

    [Fact]
    public void A_path_that_encloses_nothing_produces_no_draw() {
        var line = new PathBuilder().MoveTo(new Vector2(10, 10)).LineTo(new Vector2(90, 90));

        Assert.Empty(Build(list => Fill(list, line, Color4.Red)).Draws);
    }

    [Fact]
    public void A_path_is_clipped_like_everything_else() {
        var geometry = Build(list => {
                list.Add(ClipPush(0, 0, 50, 50));
                Fill(list, Circle(), Color4.Red);
            }
        );

        Assert.Equal(new Rectangle(0, 0, 50, 50), Assert.Single(geometry.Draws).Clip);
    }

    // ------------------------------------------------------------ Helpers

    const float RunX = 200;
    const float RunY = 100;

    static void Styled(DrawList list, CornerRadii corners, Color4 end = default, Vector2 axis = default) =>
        list.Add(
            new DrawCommand(DrawCommandKind.Rectangle, 0, 0, 80, 40, Color4.White, 0, 0) {
                Offset = list.AddBox(new BoxStyle(corners, end, axis)),
                Length = 1
            }
        );

    static PathBuilder Circle() => new PathBuilder().AddEllipse(new Rectangle(20, 20, 100, 100));

    /// <summary>An open path with exactly one corner in it, so a join is countable.</summary>
    static PathBuilder Corner() =>
        new PathBuilder()
            .MoveTo(new Vector2(20, 20))
            .LineTo(new Vector2(100, 20))
            .LineTo(new Vector2(100, 100));

    static PathBuilder Ring() =>
        new PathBuilder()
            .AddEllipse(new Rectangle(20, 20, 100, 100))
            .AddEllipse(new Rectangle(45, 45, 50, 50));

    static void Fill(DrawList list, PathBuilder path, Color4 color, PathFillRule rule = PathFillRule.NonZero) =>
        list.Add(
            new DrawCommand(DrawCommandKind.Path, 0, 0, 0, 0, color, 0, 0) {
                Offset = list.AddPath(path),
                Length = path.Count,
                FillRule = rule
            }
        );

    static void Stroke(
        DrawList list,
        PathBuilder path,
        Color4 color,
        float thickness,
        LineJoin join = LineJoin.Miter,
        LineCap cap = LineCap.Butt,
        float miterLimit = 0
    ) =>
        list.Add(
            new DrawCommand(DrawCommandKind.PathStroke, 0, 0, 0, 0, color, 0, thickness) {
                Offset = list.AddPath(path),
                Length = path.Count,
                Join = join,
                Cap = cap,
                MiterLimit = miterLimit
            }
        );

    static DrawCommand Rect(float x, float y, float width, float height, float radius = 0) =>
        new(DrawCommandKind.Rectangle, x, y, width, height, Color4.White, radius, 0);

    static DrawCommand Border(float x, float y, float width, float height, float thickness, float radius = 0) =>
        new(DrawCommandKind.Border, x, y, width, height, Color4.White, radius, thickness);

    static DrawCommand Shadow(float x, float y, float width, float height, float blur, float radius = 0) =>
        new(DrawCommandKind.Shadow, x, y, width, height, Color4.White, radius, blur);

    static DrawCommand ClipPush(float x, float y, float width, float height) =>
        new(DrawCommandKind.ClipPush, x, y, width, height, default, 0, 0);

    static DrawCommand ClipPop() => new(DrawCommandKind.ClipPop, 0, 0, 0, 0, default, 0, 0);

    static UiGeometry Build(Action<DrawList> paint) => BuildWith(null, paint);

    /// <summary>Builds without the antialiasing fringe, for a test that counts geometry.</summary>
    static UiGeometry Sharp(Action<DrawList> paint) => BuildWith(builder => builder.Fringe = 0f, paint);

    static UiGeometry BuildWith(Action<UiGeometryBuilder>? configure, Action<DrawList> paint) {
        var list = new DrawList();
        list.BeginFrame();
        paint(list);
        list.EndFrame();

        var builder = new UiGeometryBuilder();
        configure?.Invoke(builder);

        return builder.Build(list, Cache(), Viewport);
    }

    static (UiGeometry Geometry, UiGeometryBuilder Builder) BuildText(
        string text,
        float size = 24,
        float originX = RunX,
        GlyphFieldCache? cache = null,
        Action<DrawList>? before = null,
        Action<DrawList>? after = null
    ) {
        var font = Font();
        var list = new DrawList();
        list.BeginFrame();

        before?.Invoke(list);

        // ⚠ Offsets along the run, not positions on the surface — which is what the command's own
        // origin is for, and what lets two identical labels in different places share a glyph run.
        var glyphs = new List<PositionedGlyph>();
        var pen = 0f;
        foreach (var character in text) {
            glyphs.Add(new PositionedGlyph(font.GlyphFor(character), pen, 0));
            pen += size;
        }

        list.Add(
            new DrawCommand(DrawCommandKind.Text, originX, RunY, pen, size, Color4.White, 0, 0) {
                Offset = list.AddGlyphs(glyphs),
                Length = glyphs.Count,
                Font = list.AddFont(font),
                FontSize = size
            }
        );
        after?.Invoke(list);
        list.EndFrame();

        var builder = new UiGeometryBuilder();
        return (builder.Build(list, cache ?? Cache(), Viewport), builder);
    }

    /// <summary>One run of glyph ids, for a test that needs a known number of distinct fields.</summary>
    static (UiGeometry Geometry, UiGeometryBuilder Builder) BuildGlyphs(
        List<ushort> run,
        GlyphFieldCache cache,
        float size = 24
    ) {
        var font = Font();
        var list = new DrawList();
        list.BeginFrame();

        var glyphs = new List<PositionedGlyph>();
        var pen = 0f;

        foreach (var glyph in run) {
            glyphs.Add(new PositionedGlyph(glyph, pen, 0));
            pen += size;
        }

        list.Add(
            new DrawCommand(DrawCommandKind.Text, RunX, RunY, pen, size, Color4.White, 0, 0) {
                Offset = list.AddGlyphs(glyphs),
                Length = glyphs.Count,
                Font = list.AddFont(font),
                FontSize = size
            }
        );

        list.EndFrame();

        var builder = new UiGeometryBuilder();
        return (builder.Build(list, cache, Viewport), builder);
    }

    static GlyphFieldCache Cache() => new(new GlyphAtlas(512, 512));

    static FontFace? loaded;

    static FontFace Font() {
        if (loaded is not null) {
            return loaded;
        }

        using var stream = typeof(UiGeometryTests).Assembly.GetManifestResourceStream(Resource())
                           ?? throw new InvalidOperationException("no test font is embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        loaded = FontFace.Load(memory.ToArray(), name: "test");
        return loaded;
    }

    static string Resource() =>
        typeof(UiGeometryTests).Assembly.GetManifestResourceNames()
            .First(name => name.EndsWith(".ttf", StringComparison.Ordinal));
}
