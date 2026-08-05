// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Chat;

/// <summary>The chat library as a module a game composes.</summary>
/// <remarks>
///     One definition type and no stats. It declares no tags of its own: a channel's permission is
///     somebody else's tag — a guild's, a subscription's, a moderator rank's — and inventing a
///     <c>Chat.Permission</c> root here would give a game two vocabularies for the same question.
/// </remarks>
public sealed class ChatModule : IGameplayModule {
    /// <inheritdoc />
    public string Name => "Chat";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<ChatChannelDefinition>();
    }
}
