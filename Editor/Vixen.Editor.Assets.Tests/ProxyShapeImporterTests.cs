// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Xunit;

namespace Tests;

/// <summary>
///     A proxy shape set is checked against a declared vocabulary at import, which is the whole reason
///     the vocabulary is a file rather than a convention.
/// </summary>
public sealed class ProxyShapeImporterTests {
    const string Vocabulary = """
        name: humanoid
        shapes:
          - name: belly
            meaning: The front of the torso, between the ribs and the hips.
          - name: right-palm
            meaning: The gripping face of the right hand.
        tags:
          - tag: affords=grip-surface
            meaning: A hand may close on it.
        classes:
          - name: humanoid
            members:
              - name: belly
                kind: Sphere
                extents: 0.2 0.2 0.2
                required: true
              - name: right-palm
                kind: Box
                tags: [affords=grip-surface]
                extents: 0.04 0.02 0.08
                required: true
        """;

    const string Body = """
        name: Body
        vocabulary: /Assets/humanoid.vxshapevocab
        class: humanoid
        shapes:
          - name: belly
            kind: Sphere
            joint: Spine
            position: 0 0.1 0.05
            extents: 0.22 0.22 0.22
          - name: right-palm
            kind: Box
            joint: Wrist
            extents: 0.04 0.02 0.08
            tags: [affords=grip-surface]
            coarse: true
        """;

    [Fact]
    public void TheImportersClaimTheirOwnExtensions() {
        Assert.Equal([".vxshapevocab"], new ShapeVocabularyImporter().Extensions);
        Assert.Equal([".vxproxyshapes"], new ProxyShapeSetImporter().Extensions);
    }

    /// <summary>Asserted against the registry rather than a literal, for the reason P0 records.</summary>
    [Fact]
    public void TheArtifactTypesAreTheContractsOfWhatIsWritten() {
        Assert.True(TypeRegistry.TryGetByAlias(ProxyShapeSetImporter.SetType, out var set));
        Assert.Equal(typeof(ProxyShapeSetContent), set.Type);

        Assert.True(TypeRegistry.TryGetByAlias(ShapeVocabularyImporter.VocabularyType, out var vocabulary));
        Assert.Equal(typeof(ShapeVocabularyContent), vocabulary.Type);
    }

    [Fact]
    public async Task AValidSetCompilesAndBakesAgainstARig() {
        var result = await ImportSet(Body, Vocabulary);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var artifact = Assert.Single(result.Artifacts);
        var content = Serializer.Read<ProxyShapeSetContent>(artifact.Content.ToArray());

        Assert.Equal("Body", content.Name);
        Assert.Equal(2, content.Shapes.Length);

        var set = content.Bake(Rig(), null);

        Assert.Equal(2, set.Count);
        Assert.Equal(ShapeKind.Sphere, set[set.IndexOf("belly")].Kind);
        Assert.True(set[set.IndexOf("right-palm")].Coarse);
        Assert.Equal(0.22f, set[set.IndexOf("belly")].Dimensions.Radius, 1e-5f);
    }

    /// <summary>
    ///     ⚠ The failure the whole declaration exists to turn into an error somebody reads.
    /// </summary>
    /// <remarks>
    ///     Without it, a shape whose name nobody else uses is a clip that silently does nothing on
    ///     this character and works everywhere else — discovered by a player, months later.
    /// </remarks>
    [Fact]
    public async Task AShapeTheVocabularyDoesNotDeclareStopsTheImportAndIsNamed() {
        var result = await ImportSet(Body.Replace("belly", "gizzard", StringComparison.Ordinal), Vocabulary);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("gizzard", StringComparison.Ordinal)
                && entry.Message.Contains("Body", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AClassMemberMissingFromTheSetIsAnErrorAndTheClipsThatWouldBreakAreNamed() {
        const string Torso = """
            name: TorsoOnly
            vocabulary: /Assets/humanoid.vxshapevocab
            class: humanoid
            shapes:
              - name: belly
                kind: Sphere
                joint: Spine
                extents: 0.2 0.2 0.2
            """;

        var result = await ImportSet(Torso, Vocabulary);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("right-palm", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AVocabularyThatIsNotThereIsAnErrorRatherThanAnUncheckedSet() {
        var result = await ImportSet(Body, vocabulary: null);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("only right by luck", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task TwoShapesWithOneNameStopTheImport() {
        const string Doubled = """
            name: Doubled
            shapes:
              - name: left-palm
                kind: Box
                joint: Wrist
                extents: 0.04 0.02 0.08
              - name: left-palm
                kind: Box
                joint: Spine
                extents: 0.04 0.02 0.08
            """;

        var result = await ImportSet(Doubled, vocabulary: null);

        Assert.Empty(result.Artifacts);
        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("left-palm", StringComparison.Ordinal)
        );
    }

    /// <summary>A class demanding a shape its own vocabulary forbids is one file contradicting itself.</summary>
    [Fact]
    public async Task AClassMemberTheVocabularyDoesNotDeclareIsAnError() {
        const string Contradictory = """
            name: humanoid
            shapes:
              - name: belly
                meaning: The front of the torso.
            classes:
              - name: humanoid
                members:
                  - name: right-palm
                    kind: Box
                    extents: 0.04 0.02 0.08
                    required: true
            """;

        var result = await ImportVocabulary(Contradictory);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("right-palm", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ A shape whose joint the rig does not have is skipped, not put at the root.
    /// </summary>
    /// <remarks>
    ///     A shape at the root is a shape in the middle of the character, so a contact resolving there
    ///     is a hand in somebody's chest — much harder to diagnose than a contact that does nothing.
    /// </remarks>
    [Fact]
    public async Task AShapeOnAJointTheRigDoesNotHaveIsSkippedAndReported() {
        var content = Serializer.Read<ProxyShapeSetContent>(
            Assert.Single((await ImportSet(Body, Vocabulary)).Artifacts).Content.ToArray()
        );

        List<string> unresolved = [];
        var set = content.Bake(RigWithoutAHand(), unresolved);

        Assert.Equal(1, set.Count);
        Assert.Equal("right-palm", Assert.Single(unresolved));
    }

    static Skeleton Rig() =>
        Skeleton.Create(
            new() {
                Name = "Rig",
                Joints = [
                    new() { Name = "Root", Parent = -1 },
                    new() { Name = "Spine", Parent = 0 },
                    new() { Name = "Wrist", Parent = 1 }
                ]
            }
        );

    static Skeleton RigWithoutAHand() =>
        Skeleton.Create(
            new() {
                Name = "Rig",
                Joints = [new() { Name = "Root", Parent = -1 }, new() { Name = "Spine", Parent = 0 }]
            }
        );

    static async Task<ImportResult> ImportSet(string text, string? vocabulary) {
        var path = new VirtualPath("/Assets/body.vxproxyshapes");
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        if (vocabulary is not null) {
            files.Seed(new VirtualPath("/Assets/humanoid.vxshapevocab"), vocabulary);
        }

        var importer = new ProxyShapeSetImporter();
        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }

    static async Task<ImportResult> ImportVocabulary(string text) {
        var path = new VirtualPath("/Assets/humanoid.vxshapevocab");
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        var importer = new ShapeVocabularyImporter();
        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
