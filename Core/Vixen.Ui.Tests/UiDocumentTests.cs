// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A stylesheet, an element tree, and the rectangles they produce.</summary>
/// <remarks>
///     Everything before this could be judged by somebody else's conformance suite. This is the
///     first thing that can only be judged by whether the numbers are the ones a person would
///     expect — so the cases are arithmetic that is checkable by hand rather than golden values.
/// </remarks>
public class UiDocumentTests {
    const float Tolerance = 0.001f;

    [Fact]
    public void A_stylesheet_and_a_tree_produce_geometry() {
        using var document = new UiDocument(800f, 600f);

        document.Load("""
            root { width: 800px; height: 600px; flex-direction: row; }
            .side { width: 200px; }
            .main { flex-grow: 1; }
        """);

        var side = document.Root.Add("div", classNames: "side");
        var main = document.Root.Add("div", classNames: "main");

        Assert.True(document.Update());

        // A row of a fixed sidebar and a main pane taking the rest — the first thing in this phase
        // that is a user interface rather than a subsystem.
        Assert.Equal(200f, side.Width, Tolerance);
        Assert.Equal(600f, side.Height, Tolerance);
        Assert.Equal(200f, main.Left, Tolerance);
        Assert.Equal(600f, main.Width, Tolerance);
    }

    [Fact]
    public void The_cascade_reaches_layout_through_a_selector_and_not_only_a_tag() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; }
            div { height: 10px; }
            div.tall { height: 50px; }
            #named { height: 90px; }
        """);

        var plain = document.Root.Add("div");
        var tall = document.Root.Add("div", classNames: "tall");
        var named = document.Root.Add("div", "named", "tall");

        document.Update();

        Assert.Equal(10f, plain.Height, Tolerance);
        Assert.Equal(50f, tall.Height, Tolerance);

        // An id beats a class beats a tag, and it beat them all the way through to a rectangle.
        Assert.Equal(90f, named.Height, Tolerance);
    }

    [Fact]
    public void Font_size_is_inherited_and_em_measures_against_the_element_s_own() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; font-size: 20px; }
            .child { font-size: 1.5em; width: 2em; }
        """);

        var child = document.Root.Add("div", classNames: "child");
        document.Update();

        // 1.5 x 20 is 30, and 2em of *that* is 60. Measuring against the parent's 20 would give 40,
        // which is the mistake this arithmetic is chosen to catch.
        Assert.Equal(30f, child.FontSize, Tolerance);
        Assert.Equal(60f, child.Width, Tolerance);
    }

    [Fact]
    public void An_inherited_font_size_reaches_a_grandchild_that_never_declared_one() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; font-size: 10px; }
            .middle { font-size: 2em; }
            .leaf { width: 3em; }
        """);

        var middle = document.Root.Add("div", classNames: "middle");
        var leaf = middle.Add("div", classNames: "leaf");

        document.Update();

        Assert.Equal(20f, middle.FontSize, Tolerance);
        Assert.Equal(20f, leaf.FontSize, Tolerance);
        Assert.Equal(60f, leaf.Width, Tolerance);
    }

    [Fact]
    public void The_same_relative_font_size_declared_twice_compounds() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; font-size: 10px; }
            .step { font-size: 1.5em; }
        """);

        var first = document.Root.Add("div", classNames: "step");
        var second = first.Add("div", classNames: "step");
        var third = second.Add("div", classNames: "step");

        document.Update();

        // ⚠ The case that decided how font size inherits. This cascade inherits *specified* values,
        // so a child that inherited the text `1.5em` would resolve it against its own parent a
        // second time and a size meant to apply once would compound at every level. Removing
        // `font-size` from the inherited list and inheriting the computed pixel value instead is
        // what CSS does — and it also keeps this case right, where telling inheritance from an
        // identical redeclaration by comparing values could not have.
        Assert.Equal(15f, first.FontSize, Tolerance);
        Assert.Equal(22.5f, second.FontSize, Tolerance);
        Assert.Equal(33.75f, third.FontSize, Tolerance);
    }

    [Fact]
    public void Viewport_units_follow_the_surface() {
        using var document = new UiDocument(1000f, 500f);

        document.Load("root { width: 50vw; height: 20vh; }");
        document.Update();

        Assert.Equal(500f, document.Root.Width, Tolerance);
        Assert.Equal(100f, document.Root.Height, Tolerance);

        document.Resize(600f, 400f);
        document.Update();

        Assert.Equal(300f, document.Root.Width, Tolerance);
        Assert.Equal(80f, document.Root.Height, Tolerance);
    }

    [Fact]
    public void A_class_added_after_the_first_pass_changes_the_geometry() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; }
            div { height: 10px; }
            div.grown { height: 100px; }
        """);

        var element = document.Root.Add("div");
        document.Update();
        Assert.Equal(10f, element.Height, Tolerance);

        element.AddClass("grown");
        document.Update();
        Assert.Equal(100f, element.Height, Tolerance);

        element.RemoveClass("grown");
        document.Update();
        Assert.Equal(10f, element.Height, Tolerance);
    }

    [Fact]
    public void A_state_change_is_a_style_change() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; }
            div { height: 10px; }
            div:hover { height: 40px; }
        """);

        var element = document.Root.Add("div");
        document.Update();
        Assert.Equal(10f, element.Height, Tolerance);

        element.State = ElementState.Hover;
        document.Update();
        Assert.Equal(40f, element.Height, Tolerance);
    }

    [Fact]
    public void An_unchanged_document_does_no_work_at_all_on_the_next_frame() {
        using var document = new UiDocument(400f, 300f);

        document.Load("root { width: 400px; height: 300px; } div { height: 10px; }");

        for (var i = 0; i < 50; i++) {
            document.Root.Add("div");
        }

        Assert.True(document.Update());
        Assert.Equal(51, document.StylesApplied);

        // The claim the whole styling design is built on: nothing changed, so nothing is rebuilt.
        Assert.False(document.Update());
        Assert.Equal(0, document.StylesApplied);
    }

    [Fact]
    public void Only_the_elements_whose_style_actually_changed_are_rebuilt() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; }
            div { height: 10px; }
            div.grown { height: 100px; }
        """);

        for (var i = 0; i < 20; i++) {
            document.Root.Add("div");
        }

        document.Update();

        document.Root.Children[3].AddClass("grown");
        document.Update();

        // One element changed class. The other twenty resolved to the same interned style as
        // before, and a pointer comparison is all it took to know that.
        Assert.Equal(1, document.StylesApplied);
    }

    [Fact]
    public void An_ancestor_s_font_size_rebuilds_a_descendant_that_did_not_change() {
        using var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; font-size: 10px; }
            .middle { font-size: 10px; }
            .middle.big { font-size: 30px; }
            .leaf { width: 2em; }
        """);

        var middle = document.Root.Add("div", classNames: "middle");
        var leaf = middle.Add("div", classNames: "leaf");

        document.Update();
        Assert.Equal(20f, leaf.Width, Tolerance);

        middle.AddClass("big");
        document.Update();

        // ⚠ The leaf's own declarations did not change and its computed style is the same interned
        // object it was before — so a check on the style alone would skip it, and `2em` would keep
        // meaning twenty pixels while the text around it doubled. The resolved font size has to be
        // part of the test.
        Assert.Equal(60f, leaf.Width, Tolerance);
        Assert.Equal(2, document.StylesApplied);
    }

    [Fact]
    public void The_root_is_laid_out_against_the_surface_rather_than_against_nothing() {
        using var document = new UiDocument(320f, 240f);

        document.Load("root { flex-grow: 1; }");
        document.Update();

        Assert.Equal(320f, document.Root.Width, Tolerance);
        Assert.Equal(240f, document.Root.Height, Tolerance);
    }
}
