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

    static Material Compiled(params IMaterialFeature[] features) {
        var compilation = MaterialCompiler.Compile(new() { Features = features });

        Assert.False(
            compilation.Failed,
            string.Join("\n", compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }
}
