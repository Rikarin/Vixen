// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using CoreGraphics;
using Foundation;
using UIKit;

namespace Vixen.Platform.Ios;

/// <summary>
///     Polled input state, which on this platform is almost entirely absent by design.
/// </summary>
/// <remarks>
///     <para>
///         Touch is an event stream, not a state anybody polls: there is no "is the finger down"
///         without saying which finger, and the answer belongs to whoever tracked the gesture. So
///         <see cref="PointerPosition" /> is the last touch and the keyboard and mouse queries are
///         false — an iPad with a trackpad and a hardware keyboard reports through <c>UIPress</c> and
///         <c>UIPointerInteraction</c>, which are their own work and are not started.
///     </para>
///     <para>
///         Gamepads are empty rather than approximated. <c>GameController.framework</c> is the right
///         answer and is a real piece of work — profiles, connection notifications, player index
///         lights — and an empty list is honest where a stub that reports one disconnected pad is
///         not.
///     </para>
/// </remarks>
internal sealed class IosInput : IInputSource {
    /// <inheritdoc />
    public IReadOnlyList<IGamepad> Gamepads => [];

    /// <inheritdoc />
    public KeyModifiers Modifiers => KeyModifiers.None;

    /// <inheritdoc />
    public Vector2 PointerPosition { get; internal set; }

    /// <inheritdoc />
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad) {
        gamepad = null;
        return false;
    }

    /// <inheritdoc />
    public bool IsKeyDown(Key key) => false;

    /// <inheritdoc />
    public bool IsMouseButtonDown(MouseButton button) => false;
}

/// <summary>The soft keyboard, and where it is.</summary>
/// <remarks>
///     <para>
///         <b>UIKit will not show a keyboard for nothing.</b> It appears because a first responder
///         asked for one, so this puts an invisible, zero-sized text field in the view hierarchy and
///         makes that the responder. That is the standard approach and it is worth naming rather
///         than hiding: there is no <c>showKeyboard()</c>.
///     </para>
///     <para>
///         <b>The keyboard's rectangle comes from the system notification, not from a guess.</b>
///         Its height depends on the language, the presence of a predictive bar, an external
///         keyboard being paired, and the device — so it is read from
///         <c>UIKeyboardFrameEndUserInfoKey</c> when the system announces it, and is empty until
///         then rather than defaulting to a number that will be wrong somewhere.
///     </para>
/// </remarks>
internal sealed class IosTextInput : ITextInput, IDisposable {
    readonly PlatformEventBuffer events;
    readonly IosTextField field = new();

    NSObject? shown;
    NSObject? hidden;
    uint windowId;

    internal IosTextInput(PlatformEventBuffer events) {
        this.events = events;
        field.Committed = OnText;

        shown = UIKeyboard.Notifications.ObserveWillShow((_, arguments) => {
                var frame = arguments.FrameEnd;
                OnScreenKeyboardArea = new(
                    (float)frame.X,
                    (float)frame.Y,
                    (float)frame.Width,
                    (float)frame.Height
                );

                IsOnScreenKeyboardVisible = true;
            }
        );

        hidden = UIKeyboard.Notifications.ObserveWillHide((_, _) => {
                IsOnScreenKeyboardVisible = false;
                OnScreenKeyboardArea = default;
            }
        );
    }

    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public bool HasOnScreenKeyboard => true;

    /// <inheritdoc />
    public bool IsOnScreenKeyboardVisible { get; private set; }

    /// <inheritdoc />
    public Rectangle OnScreenKeyboardArea { get; private set; }

    /// <inheritdoc />
    public void Activate(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);

        if (window is not IosWindow ios) {
            throw new ArgumentException("The window was not made by this platform.", nameof(window));
        }

        windowId = ios.Id;

        if (field.Superview is null) {
            ios.View.AddSubview(field);
        }

        IsActive = field.BecomeFirstResponder();
    }

    /// <inheritdoc />
    public void Deactivate() {
        field.ResignFirstResponder();
        IsActive = false;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing. iOS positions the candidate bar itself, above the keyboard, and gives an
    ///     application no say. The argument is validated so that a caller passing a foreign window
    ///     is told here rather than on the platform that happens to look.
    /// </remarks>
    public void SetCandidateArea(IWindow window, Rectangle area) {
        ArgumentNullException.ThrowIfNull(window);
    }

    /// <inheritdoc />
    public void Dispose() {
        shown?.Dispose();
        shown = null;
        hidden?.Dispose();
        hidden = null;
        field.Dispose();
    }

    void OnText(string text) {
        if (text.Length > 0) {
            events.Post(PlatformEvent.TextInput(windowId, IosClock.Now, text));
        }
    }
}

/// <summary>
///     The invisible field that exists so UIKit has a first responder to show a keyboard for.
/// </summary>
/// <remarks>
///     Its contents are never read and never displayed. Each keystroke is forwarded as it arrives
///     and the field is left empty, so backspace on an empty field still produces a keystroke — which
///     is what a game handling its own text editing needs, and is why this does not simply read
///     <c>Text</c> when editing ends.
/// </remarks>
internal sealed class IosTextField : UITextField, IUITextFieldDelegate {
    internal IosTextField() : base(CGRect.Empty) {
        Hidden = true;
        AutocorrectionType = UITextAutocorrectionType.No;
        AutocapitalizationType = UITextAutocapitalizationType.None;
        SpellCheckingType = UITextSpellCheckingType.No;
        WeakDelegate = this;
    }

    internal Action<string>? Committed { get; set; }

    [Export("textField:shouldChangeCharactersInRange:replacementString:")]
    public bool ShouldChangeCharactersInRange(UITextField textField, NSRange range, string replacementString) {
        Committed?.Invoke(replacementString.Length > 0 ? replacementString : "\b");

        // Never actually change: the field is a keyboard trigger, not a text box.
        return false;
    }
}
