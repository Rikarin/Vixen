// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>prefers-reduced-motion</c>, as a query and as a switch the animator honours.</summary>
/// <remarks>
///     ⚠ <b>Both halves are asserted here because either alone is a feature that does nothing.</b>
///     A query with no switch means every application that did not write a reduced-motion block
///     ignores the preference; a switch with no query means an author cannot say what should happen
///     instead. Before this, the query was a *load-time diagnostic* — a stylesheet containing
///     `@media (prefers-reduced-motion: reduce)` failed to load at all.
/// </remarks>
public class ReducedMotionTests {
    [Fact]
    public void The_feature_answers_reduce_when_the_context_says_so() {
        var reduced = new MediaContext(800f, 600f, ReducedMotion: MotionPreference.Reduce);

        Assert.True(MediaQuery.TryEvaluate("(prefers-reduced-motion: reduce)", reduced, out var matches, out _));
        Assert.True(matches);

        Assert.True(
            MediaQuery.TryEvaluate("(prefers-reduced-motion: no-preference)", reduced, out var otherWay, out _)
        );

        Assert.False(otherWay);
    }

    [Fact]
    public void And_no_preference_is_the_default_rather_than_a_missing_answer() {
        var plain = new MediaContext(800f, 600f);

        Assert.True(MediaQuery.TryEvaluate("(prefers-reduced-motion: reduce)", plain, out var matches, out _));
        Assert.False(matches);

        Assert.True(MediaQuery.TryEvaluate("(prefers-reduced-motion: no-preference)", plain, out var none, out _));
        Assert.True(none);
    }

    /// <summary>The bare form means <c>reduce</c>, which is what almost every sheet writes.</summary>
    /// <remarks>
    ///     Media Queries 5 § 6.2 makes <c>no-preference</c> the feature's false value, so
    ///     <c>@media (prefers-reduced-motion)</c> is the idiomatic spelling and would otherwise be a
    ///     diagnostic on a stylesheet that is correct.
    /// </remarks>
    [Fact]
    public void The_boolean_form_means_reduce() {
        Assert.True(
            MediaQuery.TryEvaluate(
                "(prefers-reduced-motion)",
                new MediaContext(800f, 600f, ReducedMotion: MotionPreference.Reduce),
                out var reduced,
                out _
            )
        );

        Assert.True(reduced);

        Assert.True(
            MediaQuery.TryEvaluate("(prefers-reduced-motion)", new MediaContext(800f, 600f), out var plain, out _)
        );

        Assert.False(plain);
    }

    /// <summary>A nonsense value is still a diagnostic rather than a guess.</summary>
    [Fact]
    public void An_unknown_value_fails_to_evaluate() {
        Assert.False(
            MediaQuery.TryEvaluate("(prefers-reduced-motion: yes)", new MediaContext(800f, 600f), out _, out var why)
        );

        Assert.NotNull(why);

        // Discrete, so the range prefixes are a typo and not a spelling.
        Assert.False(
            MediaQuery.TryEvaluate(
                "(min-prefers-reduced-motion: reduce)",
                new MediaContext(800f, 600f),
                out _,
                out var prefixed
            )
        );

        Assert.NotNull(prefixed);
    }

    /// <summary>A block written for it applies, which is the whole point of the query.</summary>
    [Fact]
    public void A_reduced_motion_block_wins_when_the_preference_is_set() {
        var fixture = new CascadeFixture();

        fixture.Load(
            """
            .a { color: red }
            @media (prefers-reduced-motion: reduce) { .a { color: blue } }
            """,
            media: new MediaContext(800f, 600f, ReducedMotion: MotionPreference.Reduce)
        );

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);

        // The value arrives normalised by the parser, which is why this is not the literal "blue".
        Assert.Equal("rgb(0, 0, 255)", fixture.Value(element, "color"));
    }

    /// <summary>With it on, a transition does not run at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is on <c>RunningCount</c> and not on a value after some elapsed
    ///     time.</b> A transition that had started and been stepped to its end would give the same
    ///     final value as one that never started, so asserting the value would pass against exactly
    ///     the code this exists to prove wrong.
    /// </remarks>
    [Fact]
    public void A_transition_does_not_start_when_motion_is_reduced() {
        var fixture = Fixture("""
            .a { opacity: 0; transition: opacity 1s linear }
            .a.shown { opacity: 1 }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);

        fixture.Tree.AddClass(element, "shown");
        var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);

        var moving = Animator(fixture);
        moving.Observe(element, before, after, 0f);
        Assert.Equal(1, moving.RunningCount);

        var still = Animator(fixture);
        still.ReduceMotion = true;
        still.Observe(element, before, after, 0f);
        Assert.Equal(0, still.RunningCount);
    }

    /// <summary>And a keyframe animation is not left running, because it may never end.</summary>
    [Fact]
    public void A_keyframe_animation_does_not_run_when_motion_is_reduced() {
        var fixture = Fixture("""
            @keyframes spin { from { opacity: 0 } to { opacity: 1 } }
            .a { animation: spin 2s linear infinite }
            """);

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var style = fixture.Engine.Resolver.Resolve(fixture.Tree, element);

        var moving = Animator(fixture);
        moving.Observe(element, null, style, 0f);
        Assert.Equal(1, moving.AnimationCount);

        var still = Animator(fixture);
        still.ReduceMotion = true;
        still.Observe(element, null, style, 0f);
        Assert.Equal(0, still.AnimationCount);
        Assert.True(still.IsIdle);
    }

    /// <summary>The engine wires the two together, and a hot stylesheet edit does not forget it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>Reload</c> half is the one a change here breaks silently, and the first
    ///         version of this test could not see it.</b> <c>Build</c> replaces the animator, so an
    ///         implementation that wrote the flag onto the animator and nowhere else loses it on the
    ///         next hot edit of a <c>.vcss</c> — a query that still answers "reduce" and an animator
    ///         that has started animating again. Asserting it after <c>Load</c> proves nothing: ⚠
    ///         <c>Load</c> appends a sheet and does <i>not</i> rebuild, and neither does
    ///         <c>SetMedia</c>, whatever <c>Preprocess</c>' remarks used to say. <c>Reload</c> is the
    ///         call that actually goes through <c>Build</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_engine_hands_the_preference_to_its_animator_and_keeps_it_across_a_reload() {
        var engine = new StyleEngine();
        engine.Load(".a { color: red }");
        Assert.False(engine.Animations.ReduceMotion);

        engine.SetMedia(new MediaContext(800f, 600f, ReducedMotion: MotionPreference.Reduce));
        Assert.True(engine.Animations.ReduceMotion);

        engine.Reload();
        Assert.True(engine.Animations.ReduceMotion);

        engine.SetMedia(new MediaContext(800f, 600f));
        engine.Reload();
        Assert.False(engine.Animations.ReduceMotion);
    }

    static CascadeFixture Fixture(string css) {
        var fixture = new CascadeFixture();
        fixture.Load(css);
        return fixture;
    }

    static Animator Animator(CascadeFixture fixture) =>
        new(fixture.Engine.Properties, fixture.Engine.Values, fixture.Engine.Names, fixture.Engine.Keyframes);
}
