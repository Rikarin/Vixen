// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>An element that draws something a stylesheet cannot describe.</summary>
public class PathTests {
    const float Tolerance = 0.001f;

    /// <summary>An element that draws a triangle over itself.</summary>
    sealed class Triangle : UiElement {
        readonly PathBuilder path = new();

        public Color4 Tint { get; set; } = Color4.White;

        public float Stroke { get; set; }

        public PathFillRule Rule { get; set; }

        protected internal override void OnDraw(DrawContext context) {
            // Kept in a field and cleared, which is the advice the builder's remarks give: a control
            // that draws every frame should not make a new one every frame.
            path.Clear()
                .MoveTo(new Vector2(context.Bounds.Left, context.Bounds.Bottom))
                .LineTo(new Vector2(context.Bounds.Center.X, context.Bounds.Top))
                .LineTo(new Vector2(context.Bounds.Right, context.Bounds.Bottom))
                .Close();

            context.Fill(path, Tint, Rule);

            if (Stroke > 0f) {
                context.Stroke(path, Color4.Black, Stroke);
            }
        }
    }

    static (UiDocument Document, Triangle Shape) Drawn() {
        var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            shape { width: 40px; height: 20px; }
        """);

        var shape = document.Root.Add<Triangle>("shape");
        document.Update();

        return (document, shape);
    }

    [Fact]
    public void A_control_draws_itself_where_it_ended_up() {
        var (document, shape) = Drawn();
        using var owner = document;

        document.Draw();

        var command = Assert.Single(document.Drawing.Commands);
        Assert.Equal(DrawCommandKind.Path, command.Kind);
        Assert.Equal(4, command.Length);

        // Document space, from the bounds the layout pass produced, so a control does not have to be
        // handed an origin and does not have to know where in the tree it is.
        var apex = document.Drawing.Segments[command.Offset + 1].P2;
        Assert.Equal(20f, apex.X, Tolerance);
        Assert.Equal(0f, apex.Y, Tolerance);
    }

    [Fact]
    public void Filling_and_stroking_are_two_commands_over_one_path() {
        var (document, shape) = Drawn();
        using var owner = document;

        shape.Stroke = 2f;
        document.Draw();

        var commands = document.Drawing.Commands;
        Assert.Equal([DrawCommandKind.Path, DrawCommandKind.PathStroke], commands.Select(static c => c.Kind));

        // Two commands rather than one with a flag, because the two are different draws — but they
        // name the same shape, and a renderer that tessellates the outline once for both is entitled
        // to notice that from the ranges being identical in content.
        Assert.Equal(commands[0].Length, commands[1].Length);
        Assert.Equal(2f, commands[1].Thickness, Tolerance);

        for (var i = 0; i < commands[0].Length; i++) {
            Assert.Equal(
                document.Drawing.Segments[commands[0].Offset + i],
                document.Drawing.Segments[commands[1].Offset + i]
            );
        }
    }

    [Fact]
    public void Custom_drawing_sits_over_the_background_and_under_the_children() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            shape { width: 40px; height: 40px; background-color: #ffffff; }
            box { width: 10px; height: 10px; background-color: #000000; }
        """);

        var shape = document.Root.Add<Triangle>("shape");
        shape.Add("box");

        document.Update();
        document.Draw();

        // CSS's painting order, and the whole reason OnDraw is called where it is: a control's own
        // drawing covers the background it was given and is covered by anything nested inside it.
        Assert.Equal(
            [DrawCommandKind.Rectangle, DrawCommandKind.Path, DrawCommandKind.Rectangle],
            document.Drawing.Commands.Select(static command => command.Kind)
        );
    }

    [Fact]
    public void Moving_the_points_changes_the_drawing() {
        var (document, shape) = Drawn();
        using var owner = document;

        document.Draw();
        var before = document.Drawing.Version;

        // ⚠ The command is byte-identical — same kind, same colour, same range of the buffer — and
        // only the points moved. A frame diff that compared commands alone would report no change,
        // which is exactly what an animating chart does every frame.
        document.Styles.Tree.AddClass(shape.StyleNode, "wide");
        document.Load(".wide { width: 60px; }");
        document.Update();

        Assert.True(document.Draw());
        Assert.NotEqual(before, document.Drawing.Version);
        Assert.Single(document.Drawing.Commands);
    }

    [Fact]
    public void Redrawing_the_same_shape_changes_nothing() {
        var (document, shape) = Drawn();
        using var owner = document;

        document.Draw();
        var version = document.Drawing.Version;

        Assert.False(document.Draw());
        Assert.Equal(version, document.Drawing.Version);
    }

    [Fact]
    public void The_fill_rule_reaches_the_command() {
        var (document, shape) = Drawn();
        using var owner = document;

        shape.Rule = PathFillRule.EvenOdd;
        document.Draw();

        // Carried rather than assumed, because it is how most icon sets punch the hole in a letter o
        // — and a renderer that only knew non-zero would fill it in.
        Assert.Equal(PathFillRule.EvenOdd, Assert.Single(document.Drawing.Commands).FillRule);
    }

    [Fact]
    public void An_empty_path_draws_nothing() {
        using var document = new UiDocument(100f, 100f);

        document.Load("root { width: 100px; height: 100px; }");
        document.Update();

        var list = document.Drawing;
        list.BeginFrame();

        new DrawContext(document.Root, list).Fill(new PathBuilder(), Color4.White);
        new DrawContext(document.Root, list).Stroke(new PathBuilder().MoveTo(default), Color4.White, 0f);

        list.EndFrame();

        // A path with nothing in it and a stroke with no width are both "the control decided not to
        // draw this", which is an ordinary branch in a control rather than something to complain
        // about — and a command with a zero-length range is a thing every consumer has to skip.
        Assert.Empty(list.Commands);
    }

    [Fact]
    public void Close_remembers_where_the_contour_started() {
        var path = new PathBuilder()
            .MoveTo(new Vector2(1f, 2f))
            .LineTo(new Vector2(5f, 2f))
            .Close();

        var close = path.Segments[^1];

        // ⚠ The verb survives and carries the point. A stroked path's closing join is drawn
        // differently from a line back to the same place, so turning a Close into a LineTo while
        // building would round the wrong corner — and a consumer wanting the coordinates should not
        // have to walk backwards to find the last MoveTo.
        Assert.Equal(PathVerb.Close, close.Verb);
        Assert.Equal(new Vector2(1f, 2f), close.P2);
        Assert.Equal(new Vector2(1f, 2f), path.Current);
    }

    [Fact]
    public void A_second_contour_closes_to_its_own_start() {
        var path = new PathBuilder()
            .MoveTo(new Vector2(0f, 0f))
            .LineTo(new Vector2(1f, 0f))
            .Close()
            .MoveTo(new Vector2(10f, 10f))
            .LineTo(new Vector2(11f, 10f))
            .Close();

        // Which is what makes a path with a hole in it possible at all: the second contour's Close
        // has to go back to the second MoveTo and not to the first.
        Assert.Equal(new Vector2(0f, 0f), path.Segments[2].P2);
        Assert.Equal(new Vector2(10f, 10f), path.Segments[5].P2);
    }

    [Fact]
    public void A_curve_stays_a_curve() {
        var path = new PathBuilder()
            .MoveTo(new Vector2(0f, 0f))
            .CubicTo(new Vector2(1f, 0f), new Vector2(2f, 1f), new Vector2(2f, 2f));

        // ⚠ Nothing flattens it. How finely to flatten depends on how large the curve will be on
        // screen, which is a device scale the draw list does not know — flattened here, a path built
        // once and drawn at two zoom levels is faceted at one of them and nothing downstream can
        // recover the curve to do better.
        var cubic = path.Segments[1];
        Assert.Equal(PathVerb.Cubic, cubic.Verb);
        Assert.Equal(new Vector2(1f, 0f), cubic.P0);
        Assert.Equal(new Vector2(2f, 1f), cubic.P1);
        Assert.Equal(new Vector2(2f, 2f), cubic.P2);
    }

    [Fact]
    public void An_ellipse_is_four_cubics_and_ends_where_it_started() {
        var path = new PathBuilder().AddEllipse(new Rectangle(10f, 20f, 40f, 60f));

        Assert.Equal(PathVerb.Move, path.Segments[0].Verb);
        Assert.Equal(4, path.Segments.Count(static segment => segment.Verb == PathVerb.Cubic));
        Assert.Equal(PathVerb.Close, path.Segments[^1].Verb);

        // Top centre, and back to it. A quarter turn's control points are the well-known constant
        // rather than an arc, because a Bézier cannot be a circular arc exactly and the error at
        // that constant is about one part in ten thousand of the radius.
        Assert.Equal(new Vector2(30f, 20f), path.Segments[0].P2);
        Assert.Equal(new Vector2(30f, 20f), path.Segments[4].P2);
    }

    [Fact]
    public void Clearing_a_builder_forgets_where_the_pen_was() {
        var path = new PathBuilder().MoveTo(new Vector2(7f, 7f)).LineTo(new Vector2(9f, 9f));

        // ⚠ The pen goes back to the origin with the segments. Clearing only the list leaves an empty
        // builder claiming the pen is wherever last frame left it — and a control that reads
        // `Current` to decide where to start would draw from the previous frame's shape.
        Assert.Equal(default, path.Clear().Current);

        path.MoveTo(new Vector2(1f, 1f)).LineTo(new Vector2(2f, 2f)).Close();

        // Reused between frames, so the contour start has to be reset with the segments. Kept, the
        // second frame's Close would join back to the first frame's shape.
        Assert.Equal(3, path.Count);
        Assert.Equal(new Vector2(1f, 1f), path.Segments[^1].P2);
    }

    [Fact]
    public void A_path_is_clipped_by_the_element_that_clips() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            panel { width: 50px; height: 50px; overflow: hidden; }
            shape { width: 40px; height: 20px; }
        """);

        var panel = document.Root.Add("panel");
        panel.Add<Triangle>("shape");

        document.Update();
        document.Draw();

        // Custom drawing is inside the clip its ancestors pushed, like everything else in the list.
        // A control that drew straight into the list without going through the walk would escape it.
        Assert.Equal(
            [DrawCommandKind.ClipPush, DrawCommandKind.Path, DrawCommandKind.ClipPop],
            document.Drawing.Commands.Select(static command => command.Kind)
        );
    }
}
