// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>One choice in a list.</summary>
public sealed partial class Option : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "option";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Overridden to say so, because the list above this option is what shows it.</b> A
    ///     closed <see cref="Select" /> displays the chosen option's label, and <see cref="Label" />
    ///     on the base writes a part's text — which notifies nobody. Assigned after the option was
    ///     added, which is the order markup uses and the order <c>AddOption</c> uses, the field would
    ///     otherwise go on showing its placeholder for a value that is genuinely selected.
    /// </remarks>
    public override string? Label {
        get => base.Label;
        set {
            if (string.Equals(base.Label, value, StringComparison.Ordinal)) {
                return;
            }

            base.Label = value;
            LabelChanged?.Invoke(this);
        }
    }

    /// <summary>Raised when <see cref="Label" /> changes.</summary>
    internal event Action<Option>? LabelChanged;

    /// <summary>What choosing it means.</summary>
    [UiProperty]
    public partial string? Value { get; set; }

    /// <summary>Whether it is chosen.</summary>
    public bool IsSelected => (State & ElementState.Checked) != 0;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Option;

    /// <inheritdoc />
    /// <remarks><see cref="TabItem" />'s second meaning of <see cref="ElementState.Checked" />, for the same reason.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        IsSelected ? AccessibleStates.Selected : AccessibleStates.None;

    /// <summary>The tick shown beside a chosen option in a multi-select.</summary>
    public Icon? Mark { get; internal set; }
}

/// <summary>What every list of choices has in common: the options, and which of them are chosen.</summary>
/// <remarks>
///     ⚠ <b>The options live in a popover that is a root child, not inside the control.</b> A
///     dropdown inside the field that opens it would be clipped by every scrolling ancestor between
///     the two — which for a field in an inspector in a docked panel is three of them. The list is
///     therefore an <see cref="Overlay" />, and the field keeps a reference to it.
/// </remarks>
public abstract partial class SelectBase : Control {
    /// <summary>The field that opens the list.</summary>
    public UiElement Field { get; private set; } = null!;

    /// <summary>The chevron on the right of the field.</summary>
    public Icon Chevron { get; private set; } = null!;

    /// <summary>The floating list.</summary>
    public Popover List { get; private set; } = null!;

    /// <summary>The options, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the list's children rather than kept, and a fresh snapshot each time.</b> A
    ///     list this control maintained would be a second place the truth lived — one that markup
    ///     could not write to, so an <c>&lt;Option&gt;</c> written as a nested tag drew a choice that
    ///     was in the popover and not in the control: never matched by <see cref="Options" />, never
    ///     restated, and invisible to the keyboard. <c>Accordion.Sections</c> is the same arrangement
    ///     for the same reasons.
    ///     <para>
    ///         ⚠ It reads <see cref="List" />'s content and not this control's children, because that
    ///         is where the options are — see the remark on the class. Before <see cref="List" />
    ///         exists it is empty rather than a null reference, which is what lets a derived
    ///         <c>OnCreated</c> ask before calling its base.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<Option> Options => List is null ? [] : [.. List.Content.Children.OfType<Option>()];

    /// <summary>Whether the list is showing.</summary>
    public bool IsOpen => List.IsOpen;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The popover, so that an <c>&lt;Option&gt;</c> written inside a <c>&lt;Select&gt;</c>
    ///     lands where the options actually are.</b> Hung off the control itself it would sit beside
    ///     the field and the chevron, be laid out as part of the closed control, and show whether or
    ///     not the list was open — which is a row of choices printed under the field for ever.
    ///     <para>
    ///         The null guard is <c>Tabs</c>' and it is load-bearing for the same reason:
    ///         <c>ContentHost</c> can be read before <see cref="OnCreated" /> has made the popover.
    ///     </para>
    /// </remarks>
    protected override UiElement ContentHost => List is null ? this : List.Content;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Field = Part("select-field");
        Chevron = Part<Icon>();
        Chevron.Geometry = ControlIcons.ChevronDown;

        // ⚠ On the root, not on this control. It is an overlay, and an overlay inside the thing it
        // pops out of is an overlay that gets clipped. The cost of that arrangement is that the list
        // is not this control's child, so the subtree removal does not take it — `OnRemoved` below
        // is what pays it.
        List = Document.Root.Add<Popover>();
        List.AddClass("select-list");
        List.Placement = Placement.Bottom;

        // ⚠ **The one relation this control cannot do without, and the reason is the comment three
        // lines up.** The list is a child of the document *root*, so in the element tree the options
        // are nowhere near the control they belong to — no walk over `Parent` from either end finds
        // the other. `aria-owns` is exactly the statement "these are my children although the tree
        // says otherwise", and without it a screen reader walking the tree finds a combo box with
        // nothing in it and a loose list of options hanging off the root.
        List.Role = AccessibleRole.ListBox;
        AddAccessibleRelation(AccessibleRelation.Owns, List);

        // ⚠ **What makes an `<Option>` written as a nested tag mean what it looks like.** The options
        // live in the popover, so `UiElement.OnChildAdded` fires there rather than here — see
        // `Popover.ContentAdded`, which exists for this. Both routes now reach `OnOptionAdded`:
        // `AddOption` is sugar over `List.Content.Add<Option>()` and two properties.
        List.ContentAdded += (_, child) => {
            if (child is not Option option) {
                return;
            }

            OnOptionAdded(option);

            // ⚠ **And again whenever the option says something different about itself, which is the
            // half that arriving cannot cover.** A tag is created before its attributes are
            // assigned, so the line above runs on an option with no value and no label — and
            // `Restate` matches on the value and displays the label. Without this an
            // `<Option Value="cutout" />` in a `<Select Value="cutout" />` leaves the closed field
            // showing its placeholder: everything correct, nothing selected, no diagnostic.
            //
            // Any property rather than a test against two keys, because restating is a walk of a
            // handful of options and being right is worth more than the comparison it saves.
            option.PropertyChanged += (changed, _) => {
                if (changed is Option named) {
                    OnOptionAdded(named);
                }
            };

            // ⚠ And the label, which is not a `[UiProperty]` and so is not covered by the line above:
            // `ButtonBase.Label` writes a part's text. It is what the closed field displays, so a
            // label assigned after the option arrived — which is every order there is — has to reach
            // `Restate` or the field shows its placeholder for a value that is selected.
            option.LabelChanged += OnOptionAdded;
        };

        AddHandler<PointerEvent>(static (element, args) => ((SelectBase) element).Pointed(args));
        AddHandler<KeyEvent>(static (element, args) => ((SelectBase) element).Keyed(args));

        List.AddHandler<ClickEvent>((_, args) => Chosen(args));

        // ⚠ The same key handler on the list, because once an option has the focus the field is no
        // longer on the route. The list is a root child, so an event that starts at an option
        // bubbles to the root without ever passing through the control that owns it — which is the
        // one cost of putting overlays where they cannot be clipped, and it is paid here.
        List.AddHandler<KeyEvent>((_, args) => Keyed(args));

        // Closing is not always this control's doing: Escape and a click outside both go through
        // the overlay. Listening rather than only acting in CloseList is what keeps `:checked` on
        // the field in step with whether the list is actually showing.
        List.OpenChanged += (_, isOpen) => {
            if (isOpen) {
                State |= ElementState.Checked;
                return;
            }

            State &= ~ElementState.Checked;

            // The focus comes back out with it. Left on an option inside a hidden popover, the
            // keyboard would be talking to something nobody can see.
            if (Document.Focused is Option option && ReferenceEquals(option.Parent, List.Content)) {
                Document.Focus(this);
            }
        };
    }

    /// <inheritdoc />
    /// <remarks>The list is a root child, so the subtree removal does not reach it. See its creation.</remarks>
    protected override void OnRemoved() {
        if (List is { IsRemoved: false }) {
            Document.Remove(List);
            List = null!;
        }

        base.OnRemoved();
    }

    /// <summary>Adds a choice.</summary>
    /// <param name="value">What choosing it means.</param>
    /// <param name="label">What it says.</param>
    /// <returns>The option.</returns>
    public Option AddOption(string value, string? label = null) {
        ArgumentNullException.ThrowIfNull(value);

        var option = List.Content.Add<Option>();
        option.Value = value;
        option.Label = label ?? value;

        // ⚠ **`OnOptionAdded` has already run, from `Popover.ContentAdded` above — and it ran before
        // these two lines, with the value not yet set.** So it is called again here, once the option
        // says what it is. That is the same shape `RadioGroup.AddOption` has and for the same
        // reason: a hook fires when the element arrives, and a property assigned on the next line is
        // news to it.
        OnOptionAdded(option);

        return option;
    }

    /// <summary>Removes every choice.</summary>
    /// <remarks>
    ///     ⚠ <b>For a dropdown whose contents are data rather than a fixed set</b> — a filter over the
    ///     asset types a project actually holds, a list of connected devices, a build target list. The
    ///     alternative is rebuilding the control, which loses its place in the layout and its focus.
    ///     <para>
    ///         The chosen value is <i>not</i> cleared with the options, because the caller is usually
    ///         about to add them back and wants to keep the choice if it survives. Setting
    ///         <c>Value</c> to something no option carries shows the placeholder, which is the honest
    ///         state for "what was chosen is not on offer any more".
    ///     </para>
    /// </remarks>
    public void ClearOptions() {
        foreach (var option in Options) {
            option.Remove();
        }
    }

    /// <summary>Shows the list.</summary>
    public void Open() {
        if (Disabled) {
            return;
        }

        Fit();
        List.Open(this);
    }

    /// <summary>Makes the list at least as wide as the field it drops out of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A dropdown narrower than its own field reads as a different control.</b> The list
    ///         is a child of the document root, not of this element — it has to be, or it would be
    ///         clipped by the thing it pops out of — so nothing about the layout relates the two, and
    ///         the popover sized itself to its longest option. Against a 132-pixel filter holding
    ///         three short words the result is a menu floating under one end of the control it
    ///         belongs to, which is the shape of "the dropdown is the wrong size".
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>min-width</c> rather than <c>width</c>, and it is measured on every open.</b>
    ///         An option longer than the field still gets the room to say so — clipping the text is
    ///         the one thing worse than a narrow list — and the field's width is a layout result that
    ///         changes with the panel, so a value written once would be last week's.
    ///     </para>
    /// </remarks>
    void Fit() {
        if (Width > 0f) {
            List.SetStyle("min-width", Width.ToString("0.##", CultureInfo.InvariantCulture) + "px");
        }
    }

    /// <summary>Hides it.</summary>
    /// <param name="reason">Why.</param>
    /// <remarks>
    ///     ⚠ <b>A no-op once the control has been removed, and that is not defensive padding.</b>
    ///     Choosing an option raises <c>SelectionChanged</c>, and a perfectly ordinary handler for it
    ///     rebuilds the panel the select is sitting in — an "Add insert…" dropdown that adds the
    ///     insert and redraws the list it was in is the shape of every one of these. That removal runs
    ///     <see cref="OnRemoved" />, which drops the popover and nulls the field, and the caller is
    ///     still inside <see cref="OnOptionChosen" /> with two lines to go. This used to be a
    ///     <c>NullReferenceException</c> that took the editor down on a click nobody could call wrong.
    /// </remarks>
    public void CloseList(CloseReason reason = CloseReason.Code) {
        if (List is { IsRemoved: false }) {
            List.Close(reason);
        }
    }

    /// <summary>Called after an option is added, so a subclass can set its initial state.</summary>
    /// <param name="option">The option.</param>
    protected virtual void OnOptionAdded(Option option) {
    }

    /// <summary>Called when an option in the list is activated.</summary>
    /// <param name="option">The option.</param>
    protected abstract void OnOptionChosen(Option option);

    /// <summary>Moves the highlight through the list, opening it first if it is shut.</summary>
    /// <param name="step">Which way, and how far.</param>
    protected void Highlight(int step) {
        // ⚠ One snapshot, read once. `Options` walks the popover's children, so two reads are two
        // walks and two lists the moment anything between them adds an option.
        var options = Options;

        if (options.Count == 0) {
            return;
        }

        if (!IsOpen) {
            Open();
        }

        var current = -1;

        for (var i = 0; i < options.Count; i++) {
            if (options[i].IsFocused) {
                current = i;
                break;
            }
        }

        var next = current < 0
            ? step > 0 ? 0 : options.Count - 1
            : Math.Clamp(current + step, 0, options.Count - 1);

        Document.Focus(options[next]);
    }

    void Chosen(ClickEvent args) {
        // ⚠ The parent test rather than a lookup in a list this no longer keeps, and it is the
        // stronger check of the two: an `Option` belonging to some other select nested in this one's
        // popover would have been found by a `Contains` only if it had been registered here, which
        // is exactly the accident a snapshot removes.
        if (args.Source is Option option && ReferenceEquals(option.Parent, List.Content)) {
            OnOptionChosen(option);
        }
    }

    void Pointed(PointerEvent args) {
        if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Primary }) {
            return;
        }

        Document.Focus(this);

        // ⚠ Toggling rather than opening. Without it a click on an open select is a press that the
        // overlay's light dismiss closes and a click that this reopens, and the list flickers
        // instead of closing.
        if (IsOpen) {
            CloseList();
        } else {
            Open();
        }

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Down:
                Highlight(1);
                break;

            case InputKey.Up:
                Highlight(-1);
                break;

            case InputKey.Home:
                Highlight(int.MinValue / 2);
                break;

            case InputKey.End:
                Highlight(int.MaxValue / 2);
                break;

            case InputKey.Escape when IsOpen:
                CloseList(CloseReason.Cancelled);
                break;

            case InputKey.Space or InputKey.Enter or InputKey.KeypadEnter when !IsOpen:
                Open();
                break;

            default:
                return;
        }

        args.Handled = true;
    }
}

/// <summary>A field showing one choice, with a list behind it.</summary>
public sealed partial class Select : SelectBase {
    /// <inheritdoc />
    protected override string TagName => "select";

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.ComboBox;

    /// <inheritdoc />
    /// <remarks>
    ///     The chosen option's <i>label</i> and not its <see cref="Option.Value" />. A value is what
    ///     the application stores; the label is what the field shows and therefore what a screen
    ///     reader should say, and the two are routinely <c>"cutout"</c> and <c>"Cut-out"</c>.
    /// </remarks>
    protected override string? NativeAccessibleValue => Selected?.Label;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Both flags, and <see cref="AccessibleStates.Expandable" /> unconditionally.</b> A
    ///     combo box always has an <c>aria-expanded</c>; what changes is whether it is true. Sending
    ///     only <see cref="AccessibleStates.Expanded" /> when the list is open would make a closed
    ///     select indistinguishable from a control that does not open at all.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Expandable | (IsOpen ? AccessibleStates.Expanded : AccessibleStates.None);

    /// <summary>Which choice is made, or <c>null</c> if none is.</summary>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial string? Value { get; set; }

    /// <summary>What the field says when nothing is chosen.</summary>
    [UiProperty(Changed = nameof(OnPlaceholderChanged))]
    public partial string? Placeholder { get; set; }

    /// <summary>Raised when the choice changes.</summary>
    public event Action<Select, string?>? SelectionChanged;

    /// <summary>The option currently chosen, if any.</summary>
    public Option? Selected {
        get {
            foreach (var option in Options) {
                if (option.IsSelected) {
                    return option;
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    protected override void OnOptionAdded(Option option) {
        base.OnOptionAdded(option);

        if (string.Equals(option.Value, Value, StringComparison.Ordinal)) {
            Restate();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Everything after the assignment has to survive the control being gone.</b> Setting
    ///     <see cref="Value" /> raises <see cref="SelectionChanged" /> synchronously, and a handler
    ///     that rebuilds the panel this field is in has removed it before the next line runs. See
    ///     <see cref="SelectBase.CloseList" />.
    /// </remarks>
    protected override void OnOptionChosen(Option option) {
        Value = option.Value;
        CloseList(CloseReason.Committed);

        if (!IsRemoved) {
            Document.Focus(this);
        }
    }

    void OnValueChanged(string? previous, string? current) {
        Restate();

        Raise(new ValueChangedEvent<string> { Previous = previous, Value = current });
        SelectionChanged?.Invoke(this, current);
    }

    void OnPlaceholderChanged(string? previous, string? current) => Restate();

    void Restate() {
        Option? chosen = null;

        foreach (var option in Options) {
            var selected = string.Equals(option.Value, Value, StringComparison.Ordinal) && Value is not null;

            if (selected) {
                option.State |= ElementState.Checked;
                chosen = option;
            } else {
                option.State &= ~ElementState.Checked;
            }
        }

        Field.Text = chosen?.Label ?? Placeholder;

        // ⚠ **The focus is on the field and the thing to announce is an option, which is what
        // `aria-activedescendant` is the only way to say.** A `Select` keeps the keyboard focus on
        // itself while the list is open — that is what makes Escape and type-ahead work — so
        // `UiDocument.Focused` is the field, and a screen reader told only that would never announce
        // which choice is current. Cleared first: it is a single-target relation that follows the
        // selection, and appending would leave it pointing at every option ever chosen.
        ClearAccessibleRelations(AccessibleRelation.ActiveDescendant);

        if (chosen is not null) {
            AddAccessibleRelation(AccessibleRelation.ActiveDescendant, chosen);
        }

        if (chosen is null) {
            AddClass("empty");
        } else {
            RemoveClass("empty");
        }
    }
}

/// <summary>A field showing several choices at once.</summary>
/// <remarks>
///     ⚠ <b>The list stays open as options are picked.</b> A multi-select that closed on each choice
///     would make choosing three things three separate journeys — which is what a single select is
///     for. It closes on Escape, on a click outside, and on Enter.
/// </remarks>
public sealed partial class MultiSelect : SelectBase {
    readonly HashSet<string> selected = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override string TagName => "multi-select";

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.ComboBox;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="AccessibleStates.MultiSelectable" /> is the whole difference from
    ///     <see cref="Select" />, and it is what tells a screen-reader user that choosing a second
    ///     option will not undo the first.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Expandable
        | AccessibleStates.MultiSelectable
        | (IsOpen ? AccessibleStates.Expanded : AccessibleStates.None);

    /// <summary>What the field says when nothing is chosen.</summary>
    [UiProperty(Changed = nameof(OnPlaceholderChanged))]
    public partial string? Placeholder { get; set; }

    /// <summary>The values currently chosen.</summary>
    public IReadOnlyCollection<string> Values => selected;

    /// <summary>Raised when the set of chosen values changes.</summary>
    public event Action<MultiSelect>? SelectionChanged;

    /// <summary>Chooses or unchooses a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="isSelected">Which.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Select(string value, bool isSelected) {
        ArgumentNullException.ThrowIfNull(value);

        var changed = isSelected ? selected.Add(value) : selected.Remove(value);
        if (!changed) {
            return false;
        }

        Restate();
        SelectionChanged?.Invoke(this);

        return true;
    }

    /// <inheritdoc />
    protected override void OnOptionAdded(Option option) {
        base.OnOptionAdded(option);

        // The tick goes in front of the label, so it has to be moved there — a new child lands at
        // the end, which for an option is after the words.
        option.Mark = option.Add<Icon>();
        option.Mark.Geometry = ControlIcons.Check;
        Document.Move(option.Mark, 0);

        Restate();
    }

    /// <inheritdoc />
    protected override void OnOptionChosen(Option option) {
        if (option.Value is { } value) {
            Select(value, !selected.Contains(value));
        }
    }

    void OnPlaceholderChanged(string? previous, string? current) => Restate();

    void Restate() {
        var shown = 0;
        string? first = null;

        foreach (var option in Options) {
            var isSelected = option.Value is { } value && selected.Contains(value);

            if (isSelected) {
                option.State |= ElementState.Checked;
                first ??= option.Label;
                shown++;
            } else {
                option.State &= ~ElementState.Checked;
            }
        }

        // One name, or a count. Listing them all is what makes a multi-select field grow taller than
        // the form it is in the moment somebody picks five things.
        Field.Text = shown switch {
            0 => Placeholder,
            1 => first,
            _ => shown.ToString(System.Globalization.CultureInfo.InvariantCulture) + " selected"
        };

        if (shown == 0) {
            AddClass("empty");
        } else {
            RemoveClass("empty");
        }
    }
}

/// <summary>A field that can be typed into as well as chosen from.</summary>
/// <remarks>
///     ⚠ <b>The difference from <see cref="Select" /> is not the dropdown — it is that the value need
///     not be one of the options.</b> A combo box is a text field with suggestions; a select is a
///     choice among a fixed set. Conflating them gives either a select that accepts nonsense or a
///     combo box that discards what was typed.
/// </remarks>
public sealed partial class ComboBox : Control {
    /// <inheritdoc />
    protected override string TagName => "combo-box";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The editable half.</summary>
    public TextBox Editor { get; private set; } = null!;

    /// <summary>The button that drops the list.</summary>
    public IconButton Toggle { get; private set; } = null!;

    /// <summary>The floating list.</summary>
    public Popover List { get; private set; } = null!;

    /// <summary>The suggestions, in order.</summary>
    /// <remarks>
    ///     ⚠ Read from the list's children rather than kept, on <c>SelectBase.Options</c>' terms and
    ///     for its reasons — a suggestion written as a nested <c>&lt;Option&gt;</c> is a suggestion.
    ///     Unlike a select's, a combo box's options carry no derived state, so there is nothing for a
    ///     <c>Popover.ContentAdded</c> handler to do here and none is subscribed.
    /// </remarks>
    public IReadOnlyList<Option> Options => List is null ? [] : [.. List.Content.Children.OfType<Option>()];

    /// <summary>What is in the field.</summary>
    public string? Value {
        get => Editor.Value;
        set => Editor.Value = value;
    }

    /// <inheritdoc />
    /// <remarks>The popover, on <c>SelectBase.ContentHost</c>'s terms and for its reasons.</remarks>
    protected override UiElement ContentHost => List is null ? this : List.Content;

    /// <summary>Raised when the text changes, whether it was typed or chosen.</summary>
    public event Action<ComboBox, string?>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Editor = Part<TextBox>();
        Editor.AddClass("combo-editor");

        Toggle = Part<IconButton>();
        Toggle.LeadingIcon.Geometry = ControlIcons.ChevronDown;
        Toggle.Variant = ControlVariant.Subtle;
        Toggle.Label = "Show suggestions";
        Toggle.TabIndex = -1;

        List = Document.Root.Add<Popover>();
        List.AddClass("select-list");
        List.Placement = Placement.Bottom;

        Editor.ValueChanged += (_, value) => ValueChanged?.Invoke(this, value);

        AddHandler<ClickEvent>(static (element, args) => ((ComboBox) element).Chosen(args));
        List.AddHandler<ClickEvent>((_, args) => Picked(args));
    }

    /// <inheritdoc />
    /// <remarks>The list is a root child, so the subtree removal does not reach it. See its creation.</remarks>
    protected override void OnRemoved() {
        if (List is { IsRemoved: false }) {
            Document.Remove(List);
            List = null!;
        }

        base.OnRemoved();
    }

    /// <summary>Adds a suggestion.</summary>
    /// <param name="value">The text it fills in.</param>
    /// <param name="label">What it says, if that differs.</param>
    /// <returns>The option.</returns>
    public Option AddOption(string value, string? label = null) {
        ArgumentNullException.ThrowIfNull(value);

        var option = List.Content.Add<Option>();
        option.Value = value;
        option.Label = label ?? value;

        return option;
    }

    void Chosen(ClickEvent args) {
        if (!ReferenceEquals(args.Source, Toggle)) {
            return;
        }

        if (List.IsOpen) {
            List.Close();
        } else {
            // The same fit a `Select` does — see `SelectBase.Fit`. A combo box's list is the same
            // root-parented popover and drifts from its field the same way.
            if (Width > 0f) {
                List.SetStyle("min-width", Width.ToString("0.##", CultureInfo.InvariantCulture) + "px");
            }

            List.Open(this);
        }

        args.Handled = true;
    }

    void Picked(ClickEvent args) {
        if (args.Source is not Option option || !ReferenceEquals(option.Parent, List.Content)) {
            return;
        }

        Value = option.Value;

        // ⚠ And everything after this line has to survive the control being gone: the assignment
        // raised `ValueChanged`, and a handler that rebuilds the panel this box is in has already
        // removed it. `SelectBase.CloseList` records the crash this was.
        if (IsRemoved) {
            return;
        }

        List.Close(CloseReason.Committed);
        Document.Focus(Editor);

        // ⚠ The caret goes to the end rather than selecting everything. A suggestion that was picked
        // is usually a prefix somebody is about to finish, and selecting it means the next keystroke
        // deletes what they just chose.
        Editor.MoveCaret(Value?.Length ?? 0);
    }
}
