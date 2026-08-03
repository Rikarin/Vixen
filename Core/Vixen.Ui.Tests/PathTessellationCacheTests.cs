// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Paths keep their triangles between frames, and lose them when they should.</summary>
/// <remarks>
///     <para>
///         <b>Flattening and tessellating is the most expensive thing the builder does</b>, and an
///         interface asks for the same paths frame after frame. The draw list is rebuilt every frame
///         from absolute coordinates, so a control that has not moved emits identical segments — the
///         condition a cache wants.
///     </para>
///     <para>
///         ⚠ <b>What made this necessary is that one changing character cost the whole window.</b> A
///         scene pane's frame-time readout rewrites its label sixty times a second; that made the
///         draw list differ, which meant re-tessellating every path on screen. An editor's icons are
///         filled outlines whose strokes were pre-expanded into quads, so a twenty-pixel glyph is a
///         couple of hundred segments — an outliner of two dozen rows measured 21,000 segments and
///         143ms a frame in Release, and 8ms afterwards.
///     </para>
/// </remarks>
public class PathTessellationCacheTests {
    static readonly Rectangle Viewport = new(0, 0, 800, 600);

    static PathBuilder Blob() =>
        new PathBuilder()
            .MoveTo(new Vector2(20, 20))
            .LineTo(new Vector2(120, 30))
            .CubicTo(new Vector2(140, 80), new Vector2(60, 120), new Vector2(20, 90))
            .Close();

    [Fact]
    public void The_same_path_drawn_again_is_not_tessellated_again() {
        var builder = new UiGeometryBuilder();
        var glyphs = Cache();

        builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport);

        Assert.Equal(1, builder.TessellatedPaths);

        // A *fresh* list with the same content, which is what a frame actually hands over — the list
        // is rebuilt every frame and never mutated in place.
        builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport);

        Assert.Equal(0, builder.TessellatedPaths);
        Assert.Equal(1, builder.CachedPaths);
    }

    /// <summary>
    ///     ⚠ <b>The geometry has to be identical too, not merely cheap.</b> A cache that returned
    ///     stale or empty triangles would satisfy the counter above and draw nothing.
    /// </summary>
    [Fact]
    public void A_reused_path_draws_what_it_drew_the_first_time() {
        var builder = new UiGeometryBuilder();
        var glyphs = Cache();

        var first = Snapshot(builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport));
        var second = Snapshot(builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport));

        Assert.Equal(first, second);
    }

    /// <summary>
    ///     ⚠ <b>Colour is not part of the key, and this is the case that says why.</b> It is applied
    ///     when the vertices are written and never reaches the tessellator, so a glyph that tints on
    ///     hover re-uses its triangles — which is otherwise a miss on every frame of a pointer moving
    ///     down a list.
    /// </summary>
    [Fact]
    public void Recolouring_a_path_reuses_its_triangles_and_still_changes_the_colour() {
        var builder = new UiGeometryBuilder();
        var glyphs = Cache();

        var pale = builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport);
        var paleColour = pale.Vertices[0].Color;

        var deep = builder.Build(Drawn(Blob(), new Color4(1f, 0f, 0f, 1f)), glyphs, Viewport);

        Assert.Equal(0, builder.TessellatedPaths);
        Assert.NotEqual(paleColour, deep.Vertices[0].Color);
        Assert.Equal(new Color4(1f, 0f, 0f, 1f), deep.Vertices[0].Color);
    }

    [Fact]
    public void A_path_whose_geometry_moved_is_tessellated_again() {
        var builder = new UiGeometryBuilder();
        var glyphs = Cache();

        builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport);

        var moved = new PathBuilder()
            .MoveTo(new Vector2(21, 20))
            .LineTo(new Vector2(120, 30))
            .CubicTo(new Vector2(140, 80), new Vector2(60, 120), new Vector2(20, 90))
            .Close();

        builder.Build(Drawn(moved, Color4.White), glyphs, Viewport);

        Assert.Equal(1, builder.TessellatedPaths);
        Assert.Equal(2, builder.CachedPaths);
    }

    /// <summary>
    ///     ⚠ <b>Every input the tessellator reads is in the key, and a stroke's width is the one most
    ///     easily forgotten.</b> Leaving it out is a line that keeps the weight it had.
    /// </summary>
    [Fact]
    public void A_stroke_that_changed_width_is_tessellated_again() {
        var builder = new UiGeometryBuilder();
        var glyphs = Cache();

        builder.Build(Stroked(Blob(), 2f), glyphs, Viewport);
        builder.Build(Stroked(Blob(), 6f), glyphs, Viewport);

        Assert.Equal(1, builder.TessellatedPaths);
    }

    /// <summary>
    ///     ⚠ <b>And the fringe, which is not on the command at all.</b> It is the builder's own
    ///     setting, so a surface that changed scale between frames would otherwise keep an outline
    ///     antialiased for the old one.
    /// </summary>
    [Fact]
    public void Changing_the_fringe_tessellates_again() {
        var builder = new UiGeometryBuilder();
        var glyphs = Cache();

        builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport);
        builder.Fringe = 0f;
        builder.Build(Drawn(Blob(), Color4.White), glyphs, Viewport);

        Assert.Equal(1, builder.TessellatedPaths);
    }

    /// <summary>
    ///     ⚠ <b>The ceiling drops what the frame did not draw, and never what it did.</b> Trimming a
    ///     path still on screen would make it a miss on the very next frame, which is a cache that
    ///     costs its own bookkeeping and buys nothing.
    /// </summary>
    [Fact]
    public void Trimming_keeps_what_the_frame_drew() {
        var builder = new UiGeometryBuilder { CacheCapacity = 2 };
        var glyphs = Cache();

        for (var i = 0; i < 8; i++) {
            var moved = new PathBuilder()
                .MoveTo(new Vector2(20 + i, 20))
                .LineTo(new Vector2(120, 30))
                .LineTo(new Vector2(20, 90))
                .Close();

            builder.Build(Drawn(moved, Color4.White), glyphs, Viewport);
        }

        // The last frame drew one path, so one survives — and drawing it again is a hit.
        var survivor = new PathBuilder()
            .MoveTo(new Vector2(27, 20))
            .LineTo(new Vector2(120, 30))
            .LineTo(new Vector2(20, 90))
            .Close();

        builder.Build(Drawn(survivor, Color4.White), glyphs, Viewport);

        Assert.Equal(0, builder.TessellatedPaths);
    }

    static DrawList Drawn(PathBuilder path, Color4 color) {
        var list = new DrawList();

        list.BeginFrame();
        list.Add(
            new DrawCommand(DrawCommandKind.Path, 0, 0, 0, 0, color, 0, 0) {
                Offset = list.AddPath(path),
                Length = path.Count,
                FillRule = PathFillRule.NonZero
            }
        );

        list.EndFrame();
        return list;
    }

    static DrawList Stroked(PathBuilder path, float thickness) {
        var list = new DrawList();

        list.BeginFrame();
        list.Add(
            new DrawCommand(DrawCommandKind.PathStroke, 0, 0, 0, 0, Color4.White, 0, thickness) {
                Offset = list.AddPath(path),
                Length = path.Count,
                Join = LineJoin.Miter,
                Cap = LineCap.Butt
            }
        );

        list.EndFrame();
        return list;
    }

    static List<(Vector2 Position, Vector4 Shape)> Snapshot(UiGeometry geometry) =>
        [.. geometry.Vertices.Select(vertex => (vertex.Position, vertex.Shape))];

    static GlyphFieldCache Cache() => new(new GlyphAtlas(512, 512));
}
