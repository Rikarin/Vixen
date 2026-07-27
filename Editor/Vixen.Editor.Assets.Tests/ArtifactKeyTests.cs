// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class ArtifactKeyTests {
    static readonly ObjectId SourceHash = ArtifactKey.HashOf("the source bytes");
    static readonly ObjectId SettingsHash = ArtifactKey.HashOf("maxSize: 2048\n");

    static ArtifactKey Key(
        string importer = "TextureImporter",
        int version = 3,
        ObjectId? source = null,
        ObjectId? settings = null,
        string target = "Windows",
        IEnumerable<ObjectId>? dependencies = null
    ) =>
        ArtifactKey.Compute(importer, version, source ?? SourceHash, settings ?? SettingsHash, target, dependencies);

    [Fact]
    public void TheSameInputsGiveTheSameKey() => Assert.Equal(Key(), Key());

    /// <summary>
    ///     Every part of the key names something that, when it changes, must produce a different
    ///     artefact. A part that did not would be a cache hit on a stale result.
    /// </summary>
    [Fact]
    public void EveryPartOfTheKeyChangesIt() {
        var baseline = Key();

        Assert.NotEqual(baseline, Key(importer: "ModelImporter"));
        Assert.NotEqual(baseline, Key(version: 4));
        Assert.NotEqual(baseline, Key(source: ArtifactKey.HashOf("different bytes")));
        Assert.NotEqual(baseline, Key(settings: ArtifactKey.HashOf("maxSize: 1024\n")));
        Assert.NotEqual(baseline, Key(target: "Android"));
        Assert.NotEqual(baseline, Key(dependencies: [ArtifactKey.HashOf("a")]));
    }

    /// <summary>
    ///     The dependencies are sorted, and that is not tidiness: a key that depended on the order a
    ///     set enumerated in would differ between two machines with identical inputs, which turns a
    ///     shared CI artefact cache from a speed-up into a source of confusion.
    /// </summary>
    [Fact]
    public void TheOrderTheDependenciesArriveInDoesNotMatter() {
        var first = ArtifactKey.HashOf("a");
        var second = ArtifactKey.HashOf("b");
        var third = ArtifactKey.HashOf("c");

        Assert.Equal(Key(dependencies: [first, second, third]), Key(dependencies: [third, first, second]));
    }

    [Fact]
    public void ChangingOneDependencyChangesTheKey() =>
        Assert.NotEqual(
            Key(dependencies: [ArtifactKey.HashOf("a"), ArtifactKey.HashOf("b")]),
            Key(dependencies: [ArtifactKey.HashOf("a"), ArtifactKey.HashOf("c")])
        );

    /// <summary>
    ///     The parts are separated, so that <c>("Model", 1)</c> and <c>("Mode", 11)</c> are different
    ///     inputs rather than the same concatenation.
    /// </summary>
    [Fact]
    public void TwoPartsRunningTogetherAreNotOnePart() =>
        Assert.NotEqual(Key(importer: "Model", version: 11), Key(importer: "Model1", version: 1));

    [Fact]
    public void HashingBytesAndHashingAStreamAgree() {
        var bytes = "the source bytes"u8.ToArray();
        using var stream = new MemoryStream(bytes);

        Assert.Equal(ArtifactKey.HashOf(bytes.AsSpan()), ArtifactKey.HashOf(stream));
        Assert.Equal(SourceHash, ArtifactKey.HashOf(bytes.AsSpan()));
    }

    [Fact]
    public void AKeyIsThirtyTwoLowercaseHexDigits() {
        var text = Key().ToString();

        Assert.Equal(32, text.Length);
        Assert.Equal(text.ToLowerInvariant(), text);
    }
}
