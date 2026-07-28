// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The keyboard interaction matrix doc 09 asks for, for everything that can be pressed.</summary>
public class ButtonTests {
    [Fact]
    public void A_type_names_its_own_tag() {
        using var fixture = new ControlFixture();

        Assert.Equal("button", fixture.Add<Button>().Tag);
        Assert.Equal("icon-button", fixture.Add<IconButton>().Tag);
        Assert.Equal("checkbox", fixture.Add<CheckBox>().Tag);
        Assert.Equal("switch", fixture.Add<Switch>().Tag);
    }

    [Fact]
    public void A_click_activates_it_once() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Save";
        fixture.Update();

        var clicks = 0;
        button.AddHandler<ClickEvent>((_, _) => clicks++);

        fixture.Click(button);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Enter_activates_on_the_press_and_space_on_the_release() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Save";
        fixture.Update();

        var devices = new List<ActivationDevice>();
        button.AddHandler<ClickEvent>((_, args) => devices.Add(args.Device));

        fixture.Document.Focus(button);

        fixture.KeyDown(InputKey.Enter);
        Assert.Single(devices);

        // ⚠ The whole point of the split. After the press, Space has done nothing but mark the
        // button active; the activation is on the release.
        fixture.KeyDown(InputKey.Space);
        Assert.Single(devices);
        Assert.True((button.State & ElementState.Active) != 0);

        fixture.KeyUp(InputKey.Space);
        Assert.Equal(2, devices.Count);
        Assert.All(devices, static device => Assert.Equal(ActivationDevice.Keyboard, device));
    }

    [Fact]
    public void Holding_space_presses_it_once() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        var clicks = 0;
        button.AddHandler<ClickEvent>((_, _) => clicks++);

        fixture.Document.Focus(button);

        fixture.KeyDown(InputKey.Space);
        fixture.KeyDown(InputKey.Space, repeat: true);
        fixture.KeyDown(InputKey.Space, repeat: true);
        fixture.KeyUp(InputKey.Space);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Losing_the_focus_mid_press_cancels_it() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        var other = fixture.Add<Button>();
        fixture.Update();

        var clicks = 0;
        button.AddHandler<ClickEvent>((_, _) => clicks++);

        fixture.Document.Focus(button);
        fixture.KeyDown(InputKey.Space);

        fixture.Document.Focus(other);
        fixture.Update();

        Assert.Equal(ElementState.None, button.State & ElementState.Active);

        // The release goes to whatever has the focus now, so the button never hears it. Without the
        // cancellation it would stay looking pressed for the rest of the document's life.
        fixture.KeyUp(InputKey.Space);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void A_disabled_button_refuses_every_route_to_activation() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Save";
        button.Disabled = true;
        fixture.Update();

        var clicks = 0;
        button.AddHandler<ClickEvent>((_, _) => clicks++);

        fixture.Click(button);
        fixture.Document.Focus(button);
        fixture.Type(InputKey.Enter);
        button.Activate();

        Assert.Equal(0, clicks);
        Assert.True((button.State & ElementState.Disabled) != 0);
        Assert.False(button.Focusable);
    }

    [Fact]
    public void A_disabled_control_is_not_a_tab_stop() {
        using var fixture = new ControlFixture();

        var first = fixture.Add<Button>();
        var second = fixture.Add<Button>();
        var third = fixture.Add<Button>();

        second.Disabled = true;
        fixture.Update();

        fixture.Document.Focus(first);
        fixture.Type(InputKey.Tab);

        Assert.Same(third, fixture.Document.Focused);
    }

    [Fact]
    public void Disabling_the_focused_control_moves_the_focus_off_it() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        fixture.Document.Focus(button);

        button.Disabled = true;

        Assert.Null(fixture.Document.Focused);
    }

    [Fact]
    public void A_toggle_flips_before_it_reports() {
        using var fixture = new ControlFixture();

        var toggle = fixture.Add<ToggleButton>();
        toggle.Label = "Bold";
        fixture.Update();

        bool? seen = null;
        toggle.AddHandler<ClickEvent>((element, _) => seen = ((ToggleButton) element).IsChecked);

        fixture.Click(toggle);

        Assert.True(seen);
        Assert.True(toggle.IsChecked);
        Assert.True((toggle.State & ElementState.Checked) != 0);
    }

    [Fact]
    public void A_checkbox_answers_to_space_and_not_to_enter() {
        using var fixture = new ControlFixture();

        var checkbox = fixture.Add<CheckBox>();
        checkbox.Label = "Wireframe";
        fixture.Update();

        fixture.Document.Focus(checkbox);

        fixture.Type(InputKey.Enter);
        Assert.False(checkbox.IsChecked);

        fixture.Type(InputKey.Space);
        Assert.True(checkbox.IsChecked);
    }

    [Fact]
    public void Clicking_an_indeterminate_checkbox_resolves_it() {
        using var fixture = new ControlFixture();

        var checkbox = fixture.Add<CheckBox>();
        checkbox.Label = "All";
        checkbox.IsIndeterminate = true;
        fixture.Update();

        Assert.True(checkbox.HasClass("indeterminate"));

        fixture.Click(checkbox);

        Assert.False(checkbox.IsIndeterminate);
        Assert.True(checkbox.IsChecked);
        Assert.False(checkbox.HasClass("indeterminate"));
    }

    [Fact]
    public void A_checkbox_tick_is_hidden_until_it_is_ticked() {
        using var fixture = new ControlFixture();

        var checkbox = fixture.Add<CheckBox>();
        checkbox.Label = "Shadows";
        fixture.Update();

        var mark = checkbox.Box.Children[0];
        Assert.Equal(0f, mark.Width);

        checkbox.IsChecked = true;
        fixture.Update();

        // The theme shows it with `display: flex`, which is the point of putting the state on the
        // element rather than swapping geometry in code.
        Assert.True(mark.Width > 0f);
    }

    [Fact]
    public void A_variant_and_a_size_are_classes_the_cascade_can_see() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();

        Assert.True(button.HasClass("variant-default"));
        Assert.True(button.HasClass("size-md"));

        button.Variant = ControlVariant.Danger;
        button.Size = ControlSize.Large;

        Assert.False(button.HasClass("variant-default"));
        Assert.True(button.HasClass("variant-danger"));
        Assert.True(button.HasClass("size-lg"));
        Assert.False(button.HasClass("size-md"));
    }

    [Fact]
    public void A_click_focuses_without_lighting_the_ring_and_a_tab_lights_it() {
        using var fixture = new ControlFixture();

        var first = fixture.Add<Button>();
        var second = fixture.Add<Button>();
        fixture.Update();

        fixture.Click(first);

        Assert.True(first.IsFocused);
        Assert.Equal(ElementState.None, first.State & ElementState.FocusVisible);

        fixture.Type(InputKey.Tab);

        Assert.True(second.IsFocused);
        Assert.True((second.State & ElementState.FocusVisible) != 0);

        // And the ring does not stay behind on the element the focus left.
        Assert.Equal(ElementState.None, first.State & ElementState.FocusVisible);
    }

    [Fact]
    public void A_leading_icon_goes_in_front_of_the_label() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Open";

        Assert.False(button.HasIcon);

        var icon = button.LeadingIcon;

        Assert.True(button.HasIcon);
        Assert.Same(icon, button.Children[0]);
    }

    [Fact]
    public void An_access_key_presses_it_and_says_the_keyboard_did() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = AccessKey.Parse("_Save", out var key);
        button.AccessKey = key;

        var devices = new List<ActivationDevice>();
        button.AddHandler<ClickEvent>((_, args) => devices.Add(args.Device));

        fixture.Update();
        fixture.KeyDown(InputKey.S, ModifierKeys.Alt);

        // ⚠ **A keyboard activation, not a code one.** It is one — somebody held Alt and pressed a
        // letter — and `Activate()` with no argument reports `Code`, which is the wrong answer for a
        // handler that logs how a command was reached or for a menu that closes on a keyboard press.
        Assert.Equal([ActivationDevice.Keyboard], devices);
        Assert.True(button.IsFocused);

        // And the marker came out of the drawn label rather than being left in it.
        Assert.Equal("Save", button.Label);
    }

    [Fact]
    public void A_disabled_button_ignores_its_access_key() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.AccessKey = 'S';
        button.Disabled = true;

        var clicks = 0;
        button.Clicked += _ => clicks++;

        fixture.Update();
        fixture.KeyDown(InputKey.S, ModifierKeys.Alt);

        // Two independent reasons this must not fire — the document skips `:disabled` elements when
        // it looks, and the button checks again when it hears — and both are worth having, because
        // an access key that worked on a greyed-out control would be the one way past being
        // disabled.
        Assert.Equal(0, clicks);
    }
}
