// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Text;

namespace Vixen.Ui.Controls;

/// <summary>Anything the user types into.</summary>
/// <remarks>
///     <para>
///         <b>One editing core under every field in the set.</b> A text box, a search box, a numeric
///         input and a combo box's editable half are one caret, one selection and one keyboard map
///         wearing four skins; writing the map four times is how a control library ends up with a
///         field where Ctrl-Left works and one where it does not.
///     </para>
///     <para>
///         ⚠ <b>Single line unless <see cref="AcceptsNewlines" /> says otherwise</b>, which only
///         <see cref="TextArea" /> does. A one-line field's value scrolls sideways rather than
///         wrapping; a text area wraps, takes a newline from Enter, and moves its caret between
///         lines. Both are the same caret, the same selection and the same keyboard map — the
///         difference is three predicates rather than a second control.
///     </para>
///     <para>
///         ⚠ <b>A text area does not scroll vertically yet.</b> Its lines are drawn where they fall
///         and a value taller than the box is clipped by it, which is honest and is not enough: the
///         box is sized by the theme (<c>textarea { min-height }</c>) and a caller that expects to
///         hold a long document should say how tall it wants to be. A scroll region round the text
///         is owed.
///     </para>
///     <para>
///         <b>The caret is an index into the value and the selection is a second one.</b> Both are
///         UTF-16 indices on grapheme boundaries, which is what <c>ShapedText</c> works in — so
///         moving the caret over an emoji or a Devanagari syllable steps over the whole of it, and
///         the arithmetic that decides is the conformance-tested code in the text assembly rather
///         than a loop here that would get the interesting scripts wrong.
///     </para>
/// </remarks>
public abstract partial class TextField : Control {
    UiElement text = null!;
    UiElement placeholder = null!;
    int selectionColor;
    int caretColor;
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "textbox";

    /// <summary>What is in it.</summary>
    [UiProperty(Coerce = nameof(CoerceValue), Changed = nameof(OnValueChanged))]
    public partial string? Value { get; set; }

    /// <summary>What it says when it is empty.</summary>
    [UiProperty(Changed = nameof(OnPlaceholderChanged))]
    public partial string? Placeholder { get; set; }

    /// <summary>Whether it can be typed into.</summary>
    /// <remarks>
    ///     <para>
    ///         Distinct from <see cref="Control.Disabled" />: a read-only field still takes the
    ///         focus, is still a tab stop, and its text can still be selected and copied. A disabled
    ///         one is out of reach entirely. Conflating them is how a form ends up with values
    ///         nobody can read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It writes a <c>read-only</c> class, and until it did the state was invisible.</b>
    ///         <see cref="Control.Disabled" /> has <c>:disabled</c> and every theme greys it; this
    ///         had nothing at all — so a field the inspector had made read-only because the member
    ///         has no setter looked exactly like one you could type in, and the only way to find out
    ///         was to type in it and watch nothing happen. A class rather than a state because
    ///         <c>ElementState</c> is the set of <i>transient</i> conditions a selector asks about
    ///         and this is a mode the field was put into.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnReadOnlyChanged))]
    public partial bool ReadOnly { get; set; }

    /// <summary>The longest value it will take, or zero for no limit.</summary>
    [UiProperty]
    public partial int MaxLength { get; set; }

    /// <summary>Where the caret is, as a UTF-16 index into <see cref="Value" />.</summary>
    public int CaretIndex { get; private set; }

    /// <summary>Where the selection started, as a UTF-16 index. Equal to the caret when nothing is selected.</summary>
    public int SelectionAnchor { get; private set; }

    /// <summary>The lower end of the selection.</summary>
    public int SelectionStart => Math.Min(CaretIndex, SelectionAnchor);

    /// <summary>The upper end.</summary>
    public int SelectionEnd => Math.Max(CaretIndex, SelectionAnchor);

    /// <summary>Whether anything is selected.</summary>
    public bool HasSelection => CaretIndex != SelectionAnchor;

    /// <summary>The selected text.</summary>
    public string SelectedText =>
        Value is { } value ? value[SelectionStart..SelectionEnd] : string.Empty;

    /// <summary>Raised after the value changes, whoever changed it.</summary>
    public event Action<TextField, string?>? ValueChanged;

    /// <summary>Raised when Enter is pressed in the field.</summary>
    /// <remarks>
    ///     Separate from <see cref="ValueChanged" /> because they answer different questions. A
    ///     search box that queried on every keystroke would query five times for "hello"; one that
    ///     only queried on Enter would never do type-ahead. Both are legitimate and a control that
    ///     offered one event would force the wrong one.
    /// </remarks>
    public event Action<TextField>? Submitted;

    /// <summary>The element the value is drawn on.</summary>
    protected UiElement TextPart => text;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // The placeholder first, so that it sits under the value rather than over it — both are
        // absolutely positioned in the same place by the theme, and one of the two is always empty.
        placeholder = Part("field-placeholder");
        text = Part("field-text");

        selectionColor = Document.PropertyId("--selection-color");
        caretColor = Document.PropertyId("--caret-color");

        AddHandler<KeyEvent>(static (element, args) => ((TextField) element).Keyed(args));
        AddHandler<TextInputEvent>(static (element, args) => ((TextField) element).Typed(args));
        AddHandler<PointerEvent>(static (element, args) => ((TextField) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((TextField) element).Tapped(args));
        AddHandler<FocusEvent>(static (element, args) => ((TextField) element).Refocused(args));
    }

    /// <summary>Moves the caret, and either drops the selection or extends it.</summary>
    /// <param name="index">Where to put it.</param>
    /// <param name="extend">Whether Shift is held.</param>
    public void MoveCaret(int index, bool extend = false) {
        var length = Value?.Length ?? 0;

        CaretIndex = Math.Clamp(index, 0, length);

        if (!extend) {
            SelectionAnchor = CaretIndex;
        }

        Reveal();
    }

    /// <summary>Selects the whole value.</summary>
    /// <remarks>
    ///     ⚠ The anchor goes to the start and the caret to the end, rather than the other way round.
    ///     It matters for what happens next: pressing Right after Ctrl-A puts the caret at the end
    ///     of the text, which is what every editor does, and anchoring the other way would put it at
    ///     the start.
    /// </remarks>
    public void SelectAll() {
        SelectionAnchor = 0;
        CaretIndex = Value?.Length ?? 0;

        Reveal();
    }

    /// <summary>Selects the word an index falls inside.</summary>
    /// <param name="index">A UTF-16 index into <see cref="Value" />.</param>
    /// <remarks>
    ///     ⚠ <b>The anchor at the start and the caret at the end</b>, for the reason
    ///     <see cref="SelectAll" /> does it: a Shift-Right after a double click has to grow the
    ///     selection rightwards rather than eat the word from its left.
    /// </remarks>
    public void SelectWord(int index) {
        if (Value is not { Length: > 0 } value) {
            return;
        }

        // UAX#29 rather than a scan for spaces, which is the same reason the caret steps by
        // graphemes: "don't" is one word and "編集する" is three, and neither is decided by
        // whitespace.
        var (start, end) = WordBreaker.WordAt(value, Math.Clamp(index, 0, value.Length));

        SelectionAnchor = start;
        CaretIndex = end;

        Reveal();
    }

    /// <summary>Replaces the selection — or, with none, inserts at the caret.</summary>
    /// <param name="insertion">What to put there.</param>
    /// <remarks>
    ///     The one mutation. Typing, pasting, Backspace and Delete are all this with different
    ///     arguments, which is what keeps <see cref="MaxLength" />, the change notification and the
    ///     caret arithmetic in one place instead of four.
    /// </remarks>
    public void Replace(string insertion) {
        ArgumentNullException.ThrowIfNull(insertion);

        if (ReadOnly || Disabled) {
            return;
        }

        var value = Value ?? string.Empty;
        var start = SelectionStart;
        var end = SelectionEnd;

        // ⚠ The limit is checked against what the result would be, not against what is being
        // inserted. A field with three characters left in it that is handed ten takes the three —
        // truncating the paste rather than refusing it, which is what every native field does and
        // what stops a long paste into a short field from silently doing nothing.
        var replacement = insertion;
        if (MaxLength > 0) {
            var room = MaxLength - (value.Length - (end - start));
            if (room <= 0) {
                return;
            }

            if (replacement.Length > room) {
                replacement = replacement[..room];
            }
        }

        var updated = string.Concat(value.AsSpan(0, start), replacement, value.AsSpan(end));

        // The caret before the value, because assigning the value raises the change and a handler
        // that reads the caret should see where it ended up rather than where it was.
        CaretIndex = start + replacement.Length;
        SelectionAnchor = CaretIndex;

        Value = updated;
    }

    /// <summary>What the field does with a value on its way in.</summary>
    /// <param name="value">What was assigned.</param>
    /// <returns>What to store.</returns>
    /// <remarks>
    ///     Overridden by <see cref="NumericInput" />, which is the reason it exists: a numeric field
    ///     that let a caller assign "banana" would then have to decide what its number was.
    /// </remarks>
    protected virtual string? Coerce(string? value) => value;

    /// <summary>Called when Enter is pressed, before <see cref="Submitted" /> is raised.</summary>
    protected virtual void OnSubmit() {
    }

    /// <summary>Whether Enter puts a line break in the value rather than submitting it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>False everywhere except <see cref="TextArea" />, and it is what makes a text area
    ///         a text area.</b> Without it Enter submitted and the value stayed one line, so a field
    ///         offered for a YAML document — the editor's theme tokens are one — could not be given a
    ///         second line at all. That reads as a box that will not take what you type, which is a
    ///         worse bug than a missing feature because there is nothing on screen to explain it.
    ///     </para>
    ///     <para>
    ///         The wrapping and the multi-line caret were already possible: <c>TextLayout</c> breaks
    ///         on a mandatory break and answers <c>CaretAt</c> per line. What was missing was a way to
    ///         get a newline into the string.
    ///     </para>
    /// </remarks>
    protected virtual bool AcceptsNewlines => false;

    /// <summary>What a repeated tap selects.</summary>
    /// <param name="index">Where the tap landed, as a UTF-16 index into <see cref="Value" />.</param>
    /// <param name="count">How many taps in a row, two or more.</param>
    /// <remarks>
    ///     Two selects the word and three selects the lot, which is what every editor does — and it
    ///     is a method rather than a switch inside the handler because what counts as a word is a
    ///     field's own business: see <see cref="NumericInput" />.
    /// </remarks>
    protected virtual void SelectAt(int index, int count) {
        if (count >= 3) {
            SelectAll();
        } else {
            SelectWord(index);
        }
    }

    /// <summary>Whether the caret is drawn: a field that cannot be typed into has none.</summary>
    /// <remarks>
    ///     ⚠ <b>A caret is a promise that the next keystroke lands here</b>, and on a read-only or
    ///     disabled field that promise is false — so an inspector row over a member with no setter
    ///     blinked exactly like one you could edit, and the only way to find out was to type into it
    ///     and watch nothing happen. The <i>selection</i> is still drawn, because selecting and
    ///     copying is precisely what a read-only field is for; it is the insertion point that is a
    ///     lie. See <see cref="ReadOnly" />, which makes the same distinction.
    /// </remarks>
    protected bool ShowsCaret => !ReadOnly && !Disabled;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Drawn on the field rather than on the text element</b>, and before the children, so
    ///     the selection band lands under the glyphs it highlights. Drawing it on the text element
    ///     would put it over them, and a selected word would be a coloured rectangle.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (!IsFocused) {
            return;
        }

        var origin = text.AbsoluteLeft;
        var top = text.AbsoluteTop;

        // ⚠ An empty field still draws a caret, and until it did, clicking one looked like nothing
        // happening. There is no block to ask — `UiElement.Block` answers null for an element with
        // no text, deliberately — so this used to return here, which meant the *only* field with no
        // visible sign of the focus was the one you were about to type your first character into.
        // A click gives `Focus` and not `FocusVisible`, so the ring is not drawn either: between the
        // two, an empty focused search box was indistinguishable from an empty unfocused one.
        //
        // The height is the text element's own, which is a real number because the theme gives
        // `field-text` a `min-height` — the same declaration that stops an empty field collapsing.
        if (text.Block() is not { } block) {
            if (ShowsCaret) {
                context.FillRectangle(
                    new Rectangle(origin, top, 1f, MathF.Max(text.Height, 1f)),
                    Document.ColorOf(Style, caretColor) ?? context.Foreground
                );
            }

            return;
        }

        // ⚠ Per line, and a single-line field is the one-line case of it rather than a different
        // path. This used to read `block.Lines[0]` and say so — which was true while nothing wrapped
        // and became a lie the moment `TextArea` grew a newline: the caret stayed pinned to the first
        // line's baseline and a selection spanning two lines was drawn as one band across the first.
        if (HasSelection) {
            var first = block.LineOf(SelectionStart);
            var last = block.LineOf(SelectionEnd);
            var colour = Document.ColorOf(Style, selectionColor) ?? new Color4(0.25f, 0.45f, 0.85f, 0.35f);

            for (var index = first; index <= last; index++) {
                var line = block.Lines[index];
                var start = Math.Max(SelectionStart, line.Start);
                var end = Math.Min(SelectionEnd, line.Start + line.Length);

                var from = origin + line.CaretOffset(start);
                var to = origin + line.CaretOffset(end);

                // ⚠ A minimum width, so a line whose whole content is inside the selection but which
                // ends in the break still reads as selected. Zero-width is what a blank line in the
                // middle of a selected paragraph would otherwise be, and it looks like a gap.
                context.FillRectangle(
                    new Rectangle(
                        MathF.Min(from, to),
                        top + block.TopOf(index),
                        MathF.Max(MathF.Abs(to - from), index < last ? 3f : 0f),
                        line.Height
                    ),
                    colour
                );
            }
        }

        if (!ShowsCaret) {
            return;
        }

        // ⚠ The caret is drawn even when there is a selection. Every editor does — the caret is the
        // end you are extending from, and hiding it during a Shift-Arrow leaves the user unable to
        // tell which way the next keystroke will grow the selection.
        var (caretX, caretY) = block.CaretAt(CaretIndex);

        context.FillRectangle(
            new Rectangle(origin + caretX, top + caretY, 1f, block.Lines[block.LineOf(CaretIndex)].Height),
            Document.ColorOf(Style, caretColor) ?? context.Foreground
        );
    }

    string? CoerceValue(string? value) => Coerce(value);

    void OnValueChanged(string? previous, string? current) {
        text.Text = current;

        var length = current?.Length ?? 0;
        CaretIndex = Math.Clamp(CaretIndex, 0, length);
        SelectionAnchor = Math.Clamp(SelectionAnchor, 0, length);

        Restate();
        Reveal();

        Raise(new ValueChangedEvent<string> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    void OnPlaceholderChanged(string? previous, string? current) {
        placeholder.Text = current;
        Restate();
    }

    void OnReadOnlyChanged(bool previous, bool current) {
        if (current) {
            AddClass("read-only");
        } else {
            RemoveClass("read-only");
        }
    }

    /// <summary>Shows the placeholder only when there is nothing to show instead.</summary>
    /// <remarks>
    ///     A class rather than swapping the text, so that the theme decides what an empty field
    ///     looks like — and so that the placeholder element keeps its text across an edit and back,
    ///     rather than being cleared and reinstated on every keystroke.
    /// </remarks>
    void Restate() {
        if (string.IsNullOrEmpty(Value)) {
            AddClass("empty");
        } else {
            RemoveClass("empty");
        }
    }

    /// <summary>Scrolls the text sideways so that the caret is inside the box.</summary>
    /// <remarks>
    ///     ⚠ <b>Reads the layout from the last pass</b>, so a field whose value was set before it
    ///     was ever laid out reveals against a zero-width box and settles on the next frame. That is
    ///     the same limitation arrow navigation has and for the same reason — there is no geometry
    ///     until something has laid it out — and it is invisible in practice because the value that
    ///     matters is the one the user is typing into a field that is already on screen.
    /// </remarks>
    void Reveal() {
        if (text.Block() is not { } block) {
            text.OffsetX = 0f;
            return;
        }

        // ⚠ Nothing to reveal in a field whose text wraps: the caret is always inside the box
        // horizontally, and shifting the block sideways would take the *other* lines out of it.
        // Scrolling a text area vertically is the scroll region's job and is owed.
        if (AcceptsNewlines) {
            text.OffsetX = 0f;
            return;
        }

        var line = block.Lines[0];

        var viewport = Width;
        if (viewport <= 0f) {
            return;
        }

        var caret = line.CaretOffset(CaretIndex);
        var shift = -text.OffsetX;

        // A margin of one caret width at each edge, so that the caret is never flush against the
        // border it is about to move past.
        if (caret - shift > viewport - 2f) {
            shift = caret - viewport + 2f;
        } else if (caret - shift < 0f) {
            shift = caret;
        }

        // Never past the end: a field whose text has just been shortened must not keep scrolling
        // through the space the deleted characters used to occupy.
        shift = Math.Clamp(shift, 0f, MathF.Max(0f, line.Width - viewport + 2f));
        text.OffsetX = -shift;
    }

    void Refocused(FocusEvent args) {
        if (args.Gained) {
            // Selecting everything on focus is what a field in a form does, and it is what makes
            // Tab-then-type replace rather than append. A field focused by a click does not get it,
            // because the click has already said where the caret goes — which is why this asks the
            // document how the focus arrived rather than assuming.
            if (Document.KeyboardMode) {
                SelectAll();
            }
        } else {
            dragging = false;
        }
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                Document.Focus(this);
                MoveCaret(IndexAt(args.X, args.Y), args.Modifiers.HasFlag(ModifierKeys.Shift));

                // Captured, so that a selection drag that leaves the field keeps extending. Without
                // it the drag stops at the border and the user has to make the selection twice.
                dragging = true;
                Document.CapturePointer(this);

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                MoveCaret(IndexAt(args.X, args.Y), true);
                args.Handled = true;
                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();
                args.Handled = true;
                break;

            default:
                break;
        }
    }

    /// <summary>Turns the second and third taps of a run into a selection.</summary>
    /// <remarks>
    ///     ⚠ <b>On the tap rather than on the press.</b> The press that completes a double click has
    ///     already been through <see cref="Pointed" /> and put the caret where it landed — which is
    ///     what a single click means and is the right thing to have done until the release says
    ///     otherwise. Widening it here is why the caret ends up at the end of the word rather than
    ///     wherever inside it the pointer was.
    /// </remarks>
    void Tapped(TapEvent args) {
        if (args.Count < 2) {
            return;
        }

        SelectAt(IndexAt(args.X, args.Y), args.Count);
        args.Handled = true;
    }

    /// <summary>Which caret index a point in document space lands on.</summary>
    /// <remarks>
    ///     Pixels all the way, which is what changed when a line became several runs: a mixed-font
    ///     line has no single design-unit scale to divide by, so the conversion belongs inside each
    ///     run rather than out here. The <c>y</c> is what picks the line, and for a single-line field
    ///     it can only ever pick the one.
    /// </remarks>
    int IndexAt(float x, float y) =>
        text.Block() is { } block ? block.CaretIndexAt(x - text.AbsoluteLeft, y - text.AbsoluteTop) : 0;

    /// <summary>Where the line holding an index begins.</summary>
    int LineStart(int index) =>
        text.Block() is { } block ? block.Lines[block.LineOf(index)].Start : 0;

    /// <summary>And where it ends, before the break that ended it.</summary>
    int LineEnd(int index) {
        if (text.Block() is not { } block) {
            return Value?.Length ?? 0;
        }

        var line = block.Lines[block.LineOf(index)];

        return line.Start + line.Length;
    }

    /// <summary>The index a line up or down, keeping roughly the same column.</summary>
    /// <remarks>
    ///     ⚠ <b>By pixel offset rather than by character count, which is what makes it land under the
    ///     caret rather than <i>n</i> characters into the next line.</b> Proportional text has no
    ///     column, and a caret that counted characters would drift left across a line of capitals and
    ///     right across a line of commas. Off the top or the bottom, it goes to the ends — which is
    ///     what every editor does and what stops Up on the first line doing nothing at all.
    /// </remarks>
    int Vertically(int delta) {
        if (text.Block() is not { } block) {
            return CaretIndex;
        }

        var line = block.LineOf(CaretIndex);
        var wanted = line + delta;

        if (wanted < 0) {
            return 0;
        }

        if (wanted >= block.Lines.Length) {
            return Value?.Length ?? 0;
        }

        return block.Lines[wanted].CaretIndexAt(block.Lines[line].CaretOffset(CaretIndex));
    }

    void Typed(TextInputEvent args) {
        if (string.IsNullOrEmpty(args.Text)) {
            return;
        }

        Replace(args.Text);
        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        var shift = args.Modifiers.HasFlag(ModifierKeys.Shift);

        // ⚠ Ctrl on Windows and Linux, Meta on macOS — and this assembly cannot know which it is on,
        // so it takes either. The cost is that Meta-Left also moves by word on Windows, where
        // nothing else claims it; the alternative is a text field that does not respond to the
        // shortcuts of whichever platform the author did not think of.
        var word = args.Modifiers.HasFlag(ModifierKeys.Control) || args.Modifiers.HasFlag(ModifierKeys.Meta);

        switch (args.Key) {
            case InputKey.Left:
                MoveCaret(word ? WordBefore(CaretIndex) : Step(CaretIndex, -1), shift);
                break;

            case InputKey.Right:
                MoveCaret(word ? WordAfter(CaretIndex) : Step(CaretIndex, 1), shift);
                break;

            // ⚠ To the end of the *line* in a field that has more than one, and to the end of the
            // value in one that does not. Both are what Home and End mean where they came from, and
            // a text area whose Home jumped to the top of the document would be the odd one out.
            case InputKey.Home:
                MoveCaret(AcceptsNewlines ? LineStart(CaretIndex) : 0, shift);
                break;

            case InputKey.End:
                MoveCaret(AcceptsNewlines ? LineEnd(CaretIndex) : Value?.Length ?? 0, shift);
                break;

            case InputKey.Up when AcceptsNewlines:
                MoveCaret(Vertically(-1), shift);
                break;

            case InputKey.Down when AcceptsNewlines:
                MoveCaret(Vertically(1), shift);
                break;

            case InputKey.Backspace:
                Backspace();
                break;

            case InputKey.Delete:
                Forward();
                break;

            case InputKey.A when word:
                SelectAll();
                break;

            // ⚠ A newline in a text area and a submission everywhere else. Ctrl-Enter still submits
            // in a text area, because a form's default button has to stay reachable from a field
            // that has claimed the plain key.
            case InputKey.Enter or InputKey.KeypadEnter when AcceptsNewlines && !word:
                Replace("\n");
                break;

            case InputKey.Enter or InputKey.KeypadEnter:
                OnSubmit();
                Submitted?.Invoke(this);
                break;

            default:
                // Everything else, including every key that produces a character. Those arrive as
                // TextInputEvent and must not be handled here, or the field would consume Escape,
                // the function keys and every shortcut an ancestor was listening for.
                return;
        }

        args.Handled = true;
    }

    void Backspace() {
        if (!HasSelection) {
            var previous = Step(CaretIndex, -1);
            if (previous == CaretIndex) {
                return;
            }

            SelectionAnchor = previous;
        }

        Replace(string.Empty);
    }

    void Forward() {
        if (!HasSelection) {
            var next = Step(CaretIndex, 1);
            if (next == CaretIndex) {
                return;
            }

            SelectionAnchor = next;
        }

        Replace(string.Empty);
    }

    /// <summary>The grapheme boundary one step from an index.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>index ± 1</c>.</b> A caret that moved by one UTF-16 unit would land between
    ///     the two halves of a surrogate pair — inside an emoji — and between a letter and its
    ///     combining accent. <c>GraphemeBreaker</c> is the UAX#29 implementation the text assembly
    ///     already ships and tests, and this is what it is for.
    /// </remarks>
    int Step(int index, int direction) {
        if (Value is not { Length: > 0 } value) {
            return 0;
        }

        var boundaries = new List<int>();
        GraphemeBreaker.Collect(value, boundaries);

        if (direction < 0) {
            var best = 0;
            foreach (var boundary in boundaries) {
                if (boundary < index) {
                    best = boundary;
                }
            }

            return index <= 0 ? 0 : best;
        }

        foreach (var boundary in boundaries) {
            if (boundary > index) {
                return boundary;
            }
        }

        return value.Length;
    }

    /// <summary>The start of the word before an index.</summary>
    int WordBefore(int index) {
        if (Value is not { Length: > 0 } value || index <= 0) {
            return 0;
        }

        var boundaries = new List<int>();
        WordBreaker.Collect(value, boundaries);

        var best = 0;
        foreach (var boundary in boundaries) {
            if (boundary < index) {
                best = boundary;
            }
        }

        return best;
    }

    /// <summary>The start of the word after an index.</summary>
    int WordAfter(int index) {
        if (Value is not { Length: > 0 } value) {
            return 0;
        }

        var boundaries = new List<int>();
        WordBreaker.Collect(value, boundaries);

        foreach (var boundary in boundaries) {
            if (boundary > index) {
                return boundary;
            }
        }

        return value.Length;
    }
}
