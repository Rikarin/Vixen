// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests.Tests;

/// <summary>Three quests, two events that chain into each other, and the tags they all need.</summary>
/// <remarks>
///     The camp pair is cyclic on purpose — defence fails into retake, retake succeeds into defence —
///     because that is the shape doc 28 says makes a chain feel alive and it is the shape both the
///     director and the editor's projection have to survive.
/// </remarks>
public static class Content {
    public const string Prologue = "quests/prologue";
    public const string Chain = "quests/chain";
    public const string Escort = "quests/escort";
    public const string Vigil = "quests/vigil";
    public const string Culling = "quests/culling";
    public const string CampDefence = "events/camp-defence";
    public const string CampRetake = "events/camp-retake";
    public const string Queensdale = "maps/queensdale";
    public const string Elsewhere = "maps/elsewhere";
    public const string Ore = "items/ore";
    public const string Skeleton = "creatures/skeleton";
    public const string Sword = "items/sword";
    public const string Shield = "items/shield";
    public const string Wand = "items/wand";
    public const string Lever = "props/lever";
    public const string Villager = "npcs/villager";

    public const string Completed = "Quest.Completed.Prologue";
    public const string OnPrologue = "Quest.Active.Prologue";

    public static DefinitionCatalog Catalog() {
        var builder = new DefinitionCatalogBuilder();

        foreach (var verb in QuestVerbs.All) {
            builder.AddTag(verb);
        }

        return builder
            .AddTag("Creature.Undead.Skeleton")
            .AddTag("Creature.Beast.Wolf")
            .AddTag("Creature.Bandit")
            .AddTag("Item.Ore")
            .Add(
                Prologue,
                new QuestDefinition {
                    DisplayName = "A Prologue",
                    Tag = Completed,
                    GrantsTags = [OnPrologue],
                    Stages = [
                        new() {
                            Id = "hunt",
                            DisplayName = "Cull the skeletons",
                            Objectives = [
                                new() {
                                    Type = "Kill",
                                    DisplayName = "Skeletons slain",
                                    Count = 3,
                                    TargetTags = ["Creature.Undead"],
                                    Scene = Queensdale
                                }
                            ]
                        },
                        new() {
                            Id = "gather",
                            DisplayName = "Gather ore",
                            Objectives = [
                                new() { Type = "Collect", DisplayName = "Ore held", Count = 2, Target = Ore },
                                new() { Type = "Discover", DisplayName = "Find the cave", Optional = true }
                            ]
                        }
                    ],
                    Rewards = new() {
                        Experience = 500,
                        Items = [new() { Def = Sword }],
                        Choices = [new() { Def = Shield }, new() { Def = Wand }]
                    }
                }
            )
            .Add(
                Culling,
                new QuestDefinition {
                    DisplayName = "Culling",

                    // Two stages counting the *same* verb, which is the only shape that can catch the
                    // kill that ends stage one being counted again by stage two.
                    Stages = [
                        new() {
                            Id = "first",
                            Objectives = [new() { Type = "Kill", Count = 1, TargetTags = ["Creature.Undead"] }]
                        },
                        new() {
                            Id = "second",
                            Objectives = [new() { Type = "Kill", Count = 1, TargetTags = ["Creature.Undead"] }]
                        }
                    ]
                }
            )
            .Add(
                Chain,
                new QuestDefinition {
                    DisplayName = "What Follows",
                    Requirements = [new() { Kind = RequirementKind.HasTag, Subject = Completed }],
                    Stages = [
                        new() {
                            Id = "pull",
                            Objectives = [new() { Type = "Interact", Count = 1, Target = Lever }]
                        }
                    ]
                }
            )
            .Add(
                Escort,
                new QuestDefinition {
                    DisplayName = "See Them Home",
                    Stages = [
                        new() {
                            Id = "walk",
                            TimeLimit = 30f,
                            Objectives = [new() { Type = "Escort", Count = 1, Target = Villager }]
                        }
                    ]
                }
            )
            .Add(
                Vigil,
                new QuestDefinition {
                    DisplayName = "Hold the Line",
                    Stages = [
                        new() {
                            Id = "hold",
                            Objectives = [new() { Type = "Survive", DisplayName = "Seconds held", Count = 5 }]
                        }
                    ]
                }
            )
            .Add(
                CampDefence,
                new DynamicEventDefinition {
                    DisplayName = "Defend the camp",
                    Scene = Queensdale,
                    Tag = "Event.Active.CampDefence",
                    Duration = 60f,
                    Objectives = [new() { Type = "Kill", Count = 10, TargetTags = ["Creature.Bandit"] }],
                    Scaling = new() { BaseParticipants = 5, PerParticipant = 0.2f, Maximum = 3f },

                    // Deliberately out of order: the tiers are sorted at compile time, and a search
                    // from the bottom would answer Bronze for a player who earned Gold.
                    Tiers = [
                        new() { DisplayName = "Bronze", Minimum = 1 },
                        new() { DisplayName = "Gold", Minimum = 50, Rewards = new() { Experience = 900 } },
                        new() { DisplayName = "Silver", Minimum = 20 }
                    ],
                    OnFailure = [CampRetake]
                }
            )
            .Add(
                CampRetake,
                new DynamicEventDefinition {
                    DisplayName = "Retake the camp",
                    Scene = Queensdale,
                    Duration = 90f,
                    Objectives = [new() { Type = "Interact", Count = 1, Target = Lever }],
                    OnSuccess = [CampDefence]
                }
            )
            .Build();
    }

    /// <summary>Somebody, by number.</summary>
    public static PlayerId Player(ulong who) => new(who);

    /// <summary>A kill of a skeleton in Queensdale, by whoever.</summary>
    public static GameplayEvent Kill(GameplayTagSet tags, ulong who = 1, string scene = Queensdale) =>
        new(Verb(QuestVerbs.Kill), DefId.From(Skeleton), DefId.From(scene), 1, Player(who), tags);

    /// <summary>The tag set of a skeleton.</summary>
    public static GameplayTagSet Undead(GameplayTagTable table) {
        var tags = new GameplayTagSet();

        tags.Add(table.Resolve("Creature.Undead.Skeleton"));

        return tags;
    }

    /// <summary>A verb, resolved against the shared catalog's table.</summary>
    public static GameplayTag Verb(string name) => Table.Resolve(name);

    /// <summary>The table every helper here resolves against.</summary>
    public static GameplayTagTable Table { get; } = Catalog().Tags;
}
