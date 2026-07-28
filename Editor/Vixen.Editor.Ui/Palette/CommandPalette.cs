// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>One row of the palette.</summary>
public sealed partial class PaletteRow : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "palette-row";

    /// <summary>Where this row's result came from.</summary>
    public UiElement CategoryPart { get; private set; } = null!;

    /// <summary>The shortcut or path on the right, if there is one.</summary>
    public UiElement DetailPart { get; private set; } = null!;

    /// <summary>Which result it is showing, as an index into the palette's list.</summary>
    public int Index { get; internal set; } = -1;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        CategoryPart = Part("palette-category");
        DetailPart = Part("palette-detail");
    }
}

/// <summary>Fuzzy search over everything the editor can do, reached with one chord.</summary>
/// <remarks>
///     <para>
///         <b>Cheap to build on the registry, and the feature power users judge tooling by</b> —
///         doc 11's words, and the reason it is in the first version of the shell rather than a
///         later one. Nothing here knows what a command is beyond
///         <see cref="IPaletteSource" />: commands, assets, scene objects and settings all arrive
///         the same way.
///     </para>
///     <para>
///         ⚠ <b>The field keeps the focus and the rows never take it.</b> A palette where Down moved
///         the focus into the list is one where the next letter typed goes nowhere — so the arrows
///         move a highlight this class owns, and the rows are not focusable at all. It is the
///         opposite arrangement to <see cref="Menu" />, whose items <i>are</i> the focus, and for the
///         opposite reason: a menu has no text field to protect.
///     </para>
///     <para>
///         ⚠ <b>Rows are rebuilt per query and capped.</b> Ten results is what fits and what anybody
///         reads; a palette that realised a thousand rows to show ten would be the tree view's
///         virtualisation problem without the tree view's payoff.
///     </para>
/// </remarks>
public sealed partial class CommandPalette : Overlay {
    readonly List<PaletteItem> results = [];
    readonly List<IPaletteSource> sources = [];
    readonly List<PaletteRow> rows = [];

    int highlighted;

    /// <inheritdoc />
    protected override string TagName => "command-palette";

    /// <summary>How many results are shown.</summary>
    public int Limit { get; set; } = 10;

    /// <summary>What is typed into it.</summary>
    public SearchBox Field { get; private set; } = null!;

    /// <summary>Where the rows go.</summary>
    public UiElement List { get; private set; } = null!;

    /// <summary>What it says when nothing matched.</summary>
    public UiElement EmptyPart { get; private set; } = null!;

    /// <summary>Where it looks, in order.</summary>
    public IReadOnlyList<IPaletteSource> Sources => sources;

    /// <summary>What it is currently showing, best first.</summary>
    public IReadOnlyList<PaletteItem> Results => results;

    /// <summary>Which result is highlighted, or -1.</summary>
    public int Highlighted => results.Count == 0 ? -1 : highlighted;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;
        LightDismiss = true;
        CloseOnEscape = true;

        Field = Part<SearchBox>();
        List = Part("palette-list");
        EmptyPart = Part("palette-empty");

        Field.ValueChanged += (_, _) => Refresh();
        Field.Placeholder = EditorStrings.PalettePlaceholder.Text;
        EmptyPart.Text = EditorStrings.PaletteEmpty.Text;

        AddHandler<KeyEvent>(static (element, args) => ((CommandPalette) element).Keyed(args), RoutingStrategy.Capture);
        AddHandler<ClickEvent>(static (element, args) => ((CommandPalette) element).Chosen(args));
    }

    /// <summary>Adds somewhere for it to look.</summary>
    /// <param name="source">The source. Order decides the tiebreak between equal scores.</param>
    public void AddSource(IPaletteSource source) {
        ArgumentNullException.ThrowIfNull(source);
        sources.Add(source);
    }

    /// <summary>Opens it in the middle of the top of the document, empty.</summary>
    /// <remarks>
    ///     ⚠ <b>The query is cleared every time.</b> A palette that remembered what was typed last
    ///     would make the common case — open it, type three letters, press Enter — start by deleting
    ///     somebody else's word. Recency belongs in the ranking, not in the field.
    /// </remarks>
    public void OpenPalette() {
        Field.Value = string.Empty;
        highlighted = 0;

        Open();
        Refresh();

        var viewport = Document.Viewport;
        var width = Bounds.Width;

        MoveTo(MathF.Max(0f, (viewport.ViewportWidth - width) * 0.5f), viewport.ViewportHeight * 0.12f);
        Document.Focus(Field);
    }

    /// <summary>Runs the highlighted result and closes.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Accept() {
        if (Highlighted < 0) {
            return false;
        }

        var item = results[highlighted];

        // ⚠ Closed before the action runs. A command that opens a dialog, or another palette, would
        // otherwise be covered by the palette that started it — and one that throws would leave the
        // palette up with the query still in it.
        Close(CloseReason.Committed);
        item.Run();

        return true;
    }

    /// <summary>Moves the highlight.</summary>
    /// <param name="delta">By how many rows, wrapping at both ends.</param>
    public void Move(int delta) {
        if (results.Count == 0) {
            return;
        }

        highlighted = ((highlighted + delta) % results.Count + results.Count) % results.Count;
        Restyle();
    }

    /// <summary>Asks every source what matches what is typed, and shows the best of it.</summary>
    public void Refresh() {
        var query = Field.Value ?? string.Empty;

        results.Clear();

        foreach (var source in sources) {
            source.Search(query, results, Limit);
        }

        // ⚠ A stable sort, which is what `List.Sort` is not — so the order sources were added in,
        // and the order each returned its own results in, survives a tie. Without it a palette
        // showing two equally-good matches would put them in a different order on every keystroke.
        var ranked = results.OrderByDescending(item => item.Score).Take(Limit).ToList();

        results.Clear();
        results.AddRange(ranked);

        highlighted = Math.Clamp(highlighted, 0, Math.Max(0, results.Count - 1));
        Build();
    }

    /// <inheritdoc />
    protected override void OnOpened() {
        base.OnOpened();
        Document.Focus(Field);
    }

    void Build() {
        while (rows.Count < results.Count) {
            var row = List.Add<PaletteRow>();
            row.Focusable = false;

            rows.Add(row);
        }

        for (var i = 0; i < rows.Count; i++) {
            var row = rows[i];

            if (i >= results.Count) {
                // Parked rather than removed, because the palette is rebuilt on every keystroke and
                // a row is cheaper to hide than to make. The pool is bounded by `Limit`.
                row.AddClass("parked");
                row.Index = -1;

                continue;
            }

            var item = results[i];

            row.RemoveClass("parked");
            row.Index = i;
            row.Label = item.Title;
            row.CategoryPart.Text = item.Category;
            row.DetailPart.Text = item.Detail ?? string.Empty;
        }

        EmptyPart.SetStyle("display", results.Count == 0 ? "flex" : "none");
        Restyle();
    }

    void Restyle() {
        for (var i = 0; i < rows.Count; i++) {
            if (i == highlighted && i < results.Count) {
                rows[i].State |= ElementState.Checked;
            } else {
                rows[i].State &= ~ElementState.Checked;
            }
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Down:
                Move(1);
                break;

            case InputKey.Up:
                Move(-1);
                break;

            case InputKey.Enter or InputKey.KeypadEnter:
                Accept();
                break;

            default:
                return;
        }

        // ⚠ Handled on the capture leg, before the field sees it. A search box treats Enter as
        // "submit" and the arrows as caret movement, and both would fight the list.
        args.Handled = true;
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (element is not PaletteRow { Index: >= 0 } row) {
                continue;
            }

            highlighted = row.Index;
            Accept();

            args.Handled = true;
            return;
        }
    }
}
