// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>The clickable strip at the top of an <see cref="Expander" />.</summary>
/// <remarks>
///     Its own type because it is the part that takes the focus and answers to Space and Enter — the
///     expander itself is a box around two things and is not focusable. Making the header the button
///     is also what keeps a keyboard user out of the content until they open it.
/// </remarks>
public sealed partial class ExpanderHeader : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "expander-header";

    /// <summary>The chevron.</summary>
    public Icon Chevron { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="AccessibleStates.Expandable" /> unconditionally, on <c>Select</c>'s
    ///     terms.</b> A disclosure button always has an <c>aria-expanded</c>; what changes is
    ///     whether it is true. Sending only <see cref="AccessibleStates.Expanded" /> when the
    ///     section is open would make a closed expander indistinguishable from a button that does
    ///     not open anything at all.
    ///     <para>
    ///         Read from <see cref="ElementState.Checked" />, which <see cref="Expander" /> already
    ///         sets on this header for the cascade — so there is no second copy of "is it open" and
    ///         no way for the two to disagree.
    ///     </para>
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Expandable
        | ((State & ElementState.Checked) != 0 ? AccessibleStates.Expanded : AccessibleStates.None);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Chevron = Part<Icon>();
        Chevron.Geometry = ControlIcons.ChevronRight;

        Document.Move(Chevron, 0);
    }
}

/// <summary>A header, and content that appears when it is pressed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Collapsed means <c>display: none</c>, not removed.</b> The content keeps its state,
///         its scroll offset and its focus history across an open-and-shut, which is what an
///         inspector section has to do — and it means expanding is a restyle rather than a rebuild.
///         The cost is that a collapsed section's elements exist; an application with a genuinely
///         expensive one should build it on first expansion, which <see cref="Expanded" /> is for.
///     </para>
///     <para>
///         ⚠ <b>The chevron is swapped, not rotated.</b> This class used to say the stylesheet turned
///         it — <c>expander.open expander-header icon</c> — and both halves of that were wrong: no
///         such rule was ever written, and there is no <c>transform</c> in the style engine to write
///         it with. So an expander opened and closed with the arrow pointing right the whole time,
///         which is the one affordance saying whether there is anything inside. Swapping the geometry
///         is what <c>TreeView</c> and the code editor's fold gutter already do.
///     </para>
/// </remarks>
public sealed partial class Expander : Control {
    /// <inheritdoc />
    protected override string TagName => "expander";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip that opens it.</summary>
    public ExpanderHeader Header { get; private set; } = null!;

    /// <summary>Where the content goes.</summary>
    public UiElement Content { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Without this an expander written in markup cannot close.</b> Collapsing is
    ///     <c>expander-content { display: none }</c>, so it hides the <i>part</i> and nothing else —
    ///     and children hung off the control itself are siblings of that part rather than inside it.
    ///     They stayed on screen through every click of the header, with the chevron flipping over
    ///     rows that never moved. Code that builds by hand writes <c>section.Content.Add(…)</c> and
    ///     says the same thing; markup has no <c>.Content</c> to write, which is what
    ///     <see cref="UiElement.ContentHost" /> exists to answer.
    /// </remarks>
    protected override UiElement ContentHost => Content;

    /// <summary>The name markup writes to reach <see cref="Header" />.</summary>
    public const string HeaderSlot = "header";

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>slot="header"</c>, and it is what took the last panel out of C#.</b> An
    ///         expander is two things and <see cref="ContentHost" /> can only be one of them, so
    ///         markup could fill a foldout's body and had no spelling at all for its header — which
    ///         is where an inspector puts a component's icon, its remove button and the grab handle
    ///         a drag reads. The whole of <c>ComponentsView</c>'s foldout loop stayed hand-written
    ///         for that one missing name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The header itself, not a wrapper inside it.</b> A container would be a second
    ///         box in the flex row that every existing rule — <c>expander-header icon</c>,
    ///         <c>expander-header:hover .remove-component</c> — would have had to be taught about.
    ///         Content lands after the label, which is where appending puts it; anything belonging
    ///         in front of the label says so with <c>order</c>, the way it would on the web.
    ///     </para>
    /// </remarks>
    protected override UiElement? NamedHost(string name) =>
        string.Equals(name, HeaderSlot, StringComparison.Ordinal) ? Header : base.NamedHost(name);

    /// <summary>What the header says.</summary>
    public string? Label {
        get => Header.Label;
        set => Header.Label = value;
    }

    /// <summary>Whether the content is showing.</summary>
    [UiProperty(Changed = nameof(OnExpandedChanged))]
    public partial bool IsExpanded { get; set; }

    /// <summary>Raised when it opens or closes.</summary>
    /// <remarks>
    ///     The place to build content that is expensive enough not to want it before it is looked
    ///     at. Raised after the class is on, so a handler that measures something is measuring the
    ///     open state.
    /// </remarks>
    public event Action<Expander, bool>? Expanded;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Header = Part<ExpanderHeader>();
        Content = Part("expander-content");

        // ⚠ The pairing the tree already shows and a bridge still cannot infer. Header and content
        // are siblings, so nothing about being a child of the same expander says that pressing one
        // reveals the other — `aria-controls` is the sentence, and it is what lets a screen reader
        // offer to jump from the button to what it opened.
        Header.AddAccessibleRelation(AccessibleRelation.Controls, Content);

        AddHandler<ClickEvent>(static (element, args) => ((Expander) element).Chosen(args));
    }

    void Chosen(ClickEvent args) {
        // ⚠ Only the header. A button inside the content raises a click too, and it bubbles through
        // here on its way out — an expander that toggled on any click would shut itself every time
        // somebody used what is inside it.
        if (ReferenceEquals(args.Source, Header)) {
            IsExpanded = !IsExpanded;
        }
    }

    void OnExpandedChanged(bool previous, bool current) {
        Header.Chevron.Geometry = current ? ControlIcons.ChevronDown : ControlIcons.ChevronRight;

        if (current) {
            AddClass("open");
            Header.State |= ElementState.Checked;
        } else {
            RemoveClass("open");
            Header.State &= ~ElementState.Checked;
        }

        Raise(new OpenChangedEvent { IsOpen = current });
        Expanded?.Invoke(this, current);
    }
}

/// <summary>Several expanders, of which one may be open at a time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The exclusion is optional and off by default.</b> An accordion that closes what you
///         were reading because you opened something else is the single most complained-about
///         pattern in interface design, and it is right exactly when the sections are alternatives
///         rather than a list — so it is a property, and the default is the one that does not lose
///         the user's place.
///     </para>
///     <para>
///         ⚠ <b>A section is an <see cref="Expander" /> child, however it got there.</b> This used
///         to keep a list that only <see cref="AddSection" /> wrote to, and call that deliberate —
///         the shape <c>RadioGroup.AddOption</c> has. Markup is what made it wrong: an
///         <c>&lt;Accordion&gt;</c> with expanders written inside it is the only way to author one
///         in a <c>.vxml</c>, there is no <c>AddSection</c> to call from there, and the registry
///         stayed empty — so <c>AllowMultiple="false"</c> parsed, bound, and did nothing at all.
///     </para>
/// </remarks>
public sealed partial class Accordion : Control {
    /// <inheritdoc />
    protected override string TagName => "accordion";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Whether more than one section may be open at once.</summary>
    [UiProperty(Default = true)]
    public partial bool AllowMultiple { get; set; }

    /// <summary>The sections, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the children rather than kept, and a fresh snapshot each time.</b> A list
    ///     the accordion maintained would be a second place the truth lived — one that markup could
    ///     not write to, and one that a removed or reparented section would have had to be told
    ///     about. Snapshotting also makes the loop in the handler safe against a section that
    ///     rearranges the accordion while it is closing.
    /// </remarks>
    public IReadOnlyList<Expander> Sections => [.. Children.OfType<Expander>()];

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<OpenChangedEvent>(static (element, args) => ((Accordion) element).Opened(args));
    }

    /// <summary>Adds a section.</summary>
    /// <param name="label">What its header says.</param>
    /// <returns>The section, whose <see cref="Expander.Content" /> is where the content goes.</returns>
    /// <remarks>
    ///     Sugar over <c>Add&lt;Expander&gt;()</c> and a label, and nothing more — the section is a
    ///     section because it is a child, not because this method was the one that added it.
    /// </remarks>
    public Expander AddSection(string? label = null) {
        var section = Add<Expander>();
        section.Label = label;

        return section;
    }

    void Opened(OpenChangedEvent args) {
        if (AllowMultiple || !args.IsOpen || args.Source is not Expander opened) {
            return;
        }

        // ⚠ Only this accordion's own sections, which is what makes the event's *source* worth
        // testing rather than just acting on it. A section's content may hold expanders of its own —
        // a nested group in an inspector — and their OpenChangedEvent bubbles through here on its
        // way out. Acting on one would shut every section, including the one whose content the user
        // had just opened something inside.
        if (!ReferenceEquals(opened.Parent, this)) {
            return;
        }

        foreach (var section in Sections) {
            if (!ReferenceEquals(section, opened)) {
                // Reentrant, and harmlessly so: closing a section raises another OpenChangedEvent
                // that arrives here with IsOpen false, which the guard above returns on before it
                // reaches this loop.
                section.IsExpanded = false;
            }
        }
    }
}
