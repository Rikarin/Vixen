// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>Which way round something goes.</summary>
public enum Orientation : byte {
    /// <summary>Along the inline axis.</summary>
    Horizontal,

    /// <summary>Down the block axis.</summary>
    Vertical
}

/// <summary>A box that groups things, and nothing more.</summary>
/// <remarks>
///     The one control with no behaviour at all. It exists because <c>panel</c> is a name a
///     stylesheet can select on and <c>div</c> is a name everything uses — a theme that styled every
///     <c>div</c> would style the inside of every other control in this assembly.
/// </remarks>
public sealed partial class Panel : Control {
    /// <inheritdoc />
    protected override string TagName => "panel";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    // ⚠ No role, and that is the population's answer rather than a gap in it. A `Panel` is a box; a
    // tree that reported one would read a four-field form as a stack of nested groups, which is the
    // commonest way an accessibility tree comes to be technically complete and useless. An
    // application that means a landmark says so: `panel.Role = AccessibleRole.Region` and a name.
}

/// <summary>A surface with an optional header and footer.</summary>
/// <remarks>
///     ⚠ <b>The body exists from the start and the other two do not.</b> A card is nearly always
///     just a body, and an empty header still costs its padding. Asking for <see cref="Header" /> is
///     what creates it, which makes "has a header" and "was given a header" the same fact — so a
///     theme does not have to notice the difference. It now <i>can</i>: <c>:empty</c> is in the
///     selector language, and <c>card-header:empty { display: none }</c> collapses one that was
///     asked for and then never filled. This class does not write that rule, because creating a part
///     and leaving it bare is a caller's mistake rather than a state to style around.
/// </remarks>
public sealed partial class Card : Control {
    UiElement? header;
    UiElement? footer;

    /// <inheritdoc />
    protected override string TagName => "card";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the card's content goes.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>So that <c>&lt;Card&gt;…&lt;/Card&gt;</c> in markup means what it looks like.</b>
    ///     Without this a nested tag lands on the card itself rather than in the body, which is one
    ///     element outside the padding and outside <c>card-body</c>'s <c>gap</c> — a card whose
    ///     contents are visibly not in it, with nothing to say so. It is <see cref="Body" /> rather
    ///     than the header because a card is nearly always just a body; a header is one line of
    ///     C# through <see cref="Header" />, or its own tag once one exists.
    ///     <para>
    ///         ⚠ The null guard is <c>Tabs</c>' and it is load-bearing for the same reason:
    ///         <c>ContentHost</c> can be read before <see cref="OnCreated" /> has run, and answering
    ///         with an uninitialised <see cref="Body" /> is a null reference at the first nested tag.
    ///     </para>
    /// </remarks>
    protected override UiElement ContentHost => Body ?? this;

    /// <summary>The strip above the body, created the first time it is asked for.</summary>
    public UiElement Header => header ??= Prepend("card-header");

    /// <summary>The strip below it, created the first time it is asked for.</summary>
    public UiElement Footer => footer ??= Part("card-footer");

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        Body = Part("card-body");
    }

    /// <summary>Adds a part and puts it first.</summary>
    /// <remarks>
    ///     A new child lands at the end, which is right for the footer and wrong for the header
    ///     whatever order the two were asked for in. <c>Move</c> touches all three stores, so the
    ///     header is above the body for the cascade's <c>:first-child</c> as well as for flexbox.
    /// </remarks>
    UiElement Prepend(string tag) {
        var part = Part(tag);
        Document.Move(part, 0);

        return part;
    }
}

/// <summary>A rule between two groups of things.</summary>
public sealed partial class Separator : Control {
    /// <inheritdoc />
    protected override string TagName => "separator";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>separator</c>, which is one of the few roles worth giving a thing that cannot be
    ///     operated: a screen reader reading a menu straight through announces the divisions between
    ///     its groups, and without this the groups are a visual fact only.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Separator;

    /// <summary>Which way it runs.</summary>
    /// <remarks>
    ///     Written through to a class, like every other appearance on a control — a horizontal rule
    ///     and a vertical one differ in which of two dimensions is one pixel, and that is a
    ///     stylesheet's sentence rather than a property this reads.
    /// </remarks>
    [UiProperty(Changed = nameof(OnOrientationChanged))]
    public partial Orientation Orientation { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddClass(ClassOf(Orientation));
    }

    internal static string ClassOf(Orientation orientation) =>
        orientation == Orientation.Vertical ? "vertical" : "horizontal";

    void OnOrientationChanged(Orientation previous, Orientation current) {
        RemoveClass(ClassOf(previous));
        AddClass(ClassOf(current));
    }
}

/// <summary>Something the user needs to be told, in place rather than in a dialog.</summary>
/// <remarks>
///     Doc 09 lists this as "Alert/Callout" — one control, because the difference between the two
///     names in every library that has both is which one has an icon, and that is a property rather
///     than a type. <see cref="Control.Variant" /> is what says whether it is information, a warning
///     or a failure.
/// </remarks>
public sealed partial class Alert : Control {
    Icon? icon;

    /// <inheritdoc />
    protected override string TagName => "alert";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The heading, if it has one.</summary>
    public string? Title {
        get => TitlePart.Text;
        set => TitlePart.Text = value;
    }

    /// <summary>What it says.</summary>
    public string? Message {
        get => MessagePart.Text;
        set => MessagePart.Text = value;
    }

    /// <summary>The heading's element.</summary>
    public UiElement TitlePart { get; private set; } = null!;

    /// <summary>The message's element.</summary>
    public UiElement MessagePart { get; private set; } = null!;

    /// <summary>Where the content sits, so that a caller can add a button to it.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>An icon before the text, created the first time it is asked for.</summary>
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
    /// <remarks>
    ///     ARIA <c>alert</c>, which is a live region: assistive technology announces one the moment
    ///     it appears rather than waiting to be walked to. That is what this control is for, and it
    ///     is why the role is worth having on something nobody can operate.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Alert;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Title" />, and the message is the <i>description</i> rather than part of the
    ///     name — see <see cref="OnCreated" />. An alert given only a message has no name and a
    ///     description, which is the honest reading of an alert that is one sentence: saying the
    ///     sentence twice is what a name-and-description built from the same element does.
    /// </remarks>
    protected override string? NativeAccessibleName => Title;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Body = Part("alert-body");
        TitlePart = Body.Add("alert-title");
        MessagePart = Body.Add("alert-message");

        // ⚠ **A relation rather than a second name property, and this is the first thing in the set
        // to use `DescribedBy` at all.** `AccessibleDescription` had a working path and no control
        // fed it — which is this repository's commonest defect one level up: a finished consumer
        // nothing calls. A heading and the sentence under it are exactly what the pair is for, and
        // reading it through the relation means a message changed later is described correctly with
        // nothing to keep in step.
        AddAccessibleRelation(AccessibleRelation.DescribedBy, MessagePart);
    }
}

/// <summary>What a list shows when it has nothing to show.</summary>
/// <remarks>
///     ⚠ <b>Worth a control because the alternative is a blank rectangle</b>, which every user reads
///     as a bug in the application rather than as an absence of data. The three parts below are what
///     makes it not one: something to look at, a sentence saying what is missing, and somewhere to
///     put the button that fixes it.
/// </remarks>
public sealed partial class EmptyState : Control {
    Icon? icon;

    /// <inheritdoc />
    protected override string TagName => "empty-state";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The headline.</summary>
    public string? Title {
        get => TitlePart.Text;
        set => TitlePart.Text = value;
    }

    /// <summary>The sentence under it.</summary>
    public string? Description {
        get => DescriptionPart.Text;
        set => DescriptionPart.Text = value;
    }

    /// <summary>The headline's element.</summary>
    public UiElement TitlePart { get; private set; } = null!;

    /// <summary>The description's element.</summary>
    public UiElement DescriptionPart { get; private set; } = null!;

    /// <summary>Where the button that resolves it goes.</summary>
    public UiElement Actions { get; private set; } = null!;

    /// <summary>The picture, created the first time it is asked for.</summary>
    public Icon Illustration {
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
    /// <remarks>
    ///     ARIA <c>status</c> — a polite live region. An empty state appears when a query came back
    ///     with nothing, and "there is nothing here" is the answer to what the user just did; a
    ///     screen-reader user who was told only that the list finished loading has been told
    ///     nothing.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Status;

    /// <inheritdoc />
    protected override string? NativeAccessibleName => Title;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        TitlePart = Part("empty-title");
        DescriptionPart = Part("empty-description");
        Actions = Part("empty-actions");

        // The headline names it and the sentence under describes it, on `Alert`'s terms.
        AddAccessibleRelation(AccessibleRelation.DescribedBy, DescriptionPart);
    }
}
