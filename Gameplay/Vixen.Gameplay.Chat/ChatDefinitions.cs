// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Chat;

/// <summary>Which way a channel's traffic goes.</summary>
/// <remarks>
///     Doc 27 § Chat's table, as three values. The realm already knows who is nearby, so spatial chat
///     costs it nothing extra; a recipient who may be on another continent or offline is the gate's.
/// </remarks>
public enum ChatRoute {
    /// <summary>The realm, over the game connection. Say, yell, emote, zone.</summary>
    Realm,

    /// <summary>The realm when everybody is on it, the gate otherwise. Party and squad.</summary>
    RealmOrGate,

    /// <summary>The gate, over WSS. Guild, whisper, global, trade.</summary>
    Gate
}

/// <summary>How a channel's audience is found.</summary>
/// <remarks>
///     ⚠ <b>A <em>kind</em>, not an answer.</b> This library cannot resolve any of these — a party is
///     <c>Vixen.Gameplay.Social</c>'s and doc 28's spine forbids the reference — so the kind is what
///     an <see cref="IChatAudience" /> is asked with and the game answers.
/// </remarks>
public enum ChatAudienceKind {
    /// <summary>Everybody nearby on the same map.</summary>
    Scene,

    /// <summary>The sender's party or squad.</summary>
    Group,

    /// <summary>The sender's guild.</summary>
    Guild,

    /// <summary>One named recipient.</summary>
    Direct,

    /// <summary>Everybody, everywhere.</summary>
    Global
}

/// <summary>A chat channel: who hears it, how it gets there, and what it costs to speak.</summary>
/// <remarks>
///     <para>
///         <b>Authored, so a game adds a channel by writing a <c>.vxdef</c>.</b> A trade channel, a
///         recruitment channel, a language-specific zone channel and a role-play emote are all this
///         type with different numbers.
///     </para>
///     <para>
///         ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///         <see cref="ModifierDefinition" />.
///     </para>
/// </remarks>
[DataContract("ChatChannelDefinition")]
public sealed record ChatChannelDefinition : Definition {
    /// <summary>What a client shows it as — <c>Guild</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What somebody types — <c>/g</c>.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Which way it goes.</summary>
    public ChatRoute Route { get; set; }

    /// <summary>How its audience is found.</summary>
    public ChatAudienceKind Audience { get; set; }

    /// <summary>How far it carries, in metres. Zero for a channel that is not spatial.</summary>
    public float Radius { get; set; }

    /// <summary>The longest message it takes.</summary>
    public int MaximumLength { get; set; } = 256;

    /// <summary>How many messages one player may send on it in <see cref="RateWindow" />. Zero for no limit.</summary>
    public int RateLimit { get; set; }

    /// <summary>How long that window is, in seconds.</summary>
    public float RateWindow { get; set; } = 10f;

    /// <summary>A tag the sender must have to speak on it — <c>Guild.Permission.Speak</c>. Empty for none.</summary>
    public string Permission { get; set; } = string.Empty;

    /// <summary>What else has to be true to speak on it — a level floor against a trade channel.</summary>
    public List<RequirementDefinition> Requirements { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Permission.Length > 0) {
            tags.Add(Permission);
        }

        foreach (var requirement in Requirements) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>A channel with its names resolved.</summary>
public sealed class ChatChannel {
    internal ChatChannel(
        ChatChannelDefinition definition,
        GameplayTagRange permission,
        RequirementSet requirements
    ) {
        Definition = definition;
        Permission = permission;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public ChatChannelDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What a client shows it as.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>Which way it goes.</summary>
    public ChatRoute Route => Definition.Route;

    /// <summary>How its audience is found.</summary>
    public ChatAudienceKind Audience => Definition.Audience;

    /// <summary>How far it carries. Zero for a channel that is not spatial.</summary>
    public float Radius => MathF.Max(0f, Definition.Radius);

    /// <summary>The longest message it takes, never below one.</summary>
    public int MaximumLength => Math.Max(1, Definition.MaximumLength);

    /// <summary>How many messages one player may send in a window. Zero for no limit.</summary>
    public int RateLimit => Math.Max(0, Definition.RateLimit);

    /// <summary>How long that window is.</summary>
    public float RateWindow => MathF.Max(0.001f, Definition.RateWindow);

    /// <summary>The tag the sender must have, or an empty range for none.</summary>
    public GameplayTagRange Permission { get; }

    /// <summary>What else has to be true.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>Every chat channel a build knows, compiled once.</summary>
public sealed class ChatLibrary {
    readonly Dictionary<uint, ChatChannel> channels;
    readonly Dictionary<string, ChatChannel> byCommand;
    readonly string[] problems;

    ChatLibrary(Dictionary<uint, ChatChannel> channels, Dictionary<string, ChatChannel> byCommand, string[] problems) {
        this.channels = channels;
        this.byCommand = byCommand;
        this.problems = problems;
    }

    /// <summary>A library with no channels in it.</summary>
    public static ChatLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every channel, in address order.</summary>
    public IEnumerable<ChatChannel> Channels =>
        channels.Values.OrderBy(channel => channel.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static ChatLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var channels = new Dictionary<uint, ChatChannel>();
        var byCommand = new Dictionary<string, ChatChannel>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in catalog.OfType<ChatChannelDefinition>()) {
            if (definition.Audience == ChatAudienceKind.Scene && definition.Radius <= 0f) {
                problems.Add(
                    $"'{definition.Address}' is heard by everybody on the map and has no radius, so it is a "
                    + "zone channel rather than a spatial one — set a radius or say Global."
                );
            }

            if (definition.Audience != ChatAudienceKind.Scene && definition.Radius > 0f) {
                problems.Add(
                    $"'{definition.Address}' has a radius and an audience that is not spatial, so the radius "
                    + "does nothing."
                );
            }

            if (definition.Audience == ChatAudienceKind.Direct && definition.Route != ChatRoute.Gate) {
                problems.Add(
                    $"'{definition.Address}' is a whisper routed through the realm, so it cannot reach "
                    + "anybody on another shard — doc 27 § Chat routes a whisper over the gate."
                );
            }

            var channel = new ChatChannel(
                definition,
                definition.Permission.Length > 0 ? tags.RangeOf(definition.Permission) : GameplayTagRange.Empty,
                RequirementSet.Compile(definition.Requirements, tags)
            );

            channels.Add(definition.Id.Value, channel);

            if (definition.Command.Length > 0 && !byCommand.TryAdd(definition.Command, channel)) {
                problems.Add($"'{definition.Address}' and another channel both answer to '{definition.Command}'.");
            }
        }

        return new(channels, byCommand, [.. problems]);
    }

    /// <summary>Finds a channel.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public ChatChannel? Find(DefId id) => channels.GetValueOrDefault(id.Value);

    /// <summary>Finds a channel by what somebody typed.</summary>
    /// <param name="command">The command, with its slash.</param>
    /// <returns>It, or null.</returns>
    public ChatChannel? FindCommand(string? command) =>
        command is null ? null : byCommand.GetValueOrDefault(command);
}
