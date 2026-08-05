// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Ai;
using Vixen.Gameplay.Combat;
using Vixen.Gameplay.Loot;

namespace Vixen.Samples.Mmo.Rules;

/// <summary>The game's own module: what this MMO adds to the twenty it takes.</summary>
/// <remarks>
///     <para>
///         <b>The sample had no module of its own until it needed one</b>, which was a gap worth
///         noticing: doc 28's whole extension story is that a game's module and the engine's are the
///         same kind of object, and a sample that only ever composed engine modules never showed it.
///     </para>
///     <para>
///         ⚠ <b>A definition type needs declaring here or its <c>!Tag</c> names nothing.</b> That is
///         the same rule the twenty libraries live under, seen from the other side: the alias is
///         registered by a module initializer in the assembly that declares the type, and the module
///         is what makes the alias part of <em>this game's</em> vocabulary rather than an orphan.
///     </para>
///     <para>
///         ⚠ <b>It declares its dependencies</b>, and <c>Build</c> refuses a composition that took
///         this and not those — a creature's abilities are Combat's, its drops are Loot's and its
///         camp is Ai's, so a game with creatures and no combat is a composition that compiles and
///         is wrong.
///     </para>
/// </remarks>
public sealed class MmoModule : IGameplayModule {
    /// <summary>What a creature that has aggroed is.</summary>
    public const string InCombat = "State.InCombat";

    /// <summary>What a creature that is worth more than usual is.</summary>
    public const string Elite = "Creature.Elite";

    /// <inheritdoc />
    public string Name => "Mmo";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .DependsOn<CombatModule>()
            .DependsOn<LootModule>()
            .DependsOn<GameplayAiModule>()
            .Definition<CreatureDefinition>()

            // ⚠ Declared because only *code* asks about them. A tag a definition mentions reaches the
            // table on its own; these two are asked about by the realm and named by nothing, so
            // without this every query for them resolves to an empty range and matches nothing.
            .Tag(InCombat)
            .Tag(Elite);
    }
}
