// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Ui;

/// <summary>The modifiers held down when something happened.</summary>
/// <remarks>
///     Carried on the event rather than queried from a keyboard device, for the reason every other
///     piece of input state in this assembly is: an interface has to be drivable from a recorded
///     trace and from a test, and "what was held when this arrived" is a property of the arrival
///     rather than of whatever the hardware looks like by the time a handler asks.
/// </remarks>
[Flags]
public enum ModifierKeys : byte {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Either Shift.</summary>
    Shift = 1 << 0,

    /// <summary>Either Control.</summary>
    Control = 1 << 1,

    /// <summary>Either Alt — Option, on a Mac.</summary>
    Alt = 1 << 2,

    /// <summary>Either Windows or Command key.</summary>
    /// <remarks>
    ///     Named after neither platform. <c>Meta</c> is what the web calls it and what X11 calls it,
    ///     and a framework that called it <c>Command</c> would read as a lie on Linux.
    /// </remarks>
    Meta = 1 << 3
}

/// <summary>What a key did.</summary>
public enum KeyAction : byte {
    /// <summary>It went down.</summary>
    Pressed,

    /// <summary>It came up.</summary>
    Released
}

/// <summary>A key going down or coming up.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A key is a position, not a character.</b> <see cref="Key" /> is the physical key by
///         its US-QWERTY legend — the same HID usage <c>Vixen.Input</c> reports — so a control that
///         wants Escape, Tab or the arrows reads this, and a control that wants the letter somebody
///         typed reads <see cref="TextInputEvent" /> instead. On an AZERTY keyboard the key that
///         types <c>a</c> is <see cref="InputKey.Q" />, and a text box that built its content out of
///         key codes would be unusable in France.
///     </para>
///     <para>
///         Routed like anything else, from the focus outwards. That is what lets a dialog handle
///         Escape without every control inside it knowing the dialog exists, and what makes a
///         shortcut on a panel work while the focus is on a field inside it.
///     </para>
/// </remarks>
public sealed class KeyEvent : UiEvent {
    /// <summary>Which physical key.</summary>
    public InputKey Key { get; init; }

    /// <summary>Whether it went down or came up.</summary>
    public KeyAction Action { get; init; }

    /// <summary>What was held at the time.</summary>
    public ModifierKeys Modifiers { get; init; }

    /// <summary>Whether this is the platform's auto-repeat rather than a fresh press.</summary>
    /// <remarks>
    ///     A text box wants repeats — holding Backspace deletes more than one character. A button
    ///     does not: holding Space should press it once. Neither can be derived from the other, so
    ///     the platform's answer is carried rather than reconstructed from timestamps.
    /// </remarks>
    public bool IsRepeat { get; init; }

    /// <summary>When it happened, on the same clock as <see cref="PointerEvent.Timestamp" />.</summary>
    public TimeSpan Timestamp { get; init; }

    /// <summary>Whether exactly these modifiers were held, and no others.</summary>
    /// <param name="modifiers">The ones to test for.</param>
    /// <returns>Whether that is what was held.</returns>
    /// <remarks>
    ///     ⚠ <b>Exact rather than "at least".</b> A control that activates on Enter and tests only
    ///     for the absence of Control also activates on Ctrl-Shift-Alt-Enter, which is somebody
    ///     else's shortcut arriving at the wrong place. Asking for exactness makes the common test
    ///     — <c>Has(ModifierKeys.None)</c> — the safe one.
    /// </remarks>
    public bool Has(ModifierKeys modifiers) => Modifiers == modifiers;
}

/// <summary>Text the platform decided the user typed.</summary>
/// <remarks>
///     <para>
///         Separate from <see cref="KeyEvent" /> because it is a different thing that happens to
///         come from the same hardware. A key press is a position; this is what the layout, the dead
///         keys and the input method between them produced — one event may carry several characters,
///         and a great many key presses carry none.
///     </para>
///     <para>
///         A <see cref="string" /> rather than a <c>char</c>, because an emoji is a surrogate pair
///         and a composed sequence is longer still. A control that switched on a single character
///         would work for everyone who tested it and break for everyone who did not.
///     </para>
/// </remarks>
public sealed class TextInputEvent : UiEvent {
    /// <summary>What was typed.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>When, on the same clock as the rest.</summary>
    public TimeSpan Timestamp { get; init; }
}

/// <summary>Text an input method is still composing, which is not yet text the user typed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The separate event is the whole design, and folding it into
///         <see cref="TextInputEvent" /> is how an interface becomes unusable in Japanese without
///         anybody noticing.</b> A composition is <i>provisional</i>: it is replaced in place on
///         every keystroke, may be abandoned entirely, and is only real when a
///         <see cref="TextInputEvent" /> arrives carrying the committed string. A control that
///         inserted each pre-edit as typed text ends up with every intermediate reading of every
///         word in the field, and a control that ignored the event shows nothing at all while the
///         user types — the field looks broken and the candidate window floats over a blank box.
///     </para>
///     <para>
///         ⚠ <b>An empty <see cref="Text" /> is a <i>cancellation</i> and not "nothing happened".</b>
///         Every platform ends an abandoned composition by sending one, and a handler that returns
///         early on an empty string leaves the last pre-edit on screen for ever.
///     </para>
///     <para>
///         <see cref="Start" /> and <see cref="Length" /> are the input method's own cursor
///         <i>within the pre-edit</i>, which is what puts the caret in the middle of a
///         half-converted phrase where the IME thinks it is rather than at the end of it.
///     </para>
/// </remarks>
public sealed class TextCompositionEvent : UiEvent {
    /// <summary>The pre-edit string, or empty to abandon the composition.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Where the input method's own cursor sits inside <see cref="Text" />.</summary>
    public int Start { get; init; }

    /// <summary>How much of <see cref="Text" /> that cursor has selected.</summary>
    public int Length { get; init; }

    /// <summary>When, on the same clock as the rest.</summary>
    public TimeSpan Timestamp { get; init; }
}

public sealed partial class UiDocument {
    /// <summary>Whether the most recent input came from the keyboard.</summary>
    /// <remarks>
    ///     <para>
    ///         What decides whether taking the focus also lights the focus ring. A ring drawn on
    ///         every click makes an interface look permanently confused, and a ring withheld from a
    ///         keyboard user makes it unusable — so the answer is neither "always" nor "never" but
    ///         "how did the focus get here", which is the browsers' <c>:focus-visible</c> heuristic
    ///         and the only one anybody has found that satisfies both.
    ///     </para>
    ///     <para>
    ///         ⚠ Set by <see cref="Dispatch(KeyEvent)" /> and cleared by a pointer <i>press</i>
    ///         rather than by a pointer move. A mouse moved across the screen while somebody is
    ///         tabbing through a form has not taken over the interaction, and clearing on movement
    ///         would put the ring out mid-keystroke.
    ///     </para>
    /// </remarks>
    public bool KeyboardMode { get; private set; }

    /// <summary>Sends a key event to whatever has the focus.</summary>
    /// <param name="args">The event.</param>
    /// <returns>The element it went to.</returns>
    /// <remarks>
    ///     <para>
    ///         To the focus, or to the root when nothing has it. The root is a real target rather
    ///         than a fallback nobody listens on: a shortcut registered at the top of the document
    ///         has to work before anything has been clicked, which is the state every application
    ///         starts in.
    ///     </para>
    ///     <para>
    ///         <b>Tab is handled here, and only if nothing else wanted it.</b> Focus traversal is the
    ///         document's job — no single control can know what comes next — but a control that
    ///         genuinely needs the key, a grid or a code editor that inserts an indent, has to be
    ///         able to take it. Running after the route and testing <see cref="UiEvent.Handled" /> is
    ///         what gives both: the default is the fallback rather than the rule.
    ///     </para>
    /// </remarks>
    public UiElement? Dispatch(KeyEvent args) => Dispatch(args, KeyTarget);

    /// <summary>Sends a key event that arrived at a particular window.</summary>
    /// <param name="surface">The surface the platform delivered it to.</param>
    /// <param name="args">The event.</param>
    /// <returns>The element it went to.</returns>
    /// <exception cref="ArgumentException">The surface belongs to another document.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The overload <see cref="Dispatch(UiSurface, PointerEvent)" /> and
    ///         <see cref="Dispatch(UiSurface, WheelEvent)" /> always had and this one did not.</b>
    ///         The reason given was that a key goes to the focus and the focus is the document's —
    ///         true, and it stops being an answer the moment nothing is focused: the fallback was
    ///         <see cref="Primary" />'s root, so a keystroke aimed at a torn-off inspector ran
    ///         against the main window.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is a better answer than <see cref="KeySurface" /> and not a duplicate of
    ///         it.</b> The key surface is the window manager's opinion, arriving through
    ///         <c>WindowFocusGained</c>; the surface here is where <i>this</i> event was actually
    ///         delivered, which is the operating system having already answered the question by
    ///         sending it at all. A host that has the surface in hand should pass it.
    ///     </para>
    ///     <para>
    ///         <see cref="Focused" /> still outranks it, because it is still one document-global
    ///         element — <c>UiSurface.Focused</c> does not exist, so a keystroke aimed at an
    ///         unfocused control in a background window still reaches whatever holds the document's
    ///         focus. That is the larger half of the key-window work and is owed.
    ///     </para>
    /// </remarks>
    public UiElement? Dispatch(UiSurface surface, KeyEvent args) => Dispatch(args, Focused ?? Verify(surface).Root);

    UiElement? Dispatch(KeyEvent args, UiElement target) {
        ArgumentNullException.ThrowIfNull(args);

        KeyboardMode = true;

        // ⚠ <b>Before the route, and it is the only key that is.</b> `CancelDrag`'s own remarks said
        // "Escape does this" and nothing anywhere called it — the whole in-app drag had no way out
        // but a release, so a drag begun by accident had to be finished somewhere harmless. A drag
        // is a modal gesture: while one is running the pointer is captured by its source and the
        // application is showing feedback for it, so Escape belongs to the drag and not to whatever
        // holds the focus. Offered after the route instead, a text field or an open menu would eat
        // it and the drag would still be running underneath.
        if (args is { Action: KeyAction.Pressed, Key: InputKey.Escape } && CancelDrag()) {
            args.Handled = true;
            return target;
        }

        target.Raise(args);

        // ⚠ The one place a non-element responder can see a key, and until this existed there was
        // none: `EventRouter.Raise` is `UiElement`-typed end to end and its route is a
        // `List<UiElement>`, so a view controller or a document object could answer `edit.copy` and
        // still be structurally unable to see the ⌘C that means it. This walks the same links
        // `CommandRoute.Resolve` walks, in the same order, so the two chains finally agree about
        // who is on them — after the bubble leg, because a focused control must still win, and
        // before the access-key and Tab fallbacks, because those are defaults and this is not.
        if (!args.Handled) {
            args.Handled = OfferToResponders(target, args);
        }

        // ⚠ After the route and only if nothing wanted it, exactly like Tab below. A menu that is
        // open has its own idea of what Alt-S means and must be able to take it; a text field that
        // handles Alt-Left for word movement must not lose it to an access key on a button called
        // "_Left". The default is the fallback rather than the rule.
        if (!args.Handled && TryAccessKey(args, out var access)) {
            args.Handled = InvokeAccessKey(access);
        }

        if (!args.Handled && args is { Action: KeyAction.Pressed, Key: InputKey.Tab }) {
            // Shift picks the direction and everything else disqualifies it. Ctrl-Tab is a document
            // switcher in every application that has documents, and consuming it here would mean a
            // tab strip could never be given it.
            if (args.Modifiers is ModifierKeys.None or ModifierKeys.Shift) {
                args.Handled = MoveFocus(
                    args.Modifiers == ModifierKeys.Shift ? FocusDirection.Previous : FocusDirection.Next
                );
            }
        }

        return target;
    }

    /// <summary>Sends typed text to whatever has the focus.</summary>
    /// <param name="args">The event.</param>
    /// <returns>The element it went to.</returns>
    public UiElement? Dispatch(TextInputEvent args) => DispatchText(args, KeyTarget);

    /// <summary>Sends typed text that arrived at a particular window.</summary>
    /// <param name="surface">The surface the platform delivered it to.</param>
    /// <param name="args">The event.</param>
    /// <returns>The element it went to.</returns>
    /// <exception cref="ArgumentException">The surface belongs to another document.</exception>
    /// <remarks>
    ///     ⚠ <b>The same defect <see cref="Dispatch(UiSurface, KeyEvent)" /> was fixed for, and it
    ///     outlived that fix by sitting next to it.</b> A key and the text it produces arrive from
    ///     the platform two events apart and were routed by two different rules — the key by the
    ///     window it was delivered to, the text by <see cref="Primary" /> whenever nothing was
    ///     focused. So a character typed into a torn-off window landed on the main one, and did so
    ///     silently: in a one-window application the two rules agree by construction, which is
    ///     exactly why nothing caught it.
    /// </remarks>
    public UiElement? Dispatch(UiSurface surface, TextInputEvent args) =>
        DispatchText(args, Focused ?? Verify(surface).Root);

    UiElement? DispatchText(TextInputEvent args, UiElement target) {
        ArgumentNullException.ThrowIfNull(args);

        KeyboardMode = true;

        target.Raise(args);
        return target;
    }

    /// <summary>Sends an input method's pre-edit to whatever has the focus.</summary>
    /// <param name="args">The event.</param>
    /// <returns>The element it went to.</returns>
    /// <remarks>
    ///     ⚠ <b>It does <i>not</i> enter keyboard mode, and that is the one difference from
    ///     <see cref="Dispatch(TextInputEvent)" />.</b> A composition is a consequence of keystrokes
    ///     the focus has already had, so the mode is already right; raising it here as well would
    ///     light the focus ring on a field somebody is typing into with the mouse still moving,
    ///     which is the case the mode exists to distinguish.
    /// </remarks>
    public UiElement? Dispatch(TextCompositionEvent args) => DispatchComposition(args, KeyTarget);

    /// <summary>Sends an input method's pre-edit that arrived at a particular window.</summary>
    /// <param name="surface">The surface the platform delivered it to.</param>
    /// <param name="args">The event.</param>
    /// <returns>The element it went to.</returns>
    /// <exception cref="ArgumentException">The surface belongs to another document.</exception>
    /// <remarks>
    ///     See <see cref="Dispatch(UiSurface, TextInputEvent)" />. ⚠ A composition is worse than a
    ///     character to get wrong rather than better: a pre-edit raised on the wrong root leaves the
    ///     window the user is actually typing into with no way to end the composition it never
    ///     started, and <c>TextField.CancelComposition</c> runs on focus loss in a window that never
    ///     had it.
    /// </remarks>
    public UiElement? Dispatch(UiSurface surface, TextCompositionEvent args) =>
        DispatchComposition(args, Focused ?? Verify(surface).Root);

    static UiElement? DispatchComposition(TextCompositionEvent args, UiElement target) {
        ArgumentNullException.ThrowIfNull(args);

        target.Raise(args);
        return target;
    }

    /// <summary>Checks that a surface handed to a routing overload is one of this document's.</summary>
    /// <remarks>
    ///     ⚠ Throws rather than falling back to <see cref="Primary" />. A surface from another
    ///     document is a caller mistake and not a state this can be in, and quietly routing to the
    ///     primary window is precisely the behaviour these overloads exist to stop.
    /// </remarks>
    UiSurface Verify(UiSurface surface) {
        ArgumentNullException.ThrowIfNull(surface);

        if (!ReferenceEquals(surface.Document, this)) {
            throw new ArgumentException("that surface belongs to another document.", nameof(surface));
        }

        return surface;
    }

    /// <summary>Offers a key to every responder appended along the walk, nearest first.</summary>
    /// <remarks>
    ///     <para>
    ///         The element leg only, and then the document's two slots — the same three legs
    ///         <see cref="CommandRoute.Resolve" /> has, in the same order. A responder that returns
    ///         <c>true</c> ends the walk, because "I took the key" and "keep asking" are the two
    ///         answers and there is no third.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>From the target upwards and not from the root down.</b> A capture leg here would
    ///         let an appended responder take a key <i>before</i> the control the user is typing
    ///         into, which is the ordering the editor's keymap deliberately rejects — see
    ///         <c>CommandDispatcher</c>'s bubble-leg handler and AppKit's
    ///         <c>performKeyEquivalent:</c>, which Vixen does not copy.
    ///     </para>
    /// </remarks>
    bool OfferToResponders(UiElement target, KeyEvent args) {
        for (var element = target; element is not null; element = element.Parent) {
            var responders = element.Responders;

            for (var i = 0; i < responders.Count; i++) {
                if (responders[i].OnKey(args)) {
                    return true;
                }
            }
        }

        // Written out rather than looped over an array of two, for the reason `CommandRoute.Resolve`
        // gives: the order is the rule, and the array would be an allocation on a keystroke path.
        return (CommandResponder?.OnKey(args) ?? false) || (ApplicationCommandResponder?.OnKey(args) ?? false);
    }

    /// <summary>Records that the interaction has gone back to the pointer.</summary>
    internal void LeaveKeyboardMode() => KeyboardMode = false;
}
