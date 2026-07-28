// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Input;

/// <summary>The text form of an <see cref="InputControl" />: <c>&lt;Gamepad&gt;/leftStick/x</c>.</summary>
/// <remarks>
///     <para>
///         <b>Text, because it has to survive things a struct cannot.</b> A binding lives in a
///         <c>.vxinput</c> under version control, in a player's saved rebindings, and in a diff
///         someone reviews — all places where <c>(3, 32, 0)</c> is unreadable and, worse, silently
///         wrong the day a control is inserted into the middle of an enum. The path is the stable
///         name; the struct is the fast one.
///     </para>
///     <para>
///         <b>A path may name a pair.</b> <c>&lt;Gamepad&gt;/leftStick</c> is a two-dimensional
///         control and binding it to a vector action should not require the author to write two
///         bindings and a composite. So parsing yields an X control and, where the path names a
///         two-dimensional thing, a Y as well.
///     </para>
///     <para>
///         Parsing is case-insensitive and formatting is camelCase, which is the same rule the rest
///         of the engine's authored files follow.
///     </para>
/// </remarks>
public static class InputControlPath {
    /// <summary>Reads a path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="control">The control, or its X component for a two-dimensional path.</param>
    /// <returns><see langword="false" /> if the path names nothing this build has.</returns>
    public static bool TryParse(string? path, out InputControl control) => TryParse(path, out control, out _);

    /// <summary>Reads a path, which may name a two-dimensional control.</summary>
    /// <param name="path">The path.</param>
    /// <param name="x">The control, or the horizontal half of a two-dimensional one.</param>
    /// <param name="y">
    ///     The vertical half, or <see cref="InputControl.None" /> when the path names one control.
    /// </param>
    /// <returns><see langword="false" /> if the path names nothing this build has.</returns>
    public static bool TryParse(string? path, out InputControl x, out InputControl y) {
        x = InputControl.None;
        y = InputControl.None;

        if (string.IsNullOrWhiteSpace(path)) {
            return false;
        }

        var text = path!.Trim();

        if (text[0] != '<') {
            return false;
        }

        var close = text.IndexOf('>');

        if (close < 2) {
            return false;
        }

        var device = Device(text.Substring(1, close - 1));

        if (device == InputDeviceKind.None) {
            return false;
        }

        var slash = text.IndexOf('/', close);

        if (slash < 0 || slash + 1 >= text.Length) {
            return false;
        }

        // The digits between '>' and '/' are the device or finger index: <Gamepad>1/south is the
        // second pad. Absent means "the reading player's own", which is what a binding should say.
        var index = 0;

        if (slash > close + 1
            && !int.TryParse(
                text.AsSpan(close + 1, slash - close - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index
            )) {
            return false;
        }

        if (index is < 0 or > byte.MaxValue) {
            return false;
        }

        var remainder = text.Substring(slash + 1);
        var second = remainder.IndexOf('/');
        var head = second < 0 ? remainder : remainder.Substring(0, second);
        var tail = second < 0 ? string.Empty : remainder.Substring(second + 1);

        return device switch {
            InputDeviceKind.Keyboard => tail.Length == 0 && Keyboard(head, out x),
            InputDeviceKind.Mouse => Mouse(head, tail, (byte)index, out x, out y),
            InputDeviceKind.Gamepad => Gamepad(head, tail, (byte)index, out x, out y),
            InputDeviceKind.Touch => Touch(head, tail, (byte)index, out x, out y),
            _ => false
        };
    }

    /// <summary>Writes a control as a path.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The path, or <c>&lt;None&gt;</c> for <see cref="InputControl.None" />.</returns>
    public static string Format(InputControl control) {
        if (!control.IsValid) {
            return "<None>";
        }

        var builder = new StringBuilder(24);
        builder.Append('<').Append(Name(control.Device)).Append('>');

        if (control.Index != 0) {
            builder.Append(control.Index.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append('/');

        builder.Append(
            control.Device switch {
                InputDeviceKind.Keyboard => CamelCase(((InputKey)control.Code).ToString()),
                InputDeviceKind.Mouse => MouseSegment((MouseControl)control.Code),
                InputDeviceKind.Gamepad => GamepadSegment((GamepadControl)control.Code),
                _ => TouchSegment((TouchControl)control.Code)
            }
        );

        return builder.ToString();
    }

    /// <summary>Writes a pair of controls as the one path that names both.</summary>
    /// <param name="x">The horizontal half.</param>
    /// <param name="y">The vertical half.</param>
    /// <returns>
    ///     The two-dimensional path — <c>&lt;Gamepad&gt;/leftStick</c> — or
    ///     <see cref="Format(InputControl)" /> of <paramref name="x" /> when the two are not a pair.
    /// </returns>
    public static string Format(InputControl x, InputControl y) {
        if (!y.IsValid || x.Device != y.Device || x.Index != y.Index || y.Code != x.Code + 1) {
            return Format(x);
        }

        var stem = (x.Device, x.Code) switch {
            (InputDeviceKind.Mouse, (ushort)MouseControl.DeltaX) => "delta",
            (InputDeviceKind.Mouse, (ushort)MouseControl.ScrollX) => "scroll",
            (InputDeviceKind.Mouse, (ushort)MouseControl.PositionX) => "position",
            (InputDeviceKind.Touch, (ushort)TouchControl.DeltaX) => "delta",
            (InputDeviceKind.Touch, (ushort)TouchControl.PositionX) => "position",
            (InputDeviceKind.Gamepad, (ushort)GamepadControl.LeftStickX) => "leftStick",
            (InputDeviceKind.Gamepad, (ushort)GamepadControl.RightStickX) => "rightStick",
            (InputDeviceKind.Gamepad, (ushort)GamepadControl.DPadX) => "dpad",
            _ => null
        };

        if (stem is null) {
            return Format(x);
        }

        var index = x.Index == 0 ? string.Empty : x.Index.ToString(CultureInfo.InvariantCulture);
        return $"<{Name(x.Device)}>{index}/{stem}";
    }

    /// <summary>What to show a player who is looking at this binding.</summary>
    /// <param name="control">The control.</param>
    /// <returns>A human-readable name — <c>Left Stick X</c>, <c>W</c>, <c>Right Trigger</c>.</returns>
    /// <remarks>
    ///     Derived from the control's own name rather than from a translation table, and that is a
    ///     stopping point rather than an answer: a shipping game shows a glyph for a gamepad button
    ///     and the <em>layout's</em> label for a key, neither of which this assembly can know. It is
    ///     the right default for a debug panel and for the rebinding screen's first draft.
    /// </remarks>
    public static string Describe(InputControl control) {
        if (!control.IsValid) {
            return "None";
        }

        var name = control.Device switch {
            InputDeviceKind.Keyboard => ((InputKey)control.Code).ToString(),
            InputDeviceKind.Mouse => ((MouseControl)control.Code).ToString(),
            InputDeviceKind.Gamepad => ((GamepadControl)control.Code).ToString(),
            _ => ((TouchControl)control.Code).ToString()
        };

        var builder = new StringBuilder(name.Length + 8);

        for (var at = 0; at < name.Length; at++) {
            if (at > 0 && char.IsUpper(name[at]) && !char.IsUpper(name[at - 1])) {
                builder.Append(' ');
            }

            builder.Append(name[at]);
        }

        return builder.ToString();
    }

    static string MouseSegment(MouseControl control) => control switch {
        MouseControl.DeltaX => "delta/x",
        MouseControl.DeltaY => "delta/y",
        MouseControl.ScrollX => "scroll/x",
        MouseControl.ScrollY => "scroll/y",
        MouseControl.PositionX => "position/x",
        MouseControl.PositionY => "position/y",
        _ => CamelCase(control.ToString())
    };

    /// <summary>
    ///     A gamepad control's segment, where three of them are not their member's name.
    /// </summary>
    /// <remarks>
    ///     <c>LeftStick</c> the button has to write itself as <c>leftStickPress</c>, because
    ///     <c>&lt;Gamepad&gt;/leftStick</c> already means the two-dimensional stick and a format that
    ///     produced a path parsing back to a different control would break every saved rebinding the
    ///     first time one was written out.
    /// </remarks>
    static string GamepadSegment(GamepadControl control) => control switch {
        GamepadControl.LeftStick => "leftStickPress",
        GamepadControl.RightStick => "rightStickPress",
        GamepadControl.LeftStickX => "leftStick/x",
        GamepadControl.LeftStickY => "leftStick/y",
        GamepadControl.RightStickX => "rightStick/x",
        GamepadControl.RightStickY => "rightStick/y",
        GamepadControl.DPadX => "dpad/x",
        GamepadControl.DPadY => "dpad/y",
        GamepadControl.DPadUp => "dpad/up",
        GamepadControl.DPadDown => "dpad/down",
        GamepadControl.DPadLeft => "dpad/left",
        GamepadControl.DPadRight => "dpad/right",
        _ => CamelCase(control.ToString())
    };

    static string TouchSegment(TouchControl control) => control switch {
        TouchControl.DeltaX => "delta/x",
        TouchControl.DeltaY => "delta/y",
        TouchControl.PositionX => "position/x",
        TouchControl.PositionY => "position/y",
        _ => CamelCase(control.ToString())
    };

    static bool Keyboard(string name, out InputControl control) {
        control = InputControl.None;

        if (Digit(name, out var digit)) {
            control = InputControl.Key(digit);
            return true;
        }

        if (!TryReadEnum<InputKey>(name, out var key) || key == InputKey.Unknown) {
            return false;
        }

        control = InputControl.Key(key);
        return true;
    }

    static bool Mouse(string head, string tail, byte index, out InputControl x, out InputControl y) {
        x = InputControl.None;
        y = InputControl.None;

        switch (head.ToLowerInvariant()) {
            case "delta": return Pair(InputDeviceKind.Mouse, (ushort)MouseControl.DeltaX, tail, index, out x, out y);
            case "scroll": return Pair(InputDeviceKind.Mouse, (ushort)MouseControl.ScrollX, tail, index, out x, out y);
            case "position":
                return Pair(InputDeviceKind.Mouse, (ushort)MouseControl.PositionX, tail, index, out x, out y);
            case "left": x = InputControl.Mouse(MouseControl.Primary); return tail.Length == 0;
            case "right": x = InputControl.Mouse(MouseControl.Secondary); return tail.Length == 0;
            default: break;
        }

        if (tail.Length > 0 || !TryReadEnum<MouseControl>(head, out var control) || control == MouseControl.None) {
            return false;
        }

        x = new(InputDeviceKind.Mouse, (ushort)control, index);
        return true;
    }

    static bool Gamepad(string head, string tail, byte index, out InputControl x, out InputControl y) {
        x = InputControl.None;
        y = InputControl.None;

        switch (head.ToLowerInvariant()) {
            case "leftstick":
                return Pair(InputDeviceKind.Gamepad, (ushort)GamepadControl.LeftStickX, tail, index, out x, out y);

            case "rightstick":
                return Pair(InputDeviceKind.Gamepad, (ushort)GamepadControl.RightStickX, tail, index, out x, out y);

            case "dpad":
                switch (tail.ToLowerInvariant()) {
                    case "up": x = InputControl.Gamepad(GamepadControl.DPadUp, index); return true;
                    case "down": x = InputControl.Gamepad(GamepadControl.DPadDown, index); return true;
                    case "left": x = InputControl.Gamepad(GamepadControl.DPadLeft, index); return true;
                    case "right": x = InputControl.Gamepad(GamepadControl.DPadRight, index); return true;
                    default:
                        return Pair(InputDeviceKind.Gamepad, (ushort)GamepadControl.DPadX, tail, index, out x, out y);
                }

            case "leftstickpress": x = InputControl.Gamepad(GamepadControl.LeftStick, index); return tail.Length == 0;
            case "rightstickpress": x = InputControl.Gamepad(GamepadControl.RightStick, index); return tail.Length == 0;
            default: break;
        }

        // `buttonSouth` is what Unity's paths call it and what an author migrating will type. The
        // engine's own name is the positional one, and both resolve to the same control.
        var name = head.StartsWith("button", StringComparison.OrdinalIgnoreCase) && head.Length > 6
            ? head.Substring(6)
            : head;

        if (tail.Length > 0 || !TryReadEnum<GamepadControl>(name, out var control) || control == GamepadControl.None) {
            return false;
        }

        x = new(InputDeviceKind.Gamepad, (ushort)control, index);
        return true;
    }

    static bool Touch(string head, string tail, byte finger, out InputControl x, out InputControl y) {
        x = InputControl.None;
        y = InputControl.None;

        switch (head.ToLowerInvariant()) {
            case "delta": return Pair(InputDeviceKind.Touch, (ushort)TouchControl.DeltaX, tail, finger, out x, out y);

            case "position":
                return Pair(InputDeviceKind.Touch, (ushort)TouchControl.PositionX, tail, finger, out x, out y);
            default: break;
        }

        if (tail.Length > 0 || !TryReadEnum<TouchControl>(head, out var control) || control == TouchControl.None) {
            return false;
        }

        x = new(InputDeviceKind.Touch, (ushort)control, finger);
        return true;
    }

    /// <summary>Resolves a two-dimensional stem, with or without an <c>/x</c> or <c>/y</c> after it.</summary>
    /// <remarks>
    ///     Every pair in the control enums is <c>X</c> then <c>Y</c> at consecutive values, which is
    ///     what makes one line of arithmetic enough here and what
    ///     <see cref="Format(InputControl, InputControl)" /> relies on to recognise a pair going the
    ///     other way. <c>PairsAreConsecutiveTests</c> holds that property.
    /// </remarks>
    static bool Pair(InputDeviceKind device, ushort horizontal, string tail, byte index, out InputControl x, out InputControl y) {
        x = InputControl.None;
        y = InputControl.None;

        switch (tail.ToLowerInvariant()) {
            case "":
                x = new(device, horizontal, index);
                y = new(device, (ushort)(horizontal + 1), index);
                return true;

            case "x":
                x = new(device, horizontal, index);
                return true;

            case "y":
                x = new(device, (ushort)(horizontal + 1), index);
                return true;

            default:
                return false;
        }
    }

    static bool Digit(string name, out InputKey key) {
        if (name.Length == 1 && name[0] is >= '0' and <= '9') {
            key = name[0] == '0' ? InputKey.Number0 : (InputKey)((int)InputKey.Number1 + (name[0] - '1'));
            return true;
        }

        key = InputKey.Unknown;
        return false;
    }

    /// <summary>Reads an enum member by name, refusing the numeric form.</summary>
    /// <remarks>
    ///     <see cref="Enum.TryParse{T}(string, bool, out T)" /> also accepts <c>"32"</c> and returns
    ///     the member with that value, which would make <c>&lt;Keyboard&gt;/26</c> a legal spelling of
    ///     <c>w</c> and a path that stops meaning the same thing when a member is renumbered. Refused
    ///     here so that only names ever appear in an asset.
    /// </remarks>
    static bool TryReadEnum<T>(string name, out T value) where T : struct, Enum {
        value = default;

        if (name.Length == 0 || char.IsDigit(name[0]) || name[0] == '-' || name[0] == '+') {
            return false;
        }

        return Enum.TryParse(name, ignoreCase: true, out value);
    }

    static InputDeviceKind Device(string name) => name.ToLowerInvariant() switch {
        "keyboard" => InputDeviceKind.Keyboard,
        "mouse" or "pointer" => InputDeviceKind.Mouse,
        "gamepad" or "joystick" => InputDeviceKind.Gamepad,
        "touch" or "touchscreen" => InputDeviceKind.Touch,
        _ => InputDeviceKind.None
    };

    static string Name(InputDeviceKind device) => device switch {
        InputDeviceKind.Keyboard => "Keyboard",
        InputDeviceKind.Mouse => "Mouse",
        InputDeviceKind.Gamepad => "Gamepad",
        InputDeviceKind.Touch => "Touch",
        _ => "None"
    };

    static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
