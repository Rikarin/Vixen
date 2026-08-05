// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>The quality ladder.</summary>
    /// <returns>Their addresses, by id.</returns>
    public Dictionary<string, string> Rarities() {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rarity in Tables.Rarities) {
            var address = $"items/rarities/{rarity.Id}";

            Write(
                address,
                ".vxdef",
                new Yaml("ItemRarityDefinition", $"Rarity {rarity.Order}.")
                    .Put("displayName", rarity.Name)
                    .Put("order", rarity.Order)
                    .Put("affixes", rarity.Affixes, 0)
                    .Put("tag", $"Item.Rarity.{rarity.Name}")
            );

            byId[rarity.Id] = address;
        }

        return byId;
    }

    /// <summary>The affixes, and one pool per attribute-bearing slot group.</summary>
    /// <returns>The pool addresses.</returns>
    public List<string> Affixes() {
        var pools = new List<string>();
        var all = new List<string>();

        foreach (var (id, name, attribute, value) in Tables.Affixes) {
            var address = $"items/affixes/{id}";

            Write(
                address,
                ".vxdef",
                new Yaml("AffixDefinition", $"Affix carrying {attribute}.")
                    .Put("displayName", name)
                    .Put("weight", value < 0 ? 1f : 3f)
                    .Put("minimumItemLevel", 1)
                    .List("tags", [$"Item.Affix.{attribute}"])
                    .Item("stats")
                    .Put("attribute", attribute)
                    .Put("op", value is > -1 and < 1 and not 0 ? "Add" : "Add")
                    .Put("value", value)
                    .Put("maximum", value * 5)
                    .Close()
            );

            all.Add(address);
        }

        // One pool, because every slot in this game rolls from the same table — a real game splits
        // them by slot and that is a table row rather than a mechanism.
        var pool = "items/affix-pools/general";

        Write(
            pool,
            ".vxdef",
            new Yaml("AffixPoolDefinition", "Every affix. A real game splits this by slot.")
                .List("affixes", all)
        );

        pools.Add(pool);

        return pools;
    }

    /// <summary>Armour, weapons, jewellery, reagents, consumables, gems and bags.</summary>
    /// <param name="rarities">The quality ladder, by id.</param>
    /// <param name="pools">What an affixed piece rolls from.</param>
    /// <returns>Every item address, grouped by what it is for.</returns>
    public ItemCatalogue Items(Dictionary<string, string> rarities, List<string> pools) {
        var armour = new List<string>();
        var weapons = new List<string>();
        var ranged = new List<string>();
        var reagents = new List<string>();
        var consumables = new List<string>();
        var gems = new List<string>();
        var quest = new List<string>();

        // ── Armour: a set per zone per armour class, over the slots that carry armour ────────────
        foreach (var zone in Tables.Zones) {
            var rarity = zone.Low switch { < 8 => "common", < 15 => "fine", < 20 => "rare", _ => "storied" };
            var level = zone.High;

            foreach (var kind in Tables.Armours) {
                foreach (var slot in Tables.Slots) {
                    var address = $"items/armour/{zone.Id}-{kind.Id}-{slot.Id}";
                    var scale = Tables.Rarities.First(entry => entry.Id == rarity).Scale;
                    var main = Tables.Classes.First(entry => entry.Id == kind.Class).Attribute;

                    var yaml = new Yaml("ItemDefinition", $"{zone.Name} {kind.Name}, {slot.Tag}.")
                        .Put("displayName", $"{zone.Name} {kind.Name} {slot.Noun}")
                        .Put("rarity", rarities[rarity])
                        .Put("slot", $"Item.Slot.{slot.Tag}")
                        .Put("itemLevel", level)
                        .Put("maximumStack", 1)
                        .Put("maximumDurability", Nice(60 * kind.Scale))
                        .Put("binding", "OnEquip")
                        .Put("sockets", rarity == "storied" ? 2 : rarity == "rare" ? 1 : 0, 0)
                        .List("affixPools", pools)
                        .List("tags", [$"Item.Armour.{kind.Name}", $"Item.Slot.{slot.Tag}"]);

                    if (slot.ArmourShare > 0) {
                        yaml.Item("stats")
                            .Put("attribute", "Armour")
                            .Put("op", "Add")
                            .Put("value", Nice(level * 4 * kind.Scale * slot.ArmourShare * scale))
                            .Close();
                    }

                    yaml.Item(slot.ArmourShare > 0 ? null : "stats")
                        .Put("attribute", main)
                        .Put("op", "Add")
                        .Put("value", Nice(level * 0.8f * scale))
                        .Close()
                        .Item()
                        .Put("attribute", "Stamina")
                        .Put("op", "Add")
                        .Put("value", Nice(level * 0.6f * scale))
                        .Close();

                    Write(address, ".vxitem", yaml);
                    armour.Add(address);
                }
            }
        }

        // ── Weapons: one per kind per zone, and the ranged ones get ballistics too ───────────────
        foreach (var zone in Tables.Zones) {
            var rarity = zone.Low switch { < 8 => "common", < 15 => "fine", < 20 => "rare", _ => "storied" };
            var scale = Tables.Rarities.First(entry => entry.Id == rarity).Scale;

            foreach (var weapon in Tables.Weapons) {
                var address = $"items/weapons/{zone.Id}-{weapon.Id}";
                var main = Tables.Classes.First(entry => entry.Id == weapon.Class).Attribute;

                Write(
                    address,
                    ".vxitem",
                    new Yaml("ItemDefinition", $"{zone.Name} {weapon.Name}.")
                        .Put("displayName", $"{zone.Name} {weapon.Name}")
                        .Put("rarity", rarities[rarity])
                        .Put("slot", $"Item.Slot.{weapon.Slot}")
                        .Put("itemLevel", zone.High)
                        .Put("maximumStack", 1)
                        .Put("maximumDurability", 100)
                        .Put("binding", "OnEquip")
                        .List("affixPools", pools)
                        .List("tags", [$"Item.Weapon.{weapon.Name.Replace(" ", string.Empty, StringComparison.Ordinal)}", $"Item.Slot.{weapon.Slot}"])
                        .Item("stats")
                        .Put("attribute", main)
                        .Put("op", "Add")
                        .Put("value", Nice(zone.High * 1.2f * scale))
                        .Close()
                );

                weapons.Add(address);

                if (weapon.Ranged) {
                    ranged.Add(address);
                }
            }
        }

        // ── Reagents: one gathered and one refined per making profession ─────────────────────────
        foreach (var zone in Tables.Zones) {
            foreach (var (raw, refined, tag) in new[] {
                ($"{zone.Id}-ore", $"{zone.Id}-bar", "Ore"),
                ($"{zone.Id}-hide", $"{zone.Id}-leather", "Hide"),
                ($"{zone.Id}-herb", $"{zone.Id}-essence", "Herb")
            }) {
                foreach (var (id, name, kind) in new[] { (raw, "Ore", tag), (refined, "Bar", tag) }) {
                    var address = $"items/reagents/{id}";

                    Write(
                        address,
                        ".vxitem",
                        new Yaml("ItemDefinition", $"{zone.Name} reagent.")
                            .Put("displayName", $"{zone.Name} {Noun(id)}")
                            .Put("rarity", rarities["common"])
                            .Put("itemLevel", zone.Low)
                            .Put("maximumStack", 200)
                            .Put("binding", "None")
                            .List("tags", [$"Item.Reagent.{Noun(id)}"])
                    );

                    reagents.Add(address);
                }
            }
        }

        // ── Consumables: food, potions and elixirs, banded by zone ───────────────────────────────
        foreach (var zone in Tables.Zones) {
            foreach (var (id, name, tag) in new[] {
                ("ration", "Ration", "Food"),
                ("draught", "Draught", "Potion"),
                ("elixir", "Elixir", "Elixir")
            }) {
                var address = $"items/consumables/{zone.Id}-{id}";

                Write(
                    address,
                    ".vxitem",
                    new Yaml("ItemDefinition", $"{zone.Name} {name.ToLowerInvariant()}.")
                        .Put("displayName", $"{zone.Name} {name}")
                        .Put("rarity", rarities["common"])
                        .Put("itemLevel", zone.Low)
                        .Put("maximumStack", 20)
                        .Put("binding", "None")
                        .List("tags", [$"Item.Consumable.{tag}"])
                );

                consumables.Add(address);
            }
        }

        // ── Gems, for the sockets the storied pieces declare ─────────────────────────────────────
        foreach (var (id, name, attribute) in Tables.Gems) {
            foreach (var (grade, scale) in new[] { ("chipped", 1f), ("polished", 2f), ("flawless", 3.5f) }) {
                var address = $"items/gems/{grade}-{id}";

                Write(
                    address,
                    ".vxitem",
                    new Yaml("ItemDefinition", $"A {grade} {name.ToLowerInvariant()}, carrying {attribute}.")
                        .Put("displayName", $"{char.ToUpperInvariant(grade[0]) + grade[1..]} {name}")
                        .Put("rarity", rarities[grade == "flawless" ? "rare" : "fine"])
                        .Put("itemLevel", (int)(scale * 6))
                        .Put("maximumStack", 20)
                        .Put("binding", "None")
                        .List("tags", ["Item.Gem"])
                        .Item("stats")
                        .Put("attribute", attribute)
                        .Put("op", "Add")
                        .Put("value", Nice(4 * scale))
                        .Close()
                );

                gems.Add(address);
            }
        }

        // ── Quest items and bags ─────────────────────────────────────────────────────────────────
        foreach (var zone in Tables.Zones) {
            var address = $"items/quest/{zone.Id}-token";

            Write(
                address,
                ".vxitem",
                new Yaml("ItemDefinition", $"What {zone.Name}'s chain asks for.")
                    .Put("displayName", $"{zone.Name} Token")
                    .Put("rarity", rarities["fine"])
                    .Put("itemLevel", zone.Low)
                    .Put("maximumStack", 20)
                    .Put("binding", "OnPickup")
                    .List("tags", ["Item.Quest.Token"])
            );

            quest.Add(address);
        }

        foreach (var (id, name, slots) in new[] { ("wardens-pack", "Warden's Pack", 16), ("delvers-satchel", "Delver's Satchel", 20), ("tidecallers-creel", "Tidecaller's Creel", 24) }) {
            var address = $"items/bags/{id}";

            Write(
                address,
                ".vxitem",
                new Yaml("ItemDefinition", "A bag. Inventory has no definition type — the number lives here.")
                    .Put("displayName", name)
                    .Put("rarity", rarities["fine"])
                    .Put("itemLevel", 1)
                    .Put("maximumStack", 1)
                    .Put("binding", "None")
                    .List("tags", ["Item.Container.Bag"])
                    .Item("stats")
                    .Put("attribute", "Slots")
                    .Put("op", "Add")
                    .Put("value", slots)
                    .Close()
            );

            consumables.Add(address);
        }

        return new(armour, weapons, ranged, reagents, consumables, gems, quest);
    }

    static string Noun(string id) =>
        id.EndsWith("-ore", StringComparison.Ordinal) ? "Ore"
        : id.EndsWith("-bar", StringComparison.Ordinal) ? "Bar"
        : id.EndsWith("-hide", StringComparison.Ordinal) ? "Hide"
        : id.EndsWith("-leather", StringComparison.Ordinal) ? "Leather"
        : id.EndsWith("-herb", StringComparison.Ordinal) ? "Herb"
        : "Essence";

    /// <summary>Everything wearable, usable or sellable, by what it is for.</summary>
    /// <param name="Armour">Every piece of armour.</param>
    /// <param name="Weapons">Every weapon.</param>
    /// <param name="Ranged">The weapons that also need ballistics.</param>
    /// <param name="Reagents">What crafting takes.</param>
    /// <param name="Consumables">Food, potions and bags.</param>
    /// <param name="Gems">What goes in a socket.</param>
    /// <param name="Quest">What a chain asks for.</param>
    public readonly record struct ItemCatalogue(
        List<string> Armour,
        List<string> Weapons,
        List<string> Ranged,
        List<string> Reagents,
        List<string> Consumables,
        List<string> Gems,
        List<string> Quest
    );
}
