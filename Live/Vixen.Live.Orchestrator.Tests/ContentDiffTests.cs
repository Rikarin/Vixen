// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     Doc 27 § Testing: <i>"property tests over the additive classifier — a corpus of catalog pairs
///     with the expected verdict, including the ones that must be rejected."</i>
/// </summary>
public class ContentDiffTests {
    /// <summary>Doc 28's entire premise: adding an item is a catalog update and nothing else.</summary>
    [Fact]
    public void A_new_address_is_additive_without_qualification() {
        var deltas = ContentDiff.Compare(
            [Definition("items/sword", 1)],
            [Definition("items/sword", 1), Definition("items/axe", 2)]
        );

        var added = Assert.Single(deltas);

        Assert.Equal("items/axe", added.Address);
        Assert.Equal(ContentChange.Added, added.Change);
        Assert.True(added.Additive);
        Assert.True(ContentDiff.IsAdditive(deltas));
        Assert.Empty(ContentDiff.Blockers(deltas));
    }

    [Fact]
    public void An_identical_catalog_has_no_deltas() {
        var catalog = new[] { Definition("items/sword", 1), Prefab("props/barrel", 2) };

        Assert.Empty(ContentDiff.Compare(catalog, catalog));
        Assert.True(ContentDiff.IsAdditive(ContentDiff.Compare(catalog, catalog)));
    }

    /// <summary>A rebalance: the numbers moved, the shape did not.</summary>
    [Fact]
    public void A_definition_whose_content_changed_reloads_live() {
        var deltas = ContentDiff.Compare([Definition("items/sword", 1)], [Definition("items/sword", 2)]);
        var changed = Assert.Single(deltas);

        Assert.Equal(ContentChange.Modified, changed.Change);
        Assert.True(changed.Additive);
    }

    /// <summary>
    ///     ⚠ The one that must be rejected: anything already holding one of these now holds the wrong
    ///     thing, and a realm cannot be told to re-read a layout that entities were built against.
    /// </summary>
    [Fact]
    public void A_definition_whose_schema_changed_needs_a_drain() {
        var deltas = ContentDiff.Compare(
            [Definition("items/sword", 1, "damage:int")],
            [Definition("items/sword", 1, "damage:int,reach:float")]
        );

        var changed = Assert.Single(deltas);

        Assert.Equal(ContentChange.Reshaped, changed.Change);
        Assert.False(changed.Additive);
        Assert.False(ContentDiff.IsAdditive(deltas));
        Assert.Contains("shape", Assert.Single(ContentDiff.Blockers(deltas)), StringComparison.Ordinal);
    }

    /// <summary>A prefab is baked into entities that already exist.</summary>
    [Fact]
    public void A_changed_prefab_needs_a_drain_even_though_its_shape_held() {
        var deltas = ContentDiff.Compare([Prefab("props/barrel", 1)], [Prefab("props/barrel", 2)]);
        var changed = Assert.Single(deltas);

        Assert.Equal(ContentChange.Modified, changed.Change);
        Assert.False(changed.Additive);
        Assert.Contains("already exist", changed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_scene_needs_a_drain_because_a_realm_is_simulating_it() {
        var deltas = ContentDiff.Compare(
            [new("maps/queensdale", 1, "scene")],
            [new("maps/queensdale", 2, "scene")]
        );

        Assert.False(ContentDiff.IsAdditive(deltas));
    }

    /// <summary>
    ///     ⚠ Whether an address is in use is a question about every entity in every world in the
    ///     fleet, and this compares two files. A classifier that guessed would be guessing about the
    ///     case that deletes a player's sword.
    /// </summary>
    [Fact]
    public void A_removal_is_never_additive_even_of_something_nothing_uses() {
        var deltas = ContentDiff.Compare(
            [Definition("items/sword", 1), Definition("items/unused", 2)],
            [Definition("items/sword", 1)]
        );

        var removed = Assert.Single(deltas);

        Assert.Equal(ContentChange.Removed, removed.Change);
        Assert.False(removed.Additive);
    }

    /// <summary>
    ///     One address that was a prefab and is now a scene is two different things wearing one name,
    ///     and every reference to it now means something else.
    /// </summary>
    [Fact]
    public void An_address_that_changed_kind_is_reshaped_rather_than_modified() {
        var deltas = ContentDiff.Compare([Prefab("things/x", 1)], [new("things/x", 1, "scene")]);
        var changed = Assert.Single(deltas);

        Assert.Equal(ContentChange.Reshaped, changed.Change);
        Assert.False(changed.Additive);
        Assert.Contains("prefab", changed.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ There is no partial apply: a catalog is one `BuildHash`, so applying the additive half
    ///     would leave the fleet on a content version that never existed.
    /// </summary>
    [Fact]
    public void One_blocking_change_makes_the_whole_update_non_additive() {
        var deltas = ContentDiff.Compare(
            [Definition("items/sword", 1), Prefab("props/barrel", 1)],
            [Definition("items/sword", 1), Definition("items/axe", 2), Prefab("props/barrel", 2)]
        );

        Assert.Equal(2, deltas.Length);
        Assert.False(ContentDiff.IsAdditive(deltas));
        Assert.Single(ContentDiff.Blockers(deltas));
    }

    [Fact]
    public void Deltas_come_back_in_a_stable_order() {
        var deltas = ContentDiff.Compare(
            [],
            [Definition("z", 1), Definition("a", 2), Definition("m", 3)]
        );

        Assert.Equal(["a", "m", "z"], deltas.Select(delta => delta.Address));
    }

    /// <summary>
    ///     ⚠ The rule that keeps a projection from a real catalog safe. A <c>CatalogEntry</c> has an
    ///     address, a content id, a bundle and a size — nothing that says whether a definition gained
    ///     a field. Calling such a change additive is the unrecoverable direction, so an unknown shape
    ///     is never additive even for a kind that would otherwise reload live.
    /// </summary>
    [Fact]
    public void An_entry_with_no_recorded_shape_is_never_additive() {
        var deltas = ContentDiff.Compare(
            [new("items/sword", 1, "definition")],
            [new("items/sword", 2, "definition")]
        );

        var changed = Assert.Single(deltas);

        Assert.Equal(ContentChange.Modified, changed.Change);
        Assert.False(changed.Additive);
        Assert.Contains("shape is not recorded", changed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_that_appears_where_there_was_none_is_not_additive_either() {
        var deltas = ContentDiff.Compare(
            [new("items/sword", 1, "definition")],
            [new("items/sword", 1, "definition", "damage:int")]
        );

        Assert.False(ContentDiff.IsAdditive(deltas));
    }

    // ── Properties ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The safety property, stated as one: a diff is additive <b>only</b> when every change in it
    ///     is an addition or a live-reloadable modification. Nothing else may ever be.
    /// </summary>
    [Theory]
    [InlineData(20260804)]
    [InlineData(7)]
    [InlineData(0xBEEF)]
    public void Only_additions_and_reloadable_modifications_are_ever_additive(int seed) {
        var random = new Random(seed);

        for (var round = 0; round < 400; round++) {
            var (before, after) = Catalogs(random);

            foreach (var delta in ContentDiff.Compare(before, after)) {
                var permitted = delta.Change == ContentChange.Added
                    || (delta.Change == ContentChange.Modified
                        && ContentDiff.LiveReloadable.Contains(delta.Kind, StringComparer.OrdinalIgnoreCase));

                // The generator always records a shape, so the unknown-shape rule never fires here —
                // asserted rather than assumed, because a corpus that stopped exercising the
                // reloadable path would make this property vacuously true.

                Assert.Equal(permitted, delta.Additive);

                if (!delta.Additive) {
                    Assert.NotEmpty(delta.Reason);
                }
            }
        }
    }

    /// <summary>
    ///     Comparing a catalog with itself is empty, whatever is in it — the property that stops a
    ///     republished-but-unchanged catalog from demanding a drain.
    /// </summary>
    [Theory]
    [InlineData(20260804)]
    [InlineData(11)]
    public void A_catalog_never_differs_from_itself(int seed) {
        var random = new Random(seed);

        for (var round = 0; round < 400; round++) {
            var (catalog, _) = Catalogs(random);

            Assert.Empty(ContentDiff.Compare(catalog, catalog));
        }
    }

    /// <summary>Every changed address appears exactly once, whichever side it changed on.</summary>
    [Theory]
    [InlineData(20260804)]
    [InlineData(3)]
    public void Every_changed_address_is_reported_once(int seed) {
        var random = new Random(seed);

        for (var round = 0; round < 400; round++) {
            var (before, after) = Catalogs(random);
            var deltas = ContentDiff.Compare(before, after);

            Assert.Equal(deltas.Length, deltas.Select(delta => delta.Address).Distinct(StringComparer.Ordinal).Count());
        }
    }

    static (ContentEntry[] Before, ContentEntry[] After) Catalogs(Random random) {
        string[] kinds = ["definition", "prefab", "scene", "bundle", "table"];
        var count = random.Next(1, 12);
        var before = new List<ContentEntry>();
        var after = new List<ContentEntry>();

        for (var index = 0; index < count; index++) {
            var address = $"content/{index}";
            var kind = kinds[random.Next(kinds.Length)];
            var entry = new ContentEntry(address, (ulong)random.Next(1, 5), kind, $"v{random.Next(1, 3)}");

            switch (random.Next(5)) {
                case 0:                                   // only in the new one
                    after.Add(entry);

                    break;
                case 1:                                   // only in the old one
                    before.Add(entry);

                    break;
                case 2:                                   // changed content
                    before.Add(entry);
                    after.Add(entry with { Hash = entry.Hash + 1 });

                    break;
                case 3:                                   // changed shape
                    before.Add(entry);
                    after.Add(entry with { Schema = entry.Schema + "!" });

                    break;
                default:                                  // unchanged
                    before.Add(entry);
                    after.Add(entry);

                    break;
            }
        }

        return ([.. before], [.. after]);
    }

    static ContentEntry Definition(string address, ulong hash, string schema = "v1") =>
        new(address, hash, "definition", schema);

    static ContentEntry Prefab(string address, ulong hash) => new(address, hash, "prefab", "v1");
}
