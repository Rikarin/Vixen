// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Which declaration wins, one tie-break at a time.</summary>
/// <remarks>
///     <para>
///         There is no oracle for this. CSS Cascading 5 §6 is the specification and these are its
///         clauses written as assertions, which is the closest thing available — a browser would be a
///         real oracle and running one in a unit test is not a trade worth making.
///     </para>
///     <para>
///         So each test names <i>one</i> tie-break and constructs a case where every other one is
///         tied. A test that asserted a winner in a case where two rules differ in three respects
///         would pass with two of the three implemented, and the missing one is exactly the sort that
///         nobody notices until a stylesheet behaves oddly in one place.
///     </para>
/// </remarks>
public class CascadeOrderTests {
    [Fact]
    public void Later_wins_when_everything_else_is_tied() {
        var fixture = new CascadeFixture();
        fixture.Load(".a { color: first } .a { color: second }");

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);

        Assert.Equal("second", fixture.Value(element));
    }

    [Fact]
    public void More_specific_wins_regardless_of_order() {
        var fixture = new CascadeFixture();

        // The specific rule comes *first*, so an implementation that only had document order would
        // get this wrong rather than accidentally right.
        fixture.Load("div.a { color: specific } .a { color: general }");

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);

        Assert.Equal("specific", fixture.Value(element));
    }

    [Fact]
    public void Author_beats_user_beats_user_agent() {
        var fixture = new CascadeFixture();

        // Loaded in the order that would win on document order alone, so origin has to be doing it.
        fixture.Load("* { color: author }", StyleOrigin.Author);
        fixture.Load("* { color: user }", StyleOrigin.User);
        fixture.Load("* { color: agent }", StyleOrigin.UserAgent);

        var element = fixture.Tree.CreateElement("div");

        Assert.Equal("author", fixture.Value(element));
    }

    [Fact]
    public void Important_reverses_the_origins_rather_than_simply_winning() {
        // The point of importance, and the part that reads as a bug until you know why: a player's
        // accessibility override has to be able to beat a game that also insisted. If `!important`
        // merely meant "wins", author-important would beat user-important and it could not.
        var fixture = new CascadeFixture();

        // Loaded user-first, so that document order alone would say "author". The first version of
        // this test loaded them the other way round and passed with the origin ranks for important
        // declarations flattened to a single value — it was asserting document order and calling it
        // importance. Found by sabotage, which is the only thing that would have found it.
        fixture.Load("* { color: user !important }", StyleOrigin.User);
        fixture.Load("* { color: author !important }", StyleOrigin.Author);

        var element = fixture.Tree.CreateElement("div");

        Assert.Equal("user", fixture.Value(element));

        // And the user-agent origin is above the user one when both insist, which is the far end of
        // the same mirror and needs its own case for the same reason.
        var second = new CascadeFixture();
        second.Load("* { color: agent !important }", StyleOrigin.UserAgent);
        second.Load("* { color: user !important }", StyleOrigin.User);

        Assert.Equal("agent", second.Value(second.Tree.CreateElement("div")));
    }

    [Fact]
    public void Important_beats_more_specific_normal() {
        var fixture = new CascadeFixture();
        fixture.Load("#x.a.b.c { color: specific } .a { color: insistent !important }");

        var element = fixture.Tree.CreateElement("div", id: "x", classNames: ["a", "b", "c"]);

        Assert.Equal("insistent", fixture.Value(element));
    }

    [Fact]
    public void A_later_layer_beats_an_earlier_one_whatever_the_specificity() {
        // What layers are *for*. The generated utility is one class and the hand-written component
        // rule is three, so without layers the utility loses every time and the only remedy is
        // `!important` on everything the generator emits.
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer components, utilities;
            @layer components { .card .body .text { color: component } }
            @layer utilities { .text-accent { color: utility } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["text", "text-accent"]);

        Assert.Equal("utility", fixture.Value(element));
    }

    [Fact]
    public void The_statement_form_fixes_the_order_before_the_layers_have_any_rules() {
        // Which is the only reason it exists: the generated utility layer is appended to the file,
        // long after the line that says it loses to components.
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer utilities, components;
            @layer components { .x { color: component } }
            @layer utilities { .x { color: utility } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("component", fixture.Value(element));
        Assert.Equal(["utilities", "components"], fixture.Engine.Rules.Layers.Order);
    }

    [Fact]
    public void Reopening_a_layer_does_not_move_it() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer base { .x { color: base } }
            @layer theme { .x { color: theme } }
            @layer base { .x { color: base-again } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        // `base` keeps the position its first block gave it, so `theme` still wins — even though the
        // reopened block is the last thing in the file.
        Assert.Equal("theme", fixture.Value(element));
        Assert.Equal(2, fixture.Engine.Rules.Layers.Count);
    }

    [Fact]
    public void Unlayered_styles_beat_every_layer() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer base, theme;
            .x { color: unlayered }
            @layer theme { .x { color: theme } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("unlayered", fixture.Value(element));
    }

    [Fact]
    public void Important_reverses_the_layers_too_and_unlayered_loses() {
        // The same mirror importance applies to origins, applied to layers — and the reason a reset
        // layer declared first can insist on something and mean it. Two assertions because the
        // reversal and the unlayered end of it are separate rules, and an implementation can get
        // one without the other.
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer reset, theme;
            @layer reset { .x { color: reset !important } }
            @layer theme { .x { color: theme !important } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("reset", fixture.Value(element));

        var second = new CascadeFixture();
        second.Load("""
            @layer theme;
            .x { color: unlayered !important }
            @layer theme { .x { color: theme !important } }
            """);

        var other = second.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("theme", second.Value(other));
    }

    [Fact]
    public void A_nested_layer_orders_inside_its_parent() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            @layer framework.base, framework.overrides;
            @layer framework.overrides { .x { color: overrides } }
            @layer framework.base { .x { color: base } }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("overrides", fixture.Value(element));

        // `framework` is implied by its first nested layer and takes a position of its own.
        Assert.Equal(["framework", "framework.base", "framework.overrides"], fixture.Engine.Rules.Layers.Order);
    }

    [Fact]
    public void A_layer_block_inside_media_is_the_same_layer_as_one_outside() {
        var fixture = new CascadeFixture();
        fixture.Load(
            """
            @layer base, theme;
            @media (min-width: 100px) {
              @layer theme { .x { color: theme } }
            }
            @layer base { .x { color: base } }
            """,
            media: new MediaContext(200, 100)
        );

        var element = fixture.Tree.CreateElement("div", classNames: ["x"]);

        Assert.Equal("theme", fixture.Value(element));
        Assert.Equal(2, fixture.Engine.Rules.Layers.Count);
    }

    [Fact]
    public void Inline_declarations_sit_above_every_author_rule_of_the_same_importance() {
        var fixture = new CascadeFixture();
        fixture.Load("#x { color: rule }");

        var element = fixture.Tree.CreateElement("div", id: "x");
        var inline = fixture.Inline(("color", "inline", false));

        Assert.Equal("inline", fixture.Value(element, inline: inline));

        // And an important rule beats a normal inline one, which is the direction people expect
        // least — inline style is not a trump card, it is a position in the same table.
        var second = new CascadeFixture();
        second.Load("#x { color: rule !important }");

        var other = second.Tree.CreateElement("div", id: "x");

        Assert.Equal("rule", second.Value(other, inline: second.Inline(("color", "inline", false))));
    }

    [Fact]
    public void A_comma_separated_selector_cascades_as_several_rules_and_not_as_one() {
        // `#a, .b { … }` is one block and two rules, and they have different specificities. Treating
        // it as one rule means picking one of the two and being wrong about the other element.
        var fixture = new CascadeFixture();
        fixture.Load("""
            #x, .b { color: shared }
            div { color: type }
            .b { color: class }
            """);

        var byId = fixture.Tree.CreateElement("div", id: "x");
        var byClass = fixture.Tree.CreateElement("div", classNames: ["b"]);

        // For the id element the shared block wins on specificity; for the class element it ties
        // with `.b` and loses on document order.
        Assert.Equal("shared", fixture.Value(byId));
        Assert.Equal("class", fixture.Value(byClass));
    }

    [Fact]
    public void The_last_of_two_declarations_that_tie_completely_wins() {
        // Two declarations tie only when they came from the same place, and then the plainest rule
        // CSS has applies. ExCSS collapses duplicates inside a rule body before Vixen sees them, so
        // the case that reaches the cascade is an inline block — which Vixen builds itself, and
        // where nothing would have collapsed them.
        var fixture = new CascadeFixture();
        var element = fixture.Tree.CreateElement("div");
        var inline = fixture.Inline(("color", "first", false), ("color", "second", false));

        Assert.Equal("second", fixture.Value(element, inline: inline));
    }

    [Fact]
    public void Declarations_of_different_properties_do_not_compete() {
        var fixture = new CascadeFixture();
        fixture.Load("#x { color: from-id } .a { background: from-class }");

        var element = fixture.Tree.CreateElement("div", id: "x", classNames: ["a"]);
        var style = fixture.Engine.Resolver.Resolve(fixture.Tree, element);

        Assert.Equal("from-id", fixture.Read(style, "color"));
        Assert.Equal("from-class", fixture.Read(style, "background"));
    }
}
