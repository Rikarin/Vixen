// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Input.Tests;

/// <summary>What an action reports, given what the devices say.</summary>
public class InputActionTests {
    readonly InputDeviceSet devices = new();
    readonly InputActions actions = Sample.Load();

    double now;

    [Fact]
    public void ReportsNothingBeforeAnythingIsPressed() {
        Frame();

        var fire = actions.FindAction("Player/Fire");
        Assert.Equal(InputActionPhase.Waiting, fire.Phase);
        Assert.False(fire.IsPressed);
        Assert.False(fire.WasPressedThisFrame);
    }

    [Fact]
    public void ReportsADisabledMapAsDisabled() {
        actions["Player"].Disable();
        Frame();

        Assert.Equal(InputActionPhase.Disabled, actions.FindAction("Player/Fire").Phase);
    }

    [Fact]
    public void PressesAndReleasesAButton() {
        var fire = actions.FindAction("Player/Fire");

        devices.SubmitMouseButton(MouseControl.Primary, true);
        Frame();

        Assert.True(fire.IsPressed);
        Assert.True(fire.WasPressedThisFrame);
        Assert.True(fire.WasPerformedThisFrame);
        Assert.Equal(InputActionPhase.Performed, fire.Phase);

        Frame();

        Assert.True(fire.IsPressed);
        Assert.False(fire.WasPressedThisFrame);
        Assert.False(fire.WasPerformedThisFrame);

        devices.SubmitMouseButton(MouseControl.Primary, false);
        Frame();

        Assert.False(fire.IsPressed);
        Assert.True(fire.WasReleasedThisFrame);
        Assert.True(fire.WasCanceledThisFrame);
        Assert.Equal(InputActionPhase.Waiting, fire.Phase);
    }

    [Fact]
    public void RaisesStartedPerformedAndCanceledInOrder() {
        var seen = new List<InputActionPhase>();
        var fire = actions.FindAction("Player/Fire");

        fire.Started += context => seen.Add(context.Phase);
        fire.Performed += context => seen.Add(context.Phase);
        fire.Canceled += context => seen.Add(context.Phase);

        devices.SubmitMouseButton(MouseControl.Primary, true);
        Frame();
        devices.SubmitMouseButton(MouseControl.Primary, false);
        Frame();

        Assert.Equal(
            [InputActionPhase.Started, InputActionPhase.Performed, InputActionPhase.Canceled],
            seen
        );
    }

    [Fact]
    public void ReadsAVector2CompositeWithUpNegative() {
        // The engine's screen convention is Y down, and the sticks report up as negative. A composite
        // that disagreed would send the player the other way depending on which device they used.
        var move = actions.FindAction("Player/Move");

        devices.SubmitKey(InputKey.W, true);
        Frame();

        Assert.Equal(new Vector2(0f, -1f), move.ReadValue<Vector2>());

        devices.SubmitKey(InputKey.W, false);
        devices.SubmitKey(InputKey.D, true);
        Frame();

        Assert.Equal(new Vector2(1f, 0f), move.ReadValue<Vector2>());
    }

    [Fact]
    public void CancelsOpposingPartsOfAComposite() {
        var move = actions.FindAction("Player/Move");

        devices.SubmitKey(InputKey.A, true);
        devices.SubmitKey(InputKey.D, true);
        Frame();

        Assert.Equal(Vector2.Zero, move.ReadValue<Vector2>());
    }

    [Fact]
    public void TakesTheMostActuatedBindingRatherThanTheirSum() {
        var move = actions.FindAction("Player/Move");
        var pad = devices.ConnectGamepad(1);

        devices.SubmitKey(InputKey.D, true);
        devices.SubmitGamepadAxis(pad.DeviceId, GamepadControl.LeftStickX, 0.5f);
        Frame();

        // WASD gives a magnitude of one and the half-pushed stick gives less, so the keyboard wins
        // outright — rather than the two adding up to something longer than either.
        Assert.Equal(1f, move.Magnitude, 3);
        Assert.Equal(new Vector2(1f, 0f), move.ReadValue<Vector2>());
    }

    [Fact]
    public void AppliesAStickDeadzone() {
        var move = actions.FindAction("Player/Move");
        var pad = devices.ConnectGamepad(1);

        devices.SubmitGamepadAxis(pad.DeviceId, GamepadControl.LeftStickX, 0.1f);
        Frame();

        Assert.Equal(Vector2.Zero, move.ReadValue<Vector2>());

        devices.SubmitGamepadAxis(pad.DeviceId, GamepadControl.LeftStickX, 1f);
        Frame();

        Assert.Equal(1f, move.ReadValue<Vector2>().X, 3);
    }

    [Fact]
    public void AppliesABindingsProcessors() {
        var look = actions.FindAction("Player/Look");

        devices.SubmitMouseMove(new(100f, 200f), new(20f, 40f));
        Frame();

        // scale(factor=0.05)
        Assert.Equal(new Vector2(1f, 2f), look.ReadValue<Vector2>());
    }

    [Fact]
    public void ClearsMotionDeltasBetweenFrames() {
        var look = actions.FindAction("Player/Look");

        devices.SubmitMouseMove(new(100f, 200f), new(20f, 40f));
        Frame();
        Frame();

        Assert.Equal(Vector2.Zero, look.ReadValue<Vector2>());
    }

    [Fact]
    public void AccumulatesSeveralMotionEventsWithinOneFrame() {
        var look = actions.FindAction("Player/Look");

        devices.SubmitMouseMove(new(10f, 0f), new(10f, 0f));
        devices.SubmitMouseMove(new(20f, 0f), new(10f, 0f));
        Frame();

        Assert.Equal(1f, look.ReadValue<Vector2>().X, 3);
    }

    [Fact]
    public void HoldsAnActionUntilItsDurationHasPassed() {
        var crouch = actions.FindAction("Player/Crouch");

        devices.SubmitKey(InputKey.LeftControl, true);
        Frame();

        Assert.Equal(InputActionPhase.Started, crouch.Phase);
        Assert.False(crouch.WasPerformedThisFrame);

        Frame(0.2);
        Assert.False(crouch.WasPerformedThisFrame);

        Frame(0.2);
        Assert.True(crouch.WasPerformedThisFrame);
        Assert.Equal(InputActionPhase.Performed, crouch.Phase);
    }

    [Fact]
    public void CancelsAHoldReleasedTooSoon() {
        var crouch = actions.FindAction("Player/Crouch");

        devices.SubmitKey(InputKey.LeftControl, true);
        Frame();
        devices.SubmitKey(InputKey.LeftControl, false);
        Frame(0.1);

        Assert.True(crouch.WasCanceledThisFrame);
        Assert.False(crouch.WasPerformedThisFrame);
    }

    [Fact]
    public void ReadsTheDPadAsAVector() {
        var navigate = actions.FindAction("Menu/Navigate");
        var pad = devices.ConnectGamepad(1);

        devices.SubmitGamepadButton(pad.DeviceId, GamepadControl.DPadUp, true);
        Frame();

        Assert.Equal(new Vector2(0f, -1f), navigate.ReadValue<Vector2>());
    }

    [Fact]
    public void ReadsAValueActionAsFloatAndBool() {
        var fire = actions.FindAction("Player/Fire");

        devices.SubmitMouseButton(MouseControl.Primary, true);
        Frame();

        Assert.Equal(1f, fire.ReadValue<float>());
        Assert.True(fire.ReadValue<bool>());
    }

    [Fact]
    public void RefusesToReadAsAShapeItDoesNotHave() {
        Assert.Throws<NotSupportedException>(() => actions.FindAction("Player/Fire").ReadValue<int>());
    }

    [Fact]
    public void ForgetsHeldControlsOnFocusLoss() {
        var fire = actions.FindAction("Player/Fire");

        devices.SubmitMouseButton(MouseControl.Primary, true);
        Frame();
        Assert.True(fire.IsPressed);

        devices.ResetHeldState();
        Frame();

        Assert.False(fire.IsPressed);
    }

    [Fact]
    public void OnlyReadsBindingsOfTheActiveControlScheme() {
        var fire = actions.FindAction("Player/Fire");
        Assert.True(actions.SetControlScheme("Gamepad"));

        devices.SubmitMouseButton(MouseControl.Primary, true);
        Frame();

        Assert.False(fire.IsPressed);

        var pad = devices.ConnectGamepad(1);
        devices.SubmitGamepadAxis(pad.DeviceId, GamepadControl.RightTrigger, 1f);
        Frame();

        Assert.True(fire.IsPressed);
    }

    [Fact]
    public void ReadsEveryBindingWhenNoSchemeIsActive() {
        var fire = actions.FindAction("Player/Fire");

        devices.SubmitMouseButton(MouseControl.Primary, true);
        Frame();

        Assert.Null(actions.ActiveControlScheme);
        Assert.True(fire.IsPressed);
    }

    [Fact]
    public void FollowsThePlayerOntoTheDeviceTheyPickedUp() {
        actions.AutoSwitchControlSchemes = true;
        devices.ConnectGamepad(1);

        devices.SubmitKey(InputKey.W, true);
        Frame();
        Assert.Equal("Keyboard&Mouse", actions.ActiveControlScheme?.Name);

        devices.SubmitGamepadButton(1, GamepadControl.South, true);
        Frame();
        Assert.Equal("Gamepad", actions.ActiveControlScheme?.Name);
    }

    [Fact]
    public void DoesNotSwitchSchemesOnAStickThatIsMerelyDrifting() {
        actions.AutoSwitchControlSchemes = true;
        var pad = devices.ConnectGamepad(1);

        devices.SubmitKey(InputKey.W, true);
        Frame();

        devices.SubmitGamepadAxis(pad.DeviceId, GamepadControl.LeftStickX, 0.05f);
        Frame();

        Assert.Equal("Keyboard&Mouse", actions.ActiveControlScheme?.Name);
    }

    [Fact]
    public void RefusesASchemeThatDoesNotExist() {
        Assert.False(actions.SetControlScheme("Steering Wheel"));
    }

    [Fact]
    public void ReadsTheGamepadSlotTheAssetIsPairedTo() {
        var second = InputActions.Load(Sample.Text, "SampleInput");
        second.Enable();
        second.GamepadSlot = 1;

        devices.ConnectGamepad(10);
        devices.ConnectGamepad(11);

        devices.SubmitGamepadAxis(11, GamepadControl.RightTrigger, 1f);
        Frame();
        second.Update(devices, now);

        Assert.False(actions.FindAction("Player/Fire").IsPressed);
        Assert.True(second.FindAction("Player/Fire").IsPressed);
    }

    [Fact]
    public void StopsReadingAGamepadThatWasUnplugged() {
        var fire = actions.FindAction("Player/Fire");
        var pad = devices.ConnectGamepad(1);

        devices.SubmitGamepadAxis(pad.DeviceId, GamepadControl.RightTrigger, 1f);
        Frame();
        Assert.True(fire.IsPressed);

        devices.DisconnectGamepad(pad.DeviceId);
        Frame();

        Assert.False(fire.IsPressed);
    }

    [Fact]
    public void KeepsASlotWhileAPadIsMerelyDisconnected() {
        devices.ConnectGamepad(1);
        devices.ConnectGamepad(2);
        devices.DisconnectGamepad(1);

        Assert.Equal(2, devices.Gamepads.Count);
        Assert.Equal(2, devices.Gamepads[1].DeviceId);

        devices.RemoveDisconnected();

        Assert.Single(devices.Gamepads);
        Assert.Equal(2, devices.Gamepads[0].DeviceId);
    }

    /// <summary>Runs one frame: begin, then read.</summary>
    /// <param name="elapsed">How much time passed since the last one.</param>
    void Frame(double elapsed = 1.0 / 60.0) {
        now += elapsed;
        actions.Update(devices, now);
        devices.BeginFrame();
    }
}
