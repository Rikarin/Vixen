// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

partial class World {
    /// <summary>One effect per ability that applies one, plus consumable buffs.</summary>
    /// <returns>Their addresses, by id.</returns>
    public Dictionary<string, string> Effects() {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);

        // Per class: a damage-over-time, a snare, a shield and a rally.
        foreach (var cls in Tables.Classes) {
            foreach (var (id, name, seconds, stacking, attribute, value, granted) in new[] {
                ("burn", "Searing", 6f, "StackTo", $"{cls.School}Taken", 0.05f, $"State.{cls.Name}.Burning"),
                ("snare", "Hobbled", 8f, "Refresh", "MoveSpeed", -0.5f, "State.Snared"),
                ("ward", "Warded", 12f, "Refresh", "Armour", 0.25f, "State.Warded"),
                ("rally", "Rallied", 20f, "Refresh", "MaximumHealth", 0.15f, "State.Rallied")
            }) {
                var address = $"effects/{cls.Id}-{id}";

                Write(
                    address,
                    ".vxeffect",
                    new Yaml("EffectDefinition", $"{cls.Name}'s {name.ToLowerInvariant()}.")
                        .Put("displayName", $"{name}")
                        .Put("duration", seconds)
                        .Put("period", id == "burn" ? 2f : 0f, 0f)
                        .Put("stacking", stacking)
                        .Put("maximumStacks", id == "burn" ? 3 : 1, 1)
                        .List("tags", [$"Effect.{cls.Name}.{name}"])
                        .List("grantedTags", [granted])
                        .List("cancelOn", ["Event.Cleansed"])
                        .Item("modifiers")
                        .Put("attribute", attribute)
                        .Put("op", value is > -1 and < 1 ? "AddPercent" : "Add")
                        .Put("value", value)
                        .Close()
                );

                byId[$"{cls.Id}-{id}"] = address;
            }
        }

        // Per zone: what its food and its potion do.
        foreach (var zone in Tables.Zones) {
            foreach (var (id, name, seconds, attribute, value) in new[] {
                ("well-fed", "Well Fed", 900f, "Stamina", zone.High * 0.5f),
                ("fortified", "Fortified", 600f, "Armour", zone.High * 2f),
                ("quickened", "Quickened", 300f, "CritChance", 0.03f)
            }) {
                var address = $"effects/{zone.Id}-{id}";

                Write(
                    address,
                    ".vxeffect",
                    new Yaml("EffectDefinition", $"What {zone.Name}'s consumables grant.")
                        .Put("displayName", $"{name} ({zone.Name})")
                        .Put("duration", seconds)
                        .Put("stacking", "Refresh")
                        .List("tags", [$"Effect.Boon.{name.Replace(" ", string.Empty, StringComparison.Ordinal)}"])
                        .List("grantedTags", [$"State.{name.Replace(" ", string.Empty, StringComparison.Ordinal)}"])
                        .Item("modifiers")
                        .Put("attribute", attribute)
                        .Put("op", value < 1 ? "AddPercent" : "Add")
                        .Put("value", value < 1 ? value : Nice(value))
                        .Close()
                );

                byId[$"{zone.Id}-{id}"] = address;
            }
        }

        return byId;
    }

    /// <summary>Thirty-odd abilities a class, banded by level, plus one per creature family.</summary>
    /// <param name="effects">What an ability can apply, by id.</param>
    /// <returns>Every ability address, and the per-family creature ones.</returns>
    public (List<string> Player, Dictionary<string, List<string>> Creature) Abilities(Dictionary<string, string> effects) {
        var player = new List<string>();
        var creature = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // ⚠ The shapes rather than the names are the point of this table: every ability in the game
        // is one of nine arrangements of targeting, cast, cooldown and cost, and a class is which of
        // them it gets and at what level. That is what makes a rotation rather than a list.
        (string Id, string Name, string Targeting, float Cast, float Channel, float Cooldown, int Charges, float Range, float Radius, float Angle, float Power, string? Applies)[] shapes = [
            ("strike", "Strike", "Target", 0f, 0f, 0f, 0, 5f, 0f, 0f, 0.6f, null),
            ("opener", "Opener", "Target", 0f, 0f, 6f, 0, 5f, 0f, 0f, 1.0f, "ward"),
            ("bolt", "Bolt", "Target", 2.5f, 0f, 0f, 0, 30f, 0f, 0f, 0.9f, "burn"),
            ("nova", "Nova", "Ground", 1.5f, 0f, 12f, 0, 25f, 8f, 0f, 0.8f, "burn"),
            ("cone", "Sweep", "Cone", 0f, 0f, 15f, 0, 12f, 0f, 60f, 0.5f, null),
            ("channel", "Channel", "Target", 0f, 4f, 30f, 0, 30f, 0f, 0f, 0.4f, null),
            ("aimed", "Aimed Shot", "Target", 2f, 0f, 10f, 2, 40f, 0f, 0f, 1.1f, null),
            ("cry", "Cry", "Self", 0f, 0f, 180f, 0, 0f, 20f, 0f, 0f, "rally"),
            ("snare", "Snare", "Target", 0f, 0f, 12f, 0, 20f, 0f, 0f, 0.2f, "snare")
        ];

        foreach (var cls in Tables.Classes) {
            var rank = 0;

            // Every shape, at four ranks each — which is what a level 1–25 class list looks like:
            // the same nine buttons, getting bigger, unlocked over the levels.
            foreach (var shape in shapes) {
                for (var tier = 1; tier <= 4; tier++) {
                    var level = Math.Min(25, (rank++ * 25 / (shapes.Length * 4)) + 1);
                    var address = $"abilities/{cls.Id}/{shape.Id}-{tier}";
                    var applies = shape.Applies is null ? null : effects.GetValueOrDefault($"{cls.Id}-{shape.Applies}");

                    var yaml = new Yaml("AbilityDefinition", $"{cls.Name} rank {tier}, learnt at level {level}.")
                        .Put("displayName", $"{shape.Name} {Roman(tier)}")
                        .Put("targeting", shape.Targeting)
                        .Put("range", shape.Range, 0f)
                        .Put("radius", shape.Radius, 0f)
                        .Put("angle", shape.Angle, 0f)
                        .Put("castTime", shape.Cast, 0f)
                        .Put("channelTime", shape.Channel, 0f)
                        .Put("channelPeriod", shape.Channel > 0 ? 1f : 0f, 0f)
                        .Put("cooldown", shape.Cooldown, 0f)
                        .Put("charges", shape.Charges, 0)
                        .List("tags", [$"Ability.{cls.Name}.{shape.Name.Replace(" ", string.Empty, StringComparison.Ordinal)}", $"Ability.School.{cls.School}"])
                        .Item("requirements")
                        .Put("kind", "Value")
                        .Put("subject", "Level")
                        .Put("comparison", "AtLeast")
                        .Put("value", level)
                        .Close();

                    if (shape.Power > 0) {
                        yaml.Item("costs")
                            .Put("attribute", cls.Resource)
                            .Put("amount", Nice(10 + (tier * 8)))
                            .Close();
                    }

                    if (applies is not null) {
                        yaml.List(shape.Targeting == "Self" ? "appliesToSelf" : "appliesToTarget", [applies]);
                    }

                    if (shape.Power > 0) {
                        yaml.Open("damage")
                            .Put("amount", Nice(20 * tier * shape.Power))
                            .Put("coefficient", shape.Power)
                            .Put("scalesWith", cls.School == "Fire" ? "SpellPower" : "AttackPower")
                            .Put("school", cls.School)
                            .Put("threatMultiplier", cls.Id == "vanguard" ? 4f : 1f, 1f)
                            .Close();
                    }

                    Write(address, ".vxdef", yaml);
                    player.Add(address);
                }
            }
        }

        // One signature ability a family, so a fight is not every creature swinging.
        foreach (var family in Tables.Families) {
            var address = $"abilities/creatures/{family.Id}-signature";

            Write(
                address,
                ".vxdef",
                new Yaml("AbilityDefinition", $"What a {family.Name.ToLowerInvariant()} does that a swing does not.")
                    .Put("displayName", $"{family.Name}'s Fury")
                    .Put("targeting", "Target")
                    .Put("range", 8f)
                    .Put("cooldown", 12f)
                    .List("tags", [$"Ability.Creature.{family.Id}", $"Ability.School.{family.School}"])
                    .Open("damage")
                    .Put("amount", 30)
                    .Put("coefficient", 0.5f)
                    .Put("scalesWith", "AttackPower")
                    .Put("school", family.School)
                    .Close()
            );

            creature[family.Id] = [address];
        }

        return (player, creature);
    }

    /// <summary>Ballistics for the weapons that have them.</summary>
    /// <param name="ranged">The item addresses that need a matching weapon profile.</param>
    /// <returns>Their addresses.</returns>
    public List<string> Ballistics(IEnumerable<string> ranged) {
        var written = new List<string>();

        foreach (var item in ranged) {
            var id = item[(item.LastIndexOf('/') + 1)..];
            var address = $"weapons/{id}";
            var carbine = id.Contains("carbine", StringComparison.Ordinal);

            Write(
                address,
                ".vxdef",
                new Yaml("WeaponDefinition", $"Ballistics for {item}. Hitscan, so the rewind budget is exercised.")
                    .Put("displayName", id)
                    .Put("kind", "Hitscan")
                    .Put("range", carbine ? 40f : 60f)
                    .Put("roundsPerSecond", carbine ? 3.2f : 1.2f)
                    .Put("magazine", carbine ? 24 : 6)
                    .Put("reserve", carbine ? 180 : 60)
                    .Put("reloadTime", carbine ? 1.8f : 2.4f)
                    .Put("reloadsPerRound", !carbine)
                    .Put("automatic", carbine)
                    .Put("pellets", 1)
                    .List("tags", ["Weapon.Ranged"])
                    .Open("damage")
                    .Put("amount", carbine ? 14 : 48)
                    .Put("school", "Physical")
                    .Close()
                    .Open("falloff")
                    .Put("start", carbine ? 12f : 25f)
                    .Put("end", carbine ? 34f : 55f)
                    .Put("minimum", 0.4f)
                    .Close()
                    .Open("spread")
                    .Put("base", carbine ? 1.2f : 0.4f)
                    .Put("perShot", carbine ? 0.4f : 0.9f)
                    .Put("maximum", 4f)
                    .Put("recovery", 3f)
                    .Put("movingMultiplier", 2.5f)
                    .Put("aimingMultiplier", 0.25f)
                    .Close()
            );

            written.Add(address);
        }

        return written;
    }

    static string Roman(int value) => value switch { 1 => "I", 2 => "II", 3 => "III", _ => "IV" };
}
