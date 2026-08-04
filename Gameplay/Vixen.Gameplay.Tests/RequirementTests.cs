// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Tests;

public class RequirementTests {
    static GameplayTagTable Tags() =>
        new GameplayTagTableBuilder()
            .Add("Profession.Smithing")
            .Add("Profession.Cooking")
            .Add("State.InCombat")
            .Build();

    static GameplaySubject Subject() =>
        new(new AttributeLayoutBuilder().Add("Level", 1f).Add("Power", 100f).Build());

    [Fact]
    public void ThePlanDocumentsExampleReadsBackTheWayItIsWritten() {
        // requires: [ Level >= 80, HasTag(Profession.Smithing), NotHasTag(State.InCombat) ]
        var tags = Tags();

        var requirements = RequirementSet.Compile(
            [
                new() { Kind = RequirementKind.Value, Subject = "Level", Comparison = RequirementComparison.AtLeast, Value = 80f },
                new() { Kind = RequirementKind.HasTag, Subject = "Profession.Smithing" },
                new() { Kind = RequirementKind.NotHasTag, Subject = "State.InCombat" }
            ],
            tags
        );

        var subject = Subject();

        Assert.False(requirements.IsMetBy(subject));

        subject.Attributes.SetBase(AttributeId.From("Level"), 80f);
        Assert.False(requirements.IsMetBy(subject));

        subject.Tags.Add(tags.Require("Profession.Smithing"));
        Assert.True(requirements.IsMetBy(subject));

        subject.Tags.Add(tags.Require("State.InCombat"));
        Assert.False(requirements.IsMetBy(subject));
    }

    [Fact]
    public void TheFirstUnmetRequirementIsWhatATooltipIsWrittenFrom() {
        var tags = Tags();

        var requirements = RequirementSet.Compile(
            [
                new() { Kind = RequirementKind.Value, Subject = "Level", Comparison = RequirementComparison.AtLeast, Value = 80f },
                new() { Kind = RequirementKind.HasTag, Subject = "Profession.Smithing" }
            ],
            tags
        );

        Assert.True(requirements.TryFindUnmet(Subject(), out var unmet));
        Assert.Equal(RequirementKind.Value, unmet.Kind);
        Assert.Equal(AttributeId.From("Level"), unmet.Subject);
        Assert.Equal(80f, unmet.Value);
    }

    [Fact]
    public void ATagRequirementIsHierarchical() {
        var tags = Tags();
        var subject = Subject();
        subject.Tags.Add(tags.Require("Profession.Smithing"));

        var any = RequirementSet.Compile(
            [new() { Kind = RequirementKind.HasTag, Subject = "Profession" }],
            tags
        );

        var other = RequirementSet.Compile(
            [new() { Kind = RequirementKind.HasTag, Subject = "Profession.Cooking" }],
            tags
        );

        Assert.True(any.IsMetBy(subject));
        Assert.False(other.IsMetBy(subject));
    }

    [Fact]
    public void AValueTheSubjectDoesNotHaveCountsAsZeroRatherThanAsAPass() {
        var tags = Tags();

        var expensive = RequirementSet.Compile(
            [new() { Kind = RequirementKind.Value, Subject = "Currency.Gold", Comparison = RequirementComparison.AtLeast, Value = 500f }],
            tags
        );

        Assert.False(expensive.IsMetBy(Subject()));

        var free = RequirementSet.Compile(
            [new() { Kind = RequirementKind.Value, Subject = "Currency.Gold", Comparison = RequirementComparison.AtMost, Value = 500f }],
            tags
        );

        Assert.True(free.IsMetBy(Subject()));
    }

    [Fact]
    public void EveryComparisonMeansWhatItSays() {
        var tags = Tags();
        var subject = Subject();
        subject.Attributes.SetBase(AttributeId.From("Level"), 80f);

        Assert.True(Compile(RequirementComparison.AtLeast, 80f).IsMetBy(subject));
        Assert.True(Compile(RequirementComparison.AtMost, 80f).IsMetBy(subject));
        Assert.True(Compile(RequirementComparison.Exactly, 80f).IsMetBy(subject));
        Assert.False(Compile(RequirementComparison.Not, 80f).IsMetBy(subject));

        Assert.False(Compile(RequirementComparison.AtLeast, 81f).IsMetBy(subject));
        Assert.True(Compile(RequirementComparison.AtMost, 81f).IsMetBy(subject));
        Assert.False(Compile(RequirementComparison.Exactly, 81f).IsMetBy(subject));
        Assert.True(Compile(RequirementComparison.Not, 81f).IsMetBy(subject));

        RequirementSet Compile(RequirementComparison comparison, float value) =>
            RequirementSet.Compile(
                [new() { Kind = RequirementKind.Value, Subject = "Level", Comparison = comparison, Value = value }],
                tags
            );
    }

    [Fact]
    public void ARequirementSeesTheModifiedValueAndNotTheBase() {
        var tags = Tags();
        var subject = Subject();

        subject.Attributes.Add(new(AttributeId.From("Power"), ModifierOp.Add, 200f, ModifierSource.From(new(1), 1)));

        var requirements = RequirementSet.Compile(
            [new() { Kind = RequirementKind.Value, Subject = "Power", Comparison = RequirementComparison.AtLeast, Value = 250f }],
            tags
        );

        Assert.True(requirements.IsMetBy(subject));
    }

    [Fact]
    public void AnEmptySetIsMetByAnything() {
        Assert.True(RequirementSet.Always.IsMetBy(Subject()));
        Assert.True(RequirementSet.Compile(null, Tags()).IsMetBy(Subject()));
        Assert.Equal(0, RequirementSet.Always.Count);
    }

    [Fact]
    public void ARequirementAboutATagTheContentDoesNotHaveFailsClosed() {
        var tags = Tags();
        var subject = Subject();

        var has = RequirementSet.Compile(
            [new() { Kind = RequirementKind.HasTag, Subject = "Profession.Alchemy" }],
            tags
        );

        var hasNot = RequirementSet.Compile(
            [new() { Kind = RequirementKind.NotHasTag, Subject = "Profession.Alchemy" }],
            tags
        );

        Assert.False(has.IsMetBy(subject));
        Assert.True(hasNot.IsMetBy(subject));
    }
}

public class GameplaySubjectTests {
    [Fact]
    public void EffectsExpireBeforeTheStatsTheyWereHoldingUpAreRecomputed() {
        var tags = new GameplayTagTableBuilder().Add("Effect.Buff.Might").Build();
        var subject = new GameplaySubject(new AttributeLayoutBuilder().Add("Power", 100f).Build());

        var might = new EffectDefinition {
            Address = "effects/might",
            Id = DefId.From("effects/might"),
            Duration = 1f,
            Tags = ["Effect.Buff.Might"],
            Modifiers = [new() { Attribute = "Power", Op = ModifierOp.Add, Value = 10f }]
        };

        subject.Effects.Apply(EffectTemplate.Compile(might, tags));
        subject.Attributes.Recompute();
        subject.Attributes.ClearChanges();

        subject.Tick(1f);

        Assert.True(subject.Attributes.HasChanged(AttributeId.From("Power")));
        Assert.Equal(100f, subject.Attributes.ValueOf(AttributeId.From("Power")));
    }
}
