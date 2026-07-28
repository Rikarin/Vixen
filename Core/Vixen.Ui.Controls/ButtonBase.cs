// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>Anything that can be pressed.</summary>
/// <remarks>
///     <para>
///         A button, a menu item, a tab, a page number, a link. They look nothing alike and they all
///         answer to the same four things — a tap, Space, Enter, and code — so the four live here
///         once rather than in nine controls that would each get one of them slightly wrong.
///     </para>
///     <para>
///         ⚠ <b>Space activates on release and Enter on press, which is not an inconsistency.</b> It
///         is what every desktop toolkit and every browser does, and the reason is that Space is the
///         key you can back out of: holding it shows the button pressed, and moving the focus away
///         before letting go cancels. Enter has no such affordance because Enter is the one that
///         submits, and a form that waited for the release would feel late. Swapping either makes
///         the control feel wrong in a way users report as "sticky" without being able to say why.
///     </para>
///     <para>
///         <b>The label is a child element rather than text on the control</b>, and that is forced:
///         an element with text measures itself and may not have children, so a button that carried
///         its own label could never also have an icon, and its text could not be centred inside a
///         button that had been stretched. One element buys both.
///     </para>
/// </remarks>
public abstract partial class ButtonBase : Control {
    UiElement label = null!;
    Icon? icon;
    bool pressedByKeyboard;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        label = Part("label");

        AddHandler<TapEvent>(static (element, args) => ((ButtonBase) element).Tapped(args));
        AddHandler<KeyEvent>(static (element, args) => ((ButtonBase) element).Keyed(args));
        AddHandler<PointerEvent>(static (element, args) => ((ButtonBase) element).Pointed(args));
        AddHandler<FocusEvent>(static (element, args) => ((ButtonBase) element).Refocused(args));
        AddHandler<AccessKeyEvent>(static (element, args) => ((ButtonBase) element).Accessed(args));
    }

    /// <summary>An access key naming this button presses it.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported as a keyboard activation, not as code.</b> It is one — somebody held Alt and
    ///     pressed a letter — and a handler that logs, or a menu that closes on a keyboard press but
    ///     not on a programmatic one, has to be able to tell them apart. <see cref="Activate()" />
    ///     without an argument means <see cref="ActivationDevice.Code" /> and would say the wrong
    ///     thing here.
    /// </remarks>
    void Accessed(AccessKeyEvent args) {
        if (Disabled) {
            return;
        }

        Activate(ActivationDevice.Keyboard, 1, ModifierKeys.Alt);
        args.Handled = true;
    }

    /// <summary>What it says.</summary>
    /// <remarks>
    ///     <c>null</c> and the empty string are the same thing here — a label with neither measures
    ///     nothing and takes up no room, which is what an icon-only button wants and what stops a
    ///     button that was never given a label from reserving a line's height for it.
    /// </remarks>
    public string? Label {
        get => label.Text;
        set => label.Text = value;
    }

    /// <summary>An icon before the label, created the first time it is asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>Created and then <i>moved</i> to the front</b>, because a new child lands at the end
    ///     and an icon after the words is not what anybody means by a leading icon. Lazy rather than
    ///     always-present because most buttons have no icon, and an element per button that is never
    ///     used is an element per button all the same.
    /// </remarks>
    public Icon LeadingIcon {
        get {
            if (icon is not null) {
                return icon;
            }

            icon = Part<Icon>();
            Document.Move(icon, 0);

            return icon;
        }
    }

    /// <summary>Whether it has been given an icon, without giving it one.</summary>
    public bool HasIcon => icon is not null;

    /// <summary>The element the label is drawn on, for a control that wants to restyle or replace it.</summary>
    protected UiElement LabelPart => label;

    /// <summary>Whether Enter activates it as well as Space.</summary>
    /// <remarks>
    ///     ⚠ <b>False for everything that is a state rather than an action.</b> A checkbox, a switch
    ///     and a radio are activated with Space and not with Enter, and that is not a quirk of HTML:
    ///     Enter inside a form means "submit", and a dialog whose Enter ticks whichever checkbox has
    ///     the focus instead of pressing its default button is a dialog that does the wrong thing
    ///     under the fingers of everyone who never touches the mouse.
    /// </remarks>
    protected virtual bool ActivatesOnEnter => true;

    /// <summary>Activates it as though it had been clicked.</summary>
    /// <remarks>
    ///     The entry point for a test, a script, an access key and an automation peer — all of which
    ///     need the control to do its thing without a pointer existing. A disabled control does
    ///     nothing, the same as it would with one.
    /// </remarks>
    public void Activate() => Activate(ActivationDevice.Code, 1, ModifierKeys.None);

    /// <summary>What activation means for this control.</summary>
    /// <param name="device">What activated it.</param>
    /// <param name="count">How many taps in a row, for a pointer activation.</param>
    /// <param name="modifiers">What was held on the keyboard.</param>
    /// <remarks>
    ///     Overridden by the controls for which pressing is a means rather than an end — a toggle
    ///     flips, a tab selects itself, a menu item runs and closes the menu — all of which still
    ///     want the click reported afterwards, so an override calls its base.
    /// </remarks>
    protected virtual void Activate(ActivationDevice device, int count, ModifierKeys modifiers) =>
        RaiseClick(device, count, modifiers);

    void Tapped(TapEvent args) {
        if (Disabled) {
            return;
        }

        Activate(ActivationDevice.Pointer, args.Count, args.Modifiers);

        // ⚠ Handled after acting rather than before. A tap that reaches a button has been dealt
        // with — a list row under it must not also select — but a menu item is inside a menu that
        // needs to hear the same tap in order to close, and it hears it because a container listens
        // on the capture leg. Marking it here rather than in the handler stops the *bubble*, which
        // is the leg where somebody would double-handle it.
        args.Handled = true;
    }

    void Pointed(PointerEvent args) {
        if (args is { Action: PointerAction.Pressed, Button: PointerButton.Primary } && Focusable) {
            // A click focuses, and — because the document is not in keyboard mode — does not light
            // the focus ring. That combination is the whole of :focus-visible, and it is why this
            // is one call rather than a flag.
            Document.Focus(this);
        }
    }

    void Keyed(KeyEvent args) {
        if (Disabled) {
            return;
        }

        switch (args) {
            case { Action: KeyAction.Pressed, Key: InputKey.Enter or InputKey.KeypadEnter }
                when ActivatesOnEnter && args.Has(ModifierKeys.None):
                Activate(ActivationDevice.Keyboard, 1, args.Modifiers);
                args.Handled = true;
                break;

            case { Action: KeyAction.Pressed, Key: InputKey.Space } when args.Has(ModifierKeys.None):
                // The repeat is swallowed rather than ignored. Holding Space on a button should
                // press it once; letting the repeats through to an ancestor would scroll the page
                // underneath while the button looks pressed.
                if (!args.IsRepeat) {
                    pressedByKeyboard = true;
                    State |= ElementState.Active;
                }

                args.Handled = true;
                break;

            case { Action: KeyAction.Released, Key: InputKey.Space }:
                if (pressedByKeyboard) {
                    Unpress();
                    Activate(ActivationDevice.Keyboard, 1, args.Modifiers);
                }

                args.Handled = true;
                break;

            default:
                break;
        }
    }

    /// <summary>Backs out of a keyboard press that never completed.</summary>
    /// <remarks>
    ///     ⚠ <b>Losing the focus mid-press has to cancel it</b>, or the control is left looking
    ///     pressed for as long as the document lives — the release goes to whatever has the focus
    ///     now, which is not this, so nothing else will ever clear the flag. It is the same reason
    ///     the pointer's press state is cleared when a subtree is removed.
    /// </remarks>
    void Refocused(FocusEvent args) {
        if (!args.Gained) {
            Unpress();
        }
    }

    void Unpress() {
        pressedByKeyboard = false;
        State &= ~ElementState.Active;
    }
}
