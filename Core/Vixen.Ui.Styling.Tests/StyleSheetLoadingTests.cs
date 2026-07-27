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
}
