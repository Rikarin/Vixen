// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Transitions that start or end at a value no declaration ever wrote.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap these close is invisible from any fixture that declares both endpoints, which
///         is what every transition fixture here did.</b> A <see cref="ComputedStyle" /> holds only
///         what a declaration or an inheritance put in it, so an element that never mentioned
///         <c>margin-left</c> and an element whose <c>margin-left</c> computes to <c>0px</c> are the
///         same state — and the animator, which needs a value to travel <i>from</i>, had nothing to
///         offer for the first. Writing <c>margin-left: 0px</c> in the base rule made the fade work,
///         which is why it read as a stylesheet quirk rather than as a missing stage.
///     </para>
///     <para>
///         ⚠ <b>The half that is more visible in a real interface is the reverse one.</b> A
///         <c>:hover</c> rule that adds a property leaves it out of the computed style again the
///         moment the pointer goes, so a loop over the <i>new</i> style's properties never visits it:
///         the fade in ran and the fade back did not. That asymmetry cannot appear in a fixture that
///         only ever adds a class.
///     </para>
/// </remarks>
public class InitialValueTransitionTests {
    const float Tolerance = 1e-3f;

    static CascadeFixture Fixture(string css) {
        var fixture = new CascadeFixture();
        fixture.Load(css);

        return fixture;
    }

    static Animator Animator(CascadeFixture fixture) =>
        new(fixture.Engine.Properties, fixture.Engine.Values, fixture.Engine.Names, fixture.Engine.Keyframes);

    /// <summary>Adds a class, observes the change, and reports the animator and the element.</summary>
    static (Animator Animator, StyleNodeId Element) Toggled(CascadeFixture fixture, string added) {
        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, before, 0f);

        fixture.Tree.AddClass(element, added);

        var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, before, after, 0f);

        return (animator, element);
    }

    /// <summary>A property the old style never held travels from its initial value.</summary>
    /// <remarks>
    ///     ⚠ <b>The base rule deliberately does not write <c>margin-left: 0px</c></b>, because writing
    ///     it is exactly the workaround this closes — with it, the test passes against the old code
    ///     and proves nothing.
    /// </remarks>
    [Fact]
    public void A_property_the_old_style_did_not_hold_starts_at_its_initial_value() {
        var fixture = Fixture("""
            .a { transition: margin-left 1s linear }
            .a.moved { margin-left: 40px }
            """);

        var (animator, element) = Toggled(fixture, "moved");

        Assert.Equal(1, animator.RunningCount);

        var property = fixture.Engine.Properties.Lookup("margin-left");

        Assert.True(animator.TryGetCurrent(element, property, 0.25f, out var quarter));
        Assert.Equal(10f, quarter.Number, Tolerance);
        Assert.Equal(StyleUnit.Pixels, quarter.Unit);
    }

    /// <summary>And a property the new style stops holding travels back to it.</summary>
    /// <remarks>
    ///     ⚠ The loop over the new style's properties can never see this one: it is not there. Half
    ///     of a hover transition lived in that blind spot.
    /// </remarks>
    [Fact]
    public void A_property_the_new_style_stops_holding_travels_back_to_its_initial_value() {
        var fixture = Fixture("""
            .a { transition: margin-left 1s linear }
            .a.moved { margin-left: 40px }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a", "moved"]);
        var animator = Animator(fixture);

        var moved = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, moved, 0f);

        fixture.Tree.RemoveClass(element, "moved");

        var rested = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, moved, rested, 0f);

        Assert.Equal(1, animator.RunningCount);

        var property = fixture.Engine.Properties.Lookup("margin-left");

        Assert.True(animator.TryGetCurrent(element, property, 0.25f, out var quarter));
        Assert.Equal(30f, quarter.Number, Tolerance);

        Assert.Equal(1, animator.Advance(1.01f));
        Assert.True(animator.IsIdle);
    }

    /// <summary><c>opacity</c>'s initial is one, so an appearing fade goes <i>down</i>.</summary>
    /// <remarks>
    ///     ⚠ <b>The number in the table that a plausible implementation gets backwards, and the sign
    ///     of the travel is what catches it.</b> A table that filled every absent value with a zero
    ///     would run this transition from 0 to 0.25 rather than from 1 to 0.25 — the same endpoint,
    ///     the opposite journey, and a panel that flashes on before settling. So the assertion is the
    ///     value at a quarter of the way and not the value at the end.
    /// </remarks>
    [Fact]
    public void An_undeclared_opacity_starts_at_one_rather_than_at_zero() {
        var fixture = Fixture("""
            .a { transition: opacity 1s linear }
            .a.faded { opacity: 0.25 }
            """);

        var (animator, element) = Toggled(fixture, "faded");

        var property = fixture.Engine.Properties.Lookup("opacity");

        Assert.True(animator.TryGetCurrent(element, property, 0.5f, out var half));
        Assert.Equal(0.625f, half.Number, Tolerance);
    }

    /// <summary>An inherited property is filled in by the cascade and not by the table.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rule that keeps the table small, and the entry that would have been wrong.</b>
    ///         <c>color</c>'s CSS initial is the user agent's text colour; an entry for it would fade
    ///         every label from black the first time anything restyled it. It needs none —
    ///         <c>StyleResolver</c> materialises inheritance into the computed style, so the child
    ///         already holds the value it really computes to, and the transition runs from the
    ///         parent's old colour to the parent's new one.
    ///     </para>
    ///     <para>
    ///         Which makes this a test of the design rather than of a line: it passes because
    ///         inheritance is resolved, and it would go on passing if the table gained a <c>color</c>
    ///         entry — so the assertion is the <i>midpoint</i>, which a black start would move.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_inherited_property_needs_no_entry_because_the_cascade_already_filled_it_in() {
        var fixture = Fixture("""
            .parent { color: #ff0000 }
            .parent.cool { color: #0000ff }
            .kid { transition: color 1s linear }
            """);

        var parent = fixture.Tree.CreateElement("div", classNames: ["parent"]);
        var kid = fixture.Tree.CreateElement("div", parent, classNames: ["kid"]);
        var animator = Animator(fixture);

        // ⚠ Two things this fixture has to do that the single-element ones do not, and both of them
        // read as "inheritance is broken" when they are missing. The parent's resolved style is
        // handed in, because `Resolve` inherits from what it is given rather than climbing the tree —
        // one pass, not a walk, which `StyleResolver.Build` says out loud. And both are resolved
        // through `Cascade` rather than `Resolve`, because the sharing key is built from the
        // element's own tag, classes and inline block and knows nothing about which parent style was
        // passed — so the cached child comes back still inheriting the colour its parent used to
        // have.
        var warm = fixture.Engine.Resolver.Cascade(
            fixture.Tree,
            kid,
            fixture.Engine.Resolver.Cascade(fixture.Tree, parent)
        );

        animator.Observe(kid, null, warm, 0f);

        Assert.True(fixture.Tree.AddClass(parent, "cool"));

        var cool = fixture.Engine.Resolver.Cascade(
            fixture.Tree,
            kid,
            fixture.Engine.Resolver.Cascade(fixture.Tree, parent)
        );

        animator.Observe(kid, warm, cool, 0f);

        // The child declares no colour of its own, so this is the inheritance and not the cascade.
        Assert.Equal("rgb(255, 0, 0)", fixture.Read(warm, "color"));
        Assert.Equal("rgb(0, 0, 255)", fixture.Read(cool, "color"));
        Assert.Equal(1, animator.RunningCount);

        var property = fixture.Engine.Properties.Lookup("color");

        Assert.True(animator.TryGetCurrent(kid, property, 0.5f, out var half));

        // Halfway from red to blue in Oklab. What matters is that both channels are present: a
        // journey that had started at black would have no red in it at all.
        Assert.True(half.Color.R > 0.05f, $"the red end was lost: {half.Color.R}");
        Assert.True(half.Color.B > 0.05f, $"the blue end was lost: {half.Color.B}");
    }

    /// <summary>A property with no entry keeps the behaviour it had, which is no transition.</summary>
    /// <remarks>
    ///     ⚠ <b>The honest partial, asserted rather than assumed.</b> <c>left</c>'s initial is
    ///     <c>auto</c>, which is a keyword — CSS interpolates it discretely too, so an entry for it
    ///     would buy a jump at the halfway mark in place of no transition at all. That is a different
    ///     picture and not a better one, and this pins that the table did not quietly grow to cover
    ///     it.
    /// </remarks>
    [Fact]
    public void A_property_whose_initial_is_a_keyword_still_does_not_transition() {
        var fixture = Fixture("""
            .a { position: absolute; transition: left 1s linear }
            .a.moved { left: 40px }
            """);

        var (animator, _) = Toggled(fixture, "moved");

        Assert.True(animator.IsIdle);
    }
}
