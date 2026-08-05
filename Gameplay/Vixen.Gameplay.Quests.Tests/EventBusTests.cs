// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Quests.Tests;

/// <summary>The kernel's bus, and the filter rules quests depend on being exactly right.</summary>
public class EventBusTests {
    static readonly GameplayTagTable Tags = Content.Table;

    [Fact]
    public void AFilterWhoseVerbDidNotResolveMatchesNothing() {
        // ⚠ The whole reason an empty range is "nothing" rather than "everything": a designer's typo
        // must not produce an objective that completes on the first thing that happens anywhere.
        var filter = new GameplayEventFilter(Tags.RangeOf("Event.Kil"));

        Assert.False(filter.IsSome);
        Assert.False(filter.Matches(new(Content.Verb(QuestVerbs.Kill))));
        Assert.False(filter.Matches(new(Content.Verb(QuestVerbs.Craft))));
    }

    [Fact]
    public void EveryVerbIsSomethingACallerHasToWriteDown() {
        Assert.True(GameplayEventFilter.Everything.Matches(new(Content.Verb(QuestVerbs.Kill))));
        Assert.True(GameplayEventFilter.Everything.Matches(new(Content.Verb(QuestVerbs.Spend))));
    }

    [Fact]
    public void AParentVerbMatchesItsChildren() {
        var filter = new GameplayEventFilter(Tags.RangeOf(QuestVerbs.Root));

        Assert.True(filter.Matches(new(Content.Verb(QuestVerbs.Kill))));
        Assert.True(filter.Matches(new(Content.Verb(QuestVerbs.Deliver))));
    }

    [Fact]
    public void TheSceneFilterKeepsAKillInTheWrongMapOut() {
        var filter = new GameplayEventFilter(Tags.RangeOf(QuestVerbs.Kill), Scene: DefId.From(Content.Queensdale));
        var undead = Content.Undead(Tags);

        Assert.True(filter.Matches(Content.Kill(undead)));
        Assert.False(filter.Matches(Content.Kill(undead, scene: Content.Elsewhere)));
    }

    [Fact]
    public void TheTagQueryFiltersOnTheSubjectsOwnTags() {
        var filter = new GameplayEventFilter(
            Tags.RangeOf(QuestVerbs.Kill),
            Tags: GameplayTagQuery.Resolve(Tags, any: ["Creature.Undead"])
        );

        var wolf = new GameplayTagSet();
        wolf.Add(Tags.Resolve("Creature.Beast.Wolf"));

        Assert.True(filter.Matches(Content.Kill(Content.Undead(Tags))));
        Assert.False(filter.Matches(Content.Kill(wolf)));
    }

    [Fact]
    public void DeliveryIsInSubscriptionOrder() {
        var bus = new GameplayEventBus();
        var order = new List<int>();

        for (var index = 0; index < 5; index++) {
            var slot = index;
            bus.Subscribe(GameplayEventFilter.Everything, (in GameplayEvent _) => order.Add(slot));
        }

        Assert.Equal(5, bus.Post(new(Content.Verb(QuestVerbs.Kill))));
        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public void ASubscriptionMadeDuringADispatchDoesNotSeeThatEvent() {
        // ⚠ The rule that keeps one kill from being counted by both the stage it finished and the
        // stage it started — which is exactly how "no objective completes twice" gets broken.
        var bus = new GameplayEventBus();
        var late = 0;

        bus.Subscribe(
            GameplayEventFilter.Everything,
            (in GameplayEvent _) =>
                bus.Subscribe(GameplayEventFilter.Everything, (in GameplayEvent _) => late++)
        );

        bus.Post(new(Content.Verb(QuestVerbs.Kill)));

        Assert.Equal(0, late);

        bus.Post(new(Content.Verb(QuestVerbs.Kill)));

        Assert.Equal(1, late);
    }

    [Fact]
    public void CancellingDuringADispatchStopsDeliveryImmediately() {
        var bus = new GameplayEventBus();
        var second = 0;
        GameplayEventSubscription? later = null;

        bus.Subscribe(GameplayEventFilter.Everything, (in GameplayEvent _) => later!.Cancel());
        later = bus.Subscribe(GameplayEventFilter.Everything, (in GameplayEvent _) => second++);

        bus.Post(new(Content.Verb(QuestVerbs.Kill)));

        Assert.Equal(0, second);
        Assert.Equal(1, bus.Count);
    }

    [Fact]
    public void ACancelledSubscriptionIsCompactedAway() {
        var bus = new GameplayEventBus();
        var subscription = bus.Subscribe(GameplayEventFilter.Everything, (in GameplayEvent _) => { });

        Assert.Equal(1, bus.Count);
        Assert.True(subscription.Cancel());
        Assert.False(subscription.Cancel());
        Assert.Equal(0, bus.Count);
    }

    [Fact]
    public void ASubscriptionFromAnotherBusIsNotCancelledByThisOne() {
        var mine = new GameplayEventBus();
        var theirs = new GameplayEventBus();
        var subscription = theirs.Subscribe(GameplayEventFilter.Everything, (in GameplayEvent _) => { });

        Assert.False(mine.Unsubscribe(subscription));
        Assert.True(subscription.IsActive);
    }

    [Fact]
    public void ACompositeContextAnswersATagFromEitherHalf() {
        var left = new GameplaySubject(AttributeLayout.Empty);
        var right = new GameplaySubject(AttributeLayout.Empty);

        left.Tags.Add(Tags.Resolve("Creature.Undead.Skeleton"));
        right.Tags.Add(Tags.Resolve("Item.Ore"));

        var composite = new CompositeRequirementContext(left, right);

        Assert.True(composite.HasTag(Tags.RangeOf("Creature.Undead")));
        Assert.True(composite.HasTag(Tags.RangeOf("Item.Ore")));
        Assert.False(composite.HasTag(Tags.RangeOf("Creature.Beast")));
    }

    [Fact]
    public void ACompositeSkipsNullsAndTakesTheFirstValueItIsOffered() {
        var layout = new AttributeLayoutBuilder().Add("Power", 12f).Build();
        var subject = new GameplaySubject(layout);
        var composite = new CompositeRequirementContext(null, subject);

        Assert.Equal(1, composite.Count);
        Assert.True(composite.TryGetValue(AttributeId.From("Power"), out var power));
        Assert.Equal(12f, power);
        Assert.False(composite.TryGetValue(AttributeId.From("Nothing"), out _));
    }
}
