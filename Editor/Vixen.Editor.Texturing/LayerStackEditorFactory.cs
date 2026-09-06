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
///         ⚠ <b><see cref="CreateView" /> shows the rows and no picture, and until
///         <a href="https://github.com/Rikarin/Vixen/issues/831">#831</a> the sentence above was the
///         opposite of what the code did.</b> It said "lists the stack and says so" and the call was
///         <c>view.Show(stack)</c> with no picture at all — so <c>LayerStackView</c> set
///         <c>status.Text</c> to the empty string and <c>Preview.Image</c> to zero, which is a
///         chequerboard under a blank line: the shape that says nothing about whether this host could
///         have drawn one. A view built here has no <c>IEditorGraphics</c> — the evaluator is the
///         module's, because two of them over one device would be two pipeline caches — and the
///         picture it is handed now says exactly that.
///     </para>
///     <para>
///         ⚠ <b><see cref="TexturePreviewBlocker.AnotherPane" /> and not <c>NoGraphics</c>.</b> A
///         double-click happens in the editor and the editor publishes graphics, so "this host
///         publishes no IEditorGraphics" is false in the one place this sentence is ever read.
///         <c>TextureGraphEditorFactory</c> passed <c>NoGraphics</c> for a batch after this one was
///         corrected, because it was another slice's file —
///         <a href="https://github.com/Rikarin/Vixen/issues/841">#841</a>, closed.
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

        // ⚠ A picture and not `Show(stack)`. `LayerStackView` writes `picture?.Status ?? ""` under the
        // pane and `picture?.Image?.Image ?? 0` into it, so a null one is a chequerboard under a blank
        // line — the exact shape `TexturingModule.RefreshStack` grew a fallback to avoid. The extent
        // is the stack's so the zoom and the pointer readout still mean texels; only the picture is
        // missing, and the line says which pane has it.
        view.Show(
            stack,
            new LayerStackPicture(
                null,
                LayerStackPreview.DefaultUsage,
                stack.Document.BaseWidth,
                stack.Document.BaseHeight,
                TexturePreview.Describe(TexturePreviewBlocker.AnotherPane)
            )
        );

        return view.Root;
    }
}
