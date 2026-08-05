// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Social;

/// <summary>The social library as a module a game composes.</summary>
/// <remarks>
///     Two definition types and no stats. What it declares beyond them is the three tag roots the
///     rest of the framework asks about — a group tag, a guild permission, a role — because a rule in
///     C# that names a tag content never mentions resolves to an empty range and quietly matches
///     nothing.
/// </remarks>
public sealed class SocialModule : IGameplayModule {
    /// <summary>The tag every group's tag sits under.</summary>
    public const string GroupRoot = "Group";

    /// <summary>The tag every guild permission sits under.</summary>
    public const string PermissionRoot = "Guild.Permission";

    /// <summary>The tag every group role sits under.</summary>
    public const string RoleRoot = "Role";

    /// <summary>Inviting somebody to a guild.</summary>
    public const string Invite = "Guild.Permission.Invite";

    /// <summary>Removing somebody from a guild.</summary>
    public const string Kick = "Guild.Permission.Kick";

    /// <summary>Moving somebody up or down the ladder.</summary>
    public const string Rank = "Guild.Permission.Rank";

    /// <summary>Taking things out of the guild bank.</summary>
    public const string Withdraw = "Guild.Permission.Withdraw";

    /// <summary>Speaking on the guild channel.</summary>
    public const string Speak = "Guild.Permission.Speak";

    /// <inheritdoc />
    public string Name => "Social";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<GroupPolicyDefinition>()
            .Definition<GuildCharterDefinition>()
            .Tag(GroupRoot)
            .Tag(RoleRoot)
            .Tag(PermissionRoot)
            .Tag(Invite)
            .Tag(Kick)
            .Tag(Rank)
            .Tag(Withdraw)
            .Tag(Speak);
    }
}
