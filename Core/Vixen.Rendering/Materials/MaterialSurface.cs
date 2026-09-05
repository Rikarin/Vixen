// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Materials;

/// <summary>What a material looks like, reduced to the four numbers a preview can shade with.</summary>
/// <remarks>
///     <para>
///         <b>A material flattened, and the flattening is the point.</b> A <see cref="MaterialContent" />
///         is a feature tree that compiles to a shader variant with a constant buffer and a descriptor
///         set — the thing <c>MaterialRenderFeature</c> draws with, and the thing an editor viewport has
///         no compositor to draw with. This is the same material read for the question a viewport
///         actually asks: what colour is it, how metallic, how rough, does it glow.
///     </para>
///     <para>
///         ⚠ <b>Lossy on purpose, and in one direction only.</b> Textures are not sampled, normal maps
///         and clear coat and sheen are not evaluated, and a shading model that is not the standard one
///         is shaded as though it were. What survives is the metal-roughness workflow, which is what
///         <see cref="MetalRoughnessFeature" /> and <see cref="TexturedMetalRoughnessFeature" /> are and
///         what nearly every authored material is. A viewport that showed a red material as red, a metal
///         as metal and a lava as glowing is most of the distance from "one flat grey" to "the game's
///         picture"; the rest of that distance is the compositor's and stays there.
///     </para>
///     <para>
///         ⚠ <b><see cref="Default" /> is fully rough and not <see cref="MetalRoughnessFeature" />'s
///         half-rough default</b>, and the two answer different questions. A feature's default is what a
///         material that has a metal-roughness feature and did not set its roughness means. This one is
///         what an entity naming <em>no material at all</em> means — and a fully rough dielectric shades
///         to one directional term, which is exactly the picture the viewport drew before it could read
///         a material. An entity without a material therefore looks the way it always did.
///     </para>
/// </remarks>
/// <param name="BaseColour">The albedo, linear.</param>
/// <param name="Metalness">How much of a conductor the surface is, 0..1.</param>
/// <param name="Roughness">Perceptual roughness, 0..1.</param>
/// <param name="Emissive">What it emits on its own, linear, with intensity already folded in.</param>
public readonly record struct MaterialSurface(Color4 BaseColour, float Metalness, float Roughness, Color3 Emissive) {
    /// <summary>What an entity naming no material is shaded as: a fully rough dielectric.</summary>
    /// <inheritdoc cref="MaterialSurface" path="/remarks/para[3]" />
    public static MaterialSurface Default { get; } = new(Color4.White, Metalness: 0f, Roughness: 1f, Color3.Black);

    /// <summary>The surface a material's features add up to.</summary>
    /// <param name="features">The features, in the order they contribute.</param>
    /// <returns>The surface. <see cref="Default" /> when nothing in the list says anything.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Later features win, which is the order the list already means.</b>
    ///         <see cref="MaterialContent.Features" /> is "in the order they contribute", and a
    ///         composition chain has each slot wrapping the one before it — so a second base workflow
    ///         later in the list is the one on top. Emissive is added rather than replacing, because it
    ///         is the one feature that is not a base workflow.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <see cref="BlendFeature" /> is interpolated rather than resolved to one side.</b>
    ///         It is the heterogeneous half of layering and its whole content is the weight; taking
    ///         either side would make a fifty-fifty blend of chrome and rubber look like one of them,
    ///         which is worse than looking like neither.
    ///     </para>
    /// </remarks>
    public static MaterialSurface Of(IReadOnlyList<IMaterialFeature>? features) {
        if (features is null || features.Count == 0) {
            return Default;
        }

        var surface = Default;

        foreach (var feature in features) {
            surface = Fold(surface, feature);
        }

        return surface;
    }

    /// <summary>The surface a compiled material is shaded as.</summary>
    /// <param name="content">The material, or null for <see cref="Default" />.</param>
    /// <returns>The surface.</returns>
    public static MaterialSurface Of(MaterialContent? content) => Of(content?.Features);

    /// <summary>The reflectance a dielectric shows head-on, which every surface here shares.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant rather than a lane, because nothing authors it.</b>
    ///     <c>Brdf.F0FromMetalness</c> takes a reflectance and no <see cref="IMaterialFeature" /> sets
    ///     one, so carrying it per surface would be carrying <c>0.5</c> — four per cent — for every
    ///     entity in every frame. The day a feature exposes it, this becomes a field and the two
    ///     reserved lanes beside <c>MeshInstance.Surface</c> are where it goes.
    /// </remarks>
    public const float DielectricF0 = 0.04f;

    /// <summary>Whether this emits any light of its own.</summary>
    public bool IsEmissive => Emissive.R > 0f || Emissive.G > 0f || Emissive.B > 0f;

    static MaterialSurface Fold(MaterialSurface surface, IMaterialFeature feature) =>
        feature switch {
            MetalRoughnessFeature metal => surface with {
                BaseColour = Albedo(metal.BaseColor),
                Metalness = Saturate(metal.Metalness),
                Roughness = Saturate(metal.Roughness)
            },
            TexturedMetalRoughnessFeature textured => surface with {
                // The tint, without the map. A base-colour map multiplies this, and the viewport has
                // nowhere to sample it from — see the remarks about what this type drops.
                BaseColour = Albedo(textured.BaseColor),
                Metalness = Saturate(textured.Metalness),
                Roughness = Saturate(textured.Roughness)
            },
            SpecularGlossinessFeature specular => surface with {
                BaseColour = Albedo(specular.DiffuseColor),

                // ⚠ The workflow conversion, and it is deliberately the cheap one. A specular colour
                // brighter than a dielectric's four per cent is a conductor, so its magnitude reads as
                // metalness — which is right at both ends and approximate between them. The exact
                // conversion needs the specular colour itself as f0, which is a third lane this does
                // not have and a fidelity nobody looking at a viewport is asking for.
                Metalness = Saturate((Luminance(specular.SpecularColor) - DielectricF0) / (1f - DielectricF0)),
                Roughness = Saturate(1f - specular.Glossiness)
            },

            // Added, not replaced: emissive is the one feature here that is not a base workflow, and a
            // material is routinely a metal-roughness base *and* an emissive.
            EmissiveFeature emissive => surface with {
                Emissive = new(
                    emissive.EmissiveColor.X * emissive.Intensity,
                    emissive.EmissiveColor.Y * emissive.Intensity,
                    emissive.EmissiveColor.Z * emissive.Intensity
                )
            },
            // The tint, without the map — TexturedMetalRoughnessFeature's arrangement one channel over.
            // ⚠ Its colour's default is white rather than black, so a material that carries only this
            // reads as emitting at `Intensity` here where the same material on a device emits the map.
            // That is the same approximation the base colour above makes and is stated for the same
            // reason: a viewport shows the material's constants, and a map is not one.
            TexturedEmissiveFeature emissive => surface with {
                Emissive = new(
                    emissive.EmissiveColor.X * emissive.Intensity,
                    emissive.EmissiveColor.Y * emissive.Intensity,
                    emissive.EmissiveColor.Z * emissive.Intensity
                )
            },
            BlendFeature blend => Blend(surface, blend),

            // The weighted average of the layers, which is what a splat map produces where the weights
            // meet. A layered material seen without its splat map is every layer at once, and the mean
            // is the only answer that is not a guess about which one the viewer meant.
            MaterialLayersFeature layers => Layered(surface, layers),

            // And the painted stack, on exactly that argument: this one is *never* seen with its splat
            // map here, so the mean is not an approximation of the viewport's making — it is the only
            // thing the constants say.
            TexturedMaterialLayersFeature layers => Layered(surface, layers.Layers),

            // A normal map, an occlusion, a clear coat, a sheen, an anisotropy, a subsurface: each
            // modifies a surface this cannot show, so each leaves it alone rather than being refused.
            // ⚠ Silently, and that is the right silence — a material with a clear coat is still a
            // material whose base colour the viewport should draw.
            _ => surface
        };

    static MaterialSurface Blend(MaterialSurface surface, BlendFeature blend) {
        var weight = Saturate(blend.Weight);
        var under = blend.Under is null ? surface : Fold(surface, blend.Under);
        var over = blend.Over is null ? surface : Fold(surface, blend.Over);

        return new(
            Lerp(under.BaseColour, over.BaseColour, weight),
            float.Lerp(under.Metalness, over.Metalness, weight),
            float.Lerp(under.Roughness, over.Roughness, weight),
            Lerp(under.Emissive, over.Emissive, weight)
        );
    }

    static MaterialSurface Layered(MaterialSurface surface, MaterialLayersFeature layers) =>
        Layered(surface, layers.Layers);

    static MaterialSurface Layered(MaterialSurface surface, IReadOnlyList<MaterialLayerValue> layers) {
        var total = 0f;
        var colour = default(Vector3);
        var metalness = 0f;
        var roughness = 0f;

        foreach (var layer in layers) {
            var weight = MathF.Max(layer.Weight, 0f);

            total += weight;
            colour += layer.BaseColor * weight;
            metalness += Saturate(layer.Metalness) * weight;
            roughness += Saturate(layer.Roughness) * weight;
        }

        // ⚠ Weights that sum to zero are an unfinished material rather than a black one. Every layer
        // carries a weight of its own and an author who has set none yet has a list of them at zero,
        // which is on screen for as long as it takes to type the first number.
        if (total <= 0f) {
            return surface;
        }

        return surface with {
            BaseColour = Albedo(colour / total),
            Metalness = Saturate(metalness / total),
            Roughness = Saturate(roughness / total)
        };
    }

    static Color4 Albedo(Vector3 colour) => new(colour.X, colour.Y, colour.Z, 1f);

    static float Luminance(Vector3 colour) => (0.2126f * colour.X) + (0.7152f * colour.Y) + (0.0722f * colour.Z);

    static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    static Color4 Lerp(Color4 from, Color4 to, float weight) =>
        new(
            float.Lerp(from.R, to.R, weight),
            float.Lerp(from.G, to.G, weight),
            float.Lerp(from.B, to.B, weight),
            float.Lerp(from.A, to.A, weight)
        );

    static Color3 Lerp(Color3 from, Color3 to, float weight) =>
        new(
            float.Lerp(from.R, to.R, weight),
            float.Lerp(from.G, to.G, weight),
            float.Lerp(from.B, to.B, weight)
        );
}
