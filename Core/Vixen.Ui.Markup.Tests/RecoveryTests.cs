// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Syntax;
using Xunit;

namespace Vixen.Ui.Markup.Tests;

/// <summary>
///     What the parser does with a file that is wrong, or merely unfinished.
/// </summary>
/// <remarks>
///     Half of these are not errors at all in the editor: they are the states a file passes
///     through while somebody types it. A parser that gave up on any of them would make the live
///     preview blink out on every second keystroke.
/// </remarks>
public class RecoveryTests {
    /// <summary>
    ///     ⚠ <b>A diagnostic's span is an offset into the file, and it was not.</b> Both of these
    ///     were read off a node still under construction — a node with no parent, whose
    ///     <c>Position</c> is relative to itself — so every unclosed element and every mismatched
    ///     close tag was reported a few characters into the file whichever one it was about.
    ///     Invisible to every test here, which asserts <i>which</i> diagnostics were reported and
    ///     never where; found when the source generator turned these spans into editor squiggles
    ///     and the first one landed on line zero.
    /// </summary>
    [Fact]
    public void A_diagnostic_points_at_the_characters_it_is_about_rather_than_the_top_of_the_file() {
        var unclosed = Vxml.Parse("@component A\n<div>\n").Diagnostics.Single();
        var mismatched = Vxml.Parse("@component A\n<div>x</span>").Diagnostics.Single();

        // `div` on line 1, not `<`, and not offset 1.
        Assert.Equal(new LinePosition(1, 1), unclosed.Location.GetLineSpan().Start);
        Assert.Equal(3, unclosed.Location.SourceSpan.Length);

        // ...and the *closing* name, which is the word that is wrong.
        Assert.Equal(new LinePosition(1, 8), mismatched.Location.GetLineSpan().Start);
        Assert.Equal(4, mismatched.Location.SourceSpan.Length);
    }

    [Fact]
    public void An_element_the_file_never_closes_is_reported_once_and_keeps_its_content() {
        var tree = Vxml.Parse("@component A\n<div>text");

        Assert.Equal(["VXML1002"], Vxml.Ids(tree));

        var div = tree.GetDocument().DescendantNodes().OfType<ElementSyntax>().Single();
        Assert.Null(div.EndTag);
        Assert.Equal("text", ((TextSyntax)Assert.Single(div.Content.Items())).TextToken.Text);
    }

    /// <summary>
    ///     Nobody above is waiting for <c>&lt;/span&gt;</c>, so it is this element's close tag with
    ///     the wrong name written on it — one diagnostic, and the tree keeps the shape drawn.
    /// </summary>
    [Fact]
    public void A_close_tag_no_ancestor_answers_to_closes_the_element_it_found() {
        var tree = Vxml.Parse("@component A\n<div>x</span>");

        Assert.Equal(["VXML1003"], Vxml.Ids(tree));

        var div = tree.GetDocument().DescendantNodes().OfType<ElementSyntax>().Single();
        Assert.Equal("span", div.EndTag!.Name.Text);
    }

    /// <summary>
    ///     Here an ancestor <i>is</i> waiting, so the same characters mean the opposite thing: the
    ///     inner element was never closed, and the tag belongs to the outer one.
    /// </summary>
    [Fact]
    public void A_close_tag_an_ancestor_answers_to_leaves_the_inner_element_unclosed() {
        var tree = Vxml.Parse("@component A\n<div><span>x</div>");

        Assert.Equal(["VXML1002"], Vxml.Ids(tree));

        var elements = tree.GetDocument().DescendantNodes().OfType<ElementSyntax>().ToList();
        Assert.Equal("div", elements[0].StartTag.Name.Text);
        Assert.Equal("div", elements[0].EndTag!.Name.Text);
        Assert.Equal("span", elements[1].StartTag.Name.Text);
        Assert.Null(elements[1].EndTag);
    }

    [Fact]
    public void A_close_tag_with_nothing_open_is_skipped_rather_than_swallowing_the_file() {
        var tree = Vxml.Parse("@component A\n</div>\n<p />");

        Assert.Equal(["VXML1001", "VXML1001", "VXML1001"], Vxml.Ids(tree));
        Assert.Equal("p", tree.GetDocument().DescendantNodes().OfType<ElementSyntax>().Single().StartTag.Name.Text);
    }

    [Theory]
    [InlineData("@component A\n<div>@</div>")]
    [InlineData("@component A\n<div>@ x</div>")]
    [InlineData("@component A\n<div>@+</div>")]
    public void An_at_that_starts_no_expression_is_reported_and_pointed_at_the_at(string source) {
        var tree = Vxml.Parse(source);

        Assert.Equal(["VXML1004"], Vxml.Ids(tree));
        Assert.True(tree.GetDocument().DescendantNodes().OfType<InterpolationSyntax>().Single().Expression.IsMissing);
    }

    /// <summary>
    ///     ⚠ <b>An <c>@(</c> the file never closes used to eat the rest of it.</b> The scan ran to
    ///     the end looking for the <c>)</c>, failed, and the characters it had walked over were
    ///     never emitted — <c>@(abc</c> parsed to a tree that reproduced as <c>@</c>. The scan is
    ///     speculative now: when it fails the window goes back, and everything after the <c>@</c>
    ///     lexes as the ordinary content it stopped being.
    /// </summary>
    [Theory]
    [InlineData("@(")]
    [InlineData("@(abc")]
    [InlineData("@(f(a)")]
    [InlineData("@component A\n<div>@(count</div>")]
    public void An_interpolation_whose_parenthesis_is_never_closed_keeps_the_rest_of_the_file(string source) {
        var tree = Vxml.Parse(source);

        Assert.Equal(source, tree.GetDocument().ToFullString());
        Assert.Contains("VXML1005", Vxml.Ids(tree));
    }

    /// <summary>
    ///     And the file keeps its shape, not merely its characters: the close tag after the
    ///     unbalanced <c>(</c> is still a close tag, which is the whole point of not swallowing it.
    /// </summary>
    [Fact]
    public void The_markup_after_an_unclosed_parenthesis_is_still_markup() {
        var tree = Vxml.Parse("@component A\n<div>@(count</div>");
        var div = tree.GetDocument().DescendantNodes().OfType<ElementSyntax>().Single();

        Assert.Equal("div", div.EndTag!.Name.Text);
        Assert.True(div.Content.Items().OfType<InterpolationSyntax>().Single().Expression.IsMissing);
        Assert.Equal("(count", ((TextSyntax)div.Content.Items()[1]).TextToken.Text);
    }

    [Fact]
    public void An_unbalanced_code_block_is_reported_and_still_yields_a_body() {
        var tree = Vxml.Parse("@component A\n@code { var x = 1;");

        Assert.Equal(["VXML1005"], Vxml.Ids(tree));
        Assert.Equal(" var x = 1;", tree.GetDocument().DescendantNodes().OfType<CodeBlockSyntax>().Single().Body.Text);
    }

    [Fact]
    public void A_style_block_that_runs_to_the_end_of_the_file_is_reported() {
        var tree = Vxml.Parse("@component A\n<style>.a { color: red; }");

        Assert.Equal(["VXML1006"], Vxml.Ids(tree));
        Assert.Null(tree.GetDocument().DescendantNodes().OfType<StyleBlockSyntax>().Single().EndTag);
    }

    [Fact]
    public void An_if_without_parentheses_reports_once_and_does_not_hunt_for_a_body() {
        var tree = Vxml.Parse("@component A\n@if x { <a /> }");

        // One diagnostic for the missing `(` and one for the missing `{` — and crucially not a
        // third about the markup that follows, because a body that never opened is not searched for.
        Assert.All(Vxml.Ids(tree), id => Assert.Equal("VXML1001", id));

        var @if = tree.GetDocument().DescendantNodes().OfType<IfSyntax>().Single();
        Assert.True(@if.OpenParenToken.IsMissing);
        Assert.True(@if.Body.OpenBraceToken.IsMissing);
        Assert.Empty(@if.Body.Content.Items());
    }

    [Fact]
    public void An_attribute_with_an_equals_and_no_value_reports_and_keeps_the_attribute() {
        var tree = Vxml.Parse("@component A\n<div class= />");

        Assert.Equal(["VXML1001"], Vxml.Ids(tree));

        var attribute = tree.GetDocument().DescendantNodes().OfType<AttributeSyntax>().Single();
        Assert.Equal("class", attribute.Name.Text);
        Assert.Null(attribute.Value);
    }

    [Fact]
    public void An_unterminated_attribute_value_still_produces_the_attribute() {
        var tree = Vxml.Parse("@component A\n<div class=\"btn");

        var value = Assert.IsType<QuotedAttributeValueSyntax>(
            tree.GetDocument().DescendantNodes().OfType<AttributeSyntax>().Single().Value
        );

        Assert.True(value.CloseQuoteToken.IsMissing);
        Assert.Equal("btn", ((TextSyntax)Assert.Single(value.Content.Items())).TextToken.Text);
    }

    [Fact]
    public void A_file_with_no_component_header_still_parses_its_markup() {
        var tree = Vxml.Parse("<div />");

        Assert.Empty(Vxml.Ids(tree));
        Assert.Null(tree.GetDocument().Component);
        Assert.Single(tree.GetDocument().Content.Items());
    }

    /// <summary>
    ///     The editor gate. Every prefix of a real file is a state somebody's cursor passes
    ///     through, and each one has to produce a tree that still reproduces what is on screen.
    /// </summary>
    [Fact]
    public void Every_prefix_of_a_real_file_parses_into_a_tree_that_reproduces_it() {
        const string Source = """
                              @component Counter
                              @using Vixen.Ui.Controls

                              @code { private int _n; }

                              <div class="row @kind">
                                  <Text>Clicked @_n times</Text>
                                  @if (_n > 1) { <b key="@_n">many</b> } else { <i /> }
                                  @for (var i in xs) { <p>@i</p> }
                                  @switch (_n) { case 1: <a /> default: <c /> }
                                  <!-- done -->
                              </div>

                              <style scoped>.row { display: flex; }</style>
                              """;

        for (var length = 0; length <= Source.Length; length++) {
            var prefix = Source[..length];
            Assert.Equal(prefix, Vxml.Parse(prefix).GetRoot().ToFullString());
        }
    }

    /// <summary>
    ///     A file that ends in the middle of an escape used to throw out of the lexer, which is the
    ///     one thing this parser promises it never does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The end of the file arriving in the middle of a construct, which every
    ///         multi-character skip in a lexer is a chance to get wrong.</b> A backslash asks the
    ///         scanner to take two characters and there is one, so the window ended at
    ///         <c>Length + 1</c> — which nothing noticed, because <c>AtEnd</c> is <c>&gt;=</c> and
    ///         every loop stopped exactly as it should. The token that scan produced was then cut
    ///         with a range past the end of the string.
    ///     </para>
    ///     <para>
    ///         Fixed by clamping <c>SlidingTextWindow.Advance</c>, so the property belongs to the
    ///         window rather than to the dozen skips across two lexers that would each have to
    ///         remember it. Found by <c>Vixen.Fuzz</c>'s <c>vxml</c> target after a million and a
    ///         half cases; the prefix test above walks one file and never reaches this, because a
    ///         real file has no trailing backslash to be cut after.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("@code {'\\")]
    [InlineData("@code {\"\\")]
    [InlineData("@component C\n@code { var c = '\\")]
    [InlineData("@if (x) { }\n@code { var s = @\"a\\")]
    public void A_file_ending_inside_an_escape_still_parses(string source) =>
        Assert.Equal(source, Vxml.Parse(source).GetRoot().ToFullString());
}
