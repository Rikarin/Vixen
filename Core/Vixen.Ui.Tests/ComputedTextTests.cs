// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The computed-value stage for the four inherited text lengths.</summary>
/// <remarks>
///     ⚠ <b>The bug this closes was latent, which is why it is worth stating what "closed" means.</b>
///     Nothing in the framework read <c>line-height</c>, <c>letter-spacing</c>, <c>word-spacing</c> or
///     <c>text-indent</c> — <c>TextRun</c> uses the font's own metrics — so the wrong inheritance was
///     invisible and would have stayed invisible right up until the first consumer, at which point it
///     would have read as a text-rendering bug. These assert the values, not their effect, because
///     there is no effect yet.
/// </remarks>
public class ComputedTextTests {
    const float Tolerance = 0.001f;

    static UiDocument Documented(string css) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);

        return document;
    }

    [Fact]
    public void An_em_is_measured_against_the_element_that_declared_it() {
        using var document = Documented("""
            root { font-size: 16px; }
            .panel { font-size: 16px; letter-spacing: 0.5em; }
            .heading { font-size: 32px; }
        """);

        var panel = document.Root.Add("div", classNames: "panel");
        var heading = panel.Add("div", classNames: "heading");

        document.Update();

        Assert.Equal(8f, panel.TextStyle.LetterSpacing, Tolerance);

        // ⚠ The whole of the fix. Inheriting the *specified* `0.5em` would resolve it a second time
        // against the heading's own 32px and give 16 — the heading would get twice the spacing the
        // panel asked every descendant to have, which is the opposite of what inheritance means.
        Assert.Equal(8f, heading.TextStyle.LetterSpacing, Tolerance);
    }

    [Fact]
    public void And_it_does_not_compound_down_a_deep_tree() {
        using var document = Documented("""
            root { font-size: 10px; word-spacing: 2em; }
            .step { font-size: 20px; }
        """);

        var first = document.Root.Add("div", classNames: "step");
        var second = first.Add("div", classNames: "step");
        var third = second.Add("div", classNames: "step");

        document.Update();

        foreach (var element in new[] { first, second, third }) {
            Assert.Equal(20f, element.TextStyle.WordSpacing, Tolerance);
        }
    }

    [Fact]
    public void A_bare_number_line_height_keeps_being_a_number() {
        using var document = Documented("""
            root { font-size: 16px; line-height: 1.5; }
            .heading { font-size: 32px; }
        """);

        var heading = document.Root.Add("div", classNames: "heading");
        document.Update();

        Assert.Equal(24f, document.Root.TextStyle.LineHeight, Tolerance);

        // ⚠ **The exception to the whole stage, and CSS is explicit about it.** `line-height: 1.5`
        // means "one and a half times whatever size this text is", so it inherits as the number and
        // re-resolves. Computing it to 24px on the root and inheriting *that* would give a 32px
        // heading 24px lines — leading shorter than the text is tall, which is the bug the number
        // form exists to prevent.
        Assert.Equal(48f, heading.TextStyle.LineHeight, Tolerance);
        Assert.Equal(1.5f, heading.TextStyle.LineHeightFactor);
    }

    [Fact]
    public void A_line_height_with_a_unit_does_not() {
        using var document = Documented("""
            root { font-size: 16px; line-height: 1.5em; }
            .heading { font-size: 32px; }
        """);

        var heading = document.Root.Add("div", classNames: "heading");
        document.Update();

        // The other side of the same rule: a length is computed once and inherited, so the heading
        // gets the root's 24px. Written together with the test above because the pair is the whole
        // distinction, and either one alone reads like an arbitrary choice.
        Assert.Equal(24f, heading.TextStyle.LineHeight, Tolerance);
        Assert.Null(heading.TextStyle.LineHeightFactor);
    }

    [Fact]
    public void A_declaration_of_its_own_wins_over_what_it_would_inherit() {
        using var document = Documented("""
            root { font-size: 16px; text-indent: 1em; }
            .odd { font-size: 16px; text-indent: 3em; }
        """);

        var odd = document.Root.Add("div", classNames: "odd");
        var below = odd.Add("div");

        document.Update();

        Assert.Equal(16f, document.Root.TextStyle.TextIndent, Tolerance);
        Assert.Equal(48f, odd.TextStyle.TextIndent, Tolerance);
        Assert.Equal(48f, below.TextStyle.TextIndent, Tolerance);
    }

    [Fact]
    public void An_element_nobody_declared_anything_for_gets_the_initial_values() {
        using var document = Documented("root { font-size: 16px; }");
        var box = document.Root.Add("div");

        document.Update();

        Assert.Equal(ComputedText.Initial, box.TextStyle);
    }

    [Fact]
    public void A_percentage_is_refused_rather_than_guessed_at() {
        using var document = Documented("""
            root { font-size: 16px; text-indent: 50%; }
        """);

        document.Update();

        // `text-indent: 50%` resolves against the containing block's width, which is a layout result
        // and is not known at this point in the pass. Answering with half the font size — the easy
        // wrong thing — would be worse than answering with the initial value, so the declaration is
        // dropped and that is recorded rather than approximated.
        Assert.Equal(0f, document.Root.TextStyle.TextIndent, Tolerance);
    }
}
