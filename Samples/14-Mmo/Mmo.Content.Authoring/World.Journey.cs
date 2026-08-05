// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>A chain per zone, a daily per zone, and a reputation grind per faction.</summary>
    /// <param name="items">What to ask for and what to pay with.</param>
    /// <param name="currencies">What to pay in.</param>
    /// <param name="reputations">What to pay reputation to.</param>
    /// <returns>The quest addresses.</returns>
    /// <remarks>
    ///     ⚠ <b>Every Kill objective here counts a tag a creature actually grants.</b> That is the
    ///     whole of what was wrong before: the tags existed in the table because the quests mentioned
    ///     them, nothing granted them, and the objectives compiled into ones nothing could advance.
    /// </remarks>
    public List<string> Quests(
        ItemCatalogue items,
        Dictionary<string, string> currencies,
        Dictionary<string, string> reputations
    ) {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            var families = Tables.Families.Where(entry => entry.Zone == zone.Id).ToArray();
            var token = items.Quest.First(address => address.Contains(zone.Id, StringComparison.Ordinal));
            var reagent = items.Reagents.First(address => address.EndsWith($"{zone.Id}-herb", StringComparison.Ordinal));
            var faction = Tables.Factions.First(entry => entry.Zone == zone.Id);
            var reward = items.Armour.First(address => address.Contains($"/{zone.Id}-", StringComparison.Ordinal));
            var previous = string.Empty;

            // ── The chain: eight steps, each gating the next ──────────────────────────────────────
            for (var step = 1; step <= 8; step++) {
                var address = $"quests/{zone.Id}/{step:00}-{Step(step)}";
                var family = families[(step - 1) % families.Length];
                var tag = $"Quest.Completed.{Pascal(zone.Id)}{step:00}";

                var yaml = new Yaml("QuestDefinition", $"{zone.Name} chain, step {step} of 8.")
                    .Put("displayName", $"{Title(zone.Name, step)}")
                    .Put("summary", zone.Blurb)
                    .Put("repeat", "Once")
                    .Put("shareable", true)
                    .Put("tag", tag)
                    .List("grantsTags", [$"Quest.Active.{Pascal(zone.Id)}{step:00}"]);

                yaml.Item("requirements")
                    .Put("kind", "Value")
                    .Put("subject", "Level")
                    .Put("comparison", "AtLeast")
                    .Put("value", zone.Low)
                    .Close();

                if (previous.Length > 0) {
                    yaml.Item().Put("kind", "HasTag").Put("subject", previous).Close();
                }

                // Two stages, so every chain exercises the journal's stage machinery.
                yaml.Item("stages")
                    .Put("id", "hunt")
                    .Put("displayName", $"Thin out the {family.Name.ToLowerInvariant()}s")
                    .Item("objectives")
                    .Put("type", "Kill")
                    .Put("displayName", $"{family.Name}s felled")
                    .Put("count", 4 + step)
                    .List("targetTags", [family.Tag])
                    .Put("scene", $"maps/{zone.Id}")
                    .Close()
                    .Close();

                yaml.Item()
                    .Put("id", "gather")
                    .Put("displayName", "And bring something back")
                    .Item("objectives")
                    .Put("type", "Collect")
                    .Put("displayName", "Gathered")
                    .Put("count", 2 + step)
                    .Put("target", step % 2 == 0 ? token : reagent)
                    .Close()
                    .Close();

                yaml.Open("rewards")
                    .Put("experience", Nice(120 * zone.Low * step * 0.4f))
                    .Item("currencies").Put("def", currencies["gold"]).Put("count", Nice(40 * zone.Low * step * 0.3f)).Close()
                    .Item("reputation").Put("def", reputations[faction.Id]).Put("count", 150).Close();

                if (step == 8) {
                    yaml.Item("items").Put("def", reward).Close();
                }

                yaml.Close();

                Write(address, ".vxquest", yaml);
                written.Add(address);
                previous = tag;
            }

            // ── A daily, and a weekly ─────────────────────────────────────────────────────────────
            foreach (var (id, repeat, count) in new[] { ("tend", "Daily", 10), ("clear", "Weekly", 25) }) {
                var address = $"quests/{zone.Id}/{id}";
                var family = families[0];

                Write(
                    address,
                    ".vxquest",
                    new Yaml("QuestDefinition", $"{zone.Name}'s {repeat.ToLowerInvariant()}.")
                        .Put("displayName", $"{(id == "tend" ? "Tend" : "Clear")} the {zone.Name}")
                        .Put("summary", zone.Blurb)
                        .Put("repeat", repeat)
                        .Put("tag", $"Quest.Completed.{Pascal(zone.Id)}{Pascal(id)}")
                        .Item("stages")
                        .Put("id", "only")
                        .Item("objectives")
                        .Put("type", "Kill")
                        .Put("displayName", $"{family.Name}s felled")
                        .Put("count", count)
                        .List("targetTags", [family.Tag])
                        .Put("scene", $"maps/{zone.Id}")
                        .Close()
                        .Close()
                        .Open("rewards")
                        .Put("experience", Nice(200 * zone.Low))
                        .Item("currencies").Put("def", currencies["marchmarks"]).Put("count", repeat == "Daily" ? 5 : 20).Close()
                        .Item("reputation").Put("def", reputations[faction.Id]).Put("count", repeat == "Daily" ? 250 : 750).Close()
                        .Close()
                );

                written.Add(address);
            }
        }

        return written;
    }

    /// <summary>A world boss per zone, chained into a follow-up.</summary>
    /// <param name="creatures">What the boss is.</param>
    /// <param name="currencies">What a tier pays.</param>
    /// <returns>The event addresses.</returns>
    public List<string> Events(Dictionary<string, List<string>> creatures, Dictionary<string, string> currencies) {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            var family = Tables.Families.First(entry => entry.Zone == zone.Id);
            var address = $"events/{zone.Id}-rising";
            var follow = $"events/{zone.Id}-harvest";

            Write(
                address,
                ".vxdef",
                new Yaml("DynamicEventDefinition", $"{zone.Name}'s world boss. Tiers, because a latecomer is paid differently.")
                    .Put("displayName", $"The {family.Name} Rising")
                    .Put("summary", zone.Blurb)
                    .Put("scene", $"maps/{zone.Id}")
                    .Put("duration", 900f)
                    .Put("tag", $"Event.Active.{Pascal(zone.Id)}Rising")
                    .Put("scalesObjectives", true)
                    .Open("schedule").Put("intervalSeconds", 3_600f).Put("windowSeconds", 900f).Close()
                    .Open("scaling").Put("baseParticipants", 10).Put("perParticipant", 0.08f).Put("maximum", 4f).Close()
                    .Item("objectives")
                    .Put("type", "Kill")
                    .Put("displayName", "Felled")
                    .Put("count", 1)
                    .List("targetTags", ["Creature.Elite"])
                    .Put("scene", $"maps/{zone.Id}")
                    .Close()
                    .List("onSuccess", [follow])
                    .Item("tiers")
                    .Put("displayName", "Bronze").Put("minimum", 1)
                    .Open("rewards").Put("experience", 300).Close()
                    .Close()
                    .Item()
                    .Put("displayName", "Silver").Put("minimum", 2_000)
                    .Open("rewards")
                    .Put("experience", 800)
                    .Item("currencies").Put("def", currencies["marchmarks"]).Put("count", 6).Close()
                    .Close()
                    .Close()
                    .Item()
                    .Put("displayName", "Gold").Put("minimum", 6_000)
                    .Open("rewards")
                    .Put("experience", 1_600)
                    .Item("currencies").Put("def", currencies["marchmarks"]).Put("count", 15).Close()
                    .Close()
                    .Close()
            );

            Write(
                follow,
                ".vxdef",
                new Yaml("DynamicEventDefinition", "What the boss chains into. Two events pointing at each other is the smallest graph that proves it is one.")
                    .Put("displayName", $"The {zone.Name} Harvest")
                    .Put("summary", "With it down, the ground can be cleared.")
                    .Put("scene", $"maps/{zone.Id}")
                    .Put("duration", 600f)
                    .Put("tag", $"Event.Active.{Pascal(zone.Id)}Harvest")
                    .Item("objectives")
                    .Put("type", "Kill")
                    .Put("displayName", "Cleared")
                    .Put("count", 20)
                    .List("targetTags", [family.Tag])
                    .Close()
                    .List("onFailure", [address])
                    .Item("tiers")
                    .Put("displayName", "Bronze").Put("minimum", 1)
                    .Open("rewards").Put("experience", 200).Close()
                    .Close()
            );

            written.Add(address);
            written.Add(follow);
        }

        return written;
    }

    static string Step(int step) =>
        step switch {
            1 => "the-broken-fence", 2 => "ore-for-the-forge", 3 => "the-road-north", 4 => "what-walks",
            5 => "the-long-watch", 6 => "a-debt-repaid", 7 => "the-last-camp", _ => "the-crown"
        };

    static string Title(string zone, int step) =>
        step switch {
            1 => $"The Broken Fence of {zone}", 2 => "Ore for the Forge", 3 => "The Road North",
            4 => $"What Walks in {zone}", 5 => "The Long Watch", 6 => "A Debt Repaid",
            7 => "The Last Camp", _ => $"The Crown of {zone}"
        };
}
