// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Tests;

public class EffectSetTests {
    static readonly AttributeId Power = AttributeId.From("Power");

    static GameplayTagTable Tags() =>
        new GameplayTagTableBuilder()
            .Add("Effect.Control.Stun")
            .Add("Effect.Buff.Might")
            .Add("Effect.Damage.Burning")
            .Add("State.Stunned")
            .Add("State.Mighty")
            .Add("Ability.Cast.Fireball")
            .Add("Ability.Move")
            .Add("Event.Damaged")
            .Add("Event.Moved")
            .Build();

    static AttributeLayout Layout() => new AttributeLayoutBuilder().Add("Power", 100f).Build();

    static EffectDefinition Might(EffectStacking stacking, int maximumStacks = 1, float duration = 10f) =>
        new() {
            Address = "effects/might",
            Id = DefId.From("effects/might"),
            Duration = duration,
            Stacking = stacking,
            MaximumStacks = maximumStacks,
            Tags = ["Effect.Buff.Might"],
            GrantedTags = ["State.Mighty"],
            Modifiers = [new() { Attribute = "Power", Op = ModifierOp.Add, Value = 10f }]
        };

    static GameplaySubject Subject() => new(Layout());

    [Fact]
    public void ApplyingAnEffectGrantsItsTagsAndItsModifiers() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Refresh), tags);

        var handle = subject.Effects.Apply(template);

        Assert.True(handle.IsSome);
        Assert.Equal(110f, subject.Attributes.ValueOf(Power));
        Assert.True(subject.Tags.Contains(tags.Require("State.Mighty")));
    }

    [Fact]
    public void ExpiryTakesBackExactlyWhatWasGranted() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Refresh, duration: 2f), tags);

        subject.Effects.Apply(template);
        var events = new List<EffectEvent>();
        subject.Tick(2f, events);

        Assert.Equal(100f, subject.Attributes.ValueOf(Power));
        Assert.False(subject.Tags.Contains(tags.Require("State.Mighty")));
        Assert.Equal(0, subject.Effects.Count);
        Assert.Contains(events, entry => entry.Kind == EffectEventKind.Expired);
    }

    [Fact]
    public void NoneRefusesTheSecondApplication() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.None), tags);

        subject.Effects.Apply(template);
        var events = new List<EffectEvent>();

        Assert.Equal(EffectHandle.None, subject.Effects.Apply(template, 0, events));
        Assert.Equal(1, subject.Effects.Count);
        Assert.Equal(110f, subject.Attributes.ValueOf(Power));
        Assert.Contains(events, entry => entry.Kind == EffectEventKind.Refused);
    }

    [Fact]
    public void RefreshPutsTheDurationBackToFullAndAddsNoSecondStack() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Refresh, duration: 10f), tags);

        subject.Effects.Apply(template);
        subject.Tick(6f);
        subject.Effects.Apply(template);

        Assert.Equal(1, subject.Effects.Count);
        Assert.Equal(1, subject.Effects.Active[0].Stacks);
        Assert.Equal(10f, subject.Effects.Active[0].Remaining, 4);
        Assert.Equal(110f, subject.Attributes.ValueOf(Power));
    }

    [Fact]
    public void ExtendAddsToWhatIsLeft() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Extend, duration: 10f), tags);

        subject.Effects.Apply(template);
        subject.Tick(6f);
        subject.Effects.Apply(template);

        Assert.Equal(1, subject.Effects.Count);
        Assert.Equal(14f, subject.Effects.Active[0].Remaining, 4);
    }

    [Fact]
    public void StackToCountsUpToItsMaximumAndScalesTheModifiers() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.StackTo, 3), tags);

        subject.Effects.Apply(template);
        Assert.Equal(110f, subject.Attributes.ValueOf(Power));

        subject.Effects.Apply(template);
        Assert.Equal(2, subject.Effects.Active[0].Stacks);
        Assert.Equal(120f, subject.Attributes.ValueOf(Power));

        subject.Effects.Apply(template);
        subject.Effects.Apply(template);
        Assert.Equal(3, subject.Effects.Active[0].Stacks);
        Assert.Equal(130f, subject.Attributes.ValueOf(Power));

        Assert.Equal(1, subject.Effects.Count);
        Assert.Equal(3, subject.Effects.StacksOf(DefId.From("effects/might")));
    }

    [Fact]
    public void StackToTakesEveryStackOffAtOnceWhenItExpires() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.StackTo, 3, 5f), tags);

        subject.Effects.Apply(template);
        subject.Effects.Apply(template);
        subject.Effects.Apply(template);
        subject.Tick(5f);

        Assert.Equal(100f, subject.Attributes.ValueOf(Power));
        Assert.False(subject.Tags.Contains(tags.Require("State.Mighty")));
    }

    [Fact]
    public void IndependentGivesEveryApplicationItsOwnInstanceAndItsOwnClock() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Independent, duration: 10f), tags);

        subject.Effects.Apply(template);
        subject.Tick(4f);
        subject.Effects.Apply(template);

        Assert.Equal(2, subject.Effects.Count);
        Assert.Equal(120f, subject.Attributes.ValueOf(Power));

        subject.Tick(6f);

        Assert.Equal(1, subject.Effects.Count);
        Assert.Equal(110f, subject.Attributes.ValueOf(Power));
        Assert.True(subject.Tags.Contains(tags.Require("State.Mighty")));
    }

    [Fact]
    public void TwoInstigatorsGetTwoInstancesEvenUnderARefreshPolicy() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Refresh), tags);

        subject.Effects.Apply(template, 1);
        subject.Effects.Apply(template, 2);

        Assert.Equal(2, subject.Effects.Count);
        Assert.Equal(120f, subject.Attributes.ValueOf(Power));
    }

    [Theory]
    [InlineData(6f, 2f, 3)]
    [InlineData(5f, 2f, 2)]
    [InlineData(1f, 0.25f, 4)]
    [InlineData(10f, 1f, 10)]
    public void APeriodicEffectTicksExactlyAsOftenAsItsDurationBuys(float duration, float period, int expected) {
        var tags = Tags();
        var subject = Subject();

        var burning = new EffectDefinition {
            Address = "effects/burning",
            Id = DefId.From("effects/burning"),
            Duration = duration,
            Period = period,
            Tags = ["Effect.Damage.Burning"]
        };

        var template = EffectTemplate.Compile(burning, tags);
        var events = new List<EffectEvent>();

        subject.Effects.Apply(template, 0, events);

        // A frame time nothing divides evenly, so that the accumulate-and-subtract implementation
        // this one replaced would drop a tick.
        for (var frame = 0; frame < 1200 && subject.Effects.Count > 0; frame++) {
            subject.Effects.Tick(1f / 60f, events);
        }

        Assert.Equal(expected, events.Count(entry => entry.Kind == EffectEventKind.Period));
        Assert.Contains(events, entry => entry.Kind == EffectEventKind.Expired);
    }

    [Fact]
    public void APeriodicEffectSurvivesAFrameLongerThanItsPeriod() {
        var tags = Tags();
        var subject = Subject();

        var burning = new EffectDefinition {
            Address = "effects/burning",
            Id = DefId.From("effects/burning"),
            Duration = 10f,
            Period = 1f,
            Tags = ["Effect.Damage.Burning"]
        };

        var events = new List<EffectEvent>();
        subject.Effects.Apply(EffectTemplate.Compile(burning, tags), 0, events);

        // One catastrophic hitch. Four periods came due and four have to be paid.
        subject.Effects.Tick(4.5f, events);

        Assert.Equal(4, events.Count(entry => entry.Kind == EffectEventKind.Period));
    }

    [Fact]
    public void AnImmunityRefusesTheEffectsItNames() {
        var tags = Tags();
        var subject = Subject();

        var ward = new EffectDefinition {
            Address = "effects/ward",
            Id = DefId.From("effects/ward"),
            Duration = 10f,
            Tags = ["Effect.Buff.Might"],
            Immunities = ["Effect.Control"]
        };

        var stun = new EffectDefinition {
            Address = "effects/stun",
            Id = DefId.From("effects/stun"),
            Duration = 3f,
            Tags = ["Effect.Control.Stun"],
            GrantedTags = ["State.Stunned"]
        };

        subject.Effects.Apply(EffectTemplate.Compile(ward, tags));

        var events = new List<EffectEvent>();
        var stunTemplate = EffectTemplate.Compile(stun, tags);

        Assert.True(subject.Effects.IsImmuneTo(stunTemplate));
        Assert.Equal(EffectHandle.None, subject.Effects.Apply(stunTemplate, 0, events));
        Assert.False(subject.Tags.Contains(tags.Require("State.Stunned")));
        Assert.Contains(events, entry => entry.Kind == EffectEventKind.Refused);

        subject.Effects.Clear();

        Assert.True(subject.Effects.Apply(stunTemplate).IsSome);
    }

    [Fact]
    public void BlockedTagsStopTheTargetActingAndAreNotAnImmunity() {
        var tags = Tags();
        var subject = Subject();

        var stun = new EffectDefinition {
            Address = "effects/stun",
            Id = DefId.From("effects/stun"),
            Duration = 3f,
            Tags = ["Effect.Control.Stun"],
            BlockedTags = ["Ability.Cast"]
        };

        subject.Effects.Apply(EffectTemplate.Compile(stun, tags));

        Assert.True(subject.Effects.Blocks(tags.Require("Ability.Cast.Fireball")));
        Assert.False(subject.Effects.Blocks(tags.Require("Ability.Move")));
        Assert.False(subject.Effects.IsImmuneTo(EffectTemplate.Compile(Might(EffectStacking.Refresh), tags)));
    }

    [Fact]
    public void CancelOnEndsAnEffectWhenTheEventItNamesHappens() {
        var tags = Tags();
        var subject = Subject();

        var channel = new EffectDefinition {
            Address = "effects/channelling",
            Id = DefId.From("effects/channelling"),
            Duration = 0f,
            Tags = ["Effect.Buff.Might"],
            CancelOn = ["Event.Damaged"]
        };

        var events = new List<EffectEvent>();
        subject.Effects.Apply(EffectTemplate.Compile(channel, tags), 0, events);

        Assert.Equal(0, subject.Effects.Notify(tags.Require("Event.Moved"), events));
        Assert.Equal(1, subject.Effects.Count);

        Assert.Equal(1, subject.Effects.Notify(tags.Require("Event.Damaged"), events));
        Assert.Equal(0, subject.Effects.Count);
        Assert.Contains(events, entry => entry.Kind == EffectEventKind.Cancelled);
    }

    [Fact]
    public void AnInfiniteEffectNeverExpires() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Refresh, duration: 0f), tags);

        subject.Effects.Apply(template);
        subject.Tick(100000f);

        Assert.Equal(1, subject.Effects.Count);
        Assert.True(float.IsPositiveInfinity(subject.Effects.Active[0].Remaining));
        Assert.Equal(110f, subject.Attributes.ValueOf(Power));
    }

    [Fact]
    public void ATagGrantedByTwoEffectsSurvivesOneOfThemEnding() {
        var tags = Tags();
        var subject = Subject();

        var shortStun = new EffectDefinition {
            Address = "effects/stun-short",
            Id = DefId.From("effects/stun-short"),
            Duration = 1f,
            Tags = ["Effect.Control.Stun"],
            GrantedTags = ["State.Stunned"]
        };

        var longStun = new EffectDefinition {
            Address = "effects/stun-long",
            Id = DefId.From("effects/stun-long"),
            Duration = 5f,
            Tags = ["Effect.Control.Stun"],
            GrantedTags = ["State.Stunned"]
        };

        subject.Effects.Apply(EffectTemplate.Compile(shortStun, tags));
        subject.Effects.Apply(EffectTemplate.Compile(longStun, tags));

        subject.Tick(1f);

        Assert.Equal(1, subject.Effects.Count);
        Assert.True(subject.Tags.Contains(tags.Require("State.Stunned")));

        subject.Tick(4f);

        Assert.False(subject.Tags.Contains(tags.Require("State.Stunned")));
    }

    [Fact]
    public void RemovingByDefinitionTakesEveryInstanceOff() {
        var tags = Tags();
        var subject = Subject();
        var template = EffectTemplate.Compile(Might(EffectStacking.Independent), tags);

        subject.Effects.Apply(template);
        subject.Effects.Apply(template);
        subject.Effects.Apply(template);

        Assert.Equal(3, subject.Effects.RemoveByDefinition(DefId.From("effects/might")));
        Assert.Equal(100f, subject.Attributes.ValueOf(Power));
        Assert.False(subject.Tags.Contains(tags.Require("State.Mighty")));
    }

    [Fact]
    public void ATagTheContentDoesNotHaveCompilesToSomethingThatMatchesNothing() {
        var tags = Tags();
        var subject = Subject();

        var odd = new EffectDefinition {
            Address = "effects/odd",
            Id = DefId.From("effects/odd"),
            Duration = 1f,
            Tags = ["Effect.Nonexistent"],
            BlockedTags = ["Ability.Nonexistent"],
            CancelOn = ["Event.Nonexistent"]
        };

        var template = EffectTemplate.Compile(odd, tags);

        Assert.True(subject.Effects.Apply(template).IsSome);
        Assert.False(subject.Effects.Blocks(tags.Require("Ability.Cast.Fireball")));
        Assert.Equal(0, subject.Effects.Notify(tags.Require("Event.Damaged")));
    }
}
