// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vixen.Live.Gate;

/// <summary>The routes, and nothing else.</summary>
/// <remarks>
///     ⚠ <b>Every handler here reads a header, calls one <see cref="GateService" /> method and writes
///     the answer.</b> That is deliberate and it is the same shape the grains took over their state
///     machines: a decision made inside an ASP.NET handler is one that needs a web host to assert,
///     and doc 27 § Testing's whole strategy is that the interesting things run in one process on a
///     laptop. If a rule ever appears in this file, it is in the wrong file.
/// </remarks>
public static class GateEndpoints {
    /// <summary>Maps the service plane.</summary>
    /// <param name="routes">Where to map it.</param>
    /// <param name="prefix">The version prefix. One number, moved when the shapes break.</param>
    /// <returns><paramref name="routes" />, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes" /> is null.</exception>
    public static IEndpointRouteBuilder MapVixenGate(this IEndpointRouteBuilder routes, string prefix = "/v1") {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup(prefix);

        group.MapGet("/catalog", (GateService gate) => Results.Json(gate.Catalog(), GateJson.Default.CatalogResponse));

        group.MapPost(
            "/session",
            async (HttpContext context, GateService gate, CancellationToken cancellation) => {
                var request = await context.Request.ReadFromJsonAsync(GateJson.Default.SignInRequest, cancellation)
                    .ConfigureAwait(false);

                return request is null
                    ? Problem(400, "malformed", "That was not a sign-in request.")
                    : Answer(await gate.SignInAsync(request, cancellation).ConfigureAwait(false), GateJson.Default.SignInResponse);
            }
        );

        group.MapGet(
            "/characters",
            async (HttpContext context, GateService gate, CancellationToken cancellation) =>
                Authenticated(context, gate, out var session, out var refusal)
                    ? Answer(await gate.CharactersAsync(session!, cancellation).ConfigureAwait(false), GateJson.Default.CharacterList)
                    : refusal!
        );

        group.MapPost(
            "/characters",
            async (HttpContext context, GateService gate, CancellationToken cancellation) => {
                if (!Authenticated(context, gate, out var session, out var refusal)) {
                    return refusal!;
                }

                var request = await context.Request
                    .ReadFromJsonAsync(GateJson.Default.CreateCharacterRequest, cancellation)
                    .ConfigureAwait(false);

                return request is null
                    ? Problem(400, "malformed", "That was not a character.")
                    : Answer(
                        await gate.CreateCharacterAsync(session!, request, cancellation).ConfigureAwait(false),
                        GateJson.Default.CharacterSummary
                    );
            }
        );

        group.MapPost(
            "/play",
            async (HttpContext context, GateService gate, CancellationToken cancellation) => {
                if (!Authenticated(context, gate, out var session, out var refusal)) {
                    return refusal!;
                }

                var request = await context.Request.ReadFromJsonAsync(GateJson.Default.PlayRequest, cancellation)
                    .ConfigureAwait(false);

                return request is null
                    ? Problem(400, "malformed", "That was not a request to play.")
                    : Answer(
                        await gate.PlayAsync(session!, request, cancellation).ConfigureAwait(false),
                        GateJson.Default.PlayResponse
                    );
            }
        );

        group.Map("/stream", StreamAsync);

        return routes;
    }

    /// <summary>The service plane's socket: the gate talks, the client mostly listens.</summary>
    /// <remarks>
    ///     ⚠ <b>Authenticated by the <c>Authorization</c> header, not by a query string.</b> A token
    ///     in a URL is written to every access log and proxy cache between here and the player, which
    ///     is why it is refused here even though it would be more convenient. A browser cannot set
    ///     the header on a WebSocket and would need the <c>Sec-WebSocket-Protocol</c> convention
    ///     instead; a game client is not a browser, and adding the second path before something needs
    ///     it would be adding a second way in.
    /// </remarks>
    static async Task StreamAsync(HttpContext context) {
        var gate = context.RequestServices.GetRequiredService<GateService>();
        var stream = context.RequestServices.GetRequiredService<ServicePlane>();
        var log = context.RequestServices.GetRequiredService<ILogger<ServicePlane>>();
        var options = context.RequestServices.GetRequiredService<GateOptions>();

        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;

            return;
        }

        if (gate.Authenticate(context.Request.Headers.Authorization, out var session) != TokenStatus.Valid) {
            context.Response.StatusCode = 401;

            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var subscriber = new WebSocketSubscriber(session!.Account, socket);

        stream.Join(subscriber);
        log.GateStreamOpened(session.Account, stream.Count);

        var buffer = new byte[512];

        try {
            while (socket.State == WebSocketState.Open) {
                using var idle = new CancellationTokenSource(options.StreamKeepAlive);
                using var either = CancellationTokenSource.CreateLinkedTokenSource(idle.Token, context.RequestAborted);

                try {
                    var received = await socket.ReceiveAsync(buffer, either.Token).ConfigureAwait(false);

                    if (received.MessageType == WebSocketMessageType.Close) {
                        break;
                    }

                    // Anything a client sends is a ping. The service plane is a push channel, and a
                    // request belongs on a request — a socket that also carried commands would need
                    // its own authorisation, rate limiting and closed-set deserialization, which is
                    // the whole security surface doc 16 built once already.
                    await subscriber.PostAsync(new("pong", "", DateTimeOffset.UtcNow)).ConfigureAwait(false);
                } catch (OperationCanceledException) when (idle.IsCancellationRequested) {
                    // Nothing said for a while. Say something, so whatever is between the gate and
                    // the player does not decide the connection is finished with.
                    await subscriber.PostAsync(new("keep-alive", "", DateTimeOffset.UtcNow)).ConfigureAwait(false);
                }
            }
        } catch (Exception failure) when (failure is WebSocketException or OperationCanceledException) {
            // An abrupt close is how most sockets end. Nothing was lost, because nothing that matters
            // travels here.
        } finally {
            stream.Leave(subscriber);
            log.GateStreamClosed(session.Account, stream.Count);
        }
    }

    static bool Authenticated(HttpContext context, GateService gate, out GateToken? session, out IResult? refusal) {
        var status = gate.Authenticate(context.Request.Headers.Authorization, out session);

        refusal = status switch {
            TokenStatus.Valid => null,
            TokenStatus.Expired => Problem(401, "expired", "That session has expired. Sign in again."),
            _ => Problem(401, "unauthenticated", "A valid session token is required.")
        };

        return refusal is null;
    }

    static IResult Answer<T>(GateAnswer<T> answer, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape) =>
        answer.Ok
            ? Results.Json(answer.Value, shape, statusCode: answer.Status)
            : Results.Json(answer.Problem, GateJson.Default.GateProblem, statusCode: answer.Status);

    static IResult Problem(int status, string code, string detail) =>
        Results.Json(new GateProblem(code, detail), GateJson.Default.GateProblem, statusCode: status);
}
