// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>visibility</c>: the box keeps its space, paints nothing, and stops catching the pointer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The <c>shown</c> branch in <see cref="DrawListBuilder" /> had no test at all, and
///         that is the reason this file exists rather than the keyword being a one-line change.</b>
///         The property was read; nothing anywhere asserted that reading it did anything, so the
///         difference between "honoured" and "parsed and dropped" was invisible to the suite. Two of
///         the four behaviours below turned out to be missing when they were finally written down.
///     </para>
///     <para>
///         ⚠ <b>Why a draw-list test and not a screenshot.</b> The whole content of the property is
///         which commands are absent, and an absent command is exactly what a pixel comparison is
///         worst at telling you about — a blank region is equally consistent with the element being
///         hidden, mispositioned, the wrong colour, or never built. Counting commands says which.
///     </para>
/// </remarks>
public class VisibilityTests {
    const float Tolerance = 0.001f;

    static UiDocument Drawn(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();
        document.Draw();

        return document;
    }

    /// <summary>The baseline the three keyword tests are a difference against.</summary>
    [Fact]
    public void A_visible_box_paints_and_occupies_its_space() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 60px; height: 40px; background-color: #ff0000; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        var command = Assert.Single(document.Drawing.Commands);
        Assert.Equal(DrawCommandKind.Rectangle, command.Kind);

        var box = document.Root.Children[0];
        Assert.Equal(60f, box.Width, Tolerance);
        Assert.Equal(40f, box.Height, Tolerance);
    }

    /// <summary>
    ///     The difference from <c>display: none</c>, and the whole reason CSS has both.
    /// </summary>
    [Fact]
    public void Hidden_keeps_the_rectangle_in_layout_and_takes_it_out_of_the_draw_list() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; display: block; }
            .box { width: 60px; height: 40px; background-color: #ff0000; visibility: hidden; }
            .after { width: 10px; height: 25px; background-color: #00ff00; }
            """,
            document => {
                document.Root.Add("div", classNames: "box");
                document.Root.Add("div", classNames: "after");
            }
        );

        // The hidden box paints nothing; its sibling still paints.
        var command = Assert.Single(document.Drawing.Commands);
        Assert.Equal(10f, command.Width, Tolerance);

        // ⚠ The load-bearing half, and the reason the root is a block rather than the default flex
        // row: stacked, the sibling's offset is the hidden box's height, so the number below is the
        // space the hidden box still occupies. It reads 0 under `display: none` and 40 under
        // `visibility: hidden`, which is the entire difference between the two properties.
        var box = document.Root.Children[0];
        Assert.Equal(60f, box.Width, Tolerance);
        Assert.Equal(40f, box.Height, Tolerance);
        Assert.Equal(40f, document.Root.Children[1].AbsoluteTop, Tolerance);
    }

    /// <summary>
    ///     CSS 2.1 §11.2: on any box that is not a table row or column, <c>collapse</c> means
    ///     <c>hidden</c>. This engine has no table formatting context, so that is every box in it.
    /// </summary>
    [Fact]
    public void Collapse_reads_as_hidden_because_there_are_no_table_rows_to_mean_anything_else() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 60px; height: 40px; background-color: #ff0000; visibility: collapse; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        Assert.Empty(document.Drawing.Commands);
        Assert.Equal(40f, document.Root.Children[0].Height, Tolerance);
    }

    /// <summary>
    ///     ⚠ The case that makes <c>visible</c> a real keyword rather than a spelling of the initial
    ///     value, and the one thing <c>display</c> cannot express.
    /// </summary>
    /// <remarks>
    ///     It works because the property inherits and the paint walk reads it per element, so the
    ///     child is not "skipped inside a skipped parent" — it never inherited the value in the first
    ///     place. A builder that instead carried a hidden flag down the recursion would pass every
    ///     other test in this file and fail this one.
    /// </remarks>
    [Fact]
    public void A_visible_child_paints_inside_a_hidden_parent() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .parent { width: 100px; height: 80px; background-color: #ff0000; visibility: hidden; }
            .child { width: 20px; height: 15px; background-color: #0000ff; visibility: visible; }
            """,
            document => document.Root.Add("div", classNames: "parent").Add("div", classNames: "child")
        );

        var command = Assert.Single(document.Drawing.Commands);
        Assert.Equal(20f, command.Width, Tolerance);
        Assert.Equal(15f, command.Height, Tolerance);
    }

    /// <summary>Hiding is inherited, so a subtree goes with its root.</summary>
    [Fact]
    public void A_hidden_parent_hides_a_child_that_says_nothing() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .parent { width: 100px; height: 80px; background-color: #ff0000; visibility: hidden; }
            .child { width: 20px; height: 15px; background-color: #0000ff; }
            """,
            document => document.Root.Add("div", classNames: "parent").Add("div", classNames: "child")
        );

        Assert.Empty(document.Drawing.Commands);
    }

    /// <summary>
    ///     CSS UI §5.2: an invisible box is not a pointer target. This was the missing half — the
    ///     paint walk honoured the property and the hit test did not.
    /// </summary>
    [Fact]
    public void A_hidden_box_does_not_catch_the_pointer_and_what_is_behind_it_does() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .under { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px; }
            .over { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px; visibility: hidden; }
            """,
            document => {
                document.Root.Add("div", classNames: "under");
                document.Root.Add("div", classNames: "over");
            }
        );

        var under = document.Root.Children[0];
        var over = document.Root.Children[1];

        Assert.False(over.IsHitTestVisible);
        Assert.True(under.IsHitTestVisible);

        // The pointer falls through the hidden overlay to the box underneath it.
        Assert.Same(under, document.HitTest(50f, 50f));
    }

    /// <summary>
    ///     And the mirror of the paint case: a visible island inside a hidden subtree is clickable,
    ///     which is what makes reading the property per element rather than per subtree the right
    ///     call in the hit test too.
    /// </summary>
    [Fact]
    public void A_visible_child_of_a_hidden_parent_is_still_a_pointer_target() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .parent { width: 100px; height: 80px; visibility: hidden; }
            .child { width: 40px; height: 30px; visibility: visible; }
            """,
            document => document.Root.Add("div", classNames: "parent").Add("div", classNames: "child")
        );

        var parent = document.Root.Children[0];
        var child = parent.Children[0];

        Assert.False(parent.IsHitTestVisible);
        Assert.True(child.IsHitTestVisible);
        Assert.Same(child, document.HitTest(10f, 10f));
    }

    /// <summary>Collapse is hidden here too, rather than only in the paint walk.</summary>
    [Fact]
    public void Collapse_is_not_a_pointer_target_either() {
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; }
            .box { width: 100px; height: 80px; visibility: collapse; }
            """,
            document => document.Root.Add("div", classNames: "box")
        );

        Assert.False(document.Root.Children[0].IsHitTestVisible);
    }
}
