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
///     The metalness/roughness workflow with a base-colour map.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The first material feature that samples a texture</strong>, which doc 06 records
///         as a gap for as long as a feature had no way to name one: sampling needs a binding index
///         only the compiled shader knows, and a feature is composed into a shader it has never seen.
///     </para>
///     <para>
///         It carries no texture handle. What it carries is a <em>name</em> — the parameter the
///         host writes a table slot into — and the texture itself is assigned on the material, paired
///         to that name through <c>MaterialRenderFeature.TextureIndices</c>. That indirection is the
///         whole of what "materials are values, not resources" means here: this record is data that
///         serialises, and nothing in it is a handle to something on a device.
///     </para>
///     <para>
///         Needs a device with <c>HasBindless</c> and a host that gave the material feature a
///         <c>BindlessTable</c>. Without one the index stays zero and every material samples the
///         table's fallback, so a project targeting GL or WebGL2 uses
///         <see cref="MetalRoughnessFeature" /> and tints instead — which is the same fork ADR-011
///         makes everywhere else.
///     </para>
/// </remarks>
[DataContract("TexturedMetalRoughness")]
public sealed record TexturedMetalRoughnessFeature : IMaterialFeature {
    /// <summary>Multiplied into the map, so a shared map can be tinted per material.</summary>
    public Vector3 BaseColor { get; init; } = Vector3.One;

    /// <summary>How much of a conductor this is, 0..1.</summary>
    public float Metalness { get; init; }

    /// <summary>Perceptual roughness, as authored.</summary>
    public float Roughness { get; init; } = 0.5f;

    /// <summary>What the material calls the base-colour map it wants sampled.</summary>
    /// <remarks>
    ///     A name rather than a handle, because a material is authored and serialised on machines
    ///     that have no device. The host pairs it with the shader parameter this feature declares —
    ///     see <see cref="BaseColorIndexParameter" /> — and the pairing is explicit for the reason
    ///     every other name-to-name join in the renderer is.
    /// </remarks>
    public string BaseColorMap { get; init; } = "baseColorMap";

    /// <inheritdoc />
    public string ShaderName => "TexturedMetalRoughnessSurface";

    /// <summary>What the shader calls the slot, under a composition path.</summary>
    /// <param name="path">
    ///     The qualified prefix the feature was composed under, as
    ///     <see cref="MaterialCompilationContext" /> builds it.
    /// </param>
    /// <remarks>
    ///     Exposed because a host has to name both halves of the pairing and only the compiler knows
    ///     the first: a feature's parameters belong to its composition path, so the same feature under
    ///     <c>CompositeSurface.slot0</c> and <c>CompositeSurface.slot1</c> is two parameters. A host
    ///     that wrote the name down would write down one of the two.
    /// </remarks>
    public static string BaseColorIndexParameter(string path) {
        ArgumentNullException.ThrowIfNull(path);
        return path + "baseColorIndex";
    }

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("baseColor", BaseColor);
        context.Set("metalness", Metalness);
        context.Set("roughness", Roughness);

        // Zero, and it stays zero unless a host with a table writes a slot over it. Slot zero is the
        // table's fallback view, so a material whose map never reached a table samples something
        // defined and visibly wrong rather than whatever the driver left in an unwritten descriptor.
        context.Set("baseColorIndex", 0u);
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

/// <summary>Replaces the shading normal from a tangent-space normal map.</summary>
/// <remarks>
///     <para>
///         What <see cref="NormalMapFeature" /> could not be for as long as a feature could not sample.
///         That one carries a single <see cref="NormalMapFeature.NormalTS" /> constant, which bends a
///         whole surface one way and is a normal map in name only — useful for a decal or a test and
///         nothing an artist authored.
///     </para>
///     <para>
///         Same seat as <see cref="TexturedMetalRoughnessFeature" /> and the same conditions: a name
///         rather than a handle, a device with <c>HasBindless</c>, and a host that paired the name
///         through <c>MaterialRenderFeature.TextureIndices</c>. Without the pairing the index stays
///         zero and the surface is shaded by the table's fallback read as a normal, which is why the
///         fallback being obviously not a surface matters twice over.
///     </para>
///     <para>
///         Composes beside a base surface rather than replacing one: it writes
///         <c>normalWS</c> and touches nothing else, so a material is a textured base colour
///         <em>and</em> a textured normal, which is the shape every other feature in the chain has.
///     </para>
/// </remarks>
[DataContract("TexturedNormalMap")]
public sealed record TexturedNormalMapFeature : IMaterialFeature {
    /// <summary>What the material calls the tangent-space normal map it wants sampled.</summary>
    /// <remarks>
    ///     ⚠ Not a name a material may choose freely, for <see cref="TexturedMetalRoughnessFeature" />
    ///     's reason: a host pairs one name with one name, keyed off this default, so a material that
    ///     renamed its map resolves nothing and takes slot zero.
    /// </remarks>
    public string NormalMap { get; init; } = "normalMap";

    /// <summary>How far the normal is bent, where 1 is as authored.</summary>
    public float Strength { get; init; } = 1f;

    /// <inheritdoc />
    public string ShaderName => "TexturedNormalMapSurface";

    /// <summary>What the shader calls the slot, under a composition path.</summary>
    /// <param name="path">
    ///     The qualified prefix the feature was composed under, as
    ///     <see cref="MaterialCompilationContext" /> builds it.
    /// </param>
    /// <remarks>
    ///     Exposed for <see cref="TexturedMetalRoughnessFeature.BaseColorIndexParameter" />'s reason:
    ///     only the compiler knows the path, and a host that wrote the name down would write down one
    ///     composition's answer.
    /// </remarks>
    public static string NormalIndexParameter(string path) {
        ArgumentNullException.ThrowIfNull(path);
        return path + "normalIndex";
    }

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("strength", Strength);

        // Zero until a host with a table writes a slot over it, and its presence here is what says
        // this material reads a map — MaterialRenderFeature.UnresolvedTextureCount counts the ones
        // that still say so after a level has settled.
        context.Set("normalIndex", 0u);
    }
}

/// <summary>Occlusion, roughness and metalness from one packed map — R, G and B in that order.</summary>
/// <remarks>
///     <para>
///         The packing every pipeline converged on, and it matters beyond tidiness: a streaming pool
///         is bounded in <em>pages</em>, so three maps per material would spend three times the
///         residency on the same information. <see cref="OcclusionFeature.OcclusionMap" /> is a
///         <c>float</c> — the whole of what a material could say about occlusion before this — and
///         roughness and metalness were constants on the base workflow.
///     </para>
///     <para>
///         ⚠ <b>It reads the base albedo back out of the surface, so the feature before it must leave
///         its own metalness at zero.</b> At metalness zero the base workflow writes the albedo into
///         <c>diffuseColor</c> untouched and <c>f0</c> at the dielectric constant, which is
///         recoverable; at any other value the albedo has already been split between the two channels
///         by a factor this cannot see. A material that sets metalness on its base feature
///         <em>and</em> supplies one from a map is asking two things for one channel — the map wins,
///         and what it multiplies is whatever the split left behind.
///     </para>
///     <para>
///         Same conditions as every other sampling feature: a name rather than a handle, a device
///         with <c>HasBindless</c>, and a host that paired the name through
///         <c>MaterialRenderFeature.TextureIndices</c>.
///     </para>
/// </remarks>
[DataContract("TexturedOrm")]
public sealed record TexturedOrmFeature : IMaterialFeature {
    /// <summary>What the material calls the packed map it wants sampled.</summary>
    /// <remarks>
    ///     ⚠ Not a name a material may choose freely, for <see cref="TexturedMetalRoughnessFeature" />
    ///     's reason: a host pairs one name with one name, keyed off this default.
    /// </remarks>
    public string OrmMap { get; init; } = "ormMap";

    /// <summary>How much of the map's occlusion is applied, 0 for none.</summary>
    public float OcclusionStrength { get; init; } = 1f;

    /// <summary>What the map's roughness channel is scaled by.</summary>
    public float Roughness { get; init; } = 1f;

    /// <summary>And its metalness channel.</summary>
    public float Metalness { get; init; } = 1f;

    /// <inheritdoc />
    public string ShaderName => "TexturedOrmSurface";

    /// <summary>What the shader calls the slot, under a composition path.</summary>
    /// <param name="path">
    ///     The qualified prefix the feature was composed under, as
    ///     <see cref="MaterialCompilationContext" /> builds it.
    /// </param>
    public static string OrmIndexParameter(string path) {
        ArgumentNullException.ThrowIfNull(path);
        return path + "ormIndex";
    }

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("occlusionStrength", OcclusionStrength);
        context.Set("roughness", Roughness);
        context.Set("metalness", Metalness);

        // Zero until a host with a table writes a slot over it — see
        // TexturedMetalRoughnessFeature.Compile for what the zero is and why it is not nothing.
        context.Set("ormIndex", 0u);
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
