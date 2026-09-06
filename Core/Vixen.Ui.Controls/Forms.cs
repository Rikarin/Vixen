// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>A caption, the control it names, and somewhere to say what went wrong.</summary>
/// <remarks>
///     <para>
///         <b>The row every form is made of, and the one shape this control set had no way to
///         say.</b> Doc 49 § 7.1 ranks <c>LabeledContent</c> beside <c>GroupBox</c>, <c>Form</c> and
///         <c>Section</c> as rank 4 of the missing controls, and notes that <see cref="Card" /> and
///         <see cref="KeyValueList" /> only approximate it. What they approximate is the
///         <i>picture</i>; what none of them has is the join.
///     </para>
///     <para>
///         ⚠ <b>Two <c>div</c>s side by side is not this, and the difference is that a screen reader
///         reads out an unnamed field.</b> A <see cref="TextBox" />, a <see cref="NumericInput" />, a
///         <see cref="Slider" /> and a <see cref="Select" /> all deliberately answer <c>null</c> to
///         their native accessible name — a placeholder is a hint and a number is not a name — so a
///         form of eight fields beside a column of words is eight unnamed fields, and nothing in the
///         tree connects the two. <c>PropertyGrid</c> already knew this and writes the relation by
///         hand for every row it builds (<c>AddAccessibleRelation(LabelledBy, row.Label)</c>); an
///         application outside that grid had no way to. This control is that line, made the
///         container's job so that it cannot be the caller's to forget.
///     </para>
///     <para>
///         ⚠ <b>And the message is a part rather than advice.</b> <see cref="TextField" />'s own
///         remarks say plainly that <c>ValidationMessage</c> is deliberately not written into the
///         accessibility tree, because ARIA pairs <c>aria-invalid</c> with a <i>separate</i> element
///         holding the words reached by <c>aria-describedby</c> — "the error text a form shows is a
///         label somewhere in the layout". <see cref="Description" /> is that element, and the
///         relation to it is written for the same reason the label's is.
///     </para>
///     <para>
///         ⚠ <b>A caption is clickable, which is the half a container cannot get from CSS.</b> HTML's
///         <c>&lt;label for&gt;</c> focuses its control on a click and every desktop toolkit does the
///         same; a tick box with a three-word label beside it is a four-pixel target without it.
///     </para>
///     <para>
///         <b>What it deliberately is not.</b> It has no <c>Required</c> and computes no verdict:
///         those belong to the field, which already reports both — mirroring them here would be a
///         second copy of a fact, and <c>FieldValidity</c>'s remarks say why the count of copies is
///         the thing to keep at one. A row that wants an asterisk reads <c>:required</c> off its
///         field with a sibling selector.
///     </para>
/// </remarks>
public sealed partial class LabeledContent : Control {
    /// <inheritdoc />
    protected override string TagName => "labeled-content";

    /// <inheritdoc />
    /// <remarks>The field inside it is the stop. A row is a layout, not a control.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Nothing, and that is the arrangement rather than an omission</b>, on
    ///     <see cref="ComboBox.NativeRole" />'s terms. This element stands for a caption and a
    ///     control that are each already in the tree, joined by a relation; a role here would put a
    ///     third node between a screen reader and the field, named by the same words the relation
    ///     already carries.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.None;

    /// <summary>Where the caption is drawn.</summary>
    public UiElement Caption { get; private set; } = null!;

    /// <summary>Where the field goes.</summary>
    public UiElement Content { get; private set; } = null!;

    /// <summary>Where the message under the field is drawn.</summary>
    /// <remarks>
    ///     Always present and hidden while it says nothing — <c>display: none</c> rather than an
    ///     element created on demand, because an element that arrives later arrives <i>after</i> the
    ///     field it has to be related to, and the relation would then have to be rewritten for every
    ///     field already in the row.
    /// </remarks>
    public UiElement Message { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Content" />, so that <c>&lt;LabeledContent&gt;&lt;TextBox /&gt;…</c> means what
    ///     it looks like. The null guard is <see cref="Card.ContentHost" />'s and is load-bearing for
    ///     the same reason: this is read before <see cref="OnCreated" /> has run.
    /// </remarks>
    protected override UiElement ContentHost => Content ?? this;

    /// <summary>What the caption says.</summary>
    [UiProperty(Changed = nameof(OnLabelChanged))]
    public partial string? Label { get; set; }

    /// <summary>The help or error text under the field, or <c>null</c> for none.</summary>
    /// <remarks>
    ///     ⚠ <b>One property for help and for the error, deliberately.</b> They are the same element
    ///     in the same place saying the same kind of thing, and a row with both would put two lines
    ///     under one field where the second contradicts the first. A form that has just been refused
    ///     writes the field's <see cref="TextField.ValidationMessage" /> here and puts the hint back
    ///     when it clears.
    /// </remarks>
    [UiProperty(Changed = nameof(OnDescriptionChanged))]
    public partial string? Description { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Caption = Part("field-label");

        var content = Part<FieldContent>();
        content.Owner = this;
        Content = content;

        Message = Part("field-message");
        Message.SetStyle("display", "none");

        // ⚠ A tap and not a `ClickEvent`. `ClickEvent` is raised *by a control that chose to* —
        // `Control.RaiseClick` — and a caption is a bare part, so a row listening for one hears
        // nothing at all and the label silently does not work. The gesture is what actually
        // happened, and it routes through the tree from whatever was under the pointer.
        AddHandler<TapEvent>(static (element, args) => ((LabeledContent)element).Pressed(args));
    }

    /// <summary>Joins a field in this row to the caption and the message.</summary>
    /// <param name="field">The field.</param>
    /// <remarks>
    ///     ⚠ <b>Public because a row does not own every route a child arrives by.</b> A field
    ///     reparented into <see cref="Content" /> — which is what a docking host, a virtualised list
    ///     and a hot reload all do — was never <i>added</i>, and <c>UiElement.OnChildAdded</c> is
    ///     creation only and says so. The relations are idempotent, so calling this on a field that
    ///     already has them is free.
    /// </remarks>
    public void Adopt(UiElement field) {
        ArgumentNullException.ThrowIfNull(field);

        field.AddAccessibleRelation(AccessibleRelation.LabelledBy, Caption);
        field.AddAccessibleRelation(AccessibleRelation.DescribedBy, Message);
    }

    /// <summary>The first thing in the row that can take the focus.</summary>
    public UiElement? Field => First(Content);

    void OnLabelChanged(string? previous, string? current) => Caption.Text = current;

    void OnDescriptionChanged(string? previous, string? current) {
        Message.Text = current;

        // ⚠ `display: none` rather than an empty box, and the reason is the gap. A hidden flex item
        // is not an item at all, so a row with no message is exactly as tall as it was; an element
        // left in the flow with no text in it still takes the column's `gap` and pushes the next row
        // down by it.
        Message.SetStyle("display", string.IsNullOrEmpty(current) ? "none" : "flex");
    }

    /// <remarks>
    ///     ⚠ <b>The caption only, and not the row.</b> A click anywhere in the row moving the focus
    ///     would steal a drag that started on a slider's track and would fight a text field's own
    ///     caret placement — the affordance being copied is <c>&lt;label for&gt;</c>, which is the
    ///     words and not the space around them.
    /// </remarks>
    void Pressed(TapEvent args) {
        if (args.Source is not { } source || !Within(source, Caption) || Field is not { } field) {
            return;
        }

        Document.Focus(field);
        args.Handled = true;
    }

    static bool Within(UiElement element, UiElement ancestor) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, ancestor)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first focusable element under one, in document order.</summary>
    static UiElement? First(UiElement parent) {
        foreach (var child in parent.Children) {
            if (child.Focusable) {
                return child;
            }

            if (First(child) is { } deeper) {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>The panel a row's field sits in, which exists to forward what lands in it.</summary>
    /// <remarks>
    ///     ⚠ <b>Typed, with the same tag as the plain part it replaces</b>, on
    ///     <c>Popover.PopoverContent</c>'s terms and for its reason: <c>OnChildAdded</c> fires on the
    ///     element a child was added to, and that element is this part rather than the row. A row
    ///     that overrode its own would place a nested <c>&lt;TextBox /&gt;</c> correctly through
    ///     <see cref="ContentHost" /> and never hear that it had arrived — so the field would draw,
    ///     and be as unnamed as it was before this control existed.
    /// </remarks>
    sealed partial class FieldContent : UiElement {
        /// <summary>The row this is the content of.</summary>
        internal LabeledContent? Owner { get; set; }

        /// <inheritdoc />
        protected override string TagName => "field-content";

        /// <inheritdoc />
        protected override void OnChildAdded(UiElement child) {
            base.OnChildAdded(child);
            Owner?.Adopt(child);
        }
    }
}
