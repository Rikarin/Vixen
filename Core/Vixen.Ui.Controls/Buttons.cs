// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>A button.</summary>
/// <remarks>
///     Everything about how it looks is <see cref="Control.Variant" />, <see cref="Control.Size" />
///     and the stylesheet; everything about what it does is <see cref="Control.Clicked" /> and
///     <see cref="ClickEvent" />. The only other things in the type are the two names every toolkit
///     gives a form's Return and Escape, and that is the point — a control library whose button has
///     thirty properties has thirty ways to produce a button that does not match the others.
/// </remarks>
public sealed partial class Button : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "button";

    /// <summary>Whether a bare Return that reaches the form unclaimed presses this button.</summary>
    /// <remarks>
    ///     <para>
    ///         AppKit's default button and WPF's <c>IsDefault</c>: <see cref="UiElement.KeyEquivalent" />
    ///         set to Return, plus a <c>default</c> class so a theme can draw it as the one Return
    ///         will press — which every platform does, because a default button the user cannot see
    ///         is a Return that does something unannounced. One per focus scope; the first in tree
    ///         order wins if there are two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fallback, not a claim.</b> The press reaches this only when nothing on the
    ///         route took it — a focused button presses itself on Return first, a multi-line field
    ///         keeps its newline — so it cannot steal a key a control was using. See
    ///         <see cref="KeyEquivalentEvent" />.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnIsDefaultChanged))]
    public partial bool IsDefault { get; set; }

    /// <summary>Whether a bare Escape that reaches the form unclaimed presses this button.</summary>
    /// <remarks>
    ///     The cancel half of <see cref="IsDefault" />, with a <c>cancel</c> class. ⚠ Inside a
    ///     <see cref="Dialog" /> it is the dialog's own Escape that runs — <c>Overlay</c> listens on
    ///     the root's capture leg and closes with <c>CloseReason.Cancelled</c> before the key is
    ///     routed at all — so this is for the form that is not an overlay: an inline editor, a
    ///     sheet drawn by hand, a login screen.
    ///     <para>
    ///         ⚠ <b>Which is why it has no production caller today, and that is the paragraph above
    ///         rather than an oversight.</b> Every confirm-and-cancel pair in this tree is built by
    ///         <c>DialogService</c> inside an <see cref="Overlay" />, so setting this on one of them
    ///         would be a flag that never runs: the close is already decided a leg earlier. The
    ///         editor's hand-rolled Escape handlers — <c>KeyBindingsView</c> leaving capture,
    ///         <c>SceneViewport</c> cancelling a gizmo drag — are keys on a panel and not buttons in
    ///         a form, so none of them is this either. <see cref="IsDefault" /> is wired
    ///         (<c>DialogService.AddButton</c> makes the primary button the default one); this half
    ///         waits for the first non-overlay form, and is declared with it so that form does not
    ///         have to invent the concept.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnIsCancelChanged))]
    public partial bool IsCancel { get; set; }

    void OnIsDefaultChanged(bool previous, bool current) => Equivalent("default", InputKey.Enter, current);

    void OnIsCancelChanged(bool previous, bool current) => Equivalent("cancel", InputKey.Escape, current);

    /// <summary>Writes or clears the key and the class one of the two flags stands for.</summary>
    /// <remarks>
    ///     Clearing only takes the key away if it is still this flag's: a button that was default,
    ///     then cancel, then not default keeps Escape.
    /// </remarks>
    void Equivalent(string className, InputKey key, bool on) {
        if (on) {
            KeyEquivalent = key;
            AddClass(className);
        } else {
            if (KeyEquivalent == key) {
                KeyEquivalent = InputKey.Unknown;
            }

            RemoveClass(className);
        }
    }
}

/// <summary>A button that is an icon and nothing else.</summary>
/// <remarks>
///     <para>
///         Its own type rather than a button with no label, because the two are different shapes:
///         this one is square and its padding is even, and a theme cannot tell "a button whose label
///         happens to be empty" from "a button meant to be an icon" without a selector on emptiness
///         that the cascade cannot express.
///     </para>
///     <para>
///         ⚠ <b><see cref="ButtonBase.Label" /> is still meaningful and is still not drawn.</b> It is
///         the control's name — what a tooltip shows and what an accessibility bridge will read —
///         and the theme hides it, so setting it costs nothing and leaving it unset costs a button
///         nobody can identify. That the hiding is the theme's job rather than this type's is what
///         lets a dense toolbar choose to show the labels after all.
///     </para>
/// </remarks>
public sealed partial class IconButton : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "icon-button";

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // Asked for up front, unlike on a plain button, because an icon button without one is not a
        // button with a missing icon — it is a blank square. Creating it here also puts it before
        // the label without the reordering the lazy path needs.
        _ = LeadingIcon;
    }
}

/// <summary>A button that stays pressed.</summary>
/// <remarks>
///     <para>
///         The bold-italic-underline control, and the one a toolbar is made of. It is a button
///         rather than a checkbox because it is activated like one and looks like one; what it has
///         instead of a tick is <see cref="ElementState.Checked" />, which <c>:checked</c> reads.
///     </para>
///     <para>
///         ⚠ <b>Toggling happens before the click is reported</b>, so a handler asking
///         <c>IsChecked</c> gets the state the user just chose rather than the one they were
///         leaving. Every toolkit that got this backwards produced a generation of handlers written
///         with an inverted condition.
///     </para>
/// </remarks>
public sealed partial class ToggleButton : ToggleBase {
    /// <inheritdoc />
    protected override string TagName => "toggle-button";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="AccessibleStates.Pressed" /> rather than the base's
    ///     <see cref="AccessibleStates.Checked" />, and it is the same distinction the first
    ///     paragraph above makes.</b> This is a button that stays in, so ARIA's word for it is
    ///     <c>aria-pressed</c> and a screen reader says "pressed"; a checkbox is
    ///     <c>aria-checked</c> and it says "ticked". The role stays
    ///     <see cref="AccessibleRole.Button" />, which is what makes the bold-italic-underline strip
    ///     read as three buttons rather than as a form.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        IsChecked ? AccessibleStates.Pressed : AccessibleStates.None;

    /// <inheritdoc />
    /// <remarks>
    ///     True, unlike the other toggles. This one is a <i>button</i> — it lives in a toolbar
    ///     rather than in a form, so there is no submit for Enter to mean instead, and a keyboard
    ///     user who has tabbed to it and pressed the key every other button answers to should not be
    ///     met with silence.
    /// </remarks>
    protected override bool ActivatesOnEnter => true;
}
