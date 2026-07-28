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

    /// <summary>
    ///     ⚠ A rounded corner is a signed distance the shader evaluates, so the radius rides on the
    ///     vertex rather than turning into geometry. Tessellating one costs vertices in proportion to
    ///     the radius and is still faceted.
    /// </summary>
    [Fact]
    public void A_corner_radius_reaches_the_shader_rather_than_becoming_geometry() {
        var geometry = Build(list => list.Add(Rect(0, 0, 80, 40, radius: 12)));

        Assert.Equal(4, geometry.Vertices.Count);

        var shape = geometry.Vertices[0].Shape;
        Assert.Equal(40, shape.X, 3);      // half width
        Assert.Equal(20, shape.Y, 3);      // half height
        Assert.Equal(12, shape.Z, 3);      // radius
        Assert.Equal(0, shape.W, 3);       // a fill, not a border
    }

    [Fact]
    public void A_border_carries_its_thickness_and_shares_the_geometry_batch() {
        var geometry = Build(list => list.Add(Border(0, 0, 80, 40, thickness: 3)));

        Assert.Equal(BatchKind.Geometry, Assert.Single(geometry.Draws).Kind);
        Assert.Equal(3, geometry.Vertices[0].Shape.W, 3);
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

        Assert.All(geometry.Indices, index => Assert.InRange(index, 0, geometry.Vertices.Count - 1));

        // Two triangles per quad, and nothing left over.
        Assert.Equal(0, geometry.Indices.Count % 6);
        Assert.Equal(geometry.Vertices.Count / 4 * 6, geometry.Indices.Count);
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

    // ------------------------------------------------------------ Helpers

    const float RunX = 200;
    const float RunY = 100;

    static DrawCommand Rect(float x, float y, float width, float height, float radius = 0) =>
        new(DrawCommandKind.Rectangle, x, y, width, height, Color4.White, radius, 0);

    static DrawCommand Border(float x, float y, float width, float height, float thickness, float radius = 0) =>
        new(DrawCommandKind.Border, x, y, width, height, Color4.White, radius, thickness);

    static DrawCommand ClipPush(float x, float y, float width, float height) =>
        new(DrawCommandKind.ClipPush, x, y, width, height, default, 0, 0);

    static DrawCommand ClipPop() => new(DrawCommandKind.ClipPop, 0, 0, 0, 0, default, 0, 0);

    static UiGeometry Build(Action<DrawList> paint) {
        var list = new DrawList();
        list.BeginFrame();
        paint(list);
        list.EndFrame();

        return new UiGeometryBuilder().Build(list, Cache(), Viewport);
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
