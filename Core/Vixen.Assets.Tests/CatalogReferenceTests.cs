// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>Turning a <c>vx:</c> reference into something loadable.</summary>
/// <remarks>
///     The direction nothing in the runtime could go before. Every reference a game holds — a mesh on
///     an entity, a clip on an audio source — is an <see cref="AssetId" />, because that is what
///     survives renaming the file; everything that loads takes an address a build chose.
/// </remarks>
public sealed class CatalogReferenceTests {
    static readonly AssetId Hero = new(new("11111111-1111-1111-1111-111111111111"));
    static readonly AssetId Crate = new(new("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void AnAssetsMainObjectResolvesToItsAddress() {
        var catalog = Catalog(
            Entry("characters/hero", new AssetReference(Hero)),
            Entry("props/crate", new AssetReference(Crate))
        );

        Assert.True(catalog.TryGetAddress(new AssetReference(Hero), out var address));
        Assert.Equal("characters/hero", address);
    }

    /// <remarks>A component holding a bare id means the main object, which is the common case.</remarks>
    [Fact]
    public void AnAssetIdResolvesWithoutSpellingOutTheMainSubAsset() {
        var catalog = Catalog(Entry("characters/hero", new AssetReference(Hero)));

        Assert.True(catalog.TryGetAddress(Hero, out var address));
        Assert.Equal("characters/hero", address);
    }

    /// <remarks>
    ///     The sub-asset case, and the one that makes the reference worth storing rather than derived:
    ///     the address carries the part's <i>name</i> and the reference carries its <i>id</i>, so
    ///     neither can be computed from the other.
    /// </remarks>
    [Fact]
    public void APartOfAnAssetResolvesToItsOwnAddress() {
        var mesh = new AssetReference(Hero, new SubAssetId(0x2b9e5f13));

        var catalog = Catalog(
            Entry("characters/hero", new AssetReference(Hero)),
            Entry("characters/hero#Hero_Mesh", mesh)
        );

        Assert.True(catalog.TryGetAddress(mesh, out var address));
        Assert.Equal("characters/hero#Hero_Mesh", address);
    }

    [Fact]
    public void AReferenceNothingShippedIsNotFoundRatherThanEmpty() {
        var catalog = Catalog(Entry("characters/hero", new AssetReference(Hero)));

        Assert.False(catalog.TryGetAddress(new AssetReference(Crate), out var address));
        Assert.Equal(string.Empty, address);
    }

    [Fact]
    public void ANullReferenceResolvesToNothing() {
        var catalog = Catalog(Entry("characters/hero", new AssetReference(Hero)));

        Assert.False(catalog.TryGetAddress(AssetReference.Null, out _));
    }

    /// <remarks>
    ///     ⚠ Most entries have no reference — anything a test builds by hand, any chunk no authored
    ///     asset claims — so treating "no identity" as an identity would make the second one a build
    ///     error.
    /// </remarks>
    [Fact]
    public void EntriesWithNoReferenceDoNotCollideWithEachOther() {
        var catalog = Catalog(Entry("one", AssetReference.Null), Entry("two", AssetReference.Null));

        Assert.Equal(2, catalog.Count);
    }

    /// <remarks>
    ///     The mirror of the duplicate-address refusal. A reference is what a component holds, so two
    ///     addresses answering one of them is a build that cannot say what a component points at.
    /// </remarks>
    [Fact]
    public void TwoAddressesClaimingOneReferenceIsRefused() {
        var error = Assert.Throws<ArgumentException>(() =>
            Catalog(Entry("characters/hero", new AssetReference(Hero)), Entry("old/hero", new AssetReference(Hero)))
        );

        Assert.Contains("both claim to be", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceSurvivesTheFileFormat() {
        var mesh = new AssetReference(Hero, new SubAssetId(0x2b9e5f13));
        var written = CatalogFormat.Write(Catalog(Entry("characters/hero#Hero_Mesh", mesh)));

        var read = CatalogFormat.Read(written);

        Assert.True(read.TryGetAddress(mesh, out var address));
        Assert.Equal("characters/hero#Hero_Mesh", address);
    }

    /// <remarks>
    ///     ⚠ <b>The case a merge keyed by address cannot see on its own.</b> An update that moves an
    ///     asset leaves the old address in the merged catalog, and without dropping it the two entries
    ///     would both claim the reference — which the constructor refuses, so the merge would throw
    ///     rather than produce a catalog. The reference is what says they are the same asset.
    /// </remarks>
    [Fact]
    public void AnUpdateThatMovesAnAssetDropsTheAddressItLeft() {
        var shipped = Catalog(Entry("characters/hero", new AssetReference(Hero)));
        var update = Catalog(Entry("characters/hero_v2", new AssetReference(Hero)));

        var merged = shipped.MergedWith(update);

        Assert.True(merged.TryGetAddress(Hero, out var address));
        Assert.Equal("characters/hero_v2", address);
        Assert.False(merged.TryGet("characters/hero", out _));
    }

    [Fact]
    public void AnUpdateInPlaceKeepsTheAddressAndTheReference() {
        var shipped = Catalog(Entry("characters/hero", new AssetReference(Hero)));
        var update = Catalog(Entry("characters/hero", new AssetReference(Hero)));

        var merged = shipped.MergedWith(update);

        Assert.Equal(1, merged.Count);
        Assert.True(merged.TryGetAddress(Hero, out var address));
        Assert.Equal("characters/hero", address);
    }

    static ContentCatalog Catalog(params CatalogEntry[] entries) =>
        new(CatalogFormat.Version, default, "Windows", entries, []);

    static CatalogEntry Entry(string address, AssetReference reference) =>
        new(address, default, "bundle", ContentProvider.Local, [], ImmutableArray<string>.Empty, 0, reference);
}
