// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>The vocabulary, and the <c>default(T)</c> wart every value type here has to survive.</summary>
public class LedgerAccountTests {
    static readonly PlayerKey Someone = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void A_players_account_is_valid_and_a_worlds_is_too() {
        Assert.True(LedgerAccount.Of(Someone).IsValid);
        Assert.True(LedgerAccount.Of(LedgerAccount.Loot).IsValid);
        Assert.True(LedgerAccount.Of(Someone).IsPlayer);
        Assert.False(LedgerAccount.Of(LedgerAccount.Loot).IsPlayer);
    }

    [Fact]
    public void Nowhere_is_not_an_account() {
        Assert.False(LedgerAccount.Nowhere.IsValid);
        Assert.False(default(LedgerAccount).IsValid);
        Assert.Equal("nowhere", LedgerAccount.Nowhere.ToString());
    }

    /// <summary>
    ///     The same latent bug the Orleans surrogate round-trip found in <c>RealmEndpoint</c>: a
    ///     struct's property initialisers do not run for <c>default</c>, so a hand-written equality
    ///     is the only thing that makes a zeroed field and a constructed empty one one dictionary
    ///     entry rather than two.
    /// </summary>
    [Fact]
    public void Default_equals_a_constructed_empty_one() {
        Assert.Equal(default, LedgerAccount.Of(""));
        Assert.Equal(default(LedgerAccount).GetHashCode(), LedgerAccount.Of("").GetHashCode());
        Assert.Equal(default, AssetId.None);
        Assert.Equal(default(AssetId).GetHashCode(), AssetId.None.GetHashCode());
        Assert.Equal(default, new IdempotencyKey(PlayerKey.None, "", ""));

        HashSet<LedgerAccount> set = [default, LedgerAccount.Of("")];

        Assert.Single(set);
    }

    [Fact]
    public void An_account_round_trips_through_its_text() {
        foreach (var account in new[] { LedgerAccount.Of(Someone), LedgerAccount.Of(LedgerAccount.Vendor) }) {
            Assert.True(LedgerAccount.TryParse(account.ToString(), out var read));
            Assert.Equal(account, read);
        }
    }

    [Fact]
    public void Nothing_parses_as_no_account() {
        Assert.False(LedgerAccount.TryParse(null, out _));
        Assert.False(LedgerAccount.TryParse("", out _));
    }

    /// <summary>
    ///     A world name that happens to look like a player key would be read back as a player, so the
    ///     parse tries that first and the two namespaces must not overlap. <c>world/</c>-prefixed
    ///     names cannot collide with <c>guid/guid</c>, which is why the constants are spelled that way.
    /// </summary>
    [Fact]
    public void A_world_name_is_not_mistaken_for_a_player() {
        Assert.True(LedgerAccount.TryParse(LedgerAccount.Escrow, out var escrow));
        Assert.False(escrow.IsPlayer);
        Assert.Equal(LedgerAccount.Escrow, escrow.World);
    }

    [Fact]
    public void An_asset_is_its_address() {
        var asset = new AssetId("items/greatsword");

        Assert.True(asset.IsValid);
        Assert.Equal("items/greatsword", asset.ToString());
        Assert.False(AssetId.None.IsValid);
        Assert.Equal("nothing", AssetId.None.ToString());
    }

    [Fact]
    public void An_operation_key_needs_all_three_parts() {
        Assert.True(new IdempotencyKey(Someone, "trade", "42").IsValid);
        Assert.False(new IdempotencyKey(PlayerKey.None, "trade", "42").IsValid);
        Assert.False(new IdempotencyKey(Someone, "", "42").IsValid);
        Assert.False(new IdempotencyKey(Someone, "trade", "").IsValid);
    }
}
