// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
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

    // --- The one string a material may not choose --------------------------

    /// <summary>Every map name a shipped feature carries is refused when it is renamed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The failure is a picture and nothing reports it.</b> A host pairs a sampling
    ///         shader's <c>uint</c> slot with a material-side texture name keyed off the feature's
    ///         <em>default</em> — one static table for the whole frame — so a material that renames
    ///         its map resolves no entry, keeps the index at zero and samples slot zero: the fallback
    ///         checker. Nine map-name remarks say the name is not the author's and, until this,
    ///         nothing enforced any of them. See
    ///         <a href="https://github.com/Rikarin/Vixen/issues/371">#371</a>.
    ///     </para>
    ///     <para>
    ///         <b>Every name rather than an example</b>, and found rather than listed: a feature that
    ///         forgets to check its own name is the failure this is written against, and a test that
    ///         renamed the seven names somebody remembered would be green for the eighth.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARenamedMapIsRefusedForEveryNameAFeatureCarries() {
        var names = MapNames();

        // ⚠ The instrument. An empty inventory is reflection that stopped finding features, and it
        // satisfies the loop below while examining nothing. What is *in* it is asserted separately,
        // by TheInventoryIsTheNineMapNamesAndNothingElse.
        Assert.NotEmpty(names);

        foreach (var (type, property, paired) in names) {
            var renamed = (IMaterialFeature)Activator.CreateInstance(type)!;

            // ⚠ Reflection reaches an `init` accessor — the modreq that stops C# is not a runtime
            // rule — which is what lets this rename a name no list here spells out.
            property.SetValue(renamed, "mine");

            var compilation = MaterialCompiler.Compile(Standard(renamed));

            Assert.True(
                compilation.Failed,
                $"{type.Name}.{property.Name} was renamed from '{paired}' to 'mine' and the material "
                + "compiled. A host pairs that map under the default, so the index stays zero and the "
                + "surface samples the fallback checker with nothing reported."
            );

            var diagnostic = Assert.Single(
                compilation.Errors,
                error => error.Id == MaterialDiagnosticId.RenamedTextureMap
            );

            // Both names, because "this is wrong" is only actionable beside what it should have been.
            Assert.Contains("mine", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains(paired, diagnostic.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>⚠ And at load the same rename is a warning, so a shipped mesh does not leave the screen.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The same compiler runs at import and at load, and the two want different answers.</b>
    ///         At import there is an author, the content has not shipped, and refusing is the point.
    ///         At load the material is in a bundle somebody built — and ⚠ <b>a compiler change moves
    ///         no importer version</b>, so an already-imported material is re-checked by nothing. A
    ///         refusal there would take the mesh off screen, silently, because
    ///         <c>AssetMaterialSource</c> reads no diagnostics.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the picture it leaves is not a good one.</b> A renamed map has always sampled
    ///         the fallback checker; the warning does not fix that. What it refuses to do is replace a
    ///         wrong picture with no picture at all for a defect the author can only be told about
    ///         where there is an author.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARenamedMapIsAWarningRatherThanARefusalWhereThereIsNoAuthor() {
        var renamed = new TexturedMetalRoughnessFeature { BaseColorMap = "mine" };
        var descriptor = Standard(renamed);

        // The instrument, and it is the whole claim: the same feature, the same descriptor, one flag.
        Assert.True(MaterialCompiler.Compile(descriptor).Failed);

        var loaded = MaterialCompiler.Compile(descriptor, slots: null, strict: false);

        Assert.False(
            loaded.Failed,
            "a material that has been drawing the fallback checker stopped compiling at load, which takes the "
            + "mesh off screen for content no importer is going to look at again."
        );

        Assert.NotNull(loaded.Material);

        // Said, and not merely allowed: a warning nobody emits is the silence this whole rule is about.
        var warning = Assert.Single(
            loaded.Diagnostics,
            diagnostic => diagnostic.Id == MaterialDiagnosticId.RenamedTextureMap
        );

        Assert.False(warning.IsError);
        Assert.Contains("mine", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Every map name the shipped features carry, found rather than listed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The reflection lives here because it cannot live in the feature.</b>
    ///         <c>Vixen.Rendering</c> is trim annotated, so <c>Type.GetProperties</c> on a type
    ///         reached through <see cref="IMaterialFeature" /> is <c>IL2070</c> — and suppressing that
    ///         would leave a check that trimming removes from the shipping build it was written to
    ///         protect. So each feature states its own name, and this reads the assembly to find the
    ///         one that did not.
    ///     </para>
    ///     <para>
    ///         A <c>string</c> with a non-empty default <em>is</em> a map name on every feature in the
    ///         library: <see cref="IMaterialFeature.ShaderName" /> has no setter and is not reached,
    ///         and <see cref="OcclusionFeature.OcclusionMap" /> is a <c>float</c> wearing the word.
    ///         ⚠ An empty default is the exemption and it is a rule rather than a case —
    ///         <see cref="GraphSurfaceFeature.Shader" /> is a name its graph supplies, so there is
    ///         nothing to be renamed away from.
    ///     </para>
    /// </remarks>
    static List<(Type Type, PropertyInfo Property, string Paired)> MapNames() {
        List<(Type, PropertyInfo, string)> names = [];

        var features = typeof(IMaterialFeature).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IMaterialFeature).IsAssignableFrom(type))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var type in features) {
            var fresh = Activator.CreateInstance(type);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (property.PropertyType != typeof(string) || !property.CanWrite) {
                    continue;
                }

                if (property.GetValue(fresh) is string paired && paired.Length > 0) {
                    names.Add((type, property, paired));
                }
            }
        }

        return names;
    }

    /// <summary>
    ///     And the inventory is exactly the nine, which is where two claims about it are decidable.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written after noticing that the two obvious tests could not fail.</b> "A graph
    ///         feature is exempt" and "a <c>float</c> called a map is left alone" read as assertions
    ///         about the compiler and are not: nothing asks either of those features to check a name,
    ///         so a <c>DoesNotContain</c> over a compilation of one holds however the rule is written,
    ///         including when it is written wrongly. The place both claims are decidable is the
    ///         <em>inventory</em>, because an entry here is a name the test above demands a feature
    ///         refuse.
    ///     </para>
    ///     <para>
    ///         So: <see cref="GraphSurfaceFeature.Shader" /> is a <c>string</c> every graph material
    ///         sets, and an inventory listing it would demand a refusal that rejects every
    ///         shader-graph material in the tree. <see cref="OcclusionFeature.OcclusionMap" /> is a
    ///         <c>float</c> wearing the word, and an inventory keyed on the property's <em>name</em>
    ///         rather than its type would list it. Neither is here, and this is where that is a fact
    ///         rather than a hope.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheInventoryIsTheNineMapNamesAndNothingElse() {
        string[] expected = [
            $"{nameof(ParallaxOcclusionFeature)}.{nameof(ParallaxOcclusionFeature.HeightMap)}",
            $"{nameof(TexturedEmissiveFeature)}.{nameof(TexturedEmissiveFeature.EmissiveMap)}",
            $"{nameof(TexturedMaterialLayersFeature)}.{nameof(TexturedMaterialLayersFeature.HeightMap)}",
            $"{nameof(TexturedMaterialLayersFeature)}.{nameof(TexturedMaterialLayersFeature.SplatMap)}",
            $"{nameof(TexturedMetalRoughnessFeature)}.{nameof(TexturedMetalRoughnessFeature.BaseColorMap)}",
            $"{nameof(TexturedNormalMapFeature)}.{nameof(TexturedNormalMapFeature.NormalMap)}",
            $"{nameof(TexturedOcclusionFeature)}.{nameof(TexturedOcclusionFeature.OcclusionMap)}",
            $"{nameof(TexturedOpacityFeature)}.{nameof(TexturedOpacityFeature.OpacityMap)}",
            $"{nameof(TexturedOrmFeature)}.{nameof(TexturedOrmFeature.OrmMap)}"
        ];

        var found = MapNames()
            .Select(name => $"{name.Type.Name}.{name.Property.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, found);
    }

    /// <summary>And the two maps called "height" are refused for each other's name.</summary>
    /// <remarks>
    ///     ⚠ <b>The case that already happened, and the reason this is an error rather than a
    ///     warning.</b> A re-bake copied its own texture entry's name onto a preserved
    ///     <see cref="ParallaxOcclusionFeature" />, which bound a real single-channel texture under
    ///     <c>heightMap</c> — the <em>layered</em> feature's four-channel per-layer bundle — leaving
    ///     the parallax index at zero and marching the checker as a height field. Both spellings
    ///     exist, both are paired, and neither is the other's.
    /// </remarks>
    [Fact]
    public void TheTwoHeightMapsAreRefusedForEachOthersName() {
        var parallax = MaterialCompiler.Compile(
            Standard(new ParallaxOcclusionFeature { HeightMap = "heightMap" })
        );

        var layered = MaterialCompiler.Compile(
            Standard(new TexturedMaterialLayersFeature { HeightMap = "parallaxHeightMap" })
        );

        Assert.Contains(parallax.Errors, error => error.Id == MaterialDiagnosticId.RenamedTextureMap);
        Assert.Contains(layered.Errors, error => error.Id == MaterialDiagnosticId.RenamedTextureMap);
    }

    /// <summary>A map set to nothing at all is refused too, rather than falling out unexamined.</summary>
    /// <remarks>
    ///     ⚠ A null name resolves exactly as little as a wrong one, and a check that pattern-matched
    ///     the value to <c>string</c> would skip it — the silent half of this failure reached by the
    ///     one input a reader assumes cannot occur.
    /// </remarks>
    [Fact]
    public void AMapNamedNothingIsRefused() {
        var compilation = MaterialCompiler.Compile(
            Standard(new TexturedNormalMapFeature { NormalMap = null! })
        );

        Assert.Contains(compilation.Errors, error => error.Id == MaterialDiagnosticId.RenamedTextureMap);
    }

    /// <summary>The names the features ship with compile, which is the half that must not be loud.</summary>
    /// <remarks>
    ///     What the arena's nine <c>.vxmat</c> files are: every one of them spells its maps the
    ///     feature's way, so a check that refused a correct material would refuse the shipped level.
    /// </remarks>
    [Fact]
    public void TheDefaultNamesCompileWithNothingSaid() {
        var compilation = MaterialCompiler.Compile(
            Standard(
                new ParallaxOcclusionFeature(),
                new TexturedMetalRoughnessFeature(),
                new TexturedNormalMapFeature(),
                new TexturedOrmFeature()
            )
        );

        Assert.False(compilation.Failed);
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Id == MaterialDiagnosticId.RenamedTextureMap
        );
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
