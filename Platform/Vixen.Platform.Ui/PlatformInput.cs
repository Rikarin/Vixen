// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Ui;
using Vixen.Ui.Styling;
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

    /// <summary>Tells every one of a document's surfaces what the system's appearance is now.</summary>
    /// <param name="document">The document.</param>
    /// <param name="scheme">What <see cref="IPlatform.ColorScheme" /> says.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The wire <c>@media (prefers-color-scheme: …)</c> was built without.</b> The query
    ///         has been evaluated per surface since doc 43's F11, and every writer of
    ///         <see cref="UiSurface.ColorScheme" /> in the tree was a test — so an application shipped
    ///         its light palette to a dark system and nothing anywhere reported it. F11 fed width,
    ///         height, resolution and gamut and left this one behind.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every surface, not <c>UiDocument.ColorScheme</c>.</b> That property is the
    ///         primary surface's, and a torn-off panel is a second surface with its own media
    ///         context — it inherits the scheme when it is created and would keep the old one for
    ///         ever after. An appearance is a setting of the machine, so all of them move together;
    ///         a gamut is negotiated per swapchain, and that one deliberately does not.
    ///     </para>
    ///     <para>
    ///         Called once before the first frame with <see cref="IPlatform.ColorScheme" />, and
    ///         again on each <see cref="PlatformEventKind.SystemColorSchemeChanged" />. A host that
    ///         only did the second would draw every frame of a session against the wrong palette on
    ///         a machine whose appearance never changed.
    ///     </para>
    /// </remarks>
    public static void ApplyColorScheme(UiDocument document, SystemColorScheme scheme) {
        ArgumentNullException.ThrowIfNull(document);

        var preference = scheme switch {
            SystemColorScheme.Dark => ColorSchemePreference.Dark,
            SystemColorScheme.Light => ColorSchemePreference.Light,

            // ⚠ `NoPreference`, and this is the line that has to stay honest. CSS says both
            // `(prefers-color-scheme: dark)` and `(prefers-color-scheme: light)` are false when the
            // user has expressed nothing, so a platform that could not read an appearance must not
            // be flattened into light — that would make the light block apply on a machine that
            // never asked for it.
            _ => ColorSchemePreference.NoPreference
        };

        foreach (var surface in document.Surfaces) {
            surface.ColorScheme = preference;
        }
    }

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
                document.Dispatch(
                    surface,
                    Pointer(
                        platformEvent,
                        PointerAction.Moved,
                        PointerButton.None,
                        modifiers,
                        when,
                        Mouse,
                        PointerType.Mouse
                    )
                );

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
                        when,
                        Mouse,
                        PointerType.Mouse
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

            // ⚠ <b>These three arms were missing, and the consumer they starve is not the one the
            // gap is named after.</b> <c>touch-action</c> is refused in the parity ledger because
            // there is no touch pipeline for it to govern — but <c>GestureRecognizer</c>, which the
            // document already runs on every pointer event, is written for several pointers at once:
            // it keys its presses by <c>PointerEvent.PointerId</c>, and its pinch needs two distinct
            // ones. Every producer of a <c>PointerEvent</c> left that id at its default, so the
            // recogniser has never in its life seen two. A tap, a long press, a drag and a pinch were
            // all implemented, tested against synthesised events, and unreachable from a finger.
            //
            // ⚠ <b>A touch is a pointer here rather than a fourth kind of event.</b> The document
            // hit-tests, hovers, captures and focuses in terms of one pointer abstraction, and a
            // parallel touch route would need every one of those again — which is how a control ends
            // up clickable and not tappable. What a touch does not share is the button: it has none,
            // so a press is <see cref="PointerButton.Primary" /> by convention and a move carries
            // <see cref="PointerButton.None" /> exactly as a mouse move does.
            case PlatformEventKind.TouchDown:
            case PlatformEventKind.TouchUp:
                document.Dispatch(
                    surface,
                    Pointer(
                        platformEvent,
                        platformEvent.Kind == PlatformEventKind.TouchDown
                            ? PointerAction.Pressed
                            : PointerAction.Released,
                        PointerButton.Primary,
                        modifiers,
                        when,
                        Finger(platformEvent.DeviceId),
                        PointerType.Touch
                    )
                );

                return true;

            case PlatformEventKind.TouchMoved:
                document.Dispatch(
                    surface,
                    Pointer(
                        platformEvent,
                        PointerAction.Moved,
                        PointerButton.None,
                        modifiers,
                        when,
                        Finger(platformEvent.DeviceId),
                        PointerType.Touch
                    )
                );

                return true;

            case PlatformEventKind.KeyDown:
            case PlatformEventKind.KeyUp:
                // ⚠ Routed by surface, and it used not to be. The comment here read "a key event
                // goes to the focus, and the focus is the document's rather than a window's" —
                // true, and it stops being an answer the moment nothing is focused, which is the
                // state every application starts in and returns to whenever something is dismissed.
                // The fallback was the *primary* surface's root, so a keystroke the operating
                // system delivered to a torn-off inspector ran against the main window. The surface
                // is in hand here; the OS has already answered the question by sending the event to
                // that window at all.
                document.Dispatch(
                    surface,
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

            // ⚠ <b>Two more arms that were missing, and their absence was invisible for the reason
            // `TextEditing`'s was.</b> Every backend produces these — `DesktopPlatform` from SDL,
            // `WebPlatform` from focus/blur on the canvas, `HeadlessWindow` from its own harness —
            // and this bridge dropped both through the `default` below, so no assembly above
            // `Vixen.Platform` had ever been told which window the user is in. The symptom is not an
            // error either: keys with nothing focused went to the *primary* surface's root, so a
            // keystroke aimed at a torn-off inspector ran against the main window.
            //
            // ⚠ Gained sets it and Lost only clears it *if this surface still holds it*. The two
            // events do not arrive in a guaranteed order — a window manager that raises B's gained
            // before A's lost is ordinary — and an unconditional clear on Lost would take the key
            // status away from the window that had just been given it.
            case PlatformEventKind.WindowFocusGained:
                document.KeySurface = surface;
                return true;

            case PlatformEventKind.WindowFocusLost:
                if (ReferenceEquals(document.KeySurface, surface)) {
                    document.KeySurface = null;
                }

                return true;

            // ⚠ <b>The second pair of arms this bridge was missing, and the same shape as the
            // first.</b> `DropFile` and `DropText` are produced by SDL (`DesktopPlatform`) and by
            // the browser (`WebPlatform`), are asserted by both backends' own tests, and fell
            // through the `default` below — so dragging a file onto the window was inert on every
            // platform this engine runs on. As with `TextEditing`, both halves were tested and the
            // join was neither, because a producer with no consumer has nothing to disagree with.
            case PlatformEventKind.DropFile:
            case PlatformEventKind.DropText:
                document.Dispatch(
                    surface,
                    new DropEvent {
                        X = platformEvent.Position.X,
                        Y = platformEvent.Position.Y,
                        Files = platformEvent.Kind == PlatformEventKind.DropFile
                            ? [platformEvent.Text]
                            : [],
                        Text = platformEvent.Kind == PlatformEventKind.DropText ? platformEvent.Text : null,
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
        TimeSpan when,
        int pointer,
        PointerType type
    ) =>
        new() {
            PointerId = pointer,
            PointerType = type,
            X = platformEvent.Position.X,
            Y = platformEvent.Position.Y,
            Action = action,
            Button = button,
            Modifiers = modifiers,
            Timestamp = when
        };

    /// <summary>The id every mouse event carries.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero, and written down rather than left to the default — because it is now a value
    ///     something else could collide with.</b> <c>TouchTracker</c> hands out the lowest free
    ///     finger from zero, so the first finger on a screen and the mouse would be the same pointer
    ///     to <c>GestureRecognizer</c>: a tablet with a stylus and a trackpad, or any browser, can
    ///     have both alive at once, and the failure is a press that is never released because the
    ///     other device's release closed it. <see cref="Finger" /> is what keeps the two ranges
    ///     apart; this constant is what says which range the mouse is in.
    /// </remarks>
    const int Mouse = 0;

    /// <summary>Which pointer a finger is, given the platform's id for it.</summary>
    /// <remarks>
    ///     Shifted past <see cref="Mouse" />. <c>TouchTracker.MaximumTouches</c> is ten and its ids
    ///     start at zero, so fingers are one to ten here and nothing overlaps.
    /// </remarks>
    static int Finger(int device) => device + 1;

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
