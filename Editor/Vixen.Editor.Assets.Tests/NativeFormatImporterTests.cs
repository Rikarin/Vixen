// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Yaml.Meta;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class NativeFormatImporterTests {
    static readonly AssetId Albedo = new(Guid.Parse("9e8a44c9930c64e388ca034c5fe4c426"));
    static readonly AssetId Normal = new(Guid.Parse("1a2b3c4d5e6f70819a2b3c4d5e6f7081"));

    [Fact]
    public void ItClaimsTheExtensionsVixenAuthors() {
        var importer = new NativeFormatImporter();

        Assert.Equal("NativeFormatImporter", importer.Name);
        Assert.Contains(".vxgroup", importer.Extensions);
        Assert.Contains(".vxinput", importer.Extensions);

        // A scene and a material are compiled rather than carried through, so SceneImporter and
        // MaterialImporter claim them. This one is for the formats whose compiler does not exist yet
        // and the ones whose compiler would have nothing to do.
        Assert.DoesNotContain(".vxscene", importer.Extensions);
        Assert.DoesNotContain(".vxprefab", importer.Extensions);
        Assert.DoesNotContain(".vxmat", importer.Extensions);

        // And not .vxasset, which is the point of the two above being the whole list. An extension
        // that took any type tag could name any runtime type, and what it wrote under that name was
        // YAML text — so a file nothing else claims falls to RawImporter and becomes a Blob, a name
        // no typed reader resolves.
        Assert.DoesNotContain(".vxasset", importer.Extensions);
        Assert.Equal(2, importer.Extensions.Count);
    }

    /// <summary>
    ///     The whole reason this importer is not <c>RawImporter</c> under another name. A material
    ///     that does not declare the texture it names produces an artefact that is correct today and
    ///     stale for ever: the texture can be replaced and nothing re-runs the material.
    /// </summary>
    [Fact]
    public async Task EveryReferenceInTheDocumentBecomesADeclaredDependency() {
        var (context, result) = await Import(
            "hero.vxgroup",
            """
            shader: Standard
            parameters:
              albedo: vx:9e8a44c9930c64e388ca034c5fe4c426
              normal: vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081#2b9e5f13
              metallic: 0.5
            """
        );

        Assert.True(result.Succeeded);
        Assert.Equal(2, context.AssetDependencies.Count);
        Assert.Contains(Albedo, context.AssetDependencies);
        Assert.Contains(Normal, context.AssetDependencies);
    }

    /// <summary>
    ///     Found by walking the node tree rather than scanning the text, which is the difference this
    ///     test exists to hold: a GUID inside a comment or a description is not a reference, and a
    ///     dependency on one would never change and never break anything — the kind of wrongness that
    ///     is never found.
    /// </summary>
    [Fact]
    public async Task AReferenceShapedStringInsideACommentIsNotADependency() {
        var (context, result) = await Import(
            "hero.vxgroup",
            """
            # replaces vx:9e8a44c9930c64e388ca034c5fe4c426
            shader: Standard
            note: "see vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081 for why"
            """
        );

        Assert.True(result.Succeeded);
        Assert.Empty(context.AssetDependencies);
    }

    [Fact]
    public async Task ReferencesNestedInSequencesAreFoundToo() {
        var (context, result) = await Import(
            "props.vxgroup",
            """
            entities:
              - name: Player
                components:
                  - mesh: vx:9e8a44c9930c64e388ca034c5fe4c426
              - name: Prop
                components:
                  - mesh: vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081
            """
        );

        Assert.True(result.Succeeded);
        Assert.Equal(2, context.AssetDependencies.Count);
        Assert.Contains(Albedo, context.AssetDependencies);
        Assert.Contains(Normal, context.AssetDependencies);
    }

    [Fact]
    public async Task AnExplicitlyUnsetReferenceIsNotADependency() {
        var (context, result) = await Import("hero.vxgroup", "albedo: null\nnormal: ~\n");

        Assert.True(result.Succeeded);
        Assert.Empty(context.AssetDependencies);
    }

    /// <summary>
    ///     A scalar beginning <c>vx:</c> was meant to be a reference by whoever typed it. The choice
    ///     is between failing here, naming the file and the text, and shipping an asset whose pointer
    ///     resolves to nothing on a player's machine.
    /// </summary>
    [Fact]
    public async Task AMalformedReferenceFailsTheImportRatherThanBeingIgnored() {
        var (_, result) = await Import("hero.vxgroup", "albedo: vx:notaguid\n");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains("vx:notaguid", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YamlThatDoesNotParseIsReportedRatherThanThrown() {
        var (_, result) = await Import("hero.vxgroup", "shader: Standard\n  bad: indentation\n\t tab: here\n");

        Assert.False(result.Succeeded);
        Assert.Equal(ImportSeverity.Error, Assert.Single(result.Diagnostics).Severity);
    }

    [Fact]
    public async Task ADocumentThatIsNotAMappingIsRefused() {
        var (_, result) = await Import("hero.vxgroup", "- one\n- two\n");

        Assert.False(result.Succeeded);
        Assert.Contains("sequence", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A warning and not an error: the reader turns an empty document into an empty mapping on
    ///     purpose, so a file being saved right now arrives looking exactly like a valid asset with
    ///     nothing in it. Failing the build would punish an author mid-edit.
    /// </summary>
    [Fact]
    public async Task AnEmptyDocumentIsCarriedForwardWithAWarning() {
        var (_, result) = await Import("hero.vxgroup", "");

        Assert.True(result.Succeeded);
        Assert.Single(result.Artifacts);
        Assert.Equal(ImportSeverity.Warning, Assert.Single(result.Diagnostics).Severity);
    }

    [Fact]
    public async Task TheDocumentIsCarriedForwardVerbatim() {
        const string Text = "shader: Standard\nmetallic: 0.5\n";
        var (_, result) = await Import("hero.vxgroup", Text);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(Text, Encoding.UTF8.GetString(artifact.Content.Span));
        Assert.Equal(SubAssetId.Main, artifact.SubAsset);
    }

    [Fact]
    public async Task TheArtefactTypeComesFromTheExtensionWhenTheDocumentHasNoTag() {
        var (_, actions) = await Import("Player.vxinput", "name: Player\n");
        var (_, group) = await Import("UiCore.vxgroup", "name: UiCore\n");

        Assert.Equal("InputActions", Assert.Single(actions.Artifacts).Type);
        Assert.Equal("AddressableGroup", Assert.Single(group.Artifacts).Type);
    }

    /// <summary>
    ///     The escape hatch that used to be here, held shut. A document tagging itself
    ///     <c>!PhysicsMaterial</c> once produced a chunk labelled <c>PhysicsMaterial</c> whose bytes
    ///     were YAML text — the <c>.vxgrass</c> bug with the file renamed, since the runtime reader
    ///     that resolves that type name hands what it opens to the binary serializer and a game does
    ///     not link the YAML dialect. The type now comes from the extension and nowhere else.
    /// </summary>
    [Fact]
    public async Task ADocumentThatTagsItselfIsNotTakenAtItsWord() {
        var (_, result) = await Import("thing.vxgroup", "!PhysicsMaterial\nfriction: 0.6\n");

        Assert.Equal("AddressableGroup", Assert.Single(result.Artifacts).Type);
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(string name, string text) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();
        files.Seed(path, text);

        var importer = new NativeFormatImporter();
        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
