// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>An icon drawn as a distance field instead of as triangles.</summary>
/// <remarks>
///     <para>
///         <b>An icon is a glyph that is not in a font</b>, and text has been drawn from an atlased
///         distance field here since there was text. Icons were tessellated — and a hand-drawn editor
///         glyph whose strokes are pre-expanded into filled quads measured <b>13,419 vertices</b>
///         against the four a quad costs, with an outliner of two dozen rows emitting 2.6 million
///         vertices a frame to draw a few hundred pixels of artwork.
///     </para>
/// </remarks>
public class IconAtlasTests {
    static readonly Rectangle Viewport = new(0, 0, 800, 600);

    /// <summary>A pre-expanded stroke, which is what the editor's icons actually are.</summary>
    static PathBuilder Expanded(float size, int points = 20) {
        var path = new PathBuilder();
        var corners = new Vector2[points];

        for (var i = 0; i < points; i++) {
            var t = i / (float) (points - 1) * MathF.Tau * 1.5f;
            corners[i] = new Vector2(12f + (8f * MathF.Cos(t)), 12f + (8f * MathF.Sin(t)));
        }

        var half = 0.75f;
        var scale = size / 24f;

        for (var i = 0; i + 1 < points; i++) {
            var delta = corners[i + 1] - corners[i];
            var normal = new Vector2(-delta.Y, delta.X) / delta.Length() * half;

            path.MoveTo((corners[i] + normal) * scale);
            path.LineTo((corners[i + 1] + normal) * scale);
            path.LineTo((corners[i + 1] - normal) * scale);
            path.LineTo((corners[i] - normal) * scale);
            path.Close();
        }

        return path;
    }

    static PathBuilder Square(float side) =>
        new PathBuilder()
            .MoveTo(new Vector2(0f, 0f))
            .LineTo(new Vector2(side, 0f))
            .LineTo(new Vector2(side, side))
            .LineTo(new Vector2(0f, side))
            .Close();

    static DrawList Listed(
        PathBuilder path,
        DrawCommandKind kind = DrawCommandKind.Field,
        Vector2 origin = default,
        PathFillRule rule = PathFillRule.NonZero
    ) {
        var list = new DrawList();

        list.BeginFrame();
        list.Add(
            new DrawCommand(kind, origin.X, origin.Y, 0f, 0f, Color4.White, 0f, 0f) {
                Offset = list.AddPath(path),
                Length = path.Count,
                FillRule = rule
            }
        );

        list.EndFrame();
        return list;
    }

    static GlyphFieldCache Glyphs() => new(new GlyphAtlas(1024, 1024));

    // ======================================================================== The saving

    [Fact]
    public void An_icon_is_four_vertices_where_the_same_path_filled_is_thousands() {
        var path = Expanded(16f);
        var glyphs = Glyphs();

        var tessellated = new UiGeometryBuilder().Build(Listed(path, DrawCommandKind.Path), glyphs, Viewport);
        var fielded = new UiGeometryBuilder().Build(Listed(path), Glyphs(), Viewport);

        Assert.True(tessellated.Vertices.Count > 5000, $"expected thousands, got {tessellated.Vertices.Count}");
        Assert.Equal(4, fielded.Vertices.Count);
        Assert.Equal(6, fielded.Indices.Count);
    }

    /// <summary>
    ///     ⚠ <b>The case that decides whether this is faster or slower than what it replaces.</b> A
    ///     draw list is rebuilt every frame from wherever things are, so scrolling a tree moves every
    ///     icon in it — and encoding a field costs milliseconds where re-tessellating one costs
    ///     microseconds. A key that separated two instances of one icon would therefore be far worse
    ///     than no cache at all, which is why the geometry is local and the position is on the
    ///     command.
    /// </summary>
    [Fact]
    public void The_same_icon_somewhere_else_is_the_same_entry() {
        var path = Expanded(16f);
        var builder = new UiGeometryBuilder();
        var glyphs = Glyphs();

        builder.Build(Listed(path), glyphs, Viewport);

        var generated = builder.Icons!.Generated;

        for (var i = 1; i <= 50; i++) {
            builder.Build(Listed(path, origin: new Vector2(i * 0.37f, i * 13f)), glyphs, Viewport);
        }

        Assert.Equal(generated, builder.Icons.Generated);
        Assert.Equal(1, builder.Icons.Count);
    }

    /// <summary>And it really does move, rather than being cached into one place.</summary>
    [Fact]
    public void An_icon_is_drawn_where_the_command_says() {
        var path = Expanded(16f);
        var builder = new UiGeometryBuilder();
        var glyphs = Glyphs();

        var here = Snapshot(builder.Build(Listed(path), glyphs, Viewport));
        var there = Snapshot(builder.Build(Listed(path, origin: new Vector2(100f, 40f)), glyphs, Viewport));

        Assert.Equal(here.Count, there.Count);

        for (var i = 0; i < here.Count; i++) {
            Assert.Equal(here[i].X + 100f, there[i].X, 3);
            Assert.Equal(here[i].Y + 40f, there[i].Y, 3);
        }
    }

    /// <summary>
    ///     ⚠ <b>Per size, which is the one way this is weaker than the glyph cache.</b> A font's
    ///     outline arrives in design units and one field serves every size; a path arrives already
    ///     fitted to the box the layout gave it. Asserted rather than merely noted, because a change
    ///     that quietly merged two sizes would draw one of them at the other's resolution.
    /// </summary>
    [Fact]
    public void Two_sizes_of_one_icon_are_two_entries() {
        var builder = new UiGeometryBuilder();
        var glyphs = Glyphs();

        builder.Build(Listed(Expanded(16f)), glyphs, Viewport);
        builder.Build(Listed(Expanded(32f)), glyphs, Viewport);

        Assert.Equal(2, builder.Icons!.Count);
        Assert.Equal(2, builder.Icons.Generated);
    }

    // ======================================================================== Refusal

    /// <summary>
    ///     ⚠ <b>A refused path still draws, and it must not draw through the text pipeline.</b> A
    ///     field command batches as text — that is what puts an icon in the same draw as the label
    ///     beside it — so triangles emitted into that batch would be handed to the shader that
    ///     reconstructs a coverage from three distance channels, and would come out as a smear of
    ///     whatever texel their zeroed coordinates land on. The batch's draw has to be closed and the
    ///     triangles given one of their own.
    /// </summary>
    [Fact]
    public void A_refused_path_is_tessellated_into_a_draw_of_its_own() {
        var builder = new UiGeometryBuilder();
        var geometry = builder.Build(
            Listed(Expanded(16f), rule: PathFillRule.EvenOdd),
            Glyphs(),
            Viewport
        );

        Assert.Equal(1, builder.RefusedFields);
        Assert.Equal(0, builder.FieldPaths);

        // It drew, and it drew as a fill.
        Assert.NotEmpty(geometry.Vertices);
        Assert.All(geometry.Draws, draw => Assert.Equal(BatchKind.PathFill, draw.Kind));
    }

    /// <summary>
    ///     ⚠ <b>Even-odd is refused rather than approximated.</b> <c>GlyphRasterizer</c> fills by
    ///     non-zero, which every font relies on for the counter in an <c>o</c>; encoding an even-odd
    ///     path with it would produce a field of a shape the caller did not ask for — and a wrong
    ///     picture is worse than a slow one.
    /// </summary>
    [Fact]
    public void An_even_odd_path_is_refused() {
        var atlas = new IconAtlas(new GlyphAtlas(512, 512));
        var list = Listed(Expanded(16f), rule: PathFillRule.EvenOdd);
        var command = list.Commands[0];

        Assert.False(
            atlas.TryGet(list.Segments, command.Offset, command.Length, PathFillRule.EvenOdd, out _)
        );

        Assert.Equal(1, atlas.Refused);
    }

    /// <summary>
    ///     ⚠ <b>A large path is refused on quality, not on memory.</b> The field is its resolution on
    ///     the longer side whatever the path's size, so a drawing that fills a panel would be a raster
    ///     magnified many times over — where tessellating it is exact and is what a path that big
    ///     should be doing anyway.
    /// </summary>
    [Fact]
    public void A_path_larger_than_the_ceiling_is_refused() {
        var atlas = new IconAtlas(new GlyphAtlas(512, 512)) { MaxSize = 40f };
        var list = Listed(Square(200f));
        var command = list.Commands[0];

        Assert.False(atlas.TryGet(list.Segments, command.Offset, command.Length, PathFillRule.NonZero, out _));

        // And the same shape under the ceiling is not.
        var small = Listed(Square(20f));

        Assert.True(
            atlas.TryGet(small.Segments, small.Commands[0].Offset, small.Commands[0].Length, PathFillRule.NonZero, out _)
        );
    }

    /// <summary>A refusal is remembered, so a shape is not rasterised again to reach the same no.</summary>
    [Fact]
    public void A_refusal_is_not_repeated() {
        var atlas = new IconAtlas(new GlyphAtlas(512, 512)) { MaxSize = 40f };
        var list = Listed(Square(200f));
        var command = list.Commands[0];

        for (var i = 0; i < 5; i++) {
            Assert.False(atlas.TryGet(list.Segments, command.Offset, command.Length, PathFillRule.NonZero, out _));
        }

        Assert.Equal(1, atlas.Refused);
    }

    // ======================================================================== The picture

    /// <summary>
    ///     ⚠ <b>The field's edge has to land where the shape's edge is, and this is the test that
    ///     would have caught the y-flip.</b> A document's y runs down and an outline's runs up, so a
    ///     field encoded without negating one is upside down <i>about its own centre</i> — which a
    ///     symmetric icon hides completely and an arrow does not. A square is used because its edges
    ///     are at exactly known coordinates, so the whole chain — padding, scale, cell origin, the
    ///     flip and the quad — is checked against a number rather than against a picture.
    /// </summary>
    [Theory]
    [InlineData(10f)]
    [InlineData(23.5f)]
    public void The_reconstructed_edge_lands_where_the_shape_edge_is(float side) {
        var atlas = new IconAtlas(new GlyphAtlas(512, 512));
        var list = Listed(Square(side));
        var command = list.Commands[0];

        Assert.True(atlas.TryGet(list.Segments, command.Offset, command.Length, PathFillRule.NonZero, out var field));

        // The row through the middle of the cell, and the column through it.
        var middleRow = field.Region.Height / 2;
        var middleColumn = field.Region.Width / 2;

        var left = Crossing(atlas, field, middleRow, horizontal: true, rising: true);
        var right = Crossing(atlas, field, middleRow, horizontal: true, rising: false);
        var top = Crossing(atlas, field, middleColumn, horizontal: false, rising: true);
        var bottom = Crossing(atlas, field, middleColumn, horizontal: false, rising: false);

        // Within a texel of the truth, which at this resolution is a fraction of a document pixel.
        var texel = field.Quad.Width / field.Region.Width;

        Assert.InRange(left, -texel, texel);
        Assert.InRange(right, side - texel, side + texel);
        Assert.InRange(top, -texel, texel);
        Assert.InRange(bottom, side - texel, side + texel);
    }

    /// <summary>
    ///     ⚠ <b>The transport's pause glyph, which is the shape every other test here cannot see.</b>
    ///     Two disjoint bars with a gap between them, at the size the editor's toolbar actually draws
    ///     them — and for a year the field bridged the gap at both ends and drew an I-beam. What put
    ///     it there is <c>DistanceField</c>'s corner rule; what made it visible rather than merely
    ///     wrong is <see cref="IconAtlas.Feather" />, which shifts the whole field half a texel to
    ///     match the tessellator's fringe and so pushes a value of exactly 0.5 over the threshold.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every other test in this file is blind to it, which is worth saying rather than
    ///     leaving to be rediscovered.</b> <c>The_reconstructed_edge_lands_where_the_shape_edge_is</c>
    ///     is the only one that reads a texel at all, and it walks the middle row and middle column of
    ///     a <i>square</i> — two lines that never leave the shape's own silhouette, which is exactly
    ///     where the phantom band coincides with the real edge. The rest count vertices, entries and
    ///     refusals. A square is also the one transport glyph the defect left alone in the picture, so
    ///     the shape the tests used and the shape that looked fine were the same shape.
    /// </remarks>
    [Fact]
    public void The_gap_between_two_bars_is_a_gap_for_its_whole_length() {
        var atlas = new IconAtlas(new GlyphAtlas(512, 512));
        var list = Listed(Pause(18f));
        var command = list.Commands[0];

        Assert.True(atlas.TryGet(list.Segments, command.Offset, command.Length, PathFillRule.NonZero, out var field));

        var scale = 18f / 24f;
        var worst = 0f;
        var worstAt = (X: 0, Y: 0);

        for (var y = 0; y < field.Region.Height; y++) {
            for (var x = 0; x < field.Region.Width; x++) {
                var at = Point(field, x, y);

                // The gap, taken a quarter of a pixel in from each bar so that the edge's own ramp is
                // not what is being measured — and over the full height of the cell, because the
                // whole point is that the two ends behaved differently from the middle.
                if (at.X < (10.6f * scale) + 0.25f || at.X > (13.4f * scale) - 0.25f) {
                    continue;
                }

                var median = Median(atlas, field, x, y);
                if (median > worst) {
                    (worst, worstAt) = (median, (x, y));
                }
            }
        }

        Assert.True(
            worst < 0.5f,
            $"the gap reads {worst:F3} at texel {worstAt}, which the shader draws as "
            + $"{Math.Clamp(((worst - 0.5f) * field.ScreenPixelRange) + 0.5f, 0f, 1f):F2} of the icon's colour"
        );
    }

    /// <summary>
    ///     ⚠ <b>The general form: the field's silhouette is the shape's silhouette, dilated by the
    ///     fringe and by nothing else.</b> Stated over the whole cell rather than along a line,
    ///     because the defect this replaces was a band two texels clear of the artwork that no line
    ///     through the artwork could cross. The shapes are rectangles so that the distance is a closed
    ///     form owing nothing to the code under test.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The tolerance is a texel and a half, and the corner is why.</b> Outside a convex
    ///     corner the median reports the nearer of the two half planes rather than the distance to the
    ///     vertex — which is what keeps the corner sharp — so it understates by up to the distance
    ///     over root two, and at a range of four texels that is a little over one. A tolerance tight
    ///     enough to catch that would be a test of the corner rule and not of the silhouette. The
    ///     defect it exists for was a whole row at 0.5 across a cell twenty-three texels wide.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Silhouettes))]
    public void Nothing_reads_as_covered_that_is_not_within_the_fringe(string name, Rectangle[] parts) {
        var atlas = new IconAtlas(new GlyphAtlas(512, 512));
        var path = new PathBuilder();

        foreach (var part in parts) {
            path.AddRectangle(part);
        }

        var list = Listed(path);
        var command = list.Commands[0];

        Assert.True(atlas.TryGet(list.Segments, command.Offset, command.Length, PathFillRule.NonZero, out var field));

        var texel = field.Quad.Width / field.Region.Width;
        var slack = (atlas.Feather * 0.5f) + (1.5f * texel);
        var failures = new List<string>();

        for (var y = 0; y < field.Region.Height; y++) {
            for (var x = 0; x < field.Region.Width; x++) {
                var at = Point(field, x, y);
                var distance = Distance(parts, at);

                if (MathF.Abs(distance) <= slack) {
                    continue;
                }

                var covered = Median(atlas, field, x, y) >= 0.5f;
                if (covered != distance > 0f) {
                    failures.Add(
                        $"texel ({x},{y}) at ({at.X:F2},{at.Y:F2}) is {distance:F2}px "
                        + $"{(distance > 0 ? "inside" : "outside")} and reads "
                        + $"{Median(atlas, field, x, y):F3}"
                    );
                }
            }
        }

        Assert.True(failures.Count == 0, $"{name}: {failures.Count} texels\n{string.Join('\n', failures.Take(12))}");
    }

    public static TheoryData<string, Rectangle[]> Silhouettes() =>
        new() {
            { "stop", [Fit(new Rectangle(6f, 6f, 12f, 12f))] },
            { "pause", [Fit(new Rectangle(7f, 4f, 3.6f, 16f)), Fit(new Rectangle(13.4f, 4f, 3.6f, 16f))] },
            { "a hairline and a block", [Fit(new Rectangle(4f, 4f, 1f, 16f)), Fit(new Rectangle(9f, 8f, 11f, 8f))] }
        };

    /// <summary>A view-box rectangle at the size a toolbar draws an icon.</summary>
    static Rectangle Fit(Rectangle box) {
        const float Scale = 18f / 24f;
        return new Rectangle(box.X * Scale, box.Y * Scale, box.Width * Scale, box.Height * Scale);
    }

    /// <summary>The transport's pause glyph, fitted the way <c>Icon</c> fits one.</summary>
    static PathBuilder Pause(float size) {
        var scale = size / 24f;

        return new PathBuilder()
            .AddRectangle(new Rectangle(7f * scale, 4f * scale, 3.6f * scale, 16f * scale))
            .AddRectangle(new Rectangle(13.4f * scale, 4f * scale, 3.6f * scale, 16f * scale));
    }

    /// <summary>Signed distance to a union of disjoint rectangles, positive inside. A closed form.</summary>
    static float Distance(Rectangle[] parts, Vector2 point) {
        var best = float.MinValue;

        foreach (var part in parts) {
            var dx = MathF.Min(point.X - part.X, part.X + part.Width - point.X);
            var dy = MathF.Min(point.Y - part.Y, part.Y + part.Height - point.Y);

            var distance = dx >= 0f && dy >= 0f
                ? MathF.Min(dx, dy)
                : -MathF.Sqrt((MathF.Max(-dx, 0f) * MathF.Max(-dx, 0f)) + (MathF.Max(-dy, 0f) * MathF.Max(-dy, 0f)));

            best = MathF.Max(best, distance);
        }

        return best;
    }

    /// <summary>A texel's centre, in the path's own coordinates.</summary>
    static Vector2 Point(IconField field, int x, int y) =>
        new(
            field.Quad.X + ((x + 0.5f) / field.Region.Width * field.Quad.Width),
            field.Quad.Y + ((y + 0.5f) / field.Region.Height * field.Quad.Height)
        );

    /// <summary>Where the median crosses a half along one line of the cell, in the path's own units.</summary>
    static float Crossing(IconAtlas icons, IconField field, int line, bool horizontal, bool rising) {
        var count = horizontal ? field.Region.Width : field.Region.Height;

        for (var i = 0; i < count; i++) {
            var step = rising ? i : count - 1 - i;
            var next = rising ? step + 1 : step - 1;

            if (next < 0 || next >= count) {
                continue;
            }

            var here = Median(icons, field, horizontal ? next : line, horizontal ? line : next);
            var before = Median(icons, field, horizontal ? step : line, horizontal ? line : step);

            if (before >= 0.5f || here < 0.5f) {
                continue;
            }

            // Linear between the two texel centres, which is what the sampler does too.
            var t = (0.5f - before) / (here - before);
            var centre = step + 0.5f + ((next - step) * t);

            return horizontal
                ? field.Quad.X + (centre / field.Region.Width * field.Quad.Width)
                : field.Quad.Y + (centre / field.Region.Height * field.Quad.Height);
        }

        return float.NaN;
    }

    static float Median(IconAtlas icons, IconField field, int x, int y) {
        var atlas = icons.Atlas;
        var at = (((field.Region.Y + y) * atlas.Width) + field.Region.X + x) * 3;

        var r = atlas.Pixels[at];
        var g = atlas.Pixels[at + 1];
        var b = atlas.Pixels[at + 2];

        return MathF.Max(MathF.Min(r, g), MathF.Min(MathF.Max(r, g), b));
    }

    // ======================================================================== The atlas it shares

    /// <summary>
    ///     ⚠ <b>Icons go in the glyph atlas, and so they batch with the text beside them.</b> The
    ///     renderer binds one atlas descriptor for a whole frame; an icon in a texture of its own
    ///     would be a second set and a batch break between every glyph and the label next to it.
    /// </summary>
    [Fact]
    public void An_icon_and_the_glyphs_are_in_one_atlas_and_one_draw() {
        var builder = new UiGeometryBuilder();
        var glyphs = Glyphs();
        var path = Expanded(16f);

        var list = new DrawList();
        list.BeginFrame();

        for (var i = 0; i < 3; i++) {
            list.Add(
                new DrawCommand(DrawCommandKind.Field, i * 20f, 0f, 0f, 0f, Color4.White, 0f, 0f) {
                    Offset = list.AddPath(path),
                    Length = path.Count,
                    FillRule = PathFillRule.NonZero
                }
            );
        }

        list.EndFrame();

        var geometry = builder.Build(list, glyphs, Viewport);

        Assert.Equal(3, builder.FieldPaths);
        Assert.Single(geometry.Draws);
        Assert.Equal(BatchKind.Text, geometry.Draws[0].Kind);
        Assert.Equal(12, geometry.Vertices.Count);
    }

    /// <summary>
    ///     ⚠ <b>An icon's key must not be able to collide with a real glyph's.</b> They share a
    ///     dictionary now, and glyph zero of font zero is <c>.notdef</c> — a glyph a font really
    ///     draws. An icon whose hash happened to be zero would otherwise take its slot and the missing
    ///     -glyph box would appear where the icon should be.
    /// </summary>
    [Fact]
    public void An_icon_key_is_not_a_glyph_key() {
        var glyph = new GlyphKey(0, 0, 48);
        var icon = new GlyphKey(-1, 0, 0) { Path = 0 };

        Assert.NotEqual(glyph, icon);
        Assert.NotEqual(new GlyphKey(0, 0, 48) { Path = 7 }, glyph);
    }

    /// <summary>
    ///     ⚠ <b>Every icon is in the atlas before any quad reads a region out of it</b>, which is the
    ///     same hazard the glyph resolve pass exists for and is now shared with it: adding an icon can
    ///     repack the texture, and a repack moves every region — including the ones a <i>label</i>
    ///     earlier in the same frame has already baked into its vertices.
    /// </summary>
    [Fact]
    public void Icons_are_resolved_before_anything_is_emitted() {
        var builder = new UiGeometryBuilder();
        var glyphs = Glyphs();

        var list = new DrawList();
        list.BeginFrame();

        for (var i = 0; i < 6; i++) {
            var path = Expanded(12f + i, 12 + i);

            list.Add(
                new DrawCommand(DrawCommandKind.Field, i * 20f, 0f, 0f, 0f, Color4.White, 0f, 0f) {
                    Offset = list.AddPath(path),
                    Length = path.Count,
                    FillRule = PathFillRule.NonZero
                }
            );
        }

        list.EndFrame();
        builder.Build(list, glyphs, Viewport);

        Assert.Equal(6, builder.FieldPaths);
        Assert.False(builder.AtlasChanged);
    }

    static List<Vector2> Snapshot(UiGeometry geometry) => [.. geometry.Vertices.Select(vertex => vertex.Position)];
}
