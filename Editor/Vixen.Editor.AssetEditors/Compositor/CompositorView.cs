// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
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
///         <see cref="CompositorEditorFactory.CreateView" />.
///     </para>
/// </remarks>
public sealed partial class CompositorView;

/// <summary>Opens a graphics compositor.</summary>
public sealed class CompositorEditorFactory : IAssetEditorFactory {
    readonly NodeTypeRegistry registry = CompositorGraphCompiler.CreateRegistry();

    /// <inheritdoc />
    public string Name => "Graphics Compositor";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [CompositorDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every compositor this editor opens, because a node library is a property
        // of the build rather than of a file — and a registry per document would mean two open
        // compositors disagreeing about what a node type is.
        return new CompositorDocument(request.Project, request.Asset, request.Path, registry);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<CompositorView>();
        view.Show((CompositorDocument) document);

        return view;
    }
}
