// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Platform;
using Vixen.Ui;
using PlatformKey = Vixen.Platform.Key;
using PointerButton = Vixen.Ui.PointerButton;

namespace Vixen.Samples.HelloUi;

/// <summary>Turns what the window reports into what the document understands.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Fifty lines, in a sample, and that is where it has to live for now.</b>
///         <c>Vixen.Ui</c> is a <c>Core/</c> assembly and <c>Vixen.Platform</c> is not, so the
///         framework cannot depend on the thing that produces these events — which is the layering
///         doc 00 makes non-negotiable and the reason a UI framework stays usable with no backend at
///         all. Something has to join them, and until there is a second consumer that something is
///         the application. A <c>Vixen.Platform.Ui</c> assembly is where this goes when the editor
///         becomes the second one.
///     </para>
///     <para>
///         <b>The key conversion is a cast, by construction.</b> <c>Vixen.Platform.Key</c> and
///         <c>Vixen.Input.InputKey</c> are both the USB HID usage table and
///         <c>InputKeyMatchesPlatformKeyTests</c> asserts it member by member — so there is no
///         translation table here and there must never be one, because a table is a thing that can
///         drift.
///     </para>
/// </remarks>
static class UiInput {
    /// <summary>Sends one platform event to a document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="platformEvent">What happened.</param>
    /// <param name="scale">How many device-independent pixels a physical one is.</param>
    /// <returns>Whether the document did something with it.</returns>
    public static bool Dispatch(UiDocument document, in PlatformEvent platformEvent, float scale) {
        // ⚠ Stopwatch ticks, not milliseconds. The platform's clock is monotonic and its unit is
        // whatever the machine's high-resolution timer counts in — a hundred nanoseconds here, a
        // nanosecond there — so converting by the wrong constant gives a gesture recogniser whose
        // double-tap window is either eternity or nothing. It is also the same clock `Shell.Tick`
        // reads through `Stopwatch.Elapsed`, which is what makes the two comparable at all.
        var when = Stopwatch.GetElapsedTime(0, platformEvent.Timestamp);
        var modifiers = Modifiers(platformEvent.Modifiers);

        switch (platformEvent.Kind) {
            case PlatformEventKind.MouseMoved:
                document.Dispatch(
                    Pointer(platformEvent, PointerAction.Moved, PointerButton.None, modifiers, when, scale)
                );

                return true;

            case PlatformEventKind.MouseButtonDown:
            case PlatformEventKind.MouseButtonUp:
                document.Dispatch(
                    Pointer(
                        platformEvent,
                        platformEvent.Kind == PlatformEventKind.MouseButtonDown
                            ? PointerAction.Pressed
                            : PointerAction.Released,
                        Button(platformEvent.MouseButton),
                        modifiers,
                        when,
                        scale
                    )
                );

                return true;

            case PlatformEventKind.MouseWheel:
                // ⚠ Negated, and scaled by a line height. A wheel notch reports positive for "away
                // from the user" and a scroll offset grows downwards, so the two disagree by a sign;
                // the multiplier is the backend's business everywhere except here, where there is no
                // backend to ask. `WheelEvent`'s own remarks say a framework that invents a constant
                // scrolls at a different speed from every other application on the machine — this
                // one is the sample's, not the framework's.
                document.Dispatch(
                    new WheelEvent {
                        X = platformEvent.Position.X / scale,
                        Y = platformEvent.Position.Y / scale,
                        DeltaX = -platformEvent.Delta.X * LineHeight,
                        DeltaY = -platformEvent.Delta.Y * LineHeight,
                        Timestamp = when
                    }
                );

                return true;

            case PlatformEventKind.KeyDown:
            case PlatformEventKind.KeyUp:
                document.Dispatch(
                    new KeyEvent {
                        Key = (Vixen.Input.InputKey) (ushort) platformEvent.Key,
                        Action = platformEvent.Kind == PlatformEventKind.KeyDown
                            ? KeyAction.Pressed
                            : KeyAction.Released,
                        Modifiers = modifiers,
                        IsRepeat = platformEvent.IsRepeat,
                        Timestamp = when
                    }
                );

                return true;

            case PlatformEventKind.TextInput:
                document.Dispatch(new TextInputEvent { Text = platformEvent.Text, Timestamp = when });
                return true;

            default:
                return false;
        }
    }

    /// <summary>How far one notch of the wheel scrolls, in device-independent pixels.</summary>
    const float LineHeight = 48f;

    static PointerEvent Pointer(
        in PlatformEvent platformEvent,
        PointerAction action,
        PointerButton button,
        ModifierKeys modifiers,
        TimeSpan when,
        float scale
    ) =>
        new() {
            // ⚠ Divided by the DPI scale. The window reports physical pixels and the document works
            // in device-independent ones; feeding the first into the second puts every click at
            // twice its position on a retina display, which looks like a hit-testing bug in the
            // framework rather than a missing division in the host.
            X = platformEvent.Position.X / scale,
            Y = platformEvent.Position.Y / scale,
            Action = action,
            Button = button,
            Modifiers = modifiers,
            Timestamp = when
        };

    static PointerButton Button(MouseButton button) =>
        button switch {
            MouseButton.Primary => PointerButton.Primary,
            MouseButton.Secondary => PointerButton.Secondary,
            MouseButton.Middle => PointerButton.Middle,
            _ => PointerButton.None
        };

    /// <summary>Folds the platform's left/right distinction away.</summary>
    /// <remarks>
    ///     The document does not care which Shift, and nothing in the control set ever will: a
    ///     shortcut that meant something different on the right-hand Control key would be one nobody
    ///     could discover. Anything that does care — a game rebinding keys — is reading
    ///     <c>Vixen.Input</c> rather than this.
    /// </remarks>
    static ModifierKeys Modifiers(KeyModifiers modifiers) {
        var result = ModifierKeys.None;

        if ((modifiers & KeyModifiers.Shift) != 0) {
            result |= ModifierKeys.Shift;
        }

        if ((modifiers & KeyModifiers.Control) != 0) {
            result |= ModifierKeys.Control;
        }

        if ((modifiers & KeyModifiers.Alt) != 0) {
            result |= ModifierKeys.Alt;
        }

        if ((modifiers & KeyModifiers.Meta) != 0) {
            result |= ModifierKeys.Meta;
        }

        return result;
    }

    /// <summary>Whether a key is one the platform reports and this maps.</summary>
    /// <remarks>
    ///     Exists so the cast above is not silently wrong for a key nobody thought about: an unknown
    ///     usage arrives as <c>Unknown</c> at both ends, which is a key the controls ignore rather
    ///     than a key they mistake for another.
    /// </remarks>
    public static bool IsKnown(PlatformKey key) => key != PlatformKey.Unknown;
}
