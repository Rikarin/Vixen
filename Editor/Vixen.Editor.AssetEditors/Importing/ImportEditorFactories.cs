// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui.Documents;
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
public sealed class TextureEditorFactory : DocumentKind {
    /// <inheritdoc />
    public override string Name => "Texture";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } =
        [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif", ".hdr", ".ktx2", ".dds"];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(TextureImportDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new TextureImportDocument(request.Project, request.Asset, request.Path);
    }
}

/// <summary>What <see cref="TextureEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
public sealed class TextureEditorView : DocumentView<TextureImportDocument> {
    /// <inheritdoc />
    protected override UiElement Build(TextureImportDocument document, UiElement panel) {
        // ⚠ `ImportSettingsView` scrolls its foldouts and deliberately leaves the "unknown settings"
        // alert outside the scroller, because a warning that scrolls away is a warning nobody sees
        // twice. A panel that scrolled the whole view would undo exactly that.
        DockPanel.Fills(panel);

        var view = panel.Add<TextureImportView>();
        view.Show(document);

        return view;
    }
}

/// <summary>Opens a model.</summary>
/// <inheritdoc cref="TextureEditorFactory" path="/remarks" />
public sealed class ModelEditorFactory : DocumentKind {
    /// <inheritdoc />
    public override string Name => "Model";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } =
        [".fbx", ".gltf", ".glb", ".obj", ".dae", ".3ds", ".ply", ".stl", ".blend"];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(ModelImportDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new ModelImportDocument(request.Project, request.Asset, request.Path);
    }
}

/// <summary>What <see cref="ModelEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
public sealed class ModelEditorView : DocumentView<ModelImportDocument> {
    /// <inheritdoc />
    protected override UiElement Build(ModelImportDocument document, UiElement panel) {
        // An `ImportSettingsView` of its own — see `TextureEditorFactory.CreateView`.
        DockPanel.Fills(panel);

        var view = panel.Add<ModelImportView>();
        view.Show(document);

        return view;
    }
}
