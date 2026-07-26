// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.SDL;

namespace Vixen.Platform.Desktop;

/// <summary>SDL's vocabulary, translated into the engine's.</summary>
/// <remarks>
///     Mostly a set of switches, with one pleasant exception: SDL's scancodes <em>are</em> USB HID
///     usage codes for the whole main range, and <see cref="Key" /> is defined on the same table, so
///     the keyboard translation is a cast plus two special cases rather than a hundred-line map.
///     That was the point of choosing HID codes for <see cref="Key" /> rather than inventing a
///     numbering.
/// </remarks>
static class SdlTranslation {
    /// <summary>The last HID keyboard usage SDL and <see cref="Key" /> agree on.</summary>
    /// <remarks>
    ///     <c>SDL_SCANCODE_RGUI</c> = 231 = HID right GUI. Above that SDL continues with its own
    ///     numbering for keys the HID keyboard page does not have, so the identity stops being
    ///     valid and each one that matters is named below.
    /// </remarks>
    const int LastHidScancode = 231;

    const int ScancodeAcBack = 270;

    public static Key ToKey(Scancode scancode) {
        var value = (int)scancode;

        if (value is >= 4 and <= LastHidScancode) {
            return (Key)value;
        }

        return value == ScancodeAcBack ? Key.Back : Key.Unknown;
    }

    public static Scancode ToScancode(Key key) =>
        key == Key.Back ? (Scancode)ScancodeAcBack : (Scancode)(int)key;

    public static KeyModifiers ToModifiers(Keymod modifiers) {
        var value = (uint)modifiers;
        var result = KeyModifiers.None;

        if ((value & (uint)Keymod.Lshift) != 0) {
            result |= KeyModifiers.LeftShift;
        }

        if ((value & (uint)Keymod.Rshift) != 0) {
            result |= KeyModifiers.RightShift;
        }

        if ((value & (uint)Keymod.Lctrl) != 0) {
            result |= KeyModifiers.LeftControl;
        }

        if ((value & (uint)Keymod.Rctrl) != 0) {
            result |= KeyModifiers.RightControl;
        }

        if ((value & (uint)Keymod.Lalt) != 0) {
            result |= KeyModifiers.LeftAlt;
        }

        if ((value & (uint)Keymod.Ralt) != 0) {
            result |= KeyModifiers.RightAlt;
        }

        if ((value & (uint)Keymod.Lgui) != 0) {
            result |= KeyModifiers.LeftMeta;
        }

        if ((value & (uint)Keymod.Rgui) != 0) {
            result |= KeyModifiers.RightMeta;
        }

        if ((value & (uint)Keymod.Caps) != 0) {
            result |= KeyModifiers.CapsLock;
        }

        if ((value & (uint)Keymod.Num) != 0) {
            result |= KeyModifiers.NumLock;
        }

        return result;
    }

    /// <summary>
    ///     SDL's button numbering, which is already role-based: the OS has applied the user's
    ///     left-handed swap before SDL sees it.
    /// </summary>
    public static MouseButton ToMouseButton(byte button) => button switch {
        1 => MouseButton.Primary,
        2 => MouseButton.Middle,
        3 => MouseButton.Secondary,
        4 => MouseButton.Extra1,
        5 => MouseButton.Extra2,
        _ => MouseButton.None
    };

    public static GamepadButton ToGamepadButton(GameControllerButton button) => button switch {
        GameControllerButton.A => GamepadButton.South,
        GameControllerButton.B => GamepadButton.East,
        GameControllerButton.X => GamepadButton.West,
        GameControllerButton.Y => GamepadButton.North,
        GameControllerButton.Back => GamepadButton.Back,
        GameControllerButton.Guide => GamepadButton.Guide,
        GameControllerButton.Start => GamepadButton.Start,
        GameControllerButton.Leftstick => GamepadButton.LeftStick,
        GameControllerButton.Rightstick => GamepadButton.RightStick,
        GameControllerButton.Leftshoulder => GamepadButton.LeftShoulder,
        GameControllerButton.Rightshoulder => GamepadButton.RightShoulder,
        GameControllerButton.DpadUp => GamepadButton.DPadUp,
        GameControllerButton.DpadDown => GamepadButton.DPadDown,
        GameControllerButton.DpadLeft => GamepadButton.DPadLeft,
        GameControllerButton.DpadRight => GamepadButton.DPadRight,
        GameControllerButton.Touchpad => GamepadButton.Touchpad,
        _ => GamepadButton.None
    };

    public static GameControllerButton ToSdl(GamepadButton button) => button switch {
        GamepadButton.South => GameControllerButton.A,
        GamepadButton.East => GameControllerButton.B,
        GamepadButton.West => GameControllerButton.X,
        GamepadButton.North => GameControllerButton.Y,
        GamepadButton.Back => GameControllerButton.Back,
        GamepadButton.Guide => GameControllerButton.Guide,
        GamepadButton.Start => GameControllerButton.Start,
        GamepadButton.LeftStick => GameControllerButton.Leftstick,
        GamepadButton.RightStick => GameControllerButton.Rightstick,
        GamepadButton.LeftShoulder => GameControllerButton.Leftshoulder,
        GamepadButton.RightShoulder => GameControllerButton.Rightshoulder,
        GamepadButton.DPadUp => GameControllerButton.DpadUp,
        GamepadButton.DPadDown => GameControllerButton.DpadDown,
        GamepadButton.DPadLeft => GameControllerButton.DpadLeft,
        GamepadButton.DPadRight => GameControllerButton.DpadRight,
        GamepadButton.Touchpad => GameControllerButton.Touchpad,
        _ => GameControllerButton.Invalid
    };

    public static GamepadAxis ToGamepadAxis(GameControllerAxis axis) => axis switch {
        GameControllerAxis.Leftx => GamepadAxis.LeftStickX,
        GameControllerAxis.Lefty => GamepadAxis.LeftStickY,
        GameControllerAxis.Rightx => GamepadAxis.RightStickX,
        GameControllerAxis.Righty => GamepadAxis.RightStickY,
        GameControllerAxis.Triggerleft => GamepadAxis.LeftTrigger,
        GameControllerAxis.Triggerright => GamepadAxis.RightTrigger,
        _ => GamepadAxis.None
    };

    public static GameControllerAxis ToSdl(GamepadAxis axis) => axis switch {
        GamepadAxis.LeftStickX => GameControllerAxis.Leftx,
        GamepadAxis.LeftStickY => GameControllerAxis.Lefty,
        GamepadAxis.RightStickX => GameControllerAxis.Rightx,
        GamepadAxis.RightStickY => GameControllerAxis.Righty,
        GamepadAxis.LeftTrigger => GameControllerAxis.Triggerleft,
        GamepadAxis.RightTrigger => GameControllerAxis.Triggerright,
        _ => GameControllerAxis.Invalid
    };

    /// <summary>
    ///     A stick's raw <c>[-32768, 32767]</c> to <c>[-1, 1]</c>, or a trigger's <c>[0, 32767]</c>
    ///     to <c>[0, 1]</c>.
    /// </summary>
    /// <remarks>
    ///     Sticks divide by 32767 rather than 32768 and then clamp, so a stick pushed fully left
    ///     reads exactly <c>-1</c> instead of <c>-1.000031</c>. An asymmetric range is not something
    ///     a caller should have to know about, and the alternative — dividing by 32768 — makes full
    ///     deflection read <c>0.99997</c>, which never quite reaches a threshold set at 1.
    /// </remarks>
    public static float ToAxisValue(GamepadAxis axis, short raw) =>
        axis is GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger
            ? Math.Clamp(raw / 32767f, 0f, 1f)
            : Math.Clamp(raw / 32767f, -1f, 1f);

    public static GamepadKind ToGamepadKind(GameControllerType type) => type switch {
        GameControllerType.Xbox360 or GameControllerType.Xboxone =>
            GamepadKind.Xbox,
        GameControllerType.PS3 or GameControllerType.PS4
            or GameControllerType.PS5 => GamepadKind.PlayStation,
        GameControllerType.NintendoSwitchPro
            or GameControllerType.NintendoSwitchJoyconLeft
            or GameControllerType.NintendoSwitchJoyconRight
            or GameControllerType.NintendoSwitchJoyconPair => GamepadKind.Nintendo,
        GameControllerType.Virtual => GamepadKind.Virtual,
        _ => GamepadKind.Unknown
    };

    public static SystemCursor ToSdl(CursorShape shape) => shape switch {
        CursorShape.TextBeam => SystemCursor.SystemCursorIbeam,
        CursorShape.Wait => SystemCursor.SystemCursorWait,
        CursorShape.Crosshair => SystemCursor.SystemCursorCrosshair,
        CursorShape.Hand => SystemCursor.SystemCursorHand,
        CursorShape.ResizeHorizontal => SystemCursor.SystemCursorSizewe,
        CursorShape.ResizeVertical => SystemCursor.SystemCursorSizens,
        CursorShape.ResizeDiagonalUp => SystemCursor.SystemCursorSizenesw,
        CursorShape.ResizeDiagonalDown => SystemCursor.SystemCursorSizenwse,
        CursorShape.ResizeAll => SystemCursor.SystemCursorSizeall,
        CursorShape.NotAllowed => SystemCursor.SystemCursorNo,
        _ => SystemCursor.SystemCursorArrow
    };
}
