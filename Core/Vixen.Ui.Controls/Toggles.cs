// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>Anything with an on and an off.</summary>
/// <remarks>
///     A checkbox, a switch, a radio and a toggle button are one behaviour and four appearances: a
///     part that shows the state, a label beside it, Space to flip it, and <c>:checked</c> for the
///     stylesheet. What differs is the part and what the flip is allowed to do — a radio cannot turn
///     itself off — and that is what the two overridable members below are for.
/// </remarks>
public abstract partial class ToggleBase : ButtonBase {
    /// <summary>Whether it is on.</summary>
    [UiProperty(Changed = nameof(OnCheckedChanged))]
    public partial bool IsChecked { get; set; }

    /// <summary>Raised when it goes on or off, however that happened.</summary>
    public event Action<ToggleBase, bool>? CheckedChanged;

    /// <inheritdoc />
    protected override bool ActivatesOnEnter => false;

    /// <summary>Whether activating it while it is already on turns it off.</summary>
    /// <remarks>
    ///     False for a radio, and that is the whole difference between a radio and a checkbox that
    ///     happens to be in a group. Clicking the selected radio a second time must leave it
    ///     selected, or a group can be put into a state — nothing chosen — that the user cannot get
    ///     back out of with the keyboard.
    /// </remarks>
    protected virtual bool CanUncheck => true;

    /// <inheritdoc />
    protected override void Activate(ActivationDevice device, int count, ModifierKeys modifiers) {
        if (IsChecked && !CanUncheck) {
            // Reported anyway. "The user chose this one again" is a real event — a list that scrolls
            // the selection into view wants it — and swallowing it would make the control silent
            // exactly when it is already selected.
            base.Activate(device, count, modifiers);
            return;
        }

        IsChecked = !IsChecked;
        base.Activate(device, count, modifiers);
    }

    /// <summary>Called after the state changed, before it is reported.</summary>
    /// <param name="current">What it is now.</param>
    protected virtual void OnChecked(bool current) {
    }

    void OnCheckedChanged(bool previous, bool current) {
        if (current) {
            State |= ElementState.Checked;
        } else {
            State &= ~ElementState.Checked;
        }

        OnChecked(current);

        Raise(new ValueChangedEvent<bool> { Previous = previous, Value = current });
        CheckedChanged?.Invoke(this, current);
    }
}

/// <summary>A box that is ticked or not, with a label.</summary>
/// <remarks>
///     <para>
///         Three elements: the box, the tick inside it, and the label. The tick is an
///         <see cref="Icon" /> rather than something drawn here, so that a theme can replace it with
///         its own geometry, and it is present in the tree whether or not the box is ticked — the
///         stylesheet hides it with <c>display: none</c>, which costs nothing and keeps "is it
///         ticked" a single fact in a single place.
///     </para>
///     <para>
///         <b>Indeterminate is a third appearance rather than a third value.</b>
///         <c>IsChecked</c> stays a <c>bool</c>, because a tri-state checkbox that a caller
///         has to handle three cases for is a tri-state checkbox nobody handles all three cases for.
///         What <see cref="IsIndeterminate" /> means is "the box does not currently reflect
///         <c>IsChecked</c>, because the things it stands for disagree" — the parent of a
///         half-ticked list — and clicking it resolves it, which is what it is for.
///     </para>
/// </remarks>
public sealed partial class CheckBox : ToggleBase {
    Icon mark = null!;

    /// <inheritdoc />
    protected override string TagName => "checkbox";

    /// <summary>Whether the things it stands for disagree.</summary>
    [UiProperty(Changed = nameof(OnIndeterminateChanged))]
    public partial bool IsIndeterminate { get; set; }

    /// <summary>The box the tick is drawn in.</summary>
    public UiElement Box { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Box = Part("box");
        mark = Box.Add<Icon>();
        mark.Geometry = ControlIcons.Check;

        // The base class made the label first, because it does not know that anything comes before
        // it. Moving rather than reordering the base's work keeps the one rule — a derived control's
        // parts come after the ones it inherited — true everywhere except where a control says so.
        Document.Move(Box, 0);
    }

    /// <inheritdoc />
    protected override void Activate(ActivationDevice device, int count, ModifierKeys modifiers) {
        // ⚠ Resolved before the toggle rather than instead of it. A half-ticked parent that is
        // clicked becomes ticked, and leaving the flag set would leave it showing a dash while
        // claiming to be on — the state the flag exists to describe has just stopped being true.
        IsIndeterminate = false;
        base.Activate(device, count, modifiers);
    }

    void OnIndeterminateChanged(bool previous, bool current) {
        mark.Geometry = current ? ControlIcons.Dash : ControlIcons.Check;

        if (current) {
            AddClass("indeterminate");
        } else {
            RemoveClass("indeterminate");
        }
    }
}

/// <summary>A switch: a track, and a knob that slides along it.</summary>
/// <remarks>
///     ⚠ <b>Not a checkbox with a different skin, and the difference is what it means rather than
///     how it looks.</b> A checkbox is a value in a form that is applied when the form is; a switch
///     takes effect the moment it is flipped. A control set that makes them interchangeable produces
///     dialogs with a switch beside an OK button, where nobody can tell whether Cancel undoes it.
/// </remarks>
public sealed partial class Switch : ToggleBase {
    /// <inheritdoc />
    protected override string TagName => "switch";

    /// <summary>The track the knob slides along.</summary>
    public UiElement Track { get; private set; } = null!;

    /// <summary>The knob.</summary>
    /// <remarks>
    ///     Moved by the stylesheet rather than by code — <c>switch:checked > track > knob</c> is one
    ///     rule, and it means the animation that will make it slide is a transition on that rule
    ///     rather than something this type has to run and this type has to stop.
    /// </remarks>
    public UiElement Knob { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Track = Part("track");
        Knob = Track.Add("knob");

        Document.Move(Track, 0);
    }
}

/// <summary>One choice among several.</summary>
/// <remarks>
///     Belongs to a <see cref="RadioGroup" />, which is where the mutual exclusion lives. A radio
///     outside one is a checkbox that cannot be unticked, which is nothing anybody wants — so
///     <see cref="RadioGroup.AddOption" /> is the way to make one.
/// </remarks>
public sealed partial class RadioButton : ToggleBase {
    /// <inheritdoc />
    protected override string TagName => "radio";

    /// <inheritdoc />
    protected override bool CanUncheck => false;

    /// <summary>What choosing it means, for <see cref="RadioGroup.Value" />.</summary>
    [UiProperty]
    public partial string? Value { get; set; }

    /// <summary>The circle the dot is drawn in.</summary>
    public UiElement Box { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Box = Part("box");
        Box.Add("dot");

        Document.Move(Box, 0);
    }
}

/// <summary>A set of radios, of which exactly one is chosen.</summary>
/// <remarks>
///     <para>
///         <b>The exclusion lives here rather than in a shared static</b>, which is the mistake HTML
///         made with <c>name</c>: two forms on one page with a field called <c>colour</c> are one
///         radio group, and nobody finds out until both are on screen. A group is an element, its
///         members are its children, and two groups cannot interfere.
///     </para>
///     <para>
///         ⚠ <b>The arrow keys move the selection, not just the focus, and that is deliberate.</b>
///         It is what every desktop toolkit does and what the ARIA authoring practices specify, and
///         the reason is that a radio group is <i>one</i> stop in the tab order — Tab enters it at
///         whichever radio is chosen and Tab leaves it, so arrowing without selecting would leave
///         the keyboard with no way to choose at all.
///     </para>
///     <para>
///         Which is also why the tab index roves: only the chosen radio is a stop, so tabbing into
///         a group with a selection lands on the selection rather than at the top of the list.
///     </para>
/// </remarks>
public sealed partial class RadioGroup : Control {
    readonly List<RadioButton> options = [];

    /// <inheritdoc />
    protected override string TagName => "radio-group";

    /// <inheritdoc />
    /// <remarks>
    ///     The group itself is never a tab stop. Its radios are — one of them — which is what the
    ///     roving index below arranges.
    /// </remarks>
    protected override bool AcceptsFocus => false;

    /// <summary>Which choice is made, or <c>null</c> if none is.</summary>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial string? Value { get; set; }

    /// <summary>The radios, in order.</summary>
    public IReadOnlyList<RadioButton> Options => options;

    /// <summary>Raised when the choice changes.</summary>
    public event Action<RadioGroup, string?>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // On the group rather than on each radio. A radio does not know what is next to it, and a
        // group that subscribed per child would have to unsubscribe per removal — which is exactly
        // the bookkeeping routed events exist to avoid.
        AddHandler<ClickEvent>(static (element, args) => ((RadioGroup) element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((RadioGroup) element).Keyed(args));
    }

    /// <summary>Adds a choice.</summary>
    /// <param name="value">What choosing it means.</param>
    /// <param name="label">What it says.</param>
    /// <returns>The radio.</returns>
    /// <remarks>
    ///     ⚠ <b>Not called <c>Add</c>.</b> <c>UiElement.Add</c>
    ///     already is, and a one-string overload on the derived type would win over it by C#'s rule
    ///     that a derived candidate beats a base one — so <c>group.Add("div")</c> would silently
    ///     make a radio labelled "div". Every container in this set names its own method for the
    ///     same reason.
    /// </remarks>
    public RadioButton AddOption(string value, string? label = null) {
        ArgumentNullException.ThrowIfNull(value);

        var option = Add<RadioButton>();
        option.Value = value;
        option.Label = label ?? value;

        options.Add(option);

        // Chosen if it is what the group was already set to — which happens when the value is
        // assigned before the options exist, and that is the ordinary case for a group built from
        // saved settings.
        option.IsChecked = string.Equals(value, Value, StringComparison.Ordinal);
        Rove();

        return option;
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not RadioButton option || !ReferenceEquals(option.Parent, this)) {
            return;
        }

        Value = option.Value;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || !args.Has(ModifierKeys.None)) {
            return;
        }

        var step = args.Key switch {
            InputKey.Down or InputKey.Right => 1,
            InputKey.Up or InputKey.Left => -1,
            _ => 0
        };

        if (step == 0 || options.Count == 0) {
            return;
        }

        // ⚠ Wraps, unlike arrow navigation across the document. A radio group is a cycle rather than
        // a layout: it has a first and a last member and no geometry worth respecting, and stopping
        // at the end would make Down at the bottom of a three-item group do nothing at all — which
        // reads as the keyboard being broken rather than as a boundary.
        var current = options.FindIndex(static option => option.IsChecked);
        var next = current < 0
            ? step > 0 ? 0 : options.Count - 1
            : (current + step + options.Count) % options.Count;

        Value = options[next].Value;
        Document.Focus(options[next]);

        args.Handled = true;
    }

    void OnValueChanged(string? previous, string? current) {
        foreach (var option in options) {
            option.IsChecked = string.Equals(option.Value, current, StringComparison.Ordinal);
        }

        Rove();

        Raise(new ValueChangedEvent<string> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    /// <summary>Makes the chosen radio the group's one tab stop.</summary>
    /// <remarks>
    ///     With nothing chosen the first radio is the stop, so that Tab can reach an empty group at
    ///     all. Negative rather than removing focusability, because the arrows still have to be able
    ///     to move the focus onto the others.
    /// </remarks>
    void Rove() {
        var stop = options.FindIndex(static option => option.IsChecked);
        if (stop < 0) {
            stop = 0;
        }

        for (var i = 0; i < options.Count; i++) {
            options[i].TabIndex = i == stop ? 0 : -1;
        }
    }
}
