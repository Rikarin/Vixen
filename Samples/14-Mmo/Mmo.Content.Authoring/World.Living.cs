// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>A drop table per family and per boss, plus the gathering nodes and salvage.</summary>
    /// <param name="items">Everything that can drop.</param>
    /// <returns>The tables, by the family or boss they belong to.</returns>
    public Dictionary<string, string> Loot(ItemCatalogue items) {
        var byOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var family in Tables.Families) {
            var zone = Tables.Zones.First(entry => entry.Id == family.Zone);
            var armour = items.Armour.Where(address => address.Contains($"/{zone.Id}-", StringComparison.Ordinal)).ToArray();
            var reagent = items.Reagents.Where(address => address.Contains($"/{zone.Id}-", StringComparison.Ordinal)).ToArray();

            // ── Trash ────────────────────────────────────────────────────────────────────────────
            var trash = $"loot/{family.Id}-trash";
            var yaml = new Yaml("LootTableDefinition", $"What a {family.Name.ToLowerInvariant()} drops.")
                .Put("displayName", $"{family.Name}")
                .Put("rolls", 1);

            var first = true;

            foreach (var address in reagent.Take(3)) {
                yaml.Item(first ? "entries" : null).Put("item", address).Put("weight", 40f).Put("minimum", 1).Put("maximum", 3).Close();
                first = false;
            }

            foreach (var address in armour.Take(2)) {
                yaml.Item(first ? "entries" : null).Put("item", address).Put("weight", 3f).Close();
                first = false;
            }

            Write(trash, ".vxloot", yaml);
            byOwner[family.Id] = trash;

            // ── The family's rare drop, on a pity policy ─────────────────────────────────────────
            var prize = $"loot/{family.Id}-prize";
            var weapons = items.Weapons.Where(address => address.Contains($"/{zone.Id}-", StringComparison.Ordinal)).ToArray();
            var prizeYaml = new Yaml("LootTableDefinition", $"A {family.Name.ToLowerInvariant()}'s rare drop. Pity, because doc 28 says a counter that resets on a crash is a support ticket.")
                .Put("displayName", $"{family.Name} (rare)")
                .Put("rolls", 1)
                .Open("pity")
                .Put("attemptsBefore", 8)
                .Put("rampPerAttempt", 0.06f)
                .Put("guaranteedAt", 20)
                .Close();

            first = true;

            foreach (var address in weapons.Take(2)) {
                prizeYaml.Item(first ? "entries" : null).Put("item", address).Put("weight", 5f).Put("usesPity", true).Close();
                first = false;
            }

            foreach (var address in armour.Skip(2).Take(3)) {
                prizeYaml.Item(first ? "entries" : null).Put("item", address).Put("weight", 30f).Close();
                first = false;
            }

            Write(prize, ".vxloot", prizeYaml);
            byOwner[$"{family.Id}-prize"] = prize;
        }

        // ── Gathering nodes ──────────────────────────────────────────────────────────────────────
        foreach (var zone in Tables.Zones) {
            foreach (var kind in new[] { "ore", "herb", "hide" }) {
                var address = $"loot/nodes/{zone.Id}-{kind}";
                var item = items.Reagents.FirstOrDefault(entry => entry.EndsWith($"{zone.Id}-{kind}", StringComparison.Ordinal));

                if (item is null) {
                    continue;
                }

                Write(
                    address,
                    ".vxloot",
                    new Yaml("LootTableDefinition", $"What a {zone.Name} {kind} node yields.")
                        .Put("displayName", $"{zone.Name} {kind}")
                        .Put("rolls", 1)
                        .Item("entries")
                        .Put("item", item)
                        .Put("chance", 1f)
                        .Put("minimum", 2)
                        .Put("maximum", 4)
                        .Close()
                );

                byOwner[$"node-{zone.Id}-{kind}"] = address;
            }
        }

        // ── Salvage, one per armour class ────────────────────────────────────────────────────────
        foreach (var armour in Tables.Armours) {
            var yieldAddress = $"loot/salvage/{armour.Id}-yield";
            var reagent = armour.Id switch {
                "plate" => items.Reagents.First(address => address.EndsWith("-bar", StringComparison.Ordinal)),
                "leather" => items.Reagents.First(address => address.EndsWith("-leather", StringComparison.Ordinal)),
                _ => items.Reagents.First(address => address.EndsWith("-essence", StringComparison.Ordinal))
            };

            Write(
                yieldAddress,
                ".vxloot",
                new Yaml("LootTableDefinition", $"What salvaging {armour.Name.ToLowerInvariant()} gives back.")
                    .Put("displayName", $"Salvaged {armour.Name}")
                    .Put("rolls", 1)
                    .Item("entries")
                    .Put("item", reagent)
                    .Put("chance", 1f)
                    .Put("minimum", 1)
                    .Put("maximum", 3)
                    .Close()
            );

            Write(
                $"loot/salvage/{armour.Id}",
                ".vxdef",
                new Yaml("SalvageDefinition", $"Anything tagged {armour.Name} salvages into reagents.")
                    .List("itemTags", [$"Item.Armour.{armour.Name}"])
                    .Put("table", yieldAddress)
            );
        }

        return byOwner;
    }

    /// <summary>Everything that stands in the world and hits back.</summary>
    /// <param name="loot">Which table each family drops.</param>
    /// <param name="abilities">What each family casts.</param>
    /// <returns>The creature addresses, by family.</returns>
    /// <remarks>
    ///     ⚠ <b>Every one of these grants a tag, and that is the field the sample was missing.</b>
    ///     A Kill objective counts by tag, so a creature that grants nothing is a creature no quest
    ///     can be about — which is exactly the state the tree was in: four spawn tables naming
    ///     creatures that did not exist and every Kill objective waiting on a tag with no grantor.
    /// </remarks>
    public Dictionary<string, List<string>> Creatures(
        Dictionary<string, string> loot,
        Dictionary<string, List<string>> abilities
    ) {
        var byFamily = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var family in Tables.Families) {
            var zone = Tables.Zones.First(entry => entry.Id == family.Zone);
            var written = new List<string>();

            // Normal, elite, rare and a family boss: the four ranks a zone's population is made of.
            (string Id, string Rank, string Prefix, float Health, float Damage, float Experience)[] ranks = [
                ("", "Normal", "", 1f, 1f, 1f),
                ("-elite", "Elite", "Elder ", 4f, 1.8f, 3f),
                ("-rare", "Rare", "Grizzled ", 6f, 2.0f, 5f),
                ("-champion", "Boss", "Dread ", 18f, 3.0f, 20f)
            ];

            foreach (var (suffix, rank, prefix, health, damage, experience) in ranks) {
                var level = rank == "Normal" ? zone.Low + 1 : rank == "Boss" ? zone.High + 2 : zone.High;
                var address = $"creatures/{family.Id}{suffix}";

                var tags = new List<string> { family.Tag };

                // The parent tag too, so "kill twelve undead" counts a shade and a wight alike —
                // which is what the prefix test in GameplayTag is for.
                if (family.Tag.Count(character => character == '.') > 1) {
                    tags.Add(family.Tag[..family.Tag.LastIndexOf('.')]);
                }

                if (rank is "Elite" or "Boss") {
                    tags.Add("Creature.Elite");
                }

                Write(
                    address,
                    ".vxdef",
                    new Yaml("CreatureDefinition", $"{zone.Name}'s {family.Name.ToLowerInvariant()}, {rank.ToLowerInvariant()}.")
                        .Put("displayName", $"{prefix}{family.Name}")
                        .Put("rank", rank)
                        .Put("level", level)
                        .Put("health", Nice(60 * level * health))
                        .Put("damage", Nice(4 * level * damage))
                        .Put("armour", Nice(2f * level))
                        .Put("experience", Nice(12 * level * experience))
                        .Put("faction", family.Faction)
                        .List("tags", tags)
                        .List("abilities", abilities.GetValueOrDefault(family.Id, []))
                        .Put("loot", rank is "Rare" or "Boss" ? loot[$"{family.Id}-prize"] : loot[family.Id])
                );

                written.Add(address);
            }

            byFamily[family.Id] = written;
        }

        return byFamily;
    }

    /// <summary>A camp per family, leashed to where it belongs.</summary>
    /// <param name="creatures">What lives in each.</param>
    /// <returns>The spawn table addresses.</returns>
    public List<string> Spawns(Dictionary<string, List<string>> creatures) {
        var written = new List<string>();

        foreach (var family in Tables.Families) {
            foreach (var (suffix, cap, seconds, tether, breaks, behaviour) in new[] {
                ("camp", 8, 45f, 30f, 55f, "Reset"),
                ("patrol", 4, 60f, 45f, 80f, "Evade")
            }) {
                var address = $"spawns/{family.Id}-{suffix}";
                var members = creatures[family.Id];

                var yaml = new Yaml("SpawnTableDefinition", $"A {family.Name.ToLowerInvariant()} {suffix}.")
                    .Put("displayName", $"{family.Name} {suffix}")
                    .Put("cap", cap)
                    .Put("respawnSeconds", seconds)
                    .Put("respawnJitter", seconds / 4)
                    .Open("leash")
                    .Put("tether", tether)
                    .Put("break", breaks)
                    .Put("patience", 8f)
                    .Put("behaviour", behaviour)
                    .Put("healsOnReset", true)
                    .Close();

                yaml.Item("entries").Put("creature", members[0]).Put("weight", 70f).Put("minimum", 1).Put("maximum", 2).Close();
                yaml.Item().Put("creature", members[1]).Put("weight", 25f).Close();
                yaml.Item().Put("creature", members[2]).Put("weight", 5f).Close();

                Write(address, ".vxdef", yaml);
                written.Add(address);
            }
        }

        return written;
    }
}
