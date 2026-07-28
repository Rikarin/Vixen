// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Platform;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     The seam between <c>Vixen.Platform</c>'s event stream and <c>Vixen.Input</c>'s device set.
/// </summary>
/// <remarks>
///     The translation is small and mechanical, which is exactly why it is worth a test: a mis-mapped
///     axis or a payload read off the wrong event kind is invisible until someone plays the game with
///     a controller.
/// </remarks>
public class PlatformInputTests {
    readonly InputDeviceSet devices = new();

    [Fact]
    public void TranslatesAKeyPressAndRelease() {
        devices.Submit(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, 0, Key.W, KeyModifiers.None));
        Assert.True(devices.Keyboard.IsDown(InputKey.W));

        devices.Submit(PlatformEvent.Keyboard(PlatformEventKind.KeyUp, 1, 0, Key.W, KeyModifiers.None));
        Assert.False(devices.Keyboard.IsDown(InputKey.W));
    }

    [Fact]
    public void TranslatesEveryKeyThePlatformCanReport() {
        // The cast is only the identity because the two tables agree, which Vixen.Input.Tests holds
        // member by member. This is the same claim from the other side: every key the platform can
        // send arrives as a key the device set will report.
        foreach (var key in Enum.GetValues<Key>()) {
            if (key == Key.Unknown) {
                continue;
            }

            devices.Submit(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, 0, key, KeyModifiers.None));
            Assert.True(devices.Keyboard.IsDown((InputKey)key), $"{key} did not arrive");
            devices.Submit(PlatformEvent.Keyboard(PlatformEventKind.KeyUp, 1, 0, key, KeyModifiers.None));
        }
    }

    [Fact]
    public void TranslatesPointerMotionAndTheWheel() {
        devices.Submit(PlatformEvent.MouseMoved(1, 0, new(40f, 60f), new(4f, 6f)));
        devices.Submit(PlatformEvent.MouseWheel(1, 0, new(40f, 60f), new(0f, 2f)));

        Assert.Equal(new Vector2(40f, 60f), devices.Mouse.Position);
        Assert.Equal(new Vector2(4f, 6f), devices.Mouse.Delta);
        Assert.Equal(new Vector2(0f, 2f), devices.Mouse.Scroll);
    }

    [Fact]
    public void TranslatesAMouseButton() {
        devices.Submit(
            PlatformEvent.MouseButtonChanged(PlatformEventKind.MouseButtonDown, 1, 0, MouseButton.Secondary, default)
        );

        Assert.True(devices.Mouse.IsDown(MouseControl.Secondary));
    }

    [Fact]
    public void TranslatesATouchSequence() {
        devices.Submit(PlatformEvent.Touch(PlatformEventKind.TouchDown, 1, 0, 0, new(10f, 20f)));
        Assert.True(devices.Touch.TryGetPosition(0, out var down));
        Assert.Equal(new Vector2(10f, 20f), down!.Value);

        devices.Submit(PlatformEvent.Touch(PlatformEventKind.TouchMoved, 1, 0, 0, new(12f, 24f), new(2f, 4f)));
        Assert.Equal(4f, devices.Touch.ReadValue(InputControl.Touch(TouchControl.DeltaY)));

        devices.Submit(PlatformEvent.Touch(PlatformEventKind.TouchUp, 1, 0, 0, new(12f, 24f)));
        Assert.Equal(0, devices.Touch.ActiveTouches);
    }

    [Fact]
    public void TranslatesAGamepadArrivingAndLeaving() {
        devices.Submit(PlatformEvent.GamepadConnection(PlatformEventKind.GamepadConnected, 0, 7));
        Assert.True(devices.TryGetGamepad(7, out var pad));
        Assert.True(pad!.IsConnected);

        devices.Submit(PlatformEvent.GamepadConnection(PlatformEventKind.GamepadDisconnected, 0, 7));
        Assert.False(pad.IsConnected);
    }

    [Fact]
    public void TranslatesAGamepadButton() {
        devices.Submit(PlatformEvent.GamepadConnection(PlatformEventKind.GamepadConnected, 0, 7));

        devices.Submit(
            PlatformEvent.GamepadButtonChanged(PlatformEventKind.GamepadButtonDown, 0, 7, GamepadButton.South)
        );

        Assert.True(devices.Gamepads[0].IsDown(GamepadControl.South));
    }

    [Theory]
    [InlineData(GamepadAxis.LeftStickX, GamepadControl.LeftStickX)]
    [InlineData(GamepadAxis.LeftStickY, GamepadControl.LeftStickY)]
    [InlineData(GamepadAxis.RightStickX, GamepadControl.RightStickX)]
    [InlineData(GamepadAxis.RightStickY, GamepadControl.RightStickY)]
    [InlineData(GamepadAxis.LeftTrigger, GamepadControl.LeftTrigger)]
    [InlineData(GamepadAxis.RightTrigger, GamepadControl.RightTrigger)]
    public void TranslatesEveryGamepadAxis(GamepadAxis axis, GamepadControl expected) {
        devices.Submit(PlatformEvent.GamepadConnection(PlatformEventKind.GamepadConnected, 0, 7));
        devices.Submit(PlatformEvent.GamepadAxisMoved(0, 7, axis, 0.75f));

        Assert.Equal(0.75f, devices.Gamepads[0].GetAxis(expected));
    }

    [Fact]
    public void ReleasesEverythingHeldWhenTheWindowLosesFocus() {
        // The platform sends no key-up for a key held when focus is lost, so without this the player
        // comes back to a game that believes W is still down.
        devices.Submit(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, 0, Key.W, KeyModifiers.None));
        devices.Submit(PlatformEvent.Window(PlatformEventKind.WindowFocusLost, 1, 0));

        Assert.False(devices.Keyboard.IsDown(InputKey.W));
    }

    [Fact]
    public void LeavesEventsThatAreNotInputAlone() {
        Assert.False(devices.Submit(PlatformEvent.Window(PlatformEventKind.WindowShown, 1, 0)));
        Assert.False(devices.Submit(PlatformEvent.WindowResized(1, 0, new(800, 600), new(1600, 1200))));
    }
}
