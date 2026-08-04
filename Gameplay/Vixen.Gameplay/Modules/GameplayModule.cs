// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs.Systems;

namespace Vixen.Gameplay;

/// <summary>A unit of gameplay a game chooses to have: items, or combat, or its own guild ranks.</summary>
/// <remarks>
///     <para>
///         <b>The engine's modules and a game's own are the same kind of object</b> — doc 28
///         § The <c>IGameplayModule</c> seam, which is doc 16's <c>NetworkModule</c> discipline one
///         level up: build the built-ins out of the primitive users get, so that the extension point
///         is the one the engine itself uses and therefore the one that works.
///     </para>
///     <para>
///         <b>Nothing is implicit and nothing is scanned.</b> A module is reached because a game
///         wrote <see cref="GameplayConfig.Use{TModule}()" />, which is a constructor call the
///         compiler can see — an assembly scan reads metadata a trimmed publish has already deleted,
///         and produces a game that works in development and ships with no quests.
///     </para>
/// </remarks>
public interface IGameplayModule {
    /// <summary>What it is called, in a diagnostic and in the composition report.</summary>
    string Name { get; }

    /// <summary>Declares what it brings.</summary>
    /// <param name="builder">What to declare it to.</param>
    void Configure(GameplayModuleBuilder builder);
}

/// <summary>A system a module contributes, and when it runs.</summary>
/// <param name="Module">Which module asked for it.</param>
/// <param name="Phase">Which phase of the frame it belongs to.</param>
/// <param name="Create">How to make one.</param>
public readonly record struct GameplaySystemRegistration(string Module, SystemPhase Phase, Func<ISystem> Create);

/// <summary>A definition type a module brings, and how a file names it.</summary>
/// <param name="Module">Which module asked for it.</param>
/// <param name="Type">The type.</param>
/// <param name="Tag">Its <c>[DataContract]</c> alias — the <c>!Tag</c> a <c>.vxdef</c> writes.</param>
public readonly record struct DefinitionRegistration(string Module, Type Type, string Tag);

/// <summary>What one module declares while it is being configured.</summary>
/// <remarks>
///     Handed to <see cref="IGameplayModule.Configure" /> and never held afterwards. Everything it
///     collects lands in the <see cref="GameplayComposition" /> stamped with the module that asked
///     for it, so a duplicate attribute or a clashing definition tag can be reported as a
///     disagreement between two named modules rather than as an anonymous conflict.
/// </remarks>
public sealed class GameplayModuleBuilder {
    readonly GameplayConfig config;

    internal GameplayModuleBuilder(GameplayConfig config, string module) {
        this.config = config;
        Module = module;
    }

    /// <summary>Which module is being configured.</summary>
    public string Module { get; }

    /// <summary>Declares a stat.</summary>
    /// <param name="name">Its name — <c>Power</c>.</param>
    /// <param name="default">What a fresh set starts at.</param>
    /// <param name="minimum">The floor.</param>
    /// <param name="maximum">The ceiling.</param>
    /// <param name="rounding">What happens after the clamp.</param>
    /// <returns>The builder, so declarations chain.</returns>
    public GameplayModuleBuilder Attribute(
        string name,
        float @default = 0f,
        float minimum = float.NegativeInfinity,
        float maximum = float.PositiveInfinity,
        AttributeRounding rounding = AttributeRounding.None
    ) {
        config.DeclareAttribute(Module, name, @default, minimum, maximum, rounding);

        return this;
    }

    /// <summary>Declares a tag the module's own code needs, whether or not content mentions it.</summary>
    /// <param name="name">The dotted name — <c>State.InCombat</c>.</param>
    /// <returns>The builder, so declarations chain.</returns>
    /// <remarks>
    ///     Content is where most tags come from. This is for the ones a rule in C# asks about: without
    ///     it the tag is absent from the baked table, so the rule resolves to an empty range and
    ///     quietly matches nothing.
    /// </remarks>
    public GameplayModuleBuilder Tag(string name) {
        config.DeclareTag(Module, name);

        return this;
    }

    /// <summary>Declares a definition type the module can read out of a <c>.vxdef</c>.</summary>
    /// <typeparam name="TDefinition">The type.</typeparam>
    /// <returns>The builder, so declarations chain.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What this adds over the type simply existing is the composition report.</b> "Which
    ///         <c>!Tag</c>s can this game's content use" is otherwise a question answered by reading
    ///         the reference graph, and the editor's <c>.vxdef</c> completion has nowhere to get its
    ///         list from.
    ///     </para>
    ///     <para>
    ///         ⚠ It also refuses a type two modules both claim. It does <em>not</em> check that two
    ///         types have distinct aliases — <c>TypeRegistry.Register</c> already refuses that from a
    ///         module initializer, which is earlier and harder than anything a composition could do.
    ///     </para>
    /// </remarks>
    public GameplayModuleBuilder Definition<TDefinition>() where TDefinition : Definition {
        config.DeclareDefinition<TDefinition>(Module);

        return this;
    }

    /// <summary>Declares a system the module runs.</summary>
    /// <param name="phase">Which phase of the frame.</param>
    /// <param name="create">How to make one.</param>
    /// <returns>The builder, so declarations chain.</returns>
    public GameplayModuleBuilder System(SystemPhase phase, Func<ISystem> create) {
        config.DeclareSystem(Module, phase, create);

        return this;
    }

    /// <summary>Declares that this module does not work without another.</summary>
    /// <typeparam name="TModule">The other module.</typeparam>
    /// <returns>The builder, so declarations chain.</returns>
    /// <remarks>
    ///     Checked at <see cref="GameplayConfig.Build" /> and never satisfied automatically. A missing
    ///     dependency is a decision a game has to make — doc 28's whole shape is that every library
    ///     is declinable — so the composition names what is missing and stops, rather than pulling in
    ///     an inventory system somebody did not ask for.
    /// </remarks>
    public GameplayModuleBuilder DependsOn<TModule>() where TModule : IGameplayModule {
        config.DeclareDependency(Module, typeof(TModule));

        return this;
    }
}
