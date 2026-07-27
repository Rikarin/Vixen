// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Xunit;

namespace Vixen.Core.Yaml.Tests;

public sealed class MetaTests {
    const string Sidecar = """
        guid: 9e8a44c9930c64e388ca034c5fe4c426
        metaVersion: 1
        importer: !TextureImporter
          version: 3
          maxSize: 2048
          overrides:
            - target: Android
              compression: Astc6X6
              maxSize: 1024
            - target: Android/Vulkan
              maxSize: 2048
        addressable:
          address: ui/textures/hero
          group: UiCore
          labels: [ui, hd]
        subAssets:
          - { id: 3f7a91c2, name: hero, type: Texture }
        extensions: {}
        """;

    [Fact]
    public void ASidecarBindsToItsModel() {
        var meta = AssetMetaFile.Read(Sidecar);

        Assert.Equal(AssetId.Parse("9e8a44c9930c64e388ca034c5fe4c426"), meta.Guid);
        Assert.Equal(1, meta.MetaVersion);
        Assert.Equal("ui/textures/hero", meta.Addressable!.Address);
        Assert.Equal(["ui", "hd"], meta.Addressable.Labels);
        Assert.Equal("hero", Assert.Single(meta.SubAssets).Name);
        Assert.Equal(2048, Assert.IsType<TextureImportSettings>(meta.Importer).MaxSize);
    }

    [Fact]
    public void WritingASidecarAndReadingItBackIsTheIdentity() {
        var written = AssetMetaFile.Write(AssetMetaFile.Read(Sidecar));

        Assert.Equal(written, AssetMetaFile.Write(AssetMetaFile.Read(written)));
        Assert.StartsWith("guid: 9e8a44c9930c64e388ca034c5fe4c426\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ASidecarPathIsAppendedRatherThanSubstituted() =>
        // hero.png and hero.fbx in one folder must not share one sidecar.
        Assert.Equal("Assets/Textures/hero.png.meta", AssetMetaFile.PathFor("Assets/Textures/hero.png"));

    /// <summary>
    ///     The index rebuild reads three lines and stops. Doc 08 budgets a hundred thousand assets at
    ///     under ten seconds, and parsing a hundred thousand complete documents does not fit in that.
    /// </summary>
    [Fact]
    public void TheFastScanFindsTheEnvelopeWithoutReadingTheRest() {
        Assert.True(MetaScanner.TryScan(Sidecar, out var envelope));

        Assert.Equal(AssetId.Parse("9e8a44c9930c64e388ca034c5fe4c426"), envelope.Guid);
        Assert.Equal(1, envelope.MetaVersion);
        Assert.Equal("TextureImporter", envelope.ImporterTag);
    }

    /// <summary>
    ///     And it must not step into the importer's block. That block has a <c>version:</c> of its
    ///     own, and a scanner that wandered into it would report the importer's version as the
    ///     envelope's — an index that is confidently wrong.
    /// </summary>
    [Fact]
    public void TheFastScanDoesNotReadKeysBelongingToNestedBlocks() {
        const string awkward = """
            guid: 0123456789abcdef0123456789abcdef
            importer: !ModelImporter
              version: 5
              metaVersion: 99
              guid: ffffffffffffffffffffffffffffffff
            metaVersion: 1
            """;

        Assert.True(MetaScanner.TryScan(awkward, out var envelope));

        Assert.Equal(AssetId.Parse("0123456789abcdef0123456789abcdef"), envelope.Guid);
        Assert.Equal(1, envelope.MetaVersion);
        Assert.Equal("ModelImporter", envelope.ImporterTag);
    }

    [Fact]
    public void TheFastScanAgreesWithAFullParse() {
        Assert.True(MetaScanner.TryScan(Sidecar, out var envelope));
        var full = AssetMetaFile.Read(Sidecar);

        Assert.Equal(full.Guid, envelope.Guid);
        Assert.Equal(full.MetaVersion, envelope.MetaVersion);
    }

    [Fact]
    public void TheFastScanDeclinesRatherThanGuessingWhenThereIsNoGuid() {
        Assert.False(MetaScanner.TryScan("metaVersion: 1\nimporter: !FolderImporter\n", out var envelope));
        Assert.False(envelope.IsValid);
    }

    [Fact]
    public void AFileWithCommentsAndBlankLinesStillScans() {
        Assert.True(
            MetaScanner.TryScan(
                "# written by hand\n\nguid: 0123456789abcdef0123456789abcdef\n\n# and a note\nmetaVersion: 1\n",
                out var envelope
            )
        );

        Assert.Equal(1, envelope.MetaVersion);
    }

    /// <summary>
    ///     <c>metaVersion</c> is a real version with a real chain behind it. Each step takes a
    ///     document one version forward, so a file five versions old is upgraded by five small
    ///     functions rather than by one that knows every historical shape.
    /// </summary>
    [Fact]
    public void TheMigrationChainWalksOneVersionAtATime() {
        var seen = new List<int>();

        var chain = new MetaMigrationChain(3)
            .Add(1, root => {
                    seen.Add(MetaMigrationChain.VersionOf(root));
                    root.Set("addressable", new YamlMapping().Set("address", new YamlScalar("ui/hero")));
                }
            )
            .Add(2, root => {
                    seen.Add(MetaMigrationChain.VersionOf(root));
                    root.Set("subAssets", new YamlSequence());
                }
            );

        var meta = AssetMetaFile.Read("guid: 0123456789abcdef0123456789abcdef\nmetaVersion: 1\n", chain);

        // Each step sees the version its own input was at, not where the chain started.
        Assert.Equal([1, 2], seen);
        Assert.Equal(3, meta.MetaVersion);
        Assert.Equal("ui/hero", meta.Addressable!.Address);
    }

    /// <summary>A migration works on nodes, so everything it did not touch — comments included — survives.</summary>
    [Fact]
    public void AMigrationKeepsWhatItDidNotTouch() {
        const string original = """
            # an artist's note about this asset
            guid: 0123456789abcdef0123456789abcdef
            metaVersion: 1
            """;

        var chain = new MetaMigrationChain(2)
            .Add(1, root => root.Set("extensions", new YamlMapping().Set("author", new YamlScalar("jiu"))));

        var root = (YamlMapping)YamlReader.Read(original);
        chain.Apply(root);
        var written = YamlWriter.Write(root);

        Assert.StartsWith("# an artist's note about this asset\n", written, StringComparison.Ordinal);
        Assert.Contains("metaVersion: 2\n", written, StringComparison.Ordinal);
        Assert.Contains("author: jiu", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileFromANewerEditorIsRefusedRatherThanGuessedAt() {
        var failure = Assert.Throws<MetaVersionException>(
            () => AssetMetaFile.Read("guid: 0123456789abcdef0123456789abcdef\nmetaVersion: 99\n")
        );

        Assert.Equal(99, failure.FileVersion);
        Assert.Equal(MetaMigrationChain.CurrentVersion, failure.CurrentVersion);
    }

    [Fact]
    public void AHoleInTheChainSaysWhichStepIsMissing() {
        var chain = new MetaMigrationChain(3).Add(1, _ => { });

        var failure = Assert.Throws<YamlBindingException>(
            () => AssetMetaFile.Read("guid: 0123456789abcdef0123456789abcdef\nmetaVersion: 1\n", chain)
        );

        Assert.Contains("from envelope version 2 to 3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoStepsClaimingOneVersionIsAnErrorRatherThanOneSilentlyNeverRunning() =>
        Assert.Throws<ArgumentException>(() => new MetaMigrationChain(3).Add(1, _ => { }).Add(1, _ => { }));

    /// <summary>
    ///     A sub-asset's id comes from what it is, so re-exporting an FBX whose mesh order changed
    ///     does not renumber everything and break every reference to it.
    /// </summary>
    [Fact]
    public void ASubAssetIdComesFromWhatItIsAndNotFromWhereItLanded() {
        var first = SubAssets.Derive("ModelImporter", "Mesh", "Hero_Mesh");
        var again = SubAssets.Derive("ModelImporter", "Mesh", "Hero_Mesh");

        Assert.Equal(first, again);
        Assert.NotEqual(first, SubAssets.Derive("ModelImporter", "Mesh", "Cloth_Mesh"));
        Assert.NotEqual(first, SubAssets.Derive("ModelImporter", "Skeleton", "Hero_Mesh"));
        Assert.NotEqual(first, SubAssets.Derive("TextureImporter", "Mesh", "Hero_Mesh"));

        // The parts are separated, so "Mesh" + "Hero" is not the same input as "MeshHero" + "".
        Assert.NotEqual(
            SubAssets.Derive("ModelImporter", "Mesh", "Hero"),
            SubAssets.Derive("ModelImporter", "MeshHero", string.Empty)
        );

        Assert.False(first.IsMain);
    }

    [Fact]
    public void ACollisionIsReportedNamingBothRatherThanSilentlyResolved() {
        var id = SubAssets.Derive("ModelImporter", "Mesh", "Hero");

        var failure = Assert.Throws<SubAssetCollisionException>(
            () => SubAssets.EnsureDistinct([
                    new() { Id = id, Name = "Hero", Type = "Mesh" },
                    new() { Id = id, Name = "Villain", Type = "Mesh" }
                ])
        );

        Assert.Contains("Hero", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Villain", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The overrides resolve most-general-first, so the more specific target wins.</summary>
    [Fact]
    public void OverridesResolveMostSpecificLast() {
        var settings = (YamlMapping)((YamlMapping)YamlReader.Read(Sidecar))["importer"]!;

        var android = TargetOverrides.Resolve(settings, "Android");
        Assert.Equal("Astc6X6", ((YamlScalar)android["compression"]!).Value);
        Assert.Equal("1024", ((YamlScalar)android["maxSize"]!).Value);

        var vulkan = TargetOverrides.Resolve(settings, "Android/Vulkan");
        Assert.Equal("Astc6X6", ((YamlScalar)vulkan["compression"]!).Value);
        Assert.Equal("2048", ((YamlScalar)vulkan["maxSize"]!).Value);

        var windows = TargetOverrides.Resolve(settings, "Windows");
        Assert.Null(windows["compression"]);
        Assert.Equal("2048", ((YamlScalar)windows["maxSize"]!).Value);

        // And the overrides key itself is gone, because what is left is the settings as they apply.
        Assert.Null(vulkan["overrides"]);
        Assert.Equal("TextureImporter", vulkan.Tag);
    }

    /// <summary>
    ///     A prefix must be a whole segment. A plain <c>StartsWith</c> would make an override written
    ///     for <c>And</c> apply to Android, and one for <c>Windows</c> apply to <c>WindowsStore</c>.
    /// </summary>
    [Fact]
    public void ATargetPrefixMustBeAWholeSegment() {
        var settings = new YamlMapping()
            .Set("maxSize", new YamlScalar("2048", YamlScalarStyle.Plain))
            .Set(
                "overrides",
                new YamlSequence().Add(
                    new YamlMapping()
                        .Set("target", new YamlScalar("Windows"))
                        .Set("maxSize", new YamlScalar("512", YamlScalarStyle.Plain))
                )
            );

        Assert.Equal("512", ((YamlScalar)TargetOverrides.Resolve(settings, "Windows/x64")["maxSize"]!).Value);
        Assert.Equal("2048", ((YamlScalar)TargetOverrides.Resolve(settings, "WindowsStore")["maxSize"]!).Value);
    }

    [Fact]
    public void AnOverrideWithNoTargetSaysSo() {
        var settings = new YamlMapping().Set(
            "overrides",
            new YamlSequence().Add(new YamlMapping().Set("maxSize", new YamlScalar("512")))
        );

        var failure = Assert.Throws<YamlBindingException>(() => TargetOverrides.Resolve(settings, "Android"));

        Assert.Equal("overrides[0]", failure.Path);
    }

    [Theory]
    [InlineData("vx:9e8a44c9930c64e388ca034c5fe4c426", "9e8a44c9930c64e388ca034c5fe4c426", 0u)]
    [InlineData("vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081#2b9e5f13", "1a2b3c4d5e6f70819a2b3c4d5e6f7081", 0x2b9e5f13u)]
    public void AReferenceIsOneScalarThatRoundTrips(string text, string guid, uint sub) {
        Assert.True(AssetReference.TryParse(text, out var reference));

        Assert.Equal(AssetId.Parse(guid), reference.Asset);
        Assert.Equal(new SubAssetId(sub), reference.SubAsset);
        Assert.Equal(text, reference.ToString());
    }

    [Fact]
    public void ANullReferenceIsTheDocumentsNull() {
        Assert.True(AssetReference.TryParse("null", out var reference));
        Assert.True(reference.IsNull);
        Assert.Equal("null", reference.ToString());

        Assert.False(AssetReference.TryParse("9e8a44c9930c64e388ca034c5fe4c426", out _));
        Assert.False(AssetReference.TryParse("vx:not-a-guid", out _));
    }

    /// <summary>And it binds as one scalar, which is the whole point of the format.</summary>
    [Fact]
    public void AReferenceBindsAndIsWrittenAsOneScalar() {
        var read = YamlSerializer.Parse<Material>(
            "albedo: vx:9e8a44c9930c64e388ca034c5fe4c426\nmesh: vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081#2b9e5f13\nnormal: null\n"
        );

        Assert.Equal(AssetId.Parse("9e8a44c9930c64e388ca034c5fe4c426"), read.Albedo.Asset);
        Assert.Equal(new SubAssetId(0x2b9e5f13), read.Mesh.SubAsset);
        Assert.True(read.Normal.IsNull);

        Assert.Equal(
            """
            albedo: vx:9e8a44c9930c64e388ca034c5fe4c426
            mesh: vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081#2b9e5f13
            normal: null

            """.ReplaceLineEndings("\n"),
            YamlSerializer.ToYaml(read)
        );
    }
}

/// <summary>An asset that references others, which is what the <c>vx:</c> scalar exists for.</summary>
[DataContract]
public sealed record Material {
    public AssetReference Albedo { get; init; }
    public AssetReference Mesh { get; init; }
    public AssetReference Normal { get; init; }
}
