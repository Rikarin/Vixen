// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>:empty</c>, driven the way a theme drives it.</summary>
/// <remarks>
///     <para>
///         The styling tests say the matcher answers correctly given a tree that has been told what
///         it holds. These say the tree is told — which is the half that lives in this assembly, and
///         the half a selector test cannot reach.
///     </para>
///     <para>
///         ⚠ <b>Text is the whole point.</b> CSS's <c>:empty</c> means "no child <i>nodes</i>", and a
///         run of text is a node in the DOM. Vixen puts text on the element instead
///         (<see cref="UiElement.Text" />), so a <c>:empty</c> implemented as "no children" would
///         have matched every label in every document — and the two rules in this repository that
///         wanted it are on leaves whose entire content is text, so it would have hidden exactly the
///         ones with something to show. That inversion is what these tests are for.
///     </para>
/// </remarks>
public class EmptyElementTests {
    const string Theme = """
        root { width: 200px; height: 100px; flex-direction: row; }
        lane { width: 30px; height: 10px; }
        lane:empty { display: none; }
        """;

    static UiDocument Documented(string css = Theme) {
        var document = new UiDocument(200f, 100f);
        document.Load(css);

        return document;
    }

    [Fact]
    public void A_leaf_with_text_is_not_empty_and_one_without_is() {
        using var document = Documented();

        var named = document.Root.Add("lane");
        var bare = document.Root.Add("lane");
        named.Text = "X";

        document.Update();

        Assert.Equal(30f, named.Width);
        Assert.Equal(0f, bare.Width);
    }

    [Fact]
    public void Giving_a_lane_its_letter_brings_it_back_and_taking_it_away_hides_it_again() {
        // The case the node graph actually produces: a pool of lanes is created bare and bound
        // afterwards, and rebinding to a shorter set of names writes "" back over a letter. A rule
        // that only worked on the way in would leave a stale letter's lane taking room forever.
        using var document = Documented();

        var lane = document.Root.Add("lane");
        document.Update();
        Assert.Equal(0f, lane.Width);

        lane.Text = "Y";
        document.Update();
        Assert.Equal(30f, lane.Width);

        lane.Text = "";
        document.Update();
        Assert.Equal(0f, lane.Width);

        // ⚠ Null and "" are both "no text" — the same emptiness test `OnTextChanged` already had to
        // make load-bearing for the layout tree, and the cascade has to agree with it or one of them
        // is wrong about the same element.
        lane.Text = "Z";
        document.Update();
        Assert.Equal(30f, lane.Width);

        lane.Text = null;
        document.Update();
        Assert.Equal(0f, lane.Width);
    }

    [Fact]
    public void A_child_makes_an_element_not_empty_even_with_no_text() {
        using var document = Documented(
            """
            root { width: 200px; height: 100px; flex-direction: row; }
            box { width: 30px; height: 10px; }
            box:empty { display: none; }
            dot { width: 4px; height: 4px; }
            """
        );

        var filled = document.Root.Add("box");
        var bare = document.Root.Add("box");
        filled.Add("dot");

        document.Update();

        Assert.Equal(30f, filled.Width);
        Assert.Equal(0f, bare.Width);
    }

    [Fact]
    public void Removing_the_last_child_makes_an_element_empty_again() {
        using var document = Documented(
            """
            root { width: 200px; height: 100px; flex-direction: row; }
            box { width: 30px; height: 10px; }
            box:empty { display: none; }
            dot { width: 4px; height: 4px; }
            """
        );

        var box = document.Root.Add("box");
        var dot = box.Add("dot");

        document.Update();
        Assert.Equal(30f, box.Width);

        document.Remove(dot);
        document.Update();

        Assert.Equal(0f, box.Width);
    }

    [Fact]
    public void Reparenting_carries_whether_an_element_holds_text() {
        // ⚠ Reparenting rebuilds a subtree's style slots rather than moving them, and every fact a
        // selector can read has to be copied across by hand. Text was the newest of them, and a copy
        // that forgot it would leave a moved label matching `:empty` — visible immediately, and
        // impossible to attribute to a drag two seconds earlier.
        using var document = Documented(
            """
            root { width: 200px; height: 100px; flex-direction: row; }
            bay { width: 100px; height: 50px; flex-direction: row; }
            lane { width: 30px; height: 10px; }
            lane:empty { display: none; }
            """
        );

        var left = document.Root.Add("bay");
        var right = document.Root.Add("bay");

        var named = left.Add("lane");
        var bare = left.Add("lane");
        named.Text = "X";

        document.Update();
        Assert.Equal(30f, named.Width);
        Assert.Equal(0f, bare.Width);

        document.Reparent(named, right);
        document.Reparent(bare, right);
        document.Update();

        Assert.Equal(30f, named.Width);
        Assert.Equal(0f, bare.Width);
    }
}
