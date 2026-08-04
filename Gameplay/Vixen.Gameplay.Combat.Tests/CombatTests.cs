// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Combat.Tests;

/// <summary>A fireball, a channel, an instant, a silence and a shield.</summary>
public static class Content {
    public const string Fireball = "abilities/fireball";
    public const string Drain = "abilities/drain";
    public const string Strike = "abilities/strike";
    public const string Heal = "abilities/heal";
    public const string Silence = "effects/silence";
    public const string Burning = "effects/burning";
    public const string Might = "effects/might";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add(
                Silence,
                new EffectDefinition {
                    Duration = 4f,
                    Tags = ["Effect.Control.Silence"],
                    BlockedTags = ["Ability.Cast"]
                }
            )
            .Add(
                Burning,
                new EffectDefinition { Duration = 6f, Period = 2f, Tags = ["Effect.Damage.Burning"] }
            )
            .Add(
                Might,
                new EffectDefinition {
                    Duration = 10f,
                    Tags = ["Effect.Buff.Might"],
                    Modifiers = [new() { Attribute = "Power", Op = ModifierOp.AddPercent, Value = 0.2f }]
                }
            )
            .Add(
                Fireball,
                new AbilityDefinition {
                    DisplayName = "Fireball",
                    Targeting = AbilityTargeting.Target,
                    Range = 30f,
                    CastTime = 2f,
                    Cooldown = 6f,
                    Costs = [new() { Attribute = "Mana", Amount = 50f }],
                    Damage = new() {
                        School = "Damage.Fire",
                        Amount = 100f,
                        ScalesWith = "Power",
                        Coefficient = 1f,
                        ThreatMultiplier = 1f
                    },
                    AppliesToTarget = [Burning],
                    Tags = ["Ability.Cast.Fireball"]
                }
            )
            .Add(
                Drain,
                new AbilityDefinition {
                    DisplayName = "Drain",
                    Targeting = AbilityTargeting.Target,
                    Range = 20f,
                    ChannelTime = 6f,
                    ChannelPeriod = 2f,
                    Costs = [new() { Attribute = "Mana", Amount = 10f }],
                    Damage = new() { School = "Damage.Shadow", Amount = 30f },
                    Tags = ["Ability.Cast.Drain"]
                }
            )
            .Add(
                Strike,
                new AbilityDefinition {
                    DisplayName = "Strike",
                    Targeting = AbilityTargeting.Target,
                    Range = 5f,
                    Charges = 2,
                    Cooldown = 8f,
                    Damage = new() { School = "Damage.Physical", Amount = 50f, ThreatMultiplier = 2f },
                    AppliesToSelf = [Might],
                    Tags = ["Ability.Melee.Strike"]
                }
            )
            .Add(
                Heal,
                new AbilityDefinition {
                    DisplayName = "Heal",
                    Targeting = AbilityTargeting.Target,
                    Range = 30f,
                    Damage = new() { Amount = 200f, IsHealing = true, ThreatMultiplier = 0.5f },
                    Tags = ["Ability.Cast.Heal"]
                }
            )
            .Build();

    public static AbilityLibrary Abilities() => AbilityLibrary.Compile(Catalog());

    public static AttributeLayout Layout() =>
        new AttributeLayoutBuilder()
            .Add("Health", 1000f, 0f)
            .Add("MaximumHealth", 1000f, 0f)
            .Add("Power", 100f)
            .Add("Mana", 500f, 0f)
            .Add("CritChance", 0f, 0f, 1f)
            .Add("CritMultiplier", 2f, 1f)
            .Add("Absorb", 0f, 0f)
            .Add("ResistFire", 0f, 0f, 1f)
            .Build();

    public static CombatAttributes Attributes() =>
        CombatAttributes.Default.WithResistances(Catalog().Tags, ("Damage.Fire", "ResistFire"));

    public static GameplaySubject Subject() => new(Layout());

    public static CombatResolver Resolver() {
        var attributes = Attributes();

        return new(DamagePipeline.Standard(attributes), attributes);
    }
}

public class AbilityLibraryTests {
    [Fact]
    public void ACleanCatalogCompilesWithNothingToReport() {
        Assert.Empty(Content.Abilities().Problems);
        Assert.Equal(4, Content.Abilities().Count);
    }

    [Fact]
    public void AnAbilityThatCastsAndChannelsIsReportedAndTheCastWins() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "abilities/confused",
                new AbilityDefinition { CastTime = 2f, ChannelTime = 4f, Targeting = AbilityTargeting.Self }
            )
            .Build();

        var abilities = AbilityLibrary.Compile(catalog);
        var ability = abilities.Get(DefId.From("abilities/confused"));

        Assert.Contains(abilities.Problems, problem => problem.Contains("casts and then channels", StringComparison.Ordinal));
        Assert.False(ability.IsChannel);
        Assert.Equal(2f, ability.CastTime);
    }

    [Fact]
    public void AnEffectNamedByTwoAbilitiesIsCompiledOnce() {
        // Two templates would be two stacking identities for one buff.
        var catalog = new DefinitionCatalogBuilder()
            .Add(Content.Might, new EffectDefinition { Duration = 10f, Tags = ["Effect.Buff.Might"] })
            .Add("abilities/a", new AbilityDefinition { Targeting = AbilityTargeting.Self, AppliesToSelf = [Content.Might] })
            .Add("abilities/b", new AbilityDefinition { Targeting = AbilityTargeting.Self, AppliesToSelf = [Content.Might] })
            .Build();

        var abilities = AbilityLibrary.Compile(catalog);

        Assert.Same(
            abilities.Get(DefId.From("abilities/a")).AppliesToSelf[0],
            abilities.Get(DefId.From("abilities/b")).AppliesToSelf[0]
        );
    }

    [Fact]
    public void AnEffectOrSchoolThisBuildDoesNotHaveIsReported() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "abilities/odd",
                new AbilityDefinition {
                    Targeting = AbilityTargeting.Self,
                    AppliesToTarget = ["effects/nonexistent"],
                    Damage = new() { School = "Damage.Nonexistent", Amount = 1f }
                }
            )
            .Build();

        // ⚠ One, not two, and the reason is worth the comment: the school *is* in the tag table,
        // because the ability's own CollectTags put it there. A school that resolves to nothing is
        // therefore only reachable for one a rule in C# invents, which is what the check is for.
        Assert.Contains(
            AbilityLibrary.Compile(catalog).Problems,
            problem => problem.Contains("effects/nonexistent", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void DamageScalesWithTheCastersStat() {
        var abilities = Content.Abilities();
        var fireball = abilities.Get(DefId.From(Content.Fireball));
        var caster = Content.Subject();

        // 100 base plus 100 power at a coefficient of one.
        Assert.Equal(200f, fireball.BaseAmount(caster));

        caster.Attributes.SetBase(AttributeId.From("Power"), 300f);
        Assert.Equal(400f, fireball.BaseAmount(caster));

        // And the world has no stats, so it does base damage.
        Assert.Equal(100f, fireball.BaseAmount(null));
    }
}

public class DamagePipelineTests {
    [Fact]
    public void TheStagesRunInDocumentedOrder() {
        var order = new List<DamageStage>();
        var pipeline = new DamagePipeline();

        foreach (var stage in Enum.GetValues<DamageStage>().Reverse()) {
            pipeline.Add(new Recorder(stage, order));
        }

        var target = Content.Subject();
        var hit = new DamageEvent { Target = target };

        pipeline.Run(ref hit);

        Assert.Equal(
            [
                DamageStage.Compute,
                DamageStage.Crit,
                DamageStage.Mitigate,
                DamageStage.Absorb,
                DamageStage.Apply,
                DamageStage.React
            ],
            order
        );
    }

    [Fact]
    public void OrderDecidesWithinAStageRatherThanRegistrationOrder() {
        var order = new List<int>();

        var pipeline = new DamagePipeline()
            .Add(new Numbered(DamageStage.Mitigate, 10, order))
            .Add(new Numbered(DamageStage.Mitigate, -5, order))
            .Add(new Numbered(DamageStage.Mitigate, 0, order));

        var hit = new DamageEvent { Target = Content.Subject() };
        pipeline.Run(ref hit);

        Assert.Equal([-5, 0, 10], order);
    }

    [Fact]
    public void ACancelledHitStopsAfterItsOwnStage() {
        var reached = new List<DamageStage>();

        var pipeline = new DamagePipeline()
            .Add(new Canceller())
            .Add(new Recorder(DamageStage.Mitigate, reached))
            .Add(new Recorder(DamageStage.Apply, reached));

        var hit = new DamageEvent { Target = Content.Subject() };
        pipeline.Run(ref hit);

        // The peer rule in Mitigate still ran; nothing after the stage did.
        Assert.True(hit.Cancelled);
        Assert.Equal([DamageStage.Mitigate], reached);
    }

    [Fact]
    public void TheShippedPipelineIsSixOrdinaryRules() {
        // docs/plan/28 G-R1: the built-ins are written *through* the seam, so the extension point is
        // the one the engine itself uses.
        var pipeline = DamagePipeline.Standard();

        foreach (var stage in Enum.GetValues<DamageStage>()) {
            Assert.Single(pipeline.Rules(stage).ToArray());
        }
    }

    [Fact]
    public void AGamesRuleCanRunBeforeAShippedOne() {
        var pipeline = DamagePipeline.Standard(Content.Attributes()).Add(new Halver());
        var resolver = new CombatResolver(pipeline, Content.Attributes());
        var target = Content.Subject();

        var hit = resolver.Strike(
            Content.Abilities().Get(DefId.From(Content.Fireball)),
            Content.Subject(),
            target,
            eventId: 1
        );

        // 200 base, halved before the shipped mitigate rule sees it.
        Assert.Equal(100f, hit.Applied);
    }

    sealed class Recorder(DamageStage stage, List<DamageStage> into) : IDamageRule {
        public DamageStage Stage => stage;

        public int Order => 0;

        public void Apply(ref DamageEvent hit) => into.Add(stage);
    }

    sealed class Numbered(DamageStage stage, int order, List<int> into) : IDamageRule {
        public DamageStage Stage => stage;

        public int Order => order;

        public void Apply(ref DamageEvent hit) => into.Add(order);
    }

    sealed class Canceller : IDamageRule {
        public DamageStage Stage => DamageStage.Mitigate;

        public int Order => -100;

        public void Apply(ref DamageEvent hit) => hit.Cancelled = true;
    }

    sealed class Halver : IDamageRule {
        public DamageStage Stage => DamageStage.Mitigate;

        public int Order => -1;

        public void Apply(ref DamageEvent hit) => hit.Amount *= 0.5f;
    }
}

public class DamageRuleTests {
    static DamageEvent Strike(GameplaySubject caster, GameplaySubject target, string ability = Content.Fireball) =>
        Content.Resolver().Strike(Content.Abilities().Get(DefId.From(ability)), caster, target, eventId: 1);

    [Fact]
    public void HealthComesOffAsABaseValueRatherThanAModifier() {
        var target = Content.Subject();
        var hit = Strike(Content.Subject(), target);

        Assert.Equal(200f, hit.Applied);
        Assert.Equal(800f, target.Attributes.ValueOf(AttributeId.From("Health")));

        // Nothing a buff expiring could put back: a wound is not removed by source.
        Assert.Empty(target.Attributes.Modifiers);
    }

    [Fact]
    public void ResistanceTakesItsFractionOffAndIsCapped() {
        var target = Content.Subject();
        target.Attributes.SetBase(AttributeId.From("ResistFire"), 0.25f);

        var hit = Strike(Content.Subject(), target);

        Assert.Equal(50f, hit.Mitigated, 3);
        Assert.Equal(150f, hit.Applied, 3);

        var tough = Content.Subject();
        tough.Attributes.SetBase(AttributeId.From("ResistFire"), 1f);

        Assert.Equal(200f * ResistanceRule.Cap, Strike(Content.Subject(), tough).Mitigated, 3);
    }

    [Fact]
    public void AShieldSoaksWhatMitigationLeftAndIsSpent() {
        var target = Content.Subject();
        target.Attributes.SetBase(AttributeId.From("ResistFire"), 0.5f);
        target.Attributes.SetBase(AttributeId.From("Absorb"), 60f);

        var hit = Strike(Content.Subject(), target);

        // 200, mitigated to 100, of which the shield eats 60.
        Assert.Equal(100f, hit.Mitigated, 3);
        Assert.Equal(60f, hit.Absorbed, 3);
        Assert.Equal(40f, hit.Applied, 3);
        Assert.Equal(0f, target.Attributes.ValueOf(AttributeId.From("Absorb")));
    }

    [Fact]
    public void ACritMultipliesAndTheRollHappensWhetherOrNotItCan() {
        var lucky = Content.Subject();
        lucky.Attributes.SetBase(AttributeId.From("CritChance"), 1f);

        var target = Content.Subject();
        var hit = Strike(lucky, target);

        Assert.True(hit.IsCritical);
        Assert.Equal(400f, hit.Applied);

        // The unlucky caster's later rolls must land in the same places, which is what "the roll
        // happens whether or not the caster can crit" buys.
        var unlucky = Content.Subject();
        var missed = Strike(unlucky, Content.Subject());

        Assert.False(missed.IsCritical);
        Assert.Equal(200f, missed.Applied);
    }

    [Fact]
    public void HealingSkipsMitigationAndAbsorptionAndRespectsTheCeiling() {
        var target = Content.Subject();
        target.Attributes.SetBase(AttributeId.From("ResistFire"), 0.5f);
        target.Attributes.SetBase(AttributeId.From("Absorb"), 500f);
        target.Attributes.SetBase(AttributeId.From("Health"), 900f);

        var hit = Strike(Content.Subject(), target, Content.Heal);

        Assert.Equal(0f, hit.Mitigated);
        Assert.Equal(0f, hit.Absorbed);
        Assert.Equal(100f, hit.Applied);
        Assert.Equal(1000f, target.Attributes.ValueOf(AttributeId.From("Health")));
        Assert.Equal(500f, target.Attributes.ValueOf(AttributeId.From("Absorb")));
    }

    [Fact]
    public void AKillIsReportedOnceAndNotAgain() {
        var target = Content.Subject();
        target.Attributes.SetBase(AttributeId.From("Health"), 150f);

        Assert.True(Strike(Content.Subject(), target).Killed);
        Assert.False(Strike(Content.Subject(), target).Killed);
    }

    [Fact]
    public void ThreatIsWhatLandedTimesTheMultiplier() {
        var target = Content.Subject();

        Assert.Equal(200f, Strike(Content.Subject(), target).Threat);

        var melee = Content.Subject();
        Assert.Equal(100f, Strike(melee, Content.Subject(), Content.Strike).Threat);
    }

    [Fact]
    public void AHitIsReproducibleFromItsEventId() {
        var resolver = Content.Resolver();
        var fireball = Content.Abilities().Get(DefId.From(Content.Fireball));
        var caster = Content.Subject();
        caster.Attributes.SetBase(AttributeId.From("CritChance"), 0.3f);

        for (var eventId = 1ul; eventId < 200; eventId++) {
            var first = resolver.Strike(fireball, caster, Content.Subject(), eventId);
            var second = resolver.Strike(fireball, caster, Content.Subject(), eventId);

            Assert.Equal(first.IsCritical, second.IsCritical);
            Assert.Equal(first.Applied, second.Applied);
        }
    }

    [Fact]
    public void EveryTargetOfOneAbilityRollsItsOwnCrit() {
        var resolver = Content.Resolver();
        var fireball = Content.Abilities().Get(DefId.From(Content.Fireball));
        var caster = Content.Subject();
        caster.Attributes.SetBase(AttributeId.From("CritChance"), 0.5f);

        var crits = 0;

        for (var index = 0ul; index < 40; index++) {
            if (resolver.Strike(fireball, caster, Content.Subject(), 7, index).IsCritical) {
                crits++;
            }
        }

        // Forty targets of one cleave must not all crit or all miss together.
        Assert.InRange(crits, 5, 35);
    }
}

public class AbilityCasterTests {
    static AbilityCaster Caster(out GameplaySubject subject) {
        subject = Content.Subject();

        return new(subject, Content.Abilities());
    }

    static AbilityTarget At(float distance = 10f) => new(Content.Subject(), 1, distance);

    [Fact]
    public void AnInstantAbilityStartsAndCompletesInOneCall() {
        var caster = Caster(out _);
        var events = new List<AbilityEvent>();

        Assert.Equal(AbilityFailure.None, caster.TryBegin(DefId.From(Content.Strike), At(3f), events));

        Assert.Equal(
            [AbilityEventKind.Started, AbilityEventKind.Completed],
            events.Select(entry => entry.Kind)
        );

        Assert.False(caster.IsCasting);
    }

    [Fact]
    public void ACastCompletesWhenItsTimeIsUpAndPaysThen() {
        var caster = Caster(out var subject);
        var events = new List<AbilityEvent>();

        caster.TryBegin(DefId.From(Content.Fireball), At(), events);

        Assert.True(caster.IsCasting);
        Assert.Equal(500f, subject.Attributes.ValueOf(AttributeId.From("Mana")));

        caster.Tick(1f, events);
        Assert.True(caster.IsCasting);
        Assert.Equal(500f, subject.Attributes.ValueOf(AttributeId.From("Mana")));

        caster.Tick(1f, events);
        Assert.False(caster.IsCasting);
        Assert.Equal(450f, subject.Attributes.ValueOf(AttributeId.From("Mana")));
        Assert.Contains(events, entry => entry.Kind == AbilityEventKind.Completed);
    }

    [Fact]
    public void AnInterruptedCastPaysNothing() {
        var caster = Caster(out var subject);
        var events = new List<AbilityEvent>();

        caster.TryBegin(DefId.From(Content.Fireball), At(), events);
        caster.Tick(1.9f, events);

        Assert.True(caster.Interrupt(events));

        Assert.Equal(500f, subject.Attributes.ValueOf(AttributeId.From("Mana")));
        Assert.Contains(events, entry => entry.Kind == AbilityEventKind.Interrupted);
        Assert.False(caster.Interrupt());
    }

    [Fact]
    public void AChannelTicksExactlyAsOftenAsItsDurationBuysAndPaysPerTick() {
        var caster = Caster(out var subject);
        var events = new List<AbilityEvent>();

        caster.TryBegin(DefId.From(Content.Drain), At(), events);

        // A frame time nothing divides evenly.
        for (var frame = 0; frame < 400 && caster.IsCasting; frame++) {
            caster.Tick(1f / 60f, events);
        }

        Assert.Equal(3, events.Count(entry => entry.Kind == AbilityEventKind.Ticked));
        Assert.Contains(events, entry => entry.Kind == AbilityEventKind.Completed);
        Assert.Equal(470f, subject.Attributes.ValueOf(AttributeId.From("Mana")));
    }

    [Fact]
    public void AChannelThatRunsOutOfResourcesStopsRatherThanGoingIntoDebt() {
        var caster = Caster(out var subject);
        subject.Attributes.SetBase(AttributeId.From("Mana"), 15f);

        var events = new List<AbilityEvent>();
        caster.TryBegin(DefId.From(Content.Drain), At(), events);

        for (var frame = 0; frame < 400 && caster.IsCasting; frame++) {
            caster.Tick(1f / 60f, events);
        }

        Assert.Equal(1, events.Count(entry => entry.Kind == AbilityEventKind.Ticked));
        Assert.Contains(events, entry => entry.Kind == AbilityEventKind.Interrupted);
        Assert.Equal(5f, subject.Attributes.ValueOf(AttributeId.From("Mana")));
    }

    [Fact]
    public void ChargesBankAndRechargeOneAtATime() {
        var caster = Caster(out _);
        var strike = DefId.From(Content.Strike);

        Assert.Equal(2, caster.ChargesOf(strike));

        caster.TryBegin(strike, At(3f));
        Assert.Equal(1, caster.ChargesOf(strike));

        caster.Tick(2f);
        caster.TryBegin(strike, At(3f));
        Assert.Equal(0, caster.ChargesOf(strike));
        Assert.Equal(AbilityFailure.OnCooldown, caster.TryBegin(strike, At(3f)));

        // Six more seconds finishes the first charge's eight.
        caster.Tick(6f);
        Assert.Equal(1, caster.ChargesOf(strike));

        caster.Tick(8f);
        Assert.Equal(2, caster.ChargesOf(strike));
        Assert.Equal(0f, caster.CooldownOf(strike));
    }

    [Fact]
    public void TheGlobalCooldownStopsTheNextAbilityAndSomeAbilitiesIgnoreIt() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "abilities/kick",
                new AbilityDefinition {
                    Targeting = AbilityTargeting.Target,
                    Range = 5f,
                    TriggersGlobalCooldown = false,
                    RespectsGlobalCooldown = false,
                    Tags = ["Ability.Melee.Kick"]
                }
            )
            .Add(
                "abilities/slash",
                new AbilityDefinition { Targeting = AbilityTargeting.Target, Range = 5f, Tags = ["Ability.Melee.Slash"] }
            )
            .Build();

        var caster = new AbilityCaster(Content.Subject(), AbilityLibrary.Compile(catalog));

        Assert.Equal(AbilityFailure.None, caster.TryBegin(DefId.From("abilities/slash"), At(3f)));
        Assert.Equal(AbilityFailure.GlobalCooldown, caster.TryBegin(DefId.From("abilities/slash"), At(3f)));
        Assert.Equal(AbilityFailure.None, caster.TryBegin(DefId.From("abilities/kick"), At(3f)));

        caster.Tick(1.5f);
        Assert.Equal(AbilityFailure.None, caster.TryBegin(DefId.From("abilities/slash"), At(3f)));
    }

    [Fact]
    public void ASilenceStopsAnAbilityStartingAndEndsOneAlreadyCasting() {
        var caster = Caster(out var subject);
        var silence = EffectTemplate.Compile(
            (EffectDefinition)Content.Catalog().Find(DefId.From(Content.Silence))!,
            Content.Catalog().Tags
        );

        var events = new List<AbilityEvent>();

        caster.TryBegin(DefId.From(Content.Fireball), At(), events);
        caster.Tick(1f, events);

        subject.Effects.Apply(silence);
        caster.Tick(0.1f, events);

        Assert.False(caster.IsCasting);
        Assert.Contains(events, entry => entry.Kind == AbilityEventKind.Interrupted);
        Assert.Equal(AbilityFailure.Blocked, caster.TryBegin(DefId.From(Content.Fireball), At()));

        // And the melee ability is not an Ability.Cast, so once the global cooldown the fireball
        // started has run out it still works.
        caster.Tick(1.5f, events);
        Assert.Equal(AbilityFailure.None, caster.TryBegin(DefId.From(Content.Strike), At(3f)));
    }

    [Fact]
    public void EveryRefusalSaysWhichOneItIs() {
        var caster = Caster(out var subject);

        Assert.Equal(AbilityFailure.Unknown, caster.TryBegin(DefId.From("abilities/nonexistent"), At()));
        Assert.Equal(AbilityFailure.NoTarget, caster.TryBegin(DefId.From(Content.Fireball), AbilityTarget.None));
        Assert.Equal(AbilityFailure.OutOfRange, caster.TryBegin(DefId.From(Content.Fireball), At(100f)));

        subject.Attributes.SetBase(AttributeId.From("Mana"), 10f);
        Assert.Equal(AbilityFailure.Resources, caster.TryBegin(DefId.From(Content.Fireball), At()));

        subject.Attributes.SetBase(AttributeId.From("Mana"), 500f);
        caster.TryBegin(DefId.From(Content.Fireball), At());
        Assert.Equal(AbilityFailure.Casting, caster.TryBegin(DefId.From(Content.Strike), At(3f)));
    }

    [Fact]
    public void CanBeginIsTheSameCheckTryBeginRuns() {
        // The greyed-out button and the rejected request agree, which is the whole point of exposing
        // the check separately.
        var caster = Caster(out var subject);
        subject.Attributes.SetBase(AttributeId.From("Mana"), 10f);

        var fireball = DefId.From(Content.Fireball);

        Assert.Equal(caster.CanBegin(fireball, At()), caster.TryBegin(fireball, At()));
    }

    [Fact]
    public void ResettingPutsEveryChargeBack() {
        var caster = Caster(out _);
        var strike = DefId.From(Content.Strike);

        caster.TryBegin(strike, At(3f));
        caster.ResetCooldowns();

        Assert.Equal(2, caster.ChargesOf(strike));
        Assert.Equal(0f, caster.GlobalCooldownRemaining);
    }
}

public class CombatResolverTests {
    [Fact]
    public void AnAbilityAppliesItsEffectsToBothEnds() {
        var resolver = Content.Resolver();
        var abilities = Content.Abilities();
        var caster = Content.Subject();
        var target = Content.Subject();

        resolver.Resolve(
            abilities.Get(DefId.From(Content.Strike)),
            caster,
            [new(target, 2, 3f)],
            eventId: 1
        );

        // Might on the caster, and the strike's damage on the target.
        Assert.Equal(1, caster.Effects.Count);
        Assert.Equal(120f, caster.Attributes.ValueOf(AttributeId.From("Power")), 3);
        Assert.Equal(950f, target.Attributes.ValueOf(AttributeId.From("Health")));
    }

    [Fact]
    public void TheCastersOwnBuffIsUpBeforeItsDamageIsComputed() {
        // An ability that reads "gain 20 % power, then strike" has to strike with the 20 %.
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                Content.Might,
                new EffectDefinition {
                    Duration = 10f,
                    Tags = ["Effect.Buff.Might"],
                    Modifiers = [new() { Attribute = "Power", Op = ModifierOp.AddPercent, Value = 1f }]
                }
            )
            .Add(
                "abilities/empower",
                new AbilityDefinition {
                    Targeting = AbilityTargeting.Target,
                    Range = 5f,
                    AppliesToSelf = [Content.Might],
                    Damage = new() { Amount = 0f, ScalesWith = "Power", Coefficient = 1f }
                }
            )
            .Build();

        var abilities = AbilityLibrary.Compile(catalog);
        var resolver = new CombatResolver(DamagePipeline.Standard());
        var caster = Content.Subject();
        var target = Content.Subject();
        var hits = new List<AbilityHit>();

        resolver.Resolve(abilities.Get(DefId.From("abilities/empower")), caster, [new(target, 1, 1f)], 1, hits);

        Assert.Equal(200f, Assert.Single(hits).Amount);
    }

    [Fact]
    public void EveryTargetGetsItsOwnHit() {
        var resolver = Content.Resolver();
        var abilities = Content.Abilities();
        var targets = new List<AbilityTarget>();

        for (var index = 0ul; index < 5; index++) {
            targets.Add(new(Content.Subject(), index + 1, 3f));
        }

        var hits = new List<AbilityHit>();
        resolver.Resolve(abilities.Get(DefId.From(Content.Strike)), Content.Subject(), targets, 1, hits);

        Assert.Equal(5, hits.Count);
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul], hits.Select(hit => hit.Target));
        Assert.All(hits, hit => Assert.Equal(50f, hit.Amount));
    }

    [Fact]
    public void AnAbilityWithNoDamageOnlyAppliesItsEffects() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(Content.Might, new EffectDefinition { Duration = 10f, Tags = ["Effect.Buff.Might"] })
            .Add(
                "abilities/bless",
                new AbilityDefinition { Targeting = AbilityTargeting.Target, Range = 5f, AppliesToTarget = [Content.Might] }
            )
            .Build();

        var resolver = new CombatResolver(DamagePipeline.Standard());
        var target = Content.Subject();

        resolver.Resolve(
            AbilityLibrary.Compile(catalog).Get(DefId.From("abilities/bless")),
            Content.Subject(),
            [new(target, 1, 1f)],
            1
        );

        Assert.Equal(1, target.Effects.Count);
        Assert.Equal(1000f, target.Attributes.ValueOf(AttributeId.From("Health")));
    }
}

public class ThreatTableTests {
    [Fact]
    public void TheHighestThreatIsTheTarget() {
        var table = new ThreatTable();

        table.Add(1, 100f);
        table.Add(2, 250f);
        table.Add(3, 50f);

        Assert.Equal(2ul, table.Target());
        Assert.Equal(3, table.Count);
        Assert.Equal(250f, table.ThreatOf(2));
    }

    [Fact]
    public void TwoAttackersOnEqualThreatDoNotSwapTheBossBackAndForth() {
        var table = new ThreatTable();

        table.Add(7, 100f);
        table.Add(3, 100f);

        var first = table.Target();

        table.Add(7, 0f);
        table.Add(3, 0f);

        Assert.Equal(first, table.Target());
        Assert.Equal(3ul, first);
    }

    [Fact]
    public void ATauntHoldsTheTargetThroughAnyAmountOfDamage() {
        var table = new ThreatTable();

        table.Add(1, 1000f);
        table.Taunt(2, 3f);

        Assert.True(table.IsTaunted);
        Assert.Equal(2ul, table.Target());

        // A large threat number would lose here, which is the bug every homegrown table ships with.
        table.Add(1, 100000f);
        Assert.Equal(2ul, table.Target());

        Assert.True(table.Tick(3f));
        Assert.False(table.IsTaunted);
        Assert.Equal(1ul, table.Target());
    }

    [Fact]
    public void ATauntLiftsTheTaunterToTheTopSoTheBossIsNotHandedStraightBack() {
        var table = new ThreatTable();

        table.Add(1, 500f);
        table.Taunt(2, 1f);
        table.Tick(1f);

        Assert.Equal(500f, table.ThreatOf(2));
    }

    [Fact]
    public void AThreatDropIsAMultiply() {
        var table = new ThreatTable();

        table.Add(1, 1000f);
        table.Add(2, 800f);
        table.Multiply(1, 0.5f);

        Assert.Equal(500f, table.ThreatOf(1));
        Assert.Equal(2ul, table.Target());
    }

    [Fact]
    public void ThreatNeverGoesBelowZero() {
        var table = new ThreatTable();

        table.Add(1, 100f);
        table.Add(1, -500f);

        Assert.Equal(0f, table.ThreatOf(1));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void RemovingTheTaunterEndsTheTaunt() {
        var table = new ThreatTable();

        table.Add(1, 100f);
        table.Taunt(2, 10f);

        Assert.True(table.Remove(2));
        Assert.False(table.IsTaunted);
        Assert.Equal(1ul, table.Target());
    }

    [Fact]
    public void AnEmptyTableHasNoTarget() {
        var table = new ThreatTable();

        Assert.Equal(0ul, table.Target());

        table.Add(1, 100f);
        table.Clear();

        Assert.Equal(0ul, table.Target());
        Assert.Equal(0, table.Count);
    }
}

public class CombatModuleTests {
    [Fact]
    public void TheModuleDeclaresTheStatsTheShippedRulesRead() {
        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<CombatModule>()
            .Build();

        foreach (var name in new[] { "Health", "MaximumHealth", "CritChance", "CritMultiplier", "Absorb" }) {
            Assert.True(composition.Attributes.Declares(AttributeId.From(name)), name);
        }

        Assert.Contains(CombatModule.AbilityRoot, composition.Tags);
        Assert.Contains(CombatModule.DeadTag, composition.Tags);
        Assert.Contains(composition.Definitions, entry => entry.Tag == "AbilityDefinition");
    }

    [Fact]
    public void AGameCanChangeTheStartingNumbersWithoutRedeclaringTheStats() {
        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<CombatModule>(module => module.BaseHealth = 250f)
            .Build();

        var subject = new GameplaySubject(composition.Attributes);

        Assert.Equal(250f, subject.Attributes.ValueOf(AttributeId.From("Health")));
    }
}
