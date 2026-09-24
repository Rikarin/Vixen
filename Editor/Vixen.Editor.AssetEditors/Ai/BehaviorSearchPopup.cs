// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Editor.Core;
using Vixen.Ui;
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
///         relative to the view, while <c>Show</c> was documented as taking document space, so every
///         panel not docked at the window's origin would have put it off by the panel's offset.
///         Nobody saw that because nothing ever opened it (#1370). As an overlay it is drawn over
///         whatever clips the panel, closes on Escape and on a press outside it, and keeps the focus
///         in its field so the next letter typed lands there.
///     </para>
///     <para>
///         The popup is <c>BehaviorSearchPopup.vxml</c> (#89); this file is the accessibility
///         modifier, the ranking rule, which reads no element, and the two elements that exist only so
///         that markup can write an intrinsic tag's own <c>Text</c>.
///     </para>
/// </remarks>
public sealed partial class BehaviorSearchPopup {
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
}

/// <summary>A search row's label: the type's name.</summary>
/// <remarks>An element type so that markup can set an intrinsic tag's <c>Text</c> — the panel ledger's shape 5.</remarks>
internal sealed class BehaviorSearchLabel : UiElement {
    /// <inheritdoc />
    protected override string TagName => "search-label";
}

/// <summary>A search row's category, right-aligned beside its label.</summary>
/// <remarks>An element type so that markup can set an intrinsic tag's <c>Text</c> — the panel ledger's shape 5.</remarks>
internal sealed class BehaviorSearchCategory : UiElement {
    /// <inheritdoc />
    protected override string TagName => "search-category";
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
