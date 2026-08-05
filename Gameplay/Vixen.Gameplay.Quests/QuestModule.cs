// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>The quest library as a module a game composes.</summary>
/// <remarks>
///     <para>
///         Two definition types and no stats. A quest is not a thing with attributes; it is a
///         subscription, a counter and a list of what it owes.
///     </para>
///     <para>
///         ⚠ <b>It declares the verbs, and that is the load-bearing part.</b> A verb that content
///         never happens to mention is absent from the baked tag table, and an absent prefix resolves
///         to an empty range that matches nothing — so without these declarations a game whose content
///         has no <c>Event.Craft</c> anywhere would compile its crafting objective into a subscription
///         that can never fire, and nothing would say so.
///     </para>
/// </remarks>
public sealed class QuestModule : IGameplayModule {
    /// <summary>The tag every quest's completion tag sits under.</summary>
    public const string QuestRoot = "Quest";

    /// <inheritdoc />
    public string Name => "Quests";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<QuestDefinition>()
            .Definition<DynamicEventDefinition>()
            .Tag(QuestRoot);

        foreach (var verb in QuestVerbs.All) {
            builder.Tag(verb);
        }
    }
}
