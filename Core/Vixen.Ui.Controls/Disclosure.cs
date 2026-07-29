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
///     ⚠ <b>The exclusion is optional and off by default.</b> An accordion that closes what you were
///     reading because you opened something else is the single most complained-about pattern in
///     interface design, and it is right exactly when the sections are alternatives rather than a
///     list — so it is a property, and the default is the one that does not lose the user's place.
/// </remarks>
public sealed partial class Accordion : Control {
    readonly List<Expander> sections = [];

    /// <inheritdoc />
    protected override string TagName => "accordion";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Whether more than one section may be open at once.</summary>
    [UiProperty(Default = true)]
    public partial bool AllowMultiple { get; set; }

    /// <summary>The sections, in order.</summary>
    public IReadOnlyList<Expander> Sections => sections;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<OpenChangedEvent>(static (element, args) => ((Accordion) element).Opened(args));
    }

    /// <summary>Adds a section.</summary>
    /// <param name="label">What its header says.</param>
    /// <returns>The section, whose <see cref="Expander.Content" /> is where the content goes.</returns>
    public Expander AddSection(string? label = null) {
        var section = Add<Expander>();
        section.Label = label;

        sections.Add(section);
        return section;
    }

    void Opened(OpenChangedEvent args) {
        if (AllowMultiple || !args.IsOpen || args.Source is not Expander opened) {
            return;
        }

        foreach (var section in sections) {
            if (!ReferenceEquals(section, opened)) {
                // Reentrant, and harmlessly so: closing a section raises another OpenChangedEvent
                // that arrives here with IsOpen false, which the guard above returns on before it
                // reaches this loop.
                section.IsExpanded = false;
            }
        }
    }
}
