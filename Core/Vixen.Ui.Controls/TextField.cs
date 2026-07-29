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
///         ⚠ <b>Single line, because <see cref="TextLine" /> is.</b> Nothing in the framework breaks
///         a paragraph across lines yet — <c>Vixen.Ui.Text</c> has the UAX#14 breaker and the draw
///         path does not use it — so a value longer than the box scrolls sideways rather than
///         wrapping, and <see cref="TextArea" /> is a taller box rather than a different control.
///         Said plainly: this is the honest state of the text stack rather than a decision about
///         what a text area should be.
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
    ///     Distinct from <see cref="Control.Disabled" />: a read-only field still takes the focus,
    ///     is still a tab stop, and its text can still be selected and copied. A disabled one is out
    ///     of reach entirely. Conflating them is how a form ends up with values nobody can read.
    /// </remarks>
    [UiProperty]
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

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Drawn on the field rather than on the text element</b>, and before the children, so
    ///     the selection band lands under the glyphs it highlights. Drawing it on the text element
    ///     would put it over them, and a selected word would be a coloured rectangle.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (!IsFocused || text.Line() is not { } line) {
            return;
        }

        var origin = text.AbsoluteLeft;
        var top = text.AbsoluteTop;
        var height = line.Height;

        if (HasSelection) {
            var from = origin + line.CaretOffset(SelectionStart);
            var to = origin + line.CaretOffset(SelectionEnd);

            context.FillRectangle(
                new Rectangle(MathF.Min(from, to), top, MathF.Abs(to - from), height),
                Document.ColorOf(Style, selectionColor) ?? new Color4(0.25f, 0.45f, 0.85f, 0.35f)
            );
        }

        // ⚠ The caret is drawn even when there is a selection. Every editor does — the caret is the
        // end you are extending from, and hiding it during a Shift-Arrow leaves the user unable to
        // tell which way the next keystroke will grow the selection.
        context.FillRectangle(
            new Rectangle(origin + line.CaretOffset(CaretIndex), top, 1f, height),
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
        if (text.Line() is not { } line) {
            text.OffsetX = 0f;
            return;
        }

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
                MoveCaret(IndexAt(args.X), args.Modifiers.HasFlag(ModifierKeys.Shift));

                // Captured, so that a selection drag that leaves the field keeps extending. Without
                // it the drag stops at the border and the user has to make the selection twice.
                dragging = true;
                Document.CapturePointer(this);

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                MoveCaret(IndexAt(args.X), true);
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

    /// <summary>Which caret index a document-space x lands on.</summary>
    int IndexAt(float x) {
        if (text.Line() is not { } line) {
            return 0;
        }

        // Pixels all the way, which is what changed when a line became several runs: a mixed-font
        // line has no single design-unit scale to divide by, so the conversion belongs inside each
        // run rather than out here.
        return line.CaretIndexAt(x - text.AbsoluteLeft);
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

            case InputKey.Home:
                MoveCaret(0, shift);
                break;

            case InputKey.End:
                MoveCaret(Value?.Length ?? 0, shift);
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
