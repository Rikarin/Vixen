// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>A chart per zone, with points of interest and a fog grid.</summary>
    /// <returns>The chart addresses.</returns>
    public List<string> Maps() {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            var address = $"maps/{zone.Id}";
            var yaml = new Yaml("MapDefinition", $"{zone.Name}'s chart, its points and its fog.")
                .Put("displayName", zone.Name)
                .Put("columns", 64)
                .Put("rows", 48 + (zone.Low * 2))
                .Put("tag", $"Completion.{zone.Name}");

            var first = true;

            foreach (var (id, name, kind, counts) in new[] {
                ("waypoint", "Waystone", "Waypoint", true),
                ("landmark", "The Old Marker", "Landmark", true),
                ("vista", "High Vista", "Vista", true),
                ("camp", "Forward Camp", "Waypoint", true),
                ("ruin", "The Ruin", "Landmark", true),
                ("cache", "Poacher's Cache", "Cache", false)
            }) {
                yaml.Item(first ? "points" : null)
                    .Put("id", id)
                    .Put("displayName", $"{zone.Name} {name}")
                    .Put("kind", kind)
                    .Put("counts", counts ? null : "false")
                    .Put("tag", $"Discovered.{zone.Name}.{Pascal(id)}");

                if (kind == "Vista") {
                    yaml.Item("requires")
                        .Put("kind", "Value")
                        .Put("subject", "Level")
                        .Put("comparison", "AtLeast")
                        .Put("value", zone.Low)
                        .Close();
                }

                yaml.Close();
                first = false;
            }

            Write(address, ".vxdef", yaml);
            written.Add(address);
        }

        return written;
    }

    /// <summary>Gathering nodes and stations, per zone.</summary>
    /// <param name="loot">What a node yields.</param>
    /// <returns>The interactable addresses.</returns>
    public List<string> Interactables(Dictionary<string, string> loot) {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            foreach (var (kind, verb, profession, seconds) in new[] {
                ("ore", "Mine", "Mining", 3f),
                ("herb", "Gather", "Herbalism", 2f),
                ("hide", "Skin", "Leatherworking", 2.5f)
            }) {
                if (!loot.TryGetValue($"node-{zone.Id}-{kind}", out var table)) {
                    continue;
                }

                var address = $"world/{zone.Id}-{kind}-node";

                Write(
                    address,
                    ".vxdef",
                    new Yaml("InteractableDefinition", $"A {zone.Name} {kind} node.")
                        .Put("displayName", $"{zone.Name} {Pascal(kind)}")
                        .Put("verb", verb)
                        .Put("channelSeconds", seconds)
                        .Put("interrupts", "Movement")
                        .Put("uses", 1)
                        .Put("respawnSeconds", 180f)
                        .Put("instancing", kind == "herb" ? "PerPlayer" : "Shared")
                        .Put("yields", table)
                        .Put("tag", $"Node.{Pascal(kind)}.{zone.Name}")
                        .Item("requires")
                        .Put("kind", "HasTag")
                        .Put("subject", $"Profession.{profession}.Apprentice")
                        .Close()
                );

                written.Add(address);
            }

            foreach (var profession in Tables.Professions.Where(entry => !entry.Gathering)) {
                var address = $"world/{zone.Id}-{profession.Id}-station";

                Write(
                    address,
                    ".vxdef",
                    new Yaml("InteractableDefinition", "A station: no yield, no respawn, never runs out.")
                        .Put("displayName", $"{zone.Name} {profession.Name} Station")
                        .Put("verb", "Use")
                        .Put("channelSeconds", 0f)
                        .Put("interrupts", "Nothing")
                        .Put("uses", 0)

                        // ⚠ Explicitly zero. The default is a respawn timer, and a station that never
                        // runs out has nothing to respawn — the library says so rather than ignoring it.
                        .Put("respawnSeconds", 0f)
                        .Put("instancing", "Shared")
                        .List("grantsTags", [profession.Station])
                        .Put("tag", $"Station.{zone.Name}.{profession.Name}")
                );

                written.Add(address);
            }
        }

        return written;
    }

    /// <summary>Waypoints, flight paths, instance doors and a hearthstone.</summary>
    /// <param name="currencies">What a flight costs.</param>
    /// <returns>The travel addresses.</returns>
    public List<string> Travel(Dictionary<string, string> currencies) {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            var waypoint = $"travel/{zone.Id}-waystone";

            Write(
                waypoint,
                ".vxdef",
                new Yaml("TravelPointDefinition", $"{zone.Name}'s waystone.")
                    .Put("displayName", $"{zone.Name} Waystone")
                    .Put("kind", "Waypoint")
                    .Put("to", $"maps/{zone.Id}")
                    .Put("destination", "waypoint")
                    .Put("seconds", 4f)
                    .Put("cost", Nice(zone.Low * 25f))
                    .Put("currency", currencies["gold"])
                    .Put("unlockedBy", $"Discovered.{zone.Name}.Waypoint")
            );

            written.Add(waypoint);
        }

        // A flight path between each pair of adjacent zones, which is what makes a transfer a thing
        // a player does rather than a thing a test does.
        for (var index = 0; index + 1 < Tables.Zones.Length; index++) {
            var from = Tables.Zones[index];
            var to = Tables.Zones[index + 1];
            var address = $"travel/{from.Id}-to-{to.Id}";

            Write(
                address,
                ".vxdef",
                new Yaml("TravelPointDefinition", "A taxi: the one travel kind with a route rather than a destination.")
                    .Put("displayName", $"Flight to {to.Name}")
                    .Put("kind", "Taxi")
                    .Put("from", $"maps/{from.Id}")
                    .Put("to", $"maps/{to.Id}")
                    .Put("destination", "waypoint")
                    .Put("seconds", 45f)
                    .Put("cost", Nice(to.Low * 30f))
                    .Put("currency", currencies["gold"])
                    .Put("unlockedBy", $"Discovered.{to.Name}.Waypoint")
            );

            written.Add(address);
        }

        Write(
            "travel/hearthstone",
            ".vxdef",
            new Yaml("TravelPointDefinition", "The one everybody has.")
                .Put("displayName", "Hearthstone")
                .Put("kind", "Summon")
                .Put("to", "maps/greenmarch")
                .Put("destination", "waypoint")
                .Put("seconds", 10f)
        );

        written.Add("travel/hearthstone");

        return written;
    }

    /// <summary>Mounts, and the battleground's payload.</summary>
    /// <returns>The vehicle addresses.</returns>
    public List<string> Vehicles() {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            foreach (var (id, name, kind, speed, level) in new[] {
                ("courser", "Courser", "Mount", 14f, zone.Low),
                ("harrier", "Harrier", "Flying", 22f, zone.Low + 6)
            }) {
                var address = $"vehicles/{zone.Id}-{id}";
                var yaml = new Yaml("VehicleDefinition", "A mount is a single-seat vehicle whose model is a creature.")
                    .Put("displayName", $"{zone.Name} {name}")
                    .Put("kind", kind)
                    .Put("tag", $"Vehicle.Mount.{zone.Name}{name}")
                    .Open("physics")
                    .Put("maximumSpeed", speed)
                    .Put("acceleration", 8f)
                    .Put("turnRate", kind == "Flying" ? 120f : 180f)
                    .Put("ceiling", kind == "Flying" ? 400f : 0f, 0f)
                    .Close()
                    .Item("seats")
                    .Put("id", "saddle")
                    .Put("controls", true)
                    .Put("role", "Driver")
                    .Put("tag", kind == "Flying" ? "State.Flying" : "State.Mounted")
                    .Item("requires")
                    .Put("kind", "Value")
                    .Put("subject", "Level")
                    .Put("comparison", "AtLeast")
                    .Put("value", level)
                    .Close()
                    .Close();

                Write(address, ".vxdef", yaml);
                written.Add(address);
            }
        }

        // ⚠ Four seats, one of which steers and two of which shoot — and the passengers stay when the
        // driver gets off, which is the policy the library refuses to decide for you.
        Write(
            "vehicles/powder-waggon",
            ".vxdef",
            new Yaml("VehicleDefinition", "Ravensford's payload.")
                .Put("displayName", "Powder Waggon")
                .Put("kind", "Ground")
                .Put("passengersMaySteer", true)
                .Put("tag", "Vehicle.Waggon")
                .Open("physics").Put("maximumSpeed", 6f).Put("acceleration", 2f).Put("turnRate", 45f).Close()
                .Item("seats").Put("id", "driver").Put("controls", true).Put("role", "Driver").Put("tag", "State.Driving").Close()
                .Item().Put("id", "left-gun").Put("role", "Gunner").Put("tag", "State.Gunning").Close()
                .Item().Put("id", "right-gun").Put("role", "Gunner").Put("tag", "State.Gunning").Close()
                .Item().Put("id", "bench").Put("role", "Passenger").Close()
        );

        written.Add("vehicles/powder-waggon");

        return written;
    }

    /// <summary>The scene layouts. Named roots and transforms only.</summary>
    /// <returns>The scene names written.</returns>
    /// <remarks>
    ///     ⚠ <b>No renderables and no collision, and that is deliberate rather than unfinished.</b>
    ///     Every mesh reference in a <c>.vxscene</c> is a guid minted by <c>vixen import</c> and
    ///     recorded in a committed <c>.meta</c>, so a scene cannot name a model this sample does not
    ///     ship. What the fleet needs from a map is <em>where things are</em>; the game reads these
    ///     roots by name and puts its own components on them.
    ///     <para>
    ///         They are generated for the same reason everything else is: an instance names
    ///         <c>maps/ashfen-deep</c> and a battleground names <c>maps/kettle-pit</c>, and a scene
    ///         nobody wrote is a shard that will not start. The content test catches it, which is how
    ///         this method came to exist.
    ///     </para>
    /// </remarks>
    public List<string> Scenes() {
        var written = new List<string>();

        foreach (var zone in Tables.Zones) {
            var roots = new List<(string Name, int X, int Y, int Z)> {
                ("Spawn.Waypoint", 0, 0, 0),
                ("Poi.Waypoint", 0, 0, 0),
                ("Poi.Landmark", -34, 0, 22),
                ("Poi.Vista", 46, 6, 40),
                ("Poi.Camp", 52, 0, -20),
                ("Poi.Ruin", -58, 2, -48),
                ("Poi.Cache", 18, 0, 64),
                ("Border.North", 0, 0, -96),
                ("Border.South", 0, 0, 96)
            };

            var index = 0;

            foreach (var family in Tables.Families.Where(entry => entry.Zone == zone.Id)) {
                roots.Add(($"Camp.{Pascal(family.Id)}", -60 + (index * 40), 0, 14 + (index * 18)));
                roots.Add(($"Patrol.{Pascal(family.Id)}", -40 + (index * 40), 0, -30 - (index * 12)));
                index++;
            }

            foreach (var kind in new[] { "Ore", "Herb", "Hide" }) {
                roots.Add(($"Node.{kind}", -12 + (kind.Length * 9), 0, -30 + (kind.Length * 7)));
            }

            foreach (var profession in Tables.Professions.Where(entry => !entry.Gathering)) {
                roots.Add(($"Station.{profession.Name}", 18, 0, -6 - (profession.Name.Length * 2)));
            }

            roots.Add(("Event.Arena", 20, -8, 70));

            WriteScene($"maps/{zone.Id}", zone.Name, $"{zone.Name} — a public map. {zone.Blurb}", roots);
            written.Add($"maps/{zone.Id}");
        }

        // A dungeon per zone from the third onwards, matching World.Rest's instances.
        foreach (var zone in Tables.Zones.Skip(2)) {
            WriteScene(
                $"maps/{zone.Id}-deep",
                $"{zone.Name} Deep",
                "Three rooms in a line, because the encounter order is the gate.",
                [
                    ("Spawn.Entrance", 0, 0, 0),
                    ("Encounter.Warden", 0, 0, -40),
                    ("Checkpoint.Warden", 0, 0, -20),
                    ("Encounter.Choirmaster", 0, -6, -90),
                    ("Encounter.Crowned", 0, -12, -150),
                    ("Checkpoint.Crowned", 0, -12, -130)
                ]
            );

            written.Add($"maps/{zone.Id}-deep");
        }

        foreach (var (id, name) in new[] { ("ravensford", "Ravensford"), ("saltmere-shore", "Saltmere Shore"), ("kettle-pit", "The Kettle Pit") }) {
            WriteScene(
                $"maps/{id}",
                name,
                "Two spawns, three points and a payload route.",
                [
                    ("Spawn.Team0", 0, 0, -120),
                    ("Spawn.Team1", 0, 0, 120),
                    ("Objective.Mill", -70, 0, -30),
                    ("Objective.Ford", 0, 0, 0),
                    ("Objective.Rookery", 70, 0, 30),
                    ("Objective.Hollow", 0, 0, 0),
                    ("Payload.Start", 0, 0, 96),
                    ("Payload.End", 0, 0, -96)
                ]
            );

            written.Add($"maps/{id}");
        }

        return written;
    }

    void WriteScene(string address, string name, string blurb, IReadOnlyList<(string Name, int X, int Y, int Z)> roots) {
        var text = new System.Text.StringBuilder();

        text.AppendLine("# Generated by Mmo.Content.Authoring. " + blurb);
        text.AppendLine("# Named roots and transforms only — see World.Scenes for why.");
        text.AppendLine("version: 1");
        text.AppendLine($"name: {name}");
        text.AppendLine("roots:");

        foreach (var (root, x, y, z) in roots) {
            text.AppendLine($"  - name: {root}");
            text.AppendLine($"    position: {x} {y} {z}");
        }

        var path = Path.Combine(root, address.Replace('/', Path.DirectorySeparatorChar) + ".vxscene");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
    }
}
