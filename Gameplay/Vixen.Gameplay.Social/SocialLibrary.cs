// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Social;

/// <summary>An authored rank ladder, compiled.</summary>
public sealed class GuildCharter {
    readonly GuildRank[] ranks;

    internal GuildCharter(GuildCharterDefinition definition, GameplayTag tag, GuildRank[] ranks) {
        Definition = definition;
        Tag = tag;
        this.ranks = ranks;
    }

    /// <summary>The one a guild gets when nothing was authored: a leader and a member.</summary>
    public static GuildCharter Default { get; } = new(
        new(),
        GameplayTag.None,
        [new GuildRank("Leader", 0, []), new GuildRank("Member", 1, [])]
    );

    /// <summary>What it was compiled from.</summary>
    public GuildCharterDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What being in a guild is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>How many members a guild may have, never below one.</summary>
    public int MaximumMembers => Math.Max(1, Definition.MaximumMembers);

    /// <summary>The ladder, highest first.</summary>
    public ReadOnlySpan<GuildRank> Ranks => ranks;
}

/// <summary>Every social definition a build knows, compiled once.</summary>
public sealed class SocialLibrary {
    readonly Dictionary<uint, GroupPolicy> policies;
    readonly Dictionary<uint, GuildCharter> charters;
    readonly string[] problems;

    SocialLibrary(
        Dictionary<uint, GroupPolicy> policies,
        Dictionary<uint, GuildCharter> charters,
        string[] problems
    ) {
        this.policies = policies;
        this.charters = charters;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static SocialLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every group policy, in address order.</summary>
    public IEnumerable<GroupPolicy> Policies =>
        policies.Values.OrderBy(policy => policy.Definition.Address, StringComparer.Ordinal);

    /// <summary>Every guild charter, in address order.</summary>
    public IEnumerable<GuildCharter> Charters =>
        charters.Values.OrderBy(charter => charter.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static SocialLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var policies = new Dictionary<uint, GroupPolicy>();

        foreach (var definition in catalog.OfType<GroupPolicyDefinition>()) {
            if (definition.SubgroupSize > definition.MaximumMembers) {
                problems.Add(
                    $"'{definition.Address}' has subgroups of {definition.SubgroupSize} in a group of "
                    + $"{definition.MaximumMembers}, so the first subgroup is the whole group."
                );
            }

            foreach (var role in definition.Roles) {
                if (!tags.RangeOf(role).IsSome) {
                    problems.Add($"'{definition.Address}' offers the role '{role}', which is not a tag in this build.");
                }
            }

            policies.Add(
                definition.Id.Value,
                new(definition, tags.Resolve(definition.Tag), [.. definition.Roles.Select(tags.Resolve)])
            );
        }

        var charters = new Dictionary<uint, GuildCharter>();

        foreach (var definition in catalog.OfType<GuildCharterDefinition>()) {
            if (definition.Ranks.Count == 0) {
                problems.Add(
                    $"'{definition.Address}' has no ranks, so every guild founded on it gets the default "
                    + "leader-and-member ladder."
                );
            }

            var ranks = new GuildRank[definition.Ranks.Count];

            for (var order = 0; order < ranks.Length; order++) {
                var rank = definition.Ranks[order];

                foreach (var permission in rank.Permissions) {
                    if (!tags.RangeOf(permission).IsSome) {
                        problems.Add(
                            $"'{definition.Address}' rank '{rank.DisplayName}' carries '{permission}', which "
                            + "is not a tag in this build — so it grants nothing."
                        );
                    }
                }

                ranks[order] = new(
                    rank.DisplayName.Length > 0 ? rank.DisplayName : $"Rank {order}",
                    order,
                    rank.Permissions.Select(tags.Resolve)
                );
            }

            charters.Add(
                definition.Id.Value,
                new(definition, tags.Resolve(definition.Tag), ranks.Length > 0 ? ranks : [new GuildRank("Leader", 0, []), new GuildRank("Member", 1, [])])
            );
        }

        return new(policies, charters, [.. problems]);
    }

    /// <summary>Finds a group policy.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public GroupPolicy? FindPolicy(DefId id) => policies.GetValueOrDefault(id.Value);

    /// <summary>Finds a guild charter.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public GuildCharter? FindCharter(DefId id) => charters.GetValueOrDefault(id.Value);
}

/// <summary>Where the durable half of social state is kept.</summary>
/// <remarks>
///     ⚠ <b>An interface for the reason <c>IPityStore</c> is one:</b> a guild roster and a friends
///     list are a grain's, doc 28's spine says <c>Gameplay/</c> may not reference <c>Live/</c>, and
///     doc 27 hands <c>IGuildGrain</c> to this document rather than building it. So the realm fills
///     this and a test fills it with <see cref="MemorySocialStore" />.
/// </remarks>
public interface ISocialStore {
    /// <summary>Loads a guild.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>It, or null.</returns>
    Guild? LoadGuild(GuildId id);

    /// <summary>Writes a guild back.</summary>
    /// <param name="guild">The guild.</param>
    void SaveGuild(Guild guild);

    /// <summary>Which guild somebody is in.</summary>
    /// <param name="player">Who.</param>
    /// <returns>It, or <see cref="GuildId.None" />.</returns>
    GuildId GuildOf(PlayerId player);

    /// <summary>Loads somebody's friends and blocks.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their graph, or null.</returns>
    SocialGraph? LoadGraph(PlayerId player);

    /// <summary>Writes a graph back.</summary>
    /// <param name="graph">The graph.</param>
    void SaveGraph(SocialGraph graph);
}

/// <summary>An <see cref="ISocialStore" /> that keeps it all in memory. For tests and single-process games.</summary>
public sealed class MemorySocialStore : ISocialStore {
    readonly Dictionary<GuildId, Guild> guilds = [];
    readonly Dictionary<PlayerId, SocialGraph> graphs = [];

    /// <summary>How many guilds it holds.</summary>
    public int Guilds => guilds.Count;

    /// <inheritdoc />
    public Guild? LoadGuild(GuildId id) => guilds.GetValueOrDefault(id);

    /// <inheritdoc />
    public void SaveGuild(Guild guild) {
        ArgumentNullException.ThrowIfNull(guild);

        guilds[guild.Id] = guild;
    }

    /// <inheritdoc />
    public GuildId GuildOf(PlayerId player) {
        // Linear, and that is fine for the shape this exists for: a realm's store answers it from an
        // index and a test's store has a dozen guilds in it.
        foreach (var guild in guilds.Values) {
            if (guild.RankOf(player) >= 0) {
                return guild.Id;
            }
        }

        return GuildId.None;
    }

    /// <inheritdoc />
    public SocialGraph? LoadGraph(PlayerId player) => graphs.GetValueOrDefault(player);

    /// <inheritdoc />
    public void SaveGraph(SocialGraph graph) {
        ArgumentNullException.ThrowIfNull(graph);

        graphs[graph.Owner] = graph;
    }
}
