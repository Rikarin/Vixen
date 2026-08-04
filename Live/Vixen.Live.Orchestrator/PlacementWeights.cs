// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;

namespace Vixen.Live.Orchestration;

/// <summary>What "together" means to a game, as numbers. Doc 27 § Placement.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A <c>.vxplacement</c> asset a game authors, not constants the engine ships.</b> The
///         defaults below are Guild Wars 2's shape and are a starting point rather than an answer:
///         a battleground wants fill to dominate, a social hub wants locale to, and neither is
///         something an engine can know. What the engine owns is that the terms exist and that the
///         scoring is total and deterministic whatever they are set to.
///     </para>
///     <para>
///         <b>Why the party term is four times everything else put together.</b> It is not a
///         preference, it is the mechanism: "join your friend's instance" is doc 27's own list of
///         Guild Wars 2 properties, and it falls out of placement rather than being a feature beside
///         it. A party pull that could lose to a full guild would be a separate join mechanism
///         waiting to be written.
///     </para>
/// </remarks>
[DataContract]
public sealed record PlacementWeights {
    /// <summary>The file extension a game authors these in.</summary>
    public const string Extension = ".vxplacement";

    /// <summary>Doc 27's defaults, which are Guild Wars 2's shape.</summary>
    public static PlacementWeights Default { get; } = new();

    /// <summary>A party or squad member is present. Effectively a hard pull.</summary>
    public double Party { get; init; } = 10_000;

    /// <summary>Per guild member present, up to <see cref="GuildCap" />.</summary>
    public double GuildMember { get; init; } = 400;

    /// <summary>
    ///     How many guild members count. Capped so that a guild event cannot outrank a party.
    /// </summary>
    /// <remarks>
    ///     Doc 27 says "per member, capped" and does not say where. Five, because five times the
    ///     default guild weight is a fifth of the party weight — so a guild pulls hard and a party
    ///     still wins, which is the ordering the two terms exist to express.
    /// </remarks>
    public int GuildCap { get; init; } = 5;

    /// <summary>Per friend present, up to <see cref="FriendCap" />.</summary>
    public double Friend { get; init; } = 200;

    /// <summary>How many friends count.</summary>
    public int FriendCap { get; init; } = 5;

    /// <summary>The shard is speaking the requester's language.</summary>
    public double Locale { get; init; } = 300;

    /// <summary>The shard's fill is in the healthy band.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes consolidation possible.</b> Placement <em>prefers</em> filling a
    ///     shard that is already half full over an empty one, so a map that is emptying converges on
    ///     a few busy shards rather than a lot of lonely ones — which is the input the merge rule
    ///     then acts on. A score that spread players evenly would make merging impossible and every
    ///     map feel dead.
    /// </remarks>
    public double HealthyFill { get; init; } = 250;

    /// <summary>Where the healthy band starts, as a percentage of the soft cap.</summary>
    public double HealthyFrom { get; init; } = 40;

    /// <summary>Where it ends.</summary>
    public double HealthyTo { get; init; } = 80;

    /// <summary>Penalty per percentage point above <see cref="HealthyTo" />.</summary>
    /// <remarks>
    ///     Falls away steeply on purpose, so the last fifth of a shard is reserved for the people who
    ///     have a reason to be on it — a party arriving together, a guild event — rather than being
    ///     spent on whoever zoned in next.
    /// </remarks>
    public double Overfull { get; init; } = 40;

    /// <summary>Penalty for a shard past <see cref="MaxAge" />.</summary>
    public double Aged { get; init; } = -100;

    /// <summary>How old a shard has to be to be biased against.</summary>
    /// <remarks>
    ///     What makes a rolling upgrade finish rather than asymptote: an old shard that nobody is
    ///     sent to empties, and an empty shard stops.
    /// </remarks>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Penalty for the shard the requester was just moved off.</summary>
    public double AntiFlap { get; init; } = -5_000;

    /// <summary>Reads a <c>.vxplacement</c>.</summary>
    /// <param name="yaml">The document.</param>
    /// <returns>The weights, with anything unnamed left at its default.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="yaml" /> is null.</exception>
    /// <exception cref="YamlParseException">It is not YAML.</exception>
    /// <exception cref="YamlBindingException">It is YAML that is not this.</exception>
    /// <remarks>
    ///     Boot-time configuration, which is what <c>YamlSerializer</c> is for — every member read
    ///     boxes, and one allocation per property once per fleet is invisible.
    /// </remarks>
    public static PlacementWeights Parse(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        return YamlSerializer.Parse<PlacementWeights>(yaml);
    }

    /// <summary>Writes them back, which is what an editor's inspector saves.</summary>
    /// <returns>The document.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(this);
}
