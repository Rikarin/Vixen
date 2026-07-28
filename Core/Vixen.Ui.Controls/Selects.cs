// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>One choice in a list.</summary>
public sealed partial class Option : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "option";

    /// <summary>What choosing it means.</summary>
    [UiProperty]
    public partial string? Value { get; set; }

    /// <summary>Whether it is chosen.</summary>
    public bool IsSelected => (State & ElementState.Checked) != 0;

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
    readonly List<Option> options = [];

    /// <summary>The field that opens the list.</summary>
    public UiElement Field { get; private set; } = null!;

    /// <summary>The chevron on the right of the field.</summary>
    public Icon Chevron { get; private set; } = null!;

    /// <summary>The floating list.</summary>
    public Popover List { get; private set; } = null!;

    /// <summary>The options, in order.</summary>
    public IReadOnlyList<Option> Options => options;

    /// <summary>Whether the list is showing.</summary>
    public bool IsOpen => List.IsOpen;

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
        if (List is not null) {
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

        options.Add(option);
        OnOptionAdded(option);

        return option;
    }

    /// <summary>Shows the list.</summary>
    public void Open() {
        if (Disabled) {
            return;
        }

        List.Open(this);
    }

    /// <summary>Hides it.</summary>
    /// <param name="reason">Why.</param>
    public void CloseList(CloseReason reason = CloseReason.Code) => List.Close(reason);

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
        if (options.Count == 0) {
            return;
        }

        if (!IsOpen) {
            Open();
        }

        var current = options.FindIndex(static option => option.IsFocused);
        var next = current < 0
            ? step > 0 ? 0 : options.Count - 1
            : Math.Clamp(current + step, 0, options.Count - 1);

        Document.Focus(options[next]);
    }

    void Chosen(ClickEvent args) {
        if (args.Source is Option option && options.Contains(option)) {
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
    protected override void OnOptionChosen(Option option) {
        Value = option.Value;
        CloseList(CloseReason.Committed);

        Document.Focus(this);
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
    readonly List<Option> options = [];

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
    public IReadOnlyList<Option> Options => options;

    /// <summary>What is in the field.</summary>
    public string? Value {
        get => Editor.Value;
        set => Editor.Value = value;
    }

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
        if (List is not null) {
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

        options.Add(option);
        return option;
    }

    void Chosen(ClickEvent args) {
        if (!ReferenceEquals(args.Source, Toggle)) {
            return;
        }

        if (List.IsOpen) {
            List.Close();
        } else {
            List.Open(this);
        }

        args.Handled = true;
    }

    void Picked(ClickEvent args) {
        if (args.Source is not Option option || !options.Contains(option)) {
            return;
        }

        Value = option.Value;

        List.Close(CloseReason.Committed);
        Document.Focus(Editor);

        // ⚠ The caret goes to the end rather than selecting everything. A suggestion that was picked
        // is usually a prefix somebody is about to finish, and selecting it means the next keystroke
        // deletes what they just chose.
        Editor.MoveCaret(Value?.Length ?? 0);
    }
}
