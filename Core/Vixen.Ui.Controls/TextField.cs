// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;
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
public abstract partial class TextField : Control, ITextInputTarget {
    UiElement text = null!;
    UiElement placeholder = null!;
    int selectionColor;
    int caretColor;
    int caretColorStandard;
    bool dragging;
    Func<string?, string?>? validator;

    // The input method's pre-edit and its own cursor within it. Empty for the whole life of a field
    // nobody types Japanese into, which is why they are two plain fields rather than state anything
    // else has to know about.
    string composition = string.Empty;
    int compositionCaret;

    // When the caret last moved, on the document's clock, and where it was when it last drew. The
    // blink's phase is measured from the first of these rather than from zero, so typing holds the
    // caret solid — see `CaretBlink`.
    //
    // ⚠ Noticed at draw time rather than stamped from a setter, and the reason is that `CaretIndex`
    // is written from `OnValueChanged`, which a `[UiProperty]` will run on a field that has been
    // removed from its document. `Document` throws on such an element; `OnDraw` cannot be reached on
    // one. So the safe place to read a clock is the only place that is certain there is one.
    TimeSpan caretRestarted;
    int caretDrawn = -1;
    CaretAffinity caretDrawnAffinity;

    /// <summary>Scratch for the visual ranges a highlight covers on one line.</summary>
    /// <remarks>
    ///     A field rather than a local because the two things it paints — the selection and an input
    ///     method's underline — ask once per line per frame, and a caret blinking on a focused field
    ///     is the one thing in a still interface that redraws on its own.
    /// </remarks>
    readonly List<(float X, float Width)> ranges = [];

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

    /// <summary>Whether a value has to be supplied.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The only producer of <see cref="AccessibleStates.Required" /> in the tree.</b> The
    ///         flag has existed since the accessibility tree did and nothing set it, so a form's
    ///         mandatory fields sounded exactly like its optional ones — which is the one thing a
    ///         screen-reader user needs to know <i>before</i> they submit rather than after.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty required field is <i>invalid</i>, from the moment it is marked, and that
    ///         is deliberate.</b> Deferring the verdict until a submit is what makes a form tell you
    ///         about four mistakes at once at the end; the state is what the field is in, and when to
    ///         <i>show</i> it is the theme's business — see the <c>invalid</c> class.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnRequiredChanged))]
    public partial bool Required { get; set; }

    /// <summary>What decides whether the value is acceptable, for a caller that will not subclass.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The application-reachable half of <see cref="Validate" />.</b> A rule is usually
    ///         one predicate about one field — an address that has to contain an at-sign, a name
    ///         already taken — and a control library that could only express it by deriving a type
    ///         would be asking for a class per field on every form ever written.
    ///     </para>
    ///     <para>
    ///         Returns <c>null</c> when the value is acceptable and the reason when it is not. The
    ///         reason is the caller's own words: this assembly does not know what the field holds and
    ///         cannot write a sentence about it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Assigning one revalidates immediately</b>, so a rule attached to a field that has
    ///         already been filled in does not wait for the next keystroke to notice.
    ///     </para>
    /// </remarks>
    public Func<string?, string?>? Validator {
        get => validator;
        set {
            validator = value;
            Revalidate();
        }
    }

    /// <summary>Why the value is not acceptable, or <c>null</c> when it is.</summary>
    /// <remarks>
    ///     ⚠ <b>Not written into the accessibility tree by this control, and that is not an
    ///     oversight.</b> ARIA pairs <c>aria-invalid</c> — which
    ///     <see cref="NativeAccessibleState" /> does produce — with a <i>separate</i> element holding
    ///     the words, reached by <c>aria-describedby</c>; the error text a form shows is a label
    ///     somewhere in the layout, and pointing at it is one
    ///     <c>field.AddAccessibleRelation(AccessibleRelation.DescribedBy, message)</c>. Folding the
    ///     string into <see cref="UiElement.AccessibleDescription" /> from here would silently
    ///     overwrite whatever the application had put there.
    /// </remarks>
    public string? ValidationMessage { get; private set; }

    /// <summary>Whether the value is acceptable.</summary>
    public bool IsValid => ValidationMessage is null;

    /// <summary>Where the caret is, as a UTF-16 index into <see cref="Value" />.</summary>
    public int CaretIndex { get; private set; }

    /// <summary>How long the caret spends on, and then off. Zero draws it solid.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Half a period, not a whole one</b>, because that is the number every platform
    ///         exposes and the number a person setting it is thinking of. The default is 530 ms,
    ///         Windows' own, and the phase is measured from the last time the caret moved rather than
    ///         from a free-running clock — so the caret is solid on the frame a key lands and stays
    ///         solid for as long as somebody is typing. A shared phase would blink out mid-word.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="TimeSpan.Zero" /> is a solid caret and is the accessibility answer.</b>
    ///         A blink is motion, and motion beside the thing a user is reading is exactly what
    ///         <c>prefers-reduced-motion</c> is about; a control that could only blink would have to be
    ///         worked around rather than configured. Nothing in the tree sets it yet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It costs a redraw twice a period, and only while this field has the focus.</b>
    ///         <see cref="OnDraw" /> returns before anything else when it does not, so an interface
    ///         with forty fields on it and none of them focused is as still as it was — which is what
    ///         <c>EditorStillnessTests</c> measures and what a blink built on a subscription rather
    ///         than on the draw would have broken.
    ///     </para>
    /// </remarks>
    public TimeSpan CaretBlink { get; set; } = TimeSpan.FromMilliseconds(530);

    /// <summary>Which of the two characters either side of <see cref="CaretIndex" /> the caret is on.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An index names a boundary, and a boundary is not always one place.</b> At a wrap it
    ///         is the end of one row and the start of the next; where the direction changes it is at
    ///         opposite ends of a run. This is the bit that says which, and it is <i>state the field
    ///         carries</i> rather than something re-derived — a caret walked to the end of a line and
    ///         a caret pressed Down onto the next arrive at the same number, and only how they got
    ///         there tells them apart.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="CaretAffinity.Upstream" /> is the resting value, not
    ///         <c>Downstream</c>.</b> It is what the index-only overloads of
    ///         <see cref="TextLayout.CaretAt(int)" /> and <see cref="TextLine.CaretOffset(int)" />
    ///         already answered, so a field that has never been clicked draws its caret exactly where
    ///         it drew it before this existed. Defaulting the other way would move every caret sitting
    ///         on a run boundary the first time the field was shown.
    ///     </para>
    /// </remarks>
    public CaretAffinity CaretAffinity { get; private set; } = CaretAffinity.Upstream;

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

    /// <summary>What an input method is composing at the caret, and has not committed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It is deliberately <i>not</i> in <see cref="Value" />, and that is the whole
    ///         design.</b> A pre-edit is provisional: it is replaced in place on every keystroke and
    ///         may be abandoned entirely, so a field that put it in the value would raise
    ///         <see cref="ValueChanged" /> for every intermediate reading of every word — and would
    ///         hand each one to <see cref="Coerce" />, which for a
    ///         <c>NumericInput</c> means a Japanese pre-edit is rejected character by character and
    ///         the user cannot type into the field at all.
    ///     </para>
    ///     <para>
    ///         It <i>is</i> in what the field <b>displays</b>, spliced in at the caret, because the
    ///         alternative is a box that shows nothing while somebody types into it with the
    ///         candidate window floating over it. So the value and the display are two strings while
    ///         a composition is running, and every index into one of them belongs to exactly one —
    ///         which is what <see cref="DisplayCaret" /> is for.
    ///     </para>
    /// </remarks>
    public string Composition => composition;

    /// <summary>Whether an input method has an uncommitted pre-edit in this field.</summary>
    public bool IsComposing => composition.Length > 0;

    /// <summary>Where the caret is in the string the field is <i>displaying</i>.</summary>
    /// <remarks>
    ///     ⚠ <b>The same as <see cref="CaretIndex" /> except while composing</b>, when the pre-edit
    ///     sits between them and the input method's own cursor decides how far into it the caret
    ///     goes. Drawing the caret from <see cref="CaretIndex" /> instead puts it in front of a
    ///     half-converted phrase rather than inside it, which is where every IME expects it not to
    ///     be.
    /// </remarks>
    public int DisplayCaret => CaretIndex + (IsComposing ? compositionCaret : 0);

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
    protected override AccessibleRole NativeRole => AccessibleRole.TextBox;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>null</c>, deliberately, and it is the most important line in this file for
    ///     accessibility.</b> A field has no words of its own. The obvious fallback —
    ///     <see cref="Placeholder" /> — is the one every toolkit reaches for and it is wrong twice:
    ///     a placeholder is a hint rather than a name, and it disappears the moment there is a
    ///     value, so a form announced from placeholders is a form whose fields lose their names as
    ///     they are filled in. An inspector of four numeric fields would announce four fields all
    ///     called "0.00".
    ///     <para>
    ///         What a field's name comes from is the words beside it, which are somebody else's
    ///         element: one
    ///         <c>field.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption)</c>. Answering
    ///         <c>null</c> until somebody does that is what lets a gate fail an unlabelled field
    ///         rather than passing it with a plausible-looking lie.
    ///     </para>
    /// </remarks>
    protected override string? NativeAccessibleName => null;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Value" /> as it stands, read on demand. A field whose value is being typed into
    ///     has no notification to remember and no cached copy to be stale.
    /// </remarks>
    protected override string? NativeAccessibleValue => Value;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <see cref="AccessibleStates.Editable" /> always — it is what makes a field a field to
    ///         a screen reader, and it is the state that turns on a braille display's input mode.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="AccessibleStates.ReadOnly" /> is added <i>alongside</i> it rather than
    ///         instead of it</b>, for <see cref="ReadOnly" />'s own reason: a read-only field is
    ///         still a field, still takes the focus and can still have its text selected and copied.
    ///         Reporting it as not editable would be reporting it as
    ///         <see cref="Control.Disabled" />, which is the conflation the property's remarks
    ///         already warn about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="AccessibleStates.Required" /> and <see cref="AccessibleStates.Invalid" />
    ///         are produced here and nowhere else in the tree.</b> Both flags predate any control
    ///         that could set them, so until <see cref="Required" /> and <see cref="Validate" />
    ///         existed a form's mandatory fields and its rejected ones were indistinguishable from
    ///         its ordinary ones to anything reading the accessibility tree.
    ///     </para>
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Editable
        | (ReadOnly ? AccessibleStates.ReadOnly : AccessibleStates.None)
        | (AcceptsNewlines ? AccessibleStates.MultiLine : AccessibleStates.None)
        | (Required ? AccessibleStates.Required : AccessibleStates.None)
        | (IsValid ? AccessibleStates.None : AccessibleStates.Invalid);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // The placeholder first, so that it sits under the value rather than over it — both are
        // absolutely positioned in the same place by the theme, and one of the two is always empty.
        placeholder = Part("field-placeholder");
        text = Part("field-text");

        selectionColor = Document.PropertyId("--selection-color");
        caretColor = Document.PropertyId("--caret-color");
        caretColorStandard = Document.PropertyId("caret-color");

        // ⚠ The first things in this repository ever to register an element command handler, and
        // that is the point of them rather than a side effect. `CommandRoute`'s rule — the nearest
        // responder that answers wins, all the way out — had no production responders at all, so the
        // element leg of the walk always found nothing and the whole design was unfalsifiable outside
        // its own tests. A focused field answering these verbs is the smallest true instance of it: a
        // menu item now means "act on this field's text" while the caret is here and whatever the
        // shell says when it is not, with nothing shell-shaped in the control.
        //
        // ⚠ Select All was for one batch the only verb here, because nothing above `Vixen.Platform`
        // could reach `IClipboard` and no undo manager existed below the editor's `CommandStack` — a
        // handler that ran and did nothing is worse than none, since the route would report the verb
        // available and the menu item would go live. `IUiClipboard` retired the first half of that,
        // so Cut, Copy and Paste are registered here and their `CanExecute` asks the pasteboard
        // rather than assuming. Undo and Redo are still keystrokes only: they answer through
        // `FindUndoManager`, which returns nothing in an application that has not put one anywhere,
        // and an always-grey menu item is the thing this comment refuses.
        //
        // The four are ids, not a private key switch, so a menu item spelling `edit.copy` and a
        // keymap bound to it both reach the focused field without either of them knowing a text field
        // exists. The chords below still call the same methods, because a field must answer them with
        // no keymap installed at all.
        AddCommandHandler("edit.cut", () => Cut(), () => CanCopy && !ReadOnly && !Disabled);
        AddCommandHandler("edit.copy", () => Copy(), () => CanCopy);
        AddCommandHandler("edit.paste", () => Paste(), () => CanPaste);
        AddCommandHandler("edit.select-all", SelectAll, () => !Disabled && !string.IsNullOrEmpty(Value));

        AddHandler<KeyEvent>(static (element, args) => ((TextField) element).Keyed(args));
        AddHandler<TextInputEvent>(static (element, args) => ((TextField) element).Typed(args));
        AddHandler<TextCompositionEvent>(static (element, args) => ((TextField) element).Composing(args));
        AddHandler<PointerEvent>(static (element, args) => ((TextField) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((TextField) element).Tapped(args));
        AddHandler<FocusEvent>(static (element, args) => ((TextField) element).Refocused(args));
    }

    /// <summary>Moves the caret, and either drops the selection or extends it.</summary>
    /// <param name="index">Where to put it.</param>
    /// <param name="extend">Whether Shift is held.</param>
    /// <remarks>
    ///     ⚠ <b>Resets <see cref="CaretAffinity" /> to <see cref="CaretAffinity.Upstream" />.</b> A
    ///     caller that says only where the caret goes has not said which side of it, and the resting
    ///     value is the one every index-only caret answer already used. A caller that knows — a
    ///     click, a vertical move — has <see cref="MoveCaret(int, CaretAffinity, bool)" />.
    /// </remarks>
    public void MoveCaret(int index, bool extend = false) => MoveCaret(index, CaretAffinity.Upstream, extend);

    /// <summary>Moves the caret to one side of an index, and either drops the selection or extends it.</summary>
    /// <param name="index">Where to put it.</param>
    /// <param name="affinity">Which of the two characters either side of it the caret is on.</param>
    /// <param name="extend">Whether Shift is held.</param>
    public void MoveCaret(int index, CaretAffinity affinity, bool extend = false) {
        var length = Value?.Length ?? 0;

        // ⚠ A caret move ends the run of typing, so the next keystroke is a new undo entry. Without
        // it, typing a word, clicking somewhere else and typing another would be one ⌘Z that took
        // back two edits in two places.
        BreakUndoRun();

        CaretIndex = Math.Clamp(index, 0, length);
        CaretAffinity = affinity;

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

        var caretBefore = CaretIndex;
        var anchorBefore = SelectionAnchor;

        // The caret before the value, because assigning the value raises the change and a handler
        // that reads the caret should see where it ended up rather than where it was.
        CaretIndex = start + replacement.Length;
        SelectionAnchor = CaretIndex;

        Value = updated;

        Record(value, caretBefore, anchorBefore, start, end, replacement);
    }

    // The open run of typing, if one is running. `edit` is the entry already on the manager's stack;
    // extending the run mutates it in place rather than pushing a second one, which is what makes
    // one ⌘Z take back a word rather than a letter.
    FieldEdit? run;
    int runEnd;

    /// <summary>One entry on an undo stack: what the field held either side of an edit.</summary>
    /// <remarks>
    ///     ⚠ <b>Mutable, and that is what coalescing is.</b> A run of typing is one entry whose
    ///     "after" grows with every keystroke; pushing an entry per character gives a ⌘Z that takes
    ///     back one letter, which every field on every desktop refuses to do.
    /// </remarks>
    sealed class FieldEdit {
        public required string Before { get; init; }

        public required int CaretBefore { get; init; }

        public required int AnchorBefore { get; init; }

        public required string After { get; set; }

        public required int CaretAfter { get; set; }
    }

    /// <summary>Records an edit with the nearest undo manager, if the field is anywhere near one.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing happens when there is no manager, and that is the design.</b> A throwaway
    ///     field in a dialog with nothing behind it registers nothing and leaves ⌘Z to whatever else
    ///     was listening — which is what stops a text box in the editor from shadowing the editor's
    ///     own Undo with a stack that knows about typing and nothing else.
    /// </remarks>
    void Record(string before, int caretBefore, int anchorBefore, int start, int end, string replacement) {
        if (FindUndoManager() is not { IsPerforming: false } manager) {
            run = null;
            return;
        }

        var after = Value ?? string.Empty;

        // ⚠ Coalesced by *shape*, not by a clock. A wall-clock typing window calibrated on an idle
        // machine is this repository's largest flake source; what makes two keystrokes one edit is
        // that the second inserted at the end of the first with nothing selected and no line broken.
        // Anything else — a delete, a paste, a caret move, a newline — starts a fresh entry.
        var isTyping = end == start && replacement.Length > 0 && !replacement.Contains('\n');

        if (isTyping && run is { } open && start == runEnd) {
            open.After = after;
            open.CaretAfter = CaretIndex;
            runEnd = CaretIndex;

            return;
        }

        var edit = new FieldEdit {
            Before = before,
            CaretBefore = caretBefore,
            AnchorBefore = anchorBefore,
            After = after,
            CaretAfter = CaretIndex
        };

        manager.Register(
            isTyping ? "Typing" : "Editing",
            () => Restore(edit.Before, edit.CaretBefore, edit.AnchorBefore),
            () => Restore(edit.After, edit.CaretAfter, edit.CaretAfter)
        );

        run = isTyping ? edit : null;
        runEnd = CaretIndex;
    }

    /// <summary>Puts the field back to a recorded state, selection and all.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection too, not only the value and the caret.</b> Undoing a cut that has to be
    ///     followed by re-selecting what came back is an undo that only half happened, and it is what
    ///     a field that restored the string alone gives.
    /// </remarks>
    void Restore(string value, int caret, int anchor) {
        run = null;

        Value = value;

        var length = Value?.Length ?? 0;

        CaretIndex = Math.Clamp(caret, 0, length);
        SelectionAnchor = Math.Clamp(anchor, 0, length);

        Reveal();
    }

    /// <summary>Ends the open run of typing, so the next keystroke starts a new undo entry.</summary>
    /// <remarks>
    ///     Called wherever the user has said the run is over without editing: moving the caret,
    ///     clicking elsewhere, leaving the field.
    /// </remarks>
    void BreakUndoRun() => run = null;

    /// <summary>Takes back the last edit, if this field is under an undo manager.</summary>
    /// <returns>Whether anything was undone.</returns>
    public bool Undo() {
        BreakUndoRun();

        return FindUndoManager() is { CanUndo: true } manager && manager.Undo();
    }

    /// <summary>Puts back the last undone edit.</summary>
    /// <returns>Whether anything was redone.</returns>
    public bool Redo() {
        BreakUndoRun();

        return FindUndoManager() is { CanRedo: true } manager && manager.Redo();
    }

    /// <summary>Whether there is something to put on the clipboard, and somewhere to put it.</summary>
    public bool CanCopy => HasSelection && Document.Clipboard is not null;

    /// <summary>Whether there is text on the clipboard and this field would take it.</summary>
    /// <remarks>
    ///     ⚠ Asks the clipboard rather than caching, because the answer is another application's to
    ///     change and nothing tells us when it does. That is what <c>validateMenuItem:</c> does on
    ///     the platform this shape comes from.
    /// </remarks>
    public bool CanPaste => !ReadOnly && !Disabled && Document.Clipboard is { HasText: true };

    /// <summary>Puts the selection on the clipboard.</summary>
    /// <returns>Whether anything was written.</returns>
    public bool Copy() => CanCopy && Document.Clipboard!.SetText(SelectedText);

    /// <summary>Puts the selection on the clipboard and deletes it.</summary>
    /// <returns>Whether anything was written.</returns>
    /// <remarks>
    ///     ⚠ <b>A read-only field cuts nothing and copies nothing.</b> Not "copies without
    ///     deleting": the text is still on screen, so a user who reached for Cut and got Copy has no
    ///     way to tell which happened, and the next paste is a silent duplication. The verb is
    ///     disabled instead, which is what the menu shows.
    /// </remarks>
    public bool Cut() {
        if (ReadOnly || Disabled || !Copy()) {
            return false;
        }

        Replace(string.Empty);

        return true;
    }

    /// <summary>Replaces the selection with the clipboard's text.</summary>
    /// <returns>Whether anything was inserted.</returns>
    public bool Paste() {
        if (!CanPaste || !Document.Clipboard!.TryGetText(out var text) || text.Length == 0) {
            return false;
        }

        Replace(Flatten(text));

        return true;
    }

    /// <summary>What a paste actually inserts, once the field has had its say about line breaks.</summary>
    /// <remarks>
    ///     ⚠ <b>A single-line field turns every break into a space rather than dropping it.</b>
    ///     Dropping it welds the last word of one line to the first of the next — "Ada\nLovelace"
    ///     pastes as "AdaLovelace" — which looks like a truncation bug in whatever reads the field
    ///     back. Truncating at the first break, which the Win32 edit control does, loses data the
    ///     user watched themselves copy. A space is the only one of the three that is visibly what
    ///     was asked for.
    ///     <para>
    ///         CRLF and a lone CR are normalised first, so a paste from a Windows application does
    ///         not arrive with a stray carriage return inside a value that is then compared,
    ///         serialised and diffed against one without.
    ///     </para>
    /// </remarks>
    string Flatten(string text) {
        var normalised = text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

        return AcceptsNewlines ? normalised : normalised.Replace('\n', ' ');
    }

    /// <summary>What the field does with a value on its way in.</summary>
    /// <param name="value">What was assigned.</param>
    /// <returns>What to store.</returns>
    /// <remarks>
    ///     Overridden by <see cref="NumericInput" />, which is the reason it exists: a numeric field
    ///     that let a caller assign "banana" would then have to decide what its number was.
    /// </remarks>
    protected virtual string? Coerce(string? value) => value;

    /// <summary>What the field draws for a value on its way out.</summary>
    /// <param name="value">What is being shown, with any pre-edit already spliced in.</param>
    /// <returns>What to put in the text part.</returns>
    /// <remarks>
    ///     <para>
    ///         The mirror of <see cref="Coerce" /> and the only seam between what a field holds and
    ///         what it shows. Overridden by <see cref="SecureTextBox" />, which is the reason it
    ///         exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An override must return one UTF-16 unit per unit it was given.</b> The caret, the
    ///         selection, the hit test and the composition underline are all indices into the value,
    ///         and they are measured against this layout — so a substitution that changed the length
    ///         would put the caret in a different place from the character it is in front of. Masking
    ///         per code unit rather than per grapheme is what keeps that true, and on a run of
    ///         identical bullets there is nothing a grapheme would have bought.
    ///     </para>
    /// </remarks>
    protected virtual string? Shown(string? value) => value;

    /// <summary>Whether a value is acceptable, and why not when it is not.</summary>
    /// <param name="value">What the field holds.</param>
    /// <returns><c>null</c> when the value is acceptable, otherwise the reason it is not.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The third seam on a value, beside <see cref="Coerce" /> and <see cref="Shown" />,
    ///         and it is the one that answers a question rather than substituting a string.</b>
    ///         <see cref="Coerce" /> is what a field will <i>hold</i> — a numeric input refuses
    ///         letters outright — and that is deliberately not where a rule about acceptability
    ///         belongs: a field that silently dropped what was typed because it was too short could
    ///         never be typed into at all. Validity is a state the field is <i>in</i>, with the value
    ///         still there to be corrected.
    ///     </para>
    ///     <para>
    ///         The default applies <see cref="Required" /> first and then <see cref="Validator" />,
    ///         so an override that wants both calls <c>base.Validate</c>. Order matters: an empty
    ///         required field is the only case this assembly can describe in words, and a custom rule
    ///         asked about an empty string would have to repeat the emptiness check to avoid
    ///         answering "not a valid address" about a field nobody has reached yet.
    ///     </para>
    /// </remarks>
    protected virtual string? Validate(string? value) =>
        Required && string.IsNullOrEmpty(value) ? ControlStrings.FieldRequired.Text : Validator?.Invoke(value);

    /// <summary>Asks <see cref="Validate" /> again and republishes the answer.</summary>
    /// <remarks>
    ///     ⚠ <b>Public because validity can depend on something that is not the value.</b> A field
    ///     that must not match another field's contents, a name checked against a list that has just
    ///     been fetched — nothing about those changes when a keystroke lands here, so a control that
    ///     only revalidated on its own edits would sit there green until the user touched it. Cheap
    ///     to call: it is one predicate and it only writes anything when the verdict moved.
    /// </remarks>
    public void Revalidate() {
        var message = Validate(Value);

        if (string.Equals(message, ValidationMessage, StringComparison.Ordinal)) {
            return;
        }

        var was = IsValid;
        ValidationMessage = message;

        if (IsValid == was) {
            return;
        }

        // ⚠ Only when the verdict itself moved. Two different reasons for the same field being
        // invalid are the same picture and the same `aria-invalid`, and a class rewritten on every
        // keystroke of a rejected value is churn in the selector engine for no change on screen.
        if (IsValid) {
            RemoveClass("invalid");
        } else {
            AddClass("invalid");
        }

        InvalidateAccessibility();
    }

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

    /// <summary>Whether the caret is drawn on this frame, which is <see cref="ShowsCaret" /> and the blink.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two properties, and the draw asks this one.</b> <see cref="ShowsCaret" /> answers
    ///         whether this field has an insertion point at all, which is a fact about the field;
    ///         whether it is lit right now is a fact about the frame. Folding the second into the
    ///         first would make a subclass asking "does this field have a caret" get a different
    ///         answer twice a second — which is why <c>ShowsCaret</c> stays <c>protected</c> and this
    ///         is private: no subclass draws its own caret, and one that started to would want the
    ///         fact rather than the phase.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A period is <see cref="CaretBlink" /> twice over and the first half is on</b>, so
    ///         a caret that has just moved is lit — <c>caretRestarted</c> is stamped in the same draw
    ///         that notices the move, and integer division of a zero elapsed gives an even quotient.
    ///     </para>
    /// </remarks>
    bool CaretIsLit {
        get {
            if (!ShowsCaret) {
                return false;
            }

            if (CaretBlink <= TimeSpan.Zero) {
                return true;
            }

            var since = Document.Now - caretRestarted;

            // A clock that went backwards is a host that reset it, not a caret that is half-way
            // through a period. Lit is the answer that cannot look broken.
            return since < TimeSpan.Zero || since.Ticks / CaretBlink.Ticks % 2 == 0;
        }
    }

    /// <summary>What colour to draw the insertion point in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two properties and not one, and the standard spelling is asked first.</b>
    ///         <c>--caret-color</c> is Vixen's own token — <c>ControlTheme.vcss</c> and
    ///         <c>EditorTheme.vcss</c> both declare it on the root, so it is the palette's answer for
    ///         a whole document. <c>caret-color</c> is CSS's, and it is what <c>caret-accent</c>
    ///         emits: a statement about <i>this</i> field. The palette is the fallback and the
    ///         statement wins, which is the order that makes both spellings mean what somebody
    ///         writing them expects.
    ///     </para>
    ///     <para>
    ///         ⚠ What it costs, stated rather than left to be found: both names inherit, so a
    ///         document that declared <c>caret-color</c> at the root <i>and</i> <c>--caret-color</c>
    ///         on one field would get the root's answer on that field. Nothing in the tree does
    ///         that, and the alternative — comparing which declaration is nearer — is not something
    ///         a computed style can answer, because inheritance has already flattened the distance.
    ///     </para>
    /// </remarks>
    Color4 CaretColour(DrawContext context) =>
        Document.ColorOf(Style, caretColorStandard)
        ?? Document.ColorOf(Style, caretColor)
        ?? context.Foreground;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Drawn on the field rather than on the text element</b>, and before the children, so
    ///     the selection band lands under the glyphs it highlights. Drawing it on the text element
    ///     would put it over them, and a selected word would be a coloured rectangle.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (!IsFocused) {
            // ⚠ Held at the start of a period rather than left where it was, so that a field
            // refocused at the index it was last focused at gets a lit caret on the first frame
            // instead of resuming half-way through an off half. A click has to show something.
            caretRestarted = Document.Now;
            caretDrawn = -1;
            return;
        }

        // The caret went somewhere since the last frame that drew it, so the blink starts again.
        // See `caretRestarted` for why this is noticed here rather than stamped from a setter.
        if (caretDrawn != DisplayCaret || caretDrawnAffinity != CaretAffinity) {
            caretDrawn = DisplayCaret;
            caretDrawnAffinity = CaretAffinity;
            caretRestarted = Document.Now;
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
            if (CaretIsLit) {
                context.FillRectangle(CaretArea, CaretColour(context));
            }

            return;
        }

        // ⚠ Per line, and a single-line field is the one-line case of it rather than a different
        // path. This used to read `block.Lines[0]` and say so — which was true while nothing wrapped
        // and became a lie the moment `TextArea` grew a newline: the caret stayed pinned to the first
        // line's baseline and a selection spanning two lines was drawn as one band across the first.
        // ⚠ Not while an input method is composing. The composition has already replaced whatever
        // was selected, so `SelectionStart` and `SelectionEnd` are indices into the VALUE while every
        // other number in this method is an index into the displayed string — and the two differ by
        // the pre-edit. Painting a band from them puts it in the wrong place, over text nobody
        // selected.
        if (HasSelection && !IsComposing) {
            var first = block.LineOf(SelectionStart);
            var last = block.LineOf(SelectionEnd);
            var colour = Document.ColorOf(Style, selectionColor) ?? new Color4(0.25f, 0.45f, 0.85f, 0.35f);

            for (var index = first; index <= last; index++) {
                var line = block.Lines[index];
                var start = Math.Max(SelectionStart, line.Start);
                var end = Math.Min(SelectionEnd, line.Start + line.Length);

                // ⚠ **Several rectangles per line, not one.** A selection is contiguous in the text
                // and need not be contiguous on the screen: crossing into a run that faces the other
                // way puts the covered glyphs at opposite ends of that run with unselected text
                // between them, and one band from the lower offset to the higher paints over it. See
                // `TextLine.VisualRanges`, which is where the span meets the itemiser's boundaries.
                ranges.Clear();
                line.VisualRanges(start, end, ranges);

                // A line with nothing of its own inside the selection — a blank one in the middle of
                // a selected paragraph — still has to read as selected, so it gets the marker the
                // minimum width below is for.
                if (ranges.Count == 0) {
                    ranges.Add((line.CaretOffset(start), 0f));
                }

                var shift = ShiftOf(line);

                foreach (var range in ranges) {
                    // ⚠ A minimum width, so a line whose whole content is inside the selection but
                    // which ends in the break still reads as selected. Zero-width is what a blank
                    // line in the middle of a selected paragraph would otherwise be, and it looks
                    // like a gap.
                    context.FillRectangle(
                        new Rectangle(
                            origin + shift + range.X,
                            top + block.TopOf(index),
                            MathF.Max(range.Width, index < last ? 3f : 0f),
                            line.Height
                        ),
                        colour
                    );
                }
            }
        }

        if (IsComposing) {
            PaintComposition(context, block, origin, top, text.Text?.Length ?? 0);
        }

        if (!CaretIsLit) {
            return;
        }

        // ⚠ The caret is drawn even when there is a selection. Every editor does — the caret is the
        // end you are extending from, and hiding it during a Shift-Arrow leaves the user unable to
        // tell which way the next keystroke will grow the selection.
        context.FillRectangle(CaretArea, CaretColour(context));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Computed here rather than remembered from the last <see cref="OnDraw" />.</b> The
    ///     caret rectangle used to exist only as three locals inside the paint, which is why nothing
    ///     outside could place an input method's candidate window with it — and the paint now reads
    ///     this, so the two cannot drift into disagreeing about where the caret is.
    /// </remarks>
    public Rectangle CaretArea {
        get {
            var origin = text.AbsoluteLeft;
            var top = text.AbsoluteTop;

            // An empty field has no block to ask and still has a caret — see `OnDraw`, where the
            // same fallback is what makes clicking an empty search box look like something happened.
            if (text.Block() is not { } block) {
                return new(origin, top, 1f, MathF.Max(text.Height, 1f));
            }

            var line = block.Lines[block.LineOf(DisplayCaret, CaretAffinity)];
            var (x, y) = block.CaretAt(DisplayCaret, CaretAffinity);

            return new(origin + ShiftOf(line) + x, top + y, 1f, line.Height);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="ShowsCaret" />, and for exactly its reason: a caret is a promise that the next
    ///     keystroke lands here, and an input method activated over a field that will discard what it
    ///     commits makes the same false promise one layer up.
    /// </remarks>
    public bool AcceptsTextInput => ShowsCaret;

    /// <summary>Underlines the input method's pre-edit, which is what says it is not committed yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Drawn in the caret's colour and not the selection's</b>, because a pre-edit is not
    ///     selected text: it is text that does not exist yet. Every native field marks it with a rule
    ///     under it, and a field that shows it looking exactly like committed text gives a user no
    ///     way to tell what a Return will keep and what an Escape will take away.
    /// </remarks>
    void PaintComposition(DrawContext context, TextLayout block, float origin, float top, int displayed) {
        var start = Math.Clamp(CaretIndex, 0, displayed);
        var end = Math.Clamp(start + composition.Length, 0, displayed);
        var first = block.LineOf(start);
        var last = block.LineOf(end);
        var colour = CaretColour(context);

        for (var index = first; index <= last; index++) {
            var line = block.Lines[index];

            // ⚠ Underlined in visual ranges for the same reason the selection is filled in them: a
            // pre-edit whose script faces the other way from the text around it is exactly the case
            // an input method is used for, so a single rule under it is wrong in the one place this
            // is most likely to be seen.
            ranges.Clear();
            line.VisualRanges(Math.Max(start, line.Start), Math.Min(end, line.Start + line.Length), ranges);

            var shift = ShiftOf(line);

            foreach (var range in ranges) {
                context.FillRectangle(
                    new Rectangle(
                        origin + shift + range.X,
                        top + block.TopOf(index) + line.Height - 1f,
                        range.Width,
                        1f
                    ),
                    colour
                );
            }
        }
    }

    string? CoerceValue(string? value) => Coerce(value);

    void OnValueChanged(string? previous, string? current) {
        Display();

        var length = current?.Length ?? 0;
        CaretIndex = Math.Clamp(CaretIndex, 0, length);
        SelectionAnchor = Math.Clamp(SelectionAnchor, 0, length);

        Restate();
        Reveal();

        // Before the notifications rather than after, so a handler that reads `IsValid` — which is
        // what a submit button's enablement is — sees the verdict on the value it was just handed.
        Revalidate();

        Raise(new ValueChangedEvent<string> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    void OnPlaceholderChanged(string? previous, string? current) {
        placeholder.Text = current;
        Restate();
    }

    void OnRequiredChanged(bool previous, bool current) {
        Revalidate();
        InvalidateAccessibility();
    }

    void OnReadOnlyChanged(bool previous, bool current) {
        if (current) {
            AddClass("read-only");
        } else {
            RemoveClass("read-only");
        }

        // ⚠ <b>The state bit as well as the class, and the class is not redundant.</b> The bit is
        // what CSS spells `:read-only` and what the `read-only:` variant compiles to; the class is
        // what the editor's own themes already select on. Dropping the class to tidy up would
        // restyle every inspector field in the same commit as a variant nobody has used yet.
        State = current ? State | ElementState.ReadOnly : State & ~ElementState.ReadOnly;
    }

    /// <summary>Shows the placeholder only when there is nothing to show instead.</summary>
    /// <remarks>
    ///     A class rather than swapping the text, so that the theme decides what an empty field
    ///     looks like — and so that the placeholder element keeps its text across an edit and back,
    ///     rather than being cleared and reinstated on every keystroke.
    /// </remarks>
    void Restate() {
        var empty = string.IsNullOrEmpty(Value);

        if (empty) {
            AddClass("empty");
        } else {
            RemoveClass("empty");
        }

        // ⚠ <b>Both halves, which is what separates `:placeholder-shown` from the `empty` class
        // beside it.</b> Selectors 4 § 10.4 matches a field that is *currently displaying*
        // placeholder text, so a field with no value and nothing to show in its place is not one —
        // and a variant compiled against the class alone would have matched every empty field in the
        // document, including the ones with no placeholder at all.
        var shown = empty && !string.IsNullOrEmpty(Placeholder);

        State = shown ? State | ElementState.PlaceholderShown : State & ~ElementState.PlaceholderShown;
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

        // ⚠ The DISPLAY caret, because this measures the string `text` is holding — which,
        // while an input method is composing, is the value with the pre-edit spliced into it. Read
        // from `CaretIndex` the field scrolls to where the caret would be if the pre-edit were not
        // there, so a long composition runs off the right-hand edge as it is typed.
        var caret = line.CaretOffset(DisplayCaret, CaretAffinity);
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

            // ⚠ A field that has lost the focus is no longer the one the input method is talking to,
            // and the platform will not send it the end of the composition — it sends that to
            // whatever took the focus. Left alone, the pre-edit stays drawn in a field nobody is
            // typing into.
            CancelComposition();
        }
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                Document.Focus(this);
                var pressed = PositionAt(args.X, args.Y);
                MoveCaret(pressed.Index, pressed.Affinity, args.Modifiers.HasFlag(ModifierKeys.Shift));

                // Captured, so that a selection drag that leaves the field keeps extending. Without
                // it the drag stops at the border and the user has to make the selection twice.
                dragging = true;
                Document.CapturePointer(this);

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                var dragged = PositionAt(args.X, args.Y);
                MoveCaret(dragged.Index, dragged.Affinity, true);
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
    /// <remarks>
    ///     ⚠ <b>The affinity comes back with it, and is the reason this returns a pair.</b> A click
    ///     at the start of a wrapped row lands on an index that also ends the row above; a click at
    ///     a direction change lands on one that is also at the far end of the run. Keeping only the
    ///     index throws away which of the two the user pointed at, and the caret is then drawn
    ///     somewhere they did not click.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The row is picked first and the alignment is taken off afterwards</b>, and the order
    ///     is not arbitrary: <see cref="ShiftOf" /> is a property of the row, so it cannot be
    ///     subtracted before the y has said which row this is. Shifting the x does not change which
    ///     row a y lands on, so the block is asked twice about the same point and answers the second
    ///     time with the whole of its wrap-boundary reasoning intact — which is why this is not
    ///     re-implemented here.
    /// </remarks>
    (int Index, CaretAffinity Affinity) PositionAt(float x, float y) {
        if (text.Block() is not { } block) {
            return (0, CaretAffinity.Upstream);
        }

        var local = (X: x - text.AbsoluteLeft, Y: y - text.AbsoluteTop);
        var line = 0;

        while (line + 1 < block.Lines.Length && local.Y >= block.TopOf(line + 1)) {
            line++;
        }

        return block.CaretPositionAt(local.X - ShiftOf(block.Lines[line]), local.Y);
    }

    int IndexAt(float x, float y) => PositionAt(x, y).Index;

    /// <summary>How far <c>text-align</c> and <c>direction</c> push one line of the block sideways.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <c>TextLayout</c> places every line from zero, and the alignment is applied by
    ///         whoever draws it.</b> The block has no idea how wide the box around it is, so
    ///         <c>CaretOffset</c>, <c>VisualRanges</c> and <c>CaretPositionAt</c> all speak in
    ///         line-local coordinates — while <c>DrawListBuilder</c> puts the glyphs at
    ///         <c>left + UiDocument.TextAlignShift(...)</c>. Everything this control draws over the
    ///         text has to add the same number back or it lands where the text is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per line and not per block</b>, which is the whole reason it is a method: in a
    ///         wrapped RTL area the long lines fill the box and have no shift at all, while a short
    ///         one is pushed most of the width across. A single block-wide number would be right for
    ///         some rows and wrong for the rest — and the rows it is wrong for are exactly the ragged
    ///         ones a reader looks at.
    ///     </para>
    /// </remarks>
    float ShiftOf(TextLine line) =>
        Document.TextAlignShift(text, Document.ContentWidthOf(text) - line.Width - line.Offset);

    /// <summary>Where the line holding an index begins.</summary>
    /// <remarks>
    ///     ⚠ <b>The row is the one the caret's own affinity says it is on</b>, for the reason
    ///     <see cref="Vertically" /> gives: at a soft wrap one index names two rows, and reading it
    ///     from the number alone would send Home to the head of the row above the one the caret is
    ///     visibly sitting on.
    /// </remarks>
    int LineStart(int index) =>
        text.Block() is { } block ? block.Lines[block.LineOf(index, CaretAffinity)].Start : 0;

    /// <summary>And where it ends, before the break that ended it.</summary>
    int LineEnd(int index) {
        if (text.Block() is not { } block) {
            return Value?.Length ?? 0;
        }

        var line = block.Lines[block.LineOf(index, CaretAffinity)];

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
    (int Index, CaretAffinity Affinity) Vertically(int delta) {
        if (text.Block() is not { } block) {
            return (CaretIndex, CaretAffinity);
        }

        // ⚠ **The row the caret is leaving is the one its own affinity says it is on**, not the one
        // the index alone would name. A caret that walked right off the end of a wrapped line and a
        // caret that arrived at the same index from below are on different rows, so reading the row
        // from the index would send Up and Down from the same number to different places — and one
        // of them to the row it is already on.
        var line = block.LineOf(CaretIndex, CaretAffinity);
        var wanted = line + delta;

        if (wanted < 0) {
            return (0, CaretAffinity.Upstream);
        }

        if (wanted >= block.Lines.Length) {
            return (Value?.Length ?? 0, CaretAffinity.Upstream);
        }

        // ⚠ And the caret lands on the row it was sent to, whatever the index turns out to be. The
        // first index of a continuation row also ends the row above, so taking the landing line's
        // own reading would answer `Upstream` and draw the caret back on the row Down came from —
        // a Down key that visibly does nothing.
        // ⚠ And the column is a VISUAL one, so the row it is leaving and the row it is arriving at
        // are each converted through their own alignment. In a ragged right-aligned or RTL block the
        // two rows start at different x, and an offset carried across unchanged puts the caret the
        // difference between them away from where it looked like it was.
        var column = block.Lines[line].CaretOffset(CaretIndex, CaretAffinity)
            + ShiftOf(block.Lines[line])
            - ShiftOf(block.Lines[wanted]);

        var landed = block.Lines[wanted].CaretPositionAt(column);

        return block.LineOf(landed.Index, landed.Affinity) == wanted
            ? landed
            : (landed.Index, CaretAffinity.Downstream);
    }

    void Typed(TextInputEvent args) {
        // ⚠ <b>The commit ends the composition, and it has to be cleared BEFORE the value moves.</b>
        // A platform delivers a committed composition as ordinary typed text, so this event is both
        // "here is what you typed" and "the pre-edit is over" — and `Replace` raises the change,
        // which re-reads what to display. Clearing afterwards leaves one frame showing the pre-edit
        // twice: once committed into the value and once still spliced in beside it.
        var wasComposing = IsComposing;

        composition = string.Empty;
        compositionCaret = 0;

        if (string.IsNullOrEmpty(args.Text)) {
            if (wasComposing) {
                Display();
            }

            return;
        }

        Replace(args.Text);
        args.Handled = true;
    }

    /// <summary>An input method's pre-edit, which replaces itself and is not yet a value.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An empty text is a cancellation rather than nothing to do.</b> Every platform
    ///         ends an abandoned composition by sending one, so returning early on it is how the last
    ///         pre-edit stays on screen for ever — visible, uncommittable, and belonging to an input
    ///         method that has forgotten about it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The selection is deleted when a composition <i>starts</i>.</b> A pre-edit
    ///         replaces what was selected, exactly as typing would.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>!IsComposing</c> half of that guard is insurance and is labelled as
    ///         such.</b> Deleting the selection makes <c>HasSelection</c> false, so a version without
    ///         the guard deletes nothing on the updates that follow and every test here stays green —
    ///         measured, not assumed. What it is there for is a selection made <i>during</i> a
    ///         composition, which a drag can still produce, and which the pre-edit has no business
    ///         swallowing.
    ///     </para>
    /// </remarks>
    void Composing(TextCompositionEvent args) {
        if (ReadOnly || Disabled) {
            return;
        }

        if (!IsComposing && HasSelection) {
            Replace(string.Empty);
        }

        composition = args.Text;
        compositionCaret = Math.Clamp(args.Start, 0, composition.Length);

        Display();
        Reveal();

        args.Handled = true;
    }

    /// <summary>Abandons any pre-edit, for a field that has stopped being the one being typed into.</summary>
    void CancelComposition() {
        if (!IsComposing) {
            return;
        }

        composition = string.Empty;
        compositionCaret = 0;
        Display();
    }

    /// <summary>Writes what the field shows, which is the value with any pre-edit spliced in.</summary>
    void Display() {
        var value = Value ?? string.Empty;

        if (!IsComposing) {
            text.Text = Shown(Value);
            return;
        }

        // ⚠ The pre-edit goes through the same seam as the value. An input method's intermediate
        // reading of a password is the password being typed, and a field that masked what was
        // committed while showing what was being composed would leak exactly the same secret one
        // keystroke earlier.
        var at = Math.Clamp(CaretIndex, 0, value.Length);
        text.Text = Shown(string.Concat(value.AsSpan(0, at), composition, value.AsSpan(at)));
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        // ⚠ **The chord is decided once, in one table, for both text controls.** This was a
        // `switch (args.Key)` with `var word = Control || Meta` and a comment saying the assembly
        // could not know which platform it was on — while `CodeEditor` had a second copy of the same
        // switch that took Control *only*, so ⌘← moved by a word here and by one character there.
        // Neither could ever grow the AppKit emacs bindings, because ⌃A cannot be Select All and the
        // start of the line in the same table. See `EditingCommands`.
        var shift = args.Modifiers.HasFlag(ModifierKeys.Shift);
        var command = EditingCommands.Resolve(args.Key, args.Modifiers, Document.EditingKeymap);

        switch (command) {
            case EditingCommand.MoveLeft:
                var back = Step(CaretIndex, -1);
                MoveCaret(back.Index, back.Affinity, shift);
                break;

            case EditingCommand.MoveWordLeft:
                var wordBack = WordBefore(CaretIndex);
                MoveCaret(wordBack.Index, wordBack.Affinity, shift);
                break;

            case EditingCommand.MoveRight:
                var forward = Step(CaretIndex, 1);
                MoveCaret(forward.Index, forward.Affinity, shift);
                break;

            case EditingCommand.MoveWordRight:
                var wordForward = WordAfter(CaretIndex);
                MoveCaret(wordForward.Index, wordForward.Affinity, shift);
                break;

            // ⚠ To the end of the *line* in a field that has more than one, and to the end of the
            // value in one that does not. Both are what Home and End mean where they came from, and
            // a text area whose Home jumped to the top of the document would be the odd one out.
            // ⚠ And each of them says which *end* it meant, because on a wrapped row the two ends
            // are the same two indices. Home downstream is the head of the row the caret is on;
            // upstream would be the tail of the row above, so Home would appear to jump up a line.
            // End upstream is that row's own tail rather than the head of the next.
            case EditingCommand.MoveLineStart:
                MoveCaret(AcceptsNewlines ? LineStart(CaretIndex) : 0, CaretAffinity.Downstream, shift);
                break;

            case EditingCommand.MoveLineEnd:
                MoveCaret(AcceptsNewlines ? LineEnd(CaretIndex) : Value?.Length ?? 0, CaretAffinity.Upstream, shift);
                break;

            case EditingCommand.MoveDocumentStart:
                MoveCaret(0, CaretAffinity.Downstream, shift);
                break;

            case EditingCommand.MoveDocumentEnd:
                MoveCaret(Value?.Length ?? 0, CaretAffinity.Upstream, shift);
                break;

            case EditingCommand.MoveUp when AcceptsNewlines:
                var movedUp = Vertically(-1);
                MoveCaret(movedUp.Index, movedUp.Affinity, shift);
                break;

            case EditingCommand.MoveDown when AcceptsNewlines:
                var movedDown = Vertically(1);
                MoveCaret(movedDown.Index, movedDown.Affinity, shift);
                break;

            case EditingCommand.DeleteBackward:
                Backspace();
                break;

            case EditingCommand.DeleteForward:
                Forward();
                break;

            // ⚠ Written as *select, then replace with nothing*, which is what makes the whole family
            // one mutation. `Replace` is where `MaxLength`, the change notification and the caret
            // arithmetic live, so a delete that reached round it would be the second place any of
            // the three could be wrong.
            case EditingCommand.DeleteWordBackward:
                DeleteTo(WordBefore(CaretIndex).Index);
                break;

            case EditingCommand.DeleteWordForward:
                DeleteTo(WordAfter(CaretIndex).Index);
                break;

            case EditingCommand.DeleteToLineStart:
                DeleteTo(AcceptsNewlines ? LineStart(CaretIndex) : 0);
                break;

            case EditingCommand.DeleteToLineEnd:
                DeleteTo(AcceptsNewlines ? LineEnd(CaretIndex) : Value?.Length ?? 0);
                break;

            case EditingCommand.SelectAll:
                SelectAll();
                break;

            // ⚠ Unhandled when there is no manager or nothing to take back, so ⌘Z climbs to the
            // application's own `edit.undo`. A field that consumed it regardless would make the
            // editor's Undo stop working for as long as any text box had the focus.
            case EditingCommand.Undo:
                if (!Undo()) {
                    return;
                }

                break;

            case EditingCommand.Redo:
                if (!Redo()) {
                    return;
                }

                break;

            // ⚠ These return rather than break when there is nothing to do, so that an unhandled
            // ⌘C climbs to whatever else was listening — a list that wanted to copy its selection,
            // a document that wanted to copy the whole thing. Marking the chord handled on a field
            // with no selection is how a text box silently eats the application's Copy.
            case EditingCommand.Copy:
                if (!Copy()) {
                    return;
                }

                break;

            case EditingCommand.Cut:
                if (!Cut()) {
                    return;
                }

                break;

            case EditingCommand.Paste:
                if (!Paste()) {
                    return;
                }

                break;

            // ⚠ Two consumers want Enter in a text area and only one of them can have it, so this is
            // where the collision is settled and written down.
            //
            //   * The *field* wants a line break. That is the whole of what `AcceptsNewlines` is
            //     for, and a field that will not take a second line is a bug with nothing on screen
            //     to explain it.
            //   * The *form around it* wants its default action. A dialog's accept button is not
            //     focused while a field is, so Enter never reaches it as an activation — the route
            //     is `Submitted`, which is what `DialogService.Prompt` binds. A `TextBox` gives it
            //     the plain key; a text area cannot.
            //
            // So the plain chord breaks the line and the modified one submits — Ctrl-Enter on
            // Windows, ⌘-Enter on a Mac, which is `EditingCommand.Submit` in either table. A
            // single-line field has no line to break and submits on both.
            //
            // ⚠ `CodeEditor` deliberately does not join this, and it is not an oversight: Ctrl-Enter
            // there inserts a newline like any other Enter, because nothing in this tree puts a code
            // editor inside a form and the second consumer therefore does not exist for it. The day
            // one does, it raises `SubmitEvent` on the chord and this comment is why.
            case EditingCommand.InsertNewline when AcceptsNewlines:
                Replace("\n");
                break;

            // ⚠ Both, and in this order. `OnSubmit` is what reformats a half-typed number, so a
            // listener that reads the value back — which is what `bind:Value.submit` does — has to
            // hear about it afterwards or it takes `007` rather than the `7` the field settled on.
            // The routed event is raised last because it is the one an ancestor can see, and an
            // ancestor seeing the submission before the field's own handler has is a form whose
            // default button fires on a value the field has not finished with.
            case EditingCommand.InsertNewline or EditingCommand.Submit:
                OnSubmit();
                Submitted?.Invoke(this);
                Raise(new SubmitEvent());
                break;

            default:
                // Everything else, including every key that produces a character, and every verb
                // this control has no reading of — Tab, which is focus navigation; Escape, which a
                // dialog wants; the completion chords, which are the code editor's. Those must not
                // be handled here, or the field would consume every shortcut an ancestor was
                // listening for.
                return;
        }

        args.Handled = true;
    }

    /// <summary>Selects from the caret to an index and deletes what that covers.</summary>
    /// <param name="index">The other end.</param>
    /// <remarks>
    ///     ⚠ <b>Leaves the selection alone and does nothing when there is one.</b> Every desktop's
    ///     delete-by-word deletes the <i>selection</i> when there is one rather than the word beyond
    ///     it, and a field that reached past a highlighted range would delete text the user could
    ///     see was not selected.
    /// </remarks>
    void DeleteTo(int index) {
        if (!HasSelection) {
            if (index == CaretIndex) {
                return;
            }

            SelectionAnchor = index;
        }

        Replace(string.Empty);
    }

    void Backspace() {
        if (!HasSelection) {
            var previous = Step(CaretIndex, -1).Index;
            if (previous == CaretIndex) {
                return;
            }

            SelectionAnchor = previous;
        }

        Replace(string.Empty);
    }

    void Forward() {
        if (!HasSelection) {
            var next = Step(CaretIndex, 1).Index;
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
    (int Index, CaretAffinity Affinity) Step(int index, int direction) {
        if (Value is not { Length: > 0 } value) {
            return (0, Landing(0, direction));
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

            var back = index <= 0 ? 0 : best;

            return (back, Landing(back, direction));
        }

        foreach (var boundary in boundaries) {
            if (boundary > index) {
                return (boundary, Landing(boundary, direction));
            }
        }

        return (value.Length, Landing(value.Length, direction));
    }

    /// <summary>Which side of the index it landed on a horizontal step leaves the caret.</summary>
    /// <param name="index">Where the step landed.</param>
    /// <param name="direction">Which way it went, in logical order.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The caret ends up beside the character the step just crossed</b>, which is the one
    ///         rule the three cases below are all consequences of. A backward step crossed the
    ///         character <i>after</i> where it landed, so the caret leads it —
    ///         <see cref="CaretAffinity.Downstream" />. A forward step crossed the character
    ///         <i>before</i>, so the caret trails it — <see cref="CaretAffinity.Upstream" />. Inside
    ///         a run and away from a wrap the two are the same pixel and nothing can tell them
    ///         apart; where the direction changes they are a whole run apart.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A soft wrap is the one place the rule is overruled, and it is overruled on
    ///         purpose.</b> A break that consumed nothing leaves one index ending the row above and
    ///         beginning the row below, and the character a forward step crossed is the last one on
    ///         the row it is leaving — so the rule above would keep the caret up there, and the next
    ///         Right would appear to move it two characters at once from a row it was never seen on.
    ///         Rows are what a reader sees; a run boundary they cannot see is what the rule is for.
    ///         So the row wins, and the test for "is this a row boundary" is the only honest one
    ///         there is: the index answers with two different rows.
    ///     </para>
    ///     <para>
    ///         ⚠ Backward needs no such exception. A backward step onto the same boundary crossed a
    ///         character on the row <i>below</i>, and <see cref="CaretAffinity.Downstream" /> is
    ///         already the reading that puts the caret there.
    ///     </para>
    /// </remarks>
    CaretAffinity Landing(int index, int direction) {
        if (direction < 0) {
            return CaretAffinity.Downstream;
        }

        return text.Block() is { } block
            && block.LineOf(index, CaretAffinity.Downstream) != block.LineOf(index, CaretAffinity.Upstream)
                ? CaretAffinity.Downstream
                : CaretAffinity.Upstream;
    }

    /// <summary>The start of the word before an index.</summary>
    (int Index, CaretAffinity Affinity) WordBefore(int index) {
        if (Value is not { Length: > 0 } value || index <= 0) {
            return (0, CaretAffinity.Downstream);
        }

        var boundaries = new List<int>();
        WordBreaker.Collect(value, boundaries);

        var best = 0;
        foreach (var boundary in boundaries) {
            if (boundary < index) {
                best = boundary;
            }
        }

        return (best, Landing(best, -1));
    }

    /// <summary>The start of the word after an index.</summary>
    (int Index, CaretAffinity Affinity) WordAfter(int index) {
        if (Value is not { Length: > 0 } value) {
            return (0, Landing(0, 1));
        }

        var boundaries = new List<int>();
        WordBreaker.Collect(value, boundaries);

        foreach (var boundary in boundaries) {
            if (boundary > index) {
                return (boundary, Landing(boundary, 1));
            }
        }

        return (value.Length, Landing(value.Length, 1));
    }
}
