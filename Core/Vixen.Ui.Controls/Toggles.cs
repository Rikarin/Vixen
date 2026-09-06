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
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off <see cref="IsChecked" /> on demand rather than mirrored into a field
    ///         when it changes, which is the whole reason
    ///         <see cref="UiElement.NativeAccessibleState" /> is a virtual.</b> There is no second
    ///         copy to update, no callback to remember, and no state in which a checkbox is ticked
    ///         on screen and unticked to a screen reader.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not <see cref="ElementState.Checked" />, although that is set beside it.</b> A
    ///         <see cref="TabItem" /> and an <see cref="Option" /> use the same style flag to mean
    ///         <i>selected</i>, so a derivation from the cascade would announce the open tab as a
    ///         ticked checkbox. What the flag means is the control's to say, and this is it saying
    ///         so.
    ///     </para>
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        IsChecked ? AccessibleStates.Checked : AccessibleStates.None;

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

    /// <summary>Whether it has to be ticked before the form is acceptable.</summary>
    /// <remarks>
    ///     <para>
    ///         The consent box, which is the case this exists for: a box that has to be ticked rather
    ///         than a box whose two answers are both allowed. <c>:optional</c> is the negation, as it
    ///         is on <see cref="TextField.Required" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A half-ticked box is <i>not</i> ticked, so a required one showing a dash is
    ///         invalid.</b> That falls out of <see cref="IsIndeterminate" /> being a third appearance
    ///         rather than a third value — but it is worth stating, because the alternative reading
    ///         ("it has been touched, so let it through") is the one a form would silently take if the
    ///         verdict were computed from the class instead.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnRequiredChanged))]
    public partial bool Required { get; set; }

    /// <summary>The box the tick is drawn in.</summary>
    public UiElement Box { get; private set; } = null!;

    /// <summary>Whether what it holds is acceptable.</summary>
    public bool IsValid => !Required || (IsChecked && !IsIndeterminate);

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.CheckBox;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="AccessibleStates.Mixed" /> replaces
    ///     <see cref="AccessibleStates.Checked" /> rather than joining it</b>, because
    ///     <c>aria-checked</c> is one value with three settings and "ticked and half-ticked at once"
    ///     is not one of them. It is the same reason <see cref="IsIndeterminate" /> is a separate
    ///     flag rather than a third value of <see cref="ToggleBase.IsChecked" />: the appearance is
    ///     third, the value is still a <c>bool</c>, and this is where the two are reconciled for
    ///     somebody who cannot see the dash.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        (IsIndeterminate ? AccessibleStates.Mixed : base.NativeAccessibleState)
        | (Required ? AccessibleStates.Required : AccessibleStates.None)
        | (IsValid ? AccessibleStates.None : AccessibleStates.Invalid);

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

        // ⚠ Here rather than only on a change, which is the trap `TextField` hit first. A box that is
        // not required is valid from birth and never goes through a change, so without this call it
        // would carry neither `:valid` nor `:invalid` for its whole life — indistinguishable to a
        // selector from a container that does not validate at all.
        Revalidate();
    }

    /// <summary>Republishes the verdict.</summary>
    /// <remarks>
    ///     Public for <see cref="TextField.Revalidate" />'s reason: what makes a box acceptable can
    ///     be something other than the box — one of a set of which at least two must be ticked — and
    ///     nothing about that changes when this one is clicked.
    /// </remarks>
    public void Revalidate() {
        if (FieldValidity.Publish(this, Required, IsValid)) {
            InvalidateAccessibility();
        }
    }

    /// <inheritdoc />
    protected override void OnChecked(bool current) {
        base.OnChecked(current);
        Revalidate();
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

        // ⚠ <b>And the state bit, which is what `:indeterminate` is.</b> Not a third value of
        // `Checked`: CSS matches `:indeterminate` and *not* `:checked` on a half-ticked box, so the
        // two bits are independent and setting both would make `checked:` apply to a box showing a
        // dash. The class stays for the reason `TextField.ReadOnly`'s does — the themes select on it.
        State = current ? State | ElementState.Indeterminate : State & ~ElementState.Indeterminate;

        // ⚠ The class is the wrong half to rely on. `NativeAccessibleState` above swaps `Checked`
        // for `Mixed` from this flag alone, and a class change is a cascade invalidation that
        // touches nothing a bridge reads — so without this line a screen reader kept announcing a
        // half-ticked box as ticked, or as unticked, for the rest of the session.
        InvalidateAccessibility();

        // A required box showing a dash is not ticked and therefore not acceptable, so the verdict
        // follows this flag as well as `IsChecked`.
        Revalidate();
    }

    void OnRequiredChanged(bool previous, bool current) {
        Revalidate();

        // ⚠ Unconditionally, unlike the call inside `Revalidate`. `Required` is its own reported
        // flag, so a box that goes from optional to required while ticked moves nothing about the
        // verdict and still has something new to announce.
        InvalidateAccessibility();
    }
}

/// <summary>A switch: a track, and a knob that slides along it.</summary>
/// <remarks>
///     ⚠ <b>Not a checkbox with a different skin, and the difference is what it means rather than
///     how it looks.</b> A checkbox is a value in a form that is applied when the form is; a switch
///     takes effect the moment it is flipped. A control set that makes them interchangeable produces
///     dialogs with a switch beside an OK button, where nobody can tell whether Cancel undoes it.
///     <para>
///         ⚠ <b>Which is also why it does not validate, and the absence is the answer rather than
///         the next thing to add.</b> <see cref="CheckBox" /> and <see cref="RadioGroup" /> have a
///         <c>Required</c> and a verdict; a switch deliberately has neither, because "this must be
///         on before the form is acceptable" is a sentence about a form being submitted and a switch
///         is not submitted — it has already happened. A required switch is a setting the
///         application refuses to let the user turn off, which is a disabled switch or a
///         confirmation, not a validity. So a <c>&lt;Switch&gt;</c> carries <i>neither</i>
///         <c>:valid</c> nor <c>:invalid</c>, which is Selectors 4 § 10.6's own answer for an
///         element that does not take part in constraint validation — see
///         <c>FieldValidity</c> for why that is two bits and not one.
///     </para>
/// </remarks>
public sealed partial class Switch : ToggleBase {
    /// <inheritdoc />
    protected override string TagName => "switch";

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>switch</c>, which exists precisely because the distinction in the remarks above
    ///     is one the user needs: a screen reader says "on"/"off" for a switch and
    ///     "ticked"/"unticked" for a checkbox.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Switch;

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
    protected override AccessibleRole NativeRole => AccessibleRole.Radio;

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
    /// <inheritdoc />
    protected override string TagName => "radio-group";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One of the few containers that <i>is</i> a node, and the reason is the sentence
    ///     above about the tab order.</b> A group is one stop, so a screen reader has to announce
    ///     "three of five" as the arrows move — and it can only do that if the group is in the tree
    ///     as the parent of its radios rather than being read straight through.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.RadioGroup;

    /// <inheritdoc />
    /// <remarks>
    ///     The group itself is never a tab stop. Its radios are — one of them — which is what the
    ///     roving index below arranges.
    /// </remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The group reports it, not the radios, and that is a stated divergence from HTML.</b>
    ///     A browser puts <c>required</c> on each <c>&lt;input type=radio&gt;</c> and then has to
    ///     re-derive the group from the shared <c>name</c> in order to decide whether the requirement
    ///     is met. Here the group is an element and <see cref="Value" /> is the one fact, so the
    ///     declaration and the verdict belong on the same object as the answer — a member cannot be
    ///     required on its own, because "this one in particular must be chosen" is not something a
    ///     set of mutually exclusive choices can mean.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        (Required ? AccessibleStates.Required : AccessibleStates.None)
        | (IsValid ? AccessibleStates.None : AccessibleStates.Invalid);

    /// <summary>Which choice is made, or <c>null</c> if none is.</summary>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial string? Value { get; set; }

    /// <summary>Whether one of the choices has to be made.</summary>
    /// <remarks>
    ///     ⚠ <b>A group question rather than a control one</b> — see
    ///     <see cref="NativeAccessibleState" />. A group with no choice made is invalid the moment it
    ///     is marked, on <see cref="TextField.Required" />'s terms: the state is what the group is
    ///     <i>in</i>, and deferring it until a submit is what makes a form report four mistakes at
    ///     once at the end.
    /// </remarks>
    [UiProperty(Changed = nameof(OnRequiredChanged))]
    public partial bool Required { get; set; }

    /// <summary>Whether the choice made is acceptable.</summary>
    /// <remarks>
    ///     ⚠ Reads <see cref="Value" /> rather than counting checked radios. A group whose value
    ///     names a choice that has not been added yet — the ordinary case for one built from saved
    ///     settings — has a choice made; it just has nothing to show it on until the options arrive.
    /// </remarks>
    public bool IsValid => !Required || Value is not null;

    /// <summary>The radios, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the children rather than kept, and a fresh snapshot each time.</b> A list
    ///     the group maintained would be a second place the truth lived — one that markup could not
    ///     write to, so a <c>&lt;RadioButton&gt;</c> written as a nested tag drew a radio that was in
    ///     the tree and not in the group: no exclusivity, no roving tab index, no <see cref="Value" />.
    ///     That is <c>Accordion.Sections</c>' arrangement and it is the right one for the same
    ///     reasons; <see cref="OnChildAdded" /> is what does the part a snapshot cannot.
    /// </remarks>
    public IReadOnlyList<RadioButton> Options => [.. Children.OfType<RadioButton>()];

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

        // ⚠ Here, not only on a change: an optional group is valid from birth and never goes through
        // one, so without this call it would carry neither `:valid` nor `:invalid` for its whole life.
        Revalidate();
    }

    /// <summary>Republishes the verdict.</summary>
    /// <remarks>
    ///     Public on <see cref="TextField.Revalidate" />'s terms — what makes a choice acceptable can
    ///     depend on something that is not this group.
    /// </remarks>
    public void Revalidate() {
        if (FieldValidity.Publish(this, Required, IsValid)) {
            InvalidateAccessibility();
        }
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

        // ⚠ Nothing else, and that is the point. Registering, restating and re-roving all happen in
        // `OnChildAdded`, which `Add<RadioButton>` above has already run — so this method and a
        // nested `<RadioButton Value="…" />` arrive at exactly the same state by the same code.
        // ⚠ The one thing it has to redo is the check, because the value is assigned *after* the
        // child arrived: the hook saw an option with no value yet.
        Restate();

        return option;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Two things a snapshot cannot do</b>: give the arriving radio the checked state the
    ///     group's <see cref="Value" /> implies — which is the ordinary case for a group built from
    ///     saved settings, where the value is assigned before any option exists — and put the roving
    ///     tab index back, since which radio is the group's single tab stop depends on how many there
    ///     are and which is chosen.
    /// </remarks>
    protected override void OnChildAdded(UiElement child) {
        base.OnChildAdded(child);

        if (child is not RadioButton) {
            return;
        }

        Restate();
    }

    /// <summary>Brings every radio's checked state and the tab stop into line with <see cref="Value" />.</summary>
    void Restate() {
        foreach (var option in Options) {
            option.IsChecked = option.Value is { } value && string.Equals(value, Value, StringComparison.Ordinal);
        }

        Rove();
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

        var options = Options;

        if (step == 0 || options.Count == 0) {
            return;
        }

        // ⚠ Wraps, unlike arrow navigation across the document. A radio group is a cycle rather than
        // a layout: it has a first and a last member and no geometry worth respecting, and stopping
        // at the end would make Down at the bottom of a three-item group do nothing at all — which
        // reads as the keyboard being broken rather than as a boundary.
        var current = IndexOfChecked(options);
        var next = current < 0
            ? step > 0 ? 0 : options.Count - 1
            : (current + step + options.Count) % options.Count;

        Value = options[next].Value;
        Document.Focus(options[next]);

        args.Handled = true;
    }

    void OnValueChanged(string? previous, string? current) {
        Restate();

        // Before the notifications rather than after, so a handler that reads `IsValid` — which is
        // what a submit button's enablement is — sees the verdict on the choice it was just handed.
        Revalidate();

        Raise(new ValueChangedEvent<string> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    void OnRequiredChanged(bool previous, bool current) {
        Revalidate();

        // Unconditionally, because `Required` is reported in its own right: a group that goes from
        // optional to required with a choice already made moves nothing about the verdict and still
        // has something new to announce.
        InvalidateAccessibility();
    }

    /// <summary>Makes the chosen radio the group's one tab stop.</summary>
    /// <remarks>
    ///     With nothing chosen the first radio is the stop, so that Tab can reach an empty group at
    ///     all. Negative rather than removing focusability, because the arrows still have to be able
    ///     to move the focus onto the others.
    /// </remarks>
    void Rove() {
        var options = Options;
        var stop = IndexOfChecked(options);

        if (stop < 0) {
            stop = 0;
        }

        for (var i = 0; i < options.Count; i++) {
            options[i].TabIndex = i == stop ? 0 : -1;
        }
    }

    /// <summary>Which radio is chosen, or -1.</summary>
    /// <remarks>
    ///     Taken over a snapshot the caller already holds rather than over <see cref="Options" />,
    ///     because two reads of that property are two walks of the children and two different lists
    ///     the moment anything between them adds one.
    /// </remarks>
    static int IndexOfChecked(IReadOnlyList<RadioButton> options) {
        for (var i = 0; i < options.Count; i++) {
            if (options[i].IsChecked) {
                return i;
            }
        }

        return -1;
    }
}
