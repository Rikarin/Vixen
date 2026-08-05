// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>A talent tree a class, twenty nodes each, with branches rather than one chain.</summary>
    /// <param name="effects">What a capstone can grant.</param>
    /// <returns>The tree addresses by class, and the specialisation addresses.</returns>
    public (Dictionary<string, string> Trees, List<string> Specialisations) Talents(Dictionary<string, string> effects) {
        var trees = new Dictionary<string, string>(StringComparer.Ordinal);
        var specialisations = new List<string>();

        foreach (var cls in Tables.Classes) {
            var address = $"progression/trees/{cls.Id}";
            var yaml = new Yaml("TalentTreeDefinition", $"{cls.Name}: three branches of six, and two capstones.")
                .Put("displayName", cls.Name);

            var first = true;

            // ⚠ Three branches rather than one chain, because a tree with one path is a list. Each
            // branch gates on points spent, which is what makes taking one a decision not to take
            // the others.
            foreach (var (branch, attribute) in new[] { ("might", cls.Attribute), ("guile", "CritChance"), ("ward", "Armour") }) {
                for (var depth = 0; depth < 6; depth++) {
                    var id = $"{branch}-{depth + 1}";

                    yaml.Item(first ? "nodes" : null)
                        .Put("id", id)
                        .Put("displayName", $"{Pascal(branch)} {Roman(depth + 1)}")
                        .Put("maximumRanks", depth % 2 == 0 ? 3 : 2)
                        .Put("requiredPoints", depth * 4)
                        .Item("modifiers")
                        .Put("attribute", attribute)
                        .Put("op", attribute is "CritChance" ? "Add" : "AddPercent")
                        .Put("value", attribute is "CritChance" ? 0.01f : 0.03f)
                        .Close();

                    if (depth > 0) {
                        yaml.Item("requires").Put("node", $"{branch}-{depth}").Put("ranks", 2).Close();
                    }

                    yaml.Close();
                    first = false;
                }
            }

            // Two capstones, each needing a branch taken to the bottom.
            foreach (var (id, branch) in new[] { ("crescendo", "might"), ("bulwark", "ward") }) {
                yaml.Item()
                    .Put("id", id)
                    .Put("displayName", Pascal(id))
                    .Put("maximumRanks", 1)
                    .Put("costPerRank", 2)
                    .Put("requiredPoints", 20)
                    .List("grantsTags", [$"Talent.{cls.Name}.{Pascal(id)}"])
                    .Item("requires").Put("node", $"{branch}-6").Put("ranks", 2).Close()
                    .Close();
            }

            Write(address, ".vxdef", yaml);
            trees[cls.Id] = address;

            var specialisation = $"progression/specialisations/{cls.Id}";

            Write(
                specialisation,
                ".vxdef",
                new Yaml("SpecialisationDefinition", $"{cls.Name}'s specialisation, which decides what it spends.")
                    .Put("displayName", cls.Name)
                    .Put("tag", $"Specialisation.{cls.Name}")
                    .Put("talentTree", address)
                    .Item("requirements")
                    .Put("kind", "Value").Put("subject", "Level").Put("comparison", "AtLeast").Put("value", 5)
                    .Close()
                    .Item("modifiers")
                    .Put("attribute", cls.Attribute).Put("op", "Add").Put("value", 25)
                    .Close()
            );

            specialisations.Add(specialisation);
        }

        // The level curve, once.
        Write(
            "progression/curve",
            ".vxdef",
            new Yaml("ExperienceCurveDefinition", "1–25. Thresholds cover the early levels exactly; the formula takes over.")
                .Put("maximumLevel", 25)
                .List("thresholds", ["400", "900", "1400", "2100", "2800"])
                .Put("base", 3_600f)
                .Put("growth", 1.18f)
        );

        return (trees, specialisations);
    }

    /// <summary>Mounts, pets, looks, titles, toys — and the achievements over them.</summary>
    /// <returns>The collectible addresses.</returns>
    public List<string> Collections() {
        var written = new List<string>();
        var unlocks = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var zone in Tables.Zones) {
            foreach (var (kind, noun, slot) in new[] {
                ("Mount", "Courser", ""),
                ("Pet", "Whelp", ""),
                ("Appearance", "Hauberk", "Item.Slot.Chest"),
                ("Appearance", "Helm", "Item.Slot.Head"),
                ("Title", "Warden", ""),
                ("Toy", "Whistle", "")
            }) {
                var id = $"{zone.Id}-{noun.ToLowerInvariant()}";
                var address = $"collectibles/{kind.ToLowerInvariant()}/{id}";

                Write(
                    address,
                    ".vxdef",
                    new Yaml("CollectibleDefinition", $"Account-wide: earned on one character, owned by all of them.")
                        .Put("displayName", $"{zone.Name} {noun}")
                        .Put("kind", kind)
                        .Put("slot", slot)
                        .Put("tag", $"Collected.{kind}.{Pascal(id)}")
                );

                written.Add(address);
                unlocks[$"{zone.Id}-{kind}"] = address;
            }
        }

        // Achievements: one counted, one standing, one over a collection, and one that cascades.
        foreach (var zone in Tables.Zones) {
            var family = Tables.Families.First(entry => entry.Zone == zone.Id);
            var faction = Tables.Factions.First(entry => entry.Zone == zone.Id);

            Write(
                $"achievements/{zone.Id}-slayer",
                ".vxdef",
                new Yaml("AchievementDefinition", "A counted criterion, which a tag query cannot express.")
                    .Put("displayName", $"{zone.Name} Slayer")
                    .Put("description", $"Put down a hundred of {zone.Name}'s worst.")
                    .Put("points", 20)
                    .Put("tag", $"Achieved.{Pascal(zone.Id)}Slayer")
                    .List("unlocks", [unlocks[$"{zone.Id}-Title"]])
                    .Item("criteria")
                    .Put("description", "Felled")
                    .Put("verb", "Event.Kill")
                    .Put("count", 100)
                    .List("all", [family.Tag])
                    .Put("scene", $"maps/{zone.Id}")
                    .Close()
            );

            Write(
                $"achievements/{zone.Id}-exalted",
                ".vxdef",
                new Yaml("AchievementDefinition", "A standing criterion, on the requirement algebra rather than the event bus.")
                    .Put("displayName", $"{faction.Name}: Exalted")
                    .Put("description", "Stand exalted.")
                    .Put("points", 15)
                    .Put("tag", $"Achieved.{Pascal(faction.Id)}Exalted")
                    .List("unlocks", [unlocks[$"{zone.Id}-Mount"]])
                    .Item("requires")
                    .Put("kind", "HasTag")
                    .Put("subject", $"Faction.{Pascal(faction.Id)}.Exalted")
                    .Close()
            );

            Write(
                $"achievements/{zone.Id}-explorer",
                ".vxdef",
                new Yaml("AchievementDefinition", "Completion, off the exploration record's own tag.")
                    .Put("displayName", $"{zone.Name} Explorer")
                    .Put("description", "Find everything worth finding.")
                    .Put("points", 10)
                    .Put("tag", $"Achieved.{Pascal(zone.Id)}Explorer")
                    .List("unlocks", [unlocks[$"{zone.Id}-Toy"]])
                    .Item("requires")
                    .Put("kind", "HasTag")
                    .Put("subject", $"Completion.{zone.Name}")
                    .Close()
            );

            written.Add($"achievements/{zone.Id}-slayer");
            written.Add($"achievements/{zone.Id}-exalted");
            written.Add($"achievements/{zone.Id}-explorer");
        }

        // ⚠ Cascades off the others: earning one can earn another in the same settle pass, which is
        // the loop CollectionRecord.Award exists for.
        Write(
            "achievements/decorated",
            ".vxdef",
            new Yaml("AchievementDefinition", "Counting a collection needs no new requirement kind: the record answers Collection.Points as a value.")
                .Put("displayName", "Decorated")
                .Put("description", "Earn two hundred achievement points.")
                .Put("points", 50)
                .Put("hidden", true)
                .Put("tag", "Achieved.Decorated")
                .Item("requires")
                .Put("kind", "Value")
                .Put("subject", "Collection.Points")
                .Put("comparison", "AtLeast")
                .Put("value", 200)
                .Close()
        );

        Write(
            "achievements/stablemaster",
            ".vxdef",
            new Yaml("AchievementDefinition", "The record answers Collection.Mount as an ordinary requirement value.")
                .Put("displayName", "Stablemaster")
                .Put("description", "Own four mounts.")
                .Put("points", 25)
                .Put("tag", "Achieved.Stablemaster")
                .Item("requires")
                .Put("kind", "Value")
                .Put("subject", "Collection.Mount")
                .Put("comparison", "AtLeast")
                .Put("value", 4)
                .Close()
        );

        written.Add("achievements/decorated");
        written.Add("achievements/stablemaster");

        return written;
    }

    /// <summary>Dungeons, battlegrounds, the guild charter, the channels, and the freehold.</summary>
    /// <param name="creatures">Whose bosses to point the encounters at.</param>
    /// <returns>Nothing worth returning; everything downstream reads it from the catalog.</returns>
    public void Rest(Dictionary<string, List<string>> creatures) {
        // ── Dungeons: one per zone from the third onwards ────────────────────────────────────────
        foreach (var zone in Tables.Zones.Skip(2)) {
            var family = Tables.Families.First(entry => entry.Zone == zone.Id);
            var yaml = new Yaml("InstanceDefinition", $"{zone.Name}'s five-player dungeon.")
                .Put("displayName", $"The {zone.Name} Deep")
                .Put("scene", $"maps/{zone.Id}-deep")
                .Put("minimumPlayers", 1)
                .Put("maximumPlayers", 5);

            var first = true;

            foreach (var (id, name, gate, checkpoint) in new[] {
                ("warden", "The Warden", false, true),
                ("choirmaster", "The Choirmaster", true, false),
                ("crowned", "The Crowned", false, true)
            }) {
                yaml.Item(first ? "encounters" : null)
                    .Put("id", id)
                    .Put("displayName", name)
                    .Put("isGate", gate)
                    .Put("isCheckpoint", checkpoint)
                    .Close();

                first = false;
            }

            // ⚠ Two difficulties with different lockout *scopes*, which is the field that matters:
            // a daily per character and a weekly per account are different promises.
            yaml.Item("difficulties")
                .Put("id", "normal").Put("displayName", "Normal")
                .Put("healthScale", 1f).Put("damageScale", 1f)
                .Put("tag", $"Instance.{Pascal(zone.Id)}.Normal")
                .Open("lockout").Put("reset", "Daily").Put("scope", "Character").Put("completions", 1).Close()
                .Close()
                .Item()
                .Put("id", "heroic").Put("displayName", "Heroic")
                .Put("healthScale", 2.4f).Put("damageScale", 1.8f)
                .Put("tag", $"Instance.{Pascal(zone.Id)}.Heroic")
                .Item("requires")
                .Put("kind", "Value").Put("subject", "Level").Put("comparison", "AtLeast").Put("value", zone.High)
                .Close()
                .Open("lockout").Put("reset", "Weekly").Put("scope", "Account").Put("completions", 1).Close()
                .Close();

            Write($"instances/{zone.Id}-deep", ".vxdef", yaml);
        }

        // ── Battlegrounds and arenas ─────────────────────────────────────────────────────────────
        foreach (var (id, name, teams, size, score, minutes) in new[] {
            ("ravensford", "Ravensford", 2, 10, 1_500, 20),
            ("saltmere-shore", "Saltmere Shore", 2, 15, 2_000, 25),
            ("kettle-pit", "The Kettle Pit", 2, 5, 800, 12)
        }) {
            var yaml = new Yaml("PvpMapDefinition", "Three capture points and a payload: every battleground anybody has shipped, arranged.")
                .Put("displayName", name)
                .Put("kind", "Battleground")
                .Put("scene", $"maps/{id}")
                .Put("teams", teams)
                .Put("teamSize", size)
                .Put("scoreToWin", score)
                .Put("timeLimit", minutes * 60f)
                .Put("rounds", 1)
                .Put("tag", $"Match.{Pascal(id)}");

            var first = true;

            foreach (var (point, label, kind, seconds, tick) in new[] {
                ("mill", "The Mill", "CapturePoint", 8f, 3),
                ("ford", "The Ford", "CapturePoint", 12f, 5),
                ("rookery", "The Rookery", "CapturePoint", 8f, 3),
                ("waggon", "The Powder Waggon", "Payload", 6f, 0)
            }) {
                yaml.Item(first ? "objectives" : null)
                    .Put("id", point)
                    .Put("displayName", label)
                    .Put("kind", kind)
                    .Put("captureSeconds", seconds)
                    .Put("tickSeconds", tick > 0 ? 2f : 0f, 0f)
                    .Put("pointsPerTick", tick, 0)
                    .Put("pointsOnCapture", kind == "Payload" ? 200 : 0, 0)
                    .Put("startingOwner", kind == "Payload" ? 1 : 0, 0)
                    .Close();

                first = false;
            }

            Write($"pvp/{id}", ".vxdef", yaml);
        }

        Write(
            "pvp/hollow-arena",
            ".vxdef",
            new Yaml("PvpMapDefinition", "A three-a-side arena on the battleground's middle.")
                .Put("displayName", "The Hollow")
                .Put("kind", "Arena")
                .Put("scene", "maps/ravensford")
                .Put("teams", 2).Put("teamSize", 3).Put("scoreToWin", 3).Put("timeLimit", 360f).Put("rounds", 5)
                .Put("tag", "Match.Hollow")
                .Item("objectives")
                .Put("id", "hold").Put("displayName", "The Hollow").Put("kind", "ResourceControl")
                .Put("captureSeconds", 4f).Put("tickSeconds", 1f).Put("pointsPerTick", 1)
                .Close()
        );

        // ── Social, chat, housing ────────────────────────────────────────────────────────────────
        foreach (var (id, name, kind, most, subgroup, invite, leave) in new[] {
            ("party", "Party", "Party", 5, 0, true, true),
            ("raid", "Raid", "Squad", 20, 5, false, true),
            ("warband", "Warband", "Squad", 40, 5, false, true),
            ("battleground-team", "Battleground team", "Team", 15, 0, false, false)
        }) {
            Write(
                $"social/{id}",
                ".vxdef",
                new Yaml("GroupPolicyDefinition", "Three kinds, one implementation, four numbers between them.")
                    .Put("displayName", name)
                    .Put("kind", kind)
                    .Put("maximumMembers", most)
                    .Put("subgroupSize", subgroup, 0)
                    .Put("membersMayInvite", invite)
                    .Put("membersMayLeave", leave)
                    .Put("inviteSeconds", 60f)
                    .Put("tag", $"Group.{Pascal(id)}")
                    .List("roles", ["Role.Tank", "Role.Healer", "Role.Damage"])
            );
        }

        Write(
            "social/freehold-charter",
            ".vxdef",
            new Yaml("GuildCharterDefinition", "A charter is content and a guild is not: this is what a *new* guild starts as.")
                .Put("displayName", "Freehold Charter")
                .Put("maximumMembers", 500)
                .Put("tag", "Guild.Member")
                .Item("ranks").Put("displayName", "Steward").Close()
                .Item().Put("displayName", "Marshal")
                .List("permissions", ["Guild.Permission.Invite", "Guild.Permission.Kick", "Guild.Permission.Rank", "Guild.Permission.Withdraw", "Guild.Permission.Speak"])
                .Close()
                .Item().Put("displayName", "Warden").List("permissions", ["Guild.Permission.Invite", "Guild.Permission.Speak"]).Close()
                .Item().Put("displayName", "Recruit").List("permissions", ["Guild.Permission.Speak"]).Close()
        );

        foreach (var (id, name, audience, route, radius, limit, window) in new[] {
            ("say", "Say", "Scene", "Realm", 30f, 8, 10f),
            ("yell", "Yell", "Scene", "Realm", 120f, 2, 30f),
            ("party", "Party", "Group", "Realm", 0f, 12, 10f),
            ("raid", "Raid", "Group", "Realm", 0f, 12, 10f),
            ("guild", "Guild", "Guild", "Gate", 0f, 12, 10f),
            ("officer", "Officer", "Guild", "Gate", 0f, 12, 10f),
            ("whisper", "Whisper", "Direct", "Gate", 0f, 10, 10f),
            ("trade", "Trade", "Global", "Gate", 0f, 2, 60f)
        }) {
            var yaml = new Yaml("ChatChannelDefinition", audience == "Direct" ? "⚠ Gate, and the library refuses anything else: the recipient may be on another shard." : "A channel.")
                .Put("displayName", name)
                .Put("command", id)
                .Put("audience", audience)
                .Put("route", route)
                .Put("radius", radius, 0f)
                .Put("maximumLength", 255)
                .Put("rateLimit", limit)
                .Put("rateWindow", window);

            if (id is "guild" or "officer") {
                yaml.Put("permission", "Guild.Permission.Speak");
            }

            if (id == "trade") {
                yaml.Item("requires").Put("kind", "Value").Put("subject", "Level").Put("comparison", "AtLeast").Put("value", 5).Close();
            }

            Write($"chat/{id}", ".vxdef", yaml);
        }

        Write(
            "housing/freehold",
            ".vxdef",
            new Yaml("PlotDefinition", "A guild hall, which is the case that forces HousePlot.Assign: nobody outranks anybody.")
                .Put("displayName", "The Freehold")
                .Put("budget", 240)
                .Put("snapGrid", 0.5f)
                .Put("snapDegrees", 15f)
                .Put("surfaces", "Floor, Wall, Ceiling, Tabletop, Outdoors")
                .Put("openness", "Visitor").Put("enterTier", "Visitor").Put("useTier", "Guest")
                .Put("decorateTier", "Resident").Put("administerTier", "Owner")
                .Put("tag", "Plot.Freehold")
                .Item("requires")
                .Put("kind", "Value").Put("subject", "Level").Put("comparison", "AtLeast").Put("value", 12)
                .Close()
        );

        foreach (var zone in Tables.Zones) {
            foreach (var (id, name, cost, most, surfaces) in new[] {
                ("banner", "Banner", 4, 8, "Wall, Outdoors"),
                ("rug", "Rug", 2, 12, "Floor"),
                ("brazier", "Brazier", 6, 6, "Floor, Outdoors"),
                ("trophy", "Trophy", 20, 1, "Tabletop, Wall"),
                ("bookcase", "Bookcase", 8, 4, "Floor")
            }) {
                Write(
                    $"housing/furniture/{zone.Id}-{id}",
                    ".vxdef",
                    new Yaml("FurnitureDefinition", $"{zone.Name} {name.ToLowerInvariant()}.")
                        .Put("displayName", $"{zone.Name} {name}")
                        .Put("cost", cost)
                        .Put("maximumPerPlot", most)
                        .Put("surfaces", surfaces)
                        .Put("tag", $"Furniture.{Pascal(id)}.{zone.Name}")
                );
            }
        }
    }
}
