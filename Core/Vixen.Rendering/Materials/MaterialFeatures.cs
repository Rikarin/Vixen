// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Shaders;

namespace Vixen.Rendering.Materials;

/// <summary>The permutation keys the material model sets.</summary>
/// <remarks>
///     <para>
///         A material sets a permutation's <em>value</em>; whether it reaches the effect key is the
///         host's, through <see cref="Features.MaterialRenderFeature.PermutationKeys" /> — a key the
///         shader does not branch on must not be in the key, or the cache splits for variants that
///         compile to the same bytes. So a host that draws layered materials adds
///         <see cref="LayerCount" /> to the list it registers for that pass.
///     </para>
///     <para>
///         Per shader rather than one shared key, because that is how every other key in the engine
///         is named: the generator emits <c>ForwardPlus.MaxLights</c> from <c>ForwardPlus</c>'s own
///         reflection, and a key that did not match it would be a second name for one uniform.
///     </para>
/// </remarks>
public static class MaterialKeys {
    /// <summary>The default <c>MaterialLayersSurface</c> declares.</summary>
    public const int DefaultLayerCount = 2;

    /// <summary>How many layers a layered material blends, for one shading pass.</summary>
    public static PermutationKey<int> LayerCount(string shaderName) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);
        return ParameterKeys.NewPermutation(DefaultLayerCount, $"{shaderName}.LayerCount");
    }
}

/// <summary>
///     The metalness/roughness workflow: one base colour, one metalness, one roughness.
/// </summary>
/// <remarks>
///     The industry default, and the reason it is: a conductor's diffuse response is zero and its
///     specular response is tinted, while a dielectric is the other way round, so one parameter
///     selects between the two ends and the shader derives both channels from it.
/// </remarks>
[DataContract("MetalRoughness")]
public sealed record MetalRoughnessFeature : IMaterialFeature {
    /// <summary>Albedo for a dielectric, specular tint for a conductor. Linear.</summary>
    public Vector3 BaseColor { get; init; } = Vector3.One;

    /// <summary>How much of a conductor this is, 0..1.</summary>
    public float Metalness { get; init; }

    /// <summary>Perceptual roughness, as authored.</summary>
    public float Roughness { get; init; } = 0.5f;

    /// <inheritdoc />
    public string ShaderName => "MetalRoughnessSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("baseColor", BaseColor);
        context.Set("metalness", Metalness);
        context.Set("roughness", Roughness);
    }
}

/// <summary>
///     The specular/glossiness workflow, for content authored before metalness won.
/// </summary>
/// <remarks>
///     Kept because converting an asset library is lossy in the other direction: a specular colour
///     can express reflectances a single metalness cannot.
/// </remarks>
[DataContract("SpecularGlossiness")]
public sealed record SpecularGlossinessFeature : IMaterialFeature {
    /// <summary>Diffuse albedo, linear.</summary>
    public Vector3 DiffuseColor { get; init; } = Vector3.One;

    /// <summary>Reflectance at normal incidence, linear.</summary>
    public Vector3 SpecularColor { get; init; } = new(0.04f, 0.04f, 0.04f);

    /// <summary>How polished the surface is — the complement of roughness.</summary>
    public float Glossiness { get; init; } = 0.5f;

    /// <inheritdoc />
    public string ShaderName => "SpecularGlossinessSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("diffuseColor", DiffuseColor);
        context.Set("specularColor", SpecularColor);
        context.Set("glossiness", Glossiness);
    }
}

/// <summary>Replaces the shading normal from a tangent-space normal.</summary>
[DataContract("NormalMap")]
public sealed record NormalMapFeature : IMaterialFeature {
    /// <summary>The tangent-space normal. Unit +Z is flat.</summary>
    public Vector3 NormalTS { get; init; } = new(0f, 0f, 1f);

    /// <summary>How far the normal is bent, where 1 is as authored.</summary>
    public float Strength { get; init; } = 1f;

    /// <inheritdoc />
    public string ShaderName => "NormalMapSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("normalTS", NormalTS);
        context.Set("strength", Strength);
    }
}

/// <summary>Adds emitted radiance, unaffected by lighting.</summary>
[DataContract("Emissive")]
public sealed record EmissiveFeature : IMaterialFeature {
    /// <summary>The colour emitted, linear.</summary>
    public Vector3 EmissiveColor { get; init; } = Vector3.One;

    /// <summary>What it is multiplied by, so colour and brightness are authored apart.</summary>
    public float Intensity { get; init; } = 1f;

    /// <inheritdoc />
    public string ShaderName => "EmissiveSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("emissiveColor", EmissiveColor);
        context.Set("intensity", Intensity);
    }
}

/// <summary>Applies baked ambient occlusion to indirect diffuse.</summary>
[DataContract("Occlusion")]
public sealed record OcclusionFeature : IMaterialFeature {
    /// <summary>How much light reaches this point, 0..1.</summary>
    public float OcclusionMap { get; init; } = 1f;

    /// <summary>How much of the occlusion is applied.</summary>
    public float Strength { get; init; } = 1f;

    /// <inheritdoc />
    public string ShaderName => "OcclusionSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("occlusionMap", OcclusionMap);
        context.Set("strength", Strength);
    }
}

/// <summary>
///     Stretches the specular lobe along the tangent: brushed metal, vinyl, satin.
/// </summary>
/// <remarks>
///     Writes a channel that only <see cref="AnisotropicShading" /> reads. A material with this
///     feature and the standard model compiles and looks isotropic, which is why the compiler pairs
///     the two rather than leaving it to whoever authored the material.
/// </remarks>
[DataContract("Anisotropy")]
public sealed record AnisotropyFeature : IMaterialFeature {
    /// <summary>How far the lobe stretches, -1..1. The sign is which axis.</summary>
    public float Anisotropy { get; init; } = 0.5f;

    /// <inheritdoc />
    public string ShaderName => "AnisotropySurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);
        context.Set("anisotropy", Anisotropy);
    }
}

/// <summary>A clear coat over the base surface: car paint, lacquered wood, wet surfaces.</summary>
[DataContract("ClearCoat")]
public sealed record ClearCoatFeature : IMaterialFeature {
    /// <summary>How strong the coat is, 0..1.</summary>
    public float ClearCoat { get; init; } = 1f;

    /// <summary>The coat's own perceptual roughness.</summary>
    public float ClearCoatRoughness { get; init; } = 0.1f;

    /// <inheritdoc />
    public string ShaderName => "ClearCoatSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("clearCoat", ClearCoat);
        context.Set("clearCoatRoughness", ClearCoatRoughness);
    }
}

/// <summary>Bends the coat's normal without touching the base's — scratches in the lacquer.</summary>
[DataContract("ClearCoatNormalMap")]
public sealed record ClearCoatNormalMapFeature : IMaterialFeature {
    /// <summary>The coat's tangent-space normal.</summary>
    public Vector3 ClearCoatNormalTS { get; init; } = new(0f, 0f, 1f);

    /// <summary>How far it is bent.</summary>
    public float ClearCoatNormalStrength { get; init; } = 1f;

    /// <inheritdoc />
    public string ShaderName => "ClearCoatNormalMapSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("clearCoatNormalTS", ClearCoatNormalTS);
        context.Set("clearCoatNormalStrength", ClearCoatNormalStrength);
    }
}

/// <summary>The retroreflective rim fabric has: velvet, satin, brushed cloth.</summary>
[DataContract("Sheen")]
public sealed record SheenFeature : IMaterialFeature {
    /// <summary>The sheen's colour, usually a different hue from the base.</summary>
    public Vector3 SheenColor { get; init; } = new(0.2f, 0.2f, 0.2f);

    /// <summary>How wide the rim is.</summary>
    public float SheenRoughness { get; init; } = 0.3f;

    /// <inheritdoc />
    public string ShaderName => "SheenSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("sheenColor", SheenColor);
        context.Set("sheenRoughness", SheenRoughness);
    }
}

/// <summary>Light that travels through the surface: skin, wax, leaves, thin cloth.</summary>
[DataContract("Subsurface")]
public sealed record SubsurfaceFeature : IMaterialFeature {
    /// <summary>What colour light takes on as it scatters through.</summary>
    public Vector3 ScatterColor { get; init; } = new(0.8f, 0.3f, 0.2f);

    /// <summary>How far light has to travel through the surface, in metres.</summary>
    public float Thickness { get; init; } = 0.5f;

    /// <inheritdoc />
    public string ShaderName => "SubsurfaceSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("scatterColor", ScatterColor);
        context.Set("thickness", Thickness);
    }
}

/// <summary>One layer of a layered material.</summary>
/// <param name="BaseColor">Albedo, linear.</param>
/// <param name="Metalness">How much of a conductor this layer is.</param>
/// <param name="Roughness">Perceptual roughness.</param>
/// <param name="Weight">How much of the layer shows, before normalisation — a splat channel.</param>
[DataContract("MaterialLayer")]
public readonly record struct MaterialLayerValue(
    Vector3 BaseColor,
    float Metalness,
    float Roughness,
    float Weight
);

/// <summary>
///     N metal-roughness layers, blended by weight: rock under moss under snow.
/// </summary>
/// <remarks>
///     <para>
///         Layering by array rather than by composition, and the reason is the one constraint the
///         whole feature model is shaped around: a composed shader's parameters belong to its type,
///         so two composed metal-roughness layers would share one base colour. An array gives each
///         layer values of its own, and <c>LayerCount</c> is a permutation so a two-layer material's
///         constant buffer holds two layers rather than the most anyone might use.
///     </para>
///     <para>
///         Where the weights come from is the caller's: a splat map, vertex colour, a height blend.
///         What arrives here is the result.
///     </para>
/// </remarks>
[DataContract("MaterialLayers")]
public sealed record MaterialLayersFeature : IMaterialFeature {
    /// <summary>The layers, innermost first.</summary>
    public IReadOnlyList<MaterialLayerValue> Layers { get; init; } = [];

    /// <inheritdoc />
    public string ShaderName => "MaterialLayersSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        // At least one, because `LayerCount` sizes an array and a zero-length one is a shader that
        // does not compile — where an empty layer list is an unfinished material, which is a thing
        // an editor has on screen all the time.
        var count = Math.Max(Layers.Count, 1);
        context.SetPermutation("LayerCount", MaterialKeys.DefaultLayerCount, count);

        for (var i = 0; i < Layers.Count; i++) {
            var layer = Layers[i];

            // Indexed, because a key holds one value. Raven's reflection describes the array once —
            // `layers[].baseColor`, an offset and a stride — and an effect expands that into an entry
            // per element; see Effect.Parameters. A key named after the collapsed form would be one
            // value for every layer, which is a layered material that draws its first layer only.
            context.Set($"layers[{i}].baseColor", layer.BaseColor);
            context.Set($"layers[{i}].metalness", layer.Metalness);
            context.Set($"layers[{i}].roughness", layer.Roughness);
            context.Set($"layers[{i}].weight", layer.Weight);
        }
    }
}

/// <summary>
///     Two different surfaces, mixed by a weight.
/// </summary>
/// <remarks>
///     The heterogeneous half of layering — a metal-roughness base under a specular-glossiness
///     overlay. Both sides must use different features, which the compiler checks: two layers of the
///     same feature would share its parameters and be one layer twice.
///     <see cref="MaterialLayersFeature" /> is what repeated layers want.
/// </remarks>
[DataContract("Blend")]
public sealed record BlendFeature : IMaterialFeature {
    /// <summary>The layer underneath.</summary>
    public IMaterialFeature? Under { get; init; }

    /// <summary>The layer on top.</summary>
    public IMaterialFeature? Over { get; init; }

    /// <summary>How much of <see cref="Over" /> shows, 0..1.</summary>
    public float Weight { get; init; } = 0.5f;

    /// <inheritdoc />
    public string ShaderName => "BlendSurface";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("blend", Weight);

        if (Under is not null) {
            context.Compose("under", Under);
        }

        if (Over is not null) {
            context.Compose("over", Over);
        }
    }
}
