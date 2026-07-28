// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>Which edge a drawer comes in from.</summary>
public enum DrawerSide : byte {
    /// <summary>From the left.</summary>
    Left,

    /// <summary>From the right.</summary>
    Right,

    /// <summary>From the top.</summary>
    Top,

    /// <summary>From the bottom.</summary>
    Bottom
}

/// <summary>A panel over the whole window that has to be dealt with before anything else.</summary>
/// <remarks>
///     <para>
///         <b>Modality is three separate things and a control that does two of them is broken.</b>
///         The backdrop stops the pointer reaching what is behind — a real element covering the
///         viewport, not a colour. <see cref="UiElement.IsFocusScope" /> stops Tab walking out into
///         the window behind, which is the half that pointer-only implementations forget. And the
///         focus is moved <i>into</i> the dialog when it opens and put back where it was when it
///         closes, which is the half that everyone forgets.
///     </para>
///     <para>
///         ⚠ <b>The focus is restored to whatever had it, not to whatever opened it.</b> Those are
///         usually the same element and are not always: a dialog opened by a keyboard shortcut was
///         opened by nothing at all. Remembering the focus is the version that works in both cases.
///     </para>
/// </remarks>
public sealed partial class Dialog : Overlay {
    UiElement? restore;

    /// <inheritdoc />
    protected override string TagName => "dialog";

    /// <summary>The sheet that covers what is behind.</summary>
    public UiElement Backdrop { get; private set; } = null!;

    /// <summary>The dialog's own box, inside the backdrop.</summary>
    public UiElement Surface { get; private set; } = null!;

    /// <summary>The strip at the top.</summary>
    public UiElement Header { get; private set; } = null!;

    /// <summary>Where the content goes.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>Where the buttons go.</summary>
    public UiElement Footer { get; private set; } = null!;

    /// <summary>The close button in the header.</summary>
    public IconButton CloseButton { get; private set; } = null!;

    /// <summary>The heading.</summary>
    public string? Title {
        get => TitlePart.Text;
        set => TitlePart.Text = value;
    }

    /// <summary>The heading's element.</summary>
    public UiElement TitlePart { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;

        // ⚠ Light dismiss is off by default and Escape is on. A dialog is a question, and a click
        // that lands anywhere outside it is far more often a miss than an answer — losing what
        // somebody typed into a form because they clicked past its edge is the complaint. Escape is
        // deliberate enough to count.
        LightDismiss = false;

        Backdrop = Part("dialog-backdrop");
        Surface = Part("dialog-surface");

        Header = Surface.Add("dialog-header");
        TitlePart = Header.Add("dialog-title");

        CloseButton = Header.Add<IconButton>();
        CloseButton.LeadingIcon.Geometry = ControlIcons.Close;
        CloseButton.Variant = ControlVariant.Subtle;
        CloseButton.Label = "Close";

        Body = Surface.Add("dialog-body");
        Footer = Surface.Add("dialog-footer");

        AddHandler<ClickEvent>(static (element, args) => ((Dialog) element).Chosen(args));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A dialog is not placed beside anything, so the base class's anchor arithmetic would put
    ///     it wherever the last anchor was. It covers the viewport instead, which the theme arranges
    ///     with <c>position: absolute</c> and four zero insets — so there is nothing to reposition.
    /// </remarks>
    protected override void OnOpened() {
        base.OnOpened();

        restore = Document.Focused;

        // The first thing inside that can take the focus. Falling back to the close button means a
        // dialog with no fields still starts somewhere sensible rather than leaving the keyboard
        // talking to the window behind.
        var stops = UiDocument.TabOrder(this);
        Document.Focus(stops.Count > 0 ? stops[0] : CloseButton);
    }

    /// <inheritdoc />
    protected override void OnClosed(CloseReason reason) {
        base.OnClosed(reason);

        // ⚠ Only if it is still there. A dialog that deleted the thing that opened it — a row's
        // "delete" confirmation — would otherwise put the focus back onto a removed element, and
        // Focus refuses that by throwing rather than by shrugging.
        if (restore is { IsRemoved: false } previous) {
            Document.Focus(previous);
        }

        restore = null;
    }

    void Chosen(ClickEvent args) {
        if (ReferenceEquals(args.Source, CloseButton)) {
            Close(CloseReason.Cancelled);
            args.Handled = true;
        }
    }
}

/// <summary>A panel that slides in from an edge.</summary>
/// <remarks>
///     A dialog attached to a side rather than centred, and the difference that matters is not the
///     animation: a drawer is for a secondary surface — a filter panel, a details pane — that the
///     user may want to leave open while they work, so light dismiss is <i>on</i> here and off on a
///     dialog.
/// </remarks>
public sealed partial class Drawer : Overlay {
    /// <inheritdoc />
    protected override string TagName => "drawer";

    /// <summary>The sheet behind it.</summary>
    public UiElement Backdrop { get; private set; } = null!;

    /// <summary>The panel itself.</summary>
    public UiElement Surface { get; private set; } = null!;

    /// <summary>Where the content goes.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>Which edge it comes in from.</summary>
    /// <remarks>
    ///     Written through to a class — <c>side-left</c> — because which edge a drawer is attached
    ///     to is entirely a matter of four insets and a width, all of which are the theme's.
    /// </remarks>
    [UiProperty(Changed = nameof(OnSideChanged))]
    public partial DrawerSide Side { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;

        Backdrop = Part("drawer-backdrop");
        Surface = Part("drawer-surface");
        Body = Surface.Add("drawer-body");

        AddClass(ClassOf(Side));
    }

    static string ClassOf(DrawerSide side) =>
        side switch {
            DrawerSide.Right => "side-right",
            DrawerSide.Top => "side-top",
            DrawerSide.Bottom => "side-bottom",
            _ => "side-left"
        };

    void OnSideChanged(DrawerSide previous, DrawerSide current) {
        RemoveClass(ClassOf(previous));
        AddClass(ClassOf(current));
    }
}
