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

    /// <summary>A clear coat, a sheen and transmission over the core material, or null for none.</summary>
    public ModelWriterLayers? Layers { get; init; }
}

/// <summary>
///     What glTF's <c>KHR_materials_clearcoat</c>, <c>KHR_materials_sheen</c> and <c>KHR_materials_transmission</c>
///     put over a written material: each layer's factors and the images that multiply them, as URIs relative to the
///     file. A layer whose factor leaves the material alone is not written.
/// </summary>
/// <remarks>
///     ⚠ <b>glTF's channels, as each extension reads them.</b> The coat is in red of <see cref="Clearcoat" />, its
///     roughness in green of <see cref="ClearcoatRoughness" />, the sheen's roughness in alpha of
///     <see cref="SheenRoughness" /> and transmission in red of <see cref="Transmission" />; the sheen colour is sRGB
///     and the rest linear. glTF's sheen has no weight of its own, so a caller with one multiplies it into the colour.
/// </remarks>
public sealed record ModelWriterLayers {
    /// <summary>How much clear coat lies over the material, from 0 to 1; none is written at 0.</summary>
    public float ClearcoatFactor { get; init; }

    /// <summary>The coat's weight in red, multiplied by <see cref="ClearcoatFactor" />.</summary>
    public string? Clearcoat { get; init; }

    /// <summary>How rough the coat is, from 0 to 1.</summary>
    public float ClearcoatRoughnessFactor { get; init; }

    /// <summary>The coat's roughness in green, multiplied by <see cref="ClearcoatRoughnessFactor" />.</summary>
    public string? ClearcoatRoughness { get; init; }

    /// <summary>The coat's own tangent-space normal, green up.</summary>
    public string? ClearcoatNormal { get; init; }

    /// <summary>The sheen's linear colour, its weight already in it; none is written while it is black.</summary>
    public Vector3 SheenColorFactor { get; init; }

    /// <summary>The sheen's colour, sRGB, multiplied by <see cref="SheenColorFactor" />.</summary>
    public string? SheenColor { get; init; }

    /// <summary>How rough the sheen is, from 0 to 1.</summary>
    public float SheenRoughnessFactor { get; init; }

    /// <summary>The sheen's roughness in alpha, multiplied by <see cref="SheenRoughnessFactor" />.</summary>
    public string? SheenRoughness { get; init; }

    /// <summary>How much light passes through the surface, from 0 to 1; none is written at 0.</summary>
    public float TransmissionFactor { get; init; }

    /// <summary>Transmission in red, multiplied by <see cref="TransmissionFactor" />.</summary>
    public string? Transmission { get; init; }
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
