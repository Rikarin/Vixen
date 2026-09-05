// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One coloured run of a line, as an element.</summary>
public sealed class CodeSpan : UiElement {
    /// <inheritdoc />
    protected override string TagName => "code-token";

    /// <summary>What it is, which is the class it wears.</summary>
    public CodeTokenKind Kind { get; private set; } = CodeTokenKind.Plain;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // ⚠ The starting class, for the reason `Control` adds its variant class on creation: a span
        // born plain never has a change to react to, so every uncoloured run would be unstyled.
        AddClass(ClassOf(CodeTokenKind.Plain));
    }

    internal void Recolour(CodeTokenKind kind) {
        if (kind == Kind) {
            return;
        }

        RemoveClass(ClassOf(Kind));
        AddClass(ClassOf(kind));

        Kind = kind;
    }

    /// <summary>The class a kind is written through to, so the theme decides the colour.</summary>
    internal static string ClassOf(CodeTokenKind kind) =>
        kind switch {
            CodeTokenKind.Keyword => "tok-keyword",
            CodeTokenKind.Type => "tok-type",
            CodeTokenKind.Number => "tok-number",
            CodeTokenKind.String => "tok-string",
            CodeTokenKind.Comment => "tok-comment",
            CodeTokenKind.Operator => "tok-operator",
            CodeTokenKind.Directive => "tok-directive",
            _ => "tok-plain"
        };
}

/// <summary>One realised line.</summary>
/// <remarks>
///     ⚠ <b>The spans are a pool inside the line, and the lines are a pool inside the editor.</b>
///     Two levels, because both counts vary: scrolling changes which lines exist and typing changes
///     how many runs a line has. Neither pool ever shrinks — see <c>TreeView</c> for why.
/// </remarks>
public sealed class CodeLine : UiElement {
    readonly List<CodeSpan> spans = [];

    /// <inheritdoc />
    protected override string TagName => "code-line";

    /// <summary>Which line of the buffer it is showing, or -1 if it is parked.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>The runs, including the parked ones.</summary>
    public IReadOnlyList<CodeSpan> Spans => spans;

    internal void Bind(string text, List<CodeToken> tokens) => Bind(text, tokens, 0, text.Length);

    /// <summary>Shows the characters between two columns, coloured by the tokens that cover them.</summary>
    /// <remarks>
    ///     ⚠ <b>The tokens are the whole line's and the range is one wrapped row's.</b> A tokenizer
    ///     carries state along a line — a string, a block comment — so the caller cannot hand over
    ///     only the tokens of the slice without re-tokenizing from the middle of one, which colours
    ///     the second row of every wrapped line as though the file began there. Clipping here costs
    ///     one comparison per token and keeps that state intact.
    /// </remarks>
    internal void Bind(string text, List<CodeToken> tokens, int from, int to) {
        while (spans.Count < tokens.Count) {
            spans.Add(Add<CodeSpan>());
        }

        var next = 0;

        foreach (var token in tokens) {
            var start = Math.Max(token.Start, from);
            var end = Math.Min(token.Start + token.Length, to);

            // ⚠ A token of no length is still a span, because it was one before wrapping existed and
            // the spans are addressed by position in tests. Only a token the slice misses entirely
            // is dropped.
            if (token.Length > 0 && end <= start) {
                continue;
            }

            var span = spans[next++];

            span.RemoveClass("parked");
            span.Recolour(token.Kind);
            span.Text = text[start..Math.Max(start, end)];
        }

        for (var i = next; i < spans.Count; i++) {
            spans[i].AddClass("parked");
            spans[i].Text = null;
        }
    }
}

/// <summary>One line's worth of gutter: its number, its fold arrow and its diagnostic marker.</summary>
public sealed class CodeGutterRow : UiElement {
    /// <inheritdoc />
    protected override string TagName => "code-gutter-row";

    /// <summary>Which line, or -1 if parked.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>The arrow, hidden on a line that starts no fold.</summary>
    public Icon Fold { get; private set; } = null!;

    /// <summary>The line's number.</summary>
    public UiElement Number { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Fold = Add<Icon>();
        Number = Add("code-gutter-number");
    }
}

/// <summary>What a <see cref="CodeOverlay" /> is for.</summary>
enum OverlayKind : byte {
    Selection,
    Caret
}

/// <summary>The selection behind the text, or the caret in front of it.</summary>
/// <remarks>
///     Two elements rather than one, and they are on either side of the lines in document order,
///     because that is the only way to get the painting order right: <see cref="UiElement.OnDraw" />
///     runs before an element's children, so anything the editor drew itself would be under every
///     line — which is correct for a selection and wrong for a caret.
/// </remarks>
sealed class CodeOverlay : UiElement {
    /// <inheritdoc />
    protected override string TagName => "code-selection";

    public OverlayKind Kind { get; set; }

    public CodeEditor? Editor { get; set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Editor is not { } editor) {
            return;
        }

        if (Kind == OverlayKind.Caret) {
            editor.DrawCaret(context);
        } else {
            editor.DrawSelection(context);
        }
    }
}

/// <summary>A text editor for source: coloured, numbered, foldable and virtualised.</summary>
/// <remarks>
///     <para>
///         <b>Monospace by construction.</b> A column is turned into an x by multiplying, which is
///         what makes hit testing, the caret, the selection and the scroll width arithmetic rather
///         than a per-line measurement — and it is what every code editor has ever done. ⚠ Given a
///         proportional font the caret lands in the wrong place, and the theme therefore names one:
///         a game that overrides <c>code-editor</c>'s <c>font-family</c> with a proportional face
///         has broken the caret, not the colours.
///     </para>
///     <para>
///         <b>Virtualised on lines, like <c>TreeView</c> is on rows.</b> A fifty-thousand-line shader
///         is fifty thousand strings and about forty <see cref="CodeLine" />s. Folding is expressed
///         in the same place: a collapsed region is lines missing from the row list, so nothing below
///         has to know that folding exists.
///     </para>
///     <para>
///         ⚠ <b>Highlighting state is cached per line and invalidated from the edit downwards.</b>
///         A block comment opened on line 3 changes what line 4 000 is, so the state has to be
///         carried forward — and recomputing the whole file on every keystroke is what makes a
///         highlighter feel slow. Editing line <i>n</i> throws away the states from <i>n</i> on and
///         nothing above it.
///     </para>
///     <para>
///         ⚠ <b>No undo.</b> See <see cref="CodeBuffer" /> — an undo stack inside a text control can
///         only undo typing, and every application that has one wants it to cover more than that.
///         <see cref="CodeBuffer.Changed" /> is the seam.
///     </para>
/// </remarks>
public sealed partial class CodeEditor : Control {
    readonly List<CodeLine> pool = [];
    readonly List<CodeGutterRow> gutterRows = [];
    readonly List<UiElement> completionRows = [];
    readonly List<CompletionItem> completions = [];
    readonly List<CodeToken> scratch = [];

    /// <summary>A second one, because carrying the state forward tokenizes lines nobody draws.</summary>
    readonly List<CodeToken> stateScratch = [];

    readonly List<CodeDiagnostic> diagnostics = [];
    readonly List<CodeFold> folds = [];
    readonly HashSet<int> collapsed = [];

    /// <summary>Which buffer line each visible row shows.</summary>
    /// <remarks>
    ///     ⚠ <b>A line appears once per <i>visual</i> row, so with <see cref="WordWrap" /> on the
    ///     same number appears several times in a run.</b> That is what keeps wrapping out of the
    ///     virtualiser, the scroll range and the gutter — all three count rows and none of them has
    ///     to know a line can be more than one.
    /// </remarks>
    readonly List<int> rows = [];

    /// <summary>Which column of its line each visible row starts at.</summary>
    /// <remarks>
    ///     Zero everywhere while <see cref="WordWrap" /> is off, which is what makes every formula
    ///     below reduce to the one it had before wrapping existed.
    /// </remarks>
    readonly List<int> starts = [];

    /// <summary>The tokenizer's state at the start of each line.</summary>
    readonly List<int> states = [];

    // When the caret last moved, on the document's clock, and where it was on the last frame that
    // drew it. ⚠ Noticed at draw time rather than stamped from `Caret`'s setter, because a caret is
    // clamped from `SetBuffer` and from the reload path, where the element may not have a document
    // to read a clock off — and `DrawCaret` cannot be reached on an element that does not.
    TimeSpan caretRestarted;
    TextPosition? caretDrawn;

    CodeBuffer buffer = new();
    ICodeTokenizer tokenizer = PlainTokenizer.Instance;

    int statesValid;
    int first;
    int longest;
    int completionIndex;
    bool selecting;

    ComputedStyle? measured;
    float measuredFontSize = float.NaN;
    float measuredLineHeight = float.NaN;
    float characterWidth = 8f;
    float lineHeight = 16f;

    int selectionColor;
    int caretColor;
    int caretColorStandard;
    int currentLineColor;
    bool editing;

    /// <summary>How many lines are realised above and below the viewport.</summary>
    public const int Overscan = 2;

    /// <inheritdoc />
    protected override string TagName => "code-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>textbox</c> and not <c>application</c>, which is the opposite call from the four
    ///     canvases in this assembly.</b> A viewport and a node graph own their keyboard and want a
    ///     screen reader to pass keys straight through; a code editor is a multi-line text field
    ///     whose keyboard is the one a screen-reader user already knows, and announcing it as an
    ///     application would turn off exactly the reading and review commands that make text
    ///     editable at all.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.TextBox;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>null</c>, on <c>TextField</c>'s terms.</b> An editor has no words of its own —
    ///     what it is an editor *of* is the file, which is the application's sentence and usually
    ///     the panel title above it. One <see cref="AccessibleRelation.LabelledBy" /> at the call
    ///     site, and an editor nobody named reports nothing so that a gate can fail it.
    /// </remarks>
    protected override string? NativeAccessibleName => null;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Not the buffer's text.</b> A value is what a bridge announces, and announcing a
    ///     twelve-thousand-line file every time the caret moves is worse than announcing nothing —
    ///     it is also a string built per read. A real editor bridge reads a line at a time, which is
    ///     what the platform text APIs are for and is out of this tree's scope.
    /// </remarks>
    protected override string? NativeAccessibleValue => null;

    /// <inheritdoc />
    /// <remarks><see cref="AccessibleStates.MultiLine" /> always: that is what a code editor is.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Editable
        | AccessibleStates.MultiLine
        | (ReadOnly ? AccessibleStates.ReadOnly : AccessibleStates.None);

    /// <summary>The text being edited.</summary>
    /// <remarks>
    ///     Settable, because an editor pane shows one file and then another. Assigning resets the
    ///     caret and every cached highlighting state, since neither means anything in a new file.
    /// </remarks>
    public CodeBuffer Buffer {
        get => buffer;
        set {
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(buffer, value)) {
                return;
            }

            buffer.Changed -= OnBufferChanged;
            buffer = value;
            buffer.Changed += OnBufferChanged;

            Caret = default;
            Anchor = default;

            statesValid = 0;
            Refresh();
        }
    }

    /// <summary>What turns a line into colours.</summary>
    public ICodeTokenizer Tokenizer {
        get => tokenizer;
        set {
            ArgumentNullException.ThrowIfNull(value);

            tokenizer = value;
            statesValid = 0;

            Refresh();
        }
    }

    /// <summary>The whole text, for the caller who does not want the buffer.</summary>
    /// <remarks>
    ///     ⚠ Named for the file rather than called <c>Text</c>, because <see cref="UiElement.Text" />
    ///     is the string an element <i>draws itself</i> — and an element that draws text is a leaf
    ///     the layout tree refuses to give children. A code editor is nothing but children.
    /// </remarks>
    public string Source {
        get => buffer.Text;
        set => buffer.Text = value ?? string.Empty;
    }

    /// <summary>The column of numbers and markers down the left.</summary>
    public UiElement Gutter { get; private set; } = null!;

    /// <summary>The scroller the lines live in.</summary>
    public ScrollView Scroller { get; private set; } = null!;

    /// <summary>Where the realised lines go.</summary>
    public UiElement Lines { get; private set; } = null!;

    /// <summary>The autocomplete popup.</summary>
    public UiElement Completion { get; private set; } = null!;

    /// <summary>The lines that exist as elements, including the parked ones.</summary>
    public IReadOnlyList<CodeLine> Pool => pool;

    /// <summary>Which buffer line each visible row shows, folding taken out.</summary>
    /// <remarks>
    ///     ⚠ <b>Not one entry per line once <see cref="WordWrap" /> is on.</b> A line that wraps
    ///     three ways is three consecutive entries with the same number, and
    ///     <see cref="RowStarts" /> is which column each of them begins at.
    /// </remarks>
    public IReadOnlyList<int> Rows => rows;

    /// <summary>Which column of its line each visible row begins at.</summary>
    /// <remarks>All zero while <see cref="WordWrap" /> is off. Always the same length as <see cref="Rows" />.</remarks>
    public IReadOnlyList<int> RowStarts => starts;

    /// <summary>Where the caret is.</summary>
    public TextPosition Caret { get; private set; }

    /// <summary>The other end of the selection. Equal to the caret when nothing is selected.</summary>
    public TextPosition Anchor { get; private set; }

    /// <summary>How long the caret spends on, and then off. Zero draws it solid.</summary>
    /// <remarks>
    ///     <para>
    ///         The same property <c>TextField.CaretBlink</c> is, deliberately spelled and defaulted
    ///         the same way: half a period, 530 ms, and the phase measured from the last time the
    ///         caret moved rather than from a free-running clock — so holding a key down does not
    ///         make the caret flicker where the character is landing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not shared with the field through a base class, and that is the honest shape.</b>
    ///         The two carets have nothing in common below the arithmetic: a field's is an index into
    ///         a string positioned by <c>TextLayout</c>, and this one is a line and a column
    ///         multiplied by a measured cell. What they share is a number and a rule about it, which
    ///         is four lines each — and a base class holding four lines would have to be reached by
    ///         <c>TextField</c>, which is in the assembly below this one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="TimeSpan.Zero" /> is a solid caret</b>, which is what a reduced-motion
    ///         setting wants and what a screenshot test wants.
    ///     </para>
    /// </remarks>
    public TimeSpan CaretBlink { get; set; } = TimeSpan.FromMilliseconds(530);

    /// <summary>Whether the caret is drawn on this frame.</summary>
    bool CaretIsLit {
        get {
            if (CaretBlink <= TimeSpan.Zero) {
                return true;
            }

            var since = Document.Now - caretRestarted;

            return since < TimeSpan.Zero || since.Ticks / CaretBlink.Ticks % 2 == 0;
        }
    }

    /// <summary>Whether anything is selected.</summary>
    public bool HasSelection => Caret != Anchor;

    /// <summary>The selected text, or an empty string.</summary>
    public string SelectedText => HasSelection ? buffer.Slice(Anchor, Caret) : string.Empty;

    /// <summary>What is being said about the file.</summary>
    public IReadOnlyList<CodeDiagnostic> Diagnostics => diagnostics;

    /// <summary>The regions that can be collapsed.</summary>
    public IReadOnlyList<CodeFold> Folds => folds;

    /// <summary>What the popup is offering, filtered.</summary>
    public IReadOnlyList<CompletionItem> Completions => completions;

    /// <summary>Which of them is highlighted.</summary>
    public int CompletionIndex => completionIndex;

    /// <summary>Whether the popup is up.</summary>
    public bool IsCompleting { get; private set; }

    /// <summary>How many spaces Tab inserts.</summary>
    [UiProperty(Default = 4)]
    public partial int TabSize { get; set; }

    /// <summary>Whether Enter copies the previous line's indent.</summary>
    [UiProperty(Default = true)]
    public partial bool AutoIndent { get; set; }

    /// <summary>Whether foldable regions are worked out from the indentation.</summary>
    [UiProperty(Default = true, Changed = nameof(OnFoldingChanged))]
    public partial bool AutoFold { get; set; }

    /// <summary>Whether the text may be changed.</summary>
    [UiProperty]
    public partial bool ReadOnly { get; set; }

    /// <summary>Whether a line too long for the viewport is broken across several rows.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Off, and it has to stay off by default.</b> Code is written with the column
    ///         mattering — a diff, a compiler's column number, an editorconfig ruler — and an editor
    ///         that rewrapped it on open would be arguing with all three. It is a per-view setting
    ///         everywhere it exists, which is what this is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The break is arithmetic and not a text layout, because this control is a
    ///         monospace grid.</b> Every column is <see cref="CharacterWidth" /> wide by
    ///         construction — that is what makes the caret, the selection, the hit test and the
    ///         gutter one multiplication each — so the wrap column is
    ///         <c>viewport ÷ CharacterWidth</c> exactly, with no measurement and nothing to disagree
    ///         with. Handing the line to <c>TextLayout</c> instead would measure a width the rest of
    ///         this file does not believe in, and the caret would land next to the character it is
    ///         supposed to be on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The row list is the seam, and folding already proved it works.</b> A collapsed
    ///         fold is lines missing from <see cref="Rows" />; a wrapped line is a line appearing in
    ///         it more than once. The virtualiser, the scroll range, the caret's row and the gutter
    ///         all count rows, so neither feature is a special case in any of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A width the layout has not measured yet gives no wrap for one pass.</b> The
    ///         first <see cref="Refresh" /> of a new editor runs before its scroller has a size, and
    ///         a wrap column derived from nought would be one character wide. It asks the document
    ///         for a pass in that case, and <c>WhenResized</c> re-runs the whole thing whenever the
    ///         box actually changes — which is also what re-wraps a splitter drag.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnWrapChanged))]
    public partial bool WordWrap { get; set; }

    /// <summary>What to offer when a completion is asked for.</summary>
    /// <remarks>
    ///     A callback rather than a list, because what is offered depends on where the caret is —
    ///     which is the whole point of a completion — and because the answer usually comes from
    ///     something asynchronous that the editor should not be waiting on.
    /// </remarks>
    public Func<CodeEditor, string, IReadOnlyList<CompletionItem>>? CompletionProvider { get; set; }

    /// <summary>Raised after the caret or the selection moves.</summary>
    public event Action<CodeEditor>? CaretMoved;

    /// <summary>Raised after the text changes.</summary>
    public event Action<CodeEditor>? TextChanged;

    /// <summary>Raised when a completion is accepted.</summary>
    public event Action<CodeEditor, CompletionItem>? CompletionAccepted;

    /// <summary>How tall one row of the virtualiser is.</summary>
    /// <remarks>
    ///     ⚠ <b>Named for the row rather than for the line, because
    ///     <see cref="UiElement.LineHeight" /> is the cascade's own and may be
    ///     <see cref="float.NaN" />.</b> This is the resolved number the arithmetic needs: whatever
    ///     <c>line-height</c> said if it said anything, and the font's own line height otherwise.
    ///     The other virtualised controls in this assembly call the same thing <c>RowHeight</c>,
    ///     which is what it is here too.
    /// </remarks>
    public float RowHeight {
        get {
            Measure();
            return lineHeight;
        }
    }

    /// <summary>How wide one character is.</summary>
    public float CharacterWidth {
        get {
            Measure();
            return characterWidth;
        }
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        selectionColor = Document.PropertyId("--selection-color");
        caretColor = Document.PropertyId("--caret-color");

        // CSS's spelling beside Vixen's, asked first. See `TextField.CaretColour`, which states the
        // order and what it costs; a code editor's caret is the same promise a field's is, and
        // `caret-accent` has to mean the same thing written on either.
        caretColorStandard = Document.PropertyId("caret-color");
        currentLineColor = Document.PropertyId("--current-line-color");

        // The other half of `TextField`'s registration, and the reason both are done together: a
        // rule that only one control in the library obeys is a special case rather than a chain. See
        // `TextField.OnCreated` for why Select All is the only editing verb either of them can
        // register today.
        AddCommandHandler("edit.select-all", SelectAll, () => !Disabled && buffer.End != default);

        Gutter = Part("code-gutter");
        Scroller = Part<ScrollView>();

        // ⚠ Three siblings in this order, and the order is the whole design: painting order is
        // document order, so the selection is under the text and the caret is over it. An editor
        // that drew both from one place would have to put both on the same side of the glyphs.
        var selection = Scroller.Content.Add<CodeOverlay>("code-selection");
        selection.Editor = this;

        Lines = Scroller.Content.Add("code-lines");

        var caret = Scroller.Content.Add<CodeOverlay>("code-caret");
        caret.Kind = OverlayKind.Caret;
        caret.Editor = this;

        // Measured rather than declared: the character cell is the advance of a glyph in whatever
        // face the theme picked, shaped through the same cache every line goes through.
        Probe = Part("code-metrics");
        Probe.Text = "0";

        Completion = Part("code-completion");
        Completion.AddClass("hidden");

        Scroller.Scrolled += _ => Realise();
        buffer.Changed += OnBufferChanged;

        // ⚠ Gated on the size, and here that gate is doing real work rather than tidying: Refresh
        // walks every line in the buffer to rebuild the row list, so a hundred-thousand-line file
        // would pay for that on every frame of every pass. See Control.WhenResized.
        WhenResized(Refresh);

        AddHandler<KeyEvent>(static (element, args) => ((CodeEditor) element).Keyed(args));
        AddHandler<TextInputEvent>(static (element, args) => ((CodeEditor) element).Typed(args));
        AddHandler<PointerEvent>(static (element, args) => ((CodeEditor) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((CodeEditor) element).Tapped(args));
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    /// <summary>Reads the character cell out of the cascade, at most once per restyle.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured from a shaped glyph rather than declared.</b> A code editor's whole geometry
    ///     is the advance of one character in the font the theme chose, and a number in a stylesheet
    ///     would be a second place for it to be wrong. The <c>0</c> is measured through the same
    ///     shaping cache every line goes through, so it agrees with the picture by construction.
    ///     <para>
    ///         Keyed on the computed style — which the cascade interns — and on the font size, which
    ///         lives on the element rather than in the style.
    ///     </para>
    /// </remarks>
    void Measure() {
        if (ReferenceEquals(measured, Style)
            && measuredFontSize.Equals(FontSize)
            && measuredLineHeight.Equals(LineHeight)) {
            return;
        }

        measured = Style;
        measuredFontSize = FontSize;
        measuredLineHeight = LineHeight;

        // A line rather than a run, since a character picks its own font: the probe is one digit and
        // will be one run, but asking for the run would be asking the element for something it no
        // longer has — `font-family` is a per-character chain and an element's text can be in
        // several faces at once.
        if (Probe.Block()?.Lines[0] is { } line) {
            characterWidth = line.Width > 0f ? line.Width : characterWidth;
            lineHeight = line.Height > 0f ? line.Height : lineHeight;
        }

        // The cascade's `line-height`, which resolves relative units against the right font size
        // and is inherited in that form. NaN means "whatever the font recommends", which the run
        // above has already answered.
        if (!float.IsNaN(LineHeight) && LineHeight > 0f) {
            lineHeight = LineHeight;
        }
    }

    /// <summary>The hidden <c>0</c> the character cell is measured from.</summary>
    UiElement Probe { get; set; } = null!;

    // ── Refreshing ───────────────────────────────────────────────────────────

    /// <summary>Rebuilds the row list and realises the lines for it.</summary>
    /// <remarks>The one entry point after anything changes: an edit, a fold, a resize, a new file.</remarks>
    public void Refresh() {
        if (AutoFold) {
            RebuildFolds();
        }

        // ⚠ A pass before the row list rather than only after it, and only when it is needed. The
        // first refresh of a new editor runs before anything has been laid out, so the viewport is
        // nought wide and a wrap column taken from it would be one character.
        if (WordWrap && Scroller.Width <= 0f) {
            Document.Update();
        }

        var columns = WrapColumns;

        rows.Clear();
        starts.Clear();
        longest = 0;

        for (var line = 0; line < buffer.LineCount; line++) {
            var text = buffer[line];
            longest = Math.Max(longest, text.Length);

            // The unwrapped case is the wrapped one with a single row starting at column zero, which
            // is why nothing below this method asks which it is.
            var at = 0;

            do {
                rows.Add(line);
                starts.Add(at);
                at = columns > 0 ? BreakAfter(text, at, columns) : text.Length;
            } while (at < text.Length);

            // A collapsed fold is lines that are simply not in the row list. Everything below —
            // virtualisation, the caret's row, the scroll range — then works without knowing that
            // folding is a thing, which is what keeps it from being a special case in six places.
            if (collapsed.Contains(line) && FoldAt(line) is { } fold) {
                line = fold.End;
            }
        }

        Scroller.Content.SetStyle("height", Inline.Px(rows.Count * RowHeight));

        // ⚠ The viewport's own width when wrapping, which is what takes the horizontal scrollbar
        // away. Leaving it at the longest line would keep a bar that scrolls past the right-hand
        // edge of text that now ends there.
        Scroller.Content.SetStyle(
            "width",
            Inline.Px(columns > 0 ? Scroller.Width : (longest + 2) * CharacterWidth)
        );

        // A pass before anything reads a size, for the reason `TreeView.Refresh` gives: the height
        // above is a declaration and `ScrollView.Refresh` needs a measurement.
        Document.Update();

        Scroller.Refresh();
        Realise();
    }

    /// <summary>How many characters fit across the viewport, or nought when nothing is wrapping.</summary>
    int WrapColumns =>
        WordWrap && Scroller.Width > 0f && CharacterWidth > 0f
            ? Math.Max(1, (int) (Scroller.Width / CharacterWidth))
            : 0;

    /// <summary>Where the row starting at <paramref name="at" /> ends, which is where the next begins.</summary>
    /// <remarks>
    ///     ⚠ <b>After the last space that fits, so a word is not cut in half — and the space stays on
    ///     the row it ended.</b> Breaking at the column would split identifiers, which in a code
    ///     editor is the one thing wrapping must not do; keeping the trailing space on the earlier
    ///     row is what stops the next one from starting with a blank cell. A word longer than the
    ///     whole viewport has nowhere to break and is cut at the column, because the alternative is
    ///     a row wider than the box it is in.
    /// </remarks>
    static int BreakAfter(string text, int at, int columns) {
        var limit = at + columns;

        if (limit >= text.Length) {
            return text.Length;
        }

        for (var i = limit; i > at; i--) {
            if (text[i - 1] is ' ' or '\t') {
                return i;
            }
        }

        return limit;
    }

    /// <summary>Which visible row a place in the text is on, or -1 if its line is folded away.</summary>
    /// <remarks>
    ///     ⚠ <b>The <i>last</i> row of the line that starts at or before the column</b>, so a caret
    ///     sitting exactly on a break shows at the start of the row below rather than past the end of
    ///     the one above. Both are defensible and only one of them is where the next character will
    ///     appear.
    /// </remarks>
    int RowAt(TextPosition position) {
        var row = rows.IndexOf(position.Line);

        if (row < 0) {
            return -1;
        }

        while (row + 1 < rows.Count && rows[row + 1] == position.Line && starts[row + 1] <= position.Column) {
            row++;
        }

        return row;
    }

    /// <summary>The column just past the last character a row shows.</summary>
    int EndOf(int row) =>
        row + 1 < rows.Count && rows[row + 1] == rows[row] ? starts[row + 1] : buffer[rows[row]].Length;

    /// <summary>Whether a row is the last one its line occupies, so the swallowed newline is drawn on it.</summary>
    bool IsLastRowOf(int row) => row + 1 >= rows.Count || rows[row + 1] != rows[row];

    /// <summary>The furthest column a caret may sit at and still be drawn on this row.</summary>
    /// <remarks>
    ///     ⚠ <b>One short of the next row's start, and that is not an off-by-one.</b>
    ///     <see cref="RowAt" /> puts a caret sitting exactly on a break at the start of the row
    ///     below, so clamping a click or a Down key to the break itself would move the caret two
    ///     rows for one press. On the last row of a line there is no break and the limit is the
    ///     line's own end, which is what every row is while nothing is wrapping.
    /// </remarks>
    int CaretLimitOf(int row) =>
        IsLastRowOf(row) ? buffer[rows[row]].Length : Math.Max(starts[row], starts[row + 1] - 1);

    void OnWrapChanged(bool previous, bool current) => Refresh();

    void Realise() {
        var height = Scroller.Height;
        var cell = RowHeight;

        if (cell <= 0f) {
            return;
        }

        var capacity = Math.Min(rows.Count, (int) MathF.Ceiling(height / cell) + (Overscan * 2) + 1);
        first = Math.Clamp((int) MathF.Floor(Scroller.ScrollTop / cell) - Overscan, 0, Math.Max(0, rows.Count - capacity));

        while (pool.Count < capacity) {
            pool.Add(Lines.Add<CodeLine>());
        }

        while (gutterRows.Count < capacity) {
            gutterRows.Add(Gutter.Add<CodeGutterRow>());
        }

        for (var i = 0; i < pool.Count; i++) {
            var line = pool[i];
            var row = first + i;

            if (i >= capacity || row >= rows.Count) {
                line.Index = -1;
                line.AddClass("parked");

                continue;
            }

            var index = rows[row];

            line.RemoveClass("parked");
            line.Index = index;

            // ⚠ The whole line is tokenized and only a slice of it is shown. A tokenizer's state
            // runs along the line — a string, a block comment — so tokenizing from the middle of one
            // would recolour the second visual row of every wrapped line as though the file started
            // there.
            scratch.Clear();
            Tokenizer.Tokenize(buffer[index], StateAt(index), scratch);
            line.Bind(buffer[index], scratch, starts[row], EndOf(row));

            line.SetStyle("top", Inline.Px(row * cell));
            line.SetStyle("height", Inline.Px(cell));

            Flag(line, index);
        }

        for (var i = 0; i < gutterRows.Count; i++) {
            var gutterRow = gutterRows[i];
            var row = first + i;

            if (i >= capacity || row >= rows.Count) {
                gutterRow.Index = -1;
                gutterRow.AddClass("parked");

                continue;
            }

            var index = rows[row];

            // ⚠ A wrapped line is numbered once, on the row it starts on. Numbering every visual row
            // would print the same number three times down the margin, and numbering them
            // consecutively would make the gutter disagree with every compiler error in the file.
            var continued = row > 0 && rows[row - 1] == index;

            gutterRow.RemoveClass("parked");
            gutterRow.Index = index;

            if (continued) {
                gutterRow.AddClass("continued");
            } else {
                gutterRow.RemoveClass("continued");
            }

            gutterRow.Number.Text = continued
                ? string.Empty
                : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // ⚠ Positioned against the viewport rather than against the content, because the gutter
            // is outside the scroller: it follows the vertical scroll and must not follow the
            // horizontal one, which is exactly what subtracting only `ScrollTop` gives.
            gutterRow.SetStyle("top", Inline.Px((row * cell) - Scroller.ScrollTop));
            gutterRow.SetStyle("height", Inline.Px(cell));

            // The arrow belongs to the line rather than to the row, so a continuation row does not
            // get a second one that would collapse the region the row is inside.
            if (!continued && FoldAt(index) is not null) {
                gutterRow.RemoveClass("unfoldable");
                gutterRow.Fold.Geometry = collapsed.Contains(index) ? ControlIcons.ChevronRight : ControlIcons.ChevronDown;
            } else {
                gutterRow.AddClass("unfoldable");
            }

            Flag(gutterRow, index);
        }
    }

    /// <summary>Puts the worst diagnostic on a line onto an element as a class.</summary>
    void Flag(UiElement element, int line) {
        var severity = -1;

        foreach (var diagnostic in diagnostics) {
            if (diagnostic.Line == line) {
                severity = Math.Max(severity, (int) diagnostic.Severity);
            }
        }

        element.RemoveClass("has-hint");
        element.RemoveClass("has-warning");
        element.RemoveClass("has-error");

        switch (severity) {
            case (int) CodeSeverity.Hint:
                element.AddClass("has-hint");
                break;

            case (int) CodeSeverity.Warning:
                element.AddClass("has-warning");
                break;

            case (int) CodeSeverity.Error:
                element.AddClass("has-error");
                break;

            default:
                break;
        }
    }

    /// <remarks>
    ///     ⚠ <b>A change this control did not make is treated as a new file.</b> The buffer is public
    ///     and an application may edit it — a formatter, a refactor, a hot reload — and nothing on
    ///     <see cref="CodeBuffer.Changed" /> says which line moved, so every cached state has to go.
    ///     The editor's own edits set <see cref="editing" /> and invalidate precisely, which is the
    ///     case that happens sixty times a second.
    /// </remarks>
    void OnBufferChanged(CodeBuffer changed) {
        TextChanged?.Invoke(this);

        if (editing) {
            return;
        }

        statesValid = 0;

        Caret = buffer.Clamp(Caret);
        Anchor = buffer.Clamp(Anchor);

        Refresh();
    }

    // ── Highlighting state ───────────────────────────────────────────────────

    /// <summary>The tokenizer's state at the start of a line, computing what it has to.</summary>
    int StateAt(int line) {
        while (states.Count < buffer.LineCount) {
            states.Add(0);
        }

        if (statesValid == 0) {
            states[0] = Tokenizer.InitialState;
            statesValid = 1;
        }

        statesValid = Math.Min(statesValid, buffer.LineCount);

        while (statesValid <= line && statesValid < buffer.LineCount) {
            stateScratch.Clear();

            states[statesValid] = Tokenizer.Tokenize(
                buffer[statesValid - 1],
                states[statesValid - 1],
                stateScratch
            );

            statesValid++;
        }

        return line < states.Count ? states[line] : 0;
    }

    /// <summary>Throws away the cached highlighting state from a line downwards.</summary>
    void Invalidate(int line) => statesValid = Math.Min(statesValid, Math.Max(1, line + 1));

    // ── Diagnostics and folding ──────────────────────────────────────────────

    /// <summary>Replaces what is being said about the file.</summary>
    /// <param name="items">The diagnostics.</param>
    public void SetDiagnostics(params ReadOnlySpan<CodeDiagnostic> items) {
        diagnostics.Clear();

        foreach (var item in items) {
            diagnostics.Add(item);
        }

        Realise();
    }

    /// <summary>The fold that starts on a line, if one does.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The fold, or <c>null</c>.</returns>
    public CodeFold? FoldAt(int line) {
        foreach (var fold in folds) {
            if (fold.Start == line) {
                return fold;
            }
        }

        return null;
    }

    /// <summary>Whether a fold is collapsed.</summary>
    /// <param name="line">The line it starts on.</param>
    /// <returns>Whether it is.</returns>
    public bool IsCollapsed(int line) => collapsed.Contains(line);

    /// <summary>Collapses or expands the fold that starts on a line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether there was a fold there.</returns>
    public bool ToggleFold(int line) {
        if (FoldAt(line) is not { } fold) {
            return false;
        }

        if (!collapsed.Remove(fold.Start)) {
            collapsed.Add(fold.Start);

            // ⚠ The caret comes out with the lines. A caret left inside a collapsed region has no
            // row, so every arrow key would move it somewhere that is not on screen and the editor
            // would look frozen.
            if (Caret.Line > fold.Start && Caret.Line <= fold.End) {
                Move(new TextPosition(fold.Start, buffer[fold.Start].Length), false);
            }
        }

        Refresh();
        return true;
    }

    /// <summary>Replaces the foldable regions, turning the automatic ones off.</summary>
    /// <param name="regions">The regions.</param>
    public void SetFolds(params ReadOnlySpan<CodeFold> regions) {
        AutoFold = false;

        folds.Clear();

        foreach (var region in regions) {
            folds.Add(region);
        }

        Refresh();
    }

    void OnFoldingChanged(bool previous, bool current) => Refresh();

    /// <summary>Works the foldable regions out from the indentation.</summary>
    /// <remarks>
    ///     ⚠ <b>Indentation rather than brackets</b>, because the same rule then works for Raven,
    ///     C#, VCSS and YAML — and because a bracket rule on a file that is being typed has one
    ///     unmatched bracket in it almost all of the time, which makes the arrows in the gutter
    ///     flicker as somebody writes.
    /// </remarks>
    void RebuildFolds() {
        folds.Clear();

        for (var line = 0; line < buffer.LineCount; line++) {
            if (buffer.IsBlank(line)) {
                continue;
            }

            var indent = buffer.IndentOf(line);
            var end = line;

            for (var next = line + 1; next < buffer.LineCount; next++) {
                if (buffer.IsBlank(next)) {
                    continue;
                }

                if (buffer.IndentOf(next) <= indent) {
                    break;
                }

                end = next;
            }

            if (end > line) {
                folds.Add(new CodeFold(line, end));
            }
        }

        // A fold whose start no longer folds anything must not stay collapsed, or its lines are
        // hidden by a region that has ceased to exist and there is no arrow left to bring them back.
        collapsed.RemoveWhere(start => FoldAt(start) is null);
    }

    // ── Coordinates ──────────────────────────────────────────────────────────

    /// <summary>Which visible row a buffer line is on, or -1 if it is folded away.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The row.</returns>
    public int RowOf(int line) => rows.IndexOf(line);

    /// <summary>Where a place in the text is, in document space.</summary>
    /// <param name="position">The place.</param>
    /// <returns>The top-left of the character cell.</returns>
    public Vector2 ToScreen(TextPosition position) {
        var row = RowAt(position);
        var content = Scroller.Content;

        return new Vector2(
            content.AbsoluteLeft + ((position.Column - StartOf(row)) * CharacterWidth),
            content.AbsoluteTop + (Math.Max(row, 0) * RowHeight)
        );
    }

    /// <summary>The column a row begins at, or nought for a row that does not exist.</summary>
    int StartOf(int row) => row >= 0 && row < starts.Count ? starts[row] : 0;

    /// <summary>Which character a point is over.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The place, clamped into the text.</returns>
    public TextPosition ToPosition(float x, float y) {
        var content = Scroller.Content;

        var row = Math.Clamp((int) MathF.Floor((y - content.AbsoluteTop) / RowHeight), 0, Math.Max(0, rows.Count - 1));
        var line = rows.Count == 0 ? 0 : rows[row];

        // ⚠ Rounded rather than floored, so a click on the right half of a character puts the caret
        // after it. Flooring makes the last column of every line unreachable by clicking.
        var column = StartOf(row) + (int) MathF.Round((x - content.AbsoluteLeft) / CharacterWidth);

        // ⚠ Clamped to this row's own end and not only to the line's. Clicking past the end of the
        // first of three wrapped rows would otherwise land two rows further on, on a character the
        // pointer was nowhere near.
        if (rows.Count > 0) {
            column = Math.Min(column, CaretLimitOf(row));
        }

        return buffer.Clamp(new TextPosition(line, column));
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    internal void DrawSelection(DrawContext context) {
        var cell = RowHeight;
        var width = CharacterWidth;
        var content = Scroller.Content;

        if (!HasSelection) {
            var only = RowAt(Caret);

            if (only >= 0 && Document.ColorOf(Style, currentLineColor) is { } lit) {
                context.FillRectangle(
                    new Rectangle(
                        content.AbsoluteLeft,
                        content.AbsoluteTop + (only * cell),
                        MathF.Max(content.Width, Scroller.Width),
                        cell
                    ),
                    lit
                );
            }

            return;
        }

        var colour = Document.ColorOf(Style, selectionColor) ?? new Color4(0.72f, 0.80f, 0.97f, 1f);

        var from = Anchor;
        var to = Caret;

        if (from > to) {
            (from, to) = (to, from);
        }

        for (var row = first; row < rows.Count && row < first + pool.Count; row++) {
            var line = rows[row];

            if (line < from.Line || line > to.Line) {
                continue;
            }

            // ⚠ A line inside a multi-line selection is drawn one character wider than its text, so
            // the newline it swallowed is visible. Without it a block selection has ragged holes
            // where the short lines are, and nobody can tell whether the newline is included. ⚠ On
            // the line's *last* row only, or a wrapped line would show a swallowed newline at every
            // break, where there is no newline at all.
            var lower = line == from.Line ? from.Column : 0;
            var upper = line == to.Line ? to.Column : buffer[line].Length + 1;

            var start = Math.Max(lower, starts[row]);
            var end = Math.Min(upper, IsLastRowOf(row) ? buffer[line].Length + 1 : starts[row + 1]);

            context.FillRectangle(
                new Rectangle(
                    content.AbsoluteLeft + ((start - starts[row]) * width),
                    content.AbsoluteTop + (row * cell),
                    MathF.Max(0f, (end - start) * width),
                    cell
                ),
                colour
            );
        }
    }

    internal void DrawCaret(DrawContext context) {
        var row = RowAt(Caret);

        if (!IsFocused || row < 0) {
            // ⚠ Held at the start of a period, so a click into an editor lights the caret on the
            // frame it arrives rather than resuming half-way through an off half.
            caretRestarted = Document.Now;
            caretDrawn = null;
            return;
        }

        // The caret moved since the last frame that drew it, so the blink starts again — noticed
        // here rather than stamped from `Caret`'s setter, for the reason `caretRestarted` gives.
        if (caretDrawn != Caret) {
            caretDrawn = Caret;
            caretRestarted = Document.Now;
        }

        if (!CaretIsLit) {
            return;
        }

        var content = Scroller.Content;

        context.FillRectangle(
            new Rectangle(
                content.AbsoluteLeft + ((Caret.Column - starts[row]) * CharacterWidth),
                content.AbsoluteTop + (row * RowHeight),
                MathF.Max(1f, CharacterWidth * 0.1f),
                RowHeight
            ),
            Document.ColorOf(Style, caretColorStandard)
            ?? Document.ColorOf(Style, caretColor)
            ?? Document.ForegroundOf(this)
        );
    }

    // ── Caret and editing ────────────────────────────────────────────────────

    /// <summary>Puts the caret somewhere, optionally dragging the selection with it.</summary>
    /// <param name="position">Where.</param>
    /// <param name="extend">Whether to keep the anchor, which is what Shift does.</param>
    public void Move(TextPosition position, bool extend = false) {
        Caret = buffer.Clamp(position);

        if (!extend) {
            Anchor = Caret;
        }

        Reveal();
        CaretMoved?.Invoke(this);
        Document.Invalidate();
    }

    /// <summary>Selects everything.</summary>
    public void SelectAll() {
        Anchor = default;
        Caret = buffer.End;

        CaretMoved?.Invoke(this);
        Document.Invalidate();
    }

    /// <summary>Replaces the selection, or inserts at the caret if there is none.</summary>
    /// <param name="text">What to put in.</param>
    public void Insert(string text) {
        ArgumentNullException.ThrowIfNull(text);

        if (ReadOnly) {
            return;
        }

        editing = true;

        try {
            var at = DeleteSelection();

            Invalidate(at.Line);

            Caret = buffer.Insert(at, text);
            Anchor = Caret;
        } finally {
            editing = false;
        }

        Refresh();
        Reveal();

        CaretMoved?.Invoke(this);
    }

    /// <summary>Removes the selection, or one character either side of the caret.</summary>
    /// <param name="forward">Whether Delete rather than Backspace.</param>
    public void Erase(bool forward) {
        if (ReadOnly) {
            return;
        }

        editing = true;

        try {
            if (HasSelection) {
                Caret = DeleteSelection();
                Anchor = Caret;
            } else {
                var other = forward ? buffer.Forward(Caret) : buffer.Back(Caret);

                if (other == Caret) {
                    return;
                }

                Invalidate(Math.Min(other.Line, Caret.Line));

                Caret = buffer.Delete(Caret, other);
                Anchor = Caret;
            }
        } finally {
            editing = false;
        }

        Refresh();
        Reveal();

        CaretMoved?.Invoke(this);
    }

    TextPosition DeleteSelection() {
        if (!HasSelection) {
            return Caret;
        }

        Invalidate(Math.Min(Anchor.Line, Caret.Line));

        var at = buffer.Delete(Anchor, Caret);

        Caret = at;
        Anchor = at;

        return at;
    }

    /// <summary>Scrolls until the caret is on screen.</summary>
    public void Reveal() {
        var row = RowAt(Caret);

        if (row < 0) {
            return;
        }

        var cell = RowHeight;
        var top = row * cell;

        if (top < Scroller.ScrollTop) {
            Scroller.ScrollTop = top;
        } else if (top + cell > Scroller.ScrollTop + Scroller.Height) {
            Scroller.ScrollTop = top + cell - Scroller.Height;
        }

        var x = (Caret.Column - starts[row]) * CharacterWidth;

        if (x < Scroller.ScrollLeft) {
            Scroller.ScrollLeft = x;
        } else if (x + CharacterWidth > Scroller.ScrollLeft + Scroller.Width) {
            Scroller.ScrollLeft = x + CharacterWidth - Scroller.Width;
        }

        Realise();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    void Typed(TextInputEvent args) {
        if (ReadOnly || string.IsNullOrEmpty(args.Text)) {
            return;
        }

        Insert(args.Text);

        // A popup that is up filters as the word grows, and closes when the word ends. That is what
        // makes it feel like a suggestion rather than a mode.
        if (IsCompleting) {
            ShowCompletion();
        }

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        if (IsCompleting && Completing(args)) {
            args.Handled = true;
            return;
        }

        var extend = args.Modifiers.HasFlag(ModifierKeys.Shift);
        var word = args.Modifiers.HasFlag(ModifierKeys.Control);

        switch (args.Key) {
            case InputKey.Left:
                Move(word ? buffer.WordStart(Caret) : buffer.Back(Caret), extend);
                break;

            case InputKey.Right:
                Move(word ? buffer.WordEnd(Caret) : buffer.Forward(Caret), extend);
                break;

            case InputKey.Up:
                Step(-1, extend);
                break;

            case InputKey.Down:
                Step(1, extend);
                break;

            case InputKey.PageUp:
                Step(-VisibleRows, extend);
                break;

            case InputKey.PageDown:
                Step(VisibleRows, extend);
                break;

            case InputKey.Home:
                Move(word ? default : Home(), extend);
                break;

            case InputKey.End:
                Move(word ? buffer.End : Caret with { Column = buffer[Caret.Line].Length }, extend);
                break;

            case InputKey.Backspace:
                Erase(false);
                break;

            case InputKey.Delete:
                Erase(true);
                break;

            // ⚠ Whatever is held. `TextArea` gives Ctrl-Enter to submission so a form's default
            // button stays reachable from a field that took the plain key; this control does not,
            // and that is a decision rather than an omission. Nothing in this tree puts a code
            // editor inside a form, so the second claimant on the chord does not exist here — and a
            // chord that silently stopped inserting a newline would be a worse surprise than one
            // that does nothing. The day a code editor lives in a dialog, it raises `SubmitEvent`
            // on Ctrl-Enter and `TextField.Keyed`'s comment says why.
            case InputKey.Enter or InputKey.KeypadEnter:
                Insert(AutoIndent ? "\n" + buffer[Caret.Line][..buffer.IndentOf(Caret.Line)] : "\n");
                break;

            case InputKey.Tab:
                Indent(!extend);
                break;

            case InputKey.A when word:
                SelectAll();
                break;

            case InputKey.Space when word:
                ShowCompletion();
                break;

            case InputKey.Escape when IsCompleting:
                HideCompletion();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    int VisibleRows => Math.Max(1, (int) (Scroller.Height / MathF.Max(1f, RowHeight)) - 1);

    /// <summary>Where Home goes: the first non-space, or column zero if it is already there.</summary>
    /// <remarks>
    ///     The two-stop Home every editor has. Going straight to column zero makes the common case —
    ///     "take me to the start of the code on this line" — a keypress followed by a word-right.
    /// </remarks>
    TextPosition Home() {
        var indent = buffer.IndentOf(Caret.Line);
        return Caret with { Column = Caret.Column == indent ? 0 : indent };
    }

    void Step(int delta, bool extend) {
        var row = RowAt(Caret);

        if (row < 0) {
            return;
        }

        // ⚠ Through the row list rather than by adding to the line number, because a collapsed fold
        // means the line below the caret is not the next line. Down would step into a hidden line
        // and the caret would vanish. ⚠ And rows rather than lines is also what makes Down move one
        // *visual* line in a wrapped file: the offset within the row is what is kept, so the caret
        // comes down where the eye expects rather than jumping a whole paragraph.
        var target = Math.Clamp(row + delta, 0, rows.Count - 1);
        var column = Math.Min(starts[target] + (Caret.Column - starts[row]), CaretLimitOf(target));

        Move(new TextPosition(rows[target], column), extend);
    }

    /// <summary>Tab: indents the selected lines, or inserts spaces.</summary>
    void Indent(bool forward) {
        if (ReadOnly) {
            return;
        }

        if (!HasSelection && forward) {
            Insert(new string(' ', Math.Max(1, TabSize)));
            return;
        }

        var from = Math.Min(Anchor.Line, Caret.Line);
        var to = Math.Max(Anchor.Line, Caret.Line);
        var pad = new string(' ', Math.Max(1, TabSize));

        Invalidate(from);
        editing = true;

        try {
            for (var line = from; line <= to; line++) {
                if (forward) {
                    buffer.Insert(new TextPosition(line, 0), pad);
                    continue;
                }

                var indent = Math.Min(buffer.IndentOf(line), pad.Length);

                if (indent > 0) {
                    buffer.Delete(new TextPosition(line, 0), new TextPosition(line, indent));
                }
            }
        } finally {
            editing = false;
        }

        Anchor = buffer.Clamp(Anchor);
        Caret = buffer.Clamp(Caret);

        Refresh();
        CaretMoved?.Invoke(this);
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                Document.Focus(this);

                if (Fold(args) || Completion.Bounds.Contains(new Vector2(args.X, args.Y))) {
                    break;
                }

                Move(ToPosition(args.X, args.Y), args.Modifiers.HasFlag(ModifierKeys.Shift));

                selecting = true;
                Document.CapturePointer(this);

                break;

            case PointerAction.Moved when selecting:
                Move(ToPosition(args.X, args.Y), true);
                break;

            case PointerAction.Released when selecting:
                selecting = false;
                Document.ReleasePointer();

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// <summary>Whether the press was on a gutter arrow, and folded something.</summary>
    bool Fold(PointerEvent args) {
        for (var walk = args.Source; walk is not null; walk = walk.Parent) {
            if (walk is CodeGutterRow { Index: >= 0 } row) {
                return ToggleFold(row.Index);
            }
        }

        return false;
    }

    void Tapped(TapEvent args) {
        if (args.Count != 2) {
            return;
        }

        var at = ToPosition(args.X, args.Y);

        Anchor = buffer.WordStart(buffer.Forward(at));
        Caret = buffer.WordEnd(at);

        CaretMoved?.Invoke(this);
        Document.Invalidate();

        args.Handled = true;
    }

    // ── Completion ───────────────────────────────────────────────────────────

    /// <summary>Asks the provider what fits and shows the popup, if anything does.</summary>
    public void ShowCompletion() {
        completions.Clear();

        var prefix = buffer.WordBefore(Caret);

        if (CompletionProvider?.Invoke(this, prefix) is { } offered) {
            foreach (var item in offered) {
                if (prefix.Length == 0 || item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    completions.Add(item);
                }
            }
        }

        if (completions.Count == 0) {
            HideCompletion();
            return;
        }

        IsCompleting = true;
        completionIndex = Math.Clamp(completionIndex, 0, completions.Count - 1);

        while (completionRows.Count < completions.Count) {
            completionRows.Add(Completion.Add("code-completion-item"));
        }

        for (var i = 0; i < completionRows.Count; i++) {
            var row = completionRows[i];

            if (i >= completions.Count) {
                row.AddClass("parked");
                continue;
            }

            row.RemoveClass("parked");
            row.Text = completions[i].Detail is { } detail
                ? completions[i].Label + "  " + detail
                : completions[i].Label;

            if (i == completionIndex) {
                row.AddClass("selected");
            } else {
                row.RemoveClass("selected");
            }
        }

        var caret = ToScreen(Caret);

        Completion.RemoveClass("hidden");
        Completion.SetStyle("left", Inline.Px(caret.X - (prefix.Length * CharacterWidth) - AbsoluteLeft));
        Completion.SetStyle("top", Inline.Px(caret.Y + RowHeight - AbsoluteTop));
    }

    /// <summary>Takes the popup down.</summary>
    public void HideCompletion() {
        IsCompleting = false;
        completionIndex = 0;

        completions.Clear();
        Completion.AddClass("hidden");
    }

    /// <summary>Puts the highlighted completion in, replacing the word being typed.</summary>
    /// <returns>Whether there was one.</returns>
    public bool AcceptCompletion() {
        if (!IsCompleting || completionIndex >= completions.Count) {
            return false;
        }

        var item = completions[completionIndex];
        var prefix = buffer.WordBefore(Caret);

        // The prefix goes and the whole label arrives, rather than the remainder being appended.
        // Case differs — `stre` accepting `Strength` — and appending gives `streStrength`.
        if (prefix.Length > 0) {
            Invalidate(Caret.Line);
            editing = true;

            try {
                Caret = buffer.Delete(Caret with { Column = Caret.Column - prefix.Length }, Caret);
                Anchor = Caret;
            } finally {
                editing = false;
            }
        }

        HideCompletion();
        Insert(item.Label);

        CompletionAccepted?.Invoke(this, item);
        return true;
    }

    /// <summary>Whether a key belonged to the popup rather than to the text.</summary>
    bool Completing(KeyEvent args) {
        switch (args.Key) {
            case InputKey.Down:
                completionIndex = (completionIndex + 1) % Math.Max(1, completions.Count);
                break;

            case InputKey.Up:
                completionIndex = (completionIndex + Math.Max(1, completions.Count) - 1) % Math.Max(1, completions.Count);
                break;

            case InputKey.Enter or InputKey.KeypadEnter or InputKey.Tab:
                return AcceptCompletion();

            case InputKey.Escape:
                HideCompletion();
                return true;

            default:
                return false;
        }

        ShowCompletion();
        return true;
    }
}
