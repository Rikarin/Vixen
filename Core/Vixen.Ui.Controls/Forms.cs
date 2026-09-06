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

/// <summary>A titled box round a set of related controls, announced as one group.</summary>
/// <remarks>
///     <para>
///         <b>The other half of doc 49 § 7.1's rank 4, and the half that is a container.</b>
///         <see cref="LabeledContent" /> is the row; this is what a set of rows is put inside.
///         <see cref="Card" /> and <see cref="Panel" /> already draw the picture — a bordered box
///         with a heading in it — and are exempt from the accessibility sweep for saying so: they
///         are layout, and announcing them would put a group round every four fields. That is the
///         right decision for a box that happens to have a border and the wrong one for a box whose
///         whole purpose is to say <i>these belong together</i>.
///     </para>
///     <para>
///         ⚠ <b>The role is the reason this type exists, and the border is not.</b> This is HTML's
///         <c>&lt;fieldset&gt;</c> with its <c>&lt;legend&gt;</c>: a screen reader entering it says
///         the legend and then the controls, so somebody who arrives at the third one down is told
///         what question it answers. A <see cref="Card" /> with a <see cref="TextBlock" /> in its
///         header draws the same thing and says nothing — those words are read when a reader walks
///         past them and never again, and a keyboard user who tabbed straight into the group never
///         walked past them at all.
///     </para>
///     <para>
///         ⚠ <b>The name is written on the group rather than fetched from the legend by a
///         relation</b>, which is the opposite of what <see cref="LabeledContent" /> does and for a
///         difference that matters. A row's caption names a control the row does not own, so the
///         join has to be a relation; a legend is part of this control, and a fieldset's own name is
///         computed from it. One string, one copy, and no second node carrying the same words.
///     </para>
///     <para>
///         ⚠ <b>An unnamed group is still a group.</b> Reporting <see cref="AccessibleRole.None" />
///         until somebody sets <see cref="Label" /> would be a role that moves under a property —
///         nothing could rely on it, and the coverage sweep could not see it either, because it
///         builds one bare instance of every type and reads the answer once. A group with nothing to
///         say is a caller reaching for the wrong container, and <see cref="Panel" /> is the right
///         one.
///     </para>
///     <para>
///         <b>What it deliberately is not.</b> It does not collapse: that is
///         <see cref="Expander" />, whose header is a button that says what it opens, and a
///         container with both behaviours would offer two ways to hide the same content. It is also
///         not the <c>Form</c> or the <c>Section</c> doc 49 ranks beside it — a form is a submission
///         and a section is a document landmark, and neither of those is a bordered box with a
///         caption. Both are still owed.
///     </para>
/// </remarks>
public sealed partial class GroupBox : Control {
    /// <inheritdoc />
    protected override string TagName => "group-box";

    /// <inheritdoc />
    /// <remarks>The controls inside it are the stops. A group is a container, not a widget.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Group;

    /// <inheritdoc />
    /// <remarks>
    ///     The legend's words, read off the property rather than out of the element, so that a group
    ///     whose caption is drawn by something else still says what it is.
    /// </remarks>
    protected override string? NativeAccessibleName => Label;

    /// <summary>Where the caption is drawn.</summary>
    public UiElement Legend { get; private set; } = null!;

    /// <summary>Where the controls go.</summary>
    public UiElement Content { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Content" />, so that a nested tag means what it looks like. The null guard is
    ///     <see cref="Card.ContentHost" />'s and is load-bearing for the same reason: this is read
    ///     before <see cref="OnCreated" /> has run.
    /// </remarks>
    protected override UiElement ContentHost => Content ?? this;

    /// <summary>What the legend says, or <c>null</c> for a box with no caption.</summary>
    [UiProperty(Changed = nameof(OnLabelChanged))]
    public partial string? Label { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Legend = Part("group-legend");
        Legend.SetStyle("display", "none");

        Content = Part("group-content");
    }

    void OnLabelChanged(string? previous, string? current) {
        Legend.Text = current;

        // ⚠ `display: none` rather than an empty element, for `LabeledContent.Message`'s reason: a
        // hidden flex item is not an item, so a group with no caption is exactly as tall as its
        // contents, where one left in the flow takes the column's `gap` and inserts a blank line
        // above the first control.
        Legend.SetStyle("display", string.IsNullOrEmpty(current) ? "none" : "flex");

        // The group's own name just moved. `AccessibleName` is computed on read, so this is for the
        // platform bridge rather than for the getter — it sets the flag the document clears once a
        // frame.
        InvalidateAccessibility();
    }
}
