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

    /// <summary>Where a point on the box's top edge ends up, which says the angle it has turned by.</summary>
    static Vector2 Tip(UiElement box) =>
        box.Transform is { } transform ? transform.Apply(new Vector2(100f, 0f)) : new Vector2(100f, 0f);

    static void Turned(UiElement box, float degrees) {
        var radians = degrees * MathF.PI / 180f;
        var tip = Tip(box);

        Assert.Equal(100f * MathF.Cos(radians), tip.X, Tolerance);
        Assert.Equal(100f * MathF.Sin(radians), tip.Y, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The Tailwind spinner — <c>to { transform: rotate(360deg) }</c> alone — spins.</b>
    /// </summary>
    /// <remarks>
    ///     CSS Animations 1 § 3 builds the missing <c>from</c> out of the underlying value, which for a
    ///     box with no transform is <c>none</c>, so a quarter of the way through it has turned a
    ///     quarter. The one stop used to hold for the whole run: a full turn, from the first frame,
    ///     indistinguishable from standing still (#1381).
    /// </remarks>
    [Fact]
    public void A_spinner_with_only_a_to_stop_turns() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes spin { to { transform: rotate(360deg); } }
            #box { animation-name: spin; animation-duration: 1s; animation-timing-function: linear;
                   animation-iteration-count: infinite; }
            """
        );

        Frame(document, 0.25);
        Turned(box, 90f);

        Frame(document, 0.625);
        Turned(box, 225f);
    }

    /// <summary>A <c>to</c>-only animation starts from the transform the element's own rule gives it.</summary>
    /// <remarks>
    ///     The underlying value is the cascaded one, not <c>none</c>: from 30° to 90°, half way is 60°.
    ///     Holding the stop would read 90°; starting from <c>none</c> would read 45°.
    /// </remarks>
    [Fact]
    public void A_to_only_transform_starts_from_the_elements_own_transform() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes turn { to { transform: rotate(90deg); } }
            #box { transform: rotate(30deg); animation-name: turn; animation-duration: 1s;
                   animation-timing-function: linear; }
            """
        );

        Frame(document, 0.5);
        Turned(box, 60f);
    }

    /// <summary>A <c>from</c>-only animation of a number ends at the underlying value.</summary>
    /// <remarks>
    ///     The mirrored arm: <c>from { margin-left: 40px }</c> on a box that declares no margin travels
    ///     to the initial <c>0px</c>, so a quarter of the way it is at 30 and half way at 20. The one
    ///     stop used to hold at 40 for the whole run.
    /// </remarks>
    [Fact]
    public void A_from_only_margin_travels_to_the_initial_value() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes slide { from { margin-left: 40px; } }
            #box { animation-name: slide; animation-duration: 1s; animation-timing-function: linear; }
            """
        );

        Frame(document, 0.25);
        Assert.Equal(30f, box.AbsoluteLeft, Tolerance);

        Frame(document, 0.5);
        Assert.Equal(20f, box.AbsoluteLeft, Tolerance);
    }

    /// <summary>A stop in the middle alone is approached from, and left for, the declared value.</summary>
    /// <remarks>
    ///     Both ends synthesised at once: <c>50% { margin-left: 40px }</c> over a declared <c>8px</c>
    ///     is 8 → 40 → 8, so at a quarter and at three quarters the box is at 24.
    /// </remarks>
    [Fact]
    public void A_middle_stop_alone_is_reached_from_and_left_for_the_declared_value() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes bump { 50% { margin-left: 40px; } }
            #box { margin-left: 8px; animation-name: bump; animation-duration: 1s;
                   animation-timing-function: linear; }
            """
        );

        Frame(document, 0.25);
        Assert.Equal(24f, box.AbsoluteLeft, Tolerance);

        Frame(document, 0.5);
        Assert.Equal(40f, box.AbsoluteLeft, Tolerance);

        Frame(document, 0.75);
        Assert.Equal(24f, box.AbsoluteLeft, Tolerance);
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

    /// <summary>
    ///     ⚠ <b><c>to { rotate: 90deg }</c> alone turns from the identity</b>, where it used to hold
    ///     90° from the first frame (#1410).
    /// </summary>
    /// <remarks>
    ///     CSS Transforms 2 interpolates <c>rotate: none</c> against an angle as <c>0deg</c>, so a
    ///     quarter of the way through a linear second the box has turned 22.5°. The individual
    ///     property had no initial value and was not a mix, so the one stop was held and the tip read
    ///     90° — which is what a spinner written this way looked like: a full turn, standing still.
    /// </remarks>
    [Fact]
    public void A_to_only_rotate_turns_from_the_identity() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes turn { to { rotate: 90deg; } }
            #box { animation-name: turn; animation-duration: 1s; animation-timing-function: linear; }
            """
        );

        Frame(document, 0.25);
        Turned(box, 22.5f);

        Frame(document, 0.5);
        Turned(box, 45f);
    }

    /// <summary>A <c>from</c>-only <c>translate</c> travels to zero on each axis, not to a held stop.</summary>
    /// <remarks>
    ///     The identity is shaped after the other end: <c>40px 20px</c> travels to <c>0px 0px</c>, a
    ///     pair <c>StyleValue.Lerp</c> can pair, so a quarter of the way it is at 30, 15. A single zero
    ///     against the pair would not interpolate at all.
    /// </remarks>
    [Fact]
    public void A_from_only_translate_travels_to_zero_on_each_axis() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes slide { from { translate: 40px 20px; } }
            #box { animation-name: slide; animation-duration: 1s; animation-timing-function: linear; }
            """
        );

        Frame(document, 0.25);
        Assert.Equal(30f, box.AbsoluteLeft, Tolerance);
        Assert.Equal(15f, box.AbsoluteTop, Tolerance);
    }

    /// <summary>A <c>to</c>-only <c>scale</c> grows from one, not from zero and not from the stop.</summary>
    /// <remarks>
    ///     <c>scale: none</c> is one: from 1 to 3 a quarter of the way is 1.5, so the tip at (100, 0) is
    ///     at 150. Starting from zero would read 75; holding the stop, 300.
    /// </remarks>
    [Fact]
    public void A_to_only_scale_grows_from_one() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes grow { to { scale: 3; } }
            #box { animation-name: grow; animation-duration: 1s; animation-timing-function: linear; }
            """
        );

        Frame(document, 0.25);
        Assert.Equal(150f, Tip(box).X, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A rotation a class adds is transitioned into, not snapped to</b> (#1410).
    /// </summary>
    /// <remarks>
    ///     The transition half of the same defect: <c>Animator.Observe</c> read the box's rotation
    ///     before the class as <c>Unknown</c> — nothing declared it and it has no initial value — and
    ///     refused to start. Half way through a linear second the box has turned 45° of 90°.
    /// </remarks>
    [Fact]
    public void A_rotation_a_class_adds_is_transitioned_into_from_the_identity() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            #box { transition-property: rotate; transition-duration: 1s; transition-timing-function: linear; }
            #box.turned { rotate: 90deg; }
            """
        );

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 0.5);

        Turned(box, 45f);
    }
}
