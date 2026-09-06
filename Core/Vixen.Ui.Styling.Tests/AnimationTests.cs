// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>@keyframes</c> and <c>animation</c>.</summary>
public class AnimationTests {
    const float Tolerance = 1e-3f;

    [Fact]
    public void Keyframes_load_with_from_and_to_normalised_to_offsets() {
        // ExCSS parses `@keyframes`, which the spike did not establish and which saves the work
        // `@layer` needed — `from` and `to` arrive already rewritten as `0%` and `100%`.
        var fixture = new CascadeFixture();
        fixture.Load("@keyframes fade { from { opacity: 0 } 50% { opacity: 0.25 } to { opacity: 1 } }");

        Assert.Equal(1, fixture.Engine.Keyframes.Count);
        Assert.True(fixture.Engine.Keyframes.TryGet("fade", out var stops));
        Assert.Equal(3, stops.Count);
        Assert.Equal(0f, stops[0].Offset, Tolerance);
        Assert.Equal(0.5f, stops[1].Offset, Tolerance);
        Assert.Equal(1f, stops[2].Offset, Tolerance);
    }

    [Fact]
    public void Stops_are_sorted_however_the_stylesheet_listed_them() {
        var fixture = new CascadeFixture();
        fixture.Load("@keyframes fade { to { opacity: 1 } 25% { opacity: 0.5 } from { opacity: 0 } }");

        Assert.True(fixture.Engine.Keyframes.TryGet("fade", out var stops));
        Assert.Equal([0f, 0.25f, 1f], stops.Select(stop => stop.Offset));
    }

    [Fact]
    public void Redefining_a_name_replaces_it_rather_than_merging_into_it() {
        // CSS's rule, and easy to get wrong: merging would let a stop from a discarded definition
        // survive into the one that replaced it, which is a value nobody wrote appearing mid-loop.
        var fixture = new CascadeFixture();
        fixture.Load("""
            @keyframes fade { from { opacity: 0 } 50% { opacity: 0.9 } to { opacity: 1 } }
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            """);

        Assert.True(fixture.Engine.Keyframes.TryGet("fade", out var stops));
        Assert.Equal(2, stops.Count);
    }

    [Fact]
    public void An_animation_interpolates_between_the_stops_it_is_between() {
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: fade 2s linear }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.Equal(1, animator.AnimationCount);
        Assert.True(animator.TryGetAnimated(element, opacity, 0.5f, out var quarter));
        Assert.Equal(0.25f, quarter.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, opacity, 1.5f, out var threeQuarters));
        Assert.Equal(0.75f, threeQuarters.Number, Tolerance);
    }

    [Fact]
    public void A_middle_stop_is_used_where_it_sits() {
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } 25% { opacity: 1 } to { opacity: 0 } }
            .a { animation: fade 1s linear }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.125f, out var rising));
        Assert.Equal(0.5f, rising.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.25f, out var peak));
        Assert.Equal(1f, peak.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.625f, out var falling));
        Assert.Equal(0.5f, falling.Number, Tolerance);
    }

    [Fact]
    public void An_infinite_animation_loops() {
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: fade 1s linear infinite }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.25f, out var first));
        Assert.True(animator.TryGetAnimated(element, opacity, 7.25f, out var eighth));

        Assert.Equal(first.Number, eighth.Number, Tolerance);
        Assert.False(animator.IsIdle);
    }

    [Fact]
    public void Alternate_runs_the_odd_iterations_backwards() {
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: fade 1s linear infinite alternate }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.25f, out var forwards));
        Assert.Equal(0.25f, forwards.Number, Tolerance);

        // Second iteration, a quarter in — running the other way, so three quarters up.
        Assert.True(animator.TryGetAnimated(element, opacity, 1.25f, out var backwards));
        Assert.Equal(0.75f, backwards.Number, Tolerance);
    }

    [Fact]
    public void Fill_decides_what_is_left_behind_before_and_after() {
        var none = Running("@keyframes fade { from { opacity: 0.2 } to { opacity: 0.8 } } .a { animation: fade 1s linear 1s }");
        var both = Running("@keyframes fade { from { opacity: 0.2 } to { opacity: 0.8 } } .a { animation: fade 1s linear 1s both }");

        // During the delay, and after the end.
        Assert.False(none.Animator.TryGetAnimated(none.Element, none.Opacity, 0.5f, out _));
        Assert.False(none.Animator.TryGetAnimated(none.Element, none.Opacity, 5f, out _));

        Assert.True(both.Animator.TryGetAnimated(both.Element, both.Opacity, 0.5f, out var waiting));
        Assert.Equal(0.2f, waiting.Number, Tolerance);

        Assert.True(both.Animator.TryGetAnimated(both.Element, both.Opacity, 5f, out var held));
        Assert.Equal(0.8f, held.Number, Tolerance);
    }

    [Fact]
    public void The_easing_applies_within_each_iteration_and_not_across_the_whole_run() {
        // CSS's rule, and the one people are surprised by: `animation: spin 2s ease-in-out infinite`
        // eases in and out on every revolution rather than once over the run.
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: fade 1s ease-in-out infinite }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.5f, out var firstMid));
        Assert.True(animator.TryGetAnimated(element, opacity, 4.5f, out var fifthMid));

        Assert.Equal(0.5f, firstMid.Number, 1e-2f);
        Assert.Equal(firstMid.Number, fifthMid.Number, Tolerance);
    }

    [Fact]
    public void A_running_animation_keeps_its_place_when_something_else_on_the_element_changes() {
        // Restarting the animation whenever the style is re-resolved would make a spinner stutter
        // every time anything else changed — which, with invalidation working properly, is exactly
        // when it would be least expected.
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: fade 1s linear infinite }
            .a.wide { padding-left: 8px }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, before, 0f);

        fixture.Tree.AddClass(element, "wide");
        var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, before, after, 0.5f);

        Assert.True(animator.TryGetAnimated(element, opacity, 0.5f, out var value));
        Assert.Equal(0.5f, value.Number, Tolerance);
    }

    [Fact]
    public void A_transition_beats_an_animation_on_the_same_property() {
        // CSS Cascading 5 §6.2 puts transitions above animations, and it is the order that reads
        // right: a transition is a response to something that just happened and has to win over a
        // loop that was already running.
        var fixture = Fixture("""
            @keyframes fade { from { opacity: 0 } to { opacity: 0.2 } }
            .a { opacity: 0; animation: fade 1s linear infinite; transition: opacity 1s linear }
            .a.shown { opacity: 1 }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, before, 0f);

        fixture.Tree.AddClass(element, "shown");
        var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, before, after, 0f);

        // The transition says 0.5 at the halfway mark; the animation would say 0.1.
        Assert.Equal("0.5", fixture.Read(animator.Apply(element, after, 0.5f), "opacity"));
    }

    /// <summary>A <c>@keyframes</c> name is a custom ident, and its case is not the parser's to fold.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The guard on the keyword case fold, and the reason it was written at the intern
    ///         call site rather than in <see cref="NameTable" />.</b> A "keyword" to
    ///         <c>StyleValueParser</c> is any identifier it did not recognise, and CSS Values 4
    ///         § 3.1's case-insensitivity is a rule about <i>keywords</i> only — a custom ident is
    ///         matched exactly. Folding the table would have made <c>FadeIn</c> and <c>fadein</c> one
    ///         animation, and folding it in <see cref="NameTable" /> would additionally have made
    ///         VXML's <c>Button</c> component and the intrinsic <c>button</c> one selector.
    ///     </para>
    ///     <para>
    ///         It holds because a name reaches the animator out of the <i>values</i> table as raw
    ///         text — <c>animation-name</c>, <c>font-family</c> and a grid area name never touch the
    ///         keyword intern at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_keyframes_name_keeps_the_case_the_author_wrote() {
        var (animator, element, opacity) = Running("""
            @keyframes FadeIn { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: FadeIn 2s linear }
            """);

        Assert.Equal(1, animator.AnimationCount);
        Assert.True(animator.TryGetAnimated(element, opacity, 1f, out var half));
        Assert.Equal(0.5f, half.Number, Tolerance);
    }

    /// <summary>And the same name in another case names nothing, which is what "not folded" means.</summary>
    /// <remarks>
    ///     ⚠ <b><c>AnimationCount</c> is the wrong instrument for this and says 1 either way</b>: it
    ///     counts the names <c>animation-name</c> lists, not the ones that found a <c>@keyframes</c>
    ///     rule. Asking for the animated value is what tells a resolved name from an unresolved one.
    /// </remarks>
    [Fact]
    public void A_keyframes_name_in_another_case_matches_nothing() {
        var (animator, element, opacity) = Running("""
            @keyframes FadeIn { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: fadein 2s linear }
            """);

        Assert.False(animator.TryGetAnimated(element, opacity, 1f, out _));
    }

    static (Animator Animator, StyleNodeId Element, int Opacity) Running(string css) {
        var fixture = Fixture(css);
        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        return (animator, element, fixture.Engine.Properties.Lookup("opacity"));
    }

    static CascadeFixture Fixture(string css) {
        var fixture = new CascadeFixture();
        fixture.Load(css);
        return fixture;
    }

    static Animator Animator(CascadeFixture fixture) =>
        new(fixture.Engine.Properties, fixture.Engine.Values, fixture.Engine.Names, fixture.Engine.Keyframes);
}
