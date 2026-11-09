// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Interchange;

/// <summary>A material as a written file names it: what it is called, and its numbers.</summary>
/// <param name="Name">What it is called, which is what a reader matches it by.</param>
/// <param name="Factors">Its metallic-roughness numbers.</param>
public sealed record ModelWriterMaterial(string Name, ModelMaterialFactors Factors) {
    /// <summary>The images it reads, or null for a material of numbers alone.</summary>
    public ModelWriterTextures? Textures { get; init; }
}

/// <summary>How a written material's alpha is read, which is glTF's <c>alphaMode</c>.</summary>
public enum ModelAlphaMode {
    /// <summary>Alpha is ignored and the surface is opaque.</summary>
    Opaque,

    /// <summary>A texel is drawn or not, by alpha against a cutoff.</summary>
    Mask,

    /// <summary>Alpha blends the surface over what is behind it.</summary>
    Blend
}

/// <summary>
///     The images a written material reads, by glTF's metallic-roughness slots, each a URI relative to the file.
///     Any may be absent.
/// </summary>
/// <remarks>
///     ⚠ <b>glTF's channels, as <see cref="ModelMaterialTextures" /> reads them.</b> Roughness is in green and
///     metallic in blue of <see cref="MetallicRoughness" />, occlusion in red of <see cref="Occlusion" />, and a
///     normal's green points up the image. Base colour and emissive are sRGB, the rest linear. The writer does not
///     look inside an image: what a caller names is what a reader is told it holds.
/// </remarks>
public sealed record ModelWriterTextures {
    /// <summary>The base colour, with alpha in its fourth lane when it has one.</summary>
    public string? BaseColor { get; init; }

    /// <summary>Roughness in green and metallic in blue.</summary>
    public string? MetallicRoughness { get; init; }

    /// <summary>The tangent-space normal, green up.</summary>
    public string? Normal { get; init; }

    /// <summary>Occlusion in red.</summary>
    public string? Occlusion { get; init; }

    /// <summary>The emitted colour.</summary>
    public string? Emissive { get; init; }

    /// <summary>What a stored normal's X and Y are multiplied by.</summary>
    public float NormalScale { get; init; } = 1f;

    /// <summary>How much of the occlusion is applied, from 0 to 1.</summary>
    public float OcclusionStrength { get; init; } = 1f;

    /// <summary>What the emissive image is multiplied by; one when there is an image and none is given.</summary>
    public Vector3? EmissiveFactor { get; init; }

    /// <summary>
    ///     How much brighter than one the emission is, written as <c>KHR_materials_emissive_strength</c> when it is
    ///     more than one.
    /// </summary>
    public float EmissiveStrength { get; init; } = 1f;

    /// <summary>How alpha is read.</summary>
    public ModelAlphaMode AlphaMode { get; init; } = ModelAlphaMode.Opaque;

    /// <summary>The alpha below which <see cref="ModelAlphaMode.Mask" /> draws nothing.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Whether both faces are drawn.</summary>
    public bool DoubleSided { get; init; }
}
