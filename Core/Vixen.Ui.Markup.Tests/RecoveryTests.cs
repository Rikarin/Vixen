// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
}
