// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Assets.Textures;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The mirrors have to say the same thing as the records they stand for.</summary>
/// <remarks>
///     The whole justification for a mirror existing is that the settings records are <c>init</c>-only
///     for the pipeline's sake. The cost of that decision is a second declaration that can drift, and
///     this is what makes the drift a red test rather than a knob nobody can turn.
/// </remarks>
public class ImportSettingsMirrorTests {
    /// <summary>Every setting a texture import has is editable.</summary>
    [Fact]
    public void TextureMirrorCoversTheRecord() => Compare(typeof(TextureImportSettings), typeof(TextureImportEdits));

    /// <summary>And every setting a model import has.</summary>
    [Fact]
    public void ModelMirrorCoversTheRecord() => Compare(typeof(ModelImportSettings), typeof(ModelImportEdits));

    /// <summary>And every part of a group's policy.</summary>
    [Fact]
    public void GroupMirrorCoversTheRecord() =>
        Compare(typeof(AddressableGroup), typeof(Content.AddressableGroupEdits));

    /// <summary>A mirror's members are writable, which is the point of it existing.</summary>
    [Fact]
    public void MirrorMembersAreWritable() {
        foreach (var property in typeof(TextureImportEdits).GetProperties()) {
            Assert.True(property.CanWrite, $"{property.Name} cannot be written.");
        }
    }

    /// <summary>
    ///     ⚠ <c>Version</c> is deliberately absent: it is the importer's own schema version, written
    ///     by the pipeline, and a field an author could type into is a way to invalidate every
    ///     artefact in a project by accident.
    /// </summary>
    [Fact]
    public void VersionIsNotEditable() =>
        Assert.Null(typeof(TextureImportEdits).GetProperty("Version"));

    static void Compare(Type record, Type mirror) {
        var expected = record
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !string.Equals(property.Name, "Version", StringComparison.Ordinal))
            .Where(property => !string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.PropertyType);

        var actual = mirror
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, property => property.PropertyType);

        foreach (var (name, type) in expected) {
            Assert.True(actual.ContainsKey(name), $"{mirror.Name} is missing '{name}'.");
            Assert.Equal(type, actual[name]);
        }

        foreach (var name in actual.Keys) {
            Assert.True(expected.ContainsKey(name), $"{mirror.Name} has '{name}', which {record.Name} does not.");
        }
    }
}

/// <summary>What an import-settings document does to a sidecar.</summary>
public class ImportSettingsDocumentTests {
    /// <summary>A sidecar with nothing in it opens with the type's defaults.</summary>
    [Fact]
    public void AMissingSidecarIsDefaults() {
        using var fixture = new EditorFixture();
        var path = fixture.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal(TextureContent.Colour, document.Texture.Content);
        Assert.True(document.Texture.GenerateMips);
        Assert.Empty(document.Overrides);
    }

    /// <summary>The settings in the file are what the editors show.</summary>
    [Fact]
    public void SettingsAreReadFromTheSidecar() {
        using var fixture = new EditorFixture();

        var path = fixture.WriteAsset(
            "Assets/normal.png",
            "bytes",
            "guid: 00000000000000000000000000000001\nmetaVersion: 1\nimporter: !TextureImporter\n"
            + "  content: NormalMap\n  maxSize: 512\n"
        );

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal(TextureContent.NormalMap, document.Texture.Content);
        Assert.Equal(512, document.Texture.MaxSize);
    }

    /// <summary>⚠ A key this build does not know survives a save, and is reported rather than dropped.</summary>
    [Fact]
    public void UnknownKeysSurviveASave() {
        using var fixture = new EditorFixture();

        var path = fixture.WriteAsset(
            "Assets/hero.png",
            "bytes",
            "guid: 00000000000000000000000000000001\nmetaVersion: 1\nimporter: !TextureImporter\n"
            + "  maxSize: 256\n  futureSetting: 12\n"
        );

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);

        Assert.Contains(document.UnknownKeys, key => key.Contains("futureSetting", StringComparison.Ordinal));
        Assert.Contains("futureSetting: 12", document.ToYaml(), StringComparison.Ordinal);
    }

    /// <summary>The GUID the sidecar already carries is not rewritten by a save.</summary>
    [Fact]
    public void TheGuidSurvives() {
        using var fixture = new EditorFixture();

        var path = fixture.WriteAsset(
            "Assets/hero.png",
            "bytes",
            "guid: 0123456789abcdef0123456789abcdef\nmetaVersion: 1\nimporter: !TextureImporter\n"
        );

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);

        Assert.Contains("guid: 0123456789abcdef0123456789abcdef", document.ToYaml(), StringComparison.Ordinal);
    }

    /// <summary>A per-target override block is read back as a row with the right members marked.</summary>
    [Fact]
    public void OverridesAreReadAsRows() {
        using var fixture = new EditorFixture();

        var path = fixture.WriteAsset(
            "Assets/hero.png",
            "bytes",
            "guid: 00000000000000000000000000000001\nmetaVersion: 1\nimporter: !TextureImporter\n"
            + "  maxSize: 2048\n  overrides:\n    - target: Android\n      maxSize: 1024\n"
        );

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);
        var row = Assert.Single(document.Overrides);

        Assert.Equal("Android", row.Target);
        Assert.True(row.IsOverridden("MaxSize"));
        Assert.False(row.IsOverridden("GenerateMips"));

        // The row shows what the target will actually build with, which is the merged value.
        Assert.Equal(1024, ((TextureImportEdits) row.Settings).MaxSize);
    }

    /// <summary>Only the marked members are written back into the block.</summary>
    [Fact]
    public void OnlyMarkedMembersAreWritten() {
        using var fixture = new EditorFixture();

        var path = fixture.WriteAsset(
            "Assets/hero.png",
            "bytes",
            "guid: 00000000000000000000000000000001\nmetaVersion: 1\nimporter: !TextureImporter\n  maxSize: 2048\n"
        );

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);
        var row = document.AddTarget("Android");

        ((TextureImportEdits) row.Settings).MaxSize = 1024;
        ((TextureImportEdits) row.Settings).GenerateMips = false;
        document.SetOverridden(row, "MaxSize", overridden: true);

        var yaml = document.ToYaml();

        Assert.Contains("target: Android", yaml, StringComparison.Ordinal);
        Assert.Contains("maxSize: 1024", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("generateMips: false", yaml, StringComparison.Ordinal);
    }

    /// <summary>Adding a target is undoable, and the undo takes the row away.</summary>
    [Fact]
    public void AddingATargetIsUndoable() {
        using var fixture = new EditorFixture();
        var path = fixture.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);
        document.AddTarget("iOS");

        Assert.Single(document.Overrides);

        document.Stack.Undo();
        Assert.Empty(document.Overrides);

        document.Stack.Redo();
        Assert.Single(document.Overrides);
    }

    /// <summary>Two rows for one target would make the merge order decide the result, so it is refused.</summary>
    [Fact]
    public void ATargetCannotBeAddedTwice() {
        using var fixture = new EditorFixture();
        var path = fixture.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);
        document.AddTarget("Android");

        Assert.Throws<InvalidOperationException>(() => document.AddTarget("android"));
    }

    /// <summary>Saving writes the sidecar beside the asset and nothing else.</summary>
    [Fact]
    public void SavingWritesTheSidecar() {
        using var fixture = new EditorFixture();
        var path = fixture.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);
        document.Texture.MaxSize = 128;
        document.Save();

        Assert.Contains("maxSize: 128", EditorFixture.Read(path + ".meta"), StringComparison.Ordinal);
        Assert.Equal("bytes", EditorFixture.Read(path));
    }

    /// <summary>The addressable block appears only once something is in it.</summary>
    [Fact]
    public void AnAssetWithNoAddressHasNoBlock() {
        using var fixture = new EditorFixture();
        var path = fixture.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);
        Assert.DoesNotContain("addressable", document.ToYaml(), StringComparison.Ordinal);

        document.Addressable.Address = "textures/hero";
        document.Addressable.Labels = "level1, preload";

        var yaml = document.ToYaml();

        Assert.Contains("address: textures/hero", yaml, StringComparison.Ordinal);
        Assert.Contains("level1", yaml, StringComparison.Ordinal);
        Assert.Contains("preload", yaml, StringComparison.Ordinal);
    }

    /// <summary>A model's parts come from the sidecar rather than from an import.</summary>
    [Fact]
    public void ModelPartsComeFromTheSidecar() {
        using var fixture = new EditorFixture();

        var path = fixture.WriteAsset(
            "Assets/hero.fbx",
            "bytes",
            "guid: 00000000000000000000000000000001\nmetaVersion: 1\nimporter: !ModelImporter\n"
            + "subAssets:\n  - id: 00000001\n    name: Hero_Mesh\n    type: Mesh\n"
            + "  - id: 00000002\n    name: Hero\n    type: Skeleton\n"
        );

        var document = new ModelImportDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal(2, document.SubAssets.Count);
        Assert.Contains(document.SubAssets, part => string.Equals(part.Type, "Skeleton", StringComparison.Ordinal));
    }

    /// <summary>⚠ A sidecar that will not parse opens rather than throwing, so it can be fixed.</summary>
    [Fact]
    public void ABrokenSidecarStillOpens() {
        using var fixture = new EditorFixture();
        var path = fixture.WriteAsset("Assets/hero.png", "bytes", "guid: x\n  bad: indentation\n\t tab: here\n");

        var document = new TextureImportDocument(fixture.Project, AssetId.New(), path);

        Assert.NotNull(document.Settings);
        Assert.Empty(document.Overrides);
    }
}
