// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Shading;

/// <summary>A shader graph, open for editing: the canvas, the generated Raven, and what it said.</summary>
/// <remarks>
///     <para>
///         Doc 11's row for this asset is a node graph with "show generated code", and the three
///         columns here are that: the canvas, which is <see cref="NodeGraphView" />'s and is already
///         undoable in every gesture; the emitted shader, in a read-only
///         <see cref="CodeEditor" /> with the same Raven highlighting the shader editor uses; and the
///         selected node's settings with the compile's complaints under them.
///     </para>
///     <para>
///         ⚠ <b>The generated source is read-only and hidden until asked for.</b> Editing it would be
///         editing an artefact — the next compile overwrites it, so a panel that let you type into it
///         would be one that quietly threw work away. Hidden by default because a graph is what the
///         author is looking at; the toggle is what doc 07 calls "show generated code", and it is
///         beside the button that produces it rather than in a menu.
///     </para>
///     <para>
///         ⚠ <b>Two lists of complaints, drawn as one.</b> The graph compiler's name a node, Raven's
///         name a line of the generated text, and an author wants to know about both without being
///         asked to look in two places. Which kind each is stays visible in the stage column —
///         <c>graph</c> against one and <c>raven</c> against the other — because the action they call
///         for is different: one is a wire, the other is a bug in this editor.
///     </para>
///     <para>
///         ⚠ <b>A property node's name is edited here rather than in the node inspector.</b> The
///         inspector draws <i>ports</i>, and a property's name is not one — it names a binding, so it
///         is a graph text. The row writes through <c>SetPortTextCommand</c> for the reason every
///         other field in this assembly does: typing a name is one undo entry, not one per keystroke.
///     </para>
///     <para>
///         The panel is <c>ShaderGraphView.vxml</c>; this file is the accessibility modifier and
///         the factory. The emitter's partial carries no modifier and this type is handed out by
///         <see cref="ShaderGraphEditorFactory.CreateView" />.
///     </para>
/// </remarks>
public sealed partial class ShaderGraphView;

/// <summary>Opens a shader graph.</summary>
/// <remarks>
///     ⚠ <b>In this assembly rather than in <c>Vixen.Editor.ShaderGraph</c></b>, which is where doc
///     20's B5 files the row. That assembly is the node library and the compiler, and it deliberately
///     knows nothing about a project, a document or a panel — the same split
///     <c>VfxEditorFactory</c> states, and the reason the graph compiler can be tested with no editor
///     in the way.
/// </remarks>
public sealed class ShaderGraphEditorFactory : IAssetEditorFactory {
    readonly NodeTypeRegistry registry = ShaderNodeLibrary.Create();

    /// <inheritdoc />
    public string Name => "Shader Graph";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [ShaderGraphDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every graph this editor opens, for `ShaderNodeLibrary`'s reason.
        return new ShaderGraphDocument(request.Project, request.Asset, request.Path, registry);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ A node canvas pans and zooms in a space of its own and converts a pointer with
        // `Surface.AbsoluteLeft`, so a panel that also scrolled would be a second, invisible transform
        // between the cursor and the graph. The generated-source pane beside it is a `CodeEditor`,
        // which virtualises its own scrolling from its own viewport height.
        DockPanel.Fills(panel);

        var view = panel.Add<ShaderGraphView>();
        view.Show((ShaderGraphDocument) document);

        return view;
    }
}
