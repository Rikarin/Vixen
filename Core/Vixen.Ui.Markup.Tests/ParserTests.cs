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
        Assert.Equal("Vixen.Ui.Controls", Assert.Single(document.Usings).Name.Text);
    }

    /// <summary>
    ///     <c>@using X = A.B.C</c>. The alias is held ahead of the name rather than instead of it,
    ///     so <see cref="UsingDirectiveSyntax.Name" /> means the imported thing whichever shape the
    ///     file wrote.
    /// </summary>
    [Fact]
    public void A_using_directive_parses_an_alias() {
        var document = Vxml.ParseClean("""
            @component Counter
            @using Prefab = Vixen.Editor.AssetEditors.Prefabs.PrefabSource
            <panel />
            """);

        var @using = Assert.Single(document.Usings);
        Assert.Equal("Prefab", @using.Alias!.Text);
        Assert.Equal("Vixen.Editor.AssetEditors.Prefabs.PrefabSource", @using.Name.Text);
    }

    [Fact]
    public void A_using_directive_without_an_alias_has_none() {
        var document = Vxml.ParseClean("""
            @component Counter
            @using System.Text
            <panel />
            """);

        var @using = Assert.Single(document.Usings);
        Assert.Null(@using.Alias);
        Assert.Null(@using.EqualsToken);
        Assert.Equal("System.Text", @using.Name.Text);
    }

    /// <summary>Whichever shape it is written in, a using still prints back into its file.</summary>
    [Theory]
    [InlineData("@component Counter\n@using System\n<panel />")]
    [InlineData("@component Counter\n@using Text = System.Text\n<panel />")]
    [InlineData("@component Counter\n@using   Text   =   System.Text  \n<panel />")]
    public void A_using_prints_back_into_the_file_it_came_from(string source) =>
        Assert.Equal(source, Vxml.ParseClean(source).ToFullString());

    /// <summary>
    ///     ⚠ <b>The <c>=</c> is looked for without consuming the whitespace in front of it.</b> A
    ///     directive hands the lexer straight back to content, where a whitespace run that does not
    ///     span a line break is <i>text</i> — so a scan that skipped ahead to see whether an alias
    ///     followed would quietly turn the space after every plain <c>@using X</c> into trivia.
    ///     Round-tripping cannot see that, because trivia prints too.
    /// </summary>
    [Fact]
    public void The_space_after_a_using_with_no_alias_is_still_text() {
        var document = Vxml.ParseClean("@component Counter\n@using System <panel />");
        var text = Assert.IsType<TextSyntax>(document.Content.Items().First());

        Assert.Equal(" ", text.TextToken.Text);
    }

    /// <summary>
    ///     An <c>=</c> on the next line belongs to that line, not to the using above it — the scan
    ///     stops at the break, so this is a plain import followed by content.
    /// </summary>
    [Fact]
    public void An_equals_on_the_next_line_is_not_an_alias() {
        const string Source = "@component Counter\n@using System\n= x\n<panel />";
        var document = Vxml.ParseClean(Source);

        Assert.Null(Assert.Single(document.Usings).Alias);
        Assert.Equal(Source, document.ToFullString());
    }

    /// <summary>
    ///     ⚠ <b><c>static</c> is a keyword of the directive, not the name it imports.</b> Lexed as
    ///     one name and nothing else, <c>@using static System.Math</c> parsed <c>static</c> as the
    ///     namespace and left <c>System.Math</c> as a text node in the markup.
    /// </summary>
    [Fact]
    public void A_using_directive_parses_a_static_import() {
        var document = Vxml.ParseClean("""
            @component Counter
            @using static System.Math
            <panel />
            """);

        var @using = Assert.Single(document.Usings);
        Assert.Equal("static", @using.StaticKeyword!.Text);
        Assert.Equal("System.Math", @using.Name.Text);
        Assert.Null(@using.Alias);
        Assert.Empty(document.Content.Items().OfType<TextSyntax>());
    }

    [Fact]
    public void A_using_directive_that_is_not_static_has_no_static_keyword() =>
        Assert.Null(
            Assert.Single(Vxml.ParseClean("@component Counter\n@using System.Text\n<panel />").Usings).StaticKeyword
        );

    /// <summary>
    ///     ⚠ <b>A namespace whose first segment merely starts with <c>static</c> is still a name.</b>
    ///     The keyword is only taken when whitespace follows it, because <c>IsNamePart</c> takes a
    ///     <c>.</c> — so a boundary rule that did not look would have eaten the head of the name and
    ///     left the parser with no name at all.
    /// </summary>
    [Theory]
    [InlineData("@component Counter\n@using staticly.Typed\n<panel />", "staticly.Typed")]
    [InlineData("@component Counter\n@using static.Typed\n<panel />", "static.Typed")]
    public void A_name_beginning_with_static_is_not_a_static_import(string source, string expected) {
        var @using = Assert.Single(Vxml.ParseClean(source).Usings);

        Assert.Null(@using.StaticKeyword);
        Assert.Equal(expected, @using.Name.Text);
    }

    /// <summary>A static import prints back into its file like every other shape of using.</summary>
    [Theory]
    [InlineData("@component Counter\n@using static System.Math\n<panel />")]
    [InlineData("@component Counter\n@using   static   System.Math  \n<panel />")]
    public void A_static_using_prints_back_into_the_file_it_came_from(string source) =>
        Assert.Equal(source, Vxml.ParseClean(source).ToFullString());

    [Fact]
    public void A_namespace_directive_parses() {
        var document = Vxml.ParseClean("""
            @component Counter
            @namespace Game.Screens
            <panel />
            """);

        Assert.Equal("Game.Screens", document.Namespace!.Name.Text);
    }

    /// <summary>
    ///     ⚠ <c>@namespace</c> and <c>@using</c> interleave freely. A header order nobody can
    ///     remember is a diagnostic nobody wants.
    /// </summary>
    [Fact]
    public void The_namespace_may_sit_anywhere_among_the_usings() {
        var document = Vxml.ParseClean("""
            @component Counter
            @using System
            @namespace Game.Screens
            @using System.Text
            <panel />
            """);

        Assert.Equal("Game.Screens", document.Namespace!.Name.Text);
        Assert.Equal(["System", "System.Text"], document.Usings.Select(one => one.Name.Text));
    }

    /// <summary>
    ///     <c>@tag</c> takes an element name rather than an identifier, which is the whole reason
    ///     it exists: the default is the type's name in lower case and a type's name has no hyphen
    ///     in it.
    /// </summary>
    [Fact]
    public void A_tag_directive_parses_a_hyphenated_name() {
        var document = Vxml.ParseClean("""
            @component TaskCenter
            @tag task-center
            <panel />
            """);

        Assert.Equal("task-center", document.Tag!.Name.Text);
    }

    [Fact]
    public void The_tag_may_sit_anywhere_among_the_usings() {
        var document = Vxml.ParseClean("""
            @component TaskCenter
            @using System
            @tag task-center
            @namespace Game.Screens
            <panel />
            """);

        Assert.Equal("task-center", document.Tag!.Name.Text);
        Assert.Equal("Game.Screens", document.Namespace!.Name.Text);
        Assert.Equal(["System"], document.Usings.Select(one => one.Name.Text));
    }

    /// <summary>
    ///     ⚠ <b>The headers are held in source order rather than one field each</b>, which is what
    ///     makes the round-trip cases above pass whichever way round they are written. A field per
    ///     header prints them in field order, and a file that wrote them in any other order came
    ///     back rearranged — silently, because nothing that reads a header cares about the order
    ///     and only <c>ToFullString</c> can see it.
    /// </summary>
    [Fact]
    public void The_headers_are_held_in_the_order_the_file_wrote_them() {
        var document = Vxml.ParseClean("""
            @component Counter
            @using System
            @tag task-center
            @namespace Game.Screens
            @using System.Text
            <panel />
            """);

        Assert.Equal(
            [
                SyntaxKind.ComponentDirective,
                SyntaxKind.UsingDirective,
                SyntaxKind.TagDirective,
                SyntaxKind.NamespaceDirective,
                SyntaxKind.UsingDirective
            ],
            document.Directives.Items().Select(directive => directive.Kind)
        );
    }

    [Fact]
    public void A_second_tag_directive_is_reported_and_the_first_stands() {
        const string Source = """
            @component A
            @tag a-b
            @tag c-d
            <panel />
            """;

        var tree = Vxml.Parse(Source);
        var document = tree.GetDocument();

        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal("a-b", document.Tag!.Name.Text);
        Assert.Equal(Source, document.ToFullString());
    }

    /// <summary>
    ///     ⚠ A second one is rejected rather than silently replacing the first — and, like every
    ///     other stray directive, its characters survive in the tree as trivia.
    /// </summary>
    [Fact]
    public void A_second_namespace_directive_is_reported_and_the_first_stands() {
        const string Source = """
            @component Counter
            @namespace Game.Screens
            @namespace Somewhere.Else
            <panel />
            """;

        var tree = Vxml.Parse(Source);
        var document = tree.GetDocument();

        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal("Game.Screens", document.Namespace!.Name.Text);
        Assert.Equal(Source, document.ToFullString());
    }

    /// <summary>
    ///     The property everything else rests on. A tree that cannot reproduce its file cannot be
    ///     formatted, cannot be edited incrementally, and cannot map a span back to what the author
    ///     wrote — so it is asserted on every construct rather than on a happy path.
    /// </summary>
    [Theory]
    [InlineData("@component A\n<div />")]
    [InlineData("@component A\n@tag task-center\n<div />")]
    [InlineData("@component A\n@using System\n@namespace N\n<div />")]
    [InlineData("@component A\n@using System\n@tag a-b\n<div />")]
    [InlineData("@component A\n@tag a-b\n@namespace N\n<div />")]
    [InlineData("@component A\n@namespace N\n@using System\n<div />")]
    [InlineData("@component A\n@tag a-b\n@using System\n<div />")]
    [InlineData("@component A\n@namespace N\n@tag a-b\n<div />")]
    [InlineData("@component A\n@namespace N\n@using System\n@tag a-b\n@using System.Text\n<div />")]
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
