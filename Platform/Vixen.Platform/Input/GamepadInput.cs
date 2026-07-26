// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>A button on a gamepad, named by position rather than by legend.</summary>
/// <remarks>
///     <para>
///         <see cref="South" />, <see cref="East" />, <see cref="West" /> and <see cref="North" />
///         are the four face buttons by where they sit in the diamond. Naming them <c>A</c>/<c>B</c>
///         or <c>Cross</c>/<c>Circle</c> would embed one vendor's legends in the engine and then
///         require a translation table for every other — including Nintendo, where <c>A</c> and
///         <c>B</c> are swapped relative to Xbox, which is exactly the confusion the positional
///         names remove. What the button is *labelled* is a presentation question, answered by
///         <see cref="GamepadKind" /> when a prompt needs drawing.
///     </para>
/// </remarks>
public enum GamepadButton : byte {
    /// <summary>No button.</summary>
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
    Touchpad = 16
}

/// <summary>An analogue axis on a gamepad.</summary>
/// <remarks>
///     Stick axes are reported in <c>[-1, 1]</c> with up and left negative, matching the engine's
///     screen convention (<c>docs/plan/03</c>). Triggers are reported in <c>[0, 1]</c>, because a
///     trigger has a rest position rather than a centre and mapping it onto a signed range makes
///     every consumer undo the mapping.
/// </remarks>
public enum GamepadAxis : byte {
    /// <summary>No axis.</summary>
    None = 0,

    /// <summary>Left stick, horizontal. Negative is left.</summary>
    LeftStickX = 1,

    /// <summary>Left stick, vertical. Negative is up.</summary>
    LeftStickY = 2,

    /// <summary>Right stick, horizontal. Negative is left.</summary>
    RightStickX = 3,

    /// <summary>Right stick, vertical. Negative is up.</summary>
    RightStickY = 4,

    /// <summary>Left trigger, <c>[0, 1]</c>.</summary>
    LeftTrigger = 5,

    /// <summary>Right trigger, <c>[0, 1]</c>.</summary>
    RightTrigger = 6
}

/// <summary>Which family a controller belongs to, so a prompt can draw the right glyph.</summary>
/// <remarks>
///     This is the <em>only</em> thing vendor identity is for. Bindings use
///     <see cref="GamepadButton" />, which is positional; nothing in the input path branches on
///     this value.
/// </remarks>
public enum GamepadKind : byte {
    /// <summary>Unrecognised, or a generic HID gamepad. Draw the positional names.</summary>
    Unknown = 0,

    /// <summary>An Xbox controller, or something reporting itself as one.</summary>
    Xbox = 1,

    /// <summary>A DualShock or DualSense.</summary>
    PlayStation = 2,

    /// <summary>A Switch Pro controller or Joy-Con pair.</summary>
    Nintendo = 3,

    /// <summary>A virtual or remapped pad, such as Steam Input's.</summary>
    Virtual = 4
}
