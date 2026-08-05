// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Ai;
using Vixen.Gameplay.Chat;
using Vixen.Gameplay.Collections;
using Vixen.Gameplay.Combat;
using Vixen.Gameplay.Crafting;
using Vixen.Gameplay.Economy;
using Vixen.Gameplay.Exploration;
using Vixen.Gameplay.Housing;
using Vixen.Gameplay.Instances;
using Vixen.Gameplay.Interaction;
using Vixen.Gameplay.Inventory;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;
using Vixen.Gameplay.Movement;
using Vixen.Gameplay.Progression;
using Vixen.Gameplay.Pvp;
using Vixen.Gameplay.Quests;
using Vixen.Gameplay.Shooting;
using Vixen.Gameplay.Social;
using Vixen.Gameplay.Travel;

namespace Vixen.Samples.Mmo.Content.Tests;

/// <summary>Every one of doc 28's libraries, composed. The sample takes all of them on purpose.</summary>
/// <remarks>
///     <para>
///         <b>This is what makes the content readable at all, and finding that out was worth the
///         detour.</b> A definition's <c>!Tag</c> is resolved through <c>SerializerRegistry</c>, which
///         is filled by a module initializer in each library's own assembly — and a module
///         initializer runs when the assembly <em>loads</em>. A test project that merely
///         <c>ProjectReference</c>s twenty libraries and never touches a type from nineteen of them
///         gets nineteen assemblies the runtime never loaded, and every file fails to import with
///         <em>"nothing in this build claims the name"</em> about a type that is right there in the
///         build output.
///     </para>
///     <para>
///         ⚠ <b>The fix is not to touch a type per assembly — it is to declare the composition</b>,
///         which is the thing a game has to do anyway. <c>Use&lt;TModule&gt;</c> has a
///         <c>new()</c> constraint, so the constructor call is emitted at this call site and the
///         assembly is a hard compile-time dependency rather than something a trimmer can decide
///         nobody used.
///     </para>
///     <para>
///         ⚠ <b>Taking every module is a statement about the sample and not a default.</b> Doc 28
///         ships twenty-odd packages precisely so an extraction shooter does not carry a threat
///         table; this one is a full MMO and says so in one place.
///     </para>
/// </remarks>
public static class MmoModules {
    /// <summary>Composes them.</summary>
    /// <returns>The composition.</returns>
    public static GameplayComposition Compose() =>
        new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<ItemsModule>()
            .Use<InventoryModule>()
            .Use<LootModule>()
            .Use<CombatModule>()
            .Use<ShootingModule>()
            .Use<ProgressionModule>()
            .Use<QuestModule>()
            .Use<SocialModule>()
            .Use<ChatModule>()
            .Use<EconomyModule>()
            .Use<InstanceModule>()
            .Use<PvpModule>()
            .Use<InteractionModule>()
            .Use<CraftingModule>()
            .Use<ExplorationModule>()
            .Use<TravelModule>()
            .Use<MovementModule>()
            .Use<GameplayAiModule>()
            .Use<HousingModule>()
            .Use<CollectionsModule>()
            .Build();
}
