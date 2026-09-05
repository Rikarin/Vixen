// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>One choice in a <see cref="SegmentedControl" />.</summary>
/// <remarks>
///     ⚠ <b>A <see cref="ToggleBase" /> reporting <see cref="AccessibleRole.Radio" /> rather than a
///     <see cref="ToggleButton" /> reporting <c>button</c>.</b> The two look identical and are not
///     the same thing to a screen reader: a strip of three toggle buttons is announced as three
///     independent pressed-or-not buttons, where a segmented control is one question with three
///     answers and has to say "two of three". <see cref="ToggleBase.CanUncheck" /> is false for
///     <c>RadioButton</c>'s reason — clicking the chosen segment again must leave it chosen, or the
///     control reaches a state with nothing selected that the keyboard cannot get out of.
/// </remarks>
public sealed partial class Segment : ToggleBase {
    /// <inheritdoc />
    protected override string TagName => "segment";

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Radio;

    /// <inheritdoc />
    protected override bool CanUncheck => false;

    /// <inheritdoc />
    /// <remarks>
    ///     True. A segment is a button in every way a keyboard user can tell, and one that answered
    ///     nothing to the key every other button responds to would read as broken.
    /// </remarks>
    protected override bool ActivatesOnEnter => true;

    /// <summary>What choosing it means, for <see cref="SegmentedControl.Value" />.</summary>
    [UiProperty]
    public partial string? Value { get; set; }
}

/// <summary>A row of joined buttons of which exactly one is chosen.</summary>
/// <remarks>
///     <para>
///         <b>What a view switcher, a mode picker and an alignment control are.</b> The editor draws
///         one out of a bare <c>UiElement</c> and CSS — <c>ToolbarPresenter.cs:212</c> plus
///         <c>EditorTheme.vcss:289</c> — so no application could reach it and the version that
///         existed had no keyboard, no exclusivity and no accessible structure.
///     </para>
///     <para>
///         ⚠ <b>The same model as <see cref="RadioGroup" />, deliberately, and not a subclass of
///         it.</b> The exclusion, the roving tab index and the wrapping arrows are the same
///         behaviour and the members are not: a radio group's children are radios with a dot and a
///         label beside them, a segmented control's are joined buttons, and a base class shared
///         between them would have to be parameterised on the member type to say anything useful.
///         What it would save is thirty lines; what it would cost is a hierarchy where
///         <c>&lt;RadioButton&gt;</c> inside a <c>&lt;SegmentedControl&gt;</c> type-checks.
///     </para>
///     <para>
///         <b>Single selection only.</b> Multiple selection is a different control — a strip of
///         <see cref="ToggleButton" />s in a <see cref="Toolbar" />, which is already reachable —
///         and a mode flag here would make <see cref="Value" /> mean two things.
///     </para>
/// </remarks>
public sealed partial class SegmentedControl : Control {
    /// <inheritdoc />
    protected override string TagName => "segmented-control";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>radiogroup</c>, for <see cref="RadioGroup" />'s reason: the group is one tab stop,
    ///     so a screen reader can only announce "two of three" as the arrows move if the group is in
    ///     the tree as the parent of its segments.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.RadioGroup;

    /// <summary>Which segment is chosen, or <c>null</c> if none is.</summary>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial string? Value { get; set; }

    /// <summary>The segments, in order.</summary>
    /// <remarks>A fresh snapshot from the children, on <see cref="RadioGroup.Options" />'s terms.</remarks>
    public IReadOnlyList<Segment> Segments => [.. Children.OfType<Segment>()];

    /// <summary>Raised when the choice changes.</summary>
    public event Action<SegmentedControl, string?>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<ClickEvent>(static (element, args) => ((SegmentedControl) element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((SegmentedControl) element).Keyed(args));
    }

    /// <summary>Adds a segment.</summary>
    /// <param name="value">What choosing it means.</param>
    /// <param name="label">What it says. The value, if omitted.</param>
    /// <returns>The segment.</returns>
    /// <remarks>
    ///     ⚠ <b>Not called <c>Add</c></b>, for <see cref="RadioGroup.AddOption" />'s reason: a
    ///     derived one-string overload beats <c>UiElement.Add(string)</c> by C#'s own rule, so
    ///     <c>strip.Add("div")</c> would quietly make a segment labelled "div".
    /// </remarks>
    public Segment AddSegment(string value, string? label = null) {
        ArgumentNullException.ThrowIfNull(value);

        var segment = Add<Segment>();
        segment.Value = value;
        segment.Label = label ?? value;

        // The value is assigned after the child arrived, so the hook saw a segment with none.
        Restate();

        return segment;
    }

    /// <inheritdoc />
    protected override void OnChildAdded(UiElement child) {
        base.OnChildAdded(child);

        if (child is not Segment) {
            return;
        }

        Restate();
    }

    void Restate() {
        var segments = Segments;

        foreach (var segment in segments) {
            segment.IsChecked = segment.Value is { } value && string.Equals(value, Value, StringComparison.Ordinal);
        }

        var stop = IndexOfChecked(segments);

        if (stop < 0) {
            stop = 0;
        }

        for (var i = 0; i < segments.Count; i++) {
            segments[i].TabIndex = i == stop ? 0 : -1;
        }
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not Segment segment || !ReferenceEquals(segment.Parent, this)) {
            return;
        }

        Value = segment.Value;
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

        var segments = Segments;

        if (step == 0 || segments.Count == 0) {
            return;
        }

        var current = IndexOfChecked(segments);
        var next = current < 0
            ? step > 0 ? 0 : segments.Count - 1
            : (current + step + segments.Count) % segments.Count;

        Value = segments[next].Value;
        Document.Focus(segments[next]);

        args.Handled = true;
    }

    void OnValueChanged(string? previous, string? current) {
        Restate();

        Raise(new ValueChangedEvent<string> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    static int IndexOfChecked(IReadOnlyList<Segment> segments) {
        for (var i = 0; i < segments.Count; i++) {
            if (segments[i].IsChecked) {
                return i;
            }
        }

        return -1;
    }
}
