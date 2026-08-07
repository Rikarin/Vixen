// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>Variants, escaping, and the generated stylesheet as the engine reads it.</summary>
public class UtilityGenerationTests {
    [Fact]
    public void A_generated_utility_computes_to_what_the_hand_written_rule_would() {
        // The assertion that is worth the most, and the one the family tests structurally cannot
        // make: it checks the generator against the *style engine* rather than against an
        // expectation of the text it ought to produce. A generator emitting syntactically valid CSS
        // that the cascade then read differently from intended would pass every string comparison
        // and fail this.
        var fixture = new UtilityFixture();

        Assert.Equal("16px", fixture.Computed(["p-4"], "padding-left"));
        Assert.Equal("100%", fixture.Computed(["w-full"], "width"));
        Assert.Equal("rgb(23, 23, 29)", fixture.Computed(["bg-surface-2"], "background-color"));
        Assert.Equal("17px", fixture.Computed(["text-lg"], "font-size"));
        Assert.Equal("flex", fixture.Computed(["flex"], "display"));
    }

    [Fact]
    public void The_families_added_for_the_engines_own_properties_reach_it() {
        // Each of these was chosen because the engine already reads the property it sets, so this is
        // the assertion that says so rather than assuming it. `border-t-2` in particular goes through
        // ExCSS's shorthand expansion on the way, and `flex-1` becomes three longhands the cascade
        // sees instead of the word itself.
        var fixture = new UtilityFixture();

        Assert.Equal("2px", fixture.Computed(["border-t-2"], "border-top-width"));
        Assert.Equal("1px", fixture.Computed(["border-x"], "border-left-width"));
        Assert.Equal("1px", fixture.Computed(["border-x"], "border-right-width"));

        // And leaves the other two alone rather than writing a zero over them, so `border-y-2
        // border-x` is two edges of each width instead of whichever came last.
        Assert.Null(fixture.Computed(["border-x"], "border-top-width"));
        // `flex: 1 1 0%` arrives as three longhands with the basis normalised to a bare `0`, which is
        // ExCSS's doing and not the generator's — worth pinning, because it is the sort of rewrite
        // that a string comparison against the emitted CSS would never have shown.
        Assert.Equal("1", fixture.Computed(["flex-1"], "flex-grow"));
        Assert.Equal("1", fixture.Computed(["flex-1"], "flex-shrink"));
        Assert.Equal("0", fixture.Computed(["flex-1"], "flex-basis"));
        Assert.Equal("8px", fixture.Computed(["ps-2"], "padding-inline-start"));
        Assert.Equal("border-box", fixture.Computed(["box-border"], "box-sizing"));
        Assert.Equal("16/9", fixture.Computed(["aspect-video"], "aspect-ratio"));
    }

    [Fact]
    public void A_negative_utility_survives_being_a_selector() {
        // `.-mt-4` is a valid CSS identifier — a hyphen followed by a letter — but only just, and a
        // generator that escaped or emitted it wrongly would produce a rule matching nothing.
        var fixture = new UtilityFixture();

        Assert.Equal("-16px", fixture.Computed(["-mt-4"], "margin-top"));
        Assert.Equal("16px", fixture.Computed(["mt-4"], "margin-top"));
    }

    [Fact]
    public void A_direction_variant_becomes_an_ancestor_attribute_selector() {
        // The same shape as `dark:` under the class strategy: an ancestor declares it and the utility
        // applies below. `direction` is a CSS property here, so an attribute is the only thing in the
        // tree a selector can match on.
        var fixture = new UtilityFixture();

        Assert.Contains("[dir=rtl] ", fixture.Generate("rtl:ml-2"), StringComparison.Ordinal);
        Assert.Contains("[dir=ltr] ", fixture.Generate("ltr:ml-2"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_utilities_layer_loses_to_an_unlayered_component_rule_whatever_the_specificity() {
        // The one line that makes the whole system behave. A generated `.p-4` is one class and a
        // hand-written `.card .body` is two, so on specificity alone the utility wins nothing and
        // the only remedy would be `!important` on everything the generator emits.
        //
        // The component rule is loaded *first* and is no more specific, so document order and
        // specificity both favour the utility. Only the layer can produce the right answer. The
        // first version of this test loaded it second, where document order gave the same result —
        // it passed with the whole `@layer` wrapper replaced by `@media all`, which is to say it
        // proved nothing. Found by sabotage, and it is the same mistake as the cascade suite's
        // important-origins test.
        var fixture = new UtilityFixture();
        var engine = new StyleEngine();

        engine.Load(".p-4 { padding-left: 32px }");
        engine.Load(fixture.Generate("p-4"));

        var element = engine.Tree.CreateElement("div", classNames: ["p-4"]);
        var style = engine.Resolver.Resolve(engine.Tree, element);

        Assert.True(style.TryGet(engine.Properties.Lookup("padding-left"), out var value));
        Assert.Equal("32px", engine.Values.NameOf(value));
    }

    [Fact]
    public void A_variant_becomes_a_pseudo_class_on_the_generated_selector() {
        var fixture = new UtilityFixture();
        var css = fixture.Generate("hover:bg-accent");

        Assert.Contains(":hover", css, StringComparison.Ordinal);
        Assert.Null(fixture.Computed(["hover:bg-accent"], "background-color"));
        Assert.Equal(
            "rgb(79, 124, 255)",
            fixture.Computed(["hover:bg-accent"], "background-color", state: ElementState.Hover)
        );
    }

    [Fact]
    public void A_breakpoint_variant_becomes_a_media_query() {
        var fixture = new UtilityFixture();

        Assert.Contains("@media (min-width: 768px)", fixture.Generate("md:p-4"), StringComparison.Ordinal);

        Assert.Null(fixture.Computed(["md:p-4"], "padding-left", media: new MediaContext(320, 640)));
        Assert.Equal("16px", fixture.Computed(["md:p-4"], "padding-left", media: new MediaContext(1024, 768)));
    }

    [Fact]
    public void Variants_stack() {
        var fixture = new UtilityFixture();
        var css = fixture.Generate("md:hover:focus:p-4");

        Assert.Contains("@media (min-width: 768px)", css, StringComparison.Ordinal);
        Assert.Contains(":hover:focus", css, StringComparison.Ordinal);

        Assert.Equal(
            "16px",
            fixture.Computed(
                ["md:hover:focus:p-4"],
                "padding-left",
                state: ElementState.Hover | ElementState.Focus,
                media: new MediaContext(1024, 768)
            )
        );
    }

    [Fact]
    public void The_class_name_is_escaped_because_it_contains_the_grammar_it_is_made_of() {
        // `hover:w-1/2` unescaped reads as a pseudo-class on a class called `hover`. Every
        // separator the utility grammar uses means something else in a selector.
        Assert.Equal(@"hover\:w-1\/2", UtilityGenerator.Escape("hover:w-1/2"));
        Assert.Equal(@"w-\[37px\]", UtilityGenerator.Escape("w-[37px]"));
        Assert.Equal("p-4", UtilityGenerator.Escape("p-4"));
    }

    [Fact]
    public void An_escaped_selector_actually_matches_the_element_wearing_it() {
        // Escaping that produced a *different* selector would pass the string test above and match
        // nothing at runtime, which is the failure worth guarding.
        var fixture = new UtilityFixture();

        Assert.Equal("50%", fixture.Computed(["w-1/2"], "width"));
        Assert.Equal("37px", fixture.Computed(["w-[37px]"], "width"));
        Assert.Equal(
            "16px",
            fixture.Computed(["hover:p-4"], "padding-left", state: ElementState.Hover)
        );
    }

    [Fact]
    public void Dark_mode_follows_the_strategy_the_theme_names() {
        var media = new UtilityFixture();
        Assert.Contains("prefers-color-scheme: dark", media.Generate("dark:bg-accent"), StringComparison.Ordinal);

        var byClass = new UtilityFixture(UtilityFixture.Theme.Replace("--dark-mode: media", "--dark-mode: class", StringComparison.Ordinal));
        var css = byClass.Generate("dark:bg-accent");

        Assert.Contains(".dark ", css, StringComparison.Ordinal);
        Assert.DoesNotContain("prefers-color-scheme", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_variant_styles_a_child_from_an_ancestors_state() {
        var fixture = new UtilityFixture();
        Assert.Contains(".group:hover ", fixture.Generate("group-hover:bg-accent"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_variant_becomes_an_attribute_selector() {
        var fixture = new UtilityFixture();

        Assert.Contains("[data-open]", fixture.Generate("data-open:bg-accent"), StringComparison.Ordinal);
        Assert.Contains("[data-state=open]", fixture.Generate("data-[state=open]:bg-accent"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_arbitrary_variant_substitutes_the_selector_for_the_ampersand() {
        // The escape hatch that stops the variant list from having to be complete, and the reason
        // the class-name parser has to be bracket-aware: this variant contains a `>`.
        var fixture = new UtilityFixture();
        var css = fixture.Generate("[&>*]:p-4");

        Assert.Contains(@".\[\&\>\*\]\:p-4>*", css, StringComparison.Ordinal);

        // And it matches. A selector that is merely well-escaped but wrong would pass the string
        // check above and style nothing.
        var engine = new StyleEngine();
        engine.Load(css);

        var parent = engine.Tree.CreateElement("div", classNames: ["[&>*]:p-4"]);
        var child = engine.Tree.CreateElement("span", parent);
        var styles = engine.ResolveAll();
        var padding = engine.Properties.Lookup("padding-left");

        Assert.True(styles[child.Index].TryGet(padding, out var value));
        Assert.Equal("16px", engine.Values.NameOf(value));
        Assert.False(styles[parent.Index].TryGet(padding, out _));
    }

    [Fact]
    public void Utilities_sharing_a_media_query_share_one_block() {
        var fixture = new UtilityFixture();
        var css = fixture.Generate("md:p-4", "md:m-2", "md:w-full");

        Assert.Equal(1, Occurrences(css, "@media (min-width: 768px)"));
    }

    [Fact]
    public void The_same_input_produces_the_same_file_whatever_order_it_arrives_in() {
        // A generated artefact that changes order between builds turns every diff into noise and
        // every content hash into a cache miss.
        var fixture = new UtilityFixture();

        Assert.Equal(
            fixture.Generate("p-4", "m-2", "flex"),
            new UtilityFixture().Generate("flex", "m-2", "p-4", "p-4")
        );
    }

    [Fact]
    public void Only_what_is_used_is_emitted() {
        // The alternative — every family crossed with every token — is a stylesheet in the tens of
        // megabytes, and the scanner exists so nobody has to think about that.
        var fixture = new UtilityFixture();
        fixture.Generate("p-4", "not-a-utility", "bg-accent");

        Assert.Equal(2, fixture.Generator.RuleCount);
        Assert.Equal(["not-a-utility"], fixture.Generator.Unrecognised);
    }

    [Fact]
    public void An_important_utility_carries_it_into_every_declaration() {
        var fixture = new UtilityFixture();

        Assert.Contains("!important", fixture.Generate("p-4!"), StringComparison.Ordinal);
        Assert.Equal(
            "16px",
            fixture.Computed(["p-4!"], "padding-left", extraCss: "#x { padding-left: 99px }")
        );
    }

    static int Occurrences(string text, string needle) {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
