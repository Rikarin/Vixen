// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What this assembly contributes to the language a <c>.vxml</c> is written in.</summary>
/// <remarks>
///     Written against <c>BuildContext</c> directly rather than against markup, because that is the
///     contract: a <c>.vxml</c> compiles to exactly these calls, and this assembly cannot reference
///     the markup compiler that would produce them.
/// </remarks>
public class MarkupTests {
    /// <summary>A control's tag builds the control, with the tag its own type answers to.</summary>
    [Fact]
    public void A_control_tag_builds_the_control() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Bar>(fixture.Document, fixture.Document.Root);

        var progress = Assert.IsType<ProgressBar>(component.Root.Children[0]);
        Assert.Equal("progress-bar", progress.Tag);
    }

    /// <summary>
    ///     <c>on:click</c> on a control is its <i>activation</i>, which is why the control library
    ///     has to be able to say what the name means. A button is pressed by Space and by Enter as
    ///     well as by a pointer, and a binding that only heard the tap would work for everyone who
    ///     tests with a mouse and for nobody who does not use one.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_control_hears_the_keyboard_as_well_as_the_pointer() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Pressable>(fixture.Document, fixture.Document.Root);
        var button = (Button) component.Root.Children[0];

        fixture.Update();
        fixture.Click(button);
        Assert.Equal(1, component.Clicks);

        fixture.Document.Focus(button);
        fixture.Type(InputKey.Enter);
        Assert.Equal(2, component.Clicks);
    }

    /// <summary>
    ///     And exactly once for a tap. The control raises its <c>ClickEvent</c> from inside its own
    ///     tap handler, so a runtime that subscribed to both would count one press twice.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_control_counts_one_press_once() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Pressable>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Click((Button) component.Root.Children[0]);

        Assert.Equal(1, component.Clicks);
    }

    /// <summary>
    ///     An element that is not a control raises no <c>ClickEvent</c>, so <c>on:click</c> on one
    ///     stays the tap it always was. The decision is per element, not per name.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_plain_element_is_still_the_tap() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Plain>(fixture.Document, fixture.Document.Root);

        component.Root.Children[0].Raise(new TapEvent { Count = 1 });

        Assert.Equal(1, component.Clicks);
    }

    sealed class Bar : Component {
        protected override void Build(BuildContext ctx) => ctx.Child<ProgressBar>(null);
    }

    sealed class Pressable : Component {
        public int Clicks { get; private set; }

        protected override void Build(BuildContext ctx) {
            var button = ctx.Child<Button>(null);
            button.Label = "Press";

            ctx.On(BuildContext.Host(button), "click", () => Clicks++);
        }
    }

    sealed class Plain : Component {
        public int Clicks { get; private set; }

        protected override void Build(BuildContext ctx) =>
            ctx.On(ctx.Element(null, "div"), "click", () => Clicks++);
    }
}
