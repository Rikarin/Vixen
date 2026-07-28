// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Input.Tests;

/// <summary>Control paths: the text form a <c>.vxinput</c> and a saved rebinding are written in.</summary>
public class InputControlPathTests {
    [Theory]
    [InlineData("<Keyboard>/w", InputDeviceKind.Keyboard, (ushort)InputKey.W)]
    [InlineData("<Keyboard>/leftShift", InputDeviceKind.Keyboard, (ushort)InputKey.LeftShift)]
    [InlineData("<Keyboard>/1", InputDeviceKind.Keyboard, (ushort)InputKey.Number1)]
    [InlineData("<Keyboard>/0", InputDeviceKind.Keyboard, (ushort)InputKey.Number0)]
    [InlineData("<Mouse>/primary", InputDeviceKind.Mouse, (ushort)MouseControl.Primary)]
    [InlineData("<Mouse>/left", InputDeviceKind.Mouse, (ushort)MouseControl.Primary)]
    [InlineData("<Mouse>/delta/x", InputDeviceKind.Mouse, (ushort)MouseControl.DeltaX)]
    [InlineData("<Mouse>/scroll/y", InputDeviceKind.Mouse, (ushort)MouseControl.ScrollY)]
    [InlineData("<Gamepad>/south", InputDeviceKind.Gamepad, (ushort)GamepadControl.South)]
    [InlineData("<Gamepad>/buttonSouth", InputDeviceKind.Gamepad, (ushort)GamepadControl.South)]
    [InlineData("<Gamepad>/leftTrigger", InputDeviceKind.Gamepad, (ushort)GamepadControl.LeftTrigger)]
    [InlineData("<Gamepad>/leftStickPress", InputDeviceKind.Gamepad, (ushort)GamepadControl.LeftStick)]
    [InlineData("<Gamepad>/dpad/up", InputDeviceKind.Gamepad, (ushort)GamepadControl.DPadUp)]
    [InlineData("<Touch>/press", InputDeviceKind.Touch, (ushort)TouchControl.Press)]
    public void ParsesOneControl(string path, InputDeviceKind device, ushort code) {
        Assert.True(InputControlPath.TryParse(path, out var control, out var vertical));

        Assert.Equal(new InputControl(device, code), control);
        Assert.False(vertical.IsValid);
    }

    [Fact]
    public void IsCaseInsensitive() {
        Assert.True(InputControlPath.TryParse("<KEYBOARD>/LeftShift", out var control));

        Assert.Equal(InputControl.Key(InputKey.LeftShift), control);
    }

    [Theory]
    [InlineData("<Gamepad>/leftStick", (ushort)GamepadControl.LeftStickX, (ushort)GamepadControl.LeftStickY)]
    [InlineData("<Gamepad>/dpad", (ushort)GamepadControl.DPadX, (ushort)GamepadControl.DPadY)]
    public void ParsesATwoDimensionalGamepadControl(string path, ushort x, ushort y) {
        Assert.True(InputControlPath.TryParse(path, out var horizontal, out var vertical));

        Assert.Equal(new InputControl(InputDeviceKind.Gamepad, x), horizontal);
        Assert.Equal(new InputControl(InputDeviceKind.Gamepad, y), vertical);
    }

    [Fact]
    public void ParsesTheMousesDeltaAsAPair() {
        Assert.True(InputControlPath.TryParse("<Mouse>/delta", out var x, out var y));

        Assert.Equal(InputControl.Mouse(MouseControl.DeltaX), x);
        Assert.Equal(InputControl.Mouse(MouseControl.DeltaY), y);
    }

    [Fact]
    public void ReadsTheDeviceIndex() {
        Assert.True(InputControlPath.TryParse("<Gamepad>2/south", out var control));

        Assert.Equal(2, control.Index);
    }

    [Fact]
    public void ReadsTheFingerIndex() {
        Assert.True(InputControlPath.TryParse("<Touch>3/position/y", out var control));

        Assert.Equal(InputControl.Touch(TouchControl.PositionY, 3), control);
    }

    [Theory]
    [InlineData("")]
    [InlineData("w")]
    [InlineData("<Keyboard>")]
    [InlineData("<Keyboard>/")]
    [InlineData("<Keyboard>/notAKey")]
    [InlineData("<Nonsense>/w")]
    [InlineData("<Gamepad>/leftStick/z")]
    [InlineData("<Mouse>/primary/x")]
    public void RefusesAPathItCannotResolve(string path) {
        Assert.False(InputControlPath.TryParse(path, out _));
    }

    [Fact]
    public void RefusesTheNumericSpellingOfAControl() {
        // <Keyboard>/26 would be a legal spelling of `w` if the enum parser were left to itself, and
        // it would stop meaning the same thing the day a member is renumbered.
        Assert.False(InputControlPath.TryParse("<Keyboard>/26", out _));
    }

    [Theory]
    [InlineData("<Keyboard>/w")]
    [InlineData("<Keyboard>/leftShift")]
    [InlineData("<Keyboard>/f11")]
    [InlineData("<Mouse>/primary")]
    [InlineData("<Mouse>/delta/x")]
    [InlineData("<Mouse>/scroll/y")]
    [InlineData("<Mouse>/position/x")]
    [InlineData("<Gamepad>/south")]
    [InlineData("<Gamepad>/leftTrigger")]
    [InlineData("<Gamepad>/leftStick/x")]
    [InlineData("<Gamepad>/leftStick/y")]
    [InlineData("<Gamepad>/leftStickPress")]
    [InlineData("<Gamepad>/dpad/up")]
    [InlineData("<Gamepad>/dpad/x")]
    [InlineData("<Gamepad>2/rightShoulder")]
    [InlineData("<Touch>/press")]
    [InlineData("<Touch>1/delta/y")]
    public void FormatsBackToWhatItParsed(string path) {
        Assert.True(InputControlPath.TryParse(path, out var control));

        Assert.Equal(path, InputControlPath.Format(control));
    }

    [Fact]
    public void FormatsAPairAsTheOnePathThatNamesBoth() {
        Assert.True(InputControlPath.TryParse("<Gamepad>/leftStick", out var x, out var y));

        Assert.Equal("<Gamepad>/leftStick", InputControlPath.Format(x, y));
    }

    [Fact]
    public void FormatsAPairWhoseHalvesAreUnrelatedAsItsFirstHalf() {
        var x = InputControl.Key(InputKey.A);
        var y = InputControl.Mouse(MouseControl.DeltaY);

        Assert.Equal("<Keyboard>/a", InputControlPath.Format(x, y));
    }

    [Fact]
    public void FormatsTheAbsenceOfAControl() {
        Assert.Equal("<None>", InputControlPath.Format(InputControl.None));
    }

    [Theory]
    [InlineData("<Keyboard>/w", "W")]
    [InlineData("<Gamepad>/leftTrigger", "Left Trigger")]
    [InlineData("<Mouse>/primary", "Primary")]
    public void DescribesAControlForAPlayer(string path, string expected) {
        Assert.True(InputControlPath.TryParse(path, out var control));

        Assert.Equal(expected, InputControlPath.Describe(control));
    }

    [Fact]
    public void KnowsWhichControlsAreAnalogue() {
        Assert.True(InputControl.Gamepad(GamepadControl.LeftStickX).IsAnalogue);
        Assert.True(InputControl.Mouse(MouseControl.DeltaY).IsAnalogue);
        Assert.False(InputControl.Gamepad(GamepadControl.South).IsAnalogue);
        Assert.False(InputControl.Key(InputKey.W).IsAnalogue);
    }

    /// <summary>
    ///     Every two-dimensional control is <c>X</c> then <c>Y</c> at consecutive values.
    /// </summary>
    /// <remarks>
    ///     Both halves of the path code depend on it: parsing a stem adds one to reach the vertical
    ///     half, and formatting a pair recognises one by the same arithmetic. Inserting a member
    ///     between an X and its Y would leave both compiling and neither correct.
    /// </remarks>
    [Fact]
    public void KeepsEveryAxisPairConsecutive() {
        Assert.Equal((ushort)MouseControl.DeltaX + 1, (ushort)MouseControl.DeltaY);
        Assert.Equal((ushort)MouseControl.ScrollX + 1, (ushort)MouseControl.ScrollY);
        Assert.Equal((ushort)MouseControl.PositionX + 1, (ushort)MouseControl.PositionY);
        Assert.Equal((ushort)TouchControl.DeltaX + 1, (ushort)TouchControl.DeltaY);
        Assert.Equal((ushort)TouchControl.PositionX + 1, (ushort)TouchControl.PositionY);
        Assert.Equal((ushort)GamepadControl.LeftStickX + 1, (ushort)GamepadControl.LeftStickY);
        Assert.Equal((ushort)GamepadControl.RightStickX + 1, (ushort)GamepadControl.RightStickY);
        Assert.Equal((ushort)GamepadControl.DPadX + 1, (ushort)GamepadControl.DPadY);
    }
}
