using System.Diagnostics;
using Vixen.Platform;
using Vixen.Ui;
using PointerButton = Vixen.Ui.PointerButton;

namespace VixenApp1;

/// <summary>Turns what the window reports into what the document understands.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Fifty lines, in your application, and that is where they belong for now.</b>
///         <c>Vixen.Ui</c> is a <c>Core/</c> assembly and <c>Vixen.Platform</c> is not, so the
///         framework cannot depend on the thing that produces these events — which is the layering
///         that keeps a UI framework usable with no backend at all. Something has to join them, and
///         until the engine ships a <c>Vixen.Platform.Ui</c> that something is the application.
///     </para>
///     <para>
///         <b>The key conversion is a cast, by construction.</b> <c>Vixen.Platform.Key</c> and
///         <c>Vixen.Input.InputKey</c> are both the USB HID usage table, and the engine asserts it
///         member by member — so there is no translation table here and there must never be one,
///         because a table is a thing that can drift.
///     </para>
/// </remarks>
static class AppInput {
    /// <summary>Sends one platform event to a document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="platformEvent">What happened.</param>
    /// <returns>Whether the document did something with it.</returns>
    public static bool Dispatch(UiDocument document, in PlatformEvent platformEvent) {
        // ⚠ Stopwatch ticks, not milliseconds. The platform's clock is monotonic and its unit is
        // whatever the machine's high-resolution timer counts in, so converting by the wrong
        // constant gives a gesture recogniser whose double-tap window is either eternity or nothing.
        // It is also the same clock AppShell.Tick reads through Stopwatch.Elapsed, which is what
        // makes the two comparable at all.
        var when = Stopwatch.GetElapsedTime(0, platformEvent.Timestamp);
        var modifiers = Modifiers(platformEvent.Modifiers);

        switch (platformEvent.Kind) {
            case PlatformEventKind.MouseMoved:
                document.Dispatch(Pointer(platformEvent, PointerAction.Moved, PointerButton.None, modifiers, when));

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
                        when
                    )
                );

                return true;

            case PlatformEventKind.MouseWheel:
                // ⚠ Negated, and scaled by a line height. A wheel notch reports positive for "away
                // from the user" and a scroll offset grows downwards, so the two disagree by a sign;
                // the multiplier is the backend's business everywhere except here, where there is no
                // backend to ask. This constant is yours to match the rest of the machine with.
                document.Dispatch(
                    new WheelEvent {
                        X = platformEvent.Position.X,
                        Y = platformEvent.Position.Y,
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

    /// <remarks>
    ///     ⚠ <b>Not scaled by the DPI factor.</b> The platform reports pointer positions in
    ///     <i>logical points</i> — the same space <c>IWindow.ClientSize</c> is in and the same space
    ///     the document is laid out in — so there is nothing to convert. Dividing puts every click
    ///     at a fraction of where it was made, which looks like a hit-testing bug in the framework
    ///     rather than an arithmetic one here. The framebuffer is the only thing in physical pixels,
    ///     and the only thing that needs the scale is the scissor.
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
    ///     The document does not care which Shift: a shortcut that meant something different on the
    ///     right-hand Control key would be one nobody could discover. Anything that does care is
    ///     reading <c>Vixen.Input</c> rather than this.
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
