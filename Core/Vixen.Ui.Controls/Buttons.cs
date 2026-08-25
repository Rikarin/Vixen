// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>A button.</summary>
/// <remarks>
///     Everything about how it looks is <see cref="Control.Variant" />, <see cref="Control.Size" />
///     and the stylesheet; everything about what it does is <see cref="Control.Clicked" /> and
///     <see cref="ClickEvent" />. There is nothing else in the type, which is the point — a control
///     library whose button has thirty properties has thirty ways to produce a button that does not
///     match the others.
/// </remarks>
public sealed partial class Button : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "button";
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
