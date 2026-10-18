// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>::before</c> and <c>::after</c>: a box the sheet generates, laid out, painted and hit as one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion is on a box, a draw command, a hit or a name, and none is on the
///         compiled selector.</b> Doc 43's F6 was a pseudo-element that compiled cleanly and styled
///         the paragraph; a test that asked the compiler would have passed against it. The styling
///         suite checks the cascade; these check that something reads it.
///     </para>
///     <para>
///         Colours are the probe: a red <c>::before</c> beside a green element is two commands that
///         cannot be confused, and the F6 failure is the green one turning red.
///     </para>
/// </remarks>
public class GeneratedContentTests {
    const float Tolerance = 0.01f;

    static readonly Text.FontFace Face = LoadFace();

    static Text.FontFace LoadFace() {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return Text.FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    const string Row = """
        root { width: 400px; height: 300px; align-items: flex-start; }
        .card { flex-direction: row; align-items: flex-start; }
        .item { width: 20px; height: 20px; background-color: #00ff00; }
        """;

    static UiDocument Drawn(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();
        document.Draw();
        return document;
    }

    static bool IsRed(DrawCommand command) => command.Color.R > 0.99f && command.Color.G < 0.01f;

    static bool IsGreen(DrawCommand command) => command.Color.G > 0.99f && command.Color.R < 0.01f;

    [Fact]
    public void An_empty_string_before_is_a_box_laid_out_ahead_of_the_children_and_painted() {
        UiElement card = null!;

        using var document = Drawn(
            Row + ".card::before { content: ''; width: 10px; height: 10px; background-color: #ff0000; }",
            document => {
                card = document.Root.Add("div", classNames: "card");
                card.Add("div", classNames: "item");
            }
        );

        var red = Assert.Single(document.Drawing.Commands, IsRed);
        var green = Assert.Single(document.Drawing.Commands, IsGreen);

        Assert.Equal(0f, red.X, Tolerance);
        Assert.Equal(10f, red.Width, Tolerance);

        // The child is pushed along by the generated box: it is in the box tree.
        Assert.Equal(10f, green.X, Tolerance);

        // ⚠ And it is in nothing else. One element child, and it is the item.
        Assert.Single(card.Children);
        Assert.True(card.Children[0].HasClass("item"));

        // Painted first, as the first child box.
        Assert.True(document.Drawing.Commands.ToList().IndexOf(red) < document.Drawing.Commands.ToList().IndexOf(green));
    }

    [Fact]
    public void An_after_is_the_last_box_and_a_child_added_later_lands_ahead_of_it() {
        UiElement card = null!;

        using var document = Drawn(
            Row + ".card::after { content: ''; width: 10px; height: 10px; background-color: #ff0000; }",
            document => {
                card = document.Root.Add("div", classNames: "card");
                card.Add("div", classNames: "item");
            }
        );

        Assert.Equal(20f, Assert.Single(document.Drawing.Commands, IsRed).X, Tolerance);

        // ⚠ The box is the card's last LAYOUT child, so a child appended to the card's element list
        // must be inserted ahead of it rather than after it.
        card.Add("div", classNames: "item");
        document.Update();
        document.Draw();

        Assert.Equal(40f, Assert.Single(document.Drawing.Commands, IsRed).X, Tolerance);
        Assert.Equal([0f, 20f], document.Drawing.Commands.Where(IsGreen).Select(command => command.X).ToArray());
    }

    [Fact]
    public void A_before_rule_does_not_style_the_element_it_was_written_against() {
        // ⚠ Doc 43's F6, from the side that matters: `p::before { color: red }` used to compile and
        // turn the paragraph red. The paragraph is green and the box is red, or this is F6 again.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; align-items: flex-start; }
            p { width: 50px; height: 30px; background-color: #00ff00; }
            p::before { content: ''; width: 10px; height: 10px; background-color: #ff0000; }
            """,
            document => document.Root.Add("p")
        );

        var green = Assert.Single(document.Drawing.Commands, IsGreen);
        Assert.Equal(50f, green.Width, Tolerance);
        Assert.Equal(30f, green.Height, Tolerance);

        var red = Assert.Single(document.Drawing.Commands, IsRed);
        Assert.Equal(10f, red.Width, Tolerance);
    }

    [Theory]
    [InlineData(".card::before { width: 10px; height: 10px; background-color: #ff0000; }")]
    [InlineData(".card::before { content: none; width: 10px; height: 10px; background-color: #ff0000; }")]
    [InlineData(".card::before { content: normal; width: 10px; height: 10px; background-color: #ff0000; }")]
    [InlineData(".card::before { content: ''; display: none; width: 10px; height: 10px; background-color: #ff0000; }")]
    [InlineData(".card::before { content: counter(item); width: 10px; height: 10px; background-color: #ff0000; }")]
    public void No_string_content_is_no_box(string rule) {
        UiElement card = null!;

        using var document = Drawn(
            Row + rule,
            document => {
                card = document.Root.Add("div", classNames: "card");
                card.Add("div", classNames: "item");
            }
        );

        Assert.DoesNotContain(document.Drawing.Commands, IsRed);
        Assert.Equal(0f, Assert.Single(document.Drawing.Commands, IsGreen).X, Tolerance);
        Assert.Null(card.GeneratedBefore);
    }

    [Fact]
    public void A_class_change_makes_the_box_appear_and_go_through_the_incremental_pass() {
        UiElement card = null!;

        using var document = Drawn(
            Row + ".card.on::before { content: ''; width: 10px; height: 10px; background-color: #ff0000; }",
            document => {
                card = document.Root.Add("div", classNames: "card");
                card.Add("div", classNames: "item");
            }
        );

        Assert.DoesNotContain(document.Drawing.Commands, IsRed);

        card.AddClass("on");
        document.Update();
        document.Draw();

        Assert.Equal(0f, Assert.Single(document.Drawing.Commands, IsRed).X, Tolerance);
        Assert.Equal(10f, Assert.Single(document.Drawing.Commands, IsGreen).X, Tolerance);

        card.RemoveClass("on");
        document.Update();
        document.Draw();

        Assert.DoesNotContain(document.Drawing.Commands, IsRed);
        Assert.Equal(0f, Assert.Single(document.Drawing.Commands, IsGreen).X, Tolerance);
        Assert.Null(card.GeneratedBefore);
    }

    [Fact]
    public void Structural_selectors_do_not_see_the_generated_box() {
        // ⚠ The reason the box has no style-tree slot. A `::before` that counted as a child would
        // take `:first-child` from the real first child and make every empty element non-empty.
        using var document = Drawn(
            """
            root { width: 400px; height: 300px; align-items: flex-start; }
            .card { flex-direction: row; align-items: flex-start; }
            .card::before { content: ''; width: 10px; height: 10px; background-color: #ff0000; }
            .item { width: 20px; height: 20px; background-color: #0000ff; }
            .card > .item:first-child { background-color: #00ff00; }
            .empty { width: 5px; height: 5px; }
            .empty:empty { background-color: #00ff00; }
            """,
            document => {
                var card = document.Root.Add("div", classNames: "card");
                card.Add("div", classNames: "item");
                document.Root.Add("div", classNames: ["card", "empty"]);
            }
        );

        // The real first child is still the first child, and the card with only a generated box is
        // still empty.
        Assert.Equal(2, document.Drawing.Commands.Count(IsGreen));
    }

    [Fact]
    public void A_point_on_a_generated_box_hits_the_element_that_generated_it() {
        UiElement card = null!;
        UiElement item = null!;

        using var document = Drawn(
            Row + ".card::before { content: ''; width: 10px; height: 10px; background-color: #ff0000; }",
            document => {
                card = document.Root.Add("div", classNames: "card");
                item = card.Add("div", classNames: "item");
            }
        );

        // On the `::before`: the card, never the box — which has no handlers and no route.
        Assert.Same(card, document.HitTest(5f, 5f));

        // On the item, still the item.
        Assert.Same(item, document.HitTest(15f, 5f));
    }

    [Fact]
    public void The_text_of_a_generated_box_is_its_content_and_it_measures_it() {
        UiElement card = null!;

        using var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Face);
        document.Load(
            """
            root { width: 400px; height: 300px; align-items: flex-start; font-family: Test; font-size: 16px; }
            .card { flex-direction: row; align-items: flex-start; }
            .card::before { content: "\2022  ab" 'c\'d'; }
            """
        );

        card = document.Root.Add("div", classNames: "card");
        card.Add("div", classNames: "item");
        document.Update();

        var box = Assert.IsType<GeneratedBox>(card.GeneratedBefore);

        // The escape is a code point and swallows one space; the second space is content; two
        // strings are one.
        Assert.Equal("• abc'd", box.Text);
        Assert.True(box.Width > 0f, "the box measures its text");
    }

    [Fact]
    public void An_inline_generated_box_joins_the_line_of_its_inline_siblings() {
        // ⚠ The CSS default for a pseudo-element, written out because Vixen's is flex. An inline
        // leaf's collapsing edges read its NEIGHBOURS, and a generated box is in no sibling list —
        // so this is the path most likely to index a box that is not there.
        UiElement para = null!;
        UiElement word = null!;

        using var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Face);
        document.Load(
            """
            root { width: 400px; height: 300px; align-items: flex-start; font-family: Test; font-size: 16px; }
            .para { display: block; width: 300px; }
            .para::before { content: "AB "; display: inline; }
            .word { display: inline; }
            """
        );

        para = document.Root.Add("div", classNames: "para");
        word = para.Add("span", classNames: "word");
        word.Text = "CD";
        document.Update();

        var box = Assert.IsType<GeneratedBox>(para.GeneratedBefore);

        Assert.True(box.Width > 0f, "the inline box measures its text");

        // One line, the box first: the word starts after it and on the same row.
        Assert.True(word.AbsoluteLeft >= box.AbsoluteLeft + box.Width - 0.5f, $"the word at {word.AbsoluteLeft} overlaps the box ending at {box.AbsoluteLeft + box.Width}");
        Assert.Equal(box.AbsoluteTop, word.AbsoluteTop, 1f);
    }

    [Fact]
    public void An_element_with_text_of_its_own_generates_nothing_and_does_not_throw() {
        UiElement label = null!;

        using var document = Drawn(
            Row + ".label::before { content: ''; width: 10px; height: 10px; background-color: #ff0000; }",
            document => {
                label = document.Root.Add("div", classNames: "label");
                label.Text = "Name";
            }
        );

        Assert.Null(label.GeneratedBefore);
        Assert.DoesNotContain(document.Drawing.Commands, IsRed);

        // And the other direction: a box that exists is dropped before the element starts measuring
        // itself, which the layout tree would otherwise refuse.
        label.Text = null;
        document.Update();
        Assert.NotNull(label.GeneratedBefore);

        label.Text = "Name";
        document.Update();
        Assert.Null(label.GeneratedBefore);
    }

    [Fact]
    public void A_generated_box_joins_a_named_from_content_role_and_is_never_a_node() {
        using var document = new UiDocument(400f, 300f);
        document.Load(
            """
            .star::before { content: "\2605 "; }
            .star::after { content: " (3)"; }
            """
        );

        var button = document.Root.Add("div", classNames: "star");
        button.Role = AccessibleRole.Button;
        button.Add("div");

        var field = document.Root.Add("div", classNames: "star");
        field.Role = AccessibleRole.TextBox;
        field.Add("div");

        document.Update();

        Assert.Equal("★ (3)", button.AccessibleName);

        // ⚠ A text box never takes its name from content — a theme's decorative icon must not name
        // a field, or an unlabelled field stops being findable.
        Assert.Null(field.AccessibleName);

        // The two elements are the tree's only nodes: neither box became one.
        var tree = AccessibilityTree.Capture(document.Root);
        Assert.Equal(2, tree.Count);
    }

    [Theory]
    [InlineData("\"a\"", "a")]
    [InlineData("'a' \"b\"", "ab")]
    [InlineData("\"\"", "")]
    [InlineData("\"\\\"\"", "\"")]
    [InlineData("\"\\41 B\"", "AB")]
    [InlineData("none", null)]
    [InlineData("attr(title)", null)]
    [InlineData("\"a\" attr(title)", null)]
    [InlineData("\"unterminated", null)]
    public void Content_is_read_as_strings_and_only_strings(string value, string? expected) =>
        Assert.Equal(expected, UiDocument.ParseContent(value));
}
