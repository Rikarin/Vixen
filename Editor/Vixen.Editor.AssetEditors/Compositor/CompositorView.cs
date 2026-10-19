// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Compositor;

/// <summary>A graphics compositor, open for editing: the graph, the selected node, and the frame.</summary>
/// <remarks>
///     <para>
///         Doc 06's "the frame is data the user edits", with a canvas over it. The graph itself is
///         <see cref="NodeGraphView" />'s — every gesture there is already an undoable command, and
///         nothing about a compositor changes that — so what is here is the two things a compositor
///         needs beside it: the settings of whatever is selected, and what compiling says.
///     </para>
///     <para>
///         ⚠ <b>The settings panel is not the inspector, and the reason is where the values live.</b>
///         An <c>InspectorView</c> edits members of an object; a node's settings live on the
///         <i>graph</i>, in <c>Values</c> and <c>Texts</c> keyed by port name, because that is what
///         survives a save and an undo. So the rows are built from
///         <see cref="CompositorNode.Fields" /> and write through <c>SetPortValueCommand</c> and
///         <c>SetPortTextCommand</c> — which is what keeps typing a target name one undo entry
///         rather than one a keystroke.
///     </para>
///     <para>
///         The panel is <c>CompositorView.vxml</c>; this file is the accessibility modifier and the
///         factory. The emitter's partial carries no modifier and this type is handed out by
///         <see cref="CompositorEditorView" />.
///     </para>
/// </remarks>
public sealed partial class CompositorView;

/// <summary>Opens a graphics compositor.</summary>
public sealed class CompositorEditorFactory : DocumentKind {
    readonly NodeTypeRegistry registry = CompositorGraphCompiler.CreateRegistry();

    /// <inheritdoc />
    public override string Name => "Graphics Compositor";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } = [CompositorDocument.Extension];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(CompositorDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every compositor this editor opens, because a node library is a property
        // of the build rather than of a file — and a registry per document would mean two open
        // compositors disagreeing about what a node type is.
        return new CompositorDocument(request.Project, request.Asset, request.Path, registry);
    }
}

/// <summary>What <see cref="CompositorEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
public sealed class CompositorEditorView : DocumentView<CompositorDocument> {
    /// <inheritdoc />
    protected override UiElement Build(CompositorDocument document, UiElement panel) {
        var view = panel.Add<CompositorView>();
        view.Show(document);

        return view;
    }
}
