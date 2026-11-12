// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

public sealed class ReferenceIndexTests {
    [Fact]
    public void AReferenceInAnAssetIsFoundInBothDirections() {
        using var project = new ProjectFixture();
        var texture = project.Add("Assets/hero.png");
        var material = project.Add("Assets/hero.vxmat", $"albedo: vx:{texture}\n");

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        var index = new ReferenceIndex();
        index.Build(database);

        Assert.Equal([material], index.ReferrersOf(texture));
        Assert.Equal([new AssetReference(texture)], index.ReferencesFrom(material));
        Assert.True(index.IsReferenced(texture));
        Assert.False(index.IsReferenced(material));
    }

    /// <summary>
    ///     A sidecar is a referrer too — a model importer's <c>materialMapping</c> holds references —
    ///     and they are attributed to the asset rather than to a file nobody thinks of as one.
    /// </summary>
    [Fact]
    public void AReferenceWrittenInImportSettingsCountsAsTheAssetsOwn() {
        using var project = new ProjectFixture();
        var material = project.Add("Assets/body.vxmat");
        var model = project.Add("Assets/hero.fbx");

        File.AppendAllText(
            project.Paths.Absolute("Assets/hero.fbx.meta"),
            $"  materialMapping:\n    Body: vx:{material}\n"
        );

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        var index = new ReferenceIndex();
        index.Build(database);

        Assert.Equal([model], index.ReferrersOf(material));
    }

    [Fact]
    public void ASubAssetReferenceKeepsTheSubAssetAndPointsAtTheAsset() {
        using var project = new ProjectFixture();
        var model = project.Add("Assets/hero.fbx");
        var scene = project.Add("Assets/level.vxscene", $"mesh: vx:{model}#2b9e5f13\n");

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        var index = new ReferenceIndex();
        index.Build(database);

        Assert.Equal([scene], index.ReferrersOf(model));
        var reference = Assert.Single(index.ReferencesFrom(scene));
        Assert.Equal(model, reference.Asset);
        Assert.Equal(new(0x2b9e5f13), reference.SubAsset);
    }

    [Fact]
    public void AnAssetReferencingItselfIsNotItsOwnReferrer() {
        using var project = new ProjectFixture();
        var guid = AssetId.New();
        project.Add("Assets/loop.vxmat", $"self: vx:{guid}\n", guid);

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        var index = new ReferenceIndex();
        index.Build(database);

        Assert.False(index.IsReferenced(guid));
    }

    [Theory]
    [InlineData("albedo: vx:9e8a44c9930c64e388ca034c5fe4c426\n", 1)]
    [InlineData("a: vx:9e8a44c9930c64e388ca034c5fe4c426#2b9e5f13\n", 1)]
    [InlineData("two: [vx:9e8a44c9930c64e388ca034c5fe4c426, vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081]\n", 2)]
    [InlineData("prose mentioning vx: but no guid\n", 0)]
    [InlineData("truncated: vx:9e8a44c9930c64\n", 0)]
    [InlineData("notHex: vx:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\n", 0)]
    [InlineData("", 0)]
    public void TheScanFindsExactlyWhatLooksLikeAReference(string text, int expected) =>
        Assert.Equal(expected, ReferenceIndex.Scan(text).Count);

    /// <summary>
    ///     A textual scan finds a reference inside a comment. That is a false positive in a report,
    ///     and it is the right side to be wrong on: the alternative is a missed reference in a "safe
    ///     to delete" answer, which corrupts a project.
    /// </summary>
    [Fact]
    public void AReferenceInACommentIsFoundToo() =>
        Assert.Single(ReferenceIndex.Scan("# used to point at vx:9e8a44c9930c64e388ca034c5fe4c426\n"));

    [Fact]
    public void ABinaryAssetSimplyYieldsNothing() {
        using var project = new ProjectFixture();
        project.Add("Assets/hero.png", "\u0089PNG\r\n\u001a\n\0\0\0\rIHDR");

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        var index = new ReferenceIndex();
        index.Build(database);

        Assert.Equal(0, index.Count);
    }
}
