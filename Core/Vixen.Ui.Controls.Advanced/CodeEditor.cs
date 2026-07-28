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

    internal void Bind(string text, List<CodeToken> tokens) {
        while (spans.Count < tokens.Count) {
            spans.Add(Add<CodeSpan>());
        }

        for (var i = 0; i < spans.Count; i++) {
            var span = spans[i];

            if (i >= tokens.Count) {
                span.AddClass("parked");
                span.Text = null;

                continue;
            }

            var token = tokens[i];

            span.RemoveClass("parked");
            span.Recolour(token.Kind);
            span.Text = text.Substring(token.Start, token.Length);
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
    readonly List<int> rows = [];

    /// <summary>The tokenizer's state at the start of each line.</summary>
    readonly List<int> states = [];

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
    int currentLineColor;
    bool editing;

    /// <summary>How many lines are realised above and below the viewport.</summary>
    public const int Overscan = 2;

    /// <inheritdoc />
    protected override string TagName => "code-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

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
    public IReadOnlyList<int> Rows => rows;

    /// <summary>Where the caret is.</summary>
    public TextPosition Caret { get; private set; }

    /// <summary>The other end of the selection. Equal to the caret when nothing is selected.</summary>
    public TextPosition Anchor { get; private set; }

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
        currentLineColor = Document.PropertyId("--current-line-color");

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
        if (Probe.Line() is { } line) {
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

        rows.Clear();
        longest = 0;

        for (var line = 0; line < buffer.LineCount; line++) {
            rows.Add(line);
            longest = Math.Max(longest, buffer[line].Length);

            // A collapsed fold is lines that are simply not in the row list. Everything below —
            // virtualisation, the caret's row, the scroll range — then works without knowing that
            // folding is a thing, which is what keeps it from being a special case in six places.
            if (collapsed.Contains(line) && FoldAt(line) is { } fold) {
                line = fold.End;
            }
        }

        Scroller.Content.SetStyle("height", Inline.Px(rows.Count * RowHeight));
        Scroller.Content.SetStyle("width", Inline.Px((longest + 2) * CharacterWidth));

        // A pass before anything reads a size, for the reason `TreeView.Refresh` gives: the height
        // above is a declaration and `ScrollView.Refresh` needs a measurement.
        Document.Update();

        Scroller.Refresh();
        Realise();
    }

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

            scratch.Clear();
            Tokenizer.Tokenize(buffer[index], StateAt(index), scratch);
            line.Bind(buffer[index], scratch);

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

            gutterRow.RemoveClass("parked");
            gutterRow.Index = index;
            gutterRow.Number.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // ⚠ Positioned against the viewport rather than against the content, because the gutter
            // is outside the scroller: it follows the vertical scroll and must not follow the
            // horizontal one, which is exactly what subtracting only `ScrollTop` gives.
            gutterRow.SetStyle("top", Inline.Px((row * cell) - Scroller.ScrollTop));
            gutterRow.SetStyle("height", Inline.Px(cell));

            if (FoldAt(index) is not null) {
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
        var row = RowOf(position.Line);
        var content = Scroller.Content;

        return new Vector2(
            content.AbsoluteLeft + (position.Column * CharacterWidth),
            content.AbsoluteTop + (Math.Max(row, 0) * RowHeight)
        );
    }

    /// <summary>Which character a point is over.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The place, clamped into the text.</returns>
    public TextPosition ToPosition(float x, float y) {
        var content = Scroller.Content;

        var row = (int) MathF.Floor((y - content.AbsoluteTop) / RowHeight);
        var line = rows.Count == 0 ? 0 : rows[Math.Clamp(row, 0, rows.Count - 1)];

        // ⚠ Rounded rather than floored, so a click on the right half of a character puts the caret
        // after it. Flooring makes the last column of every line unreachable by clicking.
        var column = (int) MathF.Round((x - content.AbsoluteLeft) / CharacterWidth);

        return buffer.Clamp(new TextPosition(line, column));
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    internal void DrawSelection(DrawContext context) {
        var cell = RowHeight;
        var width = CharacterWidth;
        var content = Scroller.Content;

        if (!HasSelection) {
            var only = RowOf(Caret.Line);

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

            var start = line == from.Line ? from.Column : 0;

            // ⚠ A line inside a multi-line selection is drawn one character wider than its text, so
            // the newline it swallowed is visible. Without it a block selection has ragged holes
            // where the short lines are, and nobody can tell whether the newline is included.
            var end = line == to.Line ? to.Column : buffer[line].Length + 1;

            context.FillRectangle(
                new Rectangle(
                    content.AbsoluteLeft + (start * width),
                    content.AbsoluteTop + (row * cell),
                    MathF.Max(0f, (end - start) * width),
                    cell
                ),
                colour
            );
        }
    }

    internal void DrawCaret(DrawContext context) {
        var row = RowOf(Caret.Line);

        if (!IsFocused || row < 0) {
            return;
        }

        var content = Scroller.Content;

        context.FillRectangle(
            new Rectangle(
                content.AbsoluteLeft + (Caret.Column * CharacterWidth),
                content.AbsoluteTop + (row * RowHeight),
                MathF.Max(1f, CharacterWidth * 0.1f),
                RowHeight
            ),
            Document.ColorOf(Style, caretColor) ?? Document.ForegroundOf(this)
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
        var row = RowOf(Caret.Line);

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

        var x = Caret.Column * CharacterWidth;

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
        var row = RowOf(Caret.Line);

        if (row < 0) {
            return;
        }

        // ⚠ Through the row list rather than by adding to the line number, because a collapsed fold
        // means the line below the caret is not the next line. Down would step into a hidden line
        // and the caret would vanish.
        var target = Math.Clamp(row + delta, 0, rows.Count - 1);
        Move(new TextPosition(rows[target], Caret.Column), extend);
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
