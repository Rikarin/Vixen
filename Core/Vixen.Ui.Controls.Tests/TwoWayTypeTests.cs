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

    sealed class Nullable : Component {
        readonly Signal<float?> value = new(Bound);

        protected override void Build(BuildContext ctx) {
            var slider = ctx.Child<Slider>(null);
            ctx.TwoWay(slider, "Value", () => value.Value, v => value.Value = v);
        }
    }
}
