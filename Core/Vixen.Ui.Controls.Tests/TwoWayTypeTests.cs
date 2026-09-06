// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a <c>bind:</c> whose model is the wrong type does.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It used to do nothing whatsoever, and that is why this file exists.</b> Both legs of
///         a two-way binding go through <c>UiPropertyKey</c>, which boxes; the forward leg unboxes to
///         the property's own type, and an unbox is exact, so a <c>double</c> model against a
///         <c>float</c> property threw <c>InvalidCastException</c> on the first flush. But the
///         forward leg is an <c>Effect</c>, and <c>Effect.Run</c> catches, suspends and logs rather
///         than propagating — on purpose, so that one bad binding cannot take a window down. The
///         author saw a slider that never moved, a model that was never written, and no message.
///     </para>
///     <para>
///         Which is the recorded shape of "<c>bind:</c> is nominally present and practically absent":
///         every <c>bind:</c> attribute in the repository is in one file, and the first person to try
///         one against a model of a neighbouring numeric type got silence.
///     </para>
/// </remarks>
public class TwoWayTypeTests {
    /// <summary>The control the mismatch is written against, so the assertions can name a number.</summary>
    const float Bound = 0.5f;

    [Fact]
    public void A_binding_whose_types_agree_writes_the_control_from_the_model() {
        using var fixture = new ControlFixture();

        var panel = BuildContext.Build<Matched>(fixture.Document, fixture.Document.Root);
        fixture.Update();

        Assert.Equal(Bound, panel.Slider.Value);
    }

    /// <summary>
    ///     ⚠ <c>double</c> against <c>float</c>: the mismatch a model written before anybody looked at
    ///     the control makes, and the one the whole issue is about.
    /// </summary>
    [Fact]
    public void A_binding_whose_types_disagree_says_so_at_compose_rather_than_going_quiet() {
        using var fixture = new ControlFixture();

        var thrown = Assert.Throws<ArgumentException>(
            () => BuildContext.Build<Mismatched>(fixture.Document, fixture.Document.Root)
        );

        // Both types named, because "the types do not match" without them sends the author to read
        // the control's source to find out which one it wanted.
        Assert.Contains("Single", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Double", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Value", thrown.Message, StringComparison.Ordinal);

        // ⚠ And it names the answer, which is the half a refusal usually leaves out. #663 asks for a
        // converter seam; the seam exists decomposed, so the message spells the pair rather than
        // saying "convert either side explicitly" and leaving the author to work out where.
        Assert.Contains("change:Value", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The pair that message names, run: the same mismatched types bind fine when the
    ///     conversion is written where a reader can see it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what a `.vxml` emits for <c>Value="@expr"</c> beside
    ///         <c>change:Value="@(v =&gt; …)"</c></b> — an assignment plus a
    ///         <see cref="BuildContext.Bind(System.Action)" /> for the in-leg
    ///         (<c>ComponentEmitter.EmitParameter</c>) and a
    ///         <see cref="BuildContext.Changed{T}" /> for the out-leg — so the shape under test is
    ///         the generated one and not a hand-rolled approximation of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both legs, because either alone passes for the other's failure.</b> A model that
    ///         reaches the control proves nothing about the write-back, and this is the mismatch
    ///         `bind:` refuses — <c>double</c> control, <c>int</c> model — so if the pair were not a
    ///         real seam this is where it would show.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_pair_the_refusal_names_carries_the_conversion_both_ways() {
        using var fixture = new ControlFixture();

        var panel = BuildContext.Build<Converted>(fixture.Document, fixture.Document.Root);
        fixture.Update();

        // In: the int model reached the double property, and it follows the signal afterwards.
        Assert.Equal(3d, panel.Input.Number);

        panel.Count.Value = 7;
        fixture.Update();
        Assert.Equal(7d, panel.Input.Number);

        // Out: the control's double reached the int model, narrowed by the cast the panel wrote.
        panel.Input.Number = 11d;
        fixture.Update();
        Assert.Equal(11, panel.Count.Value);
    }

    /// <summary>
    ///     ⚠ And a nullable is a different type, not a lenient one: <c>float?</c> boxes as
    ///     <c>Nullable&lt;float&gt;</c> and unboxes to nothing else.
    /// </summary>
    [Fact]
    public void A_nullable_model_against_a_value_property_is_a_mismatch_too() {
        using var fixture = new ControlFixture();

        Assert.Throws<ArgumentException>(
            () => BuildContext.Build<Nullable>(fixture.Document, fixture.Document.Root)
        );
    }

    sealed class Matched : Component {
        readonly Signal<float> value = new(Bound);

        public Slider Slider { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Slider = ctx.Child<Slider>(null);
            ctx.TwoWay(Slider, "Value", () => value.Value, v => value.Value = v);
        }
    }

    sealed class Mismatched : Component {
        readonly Signal<double> value = new(Bound);

        protected override void Build(BuildContext ctx) {
            var slider = ctx.Child<Slider>(null);
            ctx.TwoWay(slider, "Value", () => value.Value, v => value.Value = v);
        }
    }

    /// <summary>The decomposed seam, in the shape <c>ComponentEmitter</c> writes it.</summary>
    sealed class Converted : Component {
        /// <summary>The model, counting in whole numbers the way a count does.</summary>
        public Signal<int> Count { get; } = new(3);

        /// <summary>The control, which deals in <c>double</c>.</summary>
        public NumericInput Input { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Input = ctx.Child<NumericInput>(null);

            // The in-leg: the statement makes the value the control is built with, and the effect
            // is what makes it follow. `int` widens to `double` in ordinary C#.
            Input.Number = Count.Value;
            ctx.Bind(() => Input.Number = Count.Value);

            // The out-leg, where the narrowing is written down.
            ctx.Changed(Input, "Number", () => Input.Number, n => Count.Value = (int)n);
        }
    }

    sealed class Nullable : Component {
        readonly Signal<float?> value = new(Bound);

        protected override void Build(BuildContext ctx) {
            var slider = ctx.Child<Slider>(null);
            ctx.TwoWay(slider, "Value", () => value.Value, v => value.Value = v);
        }
    }
}
