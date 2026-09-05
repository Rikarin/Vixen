// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui.Styling;

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

/// <summary>A field whose characters are not shown.</summary>
/// <remarks>
///     <para>
///         <b>Any login screen needs one and this control set had nothing to offer.</b> An
///         application asked for a password with a <see cref="TextBox" />, in front of whoever was
///         standing behind the user.
///     </para>
///     <para>
///         ⚠ <b>What makes it secure here is smaller than it would be on the web, and the reason is
///         an absence: there is no clipboard in <c>Vixen.Ui</c> at all.</b> Nothing copies, nothing
///         cuts and nothing pastes, so the selection a field allows cannot carry anything out of it
///         — which is why <see cref="TextField.SelectedText" /> is left alone here rather than
///         blanked. When a clipboard arrives, this is the type it has to ask before it reads.
///     </para>
///     <para>
///         <b>Masked at the last moment and nowhere else.</b> The value is the real string — a form
///         has to be able to submit it — and the bullets exist only in the text part and in what the
///         accessibility tree is told, which is what a platform's own secure field reports too. The
///         pre-edit is masked with it: an input method's intermediate reading of a password is the
///         password.
///     </para>
///     <para>
///         ⚠ <b>There is no "reveal" button and its absence is a decision.</b> Showing the value is
///         one assignment away for an application that wants it — swap the field, or read
///         <c>Value</c> into a <see cref="TextBox" /> — and a reveal built in here would be a control
///         that can be made to display a secret by anything that can reach a property on it.
///     </para>
/// </remarks>
public sealed partial class SecureTextBox : TextField {
    /// <summary>What each character is drawn as.</summary>
    /// <remarks>
    ///     U+2022 BULLET, which is what macOS and every browser but one use. A character rather than
    ///     a drawing, so it goes through the same shaping, the same font fallback and the same
    ///     measurement as any other glyph — a field that painted circles itself would put the caret
    ///     in the wrong place the first time somebody changed the font size.
    /// </remarks>
    public const char Bullet = '•';

    /// <inheritdoc />
    protected override string TagName => "secure-textbox";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The mask rather than the value, and <i>not</i> <c>null</c>.</b> Reporting nothing
    ///     would tell a screen-reader user that an empty field is the same as a full one, which is
    ///     how somebody typing blind loses track of whether their keystrokes are arriving at all. A
    ///     platform's secure field reports the bullets for exactly that reason.
    /// </remarks>
    protected override string? NativeAccessibleValue => Shown(Value);

    /// <inheritdoc />
    protected override string? Shown(string? value) => value is null ? null : new string(Bullet, value.Length);
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

/// <summary>A field that holds a number, with arrows and a drag to scrub it.</summary>
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
///     <para>
///         ⚠ <b>The spinners this summary used to claim are <see cref="Stepper" />'s, and until it
///         existed they were nobody's.</b> The line above said "arrows, spinners and a drag" and the
///         control had two of the three; the theme's read-only rule named "<c>numeric-input</c>'s
///         spinners" as well. What was true is that <see cref="Nudge" /> — the mechanism a pair of
///         buttons needs — was finished and had nothing pressing it.
///     </para>
///     <para>
///         ⚠ <b>Not sealed, and <see cref="Stepper" /> is the only reason.</b> A stepper is this
///         field with two buttons in it: the number, the range, the step, the arrow keys and the
///         scrub are all here already, and a separate control beside a field would have had to
///         mirror every one of them and keep the mirror in step.
///     </para>
/// </remarks>
public partial class NumericInput : TextField {
    bool scrubbing;
    float scrubbed;
    double offset;
    double rate;
    double origin;
    CultureInfo? culture;
    string? format;

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
    /// <remarks>
    ///     The simple half of <see cref="Format" />: with no format written this is <c>F{Decimals}</c>,
    ///     and with one it decides nothing. It is still what <see cref="Quantize" /> reads, because a
    ///     field showing no decimals is a count whatever it is formatted as.
    /// </remarks>
    [UiProperty(Changed = nameof(OnDecimalsChanged))]
    public partial int Decimals { get; set; }

    /// <summary>Which locale the number is written and read in. <c>null</c> is invariant.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One property for both directions, because printing and parsing are one
    ///         decision.</b> A field that wrote <c>1 234,50</c> and then read it back under the
    ///         invariant rules would eat the user's own text on the next commit — the parse fails,
    ///         <see cref="Reread" /> is silent by design, and the number silently stops following the
    ///         field. Two properties would have made that arrangement expressible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Invariant is the default rather than <see cref="CultureInfo.CurrentCulture" />.</b>
    ///         A control that quietly followed the thread's culture would change what every existing
    ///         field in every application prints, on a machine setting nobody in the application
    ///         chose, and a scene value written to disk is not a locale-dependent quantity. An
    ///         application that wants the user's locale says so, once.
    ///     </para>
    /// </remarks>
    public CultureInfo? Culture {
        get => culture;
        set {
            culture = value;
            Reformat();
        }
    }

    /// <summary>
    ///     A .NET numeric format string — <c>"N0"</c>, <c>"C2"</c>, <c>"P1"</c>, <c>"#,##0.###"</c>.
    ///     <c>null</c> is <c>F{Decimals}</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the locale, currency, percent and grouping the field had none of. Grouping and
    ///         currency read back because <see cref="Reread" /> widens its
    ///         <see cref="NumberStyles" /> to match what was printed, and percent reads back because
    ///         the symbol is stripped and the value divided — .NET can write a percentage and has
    ///         never been able to parse one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A custom format containing a bare <c>%</c> scales by a hundred exactly as
    ///         <c>"P"</c> does, and this control cannot tell.</b> The percent handling keys on the
    ///         standard specifier, so <c>"#0.0 %"</c> prints a hundredfold value that reads back as
    ///         itself: the field then multiplies by a hundred again on every commit. Write
    ///         <c>"P1"</c>, or put the sign in a label beside a plain format.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Assigning one rewrites the text immediately</b>, on <c>TextField.Validator</c>'s
    ///         terms — a format attached to a field that already holds a number should not wait for
    ///         the next keystroke to be visible.
    ///     </para>
    /// </remarks>
    public string? Format {
        get => format;
        set {
            format = value;
            Reformat();
        }
    }

    /// <summary>Raised when the number changes.</summary>
    public event Action<NumericInput, double>? NumberChanged;

    /// <summary>Whether <see cref="Number" /> is inside the bounds the field declares.</summary>
    /// <remarks>
    ///     ⚠ <b>A question worth being able to ask, which it was not while the field clamped.</b> The
    ///     answer was <c>true</c> unconditionally — see <see cref="CoerceNumber" /> — so nothing
    ///     could have been written against it. Both comparisons are inclusive, and both are false for
    ///     a number that is <c>NaN</c>; the coerce keeps that out of <see cref="Number" /> anyway.
    /// </remarks>
    public bool IsInRange => Number >= Minimum && Number <= Maximum;

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
    ///     <para>
    ///     ⚠ <b>And this is one of the three mutations that still clamps</b> — see
    ///     <see cref="CoerceNumber" /> for the split. A spinner's whole affordance is that it stops
    ///     at the ends, so an arrow held down at the ceiling has to sit there rather than climb into
    ///     a number the field will then call invalid. It also means an arrow is the way <i>back</i>
    ///     from a value typed outside the bounds: one press lands on the nearest end.
    ///     </para>
    /// </remarks>
    public void Nudge(double steps) => Number = Math.Clamp(Number + (steps * StepAt(Number)), Minimum, Maximum);

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
    private protected static double Scale(ModifierKeys modifiers) =>
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
            if (!Typeable(character)) {
                return Value;
            }
        }

        return value;
    }

    /// <summary>Whether a character may appear in this field's text at all.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The alphabet follows <see cref="Format" /> and <see cref="Culture" />, and until
    ///         it did, the formatting seam above did nothing at all.</b> This filter is what
    ///         <c>Value</c> is coerced through, so a field that formatted <c>€12,50</c> and then
    ///         refused the <c>€</c> discarded its own output and showed the previous text — the
    ///         formatter looked implemented and the field printed <c>0</c>. A format seam that does
    ///         not widen the input filter is not a format seam.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ASCII set is unconditional, so an unformatted field's alphabet has not
    ///         changed.</b> The invariant separators and signs are all inside it, and the currency
    ///         and percent symbols are reached only when a format asks for them — so a plain field
    ///         still refuses a <c>¤</c> exactly as it did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whitespace is allowed for those two formats and for no other reason.</b> A
    ///         currency and a percent pattern put a space between the number and the symbol in most
    ///         locales, and that space is in the <i>pattern</i> rather than in any symbol string, so
    ///         nothing in <see cref="NumberFormatInfo" /> can be consulted for it. It is the one
    ///         character here that is admitted by argument rather than by lookup.
    ///     </para>
    /// </remarks>
    bool Typeable(char character) {
        if (char.IsAsciiDigit(character) || character is '-' or '+' or '.' or ',' or 'e' or 'E') {
            return true;
        }

        var info = Locale.NumberFormat;

        if (Has(info.NumberDecimalSeparator, character)
            || Has(info.NumberGroupSeparator, character)
            || Has(info.NegativeSign, character)
            || Has(info.PositiveSign, character)) {
            return true;
        }

        if (format is ['C' or 'c', ..]) {
            return char.IsWhiteSpace(character)
                || Has(info.CurrencySymbol, character)
                || Has(info.CurrencyDecimalSeparator, character)
                || Has(info.CurrencyGroupSeparator, character);
        }

        if (IsPercent) {
            return char.IsWhiteSpace(character)
                || Has(info.PercentSymbol, character)
                || Has(info.PercentDecimalSeparator, character)
                || Has(info.PercentGroupSeparator, character);
        }

        return false;
    }

    static bool Has(string symbol, char character) => symbol.Contains(character, StringComparison.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A range violation is a constraint violation, which is CSS's own reading and not a
    ///     convenience.</b> Selectors 4 § 10.7 puts a number outside its bounds in
    ///     <c>:out-of-range</c> <i>and</i> in <c>:invalid</c>, so a stylesheet that only knows how to
    ///     colour an invalid field still colours this one. Read off <see cref="Number" /> rather than
    ///     off the text it is handed: the text is half-typed for as long as somebody is typing, and
    ///     the number is only assigned from text that parses.
    /// </remarks>
    protected override string? Validate(string? value) =>
        base.Validate(value) ?? (IsInRange ? null : ControlStrings.FieldOutOfRange.Text);

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
        Reformat();
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

    /// <summary>Keeps out what the field cannot hold, which is not the same as what it may not be.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to clamp to <see cref="Minimum" /> and <see cref="Maximum" />, and it
    ///         is the same mistake <see cref="TextField.Coerce" />'s own remarks warn about one seam
    ///         over: a field that silently rewrites what was typed is a field that will not take what
    ///         you type.</b> Somebody entering <c>500</c> into a field bounded at a hundred saw
    ///         <c>100</c> appear under their cursor with nothing said, and no state anywhere recorded
    ///         that anything had been refused — <c>:out-of-range</c> could not be true of this
    ///         control, for any length of time, by any route.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the bounds moved from the coerce to the verdict, and the split is by
    ///         gesture rather than by value.</b> A typed or assigned number is <i>held</i> and
    ///         reported — <see cref="Validate" /> makes it <c>:invalid</c> as well as
    ///         <c>:out-of-range</c>, which is what CSS says, a range violation being a constraint
    ///         violation. <see cref="Nudge" /> and the scrub still clamp, because a spinner and a
    ///         drag are affordances that stop at the ends and the person doing them is not saying a
    ///         number, they are pushing one.
    ///     </para>
    ///     <para>
    ///         The <c>NaN</c> guard stays: that is genuinely something the field cannot hold rather
    ///         than something it may not be.
    ///     </para>
    /// </remarks>
    double CoerceNumber(double value) => double.IsNaN(value) ? Number : value;

    void OnNumberChanged(double previous, double current) {
        Rerange();
        Reformat();

        // ⚠ Explicitly, because `Reformat` only reaches `Revalidate` when the *text* moved. A field
        // showing whole numbers that is pushed from ten to ten and a half formats to "10" both
        // times, so the number left the range and nothing would have said so.
        Revalidate();

        NumberChanged?.Invoke(this, current);
    }

    /// <summary>Re-decides the verdict when the bounds move rather than the number.</summary>
    /// <remarks>
    ///     ⚠ <b>It used to pull the number back inside, and that is the behaviour this issue
    ///     changed.</b> A ceiling lowered under a value that is already there does not un-say the
    ///     value; it makes it unacceptable, which is a thing the field can now express. Nothing is
    ///     lost that the arrows do not give back: one press lands on the nearest end.
    /// </remarks>
    void OnRangeChanged(double previous, double current) {
        Rerange();
        Revalidate();
    }

    /// <summary>Writes the one range bit.</summary>
    /// <remarks>
    ///     ⚠ <b>One bit and no birth call, which is where this differs from the verdict beside
    ///     it.</b> <c>:valid</c> needed a positive write at <c>OnCreated</c> because its absence is
    ///     indistinguishable from an element that does not validate; <c>:in-range</c> is compiled as
    ///     this bit's negation, so a field that has never left its bounds is in range by carrying
    ///     nothing — which is also what a field with no bounds at all should be, and
    ///     <see cref="Minimum" /> and <see cref="Maximum" /> default to the infinities.
    /// </remarks>
    void Rerange() =>
        State = IsInRange ? State & ~ElementState.OutOfRange : State | ElementState.OutOfRange;

    void OnDecimalsChanged(int previous, int current) => Reformat();

    /// <summary>The locale the field prints and parses in.</summary>
    CultureInfo Locale => culture ?? CultureInfo.InvariantCulture;

    /// <summary>Whether the format is the standard percent specifier, which scales by a hundred.</summary>
    /// <remarks>
    ///     ⚠ <b>The standard specifier only, and a custom format with a <c>%</c> in it is invisible
    ///     here.</b> Both scale, so both would need the same treatment, and telling them apart means
    ///     reading a custom format for an unescaped, unquoted <c>%</c> — a small parser for one
    ///     character, which is a worse trade than saying plainly in <see cref="Format" /> that the
    ///     custom spelling does not round-trip.
    /// </remarks>
    bool IsPercent => format is ['P' or 'p', ..];

    void Reformat() =>
        Value = Number.ToString(format ?? "F" + Decimals.ToString(CultureInfo.InvariantCulture), Locale);

    /// <summary>Reads the number back out of the text after a keystroke.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Silent on text that does not parse. A field mid-edit holds <c>-</c> and <c>1.</c> for
    ///         as long as somebody is typing them, and a control that reset the number to zero on
    ///         every one of those would fight every negative number ever entered.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The styles are widened to match what was printed, and that is the whole of what
    ///         makes a format usable.</b> <c>NumberStyles.Float</c> alone rejects the group separators
    ///         and the currency symbol this field may have written itself, so the silence above would
    ///         turn into a field that discards every commit — the failure mode of a format seam that
    ///         only formats.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Percent is handled here rather than by a style, because .NET has none.</b>
    ///         <c>ToString("P")</c> multiplies by a hundred and appends the symbol; no
    ///         <see cref="NumberStyles" /> undoes either, so the symbol is stripped and the number
    ///         divided. Without it a percentage field multiplies its own value by a hundred on every
    ///         commit — visibly, and only after the first blur.
    ///     </para>
    /// </remarks>
    void Reread() {
        var text = Value;
        var styles = NumberStyles.Float | NumberStyles.AllowThousands;

        if (format is ['C' or 'c', ..]) {
            styles = NumberStyles.Currency;
        } else if (IsPercent) {
            text = text?.Replace(Locale.NumberFormat.PercentSymbol, string.Empty, StringComparison.Ordinal);
        }

        if (double.TryParse(text, styles, Locale, out var parsed)) {
            Number = IsPercent ? parsed / 100.0 : parsed;
        }
    }

    /// <summary>Turns Up, Down, PageUp and PageDown into a step.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="TextField.ReadOnly" /> was the one route into the value that did not
    ///         ask.</b> Typing is refused, the scrub's press case is guarded, and <c>Stepper</c>'s
    ///         arrows disable themselves — so a field offered as "look, do not touch" was one
    ///         keystroke from being edited, and <c>ReadOnly</c> is precisely the state in which a
    ///         person's finger is on the arrow keys: it still takes the focus, and its text can still
    ///         be selected and copied. That is what separates it from <c>Disabled</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the key is left unhandled rather than swallowed</b>, which is the half worth
    ///         saying out loud. A read-only field has not consumed Up; marking it handled would stop
    ///         it reaching whatever the field is inside — a list that scrolls, a dialog that moves a
    ///         selection — and turn "cannot be edited" into "eats the keyboard".
    ///     </para>
    /// </remarks>
    void Stepped(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || ReadOnly) {
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
            // ⚠ `Presses(args)` and not just the three conditions, because this runs on the capture
            // leg and marks the event handled: a press on a control *inside* the field would never
            // reach it. See the method, and `Stepper`, which is the field that has one.
            case PointerAction.Pressed
                when args.Button == PointerButton.Primary && !IsFocused && !ReadOnly && Presses(args):
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

                // ⚠ Clamped here rather than in the coerce, which is where it used to be. A drag is
                // an affordance that stops at the ends — the same argument `Nudge` makes — so the
                // gesture keeps the behaviour the coerce used to give every mutation, and typing
                // keeps what it was given.
                Number = Math.Clamp(origin + Quantize(offset), Minimum, Maximum);

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

    /// <summary>Whether a press belongs to the field itself rather than to a control inside it.</summary>
    /// <remarks>
    ///     ⚠ <b>A scrub starts on the capture leg and marks the press handled, so without this the
    ///     field takes a gesture that was aimed at a button inside it.</b> The button is still
    ///     <i>clicked</i> — activation comes off the tap the gesture recogniser makes, which does not
    ///     care that the press was marked handled — so the symptom is not a dead button: it is a
    ///     press that captures the pointer for the field, selects the field's text on release, and
    ///     scrubs the value if the hand moves at all. The parts a plain field is made of —
    ///     <c>field-text</c>, <c>field-placeholder</c> — are bare elements and answer <c>true</c>
    ///     here, so nothing about an ordinary numeric field changes; a <see cref="Stepper" />'s
    ///     arrows are <see cref="Control" />s and are the case this exists for. Asked of any control
    ///     between the source and this field rather than of a list of known parts, because the next
    ///     control put inside a field would otherwise arrive at the same dead press.
    ///     <para>
    ///         ⚠ <b>The walk is the whole of it, and a source test alone is a guard that never
    ///         fires.</b> What a pointer hits is the deepest element under it, which for a button is
    ///         the <see cref="Icon" /> inside it — a bare element. Written as
    ///         <c>args.Source is not Control</c> this read <c>true</c> for every press on an arrow,
    ///         and the arrows behaved because the tap that activates a button comes out of the
    ///         gesture recogniser rather than out of the press this handler marked handled. The
    ///         symptom that remained was the focus: the field selected itself as though it had been
    ///         clicked to type in.
    ///     </para>
    /// </remarks>
    bool Presses(PointerEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, this)) {
                return true;
            }

            if (element is Control) {
                return false;
            }
        }

        return true;
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

/// <summary>A number with the two arrows that step it.</summary>
/// <remarks>
///     <para>
///         <b>The arithmetic was finished and had nothing to press it.</b>
///         <see cref="NumericInput.Nudge" /> has driven the arrow keys and the scrub since the field
///         was written, and every toolkit's answer to "adjust this by one" is two small arrows next
///         to the box — so an application that wanted a stepper had to draw one and wire it up. ⚠ The
///         field's own summary and the theme's read-only rule both described spinners that did not
///         exist.
///     </para>
///     <para>
///         ⚠ <b>A field with arrows in it rather than arrows beside a field, which is the opposite
///         of AppKit's split.</b> <c>NSStepper</c> is the pair on its own, and the binding between it
///         and whatever text field it sits next to is the application's to keep — a seam that exists
///         because the two are separate views. Here the number, the range, the step, the keys and the
///         scrub are one control already, so the arrows join it and <c>bind:Number</c> goes on
///         working; a second control beside the field would have had to mirror five properties and
///         stay in step with all of them.
///     </para>
///     <para>
///         <b>The arrows are not tab stops.</b> They do what Up and Down already do in the field they
///         are in, so putting them in the tab order would mean tabbing past a form's number landing
///         on two buttons before reaching the next question. Same argument, and the same
///         <c>TabIndex</c>, as <see cref="SearchBox" />'s clear button.
///     </para>
///     <para>
///         ⚠ <b>They disable at the ends of the range, which is a picture of something a click cannot
///         show.</b> <c>Number</c> is clamped to <c>[Minimum, Maximum]</c>, so an arrow at the end has
///         always done nothing; what is being fixed is that it did nothing <i>silently</i>. A greyed
///         arrow is what tells a person — and, through <c>AccessibleStates.Disabled</c>, an assistive
///         technology — which way there is left to go.
///     </para>
/// </remarks>
public sealed partial class Stepper : NumericInput {
    /// <inheritdoc />
    protected override string TagName => "stepper";

    /// <summary>The arrow that takes one step off.</summary>
    public IconButton DecrementButton { get; private set; } = null!;

    /// <summary>The arrow that adds one.</summary>
    public IconButton IncrementButton { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // Down first, so the pair reads in the order the numbers do. The classes are what the theme
        // pushes the pair to the right-hand end of the box by — a position rather than a `:nth`,
        // because the arrows are the only two children a stylesheet has any business moving and an
        // ordinal would silently mean the placeholder the day one is added before them.
        DecrementButton = Arrow(ControlIcons.ChevronDown, ControlStrings.StepperDecrease.Text, "step-down");
        IncrementButton = Arrow(ControlIcons.ChevronUp, ControlStrings.StepperIncrease.Text, "step-up");

        AddHandler<ClickEvent>(static (element, args) => ((Stepper) element).Arrowed(args));

        Ends();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Every property rather than the four that matter, and the cost of that is two
    ///     comparisons.</b> What decides an arrow's state is <c>Number</c>, <c>Minimum</c>,
    ///     <c>Maximum</c> and <c>ReadOnly</c> today; a filter naming those four is a list somebody
    ///     has to remember to edit when a fifth arrives, and a stale one fails as an arrow that
    ///     stays grey after the range it was measured against has moved. This is the seam the base
    ///     class documents for exactly this case: reacting to a property the type did not declare.
    /// </remarks>
    protected override void OnPropertyChanged(UiPropertyKey key) {
        base.OnPropertyChanged(key);

        // A property assigned before the parts exist has nothing to say to them yet, and `OnCreated`
        // ends by asking anyway.
        if (IncrementButton is not null) {
            Ends();
        }
    }

    IconButton Arrow(PathBuilder geometry, string label, string className) {
        var arrow = Part<IconButton>(null, className);

        arrow.LeadingIcon.Geometry = geometry;
        arrow.Variant = ControlVariant.Subtle;

        // The label is the announced name and nothing else: `icon-button label` is `display: none`,
        // which is what lets an icon-only button say a word to a screen reader without showing one.
        arrow.Label = label;

        // See the type's remarks.
        arrow.TabIndex = -1;

        return arrow;
    }

    void Ends() {
        DecrementButton.Disabled = ReadOnly || Number <= Minimum;
        IncrementButton.Disabled = ReadOnly || Number >= Maximum;
    }

    void Arrowed(ClickEvent args) {
        var steps = ReferenceEquals(args.Source, IncrementButton) ? 1d
            : ReferenceEquals(args.Source, DecrementButton) ? -1d
            : 0d;

        if (steps == 0d) {
            return;
        }

        args.Handled = true;

        // ⚠ Belt as well as braces: `Ends` has already disabled both arrows while the field is
        // read-only, and a disabled control raises no click at all. It is written out because this
        // is the third of three gestures that reach `Nudge` and the other two both say it — the
        // arrow keys said it last (#826), which is what this comment used to record as a hole.
        if (ReadOnly) {
            return;
        }

        // The same Shift-multiplies, Alt-divides scaling the arrow keys read off their own event, so
        // that a click and a keystroke cannot disagree about what a step is worth.
        Nudge(steps * Scale(args.Modifiers));
    }
}
