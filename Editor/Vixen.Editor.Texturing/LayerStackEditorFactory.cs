// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;

namespace Vixen.Editor.Texturing;

/// <summary>What claims <c>.vxlayers</c>, so a double-click opens one.</summary>
/// <remarks>
///     <para>
///         <b>The second half of <a href="https://github.com/Rikarin/Vixen/issues/806">#806</a>.</b>
///         <c>TexturingModule</c> registered a kind, a factory and a panel for
///         <c>.vxtexgraph</c> and none of the three for <c>.vxlayers</c>, so
///         <c>LayerStackDocument</c>, <c>LayerStackExplode</c>, <c>LayerPaint</c> and
///         <c>LayerStackYaml</c> — three and a half thousand lines — could be reached by nothing a
///         person does. This is the type a double-click lands on.
///     </para>
///     <para>
///         ⚠ <b>Registered inside the module's scope and given back on unload</b>, exactly as
///         <see cref="TextureGraphEditorFactory" /> is and for
///         <a href="https://github.com/Rikarin/Vixen/issues/739">#739</a>'s reason: an
///         <c>IAssetEditorFactory</c> the host cannot take out again is a reference from the editor
///         into the plugin with no way to remove it — not a leaked entry, a leaked assembly.
///     </para>
///     <para>
///         ⚠ <b><see cref="CreateView" /> shows the rows and no picture, and that is not the same
///         gap the graph's factory has.</b> A view built here has no <c>IEditorGraphics</c> — the
///         evaluator is the module's, because two of them over one device would be two pipeline
///         caches — so a tab opened by a double-click lists the stack and says so, and the panel the
///         module opens is where the map appears.
///     </para>
/// </remarks>
sealed class LayerStackEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Layer Stack";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [LayerStackDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new LayerStackDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(panel);

        if (document is not LayerStackDocument stack) {
            // Not a defence against a caller, a defence against this factory: the registry hands back
            // whatever `Open` returned, so a mismatch here means the two halves of one registration
            // have drifted apart — which is what `IAssetEditorFactory` puts them in one type to
            // prevent.
            throw new ArgumentException(
                $"A {nameof(LayerStackEditorFactory)} was handed a {document.GetType().Name} to build a view over.",
                nameof(document)
            );
        }

        var view = new LayerStackView(panel);

        view.Show(stack);

        return view.Root;
    }
}
