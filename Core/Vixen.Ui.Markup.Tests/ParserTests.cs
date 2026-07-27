// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Markup.Syntax;
using Xunit;

namespace Vixen.Ui.Markup.Tests;

/// <summary>The shape a well-formed file parses into.</summary>
public class ParserTests {
    const string Counter = """
                           @component Counter
                           @using Vixen.Ui.Controls

                           @code {
                               private readonly Signal<int> _count = new(0);
                               private void Increment() => _count.Value += Step;
                           }

                           <div class="flex flex-col gap-2">
                               <Text class="text-lg">@Title</Text>
                               <Text>Clicked @_count.Value times</Text>

                               @if (_count.Value > 10) {
                                   <Callout kind="warning">That's a lot.</Callout>
                               }

                               <div class="flex flex-row">
                                   @for (var i in Enumerable.Range(0, 3)) {
                                       <Button key="@i" on:click="@Increment">+@i</Button>
                                   }
                               </div>

                               <slot name="footer" />
                           </div>

                           <style scoped>
                               .flex { display: flex; }
                           </style>
                           """;

    [Fact]
    public void A_whole_component_parses_without_a_single_diagnostic() {
        var document = Vxml.ParseClean(Counter);

        Assert.Equal("Counter", document.Component!.Identifier.Text);
        Assert.Equal("Vixen.Ui.Controls", Assert.Single(document.Usings.Items()).Name.Text);
    }

    /// <summary>
    ///     The property everything else rests on. A tree that cannot reproduce its file cannot be
    ///     formatted, cannot be edited incrementally, and cannot map a span back to what the author
    ///     wrote — so it is asserted on every construct rather than on a happy path.
    /// </summary>
    [Theory]
    [InlineData("@component A\n<div />")]
    [InlineData("@component A\n<div></div>")]
    [InlineData("@component A\n<div>text</div>")]
    [InlineData("@component A\n<!-- a comment -->\n<div />")]
    [InlineData("@component A\n<div a=\"1\" b='2' c d=@e />")]
    [InlineData("@component A\n<div>@name</div>")]
    [InlineData("@component A\n<div>@(a + b)</div>")]
    [InlineData("@component A\n<div>a @@ b</div>")]
    [InlineData("@component A\n@if (x) { <a /> }")]
    [InlineData("@component A\n@if (x) { <a /> } else { <b /> }")]
    [InlineData("@component A\n@if (x) { <a /> } else if (y) { <b /> } else { <c /> }")]
    [InlineData("@component A\n@for (var i in xs) { <a key=\"@i\" /> }")]
    [InlineData("@component A\n@switch (x) { case 1: <a /> default: <b /> }")]
    [InlineData("@component A\n@code { var x = \"}\"; }")]
    [InlineData("@component A\n<style>.a { color: red; }</style>")]
    [InlineData("@component A\n<div>unclosed")]
    [InlineData("@component A\n</div>")]
    [InlineData("@component A\n<div>@</div>")]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Every_parse_reproduces_its_source_byte_for_byte(string source) =>
        Assert.Equal(source, Vxml.Parse(source).GetRoot().ToFullString());

    [Fact]
    public void The_whole_sample_reproduces_its_source_byte_for_byte() =>
        Assert.Equal(Counter, Vxml.Parse(Counter).GetRoot().ToFullString());

    [Fact]
    public void An_element_with_content_carries_its_end_tag() {
        var element = First<ElementSyntax>("@component A\n<div>hi</div>");

        Assert.Equal("div", element.StartTag.Name.Text);
        Assert.Equal(SyntaxKind.GreaterThanToken, element.StartTag.GreaterThanToken.Kind);
        Assert.Equal("div", element.EndTag!.Name.Text);
    }

    [Fact]
    public void A_self_closing_element_has_no_end_tag() {
        var element = First<ElementSyntax>("@component A\n<div />");

        Assert.Equal(SyntaxKind.SlashGreaterThanToken, element.StartTag.GreaterThanToken.Kind);
        Assert.Null(element.EndTag);
    }

    [Fact]
    public void An_attribute_name_keeps_its_directive_prefix_and_modifiers() {
        var attribute = First<AttributeSyntax>("@component A\n<div on:click.stop=\"@Go\" />");
        Assert.Equal("on:click.stop", attribute.Name.Text);
    }

    [Fact]
    public void A_quoted_value_mixing_text_and_expressions_keeps_both_parts() {
        var value = (QuotedAttributeValueSyntax)First<AttributeSyntax>("@component A\n<div class=\"btn @kind\" />")
            .Value!;

        Assert.Collection(
            value.Content.Items(),
            part => Assert.Equal("btn ", Assert.IsType<TextSyntax>(part).TextToken.Text),
            part => Assert.Equal("kind", Assert.IsType<InterpolationSyntax>(part).Expression.Text)
        );
    }

    [Fact]
    public void An_unquoted_value_is_an_expression() {
        var value = First<AttributeSyntax>("@component A\n<div step=@Step />").Value;
        Assert.Equal("Step", Assert.IsType<ExpressionAttributeValueSyntax>(value).Expression.Expression.Text);
    }

    /// <summary>
    ///     Razor's implicit-expression rule, which is what makes <c>Clicked @count times</c> read
    ///     the way it looks: a name and its member accesses, and nothing after the first character
    ///     that cannot continue one.
    /// </summary>
    [Theory]
    [InlineData("@count", "count")]
    [InlineData("@_count.Value", "_count.Value")]
    [InlineData("@a.b.c", "a.b.c")]
    [InlineData("@Items[0]", "Items[0]")]
    [InlineData("@Go()", "Go()")]
    [InlineData("@Go(a, \"b)\")", "Go(a, \"b)\")")]
    [InlineData("@a?.b", "a?.b")]
    [InlineData("@(a + b)", "(a + b)")]
    [InlineData("@(f(\")\"))", "(f(\")\"))")]
    [InlineData("@a + b", "a")]
    [InlineData("@a, b", "a")]
    public void An_interpolation_takes_exactly_the_expression_it_should(string written, string expected) {
        var interpolation = First<InterpolationSyntax>($"@component A\n<div>{written}</div>");
        Assert.Equal(expected, interpolation.Expression.Text);
    }

    [Fact]
    public void A_code_block_body_survives_a_brace_inside_a_string() {
        var code = First<CodeBlockSyntax>("@component A\n@code { var s = \"}\"; var t = '}'; }");
        Assert.Equal(" var s = \"}\"; var t = '}'; ", code.Body.Text);
    }

    [Fact]
    public void A_code_block_body_survives_a_brace_inside_a_raw_string() {
        var code = First<CodeBlockSyntax>("@component A\n@code { var s = \"\"\"}\"\"\"; }");
        Assert.Equal(" var s = \"\"\"}\"\"\"; ", code.Body.Text);
    }

    [Fact]
    public void A_style_block_body_is_never_lexed_as_markup() {
        var style = First<StyleBlockSyntax>("@component A\n<style scoped>.a { color: red; } @media x {}</style>");

        Assert.Equal(".a { color: red; } @media x {}", style.Css.Text);
        Assert.Equal("scoped", Assert.Single(style.StartTag.Attributes.Items()).Name.Text);
    }

    /// <summary>
    ///     A brace closes a directive body only at the element depth it was opened at, so a
    ///     literal one inside an element stays what it looks like.
    /// </summary>
    [Fact]
    public void A_brace_inside_a_deeper_element_is_text_rather_than_a_block_close() {
        var document = Vxml.ParseClean("@component A\n@if (x) { <div>a } b</div> }");
        var @if = Assert.IsType<IfSyntax>(Assert.Single(document.Content.Items()));
        var div = @if.Body.Content.Items().OfType<ElementSyntax>().Single();

        Assert.Equal("a } b", string.Concat(div.Content.Items().Select(node => ((TextSyntax)node).TextToken.Text)));
    }

    [Fact]
    public void An_else_if_chain_nests_inside_the_else_clause() {
        var @if = First<IfSyntax>("@component A\n@if (a) { <x /> } else if (b) { <y /> } else { <z /> }");

        var second = Assert.IsType<IfSyntax>(@if.Else!.Body);
        Assert.Equal("b", second.Condition.Text);
        Assert.IsType<MarkupBlockSyntax>(second.Else!.Body);
    }

    [Fact]
    public void A_for_header_splits_into_its_variable_and_its_sequence() {
        var @for = First<ForSyntax>("@component A\n@for (var item in Model.Items) { <x /> }");

        Assert.Equal("item", @for.Identifier.Text);
        Assert.Equal("Model.Items", @for.Sequence.Text);
    }

    [Fact]
    public void A_switch_keeps_one_section_per_label() {
        var @switch = First<SwitchSyntax>("@component A\n@switch (k) { case 1: <a /> case 2: <b /> default: <c /> }");

        Assert.Equal("k", @switch.Subject.Text);
        Assert.Collection(
            @switch.Sections.Items(),
            section => Assert.Equal("1", section.Pattern!.Text),
            section => Assert.Equal("2", section.Pattern!.Text),
            section => Assert.Null(section.Pattern)
        );
    }

    /// <summary>
    ///     A label inside an element of a section is text, not a new section — which is the same
    ///     depth rule the closing brace uses.
    /// </summary>
    [Fact]
    public void The_word_case_inside_an_element_stays_text() {
        var @switch = First<SwitchSyntax>("@component A\n@switch (k) { case 1: <p>case closed</p> }");
        var section = Assert.Single(@switch.Sections.Items());
        var paragraph = Assert.IsType<ElementSyntax>(Assert.Single(section.Content.Items()));

        Assert.Equal("case closed", ((TextSyntax)Assert.Single(paragraph.Content.Items())).TextToken.Text);
    }

    [Fact]
    public void Whitespace_that_crosses_a_line_is_trivia_and_whitespace_that_does_not_is_text() {
        var element = First<ElementSyntax>("@component A\n<div>\n  <a />  <b />\n</div>");

        Assert.Collection(
            element.Content.Items(),
            node => Assert.IsType<ElementSyntax>(node),
            node => Assert.Equal("  ", Assert.IsType<TextSyntax>(node).TextToken.Text),
            node => Assert.IsType<ElementSyntax>(node)
        );
    }

    [Fact]
    public void A_comment_is_trivia_rather_than_content() {
        var document = Vxml.ParseClean("@component A\n<div><!-- gone -->x</div>");
        var div = Assert.IsType<ElementSyntax>(Assert.Single(document.Content.Items()));

        Assert.Equal("x", ((TextSyntax)Assert.Single(div.Content.Items())).TextToken.Text);
    }

    [Fact]
    public void A_less_than_that_starts_nothing_is_text() {
        var element = First<ElementSyntax>("@component A\n<div>a < b</div>");
        Assert.Equal("a < b", ((TextSyntax)Assert.Single(element.Content.Items())).TextToken.Text);
    }

    static T First<T>(string source) where T : VxmlSyntaxNode =>
        Vxml.ParseClean(source).DescendantNodes().OfType<T>().First();
}
