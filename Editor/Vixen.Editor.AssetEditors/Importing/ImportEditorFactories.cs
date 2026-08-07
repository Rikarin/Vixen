// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>Opens a texture.</summary>
/// <remarks>
///     ⚠ <b>The extensions are <c>TextureImporter</c>'s, restated.</b> They have to be: an editor
///     that claimed a file the importer does not import would open a settings panel for settings
///     nothing reads. There is a test that compares the two lists, which is the only arrangement
///     where adding a format to the importer cannot quietly leave it uneditable.
/// </remarks>
public sealed class TextureEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Texture";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } =
        [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif", ".hdr", ".ktx2"];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new TextureImportDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ `ImportSettingsView` scrolls its foldouts and deliberately leaves the "unknown settings"
        // alert outside the scroller, because a warning that scrolls away is a warning nobody sees
        // twice. A panel that scrolled the whole view would undo exactly that.
        DockPanel.Fills(panel);

        var view = panel.Add<TextureImportView>();
        view.Show((TextureImportDocument) document);

        return view;
    }
}

/// <summary>Opens a model.</summary>
/// <inheritdoc cref="TextureEditorFactory" path="/remarks" />
public sealed class ModelEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Model";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } =
        [".fbx", ".gltf", ".glb", ".obj", ".dae", ".3ds", ".ply", ".stl", ".blend"];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new ModelImportDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // An `ImportSettingsView` of its own — see `TextureEditorFactory.CreateView`.
        DockPanel.Fills(panel);

        var view = panel.Add<ModelImportView>();
        view.Show((ModelImportDocument) document);

        return view;
    }
}
