// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Tests;

public class PlatformEventTests {
    [Fact]
    public void AKeyEventCarriesTheKeyTheModifiersAndWhetherItRepeated() {
        var down = PlatformEvent.Keyboard(
            PlatformEventKind.KeyDown,
            7,
            1234,
            Key.W,
            KeyModifiers.LeftShift | KeyModifiers.LeftControl,
            isRepeat: true
        );

        Assert.Equal(PlatformEventKind.KeyDown, down.Kind);
        Assert.Equal(7u, down.WindowId);
        Assert.Equal(1234, down.Timestamp);
        Assert.Equal(Key.W, down.Key);
        Assert.True(down.IsRepeat);
        Assert.True(down.Modifiers.HasAny(KeyModifiers.Shift));
        Assert.True(down.Modifiers.HasAny(KeyModifiers.Control));
        Assert.False(down.Modifiers.HasAny(KeyModifiers.Alt));
    }

    /// <summary>
    ///     The trap the either-side masks set, written down as a test because it compiles, reads
    ///     correctly, and produces a shortcut that never fires: <see cref="Enum.HasFlag" /> against
    ///     a two-bit mask asks whether <em>both</em> shift keys are held.
    /// </summary>
    [Fact]
    public void HasFlagAsksTheWrongQuestionAboutAnEitherSideMask() {
        const KeyModifiers held = KeyModifiers.LeftShift;

        Assert.False(held.HasFlag(KeyModifiers.Shift));
        Assert.True(held.HasAny(KeyModifiers.Shift));
        Assert.False(held.HasAll(KeyModifiers.Shift));
    }

    /// <summary>
    ///     <c>Ctrl+S</c> must not fire on <c>Ctrl+Shift+S</c>, and must still fire with caps lock on.
    /// </summary>
    [Fact]
    public void AShortcutMatchesExactlyTheModifiersItWantsAndIgnoresTheLocks() {
        Assert.True((KeyModifiers.LeftControl).Exactly(KeyModifiers.Control));
        Assert.True((KeyModifiers.RightControl | KeyModifiers.CapsLock).Exactly(KeyModifiers.Control));
        Assert.False((KeyModifiers.LeftControl | KeyModifiers.LeftShift).Exactly(KeyModifiers.Control));
        Assert.True((KeyModifiers.LeftControl | KeyModifiers.RightShift).Exactly(KeyModifiers.Control | KeyModifiers.Shift));
        Assert.True(KeyModifiers.NumLock.Exactly(KeyModifiers.None));
    }

    /// <summary>
    ///     The two sizes share nothing but the event they arrive in, and confusing them is how a
    ///     HiDPI window renders at a quarter of its size. They have to survive the round trip
    ///     through the shared payload slots distinctly.
    /// </summary>
    [Fact]
    public void AResizeCarriesTheLogicalSizeAndThePixelSizeSeparately() {
        var resized = PlatformEvent.WindowResized(1, 0, new(1280, 720), new(2560, 1440));

        Assert.Equal(new Int2(1280, 720), resized.Size);
        Assert.Equal(new Int2(2560, 1440), resized.PixelSize);
    }

    [Fact]
    public void PointerEventsCarryPositionAndDeltaIndependently() {
        var moved = PlatformEvent.MouseMoved(1, 0, new(100.5f, 200.25f), new(-3f, 4f));

        Assert.Equal(new Vector2(100.5f, 200.25f), moved.Position);
        Assert.Equal(new Vector2(-3f, 4f), moved.Delta);
    }

    /// <summary>
    ///     A trackpad reports fractions of a notch. Rounding them to integers is what makes smooth
    ///     scrolling stop being smooth on every modern laptop, so the payload has to be a float and
    ///     stay one.
    /// </summary>
    [Fact]
    public void AWheelDeltaKeepsItsFraction() {
        var wheel = PlatformEvent.MouseWheel(1, 0, Vector2.Zero, new(0f, 0.125f));

        Assert.Equal(0.125f, wheel.Delta.Y);
    }

    [Fact]
    public void ACompositionCarriesItsTextAndTheCursorWithinIt() {
        var editing = PlatformEvent.TextEditing(1, 0, "にほ", 1, 1);

        Assert.Equal("にほ", editing.Text);
        Assert.Equal(1, editing.SelectionStart);
        Assert.Equal(1, editing.SelectionLength);
    }

    [Fact]
    public void AGamepadAxisCarriesItsDeviceAxisAndPosition() {
        var moved = PlatformEvent.GamepadAxisMoved(0, 3, GamepadAxis.LeftStickX, -0.75f);

        Assert.Equal(3, moved.DeviceId);
        Assert.Equal(GamepadAxis.LeftStickX, moved.GamepadAxis);
        Assert.Equal(-0.75f, moved.Value);

        // Application-wide, so it belongs to no window — a pad is not plugged into one.
        Assert.Equal(0u, moved.WindowId);
    }

    [Fact]
    public void ATouchKeepsItsFingerApartFromItsPressure() {
        var touch = PlatformEvent.Touch(
            PlatformEventKind.TouchMoved,
            1,
            0,
            fingerId: 2,
            position: new(10f, 20f),
            delta: new(1f, 1f),
            pressure: 0.5f
        );

        Assert.Equal(2, touch.DeviceId);
        Assert.Equal(0.5f, touch.Value);
        Assert.Equal(new Vector2(10f, 20f), touch.Position);
    }

    [Fact]
    public void ADefaultEventIsNoEvent() {
        PlatformEvent nothing = default;

        Assert.Equal(PlatformEventKind.None, nothing.Kind);
        Assert.Equal(0u, nothing.WindowId);
    }

    [Fact]
    public void TheStringFormIsUsefulEnoughToDebugWith() {
        Assert.Contains("KeyDown", PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, 0, Key.Escape, KeyModifiers.None).ToString(), StringComparison.Ordinal);
        Assert.Contains("Escape", PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 1, 0, Key.Escape, KeyModifiers.None).ToString(), StringComparison.Ordinal);
        Assert.Contains("2560", PlatformEvent.WindowResized(1, 0, new(1280, 720), new(2560, 1440)).ToString(), StringComparison.Ordinal);
    }
}
