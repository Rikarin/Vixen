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

    /// <summary>The same, with a face registered, for the tests that are about text.</summary>
    /// <remarks>
    ///     ⚠ Separate rather than folded into <see cref="Drawn" />: an element with no font emits no
    ///     <c>Text</c> command at all, so registering one everywhere would add a command to the
    ///     expected sequence of every test above that happens to put a string somewhere.
    /// </remarks>
    static UiDocument DrawnWithText(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Face);

        document.Load(css);
        build(document);
        document.Update();
        document.Draw();

        return document;
    }

    static readonly Text.FontFace Face = LoadFace();

    static Text.FontFace LoadFace() {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return Text.FontFace.Load(memory.ToArray(), name: "TestShapeLana");
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

    /// <summary>
    ///     The same border, written the way every sheet in this repository actually writes it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the end-to-end guard on a fix that lives two projects away.</b> ExCSS cannot
    ///     expand a shorthand whose value holds a <c>var()</c>, so it hands <c>border-color</c> back
    ///     whole and nothing downstream reads that name;
    ///     <c>Vixen.Ui.Styling.StyleSheetLoader</c> takes it apart at load instead. Its own tests
    ///     check the taking-apart in isolation and the test above uses a literal colour, so without
    ///     this one the wiring between them could be removed and every suite would still pass —
    ///     while every border in the framework silently stopped being drawn, which is exactly how
    ///     the original went unnoticed.
    /// </remarks>
    [Fact]
    public void A_border_colour_behind_a_var_is_drawn_like_one_written_out() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; --border: #0000ff; }
            .box { width: 50px; height: 50px; background-color: #00ff00; border-width: 3px; border-color: var(--border); }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        // The longhand by name, because that is the thing the shorthand was not reaching. The
        // command below would also fail without it, but it would not say which half broke.
        var box = Assert.Single(document.Root.Children);
        var property = document.Styles.Properties.Lookup("border-top-color");

        Assert.True(property >= 0 && box.Style.TryGet(property, out _), "border-top-color never arrived");
        Assert.Empty(document.Styles.Loader.Diagnostics);

        Assert.Collection(
            document.Drawing.Commands,
            command => Assert.Equal(DrawCommandKind.Rectangle, command.Kind),
            command => {
                Assert.Equal(DrawCommandKind.Border, command.Kind);
                Assert.Equal(3f, command.Thickness, Tolerance);

                // Substituted as well as expanded: an unresolved `var(--border)` parses to nothing
                // and would draw the transparent border that looks exactly like no border at all.
                Assert.True(command.Color.B > 0.99f);
                Assert.Equal(0f, command.Color.R, Tolerance);
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

    /// <summary>
    ///     ⚠ <b>An element's own text is inside its own clip, and for a long time it was not.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>overflow</c> clips an element's <i>content</i>; the background and the border are
    ///         the two things it does not clip, which is why the push sits between them and the text.
    ///         Emitting the text first meant <c>overflow: hidden</c> clipped an element's children
    ///         and never its own string — so a label too long for a fixed-width column drew straight
    ///         across whatever was beside it, and five places in the editor had written
    ///         <c>overflow: hidden</c> on a text-bearing element believing otherwise.
    ///     </para>
    ///     <para>
    ///         It survived every kind of test the framework had because <b>a clip is invisible to
    ///         the element tree</b>: the box was the right size, the text was the right text, and the
    ///         glyphs went somewhere nothing was looking. It took a picture of a key/value row to
    ///         find, and this is the assertion that would have found it — the *order* of the
    ///         commands, which is the whole of what a clip is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_elements_own_text_is_clipped_by_its_own_overflow() {
        using var document = DrawnWithText(
            """
            root { width: 400px; height: 300px; }
            .box {
                width: 40px;
                height: 20px;
                overflow: hidden;
                background-color: #111111;
                border-width: 1px;
                border-color: #222222;
                white-space: nowrap;
            }
            """,
            document => document.Root.Add("div", classNames: "box").Text = "far too long for forty pixels"
        );

        var kinds = document.Drawing.Commands.Select(command => command.Kind).ToList();

        // The background and the border are outside the clip; the text is inside it. Asserted as a
        // sequence rather than as "a Text exists between two clip markers", because the position of
        // the push relative to the *background* is the other half of the rule and a containment
        // check would pass with the push moved above it.
        Assert.Equal(
            [
                DrawCommandKind.Rectangle,
                DrawCommandKind.Border,
                DrawCommandKind.ClipPush,
                DrawCommandKind.Text,
                DrawCommandKind.ClipPop
            ],
            kinds
        );
    }

    /// <summary>And an element that does not ask to clip still draws its text.</summary>
    /// <remarks>
    ///     The other half of the pair, and not a formality: the fix moved the emission inside an
    ///     <c>if</c> that had not been there, so the case with no clip at all is the one a careless
    ///     version drops entirely.
    /// </remarks>
    [Fact]
    public void An_element_that_does_not_clip_still_draws_its_text() {
        using var document = DrawnWithText(
            """
            root { width: 400px; height: 300px; }
            .box { width: 200px; height: 20px; }
            """,
            document => document.Root.Add("div", classNames: "box").Text = "visible"
        );

        Assert.Equal([DrawCommandKind.Text], document.Drawing.Commands.Select(command => command.Kind));
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

    [Fact]
    public void Opacity_fades_the_alpha_and_leaves_the_colour_alone() {
        // ⚠ Not `colour * alpha`, which would scale all four components — right in premultiplied
        // space and wrong here, where it darkens the colour towards black as well as fading it. The
        // red channel is what says which of the two happened.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #ff0000; opacity: 0.5; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        var command = Assert.Single(document.Drawing.Commands);

        Assert.Equal(0.5f, command.Color.A, Tolerance);
        Assert.True(command.Color.R > 0.99f);
    }

    [Fact]
    public void Opacity_brackets_a_group_rather_than_multiplying_down_the_tree() {
        // ⚠ `opacity` makes a group — CSS Compositing 1 § 3 — so a subtree that draws more than one
        // thing is rendered into a surface of its own and blended once, and its contents are *not*
        // faded individually on the way down. This test used to assert the multiplier and is kept
        // pointing at the same tree, because the tree is the one that tells them apart: the outer box
        // draws a background *and* contains a child, so the two models differ wherever they overlap.
        var document = new UiDocument(400f, 300f);

        // ⚠ Set explicitly even though it is now the default — see `DrawListBuilder.Compositing`,
        // which flipped once both executors could render a group. Written out because the test below
        // sets the opposite, and a pair of tests where only one names the setting reads as though the
        // other were testing something unrelated.
        document.Compositing = true;

        document.Load(
            """
            root { width: 400px; height: 300px; }
            .outer { width: 100px; height: 100px; background-color: #ff0000; opacity: 0.5; }
            .inner { width: 50px; height: 50px; background-color: #00ff00; opacity: 0.5; }
            """
        );

        document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner");
        document.Update();
        document.Draw();

        using var owned = document;
        var commands = document.Drawing.Commands;

        Assert.Equal(4, commands.Count);

        Assert.Equal(DrawCommandKind.LayerPush, commands[0].Kind);
        Assert.Equal(0.5f, commands[0].Color.A, Tolerance);

        // Inside the group, at full strength: the surface carries the fade, and fading here as well
        // is precisely the double-fade the group exists to stop.
        Assert.Equal(1f, commands[1].Color.A, Tolerance);

        // ⚠ The inner element's own group collapsed to a fade, because its subtree came to one
        // command — see `DrawList.Collapse`, where the two are shown to be the same arithmetic. So it
        // is 0.5 here and 0.25 on screen, which is the same number the multiplier used to produce and
        // is why the collapse is safe.
        Assert.Equal(0.5f, commands[2].Color.A, Tolerance);

        Assert.Equal(DrawCommandKind.LayerPop, commands[3].Kind);
    }

    /// <summary>With compositing off, opacity is still a multiplier all the way down.</summary>
    /// <remarks>
    ///     ⚠ <b><s>The default</s> no longer the default, and it is still worth having.</b> A group is
    ///     only a picture if whoever consumes the draw list can render an offscreen surface; both of
    ///     this repository's consumers now can, so the gate flipped — but the multiplier is still what
    ///     a consumer of somebody else's writing gets by turning it back off, and a path with a live
    ///     caller and no test is one that rots. `opacity` still does not inherit, so the child is faded
    ///     by its ancestor and by itself: reading it from the cascade would give 0.5 rather than 0.25.
    /// </remarks>
    [Fact]
    public void Opacity_still_multiplies_down_the_tree_when_nothing_can_composite() {
        // ⚠ Built here rather than through `Drawn`, because the setting has to be off *before* the
        // draw and `Drawn` draws. Turning it off afterwards and drawing again would test the same
        // thing, but only for as long as nobody moved the second draw.
        using var document = new UiDocument(400f, 300f);
        document.Compositing = false;

        document.Load(
            """
            root { width: 400px; height: 300px; }
            .outer { width: 100px; height: 100px; background-color: #ff0000; opacity: 0.5; }
            .inner { width: 50px; height: 50px; background-color: #00ff00; opacity: 0.5; }
            """
        );

        document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner");
        document.Update();
        document.Draw();

        Assert.Equal(2, document.Drawing.Commands.Count);
        Assert.Equal(0.5f, document.Drawing.Commands[0].Color.A, Tolerance);
        Assert.Equal(0.25f, document.Drawing.Commands[1].Color.A, Tolerance);
    }

    [Fact]
    public void A_fully_transparent_subtree_is_not_drawn_at_all() {
        // The one case where the cheapest thing to do is also exactly right: nothing under a zero
        // opacity can be visible, so the commands are not worth building to then blend away.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .outer { width: 100px; height: 100px; background-color: #ff0000; opacity: 0; }
            .inner { width: 50px; height: 50px; background-color: #00ff00; }
            """,
            document => document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner")
        );

        Assert.Empty(document.Drawing.Commands);
    }

    [Fact]
    public void An_element_with_no_opacity_is_untouched() {
        // The common path, and worth pinning: an alpha that arrived from the colour itself must not
        // be rewritten by a fade that nobody asked for.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; background-color: #ff000080; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        Assert.Equal(0.5f, Assert.Single(document.Drawing.Commands).Color.A, 0.01f);
    }

    [Fact]
    public void A_shadow_is_drawn_before_the_background_that_casts_it() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .card {
                width: 100px; height: 60px; background-color: #ffffff;
                box-shadow: 0px 4px 12px rgba(0, 0, 0, 0.5);
            }
            """,
            document => document.Root.Add("div", classNames: "card")
        );

        Assert.Equal(2, document.Drawing.Commands.Count);

        var shadow = document.Drawing.Commands[0];
        Assert.Equal(DrawCommandKind.Shadow, shadow.Kind);
        Assert.Equal(DrawCommandKind.Rectangle, document.Drawing.Commands[1].Kind);

        // Offset down by four and not spread, so the box keeps its size and moves.
        Assert.Equal(0f, shadow.X, Tolerance);
        Assert.Equal(4f, shadow.Y, Tolerance);
        Assert.Equal(100f, shadow.Width, Tolerance);
        Assert.Equal(60f, shadow.Height, Tolerance);

        // ⚠ Half the CSS blur radius. CSS's blur is the total distance the edge fades over and the
        // shader's is the half-extent either side of it, so passing the whole radius through makes
        // every shadow twice as soft as it was asked to be.
        Assert.Equal(6f, shadow.Thickness, Tolerance);
        Assert.Equal(0.5f, shadow.Color.A, Tolerance);
    }

    [Fact]
    public void A_spread_grows_the_shadow_and_its_corners_together() {
        // A spread that kept the original radius would give a shadow visibly squarer than the thing
        // casting it — most obvious on a pill, where the ends would stop being round.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .card {
                width: 100px; height: 60px; background-color: #ffffff; border-radius: 8px;
                box-shadow: 0px 0px 0px 5px #000000;
            }
            """,
            document => document.Root.Add("div", classNames: "card")
        );

        var shadow = document.Drawing.Commands[0];

        Assert.Equal(-5f, shadow.X, Tolerance);
        Assert.Equal(-5f, shadow.Y, Tolerance);
        Assert.Equal(110f, shadow.Width, Tolerance);
        Assert.Equal(70f, shadow.Height, Tolerance);
        Assert.Equal(13f, shadow.Radius, Tolerance);
    }

    [Fact]
    public void An_inset_shadow_is_refused_rather_than_drawn_on_the_wrong_side() {
        // Not a near miss: an inset shadow drawn as an outer one is a shadow outside the box that
        // was asked to have one inside it. Nothing is better than that.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .card {
                width: 100px; height: 60px; background-color: #ffffff;
                box-shadow: inset 0px 4px 12px #000000;
            }
            """,
            document => document.Root.Add("div", classNames: "card")
        );

        Assert.Equal(DrawCommandKind.Rectangle, Assert.Single(document.Drawing.Commands).Kind);
    }

    [Fact]
    public void A_shadow_fades_with_the_opacity_of_what_casts_it() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .card {
                width: 100px; height: 60px; opacity: 0.5;
                box-shadow: 0px 4px 12px #000000;
            }
            """,
            document => document.Root.Add("div", classNames: "card")
        );

        Assert.Equal(0.5f, Assert.Single(document.Drawing.Commands).Color.A, Tolerance);
    }

    [Fact]
    public void A_lifted_child_is_painted_over_its_later_siblings() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            div { width: 50px; height: 50px; position: absolute; }
            .first { background-color: #ff0000; z-index: 10; }
            .second { background-color: #00ff00; }
            .third { background-color: #0000ff; }
            """,
            document => {
                document.Root.Add("div", classNames: "first");
                document.Root.Add("div", classNames: "second");
                document.Root.Add("div", classNames: "third");
            }
        );

        // Document order is red, green, blue; paint order is green, blue, red.
        var painted = document.Drawing.Commands.Select(static command => command.Color.G > 0.5f
            ? "green"
            : command.Color.B > 0.5f
                ? "blue"
                : "red").ToArray();

        Assert.Equal(["green", "blue", "red"], painted);
    }

    [Fact]
    public void Equal_indices_keep_document_order() {
        // The sort has to be stable, or `z-10` on one child would shuffle the ones it did not touch
        // — and the shuffling would only show up where they overlap, which is to say rarely and
        // confusingly.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            div { width: 50px; height: 50px; position: absolute; z-index: 5; }
            .a { background-color: #ff0000; }
            .b { background-color: #00ff00; }
            .c { background-color: #0000ff; }
            """,
            document => {
                document.Root.Add("div", classNames: "a");
                document.Root.Add("div", classNames: "b");
                document.Root.Add("div", classNames: "c");
            }
        );

        Assert.True(document.Drawing.Commands[0].Color.R > 0.5f);
        Assert.True(document.Drawing.Commands[1].Color.G > 0.5f);
        Assert.True(document.Drawing.Commands[2].Color.B > 0.5f);
    }

    [Fact]
    public void A_negative_index_puts_a_child_behind_its_earlier_siblings() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            div { width: 50px; height: 50px; position: absolute; }
            .front { background-color: #ff0000; }
            .back { background-color: #00ff00; z-index: -1; }
            """,
            document => {
                document.Root.Add("div", classNames: "front");
                document.Root.Add("div", classNames: "back");
            }
        );

        Assert.True(document.Drawing.Commands[0].Color.G > 0.5f);
        Assert.True(document.Drawing.Commands[1].Color.R > 0.5f);
    }

    [Fact]
    public void Changing_an_index_reorders_the_next_frame() {
        // The cached order has to be invalidated by the style pass, not only by adding a child.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            div { width: 50px; height: 50px; position: absolute; }
            .a { background-color: #ff0000; }
            .b { background-color: #00ff00; }
            .a.lift { z-index: 3; }
            """,
            document => {
                document.Root.Add("div", classNames: "a");
                document.Root.Add("div", classNames: "b");
            }
        );

        Assert.True(document.Drawing.Commands[1].Color.G > 0.5f);

        document.Root.Children[0].AddClass("lift");
        document.Update();
        document.Draw();

        Assert.True(document.Drawing.Commands[1].Color.R > 0.5f);
    }
}
