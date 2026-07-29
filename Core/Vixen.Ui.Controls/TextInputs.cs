// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>A single-line field.</summary>
public sealed partial class TextBox : TextField {
    /// <inheritdoc />
    protected override string TagName => "textbox";
}

/// <summary>A field for several lines of text.</summary>
/// <remarks>
///     ⚠ <b>Today it is a taller <see cref="TextBox" />, and the reason is upstream of this file.</b>
///     The framework draws one <see cref="TextLine" /> per element and nothing wraps it, so there is
///     no second line for Enter to start. What this type does have is its own tag — so the theme can
///     give it the height and the alignment a text area needs — and its own place for the wrapping
///     to land when <c>Vixen.Ui.Text</c>'s line breaker reaches the draw path. Shipping the tag now
///     means the markup that uses it will not have to change then.
/// </remarks>
public sealed partial class TextArea : TextField {
    /// <inheritdoc />
    protected override string TagName => "textarea";
}

/// <summary>A field with a magnifying glass and a way to empty it.</summary>
/// <remarks>
///     The clear button is a real <see cref="IconButton" /> rather than something drawn, because it
///     has to be clickable, hoverable and — for a user who cannot use a mouse — reachable. It is
///     hidden by the theme while the field is empty, using the same <c>empty</c> class the
///     placeholder is shown by, so the two can never disagree about whether there is anything in the
///     box.
/// </remarks>
public sealed partial class SearchBox : TextField {
    /// <inheritdoc />
    protected override string TagName => "search-box";

    /// <summary>The magnifying glass.</summary>
    public Icon SearchIcon { get; private set; } = null!;

    /// <summary>The button that empties it.</summary>
    public IconButton ClearButton { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        SearchIcon = Part<Icon>();
        SearchIcon.Geometry = ControlIcons.Search;
        SearchIcon.FillRule = PathFillRule.EvenOdd;
        Document.Move(SearchIcon, 0);

        ClearButton = Part<IconButton>();
        ClearButton.LeadingIcon.Geometry = ControlIcons.Close;
        ClearButton.Variant = ControlVariant.Subtle;
        ClearButton.Label = "Clear";

        // ⚠ Not a tab stop. It is a shortcut for something the keyboard can already do — select all
        // and press Delete — and putting it in the tab order would mean Tab out of every search box
        // in the application lands on a button instead of on the next field.
        ClearButton.TabIndex = -1;

        AddHandler<ClickEvent>(static (element, args) => ((SearchBox) element).Cleared(args));
    }

    void Cleared(ClickEvent args) {
        if (!ReferenceEquals(args.Source, ClearButton)) {
            return;
        }

        Value = string.Empty;
        Document.Focus(this);

        args.Handled = true;
    }
}

/// <summary>A field that holds a number, with arrows, spinners and a drag to scrub it.</summary>
/// <remarks>
///     <para>
///         <b>The number is the value and the text is a rendering of it.</b> <c>Value</c> is still a
///         string — it is a field, and half-typed input like <c>-</c> or <c>1.</c> has to be
///         allowed to exist for as long as somebody is typing it — but <see cref="Number" /> is what
///         the application reads, and it is only ever assigned from text that parses.
///     </para>
///     <para>
///         ⚠ <b>Dragging scrubs only while the field is not focused, and that is the whole trick.</b>
///         A field the user is editing has to let them select text with the pointer; a field they
///         are not editing has nothing to select, so the drag is free for something better. It is
///         what every 3D application's inspector does, and it is why a click that does not move
///         still focuses the field: the two gestures start identically and are told apart by
///         whether anything happened next.
///     </para>
/// </remarks>
public sealed partial class NumericInput : TextField {
    bool scrubbing;
    float scrubbed;
    double origin;

    /// <inheritdoc />
    protected override string TagName => "numeric-input";

    /// <summary>What it holds.</summary>
    [UiProperty(Coerce = nameof(CoerceNumber), Changed = nameof(OnNumberChanged))]
    public partial double Number { get; set; }

    /// <summary>The smallest it may be.</summary>
    [UiProperty(Default = double.NegativeInfinity, Changed = nameof(OnRangeChanged))]
    public partial double Minimum { get; set; }

    /// <summary>The largest.</summary>
    [UiProperty(Default = double.PositiveInfinity, Changed = nameof(OnRangeChanged))]
    public partial double Maximum { get; set; }

    /// <summary>How much one arrow press, one spinner click or one pixel of drag is worth.</summary>
    [UiProperty(Default = 1.0)]
    public partial double Step { get; set; }

    /// <summary>How many decimal places the text shows.</summary>
    [UiProperty(Changed = nameof(OnDecimalsChanged))]
    public partial int Decimals { get; set; }

    /// <summary>Raised when the number changes.</summary>
    public event Action<NumericInput, double>? NumberChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // On the capture leg, so that a press this control wants to turn into a scrub never reaches
        // the base class's caret placement. Registering on the bubble leg and marking it handled
        // afterwards would be too late — the caret would already have moved.
        AddHandler<PointerEvent>(
            static (element, args) => ((NumericInput) element).Scrub(args),
            RoutingStrategy.Capture
        );

        AddHandler<KeyEvent>(static (element, args) => ((NumericInput) element).Stepped(args));
        AddHandler<FocusEvent>(static (element, args) => ((NumericInput) element).Blurred(args));
    }

    /// <summary>Adds a number of steps to the value.</summary>
    /// <param name="steps">How many, positive or negative.</param>
    public void Nudge(double steps) => Number += steps * Step;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Half-typed numbers are allowed through and complete ones are not reformatted.</b> A
    ///     field that rewrote <c>1.</c> to <c>1</c> would delete the decimal point the moment it was
    ///     typed, and one that rejected <c>-</c> could never be given a negative number at all. What
    ///     is refused is text that could never become a number, which keeps letters out without
    ///     fighting the person at the keyboard.
    /// </remarks>
    protected override string? Coerce(string? value) {
        if (string.IsNullOrEmpty(value)) {
            return value;
        }

        foreach (var character in value) {
            if (!char.IsAsciiDigit(character) && character is not ('-' or '+' or '.' or ',' or 'e' or 'E')) {
                return Value;
            }
        }

        return value;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Enter is what commits a half-typed number: the text is reformatted to what the number
    ///     actually is, so <c>1.</c> becomes <c>1</c> and <c>007</c> becomes <c>7</c>. Doing it on
    ///     every keystroke is what makes a numeric field impossible to type into.
    /// </remarks>
    protected override void OnSubmit() {
        base.OnSubmit();
        Commit();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Any repeated tap takes the whole number, rather than the word under it.</b> A number
    ///     is one thing to the person editing it, and the word breaker is not wrong to disagree —
    ///     <c>-1.5e3</c> is four words by UAX#29 — it is answering a question nobody asked here.
    ///     Double-clicking a field to type a new value into it and getting one digit group selected
    ///     is the field arguing with the gesture.
    /// </remarks>
    protected override void SelectAt(int index, int count) => SelectAll();

    /// <summary>Reads the text back into the number and then writes it out again.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, and the second is not redundant.</b> Rereading <c>007</c> gives seven,
    ///     which the number already was — so nothing changed, so nothing reformatted, and the field
    ///     would sit there still saying <c>007</c>. Formatting unconditionally is what makes
    ///     committing mean "show me what you actually stored".
    /// </remarks>
    void Commit() {
        Reread();
        Format();
    }

    void Blurred(FocusEvent args) {
        if (!args.Gained) {
            Commit();
        }
    }

    double CoerceNumber(double value) =>
        double.IsNaN(value) ? Number : Math.Clamp(value, Minimum, Maximum);

    void OnNumberChanged(double previous, double current) {
        Format();
        NumberChanged?.Invoke(this, current);
    }

    void OnRangeChanged(double previous, double current) => Number = CoerceNumber(Number);

    void OnDecimalsChanged(int previous, int current) => Format();

    void Format() => Value = Number.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Reads the number back out of the text after a keystroke.</summary>
    /// <remarks>
    ///     ⚠ Silent on text that does not parse. A field mid-edit holds <c>-</c> and <c>1.</c> for as
    ///     long as somebody is typing them, and a control that reset the number to zero on every one
    ///     of those would fight every negative number ever entered.
    /// </remarks>
    void Reread() {
        if (double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) {
            Number = parsed;
        }
    }

    void Stepped(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        var steps = args.Key switch {
            InputKey.Up => 1,
            InputKey.Down => -1,
            InputKey.PageUp => 10,
            InputKey.PageDown => -10,
            _ => 0
        };

        if (steps == 0) {
            return;
        }

        // Shift multiplies and Alt divides, which is the convention in every content tool. Neither
        // is a coarse and fine mode this has to remember — they are read off the keystroke.
        var scale = args.Modifiers.HasFlag(ModifierKeys.Shift) ? 10d
            : args.Modifiers.HasFlag(ModifierKeys.Alt) ? 0.1d
            : 1d;

        Nudge(steps * scale);
        args.Handled = true;
    }

    void Scrub(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary && !IsFocused && !ReadOnly:
                scrubbing = true;
                scrubbed = 0f;
                origin = Number;

                Document.CapturePointer(this);
                args.Handled = true;
                break;

            case PointerAction.Moved when scrubbing:
                scrubbed += args.X - LastX;
                Number = origin + (scrubbed * Step);

                args.Handled = true;
                break;

            case PointerAction.Released when scrubbing:
                scrubbing = false;
                Document.ReleasePointer();

                // A press that never moved is a click, and a click on a numeric field means "let me
                // type in it". The threshold is the gesture recogniser's, so that what counts as a
                // wobble here is what counts as one everywhere else in the document.
                if (MathF.Abs(scrubbed) < Document.Gestures.Settings.TouchSlop) {
                    Document.Focus(this);
                    SelectAll();
                }

                args.Handled = true;
                break;

            default:
                // A press while focused, a secondary button, a move with nothing held. All of them
                // belong to the field's own caret handling, which runs on the bubble leg after this
                // one declines to mark the event handled.
                break;
        }

        LastX = args.X;
    }

    /// <summary>Where the pointer was last seen, so a move can be turned into a delta.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept here rather than read from <see cref="DragEvent" />, which already carries one.</b>
    ///     A drag does not begin until the pointer has passed the slop threshold, and a scrub has to
    ///     respond to the first pixel — waiting eight of them makes the field feel stuck before it
    ///     suddenly jumps.
    /// </remarks>
    float LastX { get; set; }
}
