// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>Search-to-create, filtered by what may go where.</summary>
/// <remarks>
///     <para>
///         <c>NodeSearch</c>'s ranked popup, in shape and in behaviour: type a few letters, get the
///         things whose name or category matches, best first. What is different is the filter —
///         dropping on a composite's child row offers composites and tasks, and dropping on a node's
///         decorator strip offers decorators — because a tree's slots are typed where a dataflow
///         graph's ports are.
///     </para>
///     <para>
///         ⚠ <b>It ranks on the same rule <c>NodeSearch</c> does and does not share the code.</b>
///         That framework's search is over <c>NodeTypeDefinition</c>, which carries ports and a
///         factory this library has neither of. Twenty lines of ranking against a reference to a
///         framework whose model was deliberately not taken is the wrong trade — doc 37 § D19.
///     </para>
///     <para>
///         ⚠ <b>An overlay and a root child, as <c>NodeSearchPopup</c> is, and it was neither.</b> It
///         used to be a child of the tree view placed with <c>left</c>/<c>top</c> — which are
///         relative to the view, while <see cref="Show(BehaviorNodeSchema, BehaviorSlot, float, float)" />
///         was documented as taking document space, so every panel not docked at the window's origin
///         would have put it off by the panel's offset. Nobody saw that because nothing ever opened it
///         (#1370). As an overlay it is drawn over whatever clips the panel, closes on Escape and on a
///         press outside it, and keeps the focus in its field so the next letter typed lands there.
///     </para>
/// </remarks>
public sealed class BehaviorSearchPopup : Overlay {
    readonly List<BehaviorNodeType> matches = [];

    BehaviorNodeSchema? schema;
    BehaviorSlot[] slots = [];

    /// <inheritdoc />
    protected override string TagName => "behavior-search";

    /// <summary>What was typed.</summary>
    public TextBox Query { get; private set; } = null!;

    /// <summary>What scrolls the rows.</summary>
    public ScrollView List { get; private set; } = null!;

    /// <summary>The rows: <see cref="List" />'s content.</summary>
    public UiElement Results { get; private set; } = null!;

    /// <summary>What is offered, best first.</summary>
    public IReadOnlyList<BehaviorNodeType> Matches => matches;

    /// <summary>Which slots it was opened for.</summary>
    public IReadOnlyList<BehaviorSlot> Slots => slots;

    /// <summary>Raised when a row is picked.</summary>
    public event Action<BehaviorNodeType>? Chosen;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;

        Query = Add<TextBox>();
        Query.AddClass("behavior-search-query");
        Query.Placeholder = "Search nodes…";
        Query.ValueChanged += (_, _) => Rank();

        // ⚠ A scroller, where it was a plain list with `overflow: hidden`. A composite's child row
        // offers fourteen types and the popup is 320 px tall, so the list clipped the last two —
        // "Run subtree (from a key)" and "Run utility set" could be reached by typing and by no
        // pointer — and the flex column took the missing height out of the field, 34 px down to 27.
        List = Add<ScrollView>();
        List.AddClass("behavior-search-list");
        Results = List.Content;

        // ⚠ `TapEvent` and not `ClickEvent`: a click is an activation and only a `Control` raises one,
        // so a handler waiting for it on a bare `behavior-search-row` waited for ever — the defect
        // `ConsoleView.Row` and `MessageLogView` each fixed in their own rows (#89, 7d5ba5692).
        AddHandler<TapEvent>(static (element, args) => ((BehaviorSearchPopup) element).Picked(args));

        // Capture, so Enter is taken before the field treats it as a submit of its own.
        AddHandler<KeyEvent>(static (element, args) => ((BehaviorSearchPopup) element).Keyed(args), RoutingStrategy.Capture);
    }

    /// <summary>Opens it over a slot.</summary>
    /// <param name="library">The node library.</param>
    /// <param name="wanted">Which slot the new thing goes in.</param>
    /// <param name="x">Where to put the popup, in document space.</param>
    /// <param name="y">Ditto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="library" /> is null.</exception>
    public void Show(BehaviorNodeSchema library, BehaviorSlot wanted, float x, float y) => Show(library, [wanted], x, y);

    /// <summary>Opens it for anything that fits one of several slots.</summary>
    /// <param name="library">The node library.</param>
    /// <param name="wanted">
    ///     The slots the new thing may fill — a composite's child row takes a composite or a task, so
    ///     that gesture asks for both.
    /// </param>
    /// <param name="x">Where to put the popup, in document space.</param>
    /// <param name="y">Ditto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="library" /> or <paramref name="wanted" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The query is cleared every time</b>, for <c>NodeSearchPopup</c>'s reason: a popup that
    ///     remembered the last word makes the common case start by deleting it.
    /// </remarks>
    public void Show(BehaviorNodeSchema library, IReadOnlyList<BehaviorSlot> wanted, float x, float y) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(wanted);

        schema = library;
        slots = [.. wanted];

        Query.Value = string.Empty;
        Rank();

        Open();

        // Kept on screen: Space pressed near the bottom of a canvas would otherwise open a 320 px
        // popup with most of its rows below the window. `Open` has laid it out, so its size is known.
        var viewport = Document.Viewport;

        MoveTo(
            Math.Clamp(x, 0f, MathF.Max(0f, viewport.ViewportWidth - Bounds.Width)),
            Math.Clamp(y, 0f, MathF.Max(0f, viewport.ViewportHeight - Bounds.Height))
        );

        Document.Focus(Query);
    }

    /// <summary>Re-ranks the rows against what has been typed.</summary>
    public void Rank() {
        matches.Clear();

        // Back to the top before the rows go, and for two reasons. A new ranking puts the best match
        // first, which is where the reader has to be looking. ⚠ And a list scrolled down anchors on
        // one of its rows: rebuilding the rows under it leaves the anchor a removed element, and the
        // next settle asks it for a position and throws out of `UiElement.Document` (#1392). At the
        // start edge nothing is anchored.
        List.ScrollTo(0f, 0f);

        while (Results.Children.Count > 0) {
            Results.Children[^1].Remove();
        }

        if (schema is null) {
            return;
        }

        var query = (Query.Value ?? string.Empty).Trim();

        // Best first, and ties on the declaration order — which groups the composites together and
        // puts Selector above Sequence, because that is the order somebody reading the library
        // learned them in. ⚠ `OrderByDescending` because it is stable and `List.Sort` is not: the
        // latter only kept the order because a single slot is under sixteen types, where it happens
        // to use an insertion sort; a composite's child row asks for two slots and is not.
        matches.AddRange(
            schema.Types
                .Where(type => Array.IndexOf(slots, type.Slot) >= 0)
                .Select(type => (Type: type, Score: Score(type, query)))
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .Select(entry => entry.Type)
        );

        foreach (var type in matches) {
            var row = Results.Add("behavior-search-row");

            row.Add("search-label").Text = type.Label;
            row.Add("search-category").Text = type.Category;
        }
    }

    /// <summary>How well a type answers a query. Zero means it does not.</summary>
    /// <param name="type">The type.</param>
    /// <param name="query">What was typed.</param>
    /// <returns>The score.</returns>
    /// <remarks>
    ///     An exact label beats a prefix beats a substring beats the category, which is the order the
    ///     answer people meant comes in: somebody typing <c>seq</c> wants Sequence and not "the four
    ///     things filed under Sequencing".
    /// </remarks>
    public static int Score(BehaviorNodeType type, string query) {
        ArgumentNullException.ThrowIfNull(type);

        if (string.IsNullOrEmpty(query)) {
            return 1;
        }

        if (string.Equals(type.Label, query, StringComparison.OrdinalIgnoreCase)) {
            return 100;
        }

        if (type.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
            return 50;
        }

        if (type.Label.Contains(query, StringComparison.OrdinalIgnoreCase)) {
            return 20;
        }

        return type.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ? 5 : 0;
    }

    /// <summary>Picks the row at an index, which is what a click and the keyboard both do.</summary>
    /// <param name="index">Which row.</param>
    /// <returns>Whether there was one.</returns>
    public bool Pick(int index) {
        if ((uint) index >= (uint) matches.Count) {
            return false;
        }

        Chosen?.Invoke(matches[index]);
        Close();

        return true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || args.Key is not (InputKey.Enter or InputKey.KeypadEnter)) {
            return;
        }

        // The best match, which is the one at the top: type three letters, press Enter.
        Pick(0);
        args.Handled = true;
    }

    static int IndexIn(UiElement list, UiElement child) {
        for (var index = 0; index < list.Children.Count; index++) {
            if (ReferenceEquals(list.Children[index], child)) {
                return index;
            }
        }

        return -1;
    }

    void Picked(TapEvent args) {
        // ⚠ Found by position rather than by a reference on the element: `UiElement.Tag` is the
        // element's *name* in this framework, not a slot for an object, so the row's index in the
        // ranked list is what ties it back to what it offers.
        for (var element = args.Source; element is not null && !ReferenceEquals(element, this); element = element.Parent) {
            var index = IndexIn(Results, element);

            if (index >= 0 && Pick(index)) {
                args.Handled = true;

                return;
            }
        }
    }
}

/// <summary>Opens a behaviour tree.</summary>
/// <remarks>
///     ⚠ <b>In this assembly rather than in <c>Vixen.Editor.Ai</c></b>, which is where doc 37 § Part 5
///     files it. That assembly is the model, the layout and the projection, and it deliberately knows
///     nothing about a project, a document or a panel — the same split every other graph editor in
///     this repository makes, and the reason the model can be tested with no editor in the way.
/// </remarks>
public sealed class BehaviorTreeEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Behaviour Tree";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [BehaviorTreeDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new BehaviorTreeDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // A node canvas with its own pan and zoom — see `ShaderGraphView.CreateView`.
        DockPanel.Fills(panel);

        var view = panel.Add<BehaviorTreeView>();

        view.Show((BehaviorTreeDocument) document);

        return view;
    }
}
