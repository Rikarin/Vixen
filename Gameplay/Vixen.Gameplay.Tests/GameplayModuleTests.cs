// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Xunit;

namespace Vixen.Gameplay.Tests;

[DataContract("TestQuestDefinition")]
public sealed record TestQuestDefinition : Definition;

public sealed class NothingSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

public sealed class CombatTestModule : IGameplayModule {
    public string Name => "Combat";

    public void Configure(GameplayModuleBuilder builder) =>
        builder
            .Attribute("Power", 100f)
            .Attribute("Health", 1000f, 0f)
            .Tag("State.InCombat")
            .System(SystemPhase.Update, static () => new NothingSystem());
}

public sealed class QuestTestModule : IGameplayModule {
    public int MaximumActive { get; set; } = 25;

    public string Name => "Quests";

    public void Configure(GameplayModuleBuilder builder) =>
        builder
            .DependsOn<CombatTestModule>()
            .Definition<TestQuestDefinition>()
            .Tag("Quest.Objective.Kill");
}

public sealed class ClashingAttributeModule : IGameplayModule {
    public string Name => "Clashing";

    public void Configure(GameplayModuleBuilder builder) => builder.Attribute("Power", 5f);
}

public sealed record UndescribedDefinition : Definition;

public sealed class UndescribedDefinitionModule : IGameplayModule {
    public string Name => "Undescribed";

    public void Configure(GameplayModuleBuilder builder) => builder.Definition<UndescribedDefinition>();
}

public sealed class SecondQuestModule : IGameplayModule {
    public string Name => "SecondQuests";

    public void Configure(GameplayModuleBuilder builder) => builder.Definition<TestQuestDefinition>();
}

public class GameplayConfigTests {
    [Fact]
    public void AGameComposesTheModulesItChoseAndNothingElse() {
        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<CombatTestModule>()
            .Use<QuestTestModule>()
            .Build();

        Assert.Equal(3, composition.Modules.Count);
        Assert.Equal(["Gameplay", "Combat", "Quests"], composition.Modules.Select(module => module.Name));
        Assert.Equal(2, composition.Attributes.Count);
        Assert.Contains("State.InCombat", composition.Tags);
        Assert.Contains("Quest.Objective.Kill", composition.Tags);
        Assert.Single(composition.Systems);
        Assert.Equal(SystemPhase.Update, composition.Systems[0].Phase);
        Assert.Equal("Combat", composition.Systems[0].Module);
    }

    [Fact]
    public void ASystemIsMadeByItsRegistrationAndNotByTheComposition() {
        var composition = new GameplayConfig().Use<CombatTestModule>().Build();

        using var system = composition.Systems[0].Create();

        Assert.IsType<NothingSystem>(system);
    }

    [Fact]
    public void AModuleCanBeConfiguredAsItIsUsed() {
        QuestTestModule? configured = null;

        new GameplayConfig()
            .Use<CombatTestModule>()
            .Use<QuestTestModule>(
                module => {
                    module.MaximumActive = 5;
                    configured = module;
                }
            )
            .Build();

        Assert.Equal(5, configured!.MaximumActive);
    }

    [Fact]
    public void AMissingDependencyIsNamedRatherThanPulledIn() {
        var config = new GameplayConfig().Use<QuestTestModule>();

        var error = Assert.Throws<InvalidOperationException>(config.Build);

        Assert.Contains("CombatTestModule", error.Message, StringComparison.Ordinal);
        Assert.Contains("Quests", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameModuleTwiceIsRefused() {
        var config = new GameplayConfig().Use<CombatTestModule>();

        Assert.Throws<InvalidOperationException>(() => config.Use<CombatTestModule>());
    }

    [Fact]
    public void TwoModulesDeclaringTheSameStatAreRefusedByName() {
        var config = new GameplayConfig().Use<CombatTestModule>();

        var error = Assert.Throws<InvalidOperationException>(() => config.Use<ClashingAttributeModule>());

        Assert.Contains("Combat", error.Message, StringComparison.Ordinal);
        Assert.Contains("Clashing", error.Message, StringComparison.Ordinal);
        Assert.Contains("Power", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoModulesClaimingOneDefinitionTypeAreRefusedByName() {
        var config = new GameplayConfig().Use<CombatTestModule>().Use<QuestTestModule>();

        var error = Assert.Throws<InvalidOperationException>(() => config.Use<SecondQuestModule>());

        Assert.Contains("TestQuestDefinition", error.Message, StringComparison.Ordinal);
        Assert.Contains("Quests", error.Message, StringComparison.Ordinal);
        Assert.Contains("SecondQuests", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefinitionTypeWithNoDescriptorCannotBeDeclared() {
        // No [DataContract], so no !Tag resolves to it and no .vxdef can name it. The composition is
        // where that is caught, because it is the only place that knows the type was meant to be
        // authorable at all.
        var config = new GameplayConfig();

        var error = Assert.Throws<InvalidOperationException>(() => config.Use<UndescribedDefinitionModule>());

        Assert.Contains("[DataContract]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnySubsetComposes() {
        // docs/plan/28 § Testing: "a game composing an arbitrary subset links, boots and trims; every
        // library's absence is survivable by every other". The three modules here stand in for the
        // twenty libraries; what is asserted is that nothing is implicitly required except what a
        // module said it required.
        Assert.Empty(new GameplayConfig().Build().Modules);
        Assert.Single(new GameplayConfig().Use<GameplayKernelModule>().Build().Modules);
        Assert.Single(new GameplayConfig().Use<CombatTestModule>().Build().Modules);
        Assert.Equal(2, new GameplayConfig().Use<CombatTestModule>().Use<QuestTestModule>().Build().Modules.Count);
    }

    [Fact]
    public void TheKernelArrivesThroughTheSameSeamAGamesOwnModuleDoes() {
        var composition = new GameplayConfig().Use<GameplayKernelModule>().Build();

        var effects = Assert.Single(composition.Definitions);

        Assert.Equal("Gameplay", effects.Module);
        Assert.Equal(typeof(EffectDefinition), effects.Type);
        Assert.Equal("EffectDefinition", effects.Tag);
    }

    [Fact]
    public void EveryTagAModuleDeclaresReachesTheTagTable() {
        var composition = new GameplayConfig().Use<CombatTestModule>().Use<QuestTestModule>().Build();

        var builder = new DefinitionCatalogBuilder();

        foreach (var tag in composition.Tags) {
            builder.AddTag(tag);
        }

        var catalog = builder.Build();

        Assert.True(catalog.Tags.TryResolve("State.InCombat", out _));
        Assert.True(catalog.Tags.TryResolve("Quest.Objective.Kill", out _));
        Assert.True(catalog.Tags.TryResolve("Quest.Objective", out _));
    }
}
