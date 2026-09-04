// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>That <c>class="transition"</c> moves something, on its own, in a document, over frames.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The consumption gate cannot make this claim and it is worth being exact about why.</b>
///         That gate is per-<i>property</i>: it asks whether anything in the engine acts on
///         <c>transition-property</c>, and the answer has been yes since A20 — measured off the
///         <c>primed</c> scene, which declares a <c>transition-duration</c> of its own. So the family
///         could emit <c>transition-property</c> alone, score <c>works</c> on every property it
///         emitted, and still be a class that does nothing whatsoever when written by itself. That is
///         not a hypothetical: it is what <c>transition</c> was, and the ledger recorded it as a
///         <c>value_gap</c> in prose because no test could hold it.
///     </para>
///     <para>
///         ⚠ <b>The whole instrument is that no <c>duration-*</c> is written anywhere here.</b>
///         <c>transition-duration</c> initially computes to <b>zero</b>, and a transition of zero
///         duration is indistinguishable from no transition at all — both put the property at its
///         destination on the very next frame. So the reading that matters is a value strictly
///         <i>between</i> the two endpoints, for <c>Vixen.Ui.Tests.TransitionTests</c>' reason: an
///         assertion about where the property ends up passes against an engine with no transition
///         machinery at all.
///     </para>
///     <para>
///         The clock is an argument and nothing sleeps, so the numbers are the same on every machine.
///     </para>
/// </remarks>
public class TransitionUtilityTests {
    /// <summary>The theme the generated sheet resolves its tokens against.</summary>
    const string Theme = """
        @theme {
          --spacing: 4px;
        }
        """;

    /// <summary>A `transition` and nothing else, over a width the hover variant moves.</summary>
    /// <remarks>
    ///     ⚠ The width is declared on the rule rather than left to the layout, because
    ///     <c>Animator.Observe</c> reads the from-value out of the previous computed style and a
    ///     property the old style did not hold has nothing to transition <i>from</i>. That is a live
    ///     limitation of the animator, recorded in doc 43, and not something this test is trying to
    ///     prove or work around — it is what a stylesheet has to look like for any transition to run.
    /// </remarks>
    const string Css = """
        root { width: 400px; height: 200px; }
        #box { width: 10px; height: 20px; }
        #box.wide { width: 110px; }
        """;

    [Fact]
    public void The_bare_transition_class_animates_with_no_duration_beside_it() {
        var tokens = ThemeTokens.Parse(Theme);
        var generator = new UtilityGenerator(tokens);

        using var document = new UiDocument(400f, 200f);
        document.Load(Css + '\n' + generator.Generate(["transition"]), StyleOrigin.Author);

        var box = document.Create("div", document.Root, "box", "transition");

        var now = TimeSpan.Zero;
        document.Tick(now);
        document.Update();

        Assert.Equal(10f, box.Width, 1f);

        box.AddClass("wide");

        // ⚠ The class change is seen by the pass that *follows* it, and that pass is what starts the
        // transition — so this frame is still at the old value and at time zero of the run. Sampling
        // here instead is the mistake that reads a working transition as a broken one.
        document.Tick(now);
        document.Update();
        Assert.Equal(10f, box.Width, 1f);

        // ⚠ 60 ms of the family's own 150 ms, which is deliberately not a round fraction of it: a
        // half-way sample would also be produced by a `steps(2)` or by any timing function symmetric
        // about the midpoint, and this one is not.
        now += TimeSpan.FromMilliseconds(60);
        document.Tick(now);
        document.Update();

        var moving = box.Width;

        Assert.True(
            moving is > 10f and < 110f,
            $"the width snapped instead of transitioning: {moving} is not between 10 and 110. With no "
            + "`duration-*` beside it, `transition` emitting `transition-property` alone gives a "
            + "zero-duration transition, which is what an absent one looks like."
        );

        // And it does arrive, so the reading above is a transition in flight rather than a width that
        // is simply wrong. 150 ms is the family's duration; 400 ms is well past it on any timing
        // function.
        now += TimeSpan.FromMilliseconds(400);
        document.Tick(now);
        document.Update();

        Assert.Equal(110f, box.Width, 1f);
    }

    /// <summary>A <c>duration-*</c> beside it still wins, so the default is a default and not a floor.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the change above could have been made by writing the duration into the
    ///     family's own <c>Properties</c>, which would break every <c>transition duration-700</c> in
    ///     the interface.</b> The two declarations land on one element with equal specificity, so what
    ///     decides the winner is source order within the generated sheet — and that is a property of
    ///     the generator's ordering rather than of anything either family says. A test that only
    ///     proved the default works would be green for both arrangements.
    /// </remarks>
    [Fact]
    public void A_duration_beside_it_overrides_the_families_own_default() {
        var tokens = ThemeTokens.Parse(Theme);
        var generator = new UtilityGenerator(tokens);

        using var document = new UiDocument(400f, 200f);
        document.Load(Css + '\n' + generator.Generate(["transition", "duration-1000"]), StyleOrigin.Author);

        var box = document.Create("div", document.Root, "box", "transition", "duration-1000");

        var now = TimeSpan.Zero;
        document.Tick(now);
        document.Update();

        box.AddClass("wide");

        // The frame that starts the run; see the test above for why it is separate.
        document.Tick(now);
        document.Update();

        // Past the family's own 150 ms and nowhere near the second the class asked for. If the
        // default were winning, the width would be at its destination by now.
        now += TimeSpan.FromMilliseconds(300);
        document.Tick(now);
        document.Update();

        Assert.True(
            box.Width < 110f,
            $"the 150 ms default beat the `duration-1000` beside it: the width is already {box.Width}"
        );
    }

    /// <summary>An <c>ease-*</c> beside it survives too, which it does by the family staying silent.</summary>
    /// <remarks>
    ///     ⚠ <b>The hazard here is the same ordering one and the answer is the opposite — say nothing
    ///     rather than say it through a fragment.</b> <c>ease-linear</c> sorts before <c>transition</c>,
    ///     so a <c>transition</c> emitting <c>transition-timing-function: ease</c> would overwrite it;
    ///     but unlike the duration, CSS's <i>initial</i> timing function is already <c>ease</c>, so the
    ///     family gets v4's curve for free and a fragment would be mechanism bought for nothing.
    ///     <c>linear</c> is the keyword to test with because it is the one curve whose midpoint differs
    ///     from <c>ease</c>'s by more than rounding.
    /// </remarks>
    [Fact]
    public void An_ease_beside_it_is_not_overwritten_by_the_families_own_curve() {
        var tokens = ThemeTokens.Parse(Theme);
        var generator = new UtilityGenerator(tokens);

        using var document = new UiDocument(400f, 200f);
        document.Load(Css + '\n' + generator.Generate(["transition", "ease-linear"]), StyleOrigin.Author);

        var box = document.Create("div", document.Root, "box", "transition", "ease-linear");

        var now = TimeSpan.Zero;
        document.Tick(now);
        document.Update();

        box.AddClass("wide");
        document.Tick(now);
        document.Update();

        // Half of the family's 150 ms. Linear is exactly half way — 60px — where `ease`, whose curve
        // is cubic-bezier(0.25, 0.1, 0.25, 1), is well past it. The bound excludes `ease`'s value
        // rather than merely including linear's, which is what makes this an assertion about the
        // curve and not about the transition running at all.
        now += TimeSpan.FromMilliseconds(75);
        document.Tick(now);
        document.Update();

        Assert.InRange(box.Width, 50f, 70f);
    }
}
