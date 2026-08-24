// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.VfxGraph;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Vfx;

/// <summary>An effect, open for editing: the graph, the selected block, and the effect running.</summary>
/// <remarks>
///     <para>
///         Doc 11's row for this asset is "node graph + live preview viewport", and the three
///         columns here are that: the canvas, which is <see cref="NodeGraphView" />'s and is already
///         undoable in every gesture; the selected block's numbers, which are
///         <see cref="NodeInspector" />'s; and the effect itself.
///     </para>
///     <para>
///         ⚠ <b>Compiling and previewing are one button, not two.</b> A preview over a stale artefact
///         is the failure mode that wastes an afternoon — the author changes a block, watches the old
///         effect, and concludes the block does nothing. So <see cref="Compile" /> is what produces
///         both, and the preview pane draws whatever the last compile left behind.
///     </para>
///     <para>
///         The panel is <c>VfxGraphView.vxml</c>; this file is the accessibility modifier and the
///         factory, on the arrangement <c>FactRow</c>, <c>NodeInspector</c> and <c>CodeEditorView</c>
///         use. The emitter's partial carries no modifier and this type is handed out by
///         <see cref="VfxEditorFactory.CreateView" />.
///     </para>
/// </remarks>
public sealed partial class VfxGraphView;

/// <summary>Opens a VFX graph.</summary>
/// <remarks>
///     ⚠ <b>In this assembly rather than in <c>Vixen.Editor.VfxGraph</c>, which is where doc 20's
///     B5 files the row.</b> That assembly is the node library and the compiler, and it deliberately
///     knows nothing about a project, a document or a panel — which is what lets its tests compile a
///     graph with no editor in the way. The document and the view are the same shape every other row
///     of doc 11's asset-editor table has, and this is the assembly that holds them.
/// </remarks>
public sealed class VfxEditorFactory : IAssetEditorFactory {
    readonly NodeTypeRegistry registry = VfxNodeLibrary.Create();

    /// <inheritdoc />
    public string Name => "VFX Graph";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [VfxDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every effect this editor opens, for `VfxNodeLibrary`'s reason.
        return new VfxDocument(request.Project, request.Asset, request.Path, registry);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // A node canvas with its own pan and zoom — see `ShaderGraphView.CreateView`. The editor also
        // lays out as a row, so a vertical scroll over it would have nothing to say.
        DockPanel.Fills(panel);

        var view = panel.Add<VfxGraphView>();
        view.Show((VfxDocument) document);

        return view;
    }
}
