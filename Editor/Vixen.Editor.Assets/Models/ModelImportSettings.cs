// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Models;

/// <summary>How one model is imported.</summary>
/// <remarks>
///     Four settings, and each of them answers something the file cannot. Which axis is up, how the
///     material tree is wired and what the LODs should be are all decisions with better homes — the
///     first in the authoring tool, the second in a material asset, the third in the compiler that
///     sees the whole model.
/// </remarks>
[DataContract("ModelImporter")]
public sealed record ModelImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>What to multiply every length by.</summary>
    /// <remarks>
    ///     The setting nobody escapes. An FBX out of Max or Maya is in centimetres, a glTF is in
    ///     metres, and a scene mixing the two has one of them a hundred times too big. Applied to
    ///     vertex positions <em>and</em> to node translations, so a model scales exactly once however
    ///     deep its hierarchy is.
    /// </remarks>
    public float Scale { get; init; } = 1f;

    /// <summary>Whether to compute normals for meshes that have none.</summary>
    /// <remarks>
    ///     Only for meshes that have none. Recomputing normals an artist authored would throw away
    ///     the hand-adjusted shading that is most of what makes a hard-surface model read correctly.
    /// </remarks>
    public bool GenerateNormals { get; init; } = true;

    /// <summary>Whether to compute tangents for meshes that have UVs and no tangents.</summary>
    /// <remarks>
    ///     Needs texture coordinates, because a tangent frame is defined by how the UVs run across
    ///     the surface. A mesh with no UVs gets none and no complaint — it has nothing to normal-map.
    /// </remarks>
    public bool GenerateTangents { get; init; } = true;

    /// <summary>Whether to import the animation clips the file carries.</summary>
    /// <remarks>
    ///     On by default and worth turning off per asset. A character exported once per animation
    ///     ships the same skeleton and the same clip in every file, and the duplicates are dead
    ///     weight in the bundle.
    /// </remarks>
    public bool ImportAnimations { get; init; } = true;
}
