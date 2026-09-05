// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>The two things ExCSS hands over unfinished: <c>@layer</c>, and everything unknown.</summary>
public class StyleSheetLoadingTests {
    [Theory]
    [InlineData("@layer base;", new[] { "base" }, false)]
    [InlineData("@layer base, theme, utilities;", new[] { "base", "theme", "utilities" }, false)]
    [InlineData("@layer  a . b ;", new[] { "a . b" }, false)]
    [InlineData("@layer base { .x { color: red } }", new[] { "base" }, true)]
    [InlineData("@layer { .x { color: red } }", new string[0], true)]
    [InlineData("@layer base{.x{color:red}}", new[] { "base" }, true)]
    public void The_layer_prelude_is_read_in_both_forms(string css, string[] names, bool hasBody) {
        Assert.True(LayerRuleParser.TryParse(css, out var rule));
        Assert.Equal(names, rule.Names);
        Assert.Equal(hasBody, rule.Body is not null);
    }

    [Fact]
    public void A_brace_inside_a_string_does_not_end_the_layer_body() {
        // The reason this is a small reader rather than `IndexOf('}')`. A stylesheet that sets
        // `content: "}"` would otherwise have its layer cut in half, and the rules after the cut
        // would silently load into no layer at all.
        Assert.True(
            LayerRuleParser.TryParse("""@layer base { .x { content: "}" ; color: red } }""", out var rule)
        );

        Assert.Contains("color: red", rule.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brace_inside_a_comment_does_not_end_the_layer_body_either() {
        Assert.True(LayerRuleParser.TryParse("@layer base { /* } */ .x { color: red } }", out var rule));
        Assert.Contains("color: red", rule.Body!, StringComparison.Ordinal);
    }

    /// <summary>⚠ And an escaped quote in a <i>selector</i> does not open a string at all.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The mirror of the row above, and the half it was missing.</b> That one says a quote
    ///         inside a string is content; this one says a quote that is <i>escaped</i> is not a
    ///         string delimiter in the first place. CSS Syntax 3 § 4.3.7 lets any character be
    ///         escaped wherever an identifier may appear, and the place that actually happens in
    ///         this engine is a generated class name: <c>font-features-["onum"_1]</c> is emitted as
    ///         the selector <c>.font-features-\[\"onum\"_1\]</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The failure was silent and looked like a missing feature.</b> Read without the
    ///         escape rule the first <c>\"</c> opened a string that ran to the quote in the
    ///         declaration, the braces after it were counted inside a string that was not there, and
    ///         the body was cut in the wrong place — leaving a rule that matched with an <i>empty</i>
    ///         value rather than one that failed. Every arbitrary value carrying a quoted string went
    ///         through this: <c>content-['x']</c> and <c>bg-[url("a.png")]</c> as much as the
    ///         <c>font-features-*</c> family that found it, which was left unregistered for a year on
    ///         the belief that the generator's escaping was at fault.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_escaped_quote_in_a_selector_does_not_open_a_string() {
        Assert.True(
            LayerRuleParser.TryParse(
                """@layer utilities { .f-\[\"onum\"_1\] { font-feature-settings: "onum" 1; } }""",
                out var rule
            )
        );

        // The whole body, not the part before the mis-read string swallowed it.
        Assert.Contains("font-feature-settings: \"onum\" 1;", rule.Body!, StringComparison.Ordinal);

        // ⚠ And a second rule after it, which is what the mis-cut actually loses: the body ended
        // early, so everything past the first quoted selector fell out of the layer silently.
        Assert.True(
            LayerRuleParser.TryParse(
                """@layer utilities { .a\"b { color: red } .c { color: blue } }""",
                out var pair
            )
        );

        Assert.Contains("color: blue", pair.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_at_rule_that_merely_starts_with_the_word_layer_is_not_one() {
        Assert.False(LayerRuleParser.IsLayerRule("@layered { }"));
        Assert.False(LayerRuleParser.IsLayerRule("@media (min-width: 1px) { }"));
        Assert.True(LayerRuleParser.IsLayerRule("@layer base;"));
    }

    [Fact]
    public void An_anonymous_layer_cannot_be_reopened() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer { .x { color: first } }
            @layer { .x { color: second } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        // Two layers, not one, and the later one wins because it is later.
        Assert.Equal(2, fixture.Engine.Rules.Layers.Count);
        Assert.Equal("second", fixture.Value(element));
    }

    [Fact]
    public void A_media_block_that_does_not_apply_contributes_nothing() {
        var fixture = new CascadeFixture();
        fixture.Load(
            """
            .x { color: base }
            @media (min-width: 600px) { .x { color: wide } }
            """,
            media: new MediaContext(320, 640)
        );

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("base", fixture.Value(element));
        Assert.Equal(1, fixture.Engine.Rules.Count);
    }

    [Fact]
    public void A_media_block_that_applies_contributes_rules_in_place() {
        var fixture = new CascadeFixture();
        fixture.Load(
            """
            @media (min-width: 600px) { .x { color: wide } }
            .x { color: base }
            """,
            media: new MediaContext(800, 600)
        );

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        // In place: the media rule keeps its source position, so the later unconditional rule wins
        // on document order rather than the media rule winning for having been conditional.
        Assert.Equal("base", fixture.Value(element));
    }

    [Theory]
    [InlineData("(min-width: 600px)", 800, 600, true)]
    [InlineData("(min-width: 600px)", 320, 600, false)]
    [InlineData("(max-width: 600px)", 320, 600, true)]
    [InlineData("(orientation: landscape)", 800, 600, true)]
    [InlineData("(orientation: portrait)", 800, 600, false)]
    [InlineData("(min-width: 600px) and (max-height: 400px)", 800, 300, true)]
    [InlineData("(min-width: 600px) and (max-height: 400px)", 800, 900, false)]
    [InlineData("all and (min-width: 600px)", 800, 600, true)]
    public void The_media_features_compare_the_way_CSS_says(string condition, float width, float height, bool expected) {
        Assert.True(MediaQuery.TryEvaluate(condition, new MediaContext(width, height), out var matches, out _));
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void A_media_feature_Vixen_cannot_evaluate_drops_the_block_with_a_diagnostic() {
        // Not "assume true" and not "assume false". One silently applies phone styles on a desktop
        // and the other silently drops them on a phone, and nobody notices either until a screenshot
        // looks odd. The same rule the selector compiler follows.
        var fixture = new CascadeFixture();
        fixture.Load("""
            .x { color: base }
            @media (hover: hover) { .x { color: hoverable } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("base", fixture.Value(element));
        Assert.Single(fixture.Engine.Loader.Diagnostics);
        Assert.Contains("hover", fixture.Engine.Loader.Diagnostics[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shorthand_expands_and_carries_its_importance_to_every_longhand() {
        // ExCSS does the expansion, which is one of the things ADR-009 buys. What Vixen has to get
        // right is that `!important` travels with it.
        var fixture = new CascadeFixture();
        fixture.Load("""
            .x { padding: 2px 4px }
            .y { margin: 1px !important }
            """);

        var padded = fixture.Tree.CreateElement("div", classNames: ["x"]);
        var style = fixture.Engine.Resolver.Resolve(fixture.Tree, padded);

        Assert.Equal("2px", fixture.Read(style, "padding-top"));
        Assert.Equal("4px", fixture.Read(style, "padding-left"));

        var second = new CascadeFixture();
        second.Load(".y { margin: 1px !important } #z { margin-left: 9px }");

        var element = second.Tree.CreateElement("div", id: "z", classNames: ["y"]);

        Assert.Equal("1px", second.Value(element, "margin-left"));
    }

    [Fact]
    public void A_value_ExCSS_has_never_heard_of_survives_verbatim() {
        // Including this engine's own `spring()` extension, and the custom properties the whole
        // var() mechanism is built on. Verified in the spike before any of this depended on it.
        var fixture = new CascadeFixture();
        fixture.Load(".x { transition: 200ms spring(1, 100, 10); --accent: teal }");

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);
        var style = fixture.Engine.Resolver.Resolve(fixture.Tree, element);

        Assert.Equal("200ms spring(1, 100, 10)", fixture.Read(style, "transition"));
        Assert.Equal("teal", fixture.Read(style, "--accent"));
    }

    [Fact]
    public void A_known_value_arrives_normalised_and_the_same_value_through_var_does_not() {
        // ⚠ A consequence of ADR-009 the spike did not name, and the value parser has to live with
        // it: ExCSS normalises what it can see, and it cannot see through a var(). So `red` reaches
        // Vixen as `rgb(255, 0, 0)` when written literally and as `red` when substituted. Anything
        // downstream that parses a colour must accept both forms.
        var fixture = new CascadeFixture();
        fixture.Load("""
            .literal { color: red }
            .substituted { --c: red; color: var(--c) }
            """);

        var literal = fixture.Tree.CreateElement("div", classNames: ["literal"]);
        var substituted = fixture.Tree.CreateElement("div", classNames: ["substituted"]);

        Assert.Equal("rgb(255, 0, 0)", fixture.Value(literal));
        Assert.Equal("red", fixture.Value(substituted));
    }

    /// <summary>
    ///     ⚠ <b>A refusal from an inline <c>style="…"</c> says so, because there is no rule to name.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The third of <c>SelectorDiagnostic.Rule</c>'s loader-side spellings and the odd one:
    ///         the other two thread a real selector, and this one threads a literal, because a
    ///         <c>style</c> attribute belongs to one element and has no selector at all. Saying so is
    ///         worth more than saying nothing — it tells a reader not to go looking through the
    ///         stylesheets — and until this test it was the one leg that could be deleted with the
    ///         suite staying green.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Driven through <c>ReadDeclarations</c> rather than through
    ///         <c>UiElement.SetStyle</c>, and the two are not the same path.</b> <c>SetStyle</c>
    ///         interns one property and one value directly and never expands a shorthand, so it
    ///         cannot reach this refusal; the attribute path goes through ExCSS. Reaching for the
    ///         friendlier API here would have produced a test that passed while covering nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_shorthand_refused_in_an_inline_style_attribute_says_it_came_from_one() {
        var fixture = new CascadeFixture();
        var into = new List<InlineDeclaration>();

        fixture.Engine.Loader.ReadDeclarations("border: var(--x) solid", into);

        var refusal = Assert.Single(fixture.Engine.Loader.Diagnostics);

        Assert.Contains("could not be taken apart", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("style=", refusal.Where, StringComparison.Ordinal);

        // ⚠ And it reads as an enclosing rule, so the drain picks the message that carries it. A
        // literal that happened to equal the fragment would be dropped by `NamesAnEnclosingRule` and
        // the reader would be told nothing about where it came from.
        Assert.True(refusal.NamesAnEnclosingRule);
    }

    /// <summary>
    ///     ⚠ <b>A malformed alignment value is a dropped declaration, not an exception out of
    ///     <c>StyleEngine.Load</c>.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The four properties ExCSS 4.3.2 models with a <c>ConditionalStartsWithValueConverter</c>
    ///         are the four here. The converter matches the conditional start token and then fails to
    ///         match a position after it, and the <c>ConditionalStartValue</c> it keeps holds a null
    ///         its <c>CssText</c> dereferences — so reading <c>Property.Value</c> threw, out of the
    ///         loader, out of the engine, into whoever asked for the sheet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>All four, because the crash is invisible from any one of them.</b>
    ///         <c>justify-items</c>, <c>justify-self</c> and <c>place-items</c> have no such converter
    ///         and never threw, which is the control below: a suite that happened to pick one of those
    ///         would have called the whole family safe.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("align-items: safe")]
    [InlineData("align-items: unsafe")]
    [InlineData("align-items: first")]
    [InlineData("align-items: last")]
    [InlineData("align-items: safe center extra")]
    [InlineData("align-self: safe")]
    [InlineData("align-content: safe")]
    [InlineData("justify-content: safe")]
    public void A_value_ExCSS_starts_reading_and_cannot_finish_drops_one_declaration(string declaration) {
        var fixture = new CascadeFixture();

        // The good rules bracket the bad one: the rule before it proves the load reached this far,
        // and the rule *after* it is the one the throw used to take with it.
        fixture.Load($".before {{ color: red }}\n.x {{ {declaration} }}\n.after {{ color: blue }}");

        var element = fixture.Tree.CreateElement("div", classNames: ["after"]);

        Assert.Equal(3, fixture.Engine.Rules.Count);
        Assert.Equal("rgb(0, 0, 255)", fixture.Value(element));

        var refusal = Assert.Single(fixture.Engine.Loader.Diagnostics);

        // The property, and the rule it was written in — there are no line numbers to give.
        Assert.Equal(declaration[..declaration.IndexOf(':', StringComparison.Ordinal)], refusal.Text);
        Assert.Equal(".x", refusal.Where);
        Assert.Contains("could not read it back", refusal.Reason, StringComparison.Ordinal);
    }

    /// <summary>The other side of the same table: the spellings that were never the problem.</summary>
    /// <remarks>
    ///     ⚠ <b><c>align-items: sideways center</c> is nonsense and still loads, verbatim.</b> That is
    ///     not the guard failing — it is ExCSS's ordinary "unknown value survives" path, the same one
    ///     <c>spring()</c> and every custom property depend on. The guard is about the values ExCSS
    ///     half-parses, not the ones it does not recognise at all.
    /// </remarks>
    [Theory]
    [InlineData("justify-items: safe")]
    [InlineData("justify-self: safe")]
    [InlineData("align-items: sideways center")]
    [InlineData("width: 4furlongs")]
    public void A_value_with_no_conditional_start_converter_still_loads_whole(string declaration) {
        var fixture = new CascadeFixture();

        fixture.Load($".before {{ color: red }}\n.x {{ {declaration} }}\n.after {{ color: blue }}");

        Assert.Equal(3, fixture.Engine.Rules.Count);
        Assert.Empty(fixture.Engine.Loader.Diagnostics);
    }

    /// <summary>
    ///     ⚠ <c>place-items: safe</c> was a row of the theory above until the <c>place-*</c>
    ///     shorthands were expanded, and it moved for a reason worth writing down.
    /// </summary>
    /// <remarks>
    ///     It is still not a value ExCSS starts reading and cannot finish — the load survives it and
    ///     the rules after it, which is what that theory is for. What changed is that
    ///     <c>ShorthandExpansion</c> now has an opinion about the property:
    ///     <c>&lt;overflow-position&gt;</c> is a modifier and <c>safe</c> alone has nothing to
    ///     modify, so the shorthand cannot be divided and the loader says so rather than interning a
    ///     declaration no consumer reads. Two different silences, one after the other.
    /// </remarks>
    [Fact]
    public void A_place_shorthand_that_is_only_a_modifier_loads_but_is_reported() {
        var fixture = new CascadeFixture();

        fixture.Load(".before { color: red }\n.x { place-items: safe }\n.after { color: blue }");

        Assert.Equal(3, fixture.Engine.Rules.Count);
        Assert.Equal("rgb(0, 0, 255)", fixture.Value(fixture.Tree.CreateElement("div", classNames: ["after"])));

        var refusal = Assert.Single(fixture.Engine.Loader.Diagnostics);
        Assert.Equal(".x", refusal.Where);
        Assert.Contains("could not be taken apart", refusal.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The same read is made from two other places, and both threw too.</b>
    /// </summary>
    /// <remarks>
    ///     A <c>@keyframes</c> stop and a <c>style="…"</c> attribute reach ExCSS's declarations through
    ///     their own loops, so a guard written only into <c>AddRule</c> would have left two thirds of
    ///     the crash standing. Neither is reachable from the rule test above.
    /// </remarks>
    [Fact]
    public void A_keyframe_stop_and_an_inline_attribute_drop_the_declaration_as_a_rule_does() {
        var stop = new CascadeFixture();
        stop.Load("@keyframes k { from { align-items: safe; color: red } }");

        Assert.Contains(
            "@keyframes k",
            Assert.Single(stop.Engine.Loader.Diagnostics).Where,
            StringComparison.Ordinal
        );

        // The rest of the stop survives the one declaration that did not.
        Assert.True(stop.Engine.Keyframes.TryGet("k", out var stops));
        Assert.Single(stop.Engine.Keyframes.DeclarationsOf(Assert.Single(stops).Declarations).ToArray());

        var inline = new CascadeFixture();
        var into = new List<InlineDeclaration>();
        inline.Engine.Loader.ReadDeclarations("align-items: safe; color: red", into);

        Assert.Contains("style=", Assert.Single(inline.Engine.Loader.Diagnostics).Where, StringComparison.Ordinal);
        Assert.Equal("color", Assert.Single(into).Property);
    }

    /// <summary>
    ///     ⚠ <b>The sheet's text is registered before it is parsed, so the crash outlived the load.</b>
    /// </summary>
    /// <remarks>
    ///     <c>StyleEngine.Load</c> adds the text to <c>sheets</c> and then hands it to the loader, and
    ///     <c>Reload</c> replays that list. A sheet that threw on the way in therefore threw again on
    ///     every later <c>Replace</c> and <c>Reload</c> of the same engine — one mistyped
    ///     <c>align-items</c> did not lose a stylesheet, it poisoned the document for the rest of the
    ///     session. This is the assertion that would have caught that, and it is separate from the
    ///     rows above because a guard that dropped the declaration on the first load and not on the
    ///     replay would pass those and fail this.
    /// </remarks>
    [Fact]
    public void A_sheet_that_held_one_survives_being_reloaded() {
        var fixture = new CascadeFixture();
        fixture.Load(".x { align-items: safe }\n.after { color: blue }");

        fixture.Engine.Reload();

        var element = fixture.Tree.CreateElement("div", classNames: ["after"]);
        Assert.Equal("rgb(0, 0, 255)", fixture.Value(element));
    }
}
