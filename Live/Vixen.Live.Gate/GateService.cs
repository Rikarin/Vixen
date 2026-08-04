// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vixen.Live.Cluster;
using Vixen.Live.Persistence;

namespace Vixen.Live.Gate;

/// <summary>An answer, and the status code it should be sent with.</summary>
/// <typeparam name="T">What the answer carries when there is one.</typeparam>
/// <param name="Status">The HTTP status.</param>
/// <param name="Value">The answer, when the status says there is one.</param>
/// <param name="Problem">What was wrong, otherwise.</param>
public readonly record struct GateAnswer<T>(int Status, T? Value, GateProblem? Problem) {
    /// <summary>Whether there is a value.</summary>
    public bool Ok => Problem is null;

    /// <summary>An answer.</summary>
    /// <param name="value">It.</param>
    /// <param name="status">The status. 200 unless the caller says otherwise.</param>
    /// <returns>The answer.</returns>
    public static GateAnswer<T> Yes(T value, int status = 200) => new(status, value, null);

    /// <summary>A refusal.</summary>
    /// <param name="status">The status.</param>
    /// <param name="code">The stable token.</param>
    /// <param name="detail">The sentence.</param>
    /// <returns>The answer.</returns>
    public static GateAnswer<T> No(int status, string code, string detail) =>
        new(status, default, new(code, detail));
}

/// <summary>
///     Everything the gate decides, with no ASP.NET in it. Doc 27 § The three planes' service plane.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same shape the grains took: a plain class a test constructs and drives, with a
///         thin adapter over it.</b> <c>GateEndpoints</c> maps routes onto these methods and does
///         nothing else — it reads a header, calls one method, writes the answer. Everything worth
///         asserting about a gate is here, so asserting it needs neither a web host nor a cluster.
///     </para>
///     <para>
///         The order of checks in <see cref="PlayAsync" /> is load-bearing and is the reason this is
///         one method rather than a pipeline of filters. See its remarks.
///     </para>
/// </remarks>
public sealed class GateService {
    readonly GateOptions options;
    readonly IPersistence store;
    readonly IFleetDirectory fleet;
    readonly TransferTicketSigner tickets;
    readonly GateTokenSigner sessions;
    readonly IReadOnlyDictionary<string, IAccountAuthority> authorities;
    readonly TimeProvider clock;
    readonly ILogger log;

    /// <summary>Builds one.</summary>
    /// <param name="options">What this gate is.</param>
    /// <param name="store">Accounts and characters.</param>
    /// <param name="fleet">The control plane.</param>
    /// <param name="tickets">The cluster key, for minting admissions.</param>
    /// <param name="sessions">The gate key, for minting sessions.</param>
    /// <param name="authorities">
    ///     Who may say who somebody is. ⚠ <b>An empty set refuses every sign-in</b>, which is the
    ///     loud failure a deployment that forgot to configure one should get.
    /// </param>
    /// <param name="clock">The gate's clock, so a test does not wait.</param>
    /// <param name="log">Where the placement explanations go.</param>
    public GateService(
        GateOptions options,
        IPersistence store,
        IFleetDirectory fleet,
        TransferTicketSigner tickets,
        GateTokenSigner sessions,
        IEnumerable<IAccountAuthority> authorities,
        TimeProvider? clock = null,
        ILogger<GateService>? log = null
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorities);

        this.options = options;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        this.tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.clock = clock ?? TimeProvider.System;
        this.log = log ?? NullLogger<GateService>.Instance;

        this.authorities = authorities.ToDictionary(
            authority => authority.Scheme,
            StringComparer.OrdinalIgnoreCase
        );
    }

    /// <summary>What this fleet is running. The call a client makes before any other.</summary>
    /// <returns>The version, where the catalog is, and which maps exist.</returns>
    public CatalogResponse Catalog() => new(options.Version, options.Content, [.. options.Maps]);

    /// <summary>Turns a credential into a session.</summary>
    /// <param name="request">Which authority, and what it wants.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>A token, or why not.</returns>
    public async Task<GateAnswer<SignInResponse>> SignInAsync(
        SignInRequest request,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(request);

        if (!authorities.TryGetValue(request.Scheme ?? "", out var authority)) {
            // Naming the schemes that do exist, because the alternative is a developer guessing
            // whether the gate is misconfigured or their spelling is wrong.
            return GateAnswer<SignInResponse>.No(
                400,
                "unknown-scheme",
                authorities.Count == 0
                    ? "This gate has no sign-in authority configured, so nobody can sign in."
                    : $"No authority answers for `{request.Scheme}`. This gate knows {string.Join(", ", authorities.Keys)}."
            );
        }

        var decision = await authority.AuthenticateAsync(request.Credential ?? "", cancellation)
            .ConfigureAwait(false);

        if (!decision.Ok) {
            return GateAnswer<SignInResponse>.No(401, "unauthenticated", decision.Detail);
        }

        var now = clock.GetUtcNow();
        var (account, _) = await store.Accounts.EnsureAsync(decision.Handle, now, cancellation).ConfigureAwait(false);

        if (account.Suspended) {
            return GateAnswer<SignInResponse>.No(403, "suspended", "This account is suspended.");
        }

        var expires = now + options.SessionLifetime;

        log.GateSignedIn(account.Id, authority.Scheme);

        return GateAnswer<SignInResponse>.Yes(
            new(sessions.Encode(new(account.Id, expires)), account.Id, expires)
        );
    }

    /// <summary>Reads a bearer token.</summary>
    /// <param name="presented">What the client sent, with or without a <c>Bearer </c> prefix.</param>
    /// <param name="token">The session, when valid.</param>
    /// <returns>Whether to believe it.</returns>
    public TokenStatus Authenticate(string? presented, out GateToken? token) =>
        sessions.TryDecode(
            presented is not null && presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? presented[7..].Trim()
                : presented?.Trim(),
            clock.GetUtcNow(),
            out token
        );

    /// <summary>An account's characters.</summary>
    /// <param name="session">Whose.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The list, oldest first.</returns>
    public async Task<GateAnswer<CharacterList>> CharactersAsync(GateToken session, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(session);

        var rows = await store.Players.ForAccountAsync(session.Account, cancellation).ConfigureAwait(false);

        return GateAnswer<CharacterList>.Yes(new([.. rows.Select(Summarise)]));
    }

    /// <summary>Makes a character.</summary>
    /// <param name="session">Whose account.</param>
    /// <param name="request">What to call it and where to start.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The character, or why not.</returns>
    public async Task<GateAnswer<CharacterSummary>> CreateCharacterAsync(
        GateToken session,
        CreateCharacterRequest request,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name)) {
            return GateAnswer<CharacterSummary>.No(400, "name-required", "A character needs a name.");
        }

        var map = string.IsNullOrEmpty(request.Map) ? FirstMap() : request.Map;

        if (!Allowed(map)) {
            return GateAnswer<CharacterSummary>.No(400, "unknown-map", $"There is no map `{request.Map}` here.");
        }

        var existing = await store.Players.ForAccountAsync(session.Account, cancellation).ConfigureAwait(false);

        if (existing.Count >= options.CharactersPerAccount) {
            return GateAnswer<CharacterSummary>.No(
                409,
                "too-many-characters",
                $"This account already has {existing.Count} characters, and the limit is {options.CharactersPerAccount}."
            );
        }

        var now = clock.GetUtcNow();

        // The fence starts at 1 rather than 0, so that "never written" and "written under the first
        // lease" are different numbers. A zero start would make the first realm's write and a
        // completely unknown character indistinguishable in the repository.
        var record = new PlayerRecord(
            new(session.Account, Guid.NewGuid()),
            request.Name.Trim(),
            now,
            now,
            string.IsNullOrEmpty(request.Region) ? options.Region : request.Region,
            map,
            1,
            ReadOnlyMemory<byte>.Empty
        );

        return await store.Players.CreateAsync(record, cancellation).ConfigureAwait(false) switch {
            WriteOutcome.Written => GateAnswer<CharacterSummary>.Yes(Summarise(record), 201),
            WriteOutcome.Taken => GateAnswer<CharacterSummary>.No(
                409,
                "name-taken",
                $"`{record.Name}` is already somebody."
            ),
            var other => GateAnswer<CharacterSummary>.No(500, "not-created", other.ToString())
        };
    }

    /// <summary>Places a character, and mints the ticket that gets them in.</summary>
    /// <param name="session">Whose account.</param>
    /// <param name="request">Which character, which map, and what they are running.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Where to go, or why nowhere.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The order of the checks is the design.</b> Content version first, because
    ///         "fetch the update" is a different conversation from "no" and only the gate can have
    ///         it (ADR-022); ownership before existence, so that probing character ids tells a
    ///         stranger nothing; suspension before placement, so a banned account never costs the
    ///         cluster a grain call; and the lease epoch last, because it is the only step that
    ///         changes anything the client can observe twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A ticket is minted only for <see cref="PlaceStatus.Placed" />.</b> A shard that
    ///         is still starting has no endpoint, and a ticket naming one that does not answer is a
    ///         client retrying against a socket instead of asking the gate again.
    ///     </para>
    /// </remarks>
    public async Task<GateAnswer<PlayResponse>> PlayAsync(
        GateToken session,
        PlayRequest request,
        CancellationToken cancellation
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        if (options.Version.IsValid && !options.Version.Admits(request.Version)) {
            return GateAnswer<PlayResponse>.Yes(
                Nowhere(
                    PlayStatus.UpdateRequired,
                    $"This fleet is running {options.Version} and you have {request.Version}. Fetch the catalog from {options.Content}."
                )
            );
        }

        if (!Allowed(request.Map)) {
            return GateAnswer<PlayResponse>.Yes(Nowhere(PlayStatus.Refused, $"There is no map `{request.Map}` here."));
        }

        var key = new PlayerKey(session.Account, request.Character);
        var character = await store.Players.ReadAsync(key, cancellation).ConfigureAwait(false);

        if (character is null) {
            return GateAnswer<PlayResponse>.No(404, "no-such-character", "That is not one of your characters.");
        }

        var account = await store.Accounts.ReadAsync(session.Account, cancellation).ConfigureAwait(false);

        if (account is null || account.Suspended) {
            return GateAnswer<PlayResponse>.No(403, "suspended", "This account is suspended.");
        }

        var shardKey = new ShardKey(request.Map, character.Region, options.Version);

        var placement = await fleet
            .PlaceAsync(
                new(key, shardKey, request.Party, request.Guild, request.Locale ?? "", ShardId.None),
                cancellation
            )
            .ConfigureAwait(false);

        if (placement.Status != PlaceStatus.Placed) {
            var told = placement.Status == PlaceStatus.Starting ? PlayStatus.Starting : PlayStatus.Refused;

            log.GateNotPlaced(key, told, placement.Reason);

            return GateAnswer<PlayResponse>.Yes(Nowhere(told, placement.Reason));
        }

        var epoch = await fleet.NextLeaseEpochAsync(key, cancellation).ConfigureAwait(false);

        var ticket = tickets.Sign(
            new() {
                Player = key,
                Target = placement.Shard,
                Endpoint = placement.Endpoint,
                LeaseEpoch = epoch,
                Expires = clock.GetUtcNow() + options.TicketLifetime
            }
        );

        log.GatePlaced(key, placement.Shard, placement.Reason);

        return GateAnswer<PlayResponse>.Yes(
            new(
                PlayStatus.Placed,
                placement.Endpoint.ToString(),
                ticket.Encode(),
                placement.Shard.Value.ToString("D", CultureInfo.InvariantCulture),
                placement.Reason,
                TimeSpan.Zero
            )
        );
    }

    PlayResponse Nowhere(PlayStatus status, string reason) =>
        new(status, "", "", "", reason, status == PlayStatus.Starting ? options.StartingRetry : TimeSpan.Zero);

    bool Allowed(string? map) =>
        !string.IsNullOrEmpty(map)
        && (options.Maps.Count == 0 || options.Maps.Contains(map, StringComparer.Ordinal));

    string FirstMap() => options.Maps.Count > 0 ? options.Maps[0] : "";

    static CharacterSummary Summarise(PlayerRecord record) =>
        new(record.Key.Character, record.Name, record.Region, record.HomeMap, record.LastSeen);
}
