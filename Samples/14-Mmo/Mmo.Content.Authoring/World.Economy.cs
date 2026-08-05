// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>Currencies: one to spend, one to grind, one that decays, and three faction tokens.</summary>
    /// <returns>Their addresses, by id.</returns>
    public Dictionary<string, string> Currencies() {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);

        (string Id, string Name, string Scope, long Cap, float Decay, string? To, int Rate)[] rows = [
            ("gold", "Gold", "Character", 9_999_999_999L, 0f, null, 0),
            ("marchmarks", "Marchmarks", "Account", 2_000, 0f, "gold", 250),
            ("honour", "Honour", "Character", 15_000, 0.02f, null, 0),
            ("barrow-sigils", "Barrow Sigils", "Character", 500, 0f, null, 0),
            ("tide-scrip", "Tide Scrip", "Account", 1_000, 0f, "gold", 120),
            ("company-chits", "Company Chits", "Character", 800, 0.01f, null, 0)
        ];

        foreach (var (id, name, scope, cap, decay, to, rate) in rows) {
            var address = $"currencies/{id}";
            var yaml = new Yaml("CurrencyDefinition", $"{scope}-scoped currency.")
                .Put("displayName", name)
                .Put("scope", scope)
                .Put("cap", (int)Math.Min(cap, int.MaxValue))
                .Put("decayPerDay", decay, 0f)
                .Put("tag", $"Currency.{name.Replace(" ", string.Empty, StringComparison.Ordinal)}");

            if (to is not null) {
                // ⚠ One-way on purpose. A token that buys gold and gold that buys the token is a
                // token with a price, which is a different economy from the one a designer meant.
                yaml.Item("conversions").Put("to", $"currencies/{to}").Put("rate", rate).Put("oneWay", true).Close();
            }

            Write(address, ".vxdef", yaml);
            byId[id] = address;
        }

        // ⚠ Gold's cap is written as an int above because the table's long does not fit; the real
        // field is a long and a game with a bigger economy writes a bigger number.
        return byId;
    }

    /// <summary>A shop of every kind in every zone, plus the faction quartermasters.</summary>
    /// <param name="items">What there is to sell.</param>
    /// <param name="currencies">What to charge in.</param>
    /// <returns>The vendor addresses.</returns>
    /// <remarks>
    ///     ⚠ <b>A vendor is where three libraries meet and nothing checks the join.</b> Its stock is
    ///     Items', its price is Economy's currency, and its gate is the kernel's requirement algebra
    ///     — so a row naming an item that does not exist, or a currency nobody minted, compiles clean
    ///     and sells nothing. <c>ReferenceTests</c> is what catches it.
    /// </remarks>
    public List<string> Vendors(ItemCatalogue items, Dictionary<string, string> currencies) {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            foreach (var kind in Tables.VendorKinds) {
                // A stablemaster and a lapidary are not in every hamlet.
                if (kind.Id is "lapidary" or "stablemaster" && zone.Low < 12) {
                    continue;
                }

                var address = $"vendors/{zone.Id}-{kind.Id}";
                var stock = kind.Sells switch {
                    "armour" => items.Armour.Where(entry => entry.Contains($"/{zone.Id}-", StringComparison.Ordinal)).Take(9),
                    "weapon" => items.Weapons.Where(entry => entry.Contains($"/{zone.Id}-", StringComparison.Ordinal)).Take(8),
                    "reagent" => items.Reagents.Where(entry => entry.Contains($"/{zone.Id}-", StringComparison.Ordinal)).Take(6),
                    "food" => items.Consumables.Where(entry => entry.Contains($"/{zone.Id}-", StringComparison.Ordinal)),
                    "gem" => items.Gems.Take(9),
                    "mount" => [],
                    "faction" => items.Armour.Where(entry => entry.Contains($"/{zone.Id}-", StringComparison.Ordinal)).Skip(9).Take(4),
                    _ => items.Consumables.Where(entry => entry.Contains($"/{zone.Id}-", StringComparison.Ordinal)).Take(3)
                };

                var rows = stock.ToArray();

                if (rows.Length == 0) {
                    continue;
                }

                var yaml = new Yaml("VendorDefinition", $"{zone.Name}'s {kind.Name.ToLowerInvariant()}.")
                    .Put("displayName", $"{zone.Name} {kind.Name}")
                    .Put("tag", $"Vendor.{zone.Name}.{kind.Name.Replace(" ", string.Empty, StringComparison.Ordinal)}")
                    .Put("buybackSlots", 12);

                var first = true;

                foreach (var item in rows) {
                    var faction = kind.Id == "quartermaster";
                    var currency = faction ? currencies["marchmarks"] : currencies["gold"];

                    yaml.Item(first ? "stock" : null)
                        .Put("item", item)
                        .Put("currency", currency)
                        .Put("price", faction ? Nice(zone.High * 1.5f) : Nice(zone.High * zone.High * 4f))
                        .Put("quantity", faction ? 2 : 0, 0)
                        .Put("restockSeconds", faction ? 86_400f : 0f, 0f);

                    if (faction) {
                        // The whole point of a quartermaster: the stock is there and you cannot have it.
                        var reputation = Tables.Factions.First(entry => entry.Zone == zone.Id);

                        yaml.Item("requires")
                            .Put("kind", "HasTag")
                            .Put("subject", $"Faction.{Pascal(reputation.Id)}.Honoured")
                            .Close();
                    }

                    yaml.Close();
                    first = false;
                }

                Write(address, ".vxdef", yaml);
                written.Add(address);
            }
        }

        return written;
    }

    /// <summary>Professions, their tiers, and the reputations a player grinds.</summary>
    /// <returns>The profession addresses by id, and the reputation addresses by id.</returns>
    public (Dictionary<string, string> Professions, Dictionary<string, string> Reputations) Progression() {
        var professions = new Dictionary<string, string>(StringComparer.Ordinal);
        var reputations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var profession in Tables.Professions) {
            var address = $"progression/professions/{profession.Id}";
            var yaml = new Yaml("ProfessionDefinition", profession.Gathering ? "A gathering profession." : "A making profession.")
                .Put("displayName", profession.Name)
                .Put("tag", $"Profession.{profession.Name}")
                .Put("maximumSkill", 300);

            var first = true;

            foreach (var (tier, skill) in new[] { ("Apprentice", 1), ("Journeyman", 75), ("Expert", 150), ("Master", 225) }) {
                yaml.Item(first ? "tiers" : null)
                    .Put("displayName", tier)
                    .Put("skill", skill)
                    .Put("tag", $"Profession.{profession.Name}.{tier}")
                    .Close();

                first = false;
            }

            Write(address, ".vxdef", yaml);
            professions[profession.Id] = address;
        }

        foreach (var faction in Tables.Factions) {
            var address = $"progression/reputations/{faction.Id}";
            var yaml = new Yaml("ReputationDefinition", "A reputation grind.")
                .Put("displayName", faction.Name)
                .Put("tag", $"Faction.{Pascal(faction.Id)}")
                .Put("minimum", -6_000)
                .Put("maximum", 42_000);

            var first = true;

            foreach (var (rank, threshold) in new[] {
                ("Hostile", -6_000), ("Neutral", 0), ("Friendly", 3_000),
                ("Honoured", 12_000), ("Revered", 21_000), ("Exalted", 36_000)
            }) {
                yaml.Item(first ? "ranks" : null)
                    .Put("displayName", rank)
                    .Put("threshold", threshold)
                    .Put("tag", $"Faction.{Pascal(faction.Id)}.{rank}")
                    .Close();

                first = false;
            }

            Write(address, ".vxdef", yaml);
            reputations[faction.Id] = address;
        }

        return (professions, reputations);
    }

    /// <summary>Recipes, banded so a profession has something to make at every skill level.</summary>
    /// <param name="items">What to take and make.</param>
    /// <param name="professions">Who teaches it.</param>
    /// <returns>The recipe addresses.</returns>
    public List<string> Recipes(ItemCatalogue items, Dictionary<string, string> professions) {
        var written = new List<string>();

        // ⚠ A reagent line per profession, and it is not decoration — the crafting library refuses
        // two *discovered* recipes with the same inputs, because only one of them could ever be
        // found. Four professions all refining "the zone's ore" is exactly that, and the first
        // generated pass hit it forty times.
        (string Profession, string Raw, string Refined, string Source)[] lines = [
            ("smithing", "ore", "bar", "Discovered"),
            ("leatherworking", "hide", "leather", "Taught"),
            ("weaving", "herb", "essence", "Known"),
            ("alchemy", "essence", "essence", "Taught")
        ];

        foreach (var (id, raw, refined, source) in lines) {
            var profession = Tables.Professions.First(entry => entry.Id == id);
            var band = 0;

            foreach (var zone in Tables.Zones) {
                var input = items.Reagents.FirstOrDefault(address => address.EndsWith($"{zone.Id}-{raw}", StringComparison.Ordinal));
                var output = items.Reagents.FirstOrDefault(address => address.EndsWith($"{zone.Id}-{refined}", StringComparison.Ordinal));
                var made = id == "alchemy"
                    ? items.Consumables.FirstOrDefault(address => address.Contains($"/{zone.Id}-draught", StringComparison.Ordinal))
                    : items.Armour.FirstOrDefault(address => address.Contains($"/{zone.Id}-{ArmourOf(id)}-", StringComparison.Ordinal));

                if (input is null || output is null || made is null) {
                    continue;
                }

                var skill = band * 45;

                // Alchemy has no refining step of its own — it buys its essence from the weavers,
                // which is why its line reads essence → essence and only the craft is written.
                if (id != "alchemy") {
                    Write(
                        $"crafting/{id}/{zone.Id}-refine",
                        ".vxrecipe",
                        new Yaml("RecipeDefinition", $"{profession.Name}, skill {skill}.")
                            .Put("displayName", $"Refine {zone.Name} {Pascal(refined)}")
                            .Put("profession", professions[id])
                            .Put("station", profession.Station)
                            .Put("source", "Known")
                            .Put("skillRequired", skill)
                            .Put("skillGain", 2)
                            .Put("skillCap", skill + 60)
                            .Item("inputs").Put("item", input).Put("count", 2).Close()
                            .Item("outputs").Put("item", output).Put("count", 1).Close()
                    );

                    written.Add($"crafting/{id}/{zone.Id}-refine");
                }

                Write(
                    $"crafting/{id}/{zone.Id}-craft",
                    ".vxrecipe",
                    new Yaml("RecipeDefinition", $"{profession.Name}, skill {skill + 20}. Source: {source}.")
                        .Put("displayName", $"Make {zone.Name} {(id == "alchemy" ? "Draught" : "Gear")}")
                        .Put("profession", professions[id])
                        .Put("station", profession.Station)
                        .Put("source", source)
                        .Put("skillRequired", skill + 20)
                        .Put("skillGain", 3)
                        .Put("skillCap", skill + 80)
                        .Put("qualityChance", 0.1f)
                        .Item("inputs").Put("item", output).Put("count", id == "alchemy" ? 3 : 6).Close()
                        .Item("outputs").Put("item", made).Put("count", 1).Close()
                        .Item("requires")
                        .Put("kind", "HasTag")
                        .Put("subject", $"Profession.{profession.Name}.{(band < 2 ? "Apprentice" : "Journeyman")}")
                        .Close()
                );

                written.Add($"crafting/{id}/{zone.Id}-craft");
                band++;
            }
        }

        return written;
    }

    static string ArmourOf(string profession) =>
        profession switch { "smithing" => "plate", "leatherworking" => "leather", _ => "cloth" };

    internal static string Pascal(string id) =>
        string.Concat(id.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
