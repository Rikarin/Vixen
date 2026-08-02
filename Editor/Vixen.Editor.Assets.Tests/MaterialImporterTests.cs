// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Materials;
using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>
///     A <c>.vxmat</c> becomes something a frame can draw with.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this closes.</b> <c>MeshRenderable.Material</c> was authored, compiled and loaded and
///         never resolved, and the reason was one step short in the pipeline rather than anything in the
///         renderer: the material's file was carried forward as text, and the runtime links no YAML
///         parser, so nothing on the far side could read it. The parse happens here now, once, at build
///         time.
///     </para>
///     <para>
///         The assertions are about the <em>artefact</em> rather than about the parse, deliberately.
///         That the bytes read back as a material with its features on it is the whole claim; that the
///         binder can bind a mapping is <c>Vixen.Core.Yaml</c>'s test to have.
///     </para>
/// </remarks>
public sealed class MaterialImporterTests {
    static readonly AssetId Albedo = new(Guid.Parse("9e8a44c9930c64e388ca034c5fe4c426"));

    [Fact]
    public void ItClaimsTheMaterialExtensionAndNothingElse() {
        var importer = new MaterialImporter();

        Assert.Equal("MaterialImporter", importer.Name);
        Assert.Equal([".vxmat"], importer.Extensions);
    }

    /// <summary>
    ///     The artefact's type string names the type actually written, so a game can resolve it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one assertion that would have caught a material no runtime could load.</b> A chunk's
    ///     type is resolved through the registry at load, and <c>ImportPipeline.TypeIdOf</c> falls back
    ///     to an editor type rather than refusing when it does not resolve — so a wrong alias is a
    ///     build that succeeds and a game that throws about content it just made. This importer wrote
    ///     <c>"Material"</c>, which is <c>MaterialDescriptor</c>'s alias, while writing
    ///     <c>MaterialContent</c>'s bytes.
    ///
    ///     Asserting the constant against the registry rather than against a literal is deliberate:
    ///     a literal here would have been copied from the same mistaken place.
    /// </remarks>
    [Fact]
    public void TheArtifactTypeIsTheContractOfWhatIsWritten() {
        Assert.True(TypeRegistry.TryGetByAlias(MaterialImporter.MaterialType, out var descriptor));
        Assert.Equal(typeof(MaterialContent), descriptor.Type);
    }

    /// <summary>
    ///     The join itself: a document with features becomes a chunk that reads back as a material with
    ///     those features, ready for <see cref="MaterialCompiler" />.
    /// </summary>
    /// <remarks>
    ///     Read back through the serializer rather than compared to what was written, because the
    ///     artefact is the only thing a player ever sees. A test that asserted the parse would pass with
    ///     an importer that wrote no bytes at all.
    /// </remarks>
    [Fact]
    public async Task AMaterialWithFeaturesCompilesToAChunkThatReadsBack() {
        var (_, result) = await Import(
            """
            shader: ForwardPlus
            shading: StandardShading
            features:
              - !MetalRoughness
                baseColor: 0.8 0.2 0.1
                metalness: 1
                roughness: 0.25
              - !NormalMap
                strength: 0.5
            """
        );

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(MaterialImporter.MaterialType, artifact.Type);
        Assert.Equal(SubAssetId.Main, artifact.SubAsset);

        var content = Serializer.Read<MaterialContent>(artifact.Content.ToArray());

        Assert.Equal("ForwardPlus", content.Shader);
        Assert.Equal(2, content.Features.Length);

        var workflow = Assert.IsType<MetalRoughnessFeature>(content.Features[0]);

        Assert.Equal(1f, workflow.Metalness);
        Assert.Equal(0.25f, workflow.Roughness);
        Assert.Equal(0.8f, workflow.BaseColor.X, 1e-6f);

        Assert.IsType<NormalMapFeature>(content.Features[1]);
    }

    /// <summary>
    ///     A texture assignment survives to the artefact and is declared as a dependency.
    /// </summary>
    /// <remarks>
    ///     Both halves matter and they are different mechanisms. The binding is what a host turns into a
    ///     bindless slot; the dependency is what makes replacing the texture re-import the material, and
    ///     without it the material's artefact is correct today and stale for ever.
    /// </remarks>
    [Fact]
    public async Task ATextureIsBothCarriedAndDeclared() {
        var (context, result) = await Import(
            """
            features:
              - !TexturedMetalRoughness
                baseColorMap: baseColorMap
            textures:
              - parameter: baseColorMap
                texture: vx:9e8a44c9930c64e388ca034c5fe4c426
            """
        );

        Assert.Contains(Albedo, context.AssetDependencies);

        var content = Serializer.Read<MaterialContent>(Assert.Single(result.Artifacts).Content.ToArray());
        var texture = Assert.Single(content.Textures);

        Assert.Equal("baseColorMap", texture.Parameter);
        Assert.Equal(Albedo, texture.Texture.Asset);
    }

    /// <summary>
    ///     A shading model this build does not have is named back, rather than silently becoming the
    ///     default.
    /// </summary>
    /// <remarks>
    ///     The failure worth refusing: a material asking for cel shading and getting the standard model
    ///     draws, and looks like a shader bug rather than a spelling one.
    /// </remarks>
    [Fact]
    public async Task AShadingModelThisBuildDoesNotHaveIsAnError() {
        var (_, result) = await Import("shading: ToonShading\n");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains("ToonShading", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The material is compiled at import to find out whether it is one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what the compile check is for, and the only thing that catches it.</b> Two
    ///     slots filled with one shader compile — into a material where both read the same parameters,
    ///     which is a wrong image rather than a failure, and the kind that gets blamed on the artist.
    ///     Written down beside the file that caused it, it is a sentence somebody can act on.
    /// </remarks>
    [Fact]
    public async Task AMaterialThatWouldCompileWrongIsRefusedWithTheCompilersOwnWords() {
        var (_, result) = await Import(
            """
            features:
              - !NormalMap
                strength: 1
              - !NormalMap
                strength: 0.5
            """
        );

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains(
            "NormalMapSurface",
            Assert.Single(result.Diagnostics).Message,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     A material with no features is a white dielectric, which is a warning and an artefact.
    /// </summary>
    /// <remarks>
    ///     What a block-out is, and the reason the compile check reports rather than refuses: geometry
    ///     dropped into a level before anybody has made a material for it has to draw.
    /// </remarks>
    [Fact]
    public async Task AMaterialWithNoFeaturesIsBuiltAndQuestioned() {
        var (_, result) = await Import("shader: ForwardPlus\n");

        Assert.True(result.Succeeded);
        Assert.Single(result.Artifacts);
        Assert.Equal(ImportSeverity.Warning, Assert.Single(result.Diagnostics).Severity);
    }

    /// <summary>A material from a newer editor is refused by number rather than half-read.</summary>
    [Fact]
    public async Task AMaterialFromANewerBuildIsRefused() {
        var (_, result) = await Import($"version: {MaterialContent.Current + 1}\n");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
    }

    /// <summary>A feature tag this build has never heard of is reported, not thrown.</summary>
    [Fact]
    public async Task AFeatureThisBuildDoesNotHaveIsReported() {
        var (_, result) = await Import("features:\n  - !Iridescence\n    strength: 1\n");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(string text) {
        var path = new VirtualPath("/Assets/hero.vxmat");
        var files = new MemoryFileProvider();
        files.Seed(path, text);

        var importer = new MaterialImporter();
        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
