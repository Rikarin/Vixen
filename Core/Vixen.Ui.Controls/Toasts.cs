// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>A brief message that appears in a corner and goes away.</summary>
/// <remarks>
///     ⚠ <b>It does not dismiss itself, because nothing here has a clock.</b>
///     <see cref="ToastHost.Tick" /> is what expires it, on the same terms as
///     <c>GestureRecognizer.Tick</c> and <see cref="Tooltip.Tick" /> — a host with a frame loop
///     calls it, and one that does not gets toasts that stay until they are dismissed. Which is a
///     defensible interface in its own right and is not what most callers will expect, so it is
///     said here rather than left to be discovered.
/// </remarks>
public sealed partial class Toast : Control {
    Icon? icon;

    /// <inheritdoc />
    protected override string TagName => "toast";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>How long it stays before <see cref="ToastHost.Tick" /> takes it away.</summary>
    [UiProperty]
    public partial TimeSpan Duration { get; set; }

    /// <summary>When it appeared, on the host's clock.</summary>
    internal TimeSpan Shown { get; set; }

    /// <summary>What it says.</summary>
    public string? Message {
        get => MessagePart.Text;
        set => MessagePart.Text = value;
    }

    /// <summary>The message's element.</summary>
    public UiElement MessagePart { get; private set; } = null!;

    /// <summary>The button that takes it away early.</summary>
    public IconButton CloseButton { get; private set; } = null!;

    /// <summary>An icon before the message, created the first time it is asked for.</summary>
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

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        MessagePart = Part("toast-message");

        CloseButton = Part<IconButton>();
        CloseButton.LeadingIcon.Geometry = ControlIcons.Close;
        CloseButton.Variant = ControlVariant.Subtle;
        CloseButton.Label = ControlStrings.ToastDismiss.Text;
    }
}

/// <summary>The corner of the screen toasts stack up in.</summary>
/// <remarks>
///     <para>
///         A host rather than a static queue, because a document may have more than one — an editor
///         with two windows has two corners, and a global list would put both windows' messages in
///         whichever one happened to be created first.
///     </para>
///     <para>
///         ⚠ <b>Newest at the top, and it is the opposite of what a naive append gives.</b> A toast
///         added to the end of a bottom-anchored stack pushes the older ones <i>up</i>, which moves
///         a message somebody is halfway through reading. Adding at the front leaves them where they
///         are — which is why this moves each new toast to index zero rather than letting it land at
///         the end.
///     </para>
/// </remarks>
public sealed partial class ToastHost : Control {
    readonly List<Toast> live = [];

    /// <inheritdoc />
    protected override string TagName => "toast-host";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The toasts currently showing, newest first.</summary>
    public IReadOnlyList<Toast> Live => live;

    Action<UiDocument, TimeSpan>? ticked;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<ClickEvent>(static (element, args) => ((ToastHost) element).Dismissed(args));

        // The document's clock, which a host with a frame loop advances. Held in a field so that
        // `OnRemoved` can take it off again: a host removed from its document must stop expiring
        // toasts, and an event subscription is a reference the document would otherwise keep.
        ticked = (_, now) => Tick(now);
        Document.Ticked += ticked;
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (ticked is not null) {
            Document.Ticked -= ticked;
            ticked = null;
        }

        base.OnRemoved();
    }

    /// <summary>Shows a message.</summary>
    /// <param name="message">What it says.</param>
    /// <param name="variant">What sort of message it is.</param>
    /// <param name="duration">How long it stays, or zero for the default of four seconds.</param>
    /// <returns>The toast, for a caller that wants to add a button to it.</returns>
    public Toast Show(string? message, ControlVariant variant = ControlVariant.Default, TimeSpan duration = default) {
        var toast = Add<Toast>();
        toast.Message = message;
        toast.Variant = variant;
        toast.Duration = duration == TimeSpan.Zero ? TimeSpan.FromSeconds(4) : duration;

        Document.Move(toast, 0);
        live.Insert(0, toast);

        // ⚠ Stamped now rather than at the next tick, which is a real difference and not tidiness: a
        // toast shown just after a tick would otherwise be given the *following* frame's time and
        // live a frame longer than the one shown just before it. `Tick` keeps its own fallback for a
        // document whose clock has never been set, where this is still zero.
        toast.Shown = Document.Now;

        return toast;
    }

    /// <summary>Takes one away.</summary>
    /// <param name="toast">The toast.</param>
    /// <returns>Whether it was one of this host's.</returns>
    public bool Dismiss(Toast toast) {
        ArgumentNullException.ThrowIfNull(toast);

        if (!live.Remove(toast)) {
            return false;
        }

        toast.Remove();
        return true;
    }

    /// <summary>Tells the host what time it is, so that expired toasts go away.</summary>
    /// <param name="now">The current time, on the same clock as the input events.</param>
    /// <remarks>
    ///     ⚠ <b>The first tick a toast sees is what starts its clock</b>, rather than the moment it
    ///     was created — because the moment it was created is a time nothing here knows. A toast
    ///     shown in a host that is never ticked therefore never starts and never expires, which is
    ///     the same fallback the rest of the timed behaviour in this assembly has.
    /// </remarks>
    public void Tick(TimeSpan now) {
        for (var i = live.Count - 1; i >= 0; i--) {
            var toast = live[i];

            if (toast.Shown == TimeSpan.Zero) {
                toast.Shown = now;
                continue;
            }

            if (now - toast.Shown >= toast.Duration) {
                Dismiss(toast);
            }
        }
    }

    void Dismissed(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (element is Toast toast) {
                Dismiss(toast);
                args.Handled = true;

                return;
            }
        }
    }
}
