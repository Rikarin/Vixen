// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The material feature tree, and what it compiles to.
/// </summary>
/// <remarks>
///     <para>
///         A material is a list of features and a shading model; what comes out is a composition that
///         selects the shaders implementing them and a parameter collection keyed by the names those
///         shaders will have <em>once composed</em>. Everything here is about that translation, since
///         the rest — key to effect, effect to pipeline — already existed.
///     </para>
///     <para>
///         The names are predicted rather than read from a compiled shader, which is the one thing in
///         the material system that can be quietly wrong. <see cref="MaterialReflectionTests" /> is
///         where that is held against what Raven actually emits.
///     </para>
/// </remarks>
public class MaterialCompilerTests {
    static MaterialDescriptor Standard(params IMaterialFeature[] features) =>
        new() { Features = features };

    static Material Compiled(MaterialDescriptor descriptor) {
        var compilation = MaterialCompiler.Compile(descriptor);

        Assert.False(
            compilation.Failed,
            string.Join("\n", compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    static IEnumerable<string> Names(Material material) => material.Parameters.Keys.Select(key => key.Name);

    // --- Composition -------------------------------------------------------

    /// <summary>
    ///     A material's features become the shaders that fill the chain's slots.
    /// </summary>
    /// <remarks>
    ///     In order, because order is what a feature chain means: occlusion after a base workflow
    ///     multiplies into what the workflow wrote, and before it multiplies into a default.
    /// </remarks>
    [Fact]
    public void FeaturesFillTheChainsSlotsInOrder() {
        var material = Compiled(
            Standard(new MetalRoughnessFeature(), new NormalMapFeature(), new EmissiveFeature())
        );

        Assert.Equal("CompositeSurface", material.Composition.Resolve("surface"));
        Assert.Equal("MetalRoughnessSurface", material.Composition.Resolve("CompositeSurface.first"));
        Assert.Equal("NormalMapSurface", material.Composition.Resolve("CompositeSurface.second"));
        Assert.Equal("EmissiveSurface", material.Composition.Resolve("CompositeSurface.third"));
    }

    /// <summary>
    ///     Every slot the library declares is bound, including the ones this material does not use.
    /// </summary>
    /// <remarks>
    ///     Raven rejects a compilation with an unfilled slot wherever it is declared, so a
    ///     composition that answered only for the shaders one material reaches would not compile —
    ///     and the failure would arrive in whatever tries to build the effect rather than here.
    /// </remarks>
    [Fact]
    public void TheSlotsAMaterialDoesNotUseTakeTheIdentityFeature() {
        var material = Compiled(Standard(new MetalRoughnessFeature()));

        Assert.Equal("IdentitySurface", material.Composition.Resolve("CompositeSurface.eighth"));
        Assert.Equal("IdentitySurface", material.Composition.Resolve("BlendSurface.under"));
        Assert.Equal("IdentitySurface", material.Composition.Resolve("BlendSurface.over"));
    }

    /// <summary>The shading model is a slot of its own, filled independently of the features.</summary>
    [Fact]
    public void TheShadingModelFillsItsOwnSlot() {
        var material = Compiled(
            new MaterialDescriptor {
                Features = [new MetalRoughnessFeature(), new ClearCoatFeature()],
                Shading = new ClearCoatShading()
            }
        );

        Assert.Equal("ClearCoatShading", material.Composition.Resolve("shading"));
        Assert.Equal("CompositeSurface", material.Composition.Resolve("surface"));
    }

    /// <summary>
    ///     A material always goes through the chain, even with one feature.
    /// </summary>
    /// <remarks>
    ///     Binding a lone feature straight into <c>surface</c> would compile, and would name its
    ///     parameters differently from the same material with a normal map added — so adding a
    ///     feature would rename <c>baseColor</c> and silently drop whatever a host had set on it.
    /// </remarks>
    [Fact]
    public void OneFeatureIsStillComposedThroughTheChain() {
        var material = Compiled(Standard(new MetalRoughnessFeature()));

        Assert.Equal("CompositeSurface", material.Composition.Resolve("surface"));
        Assert.Contains("ForwardPlus.CompositeSurface.MetalRoughnessSurface.baseColor", Names(material));
    }

    // --- Parameters --------------------------------------------------------

    /// <summary>
    ///     A feature's parameters are named by the path of shaders they were reached through.
    /// </summary>
    /// <remarks>
    ///     The pass first, because that is how every key in the engine is qualified and how the
    ///     generator emits them; then the composition path, because that is how Raven qualifies a
    ///     composed shader's parameters. Both halves have to be right or the value is written under a
    ///     name the effect's layout never asks for — which is not an error anywhere, just a default
    ///     where a value should have been.
    /// </remarks>
    [Fact]
    public void AFeaturesParametersAreNamedByTheirPath() {
        var material = Compiled(
            Standard(
                new MetalRoughnessFeature { BaseColor = new(1f, 0f, 0f), Metalness = 1f, Roughness = 0.25f },
                new EmissiveFeature { Intensity = 3f }
            )
        );

        var names = Names(material).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.MetalRoughnessSurface.baseColor", names);
        Assert.Contains("ForwardPlus.CompositeSurface.MetalRoughnessSurface.metalness", names);
        Assert.Contains("ForwardPlus.CompositeSurface.EmissiveSurface.intensity", names);
    }

    /// <summary>The values arrive, not only the names.</summary>
    [Fact]
    public void AFeaturesValuesReachTheCollection() {
        var material = Compiled(
            Standard(new MetalRoughnessFeature { BaseColor = new(0.25f, 0.5f, 0.75f), Roughness = 0.125f })
        );

        var baseColor = ParameterKeys.New<Vector3>(
            "ForwardPlus.CompositeSurface.MetalRoughnessSurface.baseColor"
        );

        var roughness = ParameterKeys.New<float>(
            "ForwardPlus.CompositeSurface.MetalRoughnessSurface.roughness"
        );

        Assert.Equal(new(0.25f, 0.5f, 0.75f), material.Parameters.Get(baseColor));
        Assert.Equal(0.125f, material.Parameters.Get(roughness));
    }

    /// <summary>A shading model's own parameters are named after the model, not the surface.</summary>
    [Fact]
    public void AShadingModelsParametersAreNamedAfterIt() {
        var material = Compiled(
            new MaterialDescriptor {
                Features = [new MetalRoughnessFeature()],
                Shading = new CelShading { Steps = 4f }
            }
        );

        var steps = ParameterKeys.New<float>("ForwardPlus.CelShading.steps");

        Assert.Contains("ForwardPlus.CelShading.steps", Names(material));
        Assert.Equal(4f, material.Parameters.Get(steps));
    }

    /// <summary>
    ///     A layered material's layers are array elements, and the count is a permutation.
    /// </summary>
    /// <remarks>
    ///     The one feature whose parameters cannot come from composition: two composed copies of a
    ///     workflow share its storage, so layers are an array and the count sizes it at compile time.
    /// </remarks>
    [Fact]
    public void ALayeredMaterialsLayersAreArrayElements() {
        var material = Compiled(
            Standard(
                new MaterialLayersFeature {
                    Layers = [
                        new(new(1f, 0f, 0f), 0f, 0.5f, 1f),
                        new(new(0f, 1f, 0f), 1f, 0.2f, 0.5f),
                        new(new(0f, 0f, 1f), 0f, 0.9f, 0.25f)
                    ]
                }
            )
        );

        var names = Names(material).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.MaterialLayersSurface.layers[0].baseColor", names);
        Assert.Contains("ForwardPlus.CompositeSurface.MaterialLayersSurface.layers[2].weight", names);

        Assert.Equal(3, material.Parameters.Get(MaterialKeys.LayerCount("ForwardPlus")));
    }

    /// <summary>A blend's two layers are composed under it, and keep their own parameters.</summary>
    [Fact]
    public void ABlendsLayersAreComposedUnderIt() {
        var material = Compiled(
            Standard(
                new BlendFeature {
                    Under = new MetalRoughnessFeature { BaseColor = new(1f, 0f, 0f) },
                    Over = new SpecularGlossinessFeature { Glossiness = 0.9f },
                    Weight = 0.25f
                }
            )
        );

        Assert.Equal("BlendSurface", material.Composition.Resolve("CompositeSurface.first"));
        Assert.Equal("MetalRoughnessSurface", material.Composition.Resolve("BlendSurface.under"));
        Assert.Equal("SpecularGlossinessSurface", material.Composition.Resolve("BlendSurface.over"));

        var names = Names(material).ToArray();

        Assert.Contains("ForwardPlus.CompositeSurface.BlendSurface.blend", names);
        Assert.Contains("ForwardPlus.CompositeSurface.BlendSurface.MetalRoughnessSurface.baseColor", names);
        Assert.Contains("ForwardPlus.CompositeSurface.BlendSurface.SpecularGlossinessSurface.glossiness", names);
    }

    /// <summary>
    ///     A layered material's values land in the block, one layer per stride.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The end of the path the rest of this file only checks the start of: a feature's values
    ///         become keys, an effect's layout says where each key goes, and <c>EffectConstants</c>
    ///         writes them. Worth following the whole way for the layered feature in particular,
    ///         because it is the one that indexes — and an index that did not line up with the array
    ///         stride would put layer two's colour wherever layer one's roughness lives, which is a
    ///         plausible image rather than a failure.
    ///     </para>
    ///     <para>
    ///         The layout here is what a provider expands the reflection into: one entry per element,
    ///         at <c>offset + index × stride</c>. See <see cref="Effect.Parameters" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALayeredMaterialsValuesLandOneLayerPerStride() {
        using var device = new NullDevice(new() { Record = true });
        using var constants = new EffectConstants(device);

        var material = Compiled(
            Standard(
                new MaterialLayersFeature {
                    Layers = [
                        new(new(1f, 0f, 0f), 0f, 0.25f, 1f),
                        new(new(0f, 1f, 0f), 1f, 0.75f, 0.5f)
                    ]
                }
            )
        );

        const int Stride = 32;
        const string Path = "ForwardPlus.CompositeSurface.MaterialLayersSurface.layers";

        EffectParameter[] layout = [
            new(ParameterKeys.New<Vector3>($"{Path}[0].baseColor"), 0, 12),
            new(ParameterKeys.New<float>($"{Path}[0].roughness"), 16, 4),
            new(ParameterKeys.New<Vector3>($"{Path}[1].baseColor"), Stride, 12),
            new(ParameterKeys.New<float>($"{Path}[1].roughness"), Stride + 16, 4)
        ];

        Assert.True(constants.Update("Layout", Stride * 2, layout, material.Parameters));

        Assert.Equal(new(1f, 0f, 0f), MemoryMarshal.Read<Vector3>(constants.Bytes[..12]));
        Assert.Equal(0.25f, MemoryMarshal.Read<float>(constants.Bytes[16..20]));
        Assert.Equal(new(0f, 1f, 0f), MemoryMarshal.Read<Vector3>(constants.Bytes[Stride..(Stride + 12)]));
        Assert.Equal(0.75f, MemoryMarshal.Read<float>(constants.Bytes[(Stride + 16)..(Stride + 20)]));
    }

    // --- What composition cannot express -----------------------------------

    /// <summary>
    ///     A feature used twice is rejected rather than compiled into a material that aliases.
    /// </summary>
    /// <remarks>
    ///     The failure this exists to prevent is not a crash. Two slots bound to one shader compile
    ///     perfectly, into a material where both read the same parameters — so a two-layer blend of
    ///     one workflow is one layer drawn twice, and the artist who painted two colours sees one.
    /// </remarks>
    [Fact]
    public void AFeatureUsedTwiceIsRejected() {
        var compilation = MaterialCompiler.Compile(
            Standard(
                new BlendFeature {
                    Under = new MetalRoughnessFeature { BaseColor = new(1f, 0f, 0f) },
                    Over = new MetalRoughnessFeature { BaseColor = new(0f, 1f, 0f) }
                }
            )
        );

        Assert.True(compilation.Failed);

        var diagnostic = Assert.Single(compilation.Errors);
        Assert.Equal(MaterialDiagnosticId.DuplicateFeature, diagnostic.Id);
        Assert.Contains("MetalRoughnessSurface", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>The same feature in two chain slots is the same mistake, and is caught too.</summary>
    [Fact]
    public void AFeatureRepeatedInTheChainIsRejected() {
        var compilation = MaterialCompiler.Compile(
            Standard(new EmissiveFeature { Intensity = 1f }, new EmissiveFeature { Intensity = 2f })
        );

        Assert.True(compilation.Failed);
        Assert.Equal(MaterialDiagnosticId.DuplicateFeature, Assert.Single(compilation.Errors).Id);
    }

    /// <summary>More features than the chain has slots is rejected rather than truncated.</summary>
    [Fact]
    public void MoreFeaturesThanSlotsIsRejected() {
        IMaterialFeature[] features = [
            new MetalRoughnessFeature(),
            new NormalMapFeature(),
            new EmissiveFeature(),
            new OcclusionFeature(),
            new AnisotropyFeature(),
            new ClearCoatFeature(),
            new ClearCoatNormalMapFeature(),
            new SheenFeature(),
            new SubsurfaceFeature()
        ];

        var compilation = MaterialCompiler.Compile(Standard(features));

        Assert.True(compilation.Failed);
        Assert.Equal(MaterialDiagnosticId.TooManyFeatures, Assert.Single(compilation.Errors).Id);
    }

    /// <summary>A material with no features is compiled, and said out loud.</summary>
    /// <remarks>
    ///     A warning rather than an error, because it is exactly what an editor has on screen while
    ///     somebody is building one — and it is a valid material: a white dielectric.
    /// </remarks>
    [Fact]
    public void AMaterialWithNoFeaturesIsAWarningAndStillCompiles() {
        var compilation = MaterialCompiler.Compile(new());

        Assert.False(compilation.Failed);
        Assert.Empty(compilation.Errors);
        Assert.Equal(MaterialDiagnosticId.NoFeatures, Assert.Single(compilation.Diagnostics).Id);
    }

    /// <summary>The chain's full width is usable, which is what makes eight slots a real ceiling.</summary>
    [Fact]
    public void EightFeaturesFit() {
        var material = Compiled(
            Standard(
                new MetalRoughnessFeature(),
                new NormalMapFeature(),
                new EmissiveFeature(),
                new OcclusionFeature(),
                new AnisotropyFeature(),
                new ClearCoatFeature(),
                new ClearCoatNormalMapFeature(),
                new SheenFeature()
            )
        );

        Assert.Equal("SheenSurface", material.Composition.Resolve("CompositeSurface.eighth"));
    }

    // --- What the composition is for ---------------------------------------

    /// <summary>
    ///     Two materials that differ only in their features are two effect keys.
    /// </summary>
    /// <remarks>
    ///     The reason the composition is in the key at all. Same shader name, same permutations,
    ///     different code — and a cache that could not tell them apart would hand the second material
    ///     the first one's shader, which is a wrong image with nothing logged anywhere.
    /// </remarks>
    [Fact]
    public void TwoMaterialsDifferingOnlyInFeaturesAreTwoKeys() {
        var plain = Compiled(Standard(new MetalRoughnessFeature()));
        var coated = Compiled(Standard(new MetalRoughnessFeature(), new ClearCoatFeature()));

        var first = EffectKey.From(plain.ShaderName, plain.Parameters, [], plain.Composition);
        var second = EffectKey.From(coated.ShaderName, coated.Parameters, [], coated.Composition);

        Assert.NotEqual(first, second);
    }

    /// <summary>The same material compiled twice is one key, whatever order the slots come out in.</summary>
    [Fact]
    public void TheSameMaterialTwiceIsOneKey() {
        var first = Compiled(Standard(new MetalRoughnessFeature(), new NormalMapFeature()));
        var second = Compiled(Standard(new MetalRoughnessFeature(), new NormalMapFeature()));

        Assert.Equal(
            EffectKey.From(first.ShaderName, first.Parameters, [], first.Composition),
            EffectKey.From(second.ShaderName, second.Parameters, [], second.Composition)
        );
    }
}
