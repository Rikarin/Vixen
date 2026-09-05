// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A material feature that samples — the whole chain, from the authored record to the parameter
///     the host writes a table slot into.
/// </summary>
/// <remarks>
///     <para>
///         Doc 06 carried this as a gap for as long as a feature had no way to name a texture:
///         sampling needs a binding index only the compiled shader knows, and a feature is composed
///         into a shader it has never seen. Four things had to exist before this record could —
///         `Texture2D[]` as a type, `[Shared]` so every feature that samples names one table, `uv` on
///         `MaterialData` so there is somewhere to sample at, and a table slot arriving as a value.
///     </para>
///     <para>
///         What is asserted here is the join. The feature carries a <em>name</em> and no handle,
///         because a material is authored and serialised on machines with no device; the host pairs
///         that name with the shader parameter the compiler predicted. Both halves have to be right
///         or the slot is written under a name the effect's layout never asks for — which is not an
///         error anywhere, just a zero where an index should have been, and a frame lit by the
///         table's fallback texture.
///     </para>
/// </remarks>
public class TexturedMaterialTests {
    /// <summary>The index parameter is named by the path the feature was composed under.</summary>
    /// <remarks>
    ///     The name a host has to pair against, and the reason
    ///     <see cref="TexturedMetalRoughnessFeature.BaseColorIndexParameter" /> exists rather than a
    ///     host writing it down: the same feature under two composition slots is two parameters, and
    ///     a written-down name would be one of the two.
    /// </remarks>
    [Fact]
    public void The_index_is_named_by_the_composition_path() {
        var material = Compiled(new TexturedMetalRoughnessFeature());
        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColor", names);
    }

    /// <summary>And the helper produces exactly that name from the path.</summary>
    /// <remarks>
    ///     Held against the compiler's own answer rather than against a literal, so the two cannot
    ///     drift: this is the join a host makes, and a helper that agreed with a string in a test and
    ///     not with the compiler would be worse than no helper.
    /// </remarks>
    [Fact]
    public void The_helper_names_what_the_compiler_produced() {
        var material = Compiled(new TexturedMetalRoughnessFeature());

        var expected = TexturedMetalRoughnessFeature.BaseColorIndexParameter(
            "ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface."
        );

        Assert.Contains(expected, material.Parameters.Keys.Select(key => key.Name));
    }

    /// <summary>
    ///     The index starts at zero, which is a slot that exists rather than one that does not.
    /// </summary>
    /// <remarks>
    ///     A material whose map never reached a table — no bindless device, or a host that never set
    ///     the pairing — samples the table's fallback view. That is a defined thing to read and a
    ///     visible mistake, where an unwritten descriptor is whatever the driver left there.
    /// </remarks>
    [Fact]
    public void The_index_defaults_to_a_slot_that_exists() {
        var material = Compiled(new TexturedMetalRoughnessFeature());

        var key = ParameterKeys.New<uint>(
            "ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex"
        );

        Assert.True(material.Parameters.Has(key));
        Assert.Equal(0u, material.Parameters.Get(key));
    }

    /// <summary>The feature carries a name and no handle, so a material serialises.</summary>
    /// <remarks>
    ///     The property that makes "materials are values, not resources" true rather than a slogan: a
    ///     record holding a <c>TextureViewHandle</c> would be a material that cannot be written to
    ///     disk on a machine with no device, which is every machine that authors one.
    /// </remarks>
    [Fact]
    public void The_feature_names_its_map_rather_than_holding_one() {
        var feature = new TexturedMetalRoughnessFeature { BaseColorMap = "bark" };

        Assert.Equal("bark", feature.BaseColorMap);
        Assert.Equal(feature, feature with { });
    }

    /// <summary>It composes beside the features that do not sample.</summary>
    /// <remarks>
    ///     Which is the point of it being a surface feature rather than a second workflow: a textured
    ///     base colour under a clear coat and an emissive is the same chain, and the untextured
    ///     workflow stays exactly as it was for the targets that have no table.
    /// </remarks>
    [Fact]
    public void It_composes_with_the_features_that_do_not_sample() {
        var material = Compiled(
            new TexturedMetalRoughnessFeature { BaseColor = new(1f, 0.5f, 0.25f) },
            new NormalMapFeature(),
            new EmissiveFeature()
        );

        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.EmissiveSurface.intensity", names);
        Assert.Equal("CompositeSurface", material.Composition.Resolve("surface"));
    }

    /// <summary>A normal map is a second sampling feature, named under its own path.</summary>
    /// <remarks>
    ///     Which is the whole of what "a second texture parameter" costs: no new mechanism, one more
    ///     record and one more pairing. The names are asserted in full because they are what a host
    ///     joins against, and a path that drifted would leave the index at zero — a normal map read
    ///     from the table's fallback, which shades rather than fails.
    /// </remarks>
    [Fact]
    public void A_normal_map_is_named_by_its_own_composition_path() {
        var material = Compiled(new TexturedNormalMapFeature());
        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedNormalMapSurface.normalIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedNormalMapSurface.strength", names);

        Assert.Equal(
            "ForwardPlus.CompositeSurface.TexturedNormalMapSurface.normalIndex",
            TexturedNormalMapFeature.NormalIndexParameter("ForwardPlus.CompositeSurface.TexturedNormalMapSurface.")
        );
    }

    /// <summary>Its index starts at zero for the base colour's reason.</summary>
    [Fact]
    public void A_normal_maps_index_defaults_to_a_slot_that_exists() {
        var material = Compiled(new TexturedNormalMapFeature());

        var key = ParameterKeys.New<uint>("ForwardPlus.CompositeSurface.TexturedNormalMapSurface.normalIndex");

        Assert.True(material.Parameters.Has(key));
        Assert.Equal(0u, material.Parameters.Get(key));
    }

    /// <summary>And the two sampling features compose into one material.</summary>
    /// <remarks>
    ///     <para>
    ///         The claim the whole of part B rests on: a normal-mapped material was inexpressible not
    ///         because the chain could not hold two sampling features but because only one existed.
    ///         Two distinct shaders is two chain slots and two index parameters, which is what
    ///         <c>MaterialCompiler</c>'s refusal to place one shader twice guarantees.
    ///     </para>
    ///     <para>
    ///         The map names have to differ too, and that is asserted here rather than left to a
    ///         reviewer: <c>MaterialRenderFeature.TextureIndices</c> is keyed by the shader-side name
    ///         and valued by the material-side one, so two features sharing a map name would be two
    ///         indices filled from one texture — a normal map sampled as a base colour, which is a
    ///         plausible-looking frame.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_sampling_features_compose_into_one_material() {
        var material = Compiled(
            new TexturedMetalRoughnessFeature { BaseColor = new(1.739f, 1.623f, 1.456f) },
            new TexturedNormalMapFeature()
        );

        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedNormalMapSurface.normalIndex", names);

        Assert.Equal("TexturedMetalRoughnessSurface", material.Composition.Resolve("CompositeSurface.first"));
        Assert.Equal("TexturedNormalMapSurface", material.Composition.Resolve("CompositeSurface.second"));

        Assert.NotEqual(
            new TexturedMetalRoughnessFeature().BaseColorMap,
            new TexturedNormalMapFeature().NormalMap
        );
    }

    /// <summary>The packed map is a third sampling feature, on the same terms as the other two.</summary>
    /// <remarks>
    ///     Its three scalars are multipliers rather than values, so a material that wants the map
    ///     exactly leaves all three at one — which is why they default there and why the defaults are
    ///     asserted: a zero would read as "no occlusion, mirror-smooth, dielectric" and look like a
    ///     map that never arrived.
    /// </remarks>
    [Fact]
    public void A_packed_map_is_named_by_its_own_composition_path() {
        var material = Compiled(new TexturedOrmFeature());
        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedOrmSurface.ormIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedOrmSurface.occlusionStrength", names);

        Assert.Equal(
            "ForwardPlus.CompositeSurface.TexturedOrmSurface.ormIndex",
            TexturedOrmFeature.OrmIndexParameter("ForwardPlus.CompositeSurface.TexturedOrmSurface.")
        );

        var feature = new TexturedOrmFeature();

        Assert.Equal(1f, feature.OcclusionStrength);
        Assert.Equal(1f, feature.Roughness);
        Assert.Equal(1f, feature.Metalness);
    }

    /// <summary>All three sampling features compose, under three distinct names.</summary>
    /// <remarks>
    ///     <para>
    ///         Three chain slots, three index parameters, three map names — and the last of those is
    ///         the one worth an assertion. <c>MaterialRenderFeature.TextureIndices</c> is keyed by the
    ///         shader-side name and valued by the material-side one, so two features sharing a map
    ///         name would be two indices filled from one texture: an ORM map sampled as a normal,
    ///         which shades and does not fail.
    ///     </para>
    ///     <para>
    ///         This is the shape the whole of part B was for — a base colour, a normal and a packed
    ///         surface map on one material, which nothing in the library could express when only
    ///         <c>TexturedMetalRoughnessSurface</c> sampled.
    ///     </para>
    /// </remarks>
    [Fact]
    public void All_three_sampling_features_compose_under_distinct_names() {
        var material = Compiled(
            new TexturedMetalRoughnessFeature(),
            new TexturedNormalMapFeature(),
            new TexturedOrmFeature()
        );

        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedNormalMapSurface.normalIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedOrmSurface.ormIndex", names);

        string[] maps = [
            new TexturedMetalRoughnessFeature().BaseColorMap,
            new TexturedNormalMapFeature().NormalMap,
            new TexturedOrmFeature().OrmMap
        ];

        Assert.Equal(maps.Length, maps.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The emissive and opacity maps are two more sampling features on the same terms.</summary>
    /// <remarks>
    ///     ⚠ <c>TexturedEmissiveFeature.EmissiveColor</c> defaults to white where
    ///     <see cref="EmissiveFeature.EmissiveColor" />'s means the emission itself, and the defaults are
    ///     asserted because the wrong one is invisible: a black tint over a sampled map emits nothing,
    ///     which reads as a map that never arrived rather than as a default nobody meant.
    /// </remarks>
    [Fact]
    public void The_emissive_and_opacity_maps_are_named_by_their_own_composition_paths() {
        var material = Compiled(new TexturedEmissiveFeature(), new TexturedOpacityFeature());
        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedEmissiveSurface.emissiveIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedOpacitySurface.opacityIndex", names);

        Assert.Equal(
            "ForwardPlus.CompositeSurface.TexturedEmissiveSurface.emissiveIndex",
            TexturedEmissiveFeature.EmissiveIndexParameter("ForwardPlus.CompositeSurface.TexturedEmissiveSurface.")
        );

        Assert.Equal(
            "ForwardPlus.CompositeSurface.TexturedOpacitySurface.opacityIndex",
            TexturedOpacityFeature.OpacityIndexParameter("ForwardPlus.CompositeSurface.TexturedOpacitySurface.")
        );

        // Qualified, because this file's `using System.Numerics` shadows the engine's own Vector3 and
        // the two do not convert — an assertion against the wrong one does not compile, which is the
        // benign half of that collision.
        Assert.Equal(Vixen.Core.Mathematics.Vector3.One, new TexturedEmissiveFeature().EmissiveColor);
        Assert.Equal(1f, new TexturedEmissiveFeature().Intensity);
        Assert.Equal(1f, new TexturedOpacityFeature().Opacity);
    }

    /// <summary>
    ///     A layered material whose weights come from a map: doc 48 § B1's gap, expressed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three things at once, and each of them is a separate way for this feature to be a
    ///         finished thing that draws nothing: the splat index has to be named by the composition
    ///         path so a host can pair it, every layer has to carry its own indexed keys, and the count
    ///         has to be set as a permutation under the <em>pass</em>'s name rather than the feature's.
    ///     </para>
    ///     <para>
    ///         ⚠ Three layers rather than two, because two is the shader's declared default: a
    ///         permutation asserted at its default is an assertion that cannot fail.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layered_material_takes_its_weights_from_a_map() {
        var material = Compiled(
            new TexturedMaterialLayersFeature {
                Layers = [
                    new(Vector3.One, 0f, 0.8f, Weight: 1f),
                    new(new(0.2f, 0.4f, 0.2f), 0f, 0.6f, Weight: 1f),
                    new(new(0.9f, 0.9f, 0.9f), 0f, 0.3f, Weight: 1f)
                ]
            }
        );

        var names = material.Parameters.Keys.Select(key => key.Name).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface.splatIndex", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface.layers[2].baseColor", names);
        Assert.Contains("ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface.layers[2].weight", names);

        Assert.Equal(
            "ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface.splatIndex",
            TexturedMaterialLayersFeature.SplatIndexParameter(
                "ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface."
            )
        );

        // The pass's name and not the feature's, because Raven resolves a permutation across the whole
        // compilation — see MaterialCompilationContext.SetPermutation.
        Assert.Equal(3, material.Parameters.Get(MaterialKeys.LayerCount("ForwardPlus")));

        // And the map name is its own, so the pairing cannot fill it from another feature's texture.
        Assert.Equal("splatMap", new TexturedMaterialLayersFeature().SplatMap);
    }

    /// <summary>
    ///     ⚠ And the two layered features are one <c>LayerCount</c>, which is a constraint on materials.
    /// </summary>
    /// <remarks>
    ///     A permutation is resolved by name across a compilation, so a material carrying a constant
    ///     layer stack and a painted one sets one key twice — last write wins, and the loser's layers
    ///     are read out of a block sized for the winner. Asserted rather than left to a reader, because
    ///     the failure is a wrong picture and the fix is "do not author that material".
    /// </remarks>
    [Fact]
    public void The_constant_and_painted_layer_stacks_share_one_count() {
        var material = Compiled(
            new MaterialLayersFeature { Layers = [new(Vector3.One, 0f, 0.5f, 1f), new(Vector3.One, 0f, 0.5f, 1f)] },
            new TexturedMaterialLayersFeature {
                Layers = [
                    new(Vector3.One, 0f, 0.5f, 1f),
                    new(Vector3.One, 0f, 0.5f, 1f),
                    new(Vector3.One, 0f, 0.5f, 1f)
                ]
            }
        );

        // One key, and the painted stack compiled second, so three is what both shaders get.
        Assert.Equal(3, material.Parameters.Get(MaterialKeys.LayerCount("ForwardPlus")));
    }

    /// <summary>
    ///     The splat map's fourth channel is one a material has to declare, and the default declares
    ///     three.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The widest wrong picture this feature can draw, and it was the default.</b>
    ///         <c>Painted</c> returned <c>splat.a</c> for layer 3 unconditionally, and a one- or
    ///         three-channel texture samples alpha as 1 — the hazard <c>TexturedOpacitySurface</c>
    ///         argues at length and reads <c>.r</c> to avoid. Weighted 1 at every texel, layer 3 wins
    ///         the <c>1/total</c> normalisation over the whole surface: not a subtly wrong material but
    ///         a lit, plausible surface of entirely the wrong stuff, on a map nothing constrained to
    ///         have four channels.
    ///     </para>
    ///     <para>
    ///         So the number reaches the shader as a value, three by default, and the compiler says so
    ///         when a stack is deeper than the channels that paint it. <c>MaterialImporter</c> reports
    ///         every compiler diagnostic, so the message is what an author sees at import.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layer_past_the_splat_maps_painted_channels_is_a_warning_and_no_weight() {
        var compilation = MaterialCompiler.Compile(
            new() {
                Features = [
                    new TexturedMaterialLayersFeature {
                        Layers = [
                            new(Vector3.One, 0f, 0.8f, Weight: 1f),
                            new(Vector3.One, 0f, 0.6f, Weight: 1f),
                            new(Vector3.One, 0f, 0.4f, Weight: 1f),
                            new(Vector3.One, 0f, 0.2f, Weight: 1f)
                        ]
                    }
                ]
            }
        );

        // A warning, so the material still compiles and draws the three layers its map paints.
        Assert.False(compilation.Failed);

        var diagnostic = Assert.Single(compilation.Diagnostics, d => d.Id == MaterialDiagnosticId.UnpaintedLayer);

        Assert.False(diagnostic.IsError);

        Assert.Equal(
            3,
            compilation.Material!.Parameters.Get(
                ParameterKeys.New<int>(
                    "ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface.paintedChannels"
                )
            )
        );
    }

    /// <summary>A material whose map really has four channels says so, and is not warned about.</summary>
    /// <remarks>
    ///     The other half, without which the assertion above is satisfied by a feature that always
    ///     warns — and by one that writes a constant three whatever the material says.
    /// </remarks>
    [Fact]
    public void A_four_channel_splat_map_is_declared_and_paints_the_fourth_layer() {
        var compilation = MaterialCompiler.Compile(
            new() {
                Features = [
                    new TexturedMaterialLayersFeature {
                        PaintedChannels = 4,
                        Layers = [
                            new(Vector3.One, 0f, 0.8f, Weight: 1f),
                            new(Vector3.One, 0f, 0.6f, Weight: 1f),
                            new(Vector3.One, 0f, 0.4f, Weight: 1f),
                            new(Vector3.One, 0f, 0.2f, Weight: 1f)
                        ]
                    }
                ]
            }
        );

        Assert.DoesNotContain(compilation.Diagnostics, d => d.Id == MaterialDiagnosticId.UnpaintedLayer);

        Assert.Equal(
            4,
            compilation.Material!.Parameters.Get(
                ParameterKeys.New<int>(
                    "ForwardPlus.CompositeSurface.TexturedMaterialLayersSurface.paintedChannels"
                )
            )
        );
    }

    static Material Compiled(params IMaterialFeature[] features) {
        var compilation = MaterialCompiler.Compile(new() { Features = features });

        Assert.False(
            compilation.Failed,
            string.Join("\n", compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }
}
