// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vixen.Live;

/// <summary>What a client sends to prove who it is.</summary>
/// <remarks>
///     ⚠ <b><see cref="Credential" /> is opaque to the gate and to this assembly.</b> It is whatever
///     the named <see cref="Scheme" /> understands — a Steam session ticket, an OIDC id token, an EOS
///     auth token, a development handle. Nothing in the engine reads it; the deployment's
///     <c>IAccountAuthority</c> does, and answers with the handle it means.
/// </remarks>
/// <param name="Scheme">Which authority. <c>steam</c>, <c>oidc</c>, <c>development</c>.</param>
/// <param name="Credential">What that authority wants. Never a password this layer handles.</param>
public sealed record SignInRequest(string Scheme, string Credential);

/// <summary>What the gate hands back: a bearer token and who it says you are.</summary>
/// <param name="Token">Carried on every later request. Opaque — the client cannot read or forge it.</param>
/// <param name="Account">Which account, so a client can tell two of its own logins apart.</param>
/// <param name="Expires">When it stops working. Sign in again; there is no refresh.</param>
public sealed record SignInResponse(string Token, Guid Account, DateTimeOffset Expires);

/// <summary>One of an account's characters, as character select shows it.</summary>
/// <param name="Character">Which character. The half of <see cref="PlayerKey" /> a client names.</param>
/// <param name="Name">What other players see.</param>
/// <param name="Region">Its latency zone.</param>
/// <param name="Map">Where it will log in — where it was when it left.</param>
/// <param name="LastSeen">When a realm last held its lease.</param>
public sealed record CharacterSummary(
    Guid Character,
    string Name,
    string Region,
    string Map,
    DateTimeOffset LastSeen
);

/// <summary>Every character on the signed-in account.</summary>
/// <param name="Characters">Them, oldest first.</param>
public sealed record CharacterList(IReadOnlyList<CharacterSummary> Characters);

/// <summary>Make me one.</summary>
/// <param name="Name">What to call it. Taken names are refused rather than adjusted.</param>
/// <param name="Region">Which latency zone.</param>
/// <param name="Map">Which map to start on.</param>
public sealed record CreateCharacterRequest(string Name, string Region, string Map);

/// <summary>What this fleet is running, and where to fetch it.</summary>
/// <remarks>
///     ⚠ <b>The client asks this before it asks to play, and that ordering is ADR-022's whole
///     upgrade story.</b> A client whose catalog does not match is not rejected at a handshake — it
///     is told what the target is and where to get it, and it keeps playing on a shard that still
///     matches until it does. Doc 27 § Upgrades' three bounds are what stop that being forever.
/// </remarks>
/// <param name="Version">The fleet's target build and content hash.</param>
/// <param name="Content">Where the catalog lives, for the addressable update.</param>
/// <param name="Maps">Which maps a client may ask to play on.</param>
public sealed record CatalogResponse(RealmVersion Version, string Content, IReadOnlyList<string> Maps);

/// <summary>Put me somewhere.</summary>
/// <param name="Character">Which of my characters.</param>
/// <param name="Map">Which map. An addressable address (ADR-013).</param>
/// <param name="Version">What I have. Checked against the fleet's before anything else.</param>
/// <param name="Locale">My language tag, which is a placement term rather than a display setting.</param>
/// <param name="Party">My party, or empty.</param>
/// <param name="Guild">My guild, or empty.</param>
public sealed record PlayRequest(
    Guid Character,
    string Map,
    RealmVersion Version,
    string Locale,
    Guid Party,
    Guid Guild
);

/// <summary>What the gate decided about a request to play.</summary>
public enum PlayStatus : byte {
    /// <summary>There is a shard and it is ready. Open a session to the endpoint with the ticket.</summary>
    Placed = 0,

    /// <summary>A shard is coming up. Ask again shortly, and show a wait rather than a failure.</summary>
    /// <remarks>
    ///     ⚠ <b>Not an error, and a client that renders it as one turns an elastic fleet's ordinary
    ///     behaviour into a support ticket.</b> Doc 27 § Placement's spawn path takes seconds.
    /// </remarks>
    Starting = 1,

    /// <summary>Nowhere, for a reason waiting will not fix.</summary>
    Refused = 2,

    /// <summary>
    ///     Your content does not match the fleet's. Fetch the catalog and ask again. ADR-022.
    /// </summary>
    /// <remarks>
    ///     A routing decision rather than a rejection, which is the difference between a rolling
    ///     upgrade and a maintenance window.
    /// </remarks>
    UpdateRequired = 3
}

/// <summary>Where to go, and the permission to be let in when you get there.</summary>
/// <param name="Status">Whether anywhere.</param>
/// <param name="Endpoint">The realm's address, when placed. Empty otherwise.</param>
/// <param name="Ticket">The encoded <see cref="TransferTicket" />. Opaque; the client is a courier.</param>
/// <param name="Shard">Which shard, for the client's own logs and for support.</param>
/// <param name="Reason">Why, in a sentence. Always set, including on success.</param>
/// <param name="RetryAfter">How long to wait before asking again, when <see cref="PlayStatus.Starting" />.</param>
public sealed record PlayResponse(
    PlayStatus Status,
    string Endpoint,
    string Ticket,
    string Shard,
    string Reason,
    TimeSpan RetryAfter
);

/// <summary>What went wrong, when something did. The body of every non-2xx answer.</summary>
/// <param name="Code">A stable, machine-readable token — <c>unauthenticated</c>, <c>name-taken</c>.</param>
/// <param name="Detail">A sentence for a human, which may change without being a breaking change.</param>
public sealed record GateProblem(string Code, string Detail);

/// <summary>Something the gate pushed down the service-plane socket.</summary>
/// <remarks>
///     <para>
///         Doc 27 § The three planes: the client holds one UDP session to its realm and one WSS to
///         the gate. This is what travels on the second one — guild and whisper chat, party invites,
///         a catalog that has been published, a shard that is draining.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is per-tick and nothing here is waited on.</b> A message a player is
///         waiting on belongs on the data plane; anything that would be wrong to lose belongs in a
///         request. This socket is allowed to be down.
///     </para>
/// </remarks>
/// <param name="Kind">What sort. <c>catalog</c>, <c>draining</c>, <c>chat</c>, <c>pong</c>.</param>
/// <param name="Detail">Its payload, as the kind defines it.</param>
/// <param name="At">The gate's clock.</param>
public sealed record GateEvent(string Kind, string Detail, DateTimeOffset At);

/// <summary>
///     The service plane's JSON, generated rather than reflected — the client is a NativeAOT binary.
/// </summary>
/// <remarks>
///     ⚠ <b>This is why the DTOs are here and not in the gate.</b> Doc 27 § The three assemblies a
///     game writes puts the gate's contract in an assembly both ends reference; two copies of
///     <c>PlayResponse</c> would be two shapes that drift, and the drift presents as a client that
///     cannot log in after a server deploy. <c>Vixen.Live.Abstractions</c> is the assembly a client
///     is allowed to see, so the contract lives here and the ASP.NET half reads it.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(SignInResponse))]
[JsonSerializable(typeof(CharacterSummary))]
[JsonSerializable(typeof(CharacterList))]
[JsonSerializable(typeof(CreateCharacterRequest))]
[JsonSerializable(typeof(CatalogResponse))]
[JsonSerializable(typeof(PlayRequest))]
[JsonSerializable(typeof(PlayResponse))]
[JsonSerializable(typeof(GateProblem))]
[JsonSerializable(typeof(GateEvent))]
public sealed partial class GateJson : JsonSerializerContext;

/// <summary>How <see cref="RealmVersion" /> crosses the service plane.</summary>
/// <remarks>
///     A converter rather than a shape, because the version pair already has one canonical spelling —
///     <c>0.1.0+c0ffee</c> — that a command line, a log line, a grain key and now a JSON document all
///     use. A second spelling as an object with two fields would be a second thing to keep in step.
/// </remarks>
public sealed class RealmVersionJsonConverter : JsonConverter<RealmVersion> {
    /// <inheritdoc />
    public override RealmVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RealmVersion.TryParse(reader.GetString(), out var version) ? version : RealmVersion.None;

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RealmVersion value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString());
    }
}
