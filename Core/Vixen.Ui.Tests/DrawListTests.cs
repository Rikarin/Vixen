// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The commands a styled, laid-out tree produces.</summary>
public class DrawListTests {
    const float Tolerance = 0.001f;

    static UiDocument Drawn(string css, Action<UiDocument>? build = null) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build?.Invoke(document);
        document.Update();
        document.Draw();
        return document;
    }

    [Fact]
    public void A_background_becomes_a_rectangle_where_layout_put_it() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; padding-left: 20px; padding-top: 10px; }
            .box { width: 60px; height: 40px; background-color: #ff0000; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        var command = Assert.Single(document.Drawing.Commands);

        Assert.Equal(DrawCommandKind.Rectangle, command.Kind);
        Assert.Equal(20f, command.X, Tolerance);
        Assert.Equal(10f, command.Y, Tolerance);
        Assert.Equal(60f, command.Width, Tolerance);
        Assert.Equal(40f, command.Height, Tolerance);

        // Linear, not sRGB — the cascade decodes once on the way in, which is the difference
        // between a correct fade and one that darkens.
        Assert.True(command.Color.R > 0.99f);
        Assert.Equal(0f, command.Color.G, Tolerance);
    }

    [Fact]
    public void An_element_with_nothing_to_draw_produces_nothing() {
        using var document = Drawn(
            "root { width: 400px; height: 300px; } .box { width: 10px; height: 10px; }",
            document => document.Root.Add("div", classNames: "box")
        );

        Assert.Empty(document.Drawing.Commands);
    }

    [Fact]
    public void An_element_that_is_not_displayed_draws_nothing_even_with_a_background() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .gone { display: none; width: 50px; height: 50px; background-color: #f00; border-radius: 4px; }
            .kept { width: 10px; height: 10px; background-color: #0f0; }
            """,
            document => {
                document.Root.Add("div", classNames: "gone");
                document.Root.Add("div", classNames: "kept");
            }
        );

        // ⚠ `display: none` reaches here as a zero-sized box rather than as a keyword, so the guard
        // is on the geometry. Without it the element contributes a rectangle of no width — invisible
        // to look at, and a command the renderer batches, uploads and rasterises for nothing.
        var command = Assert.Single(document.Drawing.Commands);
        Assert.True(command.Color.G > command.Color.R, "the wrong element survived");
    }

    [Fact]
    public void A_border_is_drawn_after_its_own_background() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #00ff00; border-width: 3px; border-color: #0000ff; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        Assert.Collection(
            document.Drawing.Commands,
            command => Assert.Equal(DrawCommandKind.Rectangle, command.Kind),
            command => {
                Assert.Equal(DrawCommandKind.Border, command.Kind);
                Assert.Equal(3f, command.Thickness, Tolerance);
            }
        );
    }

    [Fact]
    public void A_corner_radius_reaches_the_command() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #fff; border-radius: 8px; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        Assert.Equal(8f, Assert.Single(document.Drawing.Commands).Radius, Tolerance);
    }

    [Fact]
    public void Painting_order_is_document_order_and_hit_testing_is_its_reverse() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .layer { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px; }
            .under { background-color: #111; }
            .over { background-color: #222; }
            """,
            document => {
                document.Root.Add("div", classNames: ["layer", "under"]);
                document.Root.Add("div", classNames: ["layer", "over"]);
            }
        );

        var commands = document.Drawing.Commands;

        // ⚠ The two have to agree. The element drawn last is on top, so it is the one a click lands
        // on — a rule that made painting and hit testing disagree would be a UI where things are not
        // where they look.
        Assert.Equal(2, commands.Count);
        Assert.True(commands[1].Color.R > commands[0].Color.R);
        Assert.Same(document.Root.Children[^1], document.HitTest(50f, 50f));
    }

    [Fact]
    public void A_clip_wraps_exactly_the_children_it_clips() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .clip { width: 80px; height: 80px; overflow: hidden; background-color: #111; }
            .inner { width: 20px; height: 20px; background-color: #222; }
            .after { width: 20px; height: 20px; background-color: #333; }
            """,
            document => {
                var clip = document.Root.Add("div", classNames: "clip");
                clip.Add("div", classNames: "inner");
                document.Root.Add("div", classNames: "after");
            }
        );

        var kinds = document.Drawing.Commands.Select(command => command.Kind).ToList();

        // ⚠ The pop comes after the children and before the next sibling. A list whose pushes and
        // pops do not pair is not a drawing with a mistake in it — it is a clip stack that never
        // unwinds, and everything after the offending element stays clipped for the rest of the
        // frame.
        Assert.Equal(
            [
                DrawCommandKind.Rectangle,
                DrawCommandKind.ClipPush,
                DrawCommandKind.Rectangle,
                DrawCommandKind.ClipPop,
                DrawCommandKind.Rectangle
            ],
            kinds
        );
    }

    [Fact]
    public void Every_clip_that_is_pushed_is_popped() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; overflow: hidden; background-color: #010101; }
            .clip { width: 80px; height: 80px; overflow: hidden; background-color: #111; }
            """,
            document => {
                var outer = document.Root.Add("div", classNames: "clip");
                var middle = outer.Add("div", classNames: "clip");
                middle.Add("div", classNames: "clip");
            }
        );

        var depth = 0;
        foreach (var command in document.Drawing.Commands) {
            depth += command.Kind switch {
                DrawCommandKind.ClipPush => 1,
                DrawCommandKind.ClipPop => -1,
                _ => 0
            };

            Assert.True(depth >= 0, "a clip was popped that was never pushed");
        }

        Assert.Equal(0, depth);
    }

    [Fact]
    public void Rebuilding_an_unchanged_drawing_does_not_count_as_a_change() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #abc; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        var version = document.Drawing.Version;

        Assert.False(document.Draw());
        Assert.False(document.Draw());

        // The property doc 09 asks for: a static user interface re-submits a cached command buffer
        // instead of rebuilding one, and the renderer compares one integer to know.
        Assert.Equal(version, document.Drawing.Version);
        Assert.False(document.Drawing.ChangedLastFrame);
    }

    [Fact]
    public void A_drawing_that_actually_changes_says_so() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #abc; }
            .box.big { width: 90px; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        var version = document.Drawing.Version;

        document.Root.Children[0].AddClass("big");
        document.Update();

        Assert.True(document.Draw());
        Assert.NotEqual(version, document.Drawing.Version);
        Assert.Equal(90f, document.Drawing.Commands[0].Width, Tolerance);
    }

    [Fact]
    public void The_diff_is_against_what_was_drawn_and_not_against_a_flag() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #abc; }
            .box.same { width: 50px; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        var version = document.Drawing.Version;

        // ⚠ A class changed, so the cascade produced a different computed style and the framework
        // believes something happened. Nothing visible did. A dirty flag would report a change here
        // and the renderer would rebuild a buffer identical to the one it had — which is the failure
        // a cache is supposed to absorb rather than propagate.
        document.Root.Children[0].AddClass("same");
        document.Update();

        Assert.False(document.Draw());
        Assert.Equal(version, document.Drawing.Version);
    }
}
