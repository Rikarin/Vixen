// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.Assets.Content;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What a <c>.vxgroup</c> document does to its file.</summary>
public class AddressableGroupDocumentTests {
    /// <summary>A new group is named after its file rather than after nothing.</summary>
    [Fact]
    public void ANewGroupIsNamedAfterItsFile() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/UiCore.vxgroup", string.Empty);

        var document = new AddressableGroupDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal("UiCore", document.Policy.Name);
        Assert.Null(document.LoadError);
    }

    /// <summary>The policy in the file is what the editors show.</summary>
    [Fact]
    public void ThePolicyIsRead() {
        using var fixture = new EditorFixture();

        var path = fixture.Write(
            "Assets/Remote.vxgroup",
            "name: Remote\nloadPath: Remote\npacking: PackTogetherByLabel\nremoteUrl: https://cdn.example/\n"
        );

        var document = new AddressableGroupDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal("Remote", document.Policy.Name);
        Assert.Equal(ContentProvider.Remote, document.Policy.LoadPath);
        Assert.Equal(BundlePacking.PackTogetherByLabel, document.Policy.Packing);
        Assert.Equal("https://cdn.example/", document.Policy.RemoteUrl);
    }

    /// <summary>What it writes binds back to the record the build reads.</summary>
    [Fact]
    public void ItRoundTripsThroughTheBuildsOwnType() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/UiCore.vxgroup", string.Empty);

        var document = new AddressableGroupDocument(fixture.Project, AssetId.New(), path);

        document.Policy.Compression = CompressionMethod.None;
        document.Policy.IncludeInBuild = false;
        document.Save();

        var group = Vixen.Core.Yaml.YamlSerializer.Parse<AddressableGroup>(EditorFixture.Read(path));

        Assert.Equal("UiCore", group.Name);
        Assert.Equal(CompressionMethod.None, group.Compression);
        Assert.False(group.IncludeInBuild);
    }

    /// <summary>The document's own view of the policy is the record, so nothing converts twice.</summary>
    [Fact]
    public void ToGroupIsThePolicy() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/UiCore.vxgroup", string.Empty);

        var document = new AddressableGroupDocument(fixture.Project, AssetId.New(), path);
        document.Policy.UpdateRestriction = UpdateRestriction.CannotChangePostRelease;

        Assert.Equal(UpdateRestriction.CannotChangePostRelease, document.ToGroup().UpdateRestriction);
    }

    /// <summary>⚠ A file that will not bind opens with the defaults and says why.</summary>
    [Fact]
    public void ABrokenFileOpensAndExplains() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/UiCore.vxgroup", "packing: NotAPacking\n");

        var document = new AddressableGroupDocument(fixture.Project, AssetId.New(), path);

        Assert.NotNull(document.LoadError);
        Assert.Equal("UiCore", document.Policy.Name);
    }
}
