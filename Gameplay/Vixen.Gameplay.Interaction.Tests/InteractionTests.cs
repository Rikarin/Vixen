// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Interaction.Tests;

/// <summary>A shared ore node, a per-player herb, a lever and a locked chest.</summary>
public static class Content {
    public const string Ore = "nodes/ore";
    public const string Herb = "nodes/herb";
    public const string Lever = "props/lever";
    public const string Chest = "props/chest";

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Item.Key")
            .Add(
                Ore,
                new InteractableDefinition {
                    DisplayName = "Copper vein",
                    Tag = "Interactable.Node.Ore",
                    Verb = "Mine",
                    ChannelSeconds = 3f,
                    Uses = 2,
                    RespawnSeconds = 60f,
                    Yields = "loot/ore"
                }
            )
            .Add(
                Herb,
                new InteractableDefinition {
                    DisplayName = "Blood herb",
                    Tag = "Interactable.Node.Herb",
                    ChannelSeconds = 2f,
                    Uses = 1,
                    RespawnSeconds = 30f,
                    Instancing = InteractionInstancing.PerPlayer,
                    Yields = "loot/herb"
                }
            )
            .Add(
                Lever,
                new InteractableDefinition {
                    DisplayName = "Rusty lever",
                    Tag = "Interactable.Lever",
                    ChannelSeconds = 0f,
                    Uses = 0,
                    RespawnSeconds = 0f,
                    Interrupts = InterruptOn.Nothing,
                    GrantsTags = ["State.Channelling"]
                }
            )
            .Add(
                Chest,
                new InteractableDefinition {
                    DisplayName = "Locked chest",
                    Tag = "Interactable.Chest",
                    ChannelSeconds = 1f,
                    Uses = 1,
                    RespawnSeconds = 0f,
                    Yields = "loot/chest",
                    Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Item.Key" }]
                }
            )
            .Build();
}

sealed class Keyholder : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class InteractionTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly InteractionLibrary library;

    public InteractionTests() => library = InteractionLibrary.Compile(catalog);

    InteractionNode Node(string address) => new(library.Find(DefId.From(address))!);

    [Fact]
    public void TheContentCompilesWithOneProblemAboutTheChest() {
        // The chest yields, runs out and never comes back — which is exactly what a one-off chest is,
        // so the report says so rather than refusing it.
        Assert.Single(library.Problems);
        Assert.Contains("props/chest", library.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AChannelHasToRunItsCourse() {
        var node = Node(Content.Ore);

        Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(1), 0f));
        Assert.Equal(InteractionRefusal.Unfinished, node.Complete(Content.Player(1), 2f, out _));
        Assert.False(node.IsReady(2f));
        Assert.True(node.IsReady(3f));
        Assert.Equal(InteractionRefusal.None, node.Complete(Content.Player(1), 3f, out var result));
        Assert.Equal(DefId.From("loot/ore"), result.Yields);
    }

    [Fact]
    public void ASharedNodeIsClaimedForTheDurationOfAChannel() {
        // ⚠ Without the claim two players both finish and the rock yields twice.
        var node = Node(Content.Ore);

        node.Begin(Content.Player(1), 0f);

        Assert.Equal(InteractionRefusal.Claimed, node.Begin(Content.Player(2), 0f));
        Assert.Equal(InteractionRefusal.NotChannelling, node.Complete(Content.Player(2), 5f, out _));

        node.Complete(Content.Player(1), 3f, out _);

        Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(2), 3f));
    }

    [Fact]
    public void InterruptionConsumesNothing() {
        var node = Node(Content.Ore);

        node.Begin(Content.Player(1), 0f);

        Assert.True(node.Disturb(InterruptOn.Damage));
        Assert.Equal(2, node.RemainingFor(Content.Player(1)));
        Assert.False(node.IsClaimed);
        Assert.Equal(InteractionRefusal.NotChannelling, node.Complete(Content.Player(1), 9f, out _));
    }

    [Fact]
    public void ANodeThatDoesNotCareIsNotDisturbed() {
        var node = Node(Content.Lever);

        node.Begin(Content.Player(1), 0f);

        Assert.False(node.Disturb(InterruptOn.Damage));
        Assert.False(node.Disturb(InterruptOn.Movement));
        Assert.True(node.IsClaimed);
    }

    [Fact]
    public void ALeverNeverRunsOut() {
        var node = Node(Content.Lever);

        for (var pull = 0; pull < 20; pull++) {
            Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(1), pull));
            Assert.Equal(InteractionRefusal.None, node.Complete(Content.Player(1), pull, out _));
        }

        Assert.Equal(-1, node.RemainingFor(Content.Player(1)));
    }

    [Fact]
    public void ASharedNodeRunsOutForEverybodyAndComesBackOnItsTimer() {
        var node = Node(Content.Ore);

        node.Begin(Content.Player(1), 0f);
        node.Complete(Content.Player(1), 3f, out _);
        node.Begin(Content.Player(2), 3f);
        node.Complete(Content.Player(2), 6f, out _);

        Assert.Equal(0, node.RemainingFor(Content.Player(3)));
        Assert.Equal(InteractionRefusal.Depleted, node.Begin(Content.Player(3), 10f));
        Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(3), 70f));
        Assert.Equal(2, node.RemainingFor(Content.Player(3)));
    }

    [Fact]
    public void APerPlayerNodeIsNotStolen() {
        // ⚠ One rock, one respawn timer, and everybody gets their own go at it.
        var node = Node(Content.Herb);

        node.Begin(Content.Player(1), 0f);
        node.Complete(Content.Player(1), 2f, out _);

        Assert.Equal(0, node.RemainingFor(Content.Player(1)));
        Assert.Equal(1, node.RemainingFor(Content.Player(2)));
        Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(2), 2f));
    }

    [Fact]
    public void ARespawnTimerStartsFromTheCompletionThatEmptiedIt() {
        // ⚠ A timer a failed channel could restart is a node somebody can keep out of the world by
        // standing next to it starting and cancelling.
        var node = Node(Content.Ore);

        node.Begin(Content.Player(1), 0f);
        node.Complete(Content.Player(1), 3f, out _);
        node.Begin(Content.Player(1), 3f);
        node.Complete(Content.Player(1), 6f, out _);

        for (var attempt = 0; attempt < 10; attempt++) {
            node.Begin(Content.Player(2), 10f);
            node.Interrupt(Content.Player(2));
        }

        Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(2), 66f));
    }

    [Fact]
    public void ARequirementIsChecked() {
        var node = Node(Content.Chest);
        var holder = new Keyholder();

        Assert.Equal(InteractionRefusal.Requirements, node.Begin(Content.Player(1), 0f, holder));

        holder.Tags.Add(catalog.Tags.Resolve("Item.Key"));

        Assert.Equal(InteractionRefusal.None, node.Begin(Content.Player(1), 0f, holder));
    }

    [Fact]
    public void UsingSomethingGrantsWhateverItGrants() {
        var node = Node(Content.Lever);
        var tags = new GameplayTagSet();

        node.Begin(Content.Player(1), 0f);
        node.Complete(Content.Player(1), 0f, out _, tags);

        Assert.True(tags.Contains(catalog.Tags.Resolve("State.Channelling")));
    }

    [Fact]
    public void TheSeedIsReproducibleFromTheSameChannel() {
        var first = Node(Content.Ore);
        var second = Node(Content.Ore);

        first.Begin(Content.Player(7), 12.5f);
        first.Complete(Content.Player(7), 20f, out var a);
        second.Begin(Content.Player(7), 12.5f);
        second.Complete(Content.Player(7), 20f, out var b);

        Assert.Equal(a.Seed, b.Seed);
        Assert.NotEqual(0ul, a.Seed);
    }

    [Fact]
    public void SomebodyElsesChannelIsNotInterruptible() {
        var node = Node(Content.Ore);

        node.Begin(Content.Player(1), 0f);

        Assert.False(node.Interrupt(Content.Player(2)));
        Assert.True(node.IsClaimed);
        Assert.True(node.Interrupt(Content.Player(1)));
    }

    [Fact]
    public void AnUnlimitedNodeWithATimerIsAProblem() {
        var problems = InteractionLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("props/odd", new InteractableDefinition { Uses = 0, RespawnSeconds = 30f })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("respawn timer", StringComparison.Ordinal));
    }

    [Fact]
    public void PerPlayerInstancingOnAnUnlimitedNodeIsAProblem() {
        var problems = InteractionLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "props/odd",
                        new InteractableDefinition {
                            Uses = 0,
                            RespawnSeconds = 0f,
                            Instancing = InteractionInstancing.PerPlayer
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("instancing does nothing", StringComparison.Ordinal));
    }
}
