// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Declarations written on an element rather than in a stylesheet.</summary>
public class InlineStyleTests {
    const float Tolerance = 0.001f;

    static UiDocument Documented(string css = "root { width: 400px; height: 300px; } .box { width: 40px; height: 20px; }") {
        var document = new UiDocument(400f, 300f);
        document.Load(css);

        return document;
    }

    [Fact]
    public void A_declaration_written_on_an_element_reaches_the_layout() {
        using var document = Documented();

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        Assert.Equal(40f, box.Width, Tolerance);

        box.SetStyle("width", "137px");
        document.Update();

        Assert.Equal(137f, box.Width, Tolerance);
    }

    [Fact]
    public void It_beats_a_rule_however_specific() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            div#target.box { width: 40px; height: 20px; }
        """);

        var box = document.Root.Add("div", "target", "box");
        box.SetStyle("width", "90px");

        document.Update();

        // What inline means in the cascade. It is also why a control should write the fewest
        // properties it can: whatever it writes, no theme can take back.
        Assert.Equal(90f, box.Width, Tolerance);
    }

    [Fact]
    public void Clearing_one_gives_the_stylesheet_back() {
        using var document = Documented();

        var box = document.Root.Add("div", classNames: "box");
        box.SetStyle("width", "90px");
        document.Update();

        Assert.Equal(90f, box.Width, Tolerance);

        box.SetStyle("width", null);
        document.Update();

        Assert.Equal(40f, box.Width, Tolerance);
        Assert.False(box.HasInlineStyle);
    }

    [Fact]
    public void It_reads_back_what_was_declared_and_not_what_was_computed() {
        using var document = Documented();

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        // Forty pixels wide, and it never said so.
        Assert.Null(box.GetStyle("width"));

        box.SetStyle("width", "90px");
        Assert.Equal("90px", box.GetStyle("width"));
    }

    [Fact]
    public void Two_properties_coexist_and_either_can_go() {
        using var document = Documented();

        var box = document.Root.Add("div", classNames: "box");
        box.SetStyle("width", "90px");
        box.SetStyle("height", "70px");

        document.Update();

        Assert.Equal(90f, box.Width, Tolerance);
        Assert.Equal(70f, box.Height, Tolerance);

        box.SetStyle("width", null);
        document.Update();

        Assert.Equal(40f, box.Width, Tolerance);
        Assert.Equal(70f, box.Height, Tolerance);
    }

    [Fact]
    public void Writing_the_same_value_again_costs_nothing() {
        using var document = Documented();

        var box = document.Root.Add("div", classNames: "box");
        box.SetStyle("width", "90px");
        document.Update();

        box.SetStyle("width", "90px");

        // ⚠ The document is not dirty, so a splitter that has not moved does not re-cascade the
        // subtree under it sixty times a second.
        Assert.False(document.Update());
    }

    [Fact]
    public void Changing_a_value_keeps_the_handle_rather_than_taking_a_new_one() {
        using var document = Documented();

        var box = document.Root.Add("div", classNames: "box");
        box.SetStyle("width", "10px");

        var blocks = document.Styles.InlineStyles.Count;

        for (var i = 0; i < 20; i++) {
            box.SetStyle("width", $"{i}px");
        }

        // ⚠ The regression test for a drag that allocates a block per frame. The count is also what
        // the style-sharing key carries, so an element that kept taking new handles would never
        // look like anything — including itself, a frame ago.
        Assert.Equal(blocks, document.Styles.InlineStyles.Count);
    }

    [Fact]
    public void It_survives_a_compaction() {
        using var document = Documented();

        var kept = document.Root.Add("div", classNames: "box");
        kept.SetStyle("width", "90px");

        // Enough removals to take the tombstones past the compaction floor and past the live count.
        for (var i = 0; i < 200; i++) {
            document.Root.Add("div", classNames: "box").Remove();
        }

        document.Update();

        Assert.True(document.StyleCompactions > 0);
        Assert.Equal(90f, kept.Width, Tolerance);
    }
}
