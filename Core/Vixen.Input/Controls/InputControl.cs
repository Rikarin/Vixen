// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>A control on a mouse.</summary>
/// <remarks>
///     Buttons are named by role rather than by side — <see cref="Primary" /> is the button under the
///     index finger, which the OS has already swapped for a left-handed pointer configuration by the
///     time the event reaches us. The motion controls are <em>deltas per frame</em>, not positions:
///     a camera bound to <see cref="DeltaX" /> must keep working in relative cursor mode, where the
///     pointer has no position at all.
/// </remarks>
public enum MouseControl : ushort {
    /// <summary>No control.</summary>
    None = 0,

    /// <summary>The main button: select, click, drag.</summary>
    Primary = 1,

    /// <summary>The context-menu button.</summary>
    Secondary = 2,

    /// <summary>The wheel pressed in.</summary>
    Middle = 3,

    /// <summary>The first thumb button, conventionally "back".</summary>
    Extra1 = 4,

    /// <summary>The second thumb button, conventionally "forward".</summary>
    Extra2 = 5,

    /// <summary>Horizontal motion this frame, in logical points. Positive is right.</summary>
    DeltaX = 16,

    /// <summary>Vertical motion this frame, in logical points. Positive is down.</summary>
    DeltaY = 17,

    /// <summary>Horizontal scroll this frame, in wheel notches.</summary>
    ScrollX = 18,

    /// <summary>Vertical scroll this frame, in wheel notches. Positive is up.</summary>
    ScrollY = 19,

    /// <summary>The pointer's horizontal position within the window, in logical points.</summary>
    PositionX = 20,

    /// <summary>The pointer's vertical position within the window, in logical points.</summary>
    PositionY = 21
}

/// <summary>A control on a gamepad.</summary>
/// <remarks>
///     Buttons keep the positional names <c>Vixen.Platform.GamepadButton</c> uses and the same
///     values, so the bridge casts. Axes are numbered above them in one enum rather than split into
///     two, because a binding path names one control and the parser should not have to know which
///     half to look in before it can tell whether <c>leftTrigger</c> exists.
/// </remarks>
public enum GamepadControl : ushort {
    /// <summary>No control.</summary>
    None = 0,

    /// <summary>Bottom face button: <c>A</c> on Xbox, <c>✕</c> on PlayStation, <c>B</c> on Nintendo.</summary>
    South = 1,

    /// <summary>Right face button: <c>B</c> on Xbox, <c>○</c> on PlayStation, <c>A</c> on Nintendo.</summary>
    East = 2,

    /// <summary>Left face button: <c>X</c> on Xbox, <c>□</c> on PlayStation, <c>Y</c> on Nintendo.</summary>
    West = 3,

    /// <summary>Top face button: <c>Y</c> on Xbox, <c>△</c> on PlayStation, <c>X</c> on Nintendo.</summary>
    North = 4,

    /// <summary>The left-hand small button: <c>View</c>, <c>Share</c>, <c>-</c>.</summary>
    Back = 5,

    /// <summary>The vendor button in the middle: <c>Guide</c>, <c>PS</c>, <c>Home</c>.</summary>
    Guide = 6,

    /// <summary>The right-hand small button: <c>Menu</c>, <c>Options</c>, <c>+</c>.</summary>
    Start = 7,

    /// <summary>The left stick pressed in.</summary>
    LeftStick = 8,

    /// <summary>The right stick pressed in.</summary>
    RightStick = 9,

    /// <summary>The upper left shoulder button.</summary>
    LeftShoulder = 10,

    /// <summary>The upper right shoulder button.</summary>
    RightShoulder = 11,

    /// <summary>Up on the directional pad.</summary>
    DPadUp = 12,

    /// <summary>Down on the directional pad.</summary>
    DPadDown = 13,

    /// <summary>Left on the directional pad.</summary>
    DPadLeft = 14,

    /// <summary>Right on the directional pad.</summary>
    DPadRight = 15,

    /// <summary>The touchpad pressed in, on controllers that have one.</summary>
    Touchpad = 16,

    /// <summary>Left stick, horizontal, <c>[-1, 1]</c>. Negative is left.</summary>
    LeftStickX = 32,

    /// <summary>Left stick, vertical, <c>[-1, 1]</c>. Negative is up.</summary>
    LeftStickY = 33,

    /// <summary>Right stick, horizontal, <c>[-1, 1]</c>. Negative is left.</summary>
    RightStickX = 34,

    /// <summary>Right stick, vertical, <c>[-1, 1]</c>. Negative is up.</summary>
    RightStickY = 35,

    /// <summary>Left trigger, <c>[0, 1]</c>.</summary>
    LeftTrigger = 36,

    /// <summary>Right trigger, <c>[0, 1]</c>.</summary>
    RightTrigger = 37,

    /// <summary>The directional pad read as a horizontal axis, <c>-1</c>, <c>0</c> or <c>1</c>.</summary>
    /// <remarks>
    ///     Synthesised from <see cref="DPadLeft" /> and <see cref="DPadRight" /> by the device rather
    ///     than composed in the binding, so that <c>&lt;Gamepad&gt;/dpad</c> is one binding a player
    ///     can rebind as a unit — the same reason a stick is one control and not two.
    /// </remarks>
    DPadX = 38,

    /// <summary>The directional pad read as a vertical axis. Negative is up, matching the sticks.</summary>
    DPadY = 39
}

/// <summary>A control on a touchscreen.</summary>
/// <remarks>
///     Every one of these is about <em>one</em> finger, and which finger is
///     <see cref="InputControl.Index" />. A binding that does not say means finger zero, which is
///     what a single-touch control — a fire button, a virtual stick — wants.
/// </remarks>
public enum TouchControl : ushort {
    /// <summary>No control.</summary>
    None = 0,

    /// <summary>Whether the finger is down. Reads as its pressure where the screen reports one.</summary>
    Press = 1,

    /// <summary>Horizontal motion this frame, in logical points.</summary>
    DeltaX = 16,

    /// <summary>Vertical motion this frame, in logical points.</summary>
    DeltaY = 17,

    /// <summary>The finger's horizontal position within the window, in logical points.</summary>
    PositionX = 18,

    /// <summary>The finger's vertical position within the window, in logical points.</summary>
    PositionY = 19
}

/// <summary>One control on one family of device: the thing a binding points at.</summary>
/// <param name="Device">Which family of device.</param>
/// <param name="Code">
///     Which control, as the device family's own enum — <see cref="InputKey" />,
///     <see cref="MouseControl" />, <see cref="GamepadControl" />, <see cref="TouchControl" />.
/// </param>
/// <param name="Index">
///     Which instance: the finger for a touch control, and otherwise the slot the control's device
///     is paired to. <c>0</c> is "whichever device the reading player has".
/// </param>
/// <remarks>
///     <para>
///         <b>A control names a device family, not a device.</b> Which physical gamepad a binding
///         reads is settled by the reading asset's <see cref="InputActions.GamepadSlot" />, not
///         written into the asset — otherwise a two-player game's second <c>.vxinput</c> would differ from the first
///         only in a device id, and unplugging a pad would invalidate the file rather than the
///         pairing.
///     </para>
///     <para>
///         Six bytes, no allocation, and comparable — which is what makes conflict detection a set
///         operation rather than a string comparison over binding paths.
///     </para>
/// </remarks>
public readonly record struct InputControl(InputDeviceKind Device, ushort Code, byte Index = 0) {
    /// <summary>No control. What an unresolved binding reads as.</summary>
    public static InputControl None => default;

    /// <summary>Whether this names a control at all.</summary>
    public bool IsValid => Device != InputDeviceKind.None && Code != 0;

    /// <summary>
    ///     Whether the control is analogue — a stick, a trigger, a motion delta, a position.
    /// </summary>
    /// <remarks>
    ///     What decides whether a dead zone means anything, whether a press point applies, and
    ///     whether a rebinding operation should wait for the value to settle before accepting it.
    /// </remarks>
    public bool IsAnalogue => Device switch {
        InputDeviceKind.Mouse => Code >= (ushort)MouseControl.DeltaX,
        InputDeviceKind.Gamepad => Code >= (ushort)GamepadControl.LeftStickX,
        InputDeviceKind.Touch => Code >= (ushort)TouchControl.DeltaX,
        _ => false
    };

    /// <summary>A keyboard key.</summary>
    /// <param name="key">Which key.</param>
    public static InputControl Key(InputKey key) => new(InputDeviceKind.Keyboard, (ushort)key);

    /// <summary>A mouse control.</summary>
    /// <param name="control">Which control.</param>
    public static InputControl Mouse(MouseControl control) => new(InputDeviceKind.Mouse, (ushort)control);

    /// <summary>A gamepad control.</summary>
    /// <param name="control">Which control.</param>
    /// <param name="index">Which pad slot, or <c>0</c> for the reading player's own.</param>
    public static InputControl Gamepad(GamepadControl control, byte index = 0) =>
        new(InputDeviceKind.Gamepad, (ushort)control, index);

    /// <summary>A touch control.</summary>
    /// <param name="control">Which control.</param>
    /// <param name="finger">Which finger, zero-based.</param>
    public static InputControl Touch(TouchControl control, byte finger = 0) =>
        new(InputDeviceKind.Touch, (ushort)control, finger);

    /// <inheritdoc />
    public override string ToString() => InputControlPath.Format(this);
}
