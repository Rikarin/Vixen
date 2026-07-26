// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.SDL;
using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>
///     The translation layer, tested without SDL running — none of it calls into the library, and a
///     mapping that is wrong is wrong on a machine with no display too.
/// </summary>
public class SdlTranslationTests {
    /// <summary>
    ///     The assumption <see cref="Key" />'s whole design rests on: SDL scancodes are USB HID
    ///     usage codes, so the keyboard translation is a cast. If Silk.NET ever renumbers them this
    ///     test is what says so, rather than every key on the keyboard being subtly wrong.
    /// </summary>
    [Theory]
    [InlineData(Scancode.ScancodeA, Key.A)]
    [InlineData(Scancode.ScancodeZ, Key.Z)]
    [InlineData(Scancode.ScancodeW, Key.W)]
    [InlineData(Scancode.Scancode1, Key.Number1)]
    [InlineData(Scancode.Scancode0, Key.Number0)]
    [InlineData(Scancode.ScancodeReturn, Key.Enter)]
    [InlineData(Scancode.ScancodeEscape, Key.Escape)]
    [InlineData(Scancode.ScancodeSpace, Key.Space)]
    [InlineData(Scancode.ScancodeF1, Key.F1)]
    [InlineData(Scancode.ScancodeF12, Key.F12)]
    [InlineData(Scancode.ScancodeLeft, Key.Left)]
    [InlineData(Scancode.ScancodeKP1, Key.Keypad1)]
    [InlineData(Scancode.ScancodeLctrl, Key.LeftControl)]
    [InlineData(Scancode.ScancodeRgui, Key.RightMeta)]
    [InlineData(Scancode.ScancodeNonusbackslash, Key.NonUsBackslash)]
    public void SdlScancodesAreHidUsageCodesAndSoAreOurs(Scancode scancode, Key expected) =>
        Assert.Equal(expected, SdlTranslation.ToKey(scancode));

    [Fact]
    public void EveryKeyWeNameSurvivesTheRoundTrip() {
        foreach (var key in Enum.GetValues<Key>()) {
            if (key == Key.Unknown) {
                continue;
            }

            Assert.Equal(key, SdlTranslation.ToKey(SdlTranslation.ToScancode(key)));
        }
    }

    /// <summary>
    ///     Android's back button sits outside the HID keyboard page, so it is the one key the
    ///     identity does not cover and the one that needs naming.
    /// </summary>
    [Fact]
    public void TheBackButtonIsTranslatedRatherThanCast() {
        Assert.Equal(Key.Back, SdlTranslation.ToKey((Scancode)270));
        Assert.Equal((Scancode)270, SdlTranslation.ToScancode(Key.Back));
    }

    [Fact]
    public void AScancodeWeDoNotNameIsUnknownRatherThanGarbage() {
        Assert.Equal(Key.Unknown, SdlTranslation.ToKey(Scancode.ScancodeUnknown));
        Assert.Equal(Key.Unknown, SdlTranslation.ToKey((Scancode)260));
    }

    [Fact]
    public void ModifiersKeepTheirSides() {
        var translated = SdlTranslation.ToModifiers(Keymod.Lshift | Keymod.Ralt | Keymod.Caps);

        Assert.True(translated.HasFlag(KeyModifiers.LeftShift));
        Assert.False(translated.HasFlag(KeyModifiers.RightShift));
        Assert.True(translated.HasFlag(KeyModifiers.RightAlt));
        Assert.False(translated.HasFlag(KeyModifiers.LeftAlt));
        Assert.True(translated.HasFlag(KeyModifiers.CapsLock));
        Assert.True(translated.HasAny(KeyModifiers.Shift));
        Assert.True(translated.HasAny(KeyModifiers.Alt));
    }

    /// <summary>
    ///     SDL numbers the middle button 2 and the right button 3, which is not the order anybody
    ///     guesses. Getting it wrong swaps paste and the context menu.
    /// </summary>
    [Theory]
    [InlineData(1, MouseButton.Primary)]
    [InlineData(2, MouseButton.Middle)]
    [InlineData(3, MouseButton.Secondary)]
    [InlineData(4, MouseButton.Extra1)]
    [InlineData(5, MouseButton.Extra2)]
    [InlineData(9, MouseButton.None)]
    public void SdlNumbersTheMiddleButtonSecond(byte sdlButton, MouseButton expected) =>
        Assert.Equal(expected, SdlTranslation.ToMouseButton(sdlButton));

    [Fact]
    public void EveryGamepadButtonSurvivesTheRoundTrip() {
        foreach (var button in Enum.GetValues<GamepadButton>()) {
            if (button == GamepadButton.None) {
                continue;
            }

            Assert.Equal(button, SdlTranslation.ToGamepadButton(SdlTranslation.ToSdl(button)));
        }
    }

    [Fact]
    public void EveryGamepadAxisSurvivesTheRoundTrip() {
        foreach (var axis in Enum.GetValues<GamepadAxis>()) {
            if (axis == GamepadAxis.None) {
                continue;
            }

            Assert.Equal(axis, SdlTranslation.ToGamepadAxis(SdlTranslation.ToSdl(axis)));
        }
    }

    /// <summary>
    ///     Positional names, not legends: SDL's <c>A</c> is the bottom face button, which is where
    ///     Nintendo puts <c>B</c>. Binding to the legend is how a jump button ends up on the wrong
    ///     one.
    /// </summary>
    [Fact]
    public void TheBottomFaceButtonIsSouthWhateverItIsLabelled() {
        Assert.Equal(GamepadButton.South, SdlTranslation.ToGamepadButton(GameControllerButton.A));
        Assert.Equal(GamepadButton.East, SdlTranslation.ToGamepadButton(GameControllerButton.B));
    }

    /// <summary>
    ///     A stick pushed fully left reads exactly -1, not -1.000031, and a stick pushed fully right
    ///     reads exactly 1, not 0.99997 — which is the one that never reaches a threshold set at 1.
    /// </summary>
    [Fact]
    public void FullStickDeflectionReachesExactlyOne() {
        Assert.Equal(-1f, SdlTranslation.ToAxisValue(GamepadAxis.LeftStickX, short.MinValue));
        Assert.Equal(1f, SdlTranslation.ToAxisValue(GamepadAxis.LeftStickX, short.MaxValue));
        Assert.Equal(0f, SdlTranslation.ToAxisValue(GamepadAxis.LeftStickX, 0));
    }

    /// <summary>A trigger has a rest position rather than a centre, so it is not signed.</summary>
    [Fact]
    public void TriggersRunFromZeroToOne() {
        Assert.Equal(0f, SdlTranslation.ToAxisValue(GamepadAxis.LeftTrigger, 0));
        Assert.Equal(1f, SdlTranslation.ToAxisValue(GamepadAxis.RightTrigger, short.MaxValue));
        Assert.Equal(0f, SdlTranslation.ToAxisValue(GamepadAxis.RightTrigger, short.MinValue));
    }

    [Theory]
    [InlineData(GameControllerType.Xbox360, GamepadKind.Xbox)]
    [InlineData(GameControllerType.PS5, GamepadKind.PlayStation)]
    [InlineData(GameControllerType.NintendoSwitchPro, GamepadKind.Nintendo)]
    [InlineData(GameControllerType.Virtual, GamepadKind.Virtual)]
    [InlineData(GameControllerType.Unknown, GamepadKind.Unknown)]
    public void ControllerFamiliesAreRecognisedForTheirGlyphs(GameControllerType type, GamepadKind expected) =>
        Assert.Equal(expected, SdlTranslation.ToGamepadKind(type));

    [Fact]
    public void EveryCursorShapeHasAStockCursor() {
        foreach (var shape in Enum.GetValues<CursorShape>()) {
            var cursor = SdlTranslation.ToSdl(shape);

            Assert.True(
                shape == CursorShape.Arrow || cursor != SystemCursor.SystemCursorArrow,
                $"{shape} fell through to the arrow cursor."
            );
        }
    }
}
