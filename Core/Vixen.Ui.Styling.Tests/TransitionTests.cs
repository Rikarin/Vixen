// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Reading a <c>transition</c>, and running one.</summary>
public class TransitionTests {
    const float Tolerance = 1e-3f;

    [Fact]
    public void The_shorthand_reads_the_same_whether_or_not_ExCSS_could_expand_it() {
        // ⚠ The finding this whole parser exists for. ExCSS expands `transition` only when it
        // recognises every part, so `spring()` — Vixen's own extension — makes the difference
        // between four longhand declarations and one unexpanded string. Whether the longhands exist
        // therefore depends on whether the author used a Vixen feature, and nothing downstream
        // should have to know that.
        var expanded = new CascadeFixture();
        expanded.Load(".a { transition: opacity 200ms ease-in 50ms }");

        var unexpanded = new CascadeFixture();
        unexpanded.Load(".a { transition: opacity 200ms ease-in 50ms, transform 1s spring(1, 100, 10) }");

        // ExCSS split the first into longhands and left the second whole. Both have to work.
        Assert.NotNull(expanded.Read(Resolve(expanded), "transition-property"));
        Assert.Null(expanded.Read(Resolve(expanded), "transition"));
        Assert.NotNull(unexpanded.Read(Resolve(unexpanded), "transition"));
        Assert.Null(unexpanded.Read(Resolve(unexpanded), "transition-property"));

        static ComputedStyle Resolve(CascadeFixture fixture) {
            var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
            return fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        }
    }

    [Fact]
    public void The_shorthand_grammar_is_positional_for_times_and_named_for_everything_else() {
        var properties = new NameTable();
        var parser = new TransitionParser(properties);
        var specs = new List<TransitionSpec>();

        Assert.True(parser.TryParseShorthand("opacity 200ms ease-in 50ms", specs));

        var spec = Assert.Single(specs);

        Assert.Equal(properties.Lookup("opacity"), spec.Property);
        Assert.Equal(0.2f, spec.Duration, Tolerance);
        Assert.Equal(0.05f, spec.Delay, Tolerance);
        Assert.Equal(TimingFunctionKind.CubicBezier, spec.Timing.Kind);

        // One time means duration and no delay, which is the rule people rely on without noticing.
        specs.Clear();
        Assert.True(parser.TryParseShorthand("width 1s", specs));
        Assert.Equal(1f, specs[0].Duration, Tolerance);
        Assert.Equal(0f, specs[0].Delay, Tolerance);
    }

    [Fact]
    public void All_is_the_absence_of_a_named_property() {
        var parser = new TransitionParser(new NameTable());
        var specs = new List<TransitionSpec>();

        Assert.True(parser.TryParseShorthand("all 300ms", specs));
        Assert.Equal(NameTable.None, specs[0].Property);
    }

    [Fact]
    public void Springs_survive_the_shorthand() {
        var parser = new TransitionParser(new NameTable());
        var specs = new List<TransitionSpec>();

        Assert.True(parser.TryParseShorthand("transform 400ms spring(2, 180, 12)", specs));

        var timing = specs[0].Timing;

        Assert.Equal(TimingFunctionKind.Spring, timing.Kind);
        Assert.Equal(2f, timing.Mass, Tolerance);
        Assert.Equal(180f, timing.Stiffness, Tolerance);
        Assert.Equal(12f, timing.Damping, Tolerance);
    }

    [Fact]
    public void A_comma_separated_transition_gives_one_spec_each() {
        var properties = new NameTable();
        var parser = new TransitionParser(properties);
        var specs = new List<TransitionSpec>();

        Assert.True(parser.TryParseShorthand("opacity 200ms, transform 400ms ease-out", specs));
        Assert.Equal(2, specs.Count);
        Assert.Equal(0.2f, specs[0].Duration, Tolerance);
        Assert.Equal(0.4f, specs[1].Duration, Tolerance);
    }

    [Fact]
    public void Nothing_transitions_on_the_first_resolve() {
        // An element fading in from whatever the uninitialised value happened to be is the classic
        // way to get this wrong. It should appear as specified and transition only on change.
        var fixture = TransitionFixture(".a { opacity: 1; transition: opacity 1s linear }");
        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.True(animator.IsIdle);
    }

    [Fact]
    public void A_changed_value_transitions_between_the_two() {
        var fixture = TransitionFixture("""
            .a { opacity: 0; transition: opacity 1s linear }
            .a.shown { opacity: 1 }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        var hidden = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, hidden, 0f);

        fixture.Tree.AddClass(element, "shown");
        var shown = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, hidden, shown, 0f);

        Assert.Equal(1, animator.RunningCount);

        var opacity = fixture.Engine.Properties.Lookup("opacity");

        Assert.True(animator.TryGetCurrent(element, opacity, 0.25f, out var quarter));
        Assert.Equal(0.25f, quarter.Number, Tolerance);

        Assert.True(animator.TryGetCurrent(element, opacity, 0.75f, out var threeQuarters));
        Assert.Equal(0.75f, threeQuarters.Number, Tolerance);

        Assert.Equal(1, animator.Advance(1.01f));
        Assert.True(animator.IsIdle);
    }

    [Fact]
    public void An_interrupted_transition_reverses_from_where_it_actually_is() {
        // The case that separates a good implementation from a bad one. Hovering away halfway
        // through a fade must reverse from the current value, not from the original — and must not
        // take the full duration to travel the half it has left, or moving the mouse on and off
        // repeatedly makes the element drift further behind with every pass.
        var fixture = TransitionFixture("""
            .a { opacity: 0; transition: opacity 1s linear }
            .a.shown { opacity: 1 }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        var hidden = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, hidden, 0f);

        fixture.Tree.AddClass(element, "shown");
        var shown = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, hidden, shown, 0f);

        // Halfway up, then reverse.
        Assert.True(animator.TryGetCurrent(element, opacity, 0.5f, out var midway));
        Assert.Equal(0.5f, midway.Number, Tolerance);

        fixture.Tree.RemoveClass(element, "shown");
        var again = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, shown, again, 0.5f);

        // Immediately after reversing it is still where it was, not back at the start.
        Assert.True(animator.TryGetCurrent(element, opacity, 0.5f, out var atReversal));
        Assert.Equal(0.5f, atReversal.Number, Tolerance);

        // And it takes the half-duration it has left rather than a full second: by 1.0s — half a
        // second after reversing — it has arrived.
        Assert.True(animator.TryGetCurrent(element, opacity, 1.0f, out var arrived));
        Assert.Equal(0f, arrived.Number, 1e-2f);
    }

    [Fact]
    public void A_delay_holds_the_starting_value() {
        var fixture = TransitionFixture("""
            .a { opacity: 0; transition: opacity 1s linear 0.5s }
            .a.shown { opacity: 1 }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        var hidden = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, hidden, 0f);

        fixture.Tree.AddClass(element, "shown");
        animator.Observe(element, hidden, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.True(animator.TryGetCurrent(element, opacity, 0.4f, out var waiting));
        Assert.Equal(0f, waiting.Number, Tolerance);

        Assert.True(animator.TryGetCurrent(element, opacity, 1.0f, out var moving));
        Assert.Equal(0.5f, moving.Number, Tolerance);
    }

    [Fact]
    public void A_property_no_transition_covers_changes_instantly() {
        var fixture = TransitionFixture("""
            .a { opacity: 0; background: rgb(0, 0, 0); transition: opacity 1s linear }
            .a.shown { opacity: 1; background: rgb(255, 255, 255) }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, before, 0f);

        fixture.Tree.AddClass(element, "shown");
        animator.Observe(element, before, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        // Opacity only. `transition` names one property and `background` is not it.
        Assert.Equal(1, animator.RunningCount);
        Assert.False(
            animator.TryGetCurrent(element, fixture.Engine.Properties.Lookup("background"), 0.5f, out _)
        );
    }

    [Fact]
    public void The_running_value_is_overlaid_above_everything_including_important() {
        // CSS Cascading 5 puts the transition tier at the very top of the table, above `!important`,
        // and it is the only arrangement that works: a transition that could be outvoted would
        // stutter whenever anything else changed.
        var fixture = TransitionFixture("""
            .a { opacity: 0; transition: opacity 1s linear }
            .a.shown { opacity: 1 !important }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, before, 0f);

        fixture.Tree.AddClass(element, "shown");
        var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, before, after, 0f);

        Assert.Equal("1", fixture.Read(after, "opacity"));
        Assert.Equal("0.5", fixture.Read(animator.Apply(element, after, 0.5f), "opacity"));
    }

    [Fact]
    public void Applying_nothing_returns_the_very_same_style_object() {
        // So that a frame in which nothing is animating costs no allocation and leaves every
        // reference comparison downstream answering "unchanged".
        var fixture = TransitionFixture(".a { opacity: 1 }");
        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var style = fixture.Engine.Resolver.Resolve(fixture.Tree, element);

        Assert.Same(style, animator.Apply(element, style, 0.5f));
    }

    static CascadeFixture TransitionFixture(string css) {
        var fixture = new CascadeFixture();
        fixture.Load(css);
        return fixture;
    }

    static Animator Animator(CascadeFixture fixture) =>
        new(fixture.Engine.Properties, fixture.Engine.Values, fixture.Engine.Names, fixture.Engine.Keyframes);
}
