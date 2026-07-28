// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>How prominent a control is, and what it is about to do.</summary>
/// <remarks>
///     ⚠ <b>An intent rather than a colour.</b> <c>Danger</c> says the button deletes something;
///     that it is red is the theme's decision and a theme is entitled to say it another way — with a
///     border, an icon, or nothing at all on a monochrome skin. A <c>Red</c> member would put the
///     decision in the caller, where it cannot be restyled.
/// </remarks>
public enum ControlVariant : byte {
    /// <summary>The ordinary one.</summary>
    Default,

    /// <summary>The one action a screen is mostly about.</summary>
    Primary,

    /// <summary>Quiet: no fill, no border until it is hovered.</summary>
    Subtle,

    /// <summary>It went well.</summary>
    Success,

    /// <summary>It might not.</summary>
    Warning,

    /// <summary>It destroys something.</summary>
    Danger
}

/// <summary>How large a control is.</summary>
public enum ControlSize : byte {
    /// <summary>Dense — toolbars, inspectors, anything with a lot of rows.</summary>
    Small,

    /// <summary>The default.</summary>
    Medium,

    /// <summary>Touch targets and primary actions.</summary>
    Large
}

/// <summary>What every control in the set has in common.</summary>
/// <remarks>
///     <para>
///         <b>Three things, and deliberately not more.</b> Whether it is disabled, how prominent it
///         is, and how big — because those are the three questions asked of every control by
///         somebody who is not writing a stylesheet. Everything else a control looks like is CSS,
///         which is the whole reason the styling engine exists.
///     </para>
///     <para>
///         ⚠ <b><see cref="ControlVariant" /> and <see cref="ControlSize" /> are written through to
///         classes</b> — <c>variant-primary</c>, <c>size-sm</c> — rather than read by anything here.
///         That is what puts them in reach of the cascade: a game can restyle
///         <c>button.variant-danger</c>, add a variant of its own with a plain class, and override
///         one control's size with a selector, none of which is possible for a property this
///         assembly interprets itself.
///     </para>
///     <para>
///         The class names are prefixed because they land in the same namespace as the
///         application's own. <c>.primary</c> would be a collision waiting to happen in every
///         project that has ever had a primary anything.
///     </para>
/// </remarks>
public abstract partial class Control : UiElement {
    /// <summary>Whether it refuses input.</summary>
    /// <remarks>
    ///     Sets <see cref="ElementState.Disabled" />, so <c>:disabled</c> is answered by the cascade;
    ///     takes the control out of the tab order, which is what HTML does and what stops Tab
    ///     landing on something that cannot be used; and makes the control swallow pointer events
    ///     before they reach anything inside it.
    /// </remarks>
    [UiProperty(Changed = nameof(OnDisabledChanged))]
    public partial bool Disabled { get; set; }

    /// <summary>How prominent it is.</summary>
    [UiProperty(Changed = nameof(OnVariantChanged))]
    public partial ControlVariant Variant { get; set; }

    /// <summary>How large it is.</summary>
    [UiProperty(Default = ControlSize.Medium, Changed = nameof(OnSizeChanged))]
    public partial ControlSize Size { get; set; }

    /// <summary>Whether the focus can rest on this kind of control at all.</summary>
    /// <remarks>
    ///     Distinct from <see cref="UiElement.Focusable" />, which is the answer for this instance
    ///     right now — a disabled button is not focusable and is still the sort of thing that would
    ///     be. Overridden to <c>false</c> by the controls that display rather than accept: a
    ///     progress bar, a badge, a separator.
    /// </remarks>
    protected virtual bool AcceptsFocus => true;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Focusable = AcceptsFocus;

        // ⚠ The initial classes, because a change callback fires on a change and a control that is
        // born medium-sized never had one. Without these the two properties would work perfectly
        // for every value except their defaults, which is the shape of bug that survives a test
        // suite: every test that sets a variant passes, and the plain button is unstyled.
        AddClass(ClassOf(Variant));
        AddClass(ClassOf(Size));

        // On capture rather than bubble, so that a disabled control's children never see the event.
        // A composite control — a numeric input's spinner buttons, a combo box's field — is made of
        // controls that have no idea their host has been disabled, and a check at the bubble leg
        // would run after all of them had already acted.
        AddHandler<PointerEvent>(static (element, args) => Refuse((Control) element, args), RoutingStrategy.Capture);
        AddHandler<KeyEvent>(static (element, args) => Refuse((Control) element, args), RoutingStrategy.Capture);
        AddHandler<TextInputEvent>(static (element, args) => Refuse((Control) element, args), RoutingStrategy.Capture);
    }

    /// <summary>Stops an event at a disabled control.</summary>
    /// <remarks>
    ///     ⚠ <b>Marked handled rather than dropped</b>, because the route continues either way — the
    ///     router walks a path it took before any handler ran. Handling it is what stops every
    ///     element after this one from acting, and it is also what tells an ancestor that something
    ///     inside it declined: a list row that highlights on click should not highlight when the
    ///     click was on a disabled button in it.
    /// </remarks>
    static void Refuse(Control control, UiEvent args) {
        if (control.Disabled) {
            args.Handled = true;
        }
    }

    void OnDisabledChanged(bool previous, bool current) {
        if (current) {
            State |= ElementState.Disabled;

            // Before the focus is refused rather than after. A control disabled while it has the
            // focus keeps it otherwise, and the keyboard then talks to something that will not
            // answer — with no way out, because Tab starts from wherever the focus is.
            if (IsFocused) {
                Document.Focus(null);
            }
        } else {
            State &= ~ElementState.Disabled;
        }

        Focusable = AcceptsFocus && !current;
    }

    void OnVariantChanged(ControlVariant previous, ControlVariant current) {
        RemoveClass(ClassOf(previous));
        AddClass(ClassOf(current));
    }

    void OnSizeChanged(ControlSize previous, ControlSize current) {
        RemoveClass(ClassOf(previous));
        AddClass(ClassOf(current));
    }

    /// <summary>The class a variant is written through to.</summary>
    /// <remarks>
    ///     ⚠ <c>Default</c> has one too, rather than being the absence of a class. A theme that
    ///     wants to say "every variant except the default" can then write
    ///     <c>button:not(.variant-default)</c>, and more importantly the two callbacks above stay
    ///     symmetric — a remove-then-add where one of the two values is a no-op is where the bug
    ///     that leaves two variant classes on one element comes from.
    /// </remarks>
    internal static string ClassOf(ControlVariant variant) =>
        variant switch {
            ControlVariant.Primary => "variant-primary",
            ControlVariant.Subtle => "variant-subtle",
            ControlVariant.Success => "variant-success",
            ControlVariant.Warning => "variant-warning",
            ControlVariant.Danger => "variant-danger",
            _ => "variant-default"
        };

    /// <summary>The class a size is written through to.</summary>
    internal static string ClassOf(ControlSize size) =>
        size switch {
            ControlSize.Small => "size-sm",
            ControlSize.Large => "size-lg",
            _ => "size-md"
        };

    /// <summary>Adds one of this control's own parts.</summary>
    /// <param name="tag">The part's element name, which the theme selects on.</param>
    /// <param name="classNames">Any classes it starts with.</param>
    /// <returns>The part.</returns>
    /// <remarks>
    ///     <para>
    ///         A part is an ordinary child element — there is no shadow tree here, and a document
    ///         that walked one would need two of every pass. What makes it a part rather than
    ///         content is that the control created it and holds a field pointing at it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which means a caller can see it, and reach it.</b> Said out loud rather than
    ///         defended: <c>slider.Children</c> contains the track. Every control in this set that
    ///         takes content of its own therefore offers a method for adding it — <c>AddTab</c>,
    ///         <c>AddItem</c>, a <c>Content</c> panel — so that nobody has to guess whether their
    ///         child lands before or after the parts.
    ///     </para>
    /// </remarks>
    protected UiElement Part(string tag, params string[] classNames) => Add<UiElement>(tag, null, classNames);

    /// <summary>Adds one of this control's own parts, of a particular type.</summary>
    /// <typeparam name="T">The part's element type.</typeparam>
    /// <param name="tag">Its element name, or <c>null</c> to take the one the type answers to.</param>
    /// <param name="classNames">Any classes it starts with.</param>
    /// <returns>The part.</returns>
    protected T Part<T>(string? tag = null, params string[] classNames) where T : UiElement, new() =>
        Add<T>(tag, null, classNames);

    /// <summary>Raises a <see cref="ClickEvent" /> from this control and tells the direct subscribers.</summary>
    /// <param name="device">What activated it.</param>
    /// <param name="count">How many taps in a row, for a pointer activation.</param>
    /// <param name="modifiers">What was held on the keyboard.</param>
    /// <remarks>
    ///     A disabled control raises nothing, which is belt as well as braces —
    ///     <see cref="Refuse" /> has already stopped every input route that could get here, and this
    ///     covers the one that does not go through a route at all: somebody calling
    ///     <c>Activate</c> on it.
    /// </remarks>
    protected void RaiseClick(ActivationDevice device, int count = 1, ModifierKeys modifiers = ModifierKeys.None) {
        if (Disabled) {
            return;
        }

        Raise(new ClickEvent { Device = device, Count = count, Modifiers = modifiers });
        Clicked?.Invoke(this);
    }

    /// <summary>Raised when the control is activated.</summary>
    /// <remarks>
    ///     The direct form of <see cref="ClickEvent" />, for the caller who has the control in hand
    ///     and wants one line. The routed form is for the caller who has the <i>container</i> in
    ///     hand and does not want twenty subscriptions — which is the case a list, a toolbar or a
    ///     menu is always in.
    /// </remarks>
    public event Action<Control>? Clicked;
}
