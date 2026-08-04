// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

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
/// </remarks>
public sealed class BehaviorSearchPopup : Control {
    readonly List<BehaviorNodeType> matches = [];

    BehaviorNodeSchema? schema;
    BehaviorSlot slot;

    /// <inheritdoc />
    protected override string TagName => "behavior-search";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>What was typed.</summary>
    public TextBox Query { get; private set; } = null!;

    /// <summary>The rows.</summary>
    public UiElement Results { get; private set; } = null!;

    /// <summary>Whether it is showing.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>What is offered, best first.</summary>
    public IReadOnlyList<BehaviorNodeType> Matches => matches;

    /// <summary>Raised when a row is picked.</summary>
    public event Action<BehaviorNodeType>? Chosen;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Query = Add<TextBox>();
        Query.Placeholder = "Search nodes…";
        Query.ValueChanged += (_, _) => Rank();

        Results = Add("behavior-search-results");
        AddClass("hidden");

        AddHandler<ClickEvent>(static (element, args) => ((BehaviorSearchPopup) element).Picked(args));
    }

    /// <summary>Opens it over a slot.</summary>
    /// <param name="library">The node library.</param>
    /// <param name="wanted">Which slot the new thing goes in.</param>
    /// <param name="x">Where to put the popup, in document space.</param>
    /// <param name="y">Ditto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="library" /> is null.</exception>
    public void Show(BehaviorNodeSchema library, BehaviorSlot wanted, float x, float y) {
        ArgumentNullException.ThrowIfNull(library);

        schema = library;
        slot = wanted;
        IsOpen = true;

        RemoveClass("hidden");
        SetStyle("left", Pixels(x));
        SetStyle("top", Pixels(y));

        Query.Value = string.Empty;
        Rank();
    }

    /// <summary>Closes it.</summary>
    public void Close() {
        IsOpen = false;
        AddClass("hidden");
    }

    /// <summary>Re-ranks the rows against what has been typed.</summary>
    public void Rank() {
        matches.Clear();

        while (Results.Children.Count > 0) {
            Results.Children[^1].Remove();
        }

        if (schema is null) {
            return;
        }

        var query = (Query.Value ?? string.Empty).Trim();

        foreach (var type in schema.For(slot)) {
            if (Score(type, query) > 0) {
                matches.Add(type);
            }
        }

        // Best first, and ties on the declaration order — which groups the composites together and
        // puts Selector above Sequence, because that is the order somebody reading the library
        // learned them in.
        matches.Sort((left, right) => Score(right, query).CompareTo(Score(left, query)));

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

    static string Pixels(float value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";

    static int IndexIn(UiElement list, UiElement child) {
        for (var index = 0; index < list.Children.Count; index++) {
            if (ReferenceEquals(list.Children[index], child)) {
                return index;
            }
        }

        return -1;
    }

    void Picked(ClickEvent args) {
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

        var view = panel.Add<BehaviorTreeView>();

        view.Show((BehaviorTreeDocument) document);

        return view;
    }
}
