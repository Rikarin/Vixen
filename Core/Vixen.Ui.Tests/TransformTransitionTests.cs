// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A <c>transform</c> that transitions, read in flight, in a document, over frames.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until #174 none of these could start.</b> A <c>&lt;transform-list&gt;</c> parses as
///         <c>StyleValueKind.Unknown</c>, and <c>Animator.Observe</c> removed a transition whose either
///         end was <c>Unknown</c> — so a transform did not jump at the end of its duration, it arrived
///         on the first frame. Every assertion here is therefore about a frame in the MIDDLE, which is
///         the only place a transition and its absence disagree.
///     </para>
///     <para>
///         <b>The numbers are closed form.</b> Every box is 100 by 40 at the origin with
///         <c>transform-origin: 0 0</c> and a linear one-second transition, so the matrix at time
///         <c>t</c> is a function of <c>t</c> alone and a point's image can be written down by hand.
///     </para>
/// </remarks>
public class TransformTransitionTests {
    const float Tolerance = 0.01f;

    const string Base = """
        root { width: 400px; height: 300px; }
        #box { position: absolute; left: 0px; top: 0px; width: 100px; height: 40px;
               background-color: #202020; transform-origin: 0px 0px;
               transition-property: transform; transition-duration: 1s;
               transition-timing-function: linear; }
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
        Assert.IsType<UiTransform>(box.Transform).Apply(new Vector2(100f, 0f));

    /// <summary>
    ///     ⚠ <b>A quarter turn is an eighth of a turn half way through, and the pointer agrees.</b>
    /// </summary>
    /// <remarks>
    ///     The hit test is the half an implementation that interpolated only the picture would fail.
    ///     The probe at 80 points along the 45° diagonal is inside the box at 45° and outside it at
    ///     both ends — below the unrotated box and right of the fully rotated one — so a transform
    ///     that is at either endpoint misses it.
    /// </remarks>
    [Fact]
    public void A_transform_transition_passes_through_the_middle_and_is_clicked_there() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(document, "#box { transform: rotate(0deg); } #box.turned { transform: rotate(90deg); }");

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 0.5);

        var tip = Tip(box);

        Assert.Equal(70.7107f, tip.X, Tolerance);
        Assert.Equal(70.7107f, tip.Y, Tolerance);

        var diagonal = 80f / MathF.Sqrt(2f);

        Assert.Same(box, document.HitTest(diagonal, diagonal + 1f));

        Frame(document, 1.0);

        var end = Tip(box);

        Assert.Equal(0f, end.X, Tolerance);
        Assert.Equal(100f, end.Y, Tolerance);
        Assert.NotSame(box, document.HitTest(diagonal, diagonal + 1f));
    }

    /// <summary>
    ///     ⚠ <b>Function by function, which is a whole turn where a matrix interpolation is nothing.</b>
    /// </summary>
    /// <remarks>
    ///     <c>rotate(0deg)</c> and <c>rotate(360deg)</c> are the same matrix, so an implementation that
    ///     decomposed both ends and interpolated those would hold the box still for the whole second.
    ///     CSS interpolates the arguments of a pair that shares a primitive, so a quarter of the way
    ///     through the box has turned a quarter.
    /// </remarks>
    [Fact]
    public void A_full_turn_turns_rather_than_standing_still() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(document, "#box { transform: rotate(0deg); } #box.turned { transform: rotate(360deg); }");

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 0.25);

        var tip = Tip(box);

        Assert.Equal(0f, tip.X, Tolerance);
        Assert.Equal(100f, tip.Y, Tolerance);
    }

    /// <summary>
    ///     Two lists that share no primitive meet in the middle of their decompositions.
    /// </summary>
    /// <remarks>
    ///     <c>translateX(100px)</c> against <c>rotate(90deg)</c>: the first pair is a translation and a
    ///     rotation, so both lists go to matrices, each is decomposed, and half way the parts are half
    ///     a translation and an eighth of a turn — recomposed as "turn, then move". The point
    ///     <c>(10, 0)</c> therefore lands at <c>(50 + 10 cos 45°, 10 sin 45°)</c>. ⚠ A pairwise reading
    ///     of the two, which is the failure this pins, has no answer here at all.
    /// </remarks>
    [Fact]
    public void Two_lists_that_do_not_pair_up_interpolate_by_decomposition() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(document, "#box { transform: translateX(100px); } #box.turned { transform: rotate(90deg); }");

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 0.5);

        var point = Assert.IsType<UiTransform>(box.Transform).Apply(new Vector2(10f, 0f));

        Assert.Equal(50f + (10f * MathF.Sqrt(0.5f)), point.X, Tolerance);
        Assert.Equal(10f * MathF.Sqrt(0.5f), point.Y, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>Reversed half way, it comes back over half the time, and it does not nest.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         CSS Transitions 1 § 3's reversing rule: taking the class off at 45° starts the way back
    ///         from 45°, and gives it the fraction of the duration the distance is — half a second.
    ///         A quarter of a second later the box is at 22.5°, and at the half second it is home.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the value written is still ONE mix, re-aimed.</b> A pointer brushing on and
    ///         off a card reverses it every few frames; nesting the displayed mix inside a new one
    ///         each time would grow the value without bound.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Reversed_half_way_it_returns_in_half_the_time_as_the_same_mix() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(document, "#box { transform: rotate(0deg); } #box.turned { transform: rotate(90deg); }");

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 0.5);

        box.RemoveClass("turned");
        Frame(document, 0.5);
        Frame(document, 0.75);

        var tip = Tip(box);
        var (cos, sin) = (MathF.Cos(MathF.PI / 8f), MathF.Sin(MathF.PI / 8f));

        Assert.Equal(100f * cos, tip.X, Tolerance);
        Assert.Equal(100f * sin, tip.Y, Tolerance);

        var written = document.Styles.Values.NameOf(Written(document, box));

        Assert.Equal(1, Occurrences(written, "transform-mix("));

        Frame(document, 1.0);

        Assert.Null(box.Transform);
    }

    /// <summary>A transform the cascade stops holding transitions back to <c>none</c>, visibly.</summary>
    /// <remarks>
    ///     ⚠ <b>The commonest transform transition there is</b> — a <c>:hover</c> rule, or a
    ///     <c>hover:rotate-*</c>, that puts a transform on an element with none at rest. Leaving
    ///     takes the property out of the computed style altogether, and the animator's overlay walked
    ///     only the properties the style has, so the run back to <c>none</c> went unseen and the card
    ///     snapped home.
    /// </remarks>
    [Fact]
    public void A_transform_the_cascade_drops_is_seen_on_its_way_back_to_none() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(document, "#box.turned { transform: rotate(90deg); }");

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 1.0);
        Frame(document, 1.5);

        Assert.Equal(100f, Tip(box).Y, Tolerance);

        box.RemoveClass("turned");
        Frame(document, 1.5);
        Frame(document, 2.0);

        var tip = Tip(box);

        Assert.Equal(70.7107f, tip.X, Tolerance);
        Assert.Equal(70.7107f, tip.Y, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A transition out of a diagonal reflection starts where the reflection is, rather than
    ///     half a turn away from it.</b>
    /// </summary>
    /// <remarks>
    ///     <c>matrix(0, 1, 1, 0, 0, 0)</c> against <c>rotate(45deg)</c> pairs nothing, so both ends are
    ///     decomposed — and the reflection decomposes to a negated scale and a half turn about
    ///     <c>(1, −1, 0)</c>, which the specification's quaternion extraction cannot recover the sign
    ///     of. The point <c>(100, 20)</c> sits at <c>(20, 100)</c> at rest and was drawn at
    ///     <c>(−20, −100)</c> on the transition's first frame.
    /// </remarks>
    [Fact]
    public void A_transition_out_of_a_reflection_starts_at_the_reflection() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(document, "#box { transform: matrix(0, 1, 1, 0, 0, 0); } #box.turned { transform: rotate(45deg); }");

        var rest = Assert.IsType<UiTransform>(box.Transform).Apply(new Vector2(100f, 20f));

        Assert.Equal(20f, rest.X, Tolerance);
        Assert.Equal(100f, rest.Y, Tolerance);

        box.AddClass("turned");
        Frame(document, 0.0);

        var first = Assert.IsType<UiTransform>(box.Transform).Apply(new Vector2(100f, 20f));

        Assert.Equal(20f, first.X, 0.05f);
        Assert.Equal(100f, first.Y, 0.05f);
    }

    /// <summary>
    ///     ⚠ <b><c>backface-visibility</c> is decided at every step, so a turning card disappears at
    ///     the crossing and not at either end.</b>
    /// </summary>
    /// <remarks>
    ///     <c>rotateY(0deg)</c> → <c>rotateY(180deg)</c> under <c>backface-visibility: hidden</c>. At
    ///     40% the card has turned 72° and still faces the viewer; at 60% it has turned 108° and shows
    ///     its back. The flag is asked of the composition every pass, so it needs no help from the
    ///     transition — which is exactly why the transition had to be a transform the reader re-reads,
    ///     rather than a matrix interpolated somewhere past it.
    /// </remarks>
    [Fact]
    public void A_card_turning_away_is_hidden_from_the_crossing_and_not_before() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            "#box { backface-visibility: hidden; transform: rotateY(0deg); } #box.turned { transform: rotateY(180deg); }"
        );

        box.AddClass("turned");
        Frame(document, 0.0);
        Frame(document, 0.4);

        Assert.False(box.BackfaceHidden);

        Frame(document, 0.6);

        Assert.True(box.BackfaceHidden);
    }

    /// <summary>
    ///     ⚠ <b>A <c>@keyframes</c> spin written with <c>transform</c> spins, where it used to wipe
    ///     the transform out.</b>
    /// </summary>
    /// <remarks>
    ///     A keyframe pair of transform lists was interpolated as two <c>StyleValue.Unknown</c>s,
    ///     whose CSS is the empty string — so the overlay wrote <c>transform: ""</c> and the element
    ///     lost even the transform its own rule gave it, for as long as the animation ran.
    /// </remarks>
    [Fact]
    public void A_keyframe_animation_of_transform_turns_the_box() {
        using var document = new UiDocument(400f, 300f);

        var box = Settled(
            document,
            """
            @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
            #box { animation-name: spin; animation-duration: 1s; animation-timing-function: linear;
                   animation-iteration-count: infinite; }
            """
        );

        Frame(document, 0.25);

        var tip = Tip(box);

        Assert.Equal(0f, tip.X, Tolerance);
        Assert.Equal(100f, tip.Y, Tolerance);
    }

    /// <summary>A mix written in a stylesheet is read the same way, nested and all.</summary>
    /// <remarks>
    ///     <c>transform-mix()</c> is CSS Values 5 and not an invention of the animator, so it has to
    ///     mean the same thing wherever it comes from. Half of the way from an eighth of a turn to a
    ///     quarter is three sixteenths.
    /// </remarks>
    [Fact]
    public void A_nested_mix_resolves_through_both_levels() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            "#box { transform: transform-mix(0.5, transform-mix(0.5, rotate(0deg), rotate(90deg)), rotate(90deg)); }"
        );

        var tip = Tip(box);
        var angle = MathF.PI * 3f / 8f;

        Assert.Equal(100f * MathF.Cos(angle), tip.X, Tolerance);
        Assert.Equal(100f * MathF.Sin(angle), tip.Y, Tolerance);
    }

    static int Written(UiDocument document, UiElement element) {
        Assert.True(element.Style.TryGet(document.Styles.Properties.Lookup("transform"), out var value));

        return value;
    }

    static int Occurrences(string text, string of) {
        var count = 0;

        for (var at = text.IndexOf(of, StringComparison.Ordinal); at >= 0; at = text.IndexOf(of, at + 1, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
