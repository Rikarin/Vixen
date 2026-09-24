// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Animations whose one end is a value no keyframe and no declaration wrote: the element's own
///     underlying value, read in a document over frames.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion is a frame in the middle at a known time, against a closed form.</b>
///         Both defects here are "the animation holds one end", and an end is what the animation and
///         its absence agree on — a spinner at a full turn looks exactly like a spinner at rest. Each
///         box is at the origin with <c>transform-origin: 0 0</c> and a linear one-second run, so a
///         point's image and a margin are functions of the time alone.
///     </para>
///     <para>
///         ⚠ <b>Read off the document — <see cref="UiElement.Transform" /> and
///         <see cref="UiElement.AbsoluteLeft" /> — and never off the animator.</b>
///         <c>InitialValueTransitionTests</c> asserts through <c>Animator.TryGetCurrent</c>, which saw
///         the running transition #1382 is about, and so was green over a box that snapped home.
///     </para>
/// </remarks>
public class UnderlyingValueAnimationTests {
    const float Tolerance = 0.01f;

    const string Base = """
        root { width: 400px; height: 300px; }
        #box { width: 100px; height: 40px; background-color: #202020; transform-origin: 0px 0px; }
        """;

    static UiElement Settled(UiDocument document, string css) {
        document.Load(Base + css);

        var box = document.Create("div", document.Root, "box");

        Frame(document, 0.0);

        return box;
    }

    static void Frame(UiDocument document, double seconds) {
        document.Tick(TimeSpan.FromSeconds(seconds));
        document.Update();
    }

    /// <summary>
    ///     ⚠ <b>A number the cascade stops holding is seen on its way back to its initial value.</b>
    /// </summary>
    /// <remarks>
    ///     The numeric half of #540's shape, on the transition side (#1382). Taking the class off
    ///     leaves <c>margin-left</c> out of the computed style altogether; <c>Animator.Observe</c>
    ///     starts the run back to <c>0px</c>, and nothing overlaid it, because <c>Apply</c> walks the
    ///     properties the style has and <c>Withdrawing</c> overlaid transform mixes only. Half way
    ///     back the box is at 20, where it used to be at 0 on the first frame.
    /// </remarks>
    [Fact]
    public void A_margin_the_cascade_drops_is_seen_on_its_way_back() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            #box { transition-property: margin-left; transition-duration: 1s;
                   transition-timing-function: linear; }
            #box.moved { margin-left: 40px; }
            """
        );

        box.AddClass("moved");
        Frame(document, 0.0);
        Frame(document, 1.0);
        Frame(document, 1.5);

        Assert.Equal(40f, box.AbsoluteLeft, Tolerance);

        box.RemoveClass("moved");
        Frame(document, 1.5);
        Frame(document, 2.0);

        Assert.Equal(20f, box.AbsoluteLeft, Tolerance);

        Frame(document, 2.25);

        Assert.Equal(10f, box.AbsoluteLeft, Tolerance);

        Frame(document, 2.5);
        Frame(document, 2.6);

        Assert.Equal(0f, box.AbsoluteLeft, Tolerance);
    }
}
