// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>A model, open for editing: what came out of it, and the settings that decide what will.</summary>
/// <remarks>
///     <para>
///         <b>The part list is the sidecar's, not an import's.</b> A model is a model and also a
///         mesh per material, a skeleton and a clip per animation; the last import wrote all of them
///         into <c>subAssets</c>, so the list opens instantly and says what is actually addressable
///         today. Re-importing to populate a panel would put Assimp between a double-click and a
///         window.
///     </para>
///     <para>
///         ⚠ <b>An asset that has never been imported shows an empty list and says so</b>, rather
///         than showing nothing and letting it read as a model with no meshes in it. That state is
///         ordinary — a file just dropped into the project has it until the next import pass.
///     </para>
///     <para>
///         ⚠ <b>No LOD preview and no viewport.</b> Doc 11 asks for both. Drawing a mesh needs a
///         device and a render target, which is the application's — the same wall the texture
///         preview meets — and the LOD chain needs <c>ModelCompiler</c>, which doc 08 specifies and
///         nothing has written. When the levels exist they arrive as further sub-assets and this
///         list draws them without being changed.
///     </para>
/// </remarks>
public sealed class ModelImportView : Control {
    /// <inheritdoc />
    protected override string TagName => "model-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The meshes, the skeleton and the clips, grouped by what they are.</summary>
    public TreeView Parts { get; private set; } = null!;

    /// <summary>Shown when the asset has never been imported.</summary>
    public EmptyState Empty { get; private set; } = null!;

    /// <summary>The settings, the overrides and the addressable block.</summary>
    public ImportSettingsView SettingsView { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Parts = Part<TreeView>();

        Empty = Part<EmptyState>();
        Empty.AddClass("hidden");
        Empty.Title = "Not imported yet";

        Empty.Description = "Nothing has been produced from this file, so there is nothing to list. "
            + "Import the project and the meshes, skeleton and clips appear here.";

        SettingsView = Part<ImportSettingsView>();
    }

    /// <summary>Shows a model.</summary>
    /// <param name="model">The document.</param>
    public void Show(ModelImportDocument model) {
        ArgumentNullException.ThrowIfNull(model);

        SettingsView.Show(model);

        while (Parts.Root.Children.Count > 0) {
            Parts.Root.Remove(Parts.Root.Children[^1]);
        }

        if (model.SubAssets.Count == 0) {
            Empty.RemoveClass("hidden");
            Parts.SetStyle("display", "none");
            Parts.Refresh();

            return;
        }

        Empty.AddClass("hidden");
        Parts.SetStyle("display", "flex");

        // Grouped by kind and ordered by name inside each, rather than in the order the exporter
        // happened to list them: a character with forty clips is unreadable in export order and the
        // sub-asset ids are derived from names anyway, so name order is the stable one.
        foreach (var kind in model.SubAssets.Select(part => part.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            var group = Parts.Root.Add(kind, kind);

            foreach (var part in model.SubAssets.Where(entry => string.Equals(entry.Type, kind, StringComparison.Ordinal))
                         .OrderBy(entry => entry.Name, StringComparer.Ordinal)) {
                group.Add(part.Name, part);
            }

            Parts.Expand(group);
        }

        Parts.Refresh();
    }

    /// <summary>How many parts are listed.</summary>
    public int PartCount {
        get {
            var count = 0;

            foreach (var group in Parts.Root.Children) {
                count += group.Children.Count;
            }

            return count;
        }
    }

    /// <summary>The count, as the heading a caller may want to show.</summary>
    /// <returns>The text.</returns>
    public string Summary() => PartCount.ToString(CultureInfo.InvariantCulture) + " parts";
}
