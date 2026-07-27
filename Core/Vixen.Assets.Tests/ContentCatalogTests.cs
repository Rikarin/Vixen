// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>The index everything downstream of the content build is a lookup in.</summary>
public sealed class ContentCatalogTests {
    [Fact]
    public void AnAddressResolvesToItsChunk() {
        var catalog = Build(Entry("ui/hero", bundle: "UiCore", size: 4096));

        Assert.True(catalog.TryGet("ui/hero", out var entry));
        Assert.Equal("UiCore", entry.Bundle);
        Assert.Equal(4096, entry.Size);
        Assert.True(catalog.Contains("ui/hero"));
        Assert.False(catalog.Contains("ui/villain"));
    }

    /// <summary>
    ///     An address nothing answers to is a build problem, and the message says which of the two it
    ///     is — the build did not include it, or the caller spelled it differently.
    /// </summary>
    [Fact]
    public void AnAddressNothingAnswersToNamesItself() {
        var failure = Assert.Throws<AddressNotFoundException>(() => Build().Get("ui/missing"));

        Assert.Equal("ui/missing", failure.Address);
        Assert.Contains("ui/missing", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An address is how a caller names one thing. Two entries claiming it means the build made a
    ///     choice it did not record, and whichever the dictionary happened to keep is not a rule.
    /// </summary>
    [Fact]
    public void TwoEntriesCannotShareAnAddress() {
        var failure = Assert.Throws<ArgumentException>(
            () => Build(Entry("ui/hero"), Entry("ui/hero", bundle: "Other"))
        );

        Assert.Contains("ui/hero", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The label index is derived from the entries rather than stored beside them, so it cannot
    ///     disagree with them — which a stored one silently would after any edit.
    /// </summary>
    [Fact]
    public void LabelsIndexTheEntriesThatCarryThem() {
        var catalog = Build(
            Entry("ui/hero", labels: ["ui", "boot"]),
            Entry("ui/villain", labels: ["ui"]),
            Entry("level1/rock", labels: ["level1"])
        );

        Assert.Equal(["ui/hero", "ui/villain"], catalog.ByLabel("ui"));
        Assert.Equal(["ui/hero"], catalog.ByLabel("boot"));
        Assert.Empty(catalog.ByLabel("nothing-uses-this"));
        Assert.Equal(["boot", "level1", "ui"], catalog.Labels.Order(StringComparer.Ordinal));
    }

    /// <summary>
    ///     <para>
    ///         One star stops at a slash and two do not. That is what every shell and every build tool
    ///         means by it, and collapsing the distinction is how <c>PreloadAsync(["level1/*"])</c>
    ///         quietly downloads the whole game instead of one level's top-level assets.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("level1/*", new[] { "level1/rock", "level1/tree" })]
    [InlineData("level1/**", new[] { "level1/props/barrel", "level1/rock", "level1/tree" })]
    [InlineData("**", new[] { "level1/props/barrel", "level1/rock", "level1/tree", "ui/hero" })]
    [InlineData("*/hero", new[] { "ui/hero" })]
    [InlineData("level1/????", new[] { "level1/rock", "level1/tree" })]
    [InlineData("ui/hero", new[] { "ui/hero" })]
    [InlineData("nothing/*", new string[0])]
    public void AGlobSelectsWhatEveryShellWouldSelect(string pattern, string[] expected) {
        var catalog = Build(
            Entry("ui/hero"),
            Entry("level1/rock"),
            Entry("level1/tree"),
            Entry("level1/props/barrel")
        );

        Assert.Equal(expected, catalog.Match(pattern));
    }

    [Theory]
    [InlineData("a/b", "a/b", true)]
    [InlineData("a/b", "a/*", true)]
    [InlineData("a/b/c", "a/*", false)]
    [InlineData("a/b/c", "a/**", true)]
    [InlineData("a/b", "a/**", true)]
    [InlineData("a/b", "*", false)]
    [InlineData("ab", "*", true)]
    [InlineData("a/b", "?/?", true)]
    [InlineData("a/b", "???", false)]
    [InlineData("abc", "a*c", true)]
    [InlineData("abc", "a*d", false)]
    [InlineData("a", "a*", true)]
    [InlineData("", "*", true)]
    public void TheGlobRulesStatedOneByOne(string address, string pattern, bool expected) =>
        Assert.Equal(expected, ContentCatalog.Matches(address, pattern));

    /// <summary>
    ///     Dependency-first, so a caller loading the result in order never reaches something before
    ///     the thing it points at exists.
    /// </summary>
    [Fact]
    public void AClosureComesBackDependencyFirst() {
        var catalog = Build(
            Entry("scene", dependencies: ["material"]),
            Entry("material", dependencies: ["texture", "shader"]),
            Entry("texture"),
            Entry("shader")
        );

        var order = catalog.Closure("scene");

        Assert.Equal(4, order.Length);
        Assert.True(order.IndexOf("texture") < order.IndexOf("material"), "texture before material");
        Assert.True(order.IndexOf("shader") < order.IndexOf("material"), "shader before material");
        Assert.True(order.IndexOf("material") < order.IndexOf("scene"), "material before scene");
    }

    [Fact]
    public void AClosureVisitsAThingSharedByTwoRootsOnce() {
        var catalog = Build(
            Entry("a", dependencies: ["shared"]),
            Entry("b", dependencies: ["shared"]),
            Entry("shared")
        );

        Assert.Equal(["shared", "a", "b"], catalog.Closure("a", "b"));
    }

    /// <summary>
    ///     A dependency the build dropped is skipped rather than thrown for. It is a build problem
    ///     and it should be reported as one, but failing the whole load turns one missing texture
    ///     into a black screen.
    /// </summary>
    [Fact]
    public void AClosureSkipsADependencyTheCatalogDoesNotHave() {
        var catalog = Build(Entry("scene", dependencies: ["material", "deleted"]), Entry("material"));

        Assert.Equal(["material", "scene"], catalog.Closure("scene"));
    }

    [Fact]
    public void ACycleIsRefusedWithTheChainThatMadeIt() {
        var catalog = Build(Entry("a", dependencies: ["b"]), Entry("b", dependencies: ["a"]));

        var failure = Assert.Throws<InvalidOperationException>(() => catalog.Closure("a"));

        Assert.Contains("depends on itself", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <para>
    ///         Counted per bundle rather than per address, because two addresses in the same bundle
    ///         cost one download. Summing entry sizes is the mistake that tells a player a four
    ///         megabyte pack is forty.
    ///     </para>
    /// </summary>
    [Fact]
    public void ADownloadIsCountedPerBundleAndNotPerAddress() {
        var catalog = Build(
            [
                Entry("dlc/a", bundle: "Dlc", provider: ContentProvider.Remote, size: 1_000_000),
                Entry("dlc/b", bundle: "Dlc", provider: ContentProvider.Remote, size: 1_000_000),
                Entry("ui/hero", bundle: "UiCore", size: 500_000)
            ],
            [Bundle("Dlc", size: 4_000_000), Bundle("UiCore", size: 9_000_000)]
        );

        Assert.Equal(4_000_000, catalog.DownloadSize(["dlc/a", "dlc/b"]));
    }

    /// <summary>Local content is already there, so it costs nothing to reach whatever its size is.</summary>
    [Fact]
    public void LocalContentCostsNothingToDownload() {
        var catalog = Build(
            [Entry("ui/hero", bundle: "UiCore", size: 500_000)],
            [Bundle("UiCore", size: 9_000_000)]
        );

        Assert.Equal(0, catalog.DownloadSize(["ui/hero"]));
    }

    [Fact]
    public void ABundleAlreadyOnTheDeviceIsNotCountedAgain() {
        var catalog = Build(
            [Entry("dlc/a", bundle: "Dlc", provider: ContentProvider.Remote)],
            [Bundle("Dlc", size: 4_000_000)]
        );

        Assert.Equal(0, catalog.DownloadSize(["dlc/a"], bundle => bundle.Name == "Dlc"));
        Assert.Equal(4_000_000, catalog.DownloadSize(["dlc/a"], _ => false));
    }

    /// <summary>
    ///     A remote dependency of a local address still has to be downloaded, which is why the size
    ///     is computed over the closure rather than over the addresses asked for.
    /// </summary>
    [Fact]
    public void ARemoteDependencyOfALocalAddressIsStillADownload() {
        var catalog = Build(
            [
                Entry("scene", bundle: "Boot", dependencies: ["dlc/prop"]),
                Entry("dlc/prop", bundle: "Dlc", provider: ContentProvider.Remote)
            ],
            [Bundle("Boot", size: 100), Bundle("Dlc", size: 4_000_000)]
        );

        Assert.Equal(4_000_000, catalog.DownloadSize(["scene"]));
    }

    /// <summary>
    ///     <para>
    ///         The content update. An address in both catalogs takes the update's version and an
    ///         address only in the shipped one survives, which is what lets a patch replace one asset
    ///         without shipping every asset.
    ///     </para>
    ///     <para>
    ///         An update also cannot make an address disappear: the shipped application still has the
    ///         bundle on disk, and a runtime that forgot the address would fail to load something
    ///         that is sitting right there.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnUpdateReplacesWhatItMentionsAndLeavesTheRest() {
        var shipped = Build(
            Entry("ui/hero", bundle: "UiCore", size: 100),
            Entry("ui/villain", bundle: "UiCore", size: 200)
        );

        var update = Build(Entry("ui/hero", bundle: "UiCore_v2", size: 150));

        var merged = shipped.MergedWith(update);

        Assert.Equal("UiCore_v2", merged.Get("ui/hero").Bundle);
        Assert.Equal(150, merged.Get("ui/hero").Size);
        Assert.Equal("UiCore", merged.Get("ui/villain").Bundle);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void AnUpdateBringsItsOwnBundlesAndItsOwnBuildHash() {
        var shipped = Build([Entry("ui/hero", bundle: "UiCore")], [Bundle("UiCore", size: 10)]);

        var update = new ContentCatalog(
            CatalogFormat.Version,
            new(7, 7),
            "Windows",
            [Entry("ui/hero", bundle: "Patch")],
            [Bundle("Patch", size: 20)]
        );

        var merged = shipped.MergedWith(update);

        Assert.Equal(new ObjectId(7, 7), merged.BuildHash);
        Assert.True(merged.TryGetBundle("Patch", out _));
        Assert.True(merged.TryGetBundle("UiCore", out _));
    }

    /// <summary>
    ///     Applying an Android catalog to a Windows one would resolve addresses to chunks in a format
    ///     the device cannot read — a build mix-up that would otherwise surface as a corrupt texture.
    /// </summary>
    [Fact]
    public void AnUpdateForAnotherTargetIsRefused() {
        var shipped = Build(Entry("ui/hero"));

        var update = new ContentCatalog(CatalogFormat.Version, default, "Android/Vulkan", [Entry("ui/hero")], []);

        var failure = Assert.Throws<ArgumentException>(() => shipped.MergedWith(update));

        Assert.Contains("Android/Vulkan", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateInAnotherFormatVersionIsRefused() {
        var shipped = Build(Entry("ui/hero"));

        var update = new ContentCatalog(CatalogFormat.Version + 1, default, "Windows", [Entry("ui/hero")], []);

        var failure = Assert.Throws<ArgumentException>(() => shipped.MergedWith(update));

        Assert.Contains("migration", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Merging produces a third catalog; neither input is touched.</summary>
    [Fact]
    public void MergingChangesNeitherInput() {
        var shipped = Build(Entry("ui/hero", bundle: "UiCore"));
        var update = Build(Entry("ui/hero", bundle: "Patch"));

        shipped.MergedWith(update);

        Assert.Equal("UiCore", shipped.Get("ui/hero").Bundle);
        Assert.Equal("Patch", update.Get("ui/hero").Bundle);
    }

    /// <summary>
    ///     <para>
    ///         Two entries with the same contents are equal, and this is written out because the
    ///         compiler's generated version is wrong for these types. A record compares its members
    ///         with <c>Equals</c>, and <c>ImmutableArray</c>'s compares the identity of its backing
    ///         array rather than its contents — so two entries read from the same file twice would be
    ///         unequal.
    ///     </para>
    ///     <para>
    ///         That is not an abstract concern. The first question anyone asks of a content update is
    ///         "did this actually change anything?", and the default answer would have been yes,
    ///         always, for every asset in the game.
    ///     </para>
    /// </summary>
    [Fact]
    public void TwoEntriesWithTheSameContentsAreEqual() {
        var one = Entry("ui/hero", dependencies: ["ui/shader"], labels: ["ui"]);
        var other = Entry("ui/hero", dependencies: ["ui/shader"], labels: ["ui"]);

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
        Assert.NotEqual(one, Entry("ui/hero", dependencies: ["ui/other"], labels: ["ui"]));
        Assert.NotEqual(one, Entry("ui/hero", dependencies: ["ui/shader"], labels: []));
    }

    [Fact]
    public void AndSoAreTwoBundles() {
        var one = Bundle("Dlc", size: 10) with { Dependencies = ["UiCore"] };
        var other = Bundle("Dlc", size: 10) with { Dependencies = ["UiCore"] };

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
        Assert.NotEqual(one, one with { Dependencies = ["Something"] });
    }

    static CatalogEntry Entry(
        string address,
        string bundle = "Main",
        ContentProvider provider = ContentProvider.Local,
        long size = 0,
        string[]? dependencies = null,
        string[]? labels = null
    ) =>
        new(address, default, bundle, provider, [.. dependencies ?? []], [.. labels ?? []], size);

    static CatalogBundle Bundle(string name, long size) =>
        new(name, $"https://cdn.example/{name}", default, size, 0, CompressionMethod.Lz4, []);

    static ContentCatalog Build(params CatalogEntry[] entries) => Build(entries, []);

    static ContentCatalog Build(IEnumerable<CatalogEntry> entries, IEnumerable<CatalogBundle> bundles) =>
        new(CatalogFormat.Version, default, "Windows", entries, bundles);
}
