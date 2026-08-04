// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Gate;

/// <summary>What this gate is, and what it will do.</summary>
public sealed class GateOptions {
    /// <summary>The fleet's target build and content hash. ADR-022's filter, from the client's side.</summary>
    /// <remarks>
    ///     ⚠ <b>The gate refuses a mismatched client before asking the cluster anything.</b> Placement
    ///     filters on the same pair, so a mismatch would come back <c>Refused</c> anyway — but
    ///     "refused" and "fetch the update" are different conversations, and only the gate knows
    ///     enough to have the second one.
    /// </remarks>
    public RealmVersion Version { get; set; }

    /// <summary>Where the catalog is published, for the addressable update.</summary>
    public string Content { get; set; } = "";

    /// <summary>The latency zone this gate places into.</summary>
    /// <remarks>Doc 27 M-Q5's opaque string. Every game has regions and none of them mean the same.</remarks>
    public string Region { get; set; } = "";

    /// <summary>Which maps a client may ask for. Empty means any.</summary>
    /// <remarks>
    ///     ⚠ <b>A closed list is the difference between a typo and a spawned shard.</b> A map address
    ///     arrives from a client, and <c>IMapGrain</c> is keyed by it — so an unfiltered gate lets
    ///     anybody create a fleet for <c>maps/../../etc</c> and watch the orchestrator try to start
    ///     it. Empty is offered for a single-map game and is a decision rather than a default.
    /// </remarks>
    public IList<string> Maps { get; } = [];

    /// <summary>How long a session token lasts.</summary>
    /// <remarks>Hours, not weeks: the token is stateless, so its lifetime is the whole of its bound.</remarks>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>How long a minted <see cref="TransferTicket" /> stays good.</summary>
    /// <remarks>
    ///     Long enough to load a map over a bad connection, short enough that a stolen one is worth
    ///     little. The lease epoch is the real bound; this is the cruder second one.
    /// </remarks>
    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>What to tell a client to wait when a shard is coming up.</summary>
    public TimeSpan StartingRetry { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many characters an account may have.</summary>
    public int CharactersPerAccount { get; set; } = 8;

    /// <summary>How often the service-plane socket says something, so a proxy does not close it.</summary>
    public TimeSpan StreamKeepAlive { get; set; } = TimeSpan.FromSeconds(30);
}
