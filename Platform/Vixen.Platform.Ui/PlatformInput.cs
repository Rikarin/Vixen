// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Ui;
using PointerButton = Vixen.Ui.PointerButton;

namespace Vixen.Platform.Ui;

/// <summary>Turns what a window reports into what a document understands.</summary>
/// <remarks>
///     <para>
///         <b>The assembly this was always going to live in.</b> <c>Vixen.Ui</c> is a <c>Core/</c>
///         assembly and <c>Vixen.Platform</c> is not, so the framework cannot depend on the thing
///         that produces these events — the layering doc 00 makes non-negotiable, and the reason a
///         UI framework stays usable with no backend at all. <c>Samples/02-HelloUi</c> and
///         <c>Vixen.Editor.App</c> each carried a copy of this file and each copy said the same
///         thing: that a <c>Vixen.Platform.Ui</c> is where it goes once there is a second consumer.
///     </para>
///     <para>
///         <b>The key conversion is a cast, by construction.</b> <c>Vixen.Platform.Key</c> and
///         <c>Vixen.Input.InputKey</c> are both the USB HID usage table and
///         <c>InputKeyMatchesPlatformKeyTests</c> asserts it member by member — so there is no
///         translation table here and there must never be one, because a table is a thing that can
///         drift.
///     </para>
///     <para>
///         ⚠ <b>Every pointer event names the surface it happened in.</b> A document can be shown in
///         several windows, and two windows do not share a coordinate space: an event delivered to
///         the wrong one lands at the right numbers in the wrong place, which looks like a hit-test
///         bug and is a routing one. The window id an event carries is what decides, and
///         <see cref="PlatformWindowHost.TryResolve" /> is what turns it into a surface.
///     </para>
/// </remarks>
public static class PlatformInput {
    /// <summary>How far one notch of the wheel scrolls, in device-independent pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant, and one that should not be.</b> A wheel notch is not a pixel and every
    ///     platform disagrees about how many it is worth; SDL 2 does not report the user's system
    ///     setting, so this is an application's number standing in for the operating system's. It is
    ///     public so an application that knows better can pass its own.
    /// </remarks>
    public const float WheelLineHeight = 48f;

    /// <summary>Sends one platform event to a document's primary surface.</summary>
    /// <param name="document">The document.</param>
    /// <param name="platformEvent">What happened.</param>
    /// <returns>Whether the document did something with it.</returns>
    public static bool Dispatch(UiDocument document, in PlatformEvent platformEvent) {
        ArgumentNullException.ThrowIfNull(document);
        return Dispatch(document, document.Primary, platformEvent);
    }

    /// <summary>Sends one platform event to the surface the window it names is showing.</summary>
    /// <param name="document">The document.</param>
    /// <param name="surface">Which of its surfaces the event happened in.</param>
    /// <param name="platformEvent">What happened.</param>
    /// <param name="wheelLineHeight">How far a wheel notch scrolls.</param>
    /// <returns>Whether the document did something with it.</returns>
    public static bool Dispatch(
        UiDocument document,
        UiSurface surface,
        in PlatformEvent platformEvent,
        float wheelLineHeight = WheelLineHeight
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(surface);

        // ⚠ Stopwatch ticks, not milliseconds. The platform's clock is monotonic and its unit is
        // whatever the machine's high-resolution timer counts in — a hundred nanoseconds here, a
        // nanosecond there — so converting by the wrong constant gives a gesture recogniser whose
        // double-tap window is either eternity or nothing. It is also the same clock a host reads
        // through `Stopwatch.Elapsed`, which is what makes the two comparable at all.
        var when = Stopwatch.GetElapsedTime(0, platformEvent.Timestamp);
        var modifiers = Modifiers(platformEvent.Modifiers);

        switch (platformEvent.Kind) {
            case PlatformEventKind.MouseMoved:
                document.Dispatch(surface, Pointer(platformEvent, PointerAction.Moved, PointerButton.None, modifiers, when));

                return true;

            case PlatformEventKind.MouseButtonDown:
            case PlatformEventKind.MouseButtonUp:
                document.Dispatch(
                    surface,
                    Pointer(
                        platformEvent,
                        platformEvent.Kind == PlatformEventKind.MouseButtonDown
                            ? PointerAction.Pressed
                            : PointerAction.Released,
                        Button(platformEvent.MouseButton),
                        modifiers,
                        when
                    )
                );

                return true;

            case PlatformEventKind.MouseWheel:
                // ⚠ Negated, and scaled by a line height. A wheel notch reports positive for "away
                // from the user" and a scroll offset grows downwards, so the two disagree by a sign;
                // the multiplier is the backend's business everywhere except here, where there is no
                // backend to ask.
                document.Dispatch(
                    surface,
                    new WheelEvent {
                        X = platformEvent.Position.X,
                        Y = platformEvent.Position.Y,
                        DeltaX = -platformEvent.Delta.X * wheelLineHeight,
                        DeltaY = -platformEvent.Delta.Y * wheelLineHeight,
                        Modifiers = modifiers,
                        Timestamp = when
                    }
                );

                return true;

            case PlatformEventKind.KeyDown:
            case PlatformEventKind.KeyUp:
                // Not routed by surface: a key event goes to the focus, and the focus is the
                // document's rather than a window's. Which window has it is the operating system's
                // question and it has already answered by sending the event at all.
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

            // ⚠ <b>This arm was missing, and its absence was invisible from either end.</b> Two
            // platform heads produce `TextEditing` — `DesktopPlatform` from SDL and `WebPlatform`
            // from the invisible `<input>`'s composition events — and this bridge dropped it through
            // the `default` below, so nothing in `Vixen.Ui` had ever seen an input method's pre-edit.
            // The symptom is not an error: a Japanese user types, the field stays empty until the
            // composition commits, and the candidate window floats over a blank box. Nothing in
            // either assembly's tests could see it, because the producers had no consumer to
            // disagree with.
            case PlatformEventKind.TextEditing:
                document.Dispatch(
                    new TextCompositionEvent {
                        Text = platformEvent.Text,
                        Start = platformEvent.SelectionStart,
                        Length = platformEvent.SelectionLength,
                        Timestamp = when
                    }
                );

                return true;

            default:
                return false;
        }
    }

    /// <remarks>
    ///     ⚠ <b>Not scaled, and an earlier version of this divided by the DPI factor.</b> The
    ///     platform reports pointer positions in <i>logical points</i> — the same space
    ///     <c>IWindow.ClientSize</c> is in and the same space the document is laid out in — so there
    ///     is nothing to convert. Dividing put every click at a fraction of where it was made, which
    ///     showed up as hover highlighting the wrong control and read as a hit-testing bug in the
    ///     framework rather than an arithmetic one in the host. The framebuffer is the only thing in
    ///     physical pixels, and the only thing that needs the scale is the scissor.
    /// </remarks>
    static PointerEvent Pointer(
        in PlatformEvent platformEvent,
        PointerAction action,
        PointerButton button,
        ModifierKeys modifiers,
        TimeSpan when
    ) =>
        new() {
            X = platformEvent.Position.X,
            Y = platformEvent.Position.Y,
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
}
