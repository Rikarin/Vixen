// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>The editable mirror of <see cref="ModelImportSettings" />.</summary>
/// <inheritdoc cref="TextureImportEdits" path="/remarks" />
[DataContract("ModelImportEdits")]
public sealed class ModelImportEdits {
    /// <summary>What to multiply every length by.</summary>
    [Inspector]
    [Tooltip("An FBX out of Max is centimetres and a glTF is metres. Applied to positions and to node translations.")]
    public float Scale { get; set; } = 1f;

    /// <summary>Whether to compute normals for meshes that have none.</summary>
    [Inspector]
    [Tooltip("Only for meshes that have none — recomputing authored normals throws away the shading.")]
    public bool GenerateNormals { get; set; } = true;

    /// <summary>Whether to compute tangents for meshes that have UVs and no tangents.</summary>
    [Inspector]
    [Tooltip("Needs texture coordinates. A mesh with no UVs gets none and no complaint.")]
    public bool GenerateTangents { get; set; } = true;

    /// <summary>Whether to import the animation clips the file carries.</summary>
    [Inspector]
    [Tooltip("Off for a character exported once per animation, where every file repeats the same skeleton.")]
    public bool ImportAnimations { get; set; } = true;
}

/// <summary>A model's import settings, open for editing.</summary>
/// <remarks>
///     <para>
///         The part list doc 11 asks for — a mesh per material, a skeleton, a clip per animation —
///         is <see cref="ImportSettingsDocument.SubAssets" />, read out of the sidecar rather than by
///         re-importing. That is the difference between opening a model and waiting for Assimp.
///     </para>
///     <para>
///         ⚠ <b>No LOD preview, and the honest reason is that there are no LODs yet.</b> Doc 08 puts
///         LOD generation in <c>ModelCompiler</c>, which does not exist — the importer produces one
///         mesh per material and nothing generates a chain. When it does, the levels arrive here as
///         further sub-assets and the list draws them without being told.
///     </para>
/// </remarks>
public sealed class ModelImportDocument : ImportSettingsDocument {
    /// <summary>The settings, typed.</summary>
    public ModelImportEdits Model => (ModelImportEdits) Settings;

    /// <inheritdoc />
    protected override Type SettingsType => typeof(ModelImportEdits);

    /// <inheritdoc />
    protected override string ImporterTag => "ModelImporter";

    /// <summary>Opens a model's import settings.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public ModelImportDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, path) {
    }
}
