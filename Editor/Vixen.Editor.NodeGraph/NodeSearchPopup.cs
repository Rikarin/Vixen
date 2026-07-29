// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.NodeGraph;

/// <summary>One offered node type.</summary>
public sealed partial class NodeSearchRow : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "node-search-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the type sits in the create menu.</summary>
    public UiElement CategoryPart { get; private set; } = null!;

    /// <summary>The port a dragged wire would land on, when there is one.</summary>
    public UiElement PortPart { get; private set; } = null!;

    /// <summary>Which result it is showing, as an index into the popup's list.</summary>
    public int Index { get; internal set; } = -1;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        CategoryPart = Part("node-search-category");
        PortPart = Part("node-search-port");
    }
}

/// <summary>
///     Create-a-node by typing, and by dragging a wire into empty space.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the feature the UX target is judged by.</b> Doc 11 names Unity's shader graph as
///         best-in-class and names searchable creation from a dragged wire as the reason. A create
///         menu of nested submenus is what a graph editor has instead when nobody built this.
///     </para>
///     <para>
///         ⚠ <b>The field keeps the focus and the rows never take it</b>, the same arrangement
///         <c>CommandPalette</c> has and for the same reason: a popup where Down moved the focus into
///         the list is one where the next letter typed goes nowhere. The arrows move a highlight this
///         class owns.
///     </para>
///     <para>
///         <b>Filtered by the wire, not merely opened by it.</b> A wire dragged off a texture output
///         offers the node types with a texture input, and the row says which port it would land on —
///         so the gesture is one drag and one Enter rather than a drag, a menu, a guess and a second
///         drag.
///     </para>
/// </remarks>
public sealed partial class NodeSearchPopup : Overlay {
    readonly List<NodeSearchResult> results = [];
    readonly List<NodeSearchRow> rows = [];

    NodeTypeRegistry? registry;
    PortFilter? filter;
    int highlighted;

    /// <inheritdoc />
    protected override string TagName => "node-search";

    /// <summary>How many results are shown.</summary>
    public int Limit { get; set; } = 12;

    /// <summary>What is typed into it.</summary>
    public SearchBox Field { get; private set; } = null!;

    /// <summary>Where the rows go.</summary>
    public UiElement List { get; private set; } = null!;

    /// <summary>What it says when nothing matched.</summary>
    public UiElement EmptyPart { get; private set; } = null!;

    /// <summary>What it is showing, best first.</summary>
    public IReadOnlyList<NodeSearchResult> Results => results;

    /// <summary>Which result is highlighted, or -1 when there are none.</summary>
    public int Highlighted => results.Count == 0 ? -1 : highlighted;

    /// <summary>The port a dragged wire is looking for, when the popup was opened by one.</summary>
    public PortFilter? Filter => filter;

    /// <summary>Raised when a node type is chosen.</summary>
    public event Action<NodeSearchPopup, NodeSearchResult>? Accepted;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;
        LightDismiss = true;
        CloseOnEscape = true;

        Field = Part<SearchBox>();
        Field.Placeholder = "Create node";
        Field.ValueChanged += (_, _) => Refresh();

        List = Part("node-search-list");

        EmptyPart = Part("node-search-empty");
        EmptyPart.Text = "No node type matches.";

        // Capture, so the arrows and Enter are taken before the search box treats them as text
        // navigation — the field is what has the focus, and it would otherwise move a caret.
        AddHandler<KeyEvent>(static (element, args) => ((NodeSearchPopup) element).Keyed(args), RoutingStrategy.Capture);
        AddHandler<ClickEvent>(static (element, args) => ((NodeSearchPopup) element).Chosen(args));
    }

    /// <summary>Opens it at a point, over everything.</summary>
    /// <param name="library">The node types it may offer.</param>
    /// <param name="wire">The port a dragged wire left, when one was dragged.</param>
    /// <param name="x">Where to put it, in document space.</param>
    /// <param name="y">And vertically.</param>
    /// <exception cref="ArgumentNullException"><paramref name="library" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The query is cleared every time.</b> A popup that remembered what was typed last makes
    ///     the common case — open it, type three letters, press Enter — start by deleting somebody
    ///     else's word.
    /// </remarks>
    public void Show(NodeTypeRegistry library, PortFilter? wire, float x, float y) {
        ArgumentNullException.ThrowIfNull(library);

        registry = library;
        filter = wire;
        highlighted = 0;

        Field.Value = "";

        Refresh();
        Open();
        MoveTo(x, y);

        Document.Focus(Field);
    }

    /// <summary>Chooses the highlighted result, as Enter does.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Accept() {
        if (Highlighted < 0) {
            return false;
        }

        var chosen = results[highlighted];

        Close();
        Accepted?.Invoke(this, chosen);

        return true;
    }

    /// <summary>Moves the highlight.</summary>
    /// <param name="delta">How far, in rows.</param>
    /// <remarks>
    ///     Clamped rather than wrapped. A list of twelve that jumps from the bottom to the top on one
    ///     more Down is one where holding the key never settles anywhere.
    /// </remarks>
    public void Highlight(int delta) {
        if (results.Count == 0) {
            return;
        }

        highlighted = Math.Clamp(highlighted + delta, 0, results.Count - 1);
        Restate();
    }

    void Refresh() {
        results.Clear();

        if (registry is not null) {
            results.AddRange(NodeSearch.Rank(registry, Field.Value, filter, Limit));
        }

        highlighted = Math.Clamp(highlighted, 0, Math.Max(0, results.Count - 1));

        // ⚠ Rebuilt per query and capped, for CommandPalette's reason: a popup that realised a
        // thousand rows to show twelve would be the tree view's virtualisation problem without the
        // tree view's payoff. The pool only grows, because removal is final in this framework.
        while (rows.Count < results.Count) {
            var row = List.Add<NodeSearchRow>();
            row.Index = rows.Count;

            rows.Add(row);
        }

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (index >= results.Count) {
                row.AddClass("hidden");

                continue;
            }

            var result = results[index];

            row.RemoveClass("hidden");
            row.Label = result.Type.Title;
            row.CategoryPart.Text = result.Type.Category;
            row.PortPart.Text = result.Port;
        }

        if (results.Count == 0) {
            EmptyPart.RemoveClass("hidden");
        } else {
            EmptyPart.AddClass("hidden");
        }

        Restate();
    }

    void Restate() {
        for (var index = 0; index < rows.Count; index++) {
            if (index == highlighted && index < results.Count) {
                rows[index].State |= ElementState.Checked;
            } else {
                rows[index].State &= ~ElementState.Checked;
            }
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Down:
                Highlight(1);
                break;

            case InputKey.Up:
                Highlight(-1);
                break;

            case InputKey.Enter or InputKey.KeypadEnter:
                Accept();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not NodeSearchRow row || row.Index < 0 || row.Index >= results.Count) {
            return;
        }

        highlighted = row.Index;
        args.Handled = true;

        Accept();
    }
}
