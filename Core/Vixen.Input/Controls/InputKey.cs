// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>A key by its <em>physical position</em>, independent of the keyboard layout.</summary>
/// <remarks>
///     <para>
///         <b>The same values as <c>Vixen.Platform.Key</c>, deliberately and by construction.</b>
///         Both are the USB HID usage table for keyboards, so the conversion between them is a cast
///         and the bridge that feeds this assembly from a platform's event stream does not carry a
///         translation table. <c>InputKeyMatchesPlatformKeyTests</c> asserts member by member that
///         the two agree; a value that drifted would be a failing test rather than a control that
///         binds to the wrong key on one backend.
///     </para>
///     <para>
///         <b>Why the enum is here at all rather than referenced.</b> <c>Vixen.Input</c> is a
///         <c>Core/</c> assembly and <c>Vixen.Platform</c> is not, and <c>CheckArchitecture</c>
///         refuses the reference upward — for the reason that makes the layering worth having:
///         <c>Vixen.Ui</c> consumes this assembly and must stay usable without a platform backend.
///         The engine's answer to that everywhere else is the same as here, and
///         <c>Vixen.Ui.PointerButton</c> is the existing instance of it.
///     </para>
///     <para>
///         The names below are the US-QWERTY legends because something has to name a position. On a
///         French AZERTY layout <see cref="Q" /> is the key labelled <c>A</c>. Typed characters are
///         not keys and never arrive as one — see <c>ITextInput</c>.
///     </para>
/// </remarks>
public enum InputKey : ushort {
    /// <summary>No key, or a key this platform does not report.</summary>
    Unknown = 0,

    A = 4, B = 5, C = 6, D = 7, E = 8, F = 9, G = 10, H = 11, I = 12,
    J = 13, K = 14, L = 15, M = 16, N = 17, O = 18, P = 19, Q = 20, R = 21,
    S = 22, T = 23, U = 24, V = 25, W = 26, X = 27, Y = 28, Z = 29,

    /// <summary>The <c>1</c> on the number row, not the numeric keypad.</summary>
    Number1 = 30,
    Number2 = 31, Number3 = 32, Number4 = 33, Number5 = 34,
    Number6 = 35, Number7 = 36, Number8 = 37, Number9 = 38, Number0 = 39,

    Enter = 40,
    Escape = 41,
    Backspace = 42,
    Tab = 43,
    Space = 44,
    Minus = 45,
    Equals = 46,
    LeftBracket = 47,
    RightBracket = 48,
    Backslash = 49,
    Semicolon = 51,
    Apostrophe = 52,
    Grave = 53,
    Comma = 54,
    Period = 55,
    Slash = 56,
    CapsLock = 57,

    F1 = 58, F2 = 59, F3 = 60, F4 = 61, F5 = 62, F6 = 63,
    F7 = 64, F8 = 65, F9 = 66, F10 = 67, F11 = 68, F12 = 69,

    PrintScreen = 70,
    ScrollLock = 71,
    Pause = 72,
    Insert = 73,
    Home = 74,
    PageUp = 75,
    Delete = 76,
    End = 77,
    PageDown = 78,
    Right = 79,
    Left = 80,
    Down = 81,
    Up = 82,

    NumLock = 83,
    KeypadDivide = 84,
    KeypadMultiply = 85,
    KeypadMinus = 86,
    KeypadPlus = 87,
    KeypadEnter = 88,
    Keypad1 = 89, Keypad2 = 90, Keypad3 = 91, Keypad4 = 92, Keypad5 = 93,
    Keypad6 = 94, Keypad7 = 95, Keypad8 = 96, Keypad9 = 97, Keypad0 = 98,
    KeypadPeriod = 99,

    /// <summary>The extra key next to left shift on ISO keyboards, absent on ANSI ones.</summary>
    NonUsBackslash = 100,

    /// <summary>The menu key, and the same physical key Android reports for its menu button.</summary>
    Application = 101,

    F13 = 104, F14 = 105, F15 = 106, F16 = 107, F17 = 108, F18 = 109,
    F19 = 110, F20 = 111, F21 = 112, F22 = 113, F23 = 114, F24 = 115,

    LeftControl = 224,
    LeftShift = 225,

    /// <summary>Left <c>Alt</c>, and <c>Option</c> on a Mac.</summary>
    LeftAlt = 226,

    /// <summary>Left <c>Windows</c>, <c>Command</c> on a Mac, <c>Super</c> on Linux.</summary>
    LeftMeta = 227,

    RightControl = 228,
    RightShift = 229,

    /// <summary>Right <c>Alt</c>, which is <c>AltGr</c> on layouts that have one.</summary>
    RightAlt = 230,

    /// <summary>Right <c>Windows</c>, <c>Command</c> on a Mac, <c>Super</c> on Linux.</summary>
    RightMeta = 231,

    /// <summary>Android's hardware back button, and the browser's back gesture.</summary>
    Back = 512
}
