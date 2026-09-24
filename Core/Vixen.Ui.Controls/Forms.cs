// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

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
///         not the <see cref="Form" /> or the <see cref="Section" /> doc 49 ranks beside it — a form
///         is a submission and a section is a document landmark, and neither of those is a bordered
///         box with a caption. Both are below.
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

/// <summary>A set of fields that is submitted as one, and refuses to be while any of them is wrong.</summary>
/// <remarks>
///     <para>
///         <b>The submission half of doc 49 § 7.1's rank 4.</b> <see cref="LabeledContent" /> is the
///         row and <see cref="GroupBox" /> the box round a question; neither knows that the rows are
///         <i>sent</i>. Every field here already validates itself — <see cref="TextField" />,
///         <see cref="Select" />, <see cref="MultiSelect" />, <see cref="ComboBox" />,
///         <see cref="CheckBox" /> and <see cref="RadioGroup" /> each answer <c>IsValid</c> and
///         <c>Revalidate()</c> — and nothing asked all of them at once, so an application's OK button
///         had to know every field it sat under.
///     </para>
///     <para>
///         ⚠ <b>HTML's three submission rules, and each is a place an application used to write the
///         form by hand.</b> Enter in a field that does not want it for a line break submits
///         (<i>implicit submission</i> — heard as the routed <see cref="SubmitEvent" /> a field raises,
///         so the field decides what Enter means and a text area keeps its line breaks); a click on
///         the form's default button submits; and a submission with a field that will not take its
///         value is refused, the <i>first</i> such field in document order is focused, and every
///         field is marked as having been through a submission — which is what lets a
///         <c>:user-invalid</c> rule match the required field nobody reached, exactly as a browser's
///         does after the first press of Submit.
///     </para>
///     <para>
///         ⚠ <b>A field belongs to the nearest form above it</b>, which is HTML's <i>form owner</i>.
///         A form inside a form — which HTML forbids and a composed panel can still produce — does not
///         validate its inner form's fields, and a submission that started inside the inner one is
///         the inner one's and not also the outer's. A disabled field, or one under a disabled
///         control, is not validated: it is barred from constraint validation, as a disabled
///         <c>&lt;input&gt;</c> is.
///     </para>
///     <para>
///         ⚠ <b>The event is not marked handled.</b> A dialog that closes on its field's
///         <see cref="TextField.Submitted" />, or an ancestor listening for the routed event, still
///         hears the key; the form's answer is <see cref="Submitted" /> and <see cref="Submit" />'s
///         return value, and whether the dialog should have closed on an invalid form is the
///         dialog's question.
///     </para>
///     <para>
///         <b>What it deliberately is not.</b> It draws nothing and has no title: a form's heading is
///         the panel's, and its accessible name — which makes it a <c>form</c> landmark a screen
///         reader can jump to — is <see cref="UiElement.AccessibleName" />, the property every element
///         already has. It holds no values either; the fields do, bound to the application's model.
///     </para>
/// </remarks>
public sealed partial class Form : Control {
    /// <inheritdoc />
    protected override string TagName => "form";

    /// <inheritdoc />
    /// <remarks>The fields are the stops. A form is a container, not a widget.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The first producer of <see cref="AccessibleRole.Form" /> in the tree.</b> The member
    ///     has existed as long as the enumeration has, and nothing reported it — so a screen reader
    ///     could not jump to the form in a settings window as it can to one on a web page. ARIA
    ///     exposes an unnamed <c>form</c> as a plain group, so a caller who wants the landmark names
    ///     it.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Form;

    /// <summary>Raised when a submission is accepted: every field in the form took its value.</summary>
    public event Action<Form>? Submitted;

    /// <summary>The fields this form validates, in document order.</summary>
    /// <remarks>
    ///     A fresh walk each time, on <c>SelectBase.Options</c>' terms: a list kept here would be a
    ///     second place the truth lived, and fields arrive by every route a panel can build them.
    /// </remarks>
    public IReadOnlyList<Control> Fields {
        get {
            var fields = new List<Control>();
            Collect(this, fields);

            return fields;
        }
    }

    /// <summary>Validates every field and, if all of them take their values, raises <see cref="Submitted" />.</summary>
    /// <returns>Whether the submission was accepted.</returns>
    /// <remarks>
    ///     Public because a submission is not always a key or a button — a toolbar command, a timer
    ///     and a test all submit. Refusing moves the focus to the first field that said no, so the
    ///     keyboard is where the correction is to be made.
    /// </remarks>
    public bool Submit() {
        Control? refused = null;

        foreach (var field in Fields) {
            var validated = (IValidated) field;
            validated.Revalidate();

            // ⚠ The bit `:user-invalid` waits for, set on the field and on whatever in it takes the
            // focus. A required field nobody reached has been answered by the submission — with
            // nothing — and a browser shows it as wrong from here on; before this the only way the
            // bit arrived was the user editing that very field.
            field.State |= ElementState.UserInteracted;

            if (Target(field) is { } target && !ReferenceEquals(target, field)) {
                target.State |= ElementState.UserInteracted;
            }

            if (!validated.IsValid) {
                refused ??= field;
            }
        }

        if (refused is not null) {
            if (Target(refused) is { } target) {
                Document.Focus(target);
            }

            return false;
        }

        Submitted?.Invoke(this);

        return true;
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<SubmitEvent>(static (element, args) => ((Form) element).Heard(args.Source));

        AddHandler<ClickEvent>(static (element, args) => {
            if (args.Source is Button { IsDefault: true } button) {
                ((Form) element).Heard(button);
            }
        });
    }

    void Heard(UiElement? source) {
        if (source is not null && ReferenceEquals(OwnerOf(source), this)) {
            Submit();
        }
    }

    /// <summary>The nearest form above an element, which is the one it belongs to.</summary>
    static Form? OwnerOf(UiElement element) {
        for (var walk = element.Parent; walk is not null; walk = walk.Parent) {
            if (walk is Form form) {
                return form;
            }
        }

        return null;
    }

    /// <summary>Gathers the validating fields under one element, stopping at a nested form and at anything disabled.</summary>
    static void Collect(UiElement parent, List<Control> fields) {
        foreach (var child in parent.Children) {
            if (child is Form || child is Control { Disabled: true }) {
                continue;
            }

            if (child is IValidated and Control field) {
                fields.Add(field);

                // A validating field answers for what it is made of — a combo box's editor is the
                // box's verdict, not a second one.
                continue;
            }

            Collect(child, fields);
        }
    }

    /// <summary>What to focus to put the keyboard in a field: the field, or the first stop inside it.</summary>
    static UiElement? Target(UiElement field) {
        if (field.Focusable) {
            return field;
        }

        foreach (var child in field.Children) {
            if (Target(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}

/// <summary>A titled part of a page that a screen reader can jump to: a heading, and what it heads.</summary>
/// <remarks>
///     <para>
///         <b>The landmark half of doc 49 § 7.1's rank 4.</b> <see cref="GroupBox" /> says <i>these
///         controls answer one question</i>; <see cref="Form" /> says <i>these fields are sent
///         together</i>; this says <i>this is a part of the page, and here is its name</i>. It is
///         HTML's <c>&lt;section aria-labelledby&gt;</c> with its <c>&lt;h2&gt;</c>, and it is what a
///         settings window made of "Display", "Audio" and "Controls" is built from.
///     </para>
///     <para>
///         ⚠ <b>Two nodes in the tree, deliberately, where <see cref="GroupBox" /> has one.</b> A
///         screen-reader user moves through a long page in two ways — by landmark and by heading —
///         and a section has to be reachable by both. So the heading is a real
///         <see cref="AccessibleRole.Heading" /> node, and the section is a
///         <see cref="AccessibleRole.Region" /> <i>named by it</i> through
///         <see cref="AccessibleRelation.LabelledBy" />, which is ARIA's own pattern. A group box's
///         legend is not a heading anybody navigates to, so there the name is written on the group
///         and the legend stays a caption; here the relation is what keeps the one string in one
///         place while two nodes read it.
///     </para>
///     <para>
///         ⚠ <b>Unnamed, it is still a region — and an unnamed region is a defect the tree reports
///         rather than hides.</b> ARIA exposes a <c>&lt;section&gt;</c> with no name as a plain
///         generic, so a role that appeared only once a title was set would look correct and make
///         the omission invisible: <c>AccessibilitySnapshot.Unnamed</c> cannot see a landmark that is
///         not there. Kept a region, a titleless section is an unnamed node that gate reports, which
///         is where <see cref="GroupBox" /> came out for the same reason. An application that wants
///         the landmark without a visible heading sets <see cref="UiElement.AccessibleName" />, which
///         wins over the relation.
///     </para>
///     <para>
///         ⚠ <b>The heading's role does move with <see cref="Title" />, and that is the other answer
///         on purpose.</b> The heading part is hidden when there is no title, and a hidden part is
///         still walked by the accessibility tree — <c>display</c> is a picture, not a statement
///         about the tree — so an empty heading would be announced as a heading with no name. No
///         title means no heading; the section it belongs to is what stays put.
///     </para>
///     <para>
///         <b>What it deliberately is not.</b> It draws no border: it is a part of a page rather than
///         a box on one, and a settings window framing every section would read as a stack of cards.
///         It does not collapse — that is <see cref="Expander" />. And there is no heading
///         <i>level</i>: the accessibility model has no <c>aria-level</c> to carry one, so a section
///         inside a section is announced as a heading exactly like its parent's. That is recorded
///         here rather than implied by a smaller font.
///     </para>
///     <para>
///         ⚠ <b>Nest in markup, or add to <see cref="Content" /> from C#.</b> A nested tag goes to
///         <see cref="ContentHost" />; <c>section.Add&lt;T&gt;()</c> parents on the section itself,
///         beside the heading, as it does for every container in this assembly.
///     </para>
/// </remarks>
public sealed partial class Section : Control {
    /// <inheritdoc />
    protected override string TagName => "section";

    /// <inheritdoc />
    /// <remarks>The controls inside it are the stops. A section is a part of a page, not a widget.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The first producer of <see cref="AccessibleRole.Region" /> in the control set.</b> The
    ///     member has existed as long as the enumeration has, and the only way to reach it was
    ///     <c>panel.Role = AccessibleRole.Region</c> by hand, which is what <see cref="Panel" />'s own
    ///     comment told an application to write.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Region;

    /// <summary>Where the title is drawn, and the heading a screen reader navigates to.</summary>
    public UiElement Heading { get; private set; } = null!;

    /// <summary>Where the section's controls go.</summary>
    public UiElement Content { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Content" />, so that a nested tag means what it looks like. The null guard is
    ///     <see cref="Card.ContentHost" />'s and is load-bearing for the same reason: this is read
    ///     before <see cref="OnCreated" /> has run.
    /// </remarks>
    protected override UiElement ContentHost => Content ?? this;

    /// <summary>What the heading says, which is also the section's name; <c>null</c> for no heading.</summary>
    [UiProperty(Changed = nameof(OnTitleChanged))]
    public partial string? Title { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Heading = Part("section-heading");
        Heading.Role = AccessibleRole.None;
        Heading.SetStyle("display", "none");

        Content = Part("section-content");

        // The region's name is the heading's words, read through the relation on every ask — so
        // there is one copy of the title, and it is the one on screen.
        AddAccessibleRelation(AccessibleRelation.LabelledBy, Heading);
    }

    void OnTitleChanged(string? previous, string? current) {
        var titled = !string.IsNullOrEmpty(current);

        Heading.Text = current;

        // ⚠ `display: none` for `LabeledContent.Message`'s reason — a hidden flex item takes no
        // `gap` — and the role with it, for the reason in the class remarks: the tree walks hidden
        // parts, so a heading with nothing to say would be announced as an unnamed heading.
        Heading.SetStyle("display", titled ? "flex" : "none");
        Heading.Role = titled ? AccessibleRole.Heading : AccessibleRole.None;

        // The section's own name just moved too. `AccessibleName` is computed on read, so this is
        // for the platform bridge rather than the getter.
        InvalidateAccessibility();
    }
}
