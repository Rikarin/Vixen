// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>More than one <c>animation-name</c> on one element.</summary>
/// <remarks>
///     <para>
///         <c>animation: spin 1s infinite, pulse 2s infinite</c> is ordinary CSS and the animator ran
///         only the first of them. A spinner that also breathes is the obvious case; the one that
///         made it worth doing properly is that every longhand is a <i>list</i>, so a reader that
///         took the first duration gave the second animation the first one's timing — which is a
///         plausible-looking wrong answer rather than a missing feature.
///     </para>
///     <para>
///         Verified by sabotage, seven of seven landing: reading only the first name fails 7,
///         indexing the longhands instead of cycling them fails 2, collapsing the list where a name
///         is <c>none</c> fails 1, letting the <i>first</i> animation win a shared property fails 1,
///         asking only the last animation rather than the last one with an opinion fails 6, and
///         rebuilding the running list whenever anything changes fails 1.
///     </para>
///     <para>
///         ⚠ <b>Writing these found a defect underneath them.</b> <c>from { width: 0 }</c> to
///         <c>to { width: 100px }</c> had no interpolation at all and swapped at the halfway mark,
///         because a bare zero is a <c>Number</c> and <c>100px</c> is a <c>Length</c>. CSS Values 4
///         says a zero is a valid length; ExCSS serialises <c>0px</c> back out as <c>0</c>; and
///         "grow from nothing" is the commonest animation there is. Fixed in
///         <c>StyleValue.CanInterpolate</c>, pinned by
///         <c>StyleValueTests.A_bare_zero_interpolates_with_a_length_and_takes_its_unit</c>, and
///         removing the fix fails 6 of the tests here.
///     </para>
/// </remarks>
public class MultipleAnimationTests {
    const float Tolerance = 1e-3f;

    [Fact]
    public void Both_animations_run() {
        var (animator, element, fixture) = Running("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .a { animation: fade 2s linear, grow 2s linear }
            """);

        Assert.Equal(2, animator.AnimationCount);
        Assert.Equal(1, animator.AnimatedElementCount);

        // ⚠ Two *different* properties, which is what makes this test see both animations rather
        // than one of them winning. A suite that only ever animated `opacity` twice could not tell
        // "both run" from "the last one runs".
        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("opacity"), 1f, out var opacity));
        Assert.Equal(0.5f, opacity.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("width"), 1f, out var width));
        Assert.Equal(50f, width.Number, Tolerance);
    }

    [Fact]
    public void Each_animation_gets_its_own_duration() {
        var (animator, element, fixture) = Running("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .a { animation: fade 1s linear, grow 4s linear }
            """);

        // ⚠ At half a second the fast one is half done and the slow one is an eighth. Reading the
        // first duration for both — the obvious mistake — puts the second at half as well, and every
        // assertion that used the same duration twice would agree with it.
        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("opacity"), 0.5f, out var opacity));
        Assert.Equal(0.5f, opacity.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("width"), 0.5f, out var width));
        Assert.Equal(12.5f, width.Number, Tolerance);
    }

    [Fact]
    public void A_shorter_longhand_list_cycles_over_the_names() {
        var (animator, element, fixture) = Running("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .a { animation-name: fade, grow; animation-duration: 2s; animation-timing-function: linear }
            """);

        // ⚠ CSS Animations 1 §4.4: the shorter list repeats rather than running out. One duration for
        // two names gives both two seconds — an implementation that indexed straight into the list
        // would give the second animation a duration of zero and drop it, which reads as "the second
        // animation does not work" rather than as a list-matching rule.
        Assert.Equal(2, animator.AnimationCount);

        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("opacity"), 1f, out var opacity));
        Assert.Equal(0.5f, opacity.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("width"), 1f, out var width));
        Assert.Equal(50f, width.Number, Tolerance);
    }

    [Fact]
    public void The_later_animation_wins_a_property_they_both_set() {
        var (animator, element, fixture) = Running("""
            @keyframes dim { from { opacity: 0 } to { opacity: 0.2 } }
            @keyframes bright { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: dim 2s linear, bright 2s linear }
            """);

        // CSS Animations 1 §3: where two of an element's animations set the same property, the one
        // closer to the end of `animation-name` decides it.
        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("opacity"), 1f, out var value));
        Assert.Equal(0.5f, value.Number, Tolerance);
    }

    [Fact]
    public void An_animation_with_no_opinion_does_not_silence_the_one_before_it() {
        var (animator, element, fixture) = Running("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .a { animation: fade 2s linear, grow 2s linear }
            """);

        // ⚠ **The last animation that has an opinion, not the last animation.** `grow` says nothing
        // about opacity, and a loop that stopped at the last entry — or that returned as soon as it
        // reached one — would report no opacity at all here. The two mistakes fail in opposite
        // directions and this is the one that catches the first.
        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("opacity"), 1f, out var opacity));
        Assert.Equal(0.5f, opacity.Number, Tolerance);
    }

    [Fact]
    public void Changing_one_name_leaves_the_other_where_it_was() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            @keyframes grow { from { width: 0px } to { width: 100px } }
            @keyframes shrink { from { width: 100px } to { width: 0px } }
            .a { animation: fade 4s linear, grow 4s linear }
            .a.swapped { animation: fade 4s linear, shrink 4s linear }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);
        var opacity = fixture.Engine.Properties.Lookup("opacity");
        var width = fixture.Engine.Properties.Lookup("width");

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, null, before, 0f);

        // Two seconds in, both are halfway. Now the second name changes.
        fixture.Tree.AddClass(element, "swapped");
        var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, before, after, 2f);

        // ⚠ **The fade keeps its place and the new one starts from zero.** Rebuilding the list on any
        // change would restart the fade too, which is the stutter the single-animation code was
        // careful to avoid and which a list makes easy to reintroduce. At t=3 the fade is three
        // quarters through its own four seconds; the shrink is one second into its.
        Assert.True(animator.TryGetAnimated(element, opacity, 3f, out var faded));
        Assert.Equal(0.75f, faded.Number, Tolerance);

        Assert.True(animator.TryGetAnimated(element, width, 3f, out var shrunk));
        Assert.Equal(75f, shrunk.Number, Tolerance);
    }

    [Fact]
    public void Re_resolving_an_unchanged_style_restarts_nothing() {
        var (animator, element, fixture) = Running("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .a { animation: fade 2s linear, grow 2s linear }
            """);

        var style = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
        animator.Observe(element, style, style, 1f);

        // The style is the same, so both animations keep their start times — otherwise a spinner
        // stutters every time anything else on the element changes.
        Assert.Equal(2, animator.AnimationCount);
        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("opacity"), 1f, out var value));
        Assert.Equal(0.5f, value.Number, Tolerance);
    }

    [Fact]
    public void A_name_of_none_takes_its_place_in_the_list_without_running() {
        var (animator, element, fixture) = Running("""
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .a { animation-name: none, grow; animation-duration: 1s, 4s; animation-timing-function: linear }
            """);

        // ⚠ `none` is skipped but its *position* is not: the durations are matched by index, so the
        // grow must get the 4s and not the 1s. Collapsing the list before matching is the tidy
        // implementation and gives the wrong animation the wrong duration.
        Assert.Equal(1, animator.AnimationCount);

        Assert.True(animator.TryGetAnimated(element, fixture.Engine.Properties.Lookup("width"), 1f, out var width));
        Assert.Equal(25f, width.Number, Tolerance);
    }

    [Fact]
    public void The_names_decide_how_many_there_are() {
        var (animator, _, _) = Running("""
            @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
            .a { animation-name: fade; animation-duration: 1s, 2s, 3s; animation-timing-function: linear }
            """);

        // One name, one animation. Taking the longest list instead would invent two more with no
        // keyframes behind them.
        Assert.Equal(1, animator.AnimationCount);
    }

    static (Animator Animator, StyleNodeId Element, CascadeFixture Fixture) Running(string css) {
        var fixture = new CascadeFixture();
        fixture.Load(css);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var animator = Animator(fixture);

        animator.Observe(element, null, fixture.Engine.Resolver.Resolve(fixture.Tree, element), 0f);

        return (animator, element, fixture);
    }

    static Animator Animator(CascadeFixture fixture) =>
        new(fixture.Engine.Properties, fixture.Engine.Values, fixture.Engine.Names, fixture.Engine.Keyframes);
}
