// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a declared <c>@keyframes</c> animation actually runs, in a document, over frames.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The rule deliberately does not declare the property the keyframes move.</b> That is
///         the whole fixture. <c>Animator.Apply</c> used to overlay only the properties already in
///         the element's computed style, which is right for a transition — <c>Observe</c> takes a
///         transition's from-value out of the previous style, so one on an undeclared property has
///         nothing to start from — and wrong for an animation, which is a complete description of the
///         value over time and asks the cascade for nothing. A <c>@keyframes</c> block therefore
///         parsed, started, counted, and answered <c>TryGetAnimated</c> correctly while moving
///         nothing at all; writing the property into the rule as well made it work, which is not
///         something CSS asks an author to do.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the repository could see that.</b> The one test that went through
///         <c>Apply</c> at all — <c>Vixen.Ui.Styling.Tests.AnimationTests</c> — declares
///         <c>opacity: 0</c> in its rule, so the property was in the style and the loop found it;
///         every other animation test calls <c>TryGetAnimated</c> directly and skips <c>Apply</c>.
///         And there was no document-level animation test beside <see cref="TransitionTests" />,
///         which is what would have asked the question the way an author does.
///     </para>
///     <para>
///         The frames are driven by hand and the clock is an argument, as <see cref="TransitionTests" />
///         explains: nothing here sleeps and nothing reads the wall clock.
///     </para>
/// </remarks>
public class AnimationTests {
    /// <summary>
    ///     ⚠ <c>#box</c> declares a height and no width; the width exists only in the keyframes.
    /// </summary>
    const string Css = """
        root { width: 400px; height: 200px; }
        @keyframes grow { from { width: 10px } to { width: 110px } }
        #box { height: 20px; animation: grow 200ms linear; }
        """;

    static UiElement Settled(UiDocument document, string css) {
        document.Load(css);

        var box = document.Create("div", document.Root, "box");

        document.Tick(TimeSpan.Zero);
        document.Update();

        return box;
    }

    static void Frame(UiDocument document, double seconds) {
        document.Tick(TimeSpan.FromSeconds(seconds));
        document.Update();
    }

    /// <summary>A keyframes block moves a property the element's own rule never mentions.</summary>
    /// <remarks>
    ///     ⚠ <b>Two points in flight and not an endpoint, for <see cref="TransitionTests" />' reason.</b>
    ///     An animation and its absence disagree about everything here — with the property undeclared
    ///     the element has no width at all and came out at zero, which is what this measured before
    ///     the fix — but a single reading would still be satisfied by a property that had been
    ///     <i>set</i> once rather than animated. Two readings a tenth of a second apart, each
    ///     excluding both stops, say it is travelling.
    /// </remarks>
    [Fact]
    public void A_keyframes_block_animates_a_property_the_rule_does_not_declare() {
        using var document = new UiDocument(400f, 200f);
        var box = Settled(document, Css);

        Assert.Equal(10f, box.Width);

        Frame(document, 0.05);
        Assert.InRange(box.Width, 20f, 45f);

        Frame(document, 0.15);
        Assert.InRange(box.Width, 75f, 100f);
    }

    /// <summary>The element's own declaration is what the animation overrides, when it has one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The mirror of the test above, and it is what stops the fix being "append whatever
    ///         the keyframes name".</b> A property the cascade <i>did</i> set must still be overlaid
    ///         in place rather than appended a second time — <c>ComputedStyle</c> is a sorted table
    ///         read by binary search, and a duplicated key resolves to whichever of the pair the
    ///         search happens to land on.
    ///     </para>
    ///     <para>
    ///         The last frame is past the end of the animation, where <c>animation-fill-mode: none</c>
    ///         hands the property back to the cascade. Reading exactly the declared fifty there is
    ///         what says the table came back intact rather than holding a stale animated value under
    ///         a second copy of the key.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_animated_property_the_rule_declares_is_overlaid_in_place() {
        using var document = new UiDocument(400f, 200f);

        var box = Settled(
            document,
            """
            root { width: 400px; height: 200px; }
            @keyframes grow { from { width: 10px } to { width: 110px } }
            #box { width: 50px; height: 20px; animation: grow 200ms linear; }
            """
        );

        Assert.Equal(10f, box.Width);

        Frame(document, 0.10);
        Assert.InRange(box.Width, 40f, 80f);

        Frame(document, 0.30);
        Assert.Equal(50f, box.Width);
    }
}
