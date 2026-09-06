// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.Texturing;

/// <summary>What claims <c>.vxtexgraph</c>, so a double-click opens one.</summary>
/// <remarks>
///     <para>
///         <b>The first asset editor registered by a plugin rather than by the application.</b>
///         Every other <c>IAssetEditorFactory</c> in this build is in
///         <c>StandardEditors.CreateDefault</c>, registered once for the life of the process; this
///         one arrives with a module and leaves with it, which is what
///         <a href="https://github.com/Rikarin/Vixen/issues/739">#739</a> had to be fixed for. Until
///         <c>AssetEditorRegistry.Add</c> handed back a removal, registering here was a reference
///         from the editor into the plugin with no way to take it out — not a leaked entry, a leaked
///         assembly.
///     </para>
///     <para>
///         ⚠ <b>The document is the project's, and this may be asked for one that is already
///         open.</b> <c>AssetEditorRegistry.TryOpen</c> checks the project first, so this runs only
///         for an asset nothing is editing — which is why the constructor below may register with
///         the project unconditionally.
///     </para>
///     <para>
///         ⚠ <b><see cref="CreateView" /> runs again every time the panel is reopened</b>, so
///         nothing durable lives in the view. What survives a reopen is on the document, and the
///         picture is the module's — a view built here has no graphics service to evaluate with, and
///         a factory that took one would be a second route to the device beside the panel's.
///     </para>
///     <para>
///         ⚠ <b>And what it says about that used to be false</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/841">#841</a>. It passed
///         <c>NoGraphics</c>, whose sentence is "this host publishes no IEditorGraphics" — and a
///         double-click happens in the editor, which publishes one. The sibling
///         <see cref="LayerStackEditorFactory" /> was corrected by
///         <a href="https://github.com/Rikarin/Vixen/issues/831">#831</a> and this one was not,
///         because this file was not that slice's.
///     </para>
/// </remarks>
sealed class TextureGraphEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Texture Graph";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [TextureGraphDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new TextureGraphDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(panel);

        if (document is not TextureGraphDocument graph) {
            // Not a defence against a caller, a defence against this factory: the registry hands back
            // whatever `Open` returned, so a mismatch here means the two halves of one registration
            // have drifted apart — which is the thing `IAssetEditorFactory` puts them in one type to
            // prevent.
            throw new ArgumentException(
                $"A {nameof(TextureGraphEditorFactory)} was handed a {document.GetType().Name} to build a view over.",
                nameof(document)
            );
        }

        var view = new TextureGraphView(panel);

        // ⚠ No picture here, and the pane says why in the one sentence that is true of it. A tab
        // opened by a double-click shows the canvas; the preview is the module's panel, which is the
        // thing holding the evaluator.
        //
        // ⚠ `NoGraphics` was a lie in this exact position and it took a batch to notice — #841. A
        // double-click happens in the editor and the editor publishes `IEditorGraphics`, so "this
        // host publishes no IEditorGraphics" sent the one reader who could act on it to look for a
        // plugin point that is already there. `AnotherPane` is a fact about this view rather than
        // about the host, which is why no `TexturePreview.Blocking` overload can return it.
        view.Show(graph, TexturePreviewBlocker.AnotherPane);

        return view.Root;
    }
}
