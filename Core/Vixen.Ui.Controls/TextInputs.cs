// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>A single-line field.</summary>
/// <remarks>
///     ⚠ <b>Not sealed, alone among the leaf controls in this set, and for one reason.</b> ARIA 1.2's
///     editable combo box is a <i>text input</i> carrying <c>role="combobox"</c> and its own
///     <c>aria-expanded</c> — so <c>ComboBox</c>'s editor has to be a text box that answers two
///     accessibility virtuals differently. The alternative was assigning the role and writing the
///     expanded state into <c>DeclaredAccessibleState</c> from an event, which is a second copy of
///     "is the list open" kept by a handler; a derived type reads the popover and cannot disagree
///     with it. Nothing else derives from this and nothing else should.
/// </remarks>
public partial class TextBox : TextField {
    /// <inheritdoc />
    protected override string TagName => "textbox";
}

/// <summary>A field for several lines of text.</summary>
/// <remarks>
///     <para>
///         <b>A <see cref="TextBox" /> that takes a line break, and that is the whole difference.</b>
///         The wrapping was already there — the theme puts <c>white-space: normal</c> on this tag's
///         text and <c>TextLayout</c> breaks on both a mandatory break and a measured one — and what
///         was missing was any way to <i>get</i> a newline into the value: Enter submitted, so a box
///         offered for a YAML document could hold exactly one line of it. That is the shape of bug
///         that reads as a field which will not accept what you type.
///     </para>
///     <para>
///         ⚠ <b>Ctrl-Enter still submits</b>, so a form whose default button lives behind
///         <c>Submitted</c> is still reachable from inside the one field that has claimed the plain
///         key. That is a keybinding collision with exactly two claimants and only one plain key —
///         the field wants a line break, the form wants its default action, and a dialog's accept
///         button is not focused while a field is, so Enter never reaches it as an activation.
///         <c>TextField.Keyed</c> carries the resolution; <see cref="SubmitEvent" /> is the routed
///         half, so an ancestor can hear it without holding a reference to the field.
///     </para>
/// </remarks>
public sealed partial class TextArea : TextField {
    /// <inheritdoc />
    protected override string TagName => "textarea";

    /// <inheritdoc />
    protected override bool AcceptsNewlines => true;
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

    /// <inheritdoc />
    /// <remarks>ARIA <c>searchbox</c>: a text box whose text is a query rather than a value.</remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.SearchBox;

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
        ClearButton.Label = ControlStrings.TextInputClear.Text;

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
///     <para>
///         ⚠ <b>One pixel is worth a percentage of the number, not a fixed amount of it.</b> A field
///         that always moved by <see cref="Step" /> was dead on anything large: a directional light
///         is a hundred thousand lux, and a scrub that shifted it by one moved it by a thousandth of
///         a percent per pixel. The light was only the messenger — a range in centimetres, a budget
///         in bytes and a distance in metres all have the same shape — so the cure is in the
///         arithmetic rather than in a number chosen per member. See <see cref="RelativeStep" />.
///     </para>
/// </remarks>
public sealed partial class NumericInput : TextField {
    bool scrubbing;
    float scrubbed;
    double offset;
    double rate;
    double origin;

    /// <inheritdoc />
    protected override string TagName => "numeric-input";

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>spinbutton</c>, not <c>textbox</c>. The difference a screen-reader user acts on is
    ///     that a spin button announces the arrow keys as a way to change the value, which for this
    ///     control is true and for a plain field is not.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.SpinButton;

    /// <summary>What it holds.</summary>
    [UiProperty(Coerce = nameof(CoerceNumber), Changed = nameof(OnNumberChanged))]
    public partial double Number { get; set; }

    /// <summary>The smallest it may be.</summary>
    [UiProperty(Default = double.NegativeInfinity, Changed = nameof(OnRangeChanged))]
    public partial double Minimum { get; set; }

    /// <summary>The largest.</summary>
    [UiProperty(Default = double.PositiveInfinity, Changed = nameof(OnRangeChanged))]
    public partial double Maximum { get; set; }

    /// <summary>The smallest one arrow press, one spinner click or one pixel of drag is worth.</summary>
    /// <remarks>
    ///     ⚠ <b>A floor rather than the whole answer</b>, since <see cref="RelativeStep" /> exists.
    ///     It is what the field moves by while it is small enough for a fixed amount to still make
    ///     sense, and what it moves by at nought.
    /// </remarks>
    [UiProperty(Default = 1.0)]
    public partial double Step { get; set; }

    /// <summary>How much one step is worth as a fraction of the number's own magnitude.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A percentage per pixel, which is the only rate that works across the range a
    ///         number can hold.</b> One hundredth is a hundred pixels to double a value or to take it
    ///         to nothing, whatever the value is: a hundred thousand lux and a roughness of one
    ///         scrub at the same <i>felt</i> speed, because the thing a person is adjusting is the
    ///         proportion rather than the absolute amount.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Step" /> is the floor, and that is the deliberate answer to zero.</b>
    ///         Nought has no magnitude to take a fraction of, so a purely proportional rate would
    ///         leave a field sitting at zero unscrubbable — and zero is the value a field is most
    ///         often dragged <i>away</i> from. Taking the larger of the two means the proportional
    ///         part only takes over once it has outgrown the absolute one, which is exactly where
    ///         the absolute one had stopped being useful: at a hundred times <see cref="Step" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set it to zero for a field that genuinely wants a fixed amount per pixel</b> — a
    ///         grid pitch, a page number — and the arithmetic collapses back to
    ///         <c>origin + pixels × Step</c> exactly.
    ///     </para>
    /// </remarks>
    [UiProperty(Default = 0.01)]
    public partial double RelativeStep { get; set; }

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
    /// <remarks>
    ///     ⚠ <b>The step is worked out from where the number is now, so repeated presses compound.</b>
    ///     Holding Up on a hundred thousand lux climbs by a percent of whatever it has reached rather
    ///     than by a percent of where it started — which is the behaviour a person expects from a key
    ///     they are pressing over and over, and the opposite of what a drag wants. A drag freezes its
    ///     rate instead; <see cref="Scrub" /> says why.
    /// </remarks>
    public void Nudge(double steps) => Number += steps * StepAt(Number);

    /// <summary>What one step is worth at a given value.</summary>
    /// <remarks>
    ///     ⚠ <b>The larger of the absolute and the proportional, never their sum.</b> Adding them
    ///     would make <see cref="Step" /> a permanent tax on the rate — a hundred thousand lux would
    ///     move by <c>1000 + 1</c>, and the one is noise — whereas taking the maximum makes the two
    ///     a hand-over: <see cref="Step" /> owns everything below a hundred times itself and the
    ///     fraction owns everything above.
    /// </remarks>
    double StepAt(double value) => Math.Max(Step, Math.Abs(value) * RelativeStep);

    /// <summary>Fine and coarse, read off whatever was held on the keyboard.</summary>
    /// <remarks>
    ///     Shift multiplies and Alt divides, which is the convention in every content tool. Neither
    ///     is a mode this has to remember — they are read off the event that arrived, so a scrub and
    ///     an arrow key cannot drift apart about what Shift means.
    /// </remarks>
    static double Scale(ModifierKeys modifiers) =>
        modifiers.HasFlag(ModifierKeys.Shift) ? 10d
        : modifiers.HasFlag(ModifierKeys.Alt) ? 0.1d
        : 1d;

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

    /// <summary>Rounds what a drag has added to something the field can actually hold.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A field showing no decimals is a count, and four point one three cascades is a
    ///         worse bug than the one the proportional step is here to fix.</b> The floor in
    ///         <see cref="StepAt" /> already keeps a small count moving by whole units — a percent of
    ///         four is less than one, so <see cref="Step" /> wins — but nothing stops a fractional
    ///         pixel delta on a scaled display, or a rate of a thousand landing on a half. Rounding
    ///         is what makes the guarantee unconditional rather than a consequence of the numbers
    ///         happening to be tidy.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The offset rather than the resulting number</b>, so an origin the field was given
    ///         is never snapped by the act of touching it, and so a drag back to nought pixels lands
    ///         on exactly the value the gesture started from.
    ///     </para>
    ///     <para>
    ///         Sub-unit movement is kept in <c>offset</c> rather than thrown away, so a slow drag on
    ///         a count still arrives at the next whole number instead of being rounded to nothing
    ///         over and over.
    ///     </para>
    /// </remarks>
    double Quantize(double value) => Decimals == 0 ? Math.Round(value, MidpointRounding.AwayFromZero) : value;

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

        Nudge(steps * Scale(args.Modifiers));
        args.Handled = true;
    }

    /// <summary>Turns a press, a drag and a release into a change of value or into a focus.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rate is frozen at the press, like the origin, and that is what makes the
    ///         gesture reversible.</b> Reading the magnitude again on every move would compound —
    ///         each move worth a percent of a number a percent bigger than the last — so the value
    ///         would run away exponentially, and dragging back the same distance would not return
    ///         to where it started. It would also make the result depend on how many move events the
    ///         platform happened to deliver, which is a property no gesture should have. Lifting and
    ///         pressing again is what re-derives the rate, and it is the same motion a person already
    ///         makes when they want to keep going.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two accumulators, because they answer different questions.</b> <c>scrubbed</c> is
    ///         pixels and only the click threshold reads it — a slow drag on a field whose rate is a
    ///         thousandth would otherwise be indistinguishable from a click. <c>offset</c> is the
    ///         value the drag has added so far, accumulated rather than recomputed, so that changing
    ///         Shift or Alt part way through changes the rate from there on instead of jumping
    ///         everything that came before to the new one.
    ///     </para>
    /// </remarks>
    void Scrub(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary && !IsFocused && !ReadOnly:
                scrubbing = true;
                scrubbed = 0f;
                offset = 0d;
                origin = Number;
                rate = StepAt(origin);

                Document.CapturePointer(this);
                args.Handled = true;
                break;

            case PointerAction.Moved when scrubbing:
                var delta = args.X - LastX;

                scrubbed += delta;
                offset += delta * rate * Scale(args.Modifiers);
                Number = origin + Quantize(offset);

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
