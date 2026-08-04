// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Vixen.Live.Client;

/// <summary>What a gate answered, or what stopped it answering.</summary>
/// <typeparam name="T">The answer's shape.</typeparam>
/// <param name="Value">It, when there is one.</param>
/// <param name="Problem">Why not, otherwise.</param>
/// <param name="Status">The HTTP status, or 0 when the request never arrived.</param>
public readonly record struct GateOutcome<T>(T? Value, GateProblem? Problem, int Status) {
    /// <summary>Whether there is an answer.</summary>
    public bool Ok => Problem is null;

    /// <summary>Whether nothing was reached at all, as opposed to being refused by something.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth distinguishing, because the two want different pixels.</b> "The gate said no"
    ///     is a sentence to show the player; "the gate did not answer" is a spinner and a retry, and
    ///     a client that showed the first for the second sends people to a support forum over a
    ///     dropped Wi-Fi connection.
    /// </remarks>
    public bool Unreachable => Status == 0;

    /// <summary>The answer, or a sentence explaining its absence.</summary>
    /// <returns>Something printable.</returns>
    public override string ToString() =>
        Ok
            ? string.Create(CultureInfo.InvariantCulture, $"{Status}: {Value}")
            : string.Create(CultureInfo.InvariantCulture, $"{Status} {Problem!.Code}: {Problem.Detail}");
}

/// <summary>The four calls a game makes to a gate, and the token it carries between them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here throws for a refusal.</b> A gate saying "that name is taken" or "fetch
///         the update" is an ordinary answer, and turning it into an exception makes the client's
///         happy path the only path anybody writes. Exceptions are for a request that did not
///         happen, and even those are caught here and reported as <see cref="GateOutcome{T}.Unreachable" />
///         — because on a phone, "the network went away" is not exceptional either.
///     </para>
///     <para>
///         ⚠ <b>The token is held here and never written anywhere.</b> Persisting it would make it a
///         credential on disk with none of the protection a credential deserves, and the whole point
///         of a twelve-hour session token is that signing in again is cheap.
///     </para>
/// </remarks>
/// <param name="http">
///     The transport. Its <c>BaseAddress</c> is the gate's, and the caller owns its lifetime, its
///     handler, its timeout and its TLS — every one of which is a decision a game has already made.
/// </param>
public sealed class GateClient(HttpClient http) {
    readonly HttpClient http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>The current session, once <see cref="SignInAsync" /> has succeeded.</summary>
    public SignInResponse? Session { get; private set; }

    /// <summary>Whether there is a session at all. Says nothing about whether it has expired.</summary>
    public bool SignedIn => Session is not null;

    /// <summary>The bearer header a service-plane socket needs.</summary>
    /// <remarks>Null until signed in. <c>GateConnection</c> is the consumer.</remarks>
    public AuthenticationHeaderValue? Authorization =>
        Session is null ? null : new("Bearer", Session.Token);

    /// <summary>What the fleet is running. The call to make before any other.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The version, where the catalog is, and which maps exist.</returns>
    /// <remarks>
    ///     ⚠ <b>Before sign-in, deliberately.</b> A client whose content is stale should find that out
    ///     before it has a session rather than at the moment it tries to play, and this call needs no
    ///     token so a launcher can make it too.
    /// </remarks>
    public Task<GateOutcome<CatalogResponse>> CatalogAsync(CancellationToken cancellation) =>
        SendAsync(HttpMethod.Get, "catalog", null, GateJson.Default.CatalogResponse, cancellation);

    /// <summary>Turns a credential into a session, and remembers it.</summary>
    /// <param name="scheme">Which authority — <c>steam</c>, <c>oidc</c>, <c>development</c>.</param>
    /// <param name="credential">Whatever that authority wants.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The session, or why not.</returns>
    public async Task<GateOutcome<SignInResponse>> SignInAsync(
        string scheme,
        string credential,
        CancellationToken cancellation
    ) {
        var answer = await SendAsync(
                HttpMethod.Post,
                "session",
                content => JsonContent.Create(new SignInRequest(scheme, credential), GateJson.Default.SignInRequest),
                GateJson.Default.SignInResponse,
                cancellation
            )
            .ConfigureAwait(false);

        if (answer.Ok) {
            Session = answer.Value;
        }

        return answer;
    }

    /// <summary>Forgets the session.</summary>
    /// <remarks>
    ///     ⚠ <b>Local only, and the token keeps working until it expires.</b> A stateless token cannot
    ///     be revoked, which is the trade the gate makes for having no session table; on a shared
    ///     machine, signing out is not the same as being signed out.
    /// </remarks>
    public void SignOut() => Session = null;

    /// <summary>The account's characters.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Them, oldest first.</returns>
    public Task<GateOutcome<CharacterList>> CharactersAsync(CancellationToken cancellation) =>
        SendAsync(HttpMethod.Get, "characters", null, GateJson.Default.CharacterList, cancellation);

    /// <summary>Makes a character.</summary>
    /// <param name="request">What to call it, and where to start.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The character, or why not — <c>name-taken</c> is the answer to expect.</returns>
    public Task<GateOutcome<CharacterSummary>> CreateCharacterAsync(
        CreateCharacterRequest request,
        CancellationToken cancellation
    ) =>
        SendAsync(
            HttpMethod.Post,
            "characters",
            _ => JsonContent.Create(request, GateJson.Default.CreateCharacterRequest),
            GateJson.Default.CharacterSummary,
            cancellation
        );

    /// <summary>Asks to be put somewhere.</summary>
    /// <param name="request">Which character, which map, and what this client is running.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Where to go — or a wait, an update, or a refusal.</returns>
    /// <remarks>
    ///     ⚠ <b>All four <see cref="PlayStatus" /> values arrive as a successful answer.</b>
    ///     <c>Starting</c> is a wait and <c>UpdateRequired</c> is an errand; only <c>Refused</c> is a
    ///     sentence to show. <see cref="EnterAsync" /> handles the first for you and deliberately does
    ///     not handle the second.
    /// </remarks>
    public Task<GateOutcome<PlayResponse>> PlayAsync(PlayRequest request, CancellationToken cancellation) =>
        SendAsync(
            HttpMethod.Post,
            "play",
            _ => JsonContent.Create(request, GateJson.Default.PlayRequest),
            GateJson.Default.PlayResponse,
            cancellation
        );

    /// <summary>Asks to play, and waits out a shard that is coming up.</summary>
    /// <param name="request">Which character, and where.</param>
    /// <param name="attempts">How many times to ask before giving up and returning the last answer.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The last answer — placed, refused, or still starting after <paramref name="attempts" />.</returns>
    /// <remarks>
    ///     ⚠ <b>It waits for <c>Starting</c> and returns <c>UpdateRequired</c> untouched, and the
    ///     asymmetry is the point.</b> A shard coming up needs nothing from the game but patience;
    ///     fetching a catalog is the game's own asset system doing work it must decide to do, on a
    ///     connection the player may be paying for. A helper that quietly downloaded a gigabyte would
    ///     be a helper nobody could trust.
    /// </remarks>
    public async Task<GateOutcome<PlayResponse>> EnterAsync(
        PlayRequest request,
        int attempts,
        CancellationToken cancellation
    ) {
        var answer = await PlayAsync(request, cancellation).ConfigureAwait(false);

        for (var attempt = 1; attempt < Math.Max(1, attempts); attempt++) {
            if (!answer.Ok || answer.Value!.Status != PlayStatus.Starting) {
                return answer;
            }

            // The gate's own number rather than one chosen here: how long a shard takes to come up is
            // a property of the fleet, and a client that guessed would either hammer it or feel slow.
            await Task.Delay(answer.Value.RetryAfter, cancellation).ConfigureAwait(false);

            answer = await PlayAsync(request, cancellation).ConfigureAwait(false);
        }

        return answer;
    }

    async Task<GateOutcome<T>> SendAsync<T>(
        HttpMethod method,
        string route,
        Func<object?, HttpContent>? body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape,
        CancellationToken cancellation
    ) {
        using var request = new HttpRequestMessage(method, route);

        if (body is not null) {
            request.Content = body(null);
        }

        request.Headers.Authorization = Authorization;

        try {
            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode) {
                var value = await response.Content.ReadFromJsonAsync(shape, cancellation).ConfigureAwait(false);

                return value is null
                    ? new(default, new("malformed", "The gate answered with something that was not the shape it should be."), status)
                    : new(value, null, status);
            }

            // A gate always answers a refusal with a GateProblem. Anything else on this route is an
            // intermediary — a proxy, a load balancer, a captive portal — and saying so is more use
            // than reporting whatever HTML it sent.
            var problem = await ReadProblemAsync(response, cancellation).ConfigureAwait(false);

            return new(default, problem, status);
        } catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested) {
            return new(
                default,
                new("unreachable", $"The gate did not answer: {failure.Message}"),
                0
            );
        }
    }

    static async Task<GateProblem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellation) {
        try {
            return await response.Content.ReadFromJsonAsync(GateJson.Default.GateProblem, cancellation)
                       .ConfigureAwait(false)
                   ?? Unexplained(response.StatusCode);
        } catch (Exception failure) when (failure is System.Text.Json.JsonException or NotSupportedException) {
            return Unexplained(response.StatusCode);
        }
    }

    static GateProblem Unexplained(HttpStatusCode status) =>
        new(
            "unexplained",
            $"Something between this client and the gate answered {(int)status} without saying why."
        );
}
