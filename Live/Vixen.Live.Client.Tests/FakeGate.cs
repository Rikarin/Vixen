// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Vixen.Live.Client.Tests;

/// <summary>A gate that is a queue of answers.</summary>
/// <remarks>
///     ⚠ <b>A message handler rather than a running server, and the distinction is what the tests are
///     about.</b> Everything worth asserting on this side — that the bearer header is sent, that a
///     refusal is an answer rather than an exception, that <c>Starting</c> is waited out and
///     <c>UpdateRequired</c> is not — is a property of the client. Whether Kestrel serves the routes
///     is <c>Vixen.Live.Gate</c>'s question and is answered there.
/// </remarks>
sealed class FakeGate : HttpMessageHandler {
    readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> answers = new();

    /// <summary>Every request it was sent, in order.</summary>
    public List<(string Route, string? Authorization)> Seen { get; } = [];

    /// <summary>An <c>HttpClient</c> pointed at it.</summary>
    public HttpClient Client => new(this) { BaseAddress = new("https://gate.example/v1/") };

    /// <summary>Queues an answer.</summary>
    /// <typeparam name="T">Its shape.</typeparam>
    /// <param name="value">It.</param>
    /// <param name="shape">How to write it.</param>
    /// <param name="status">The status to send it with.</param>
    /// <returns>This, for chaining.</returns>
    public FakeGate Answers<T>(T value, JsonTypeInfo<T> shape, HttpStatusCode status = HttpStatusCode.OK) {
        answers.Enqueue(_ => new(status) { Content = JsonContent.Create(value, shape) });

        return this;
    }

    /// <summary>Queues a refusal.</summary>
    /// <param name="status">The status.</param>
    /// <param name="code">The stable token.</param>
    /// <param name="detail">The sentence.</param>
    /// <returns>This, for chaining.</returns>
    public FakeGate Refuses(HttpStatusCode status, string code, string detail) =>
        Answers(new GateProblem(code, detail), GateJson.Default.GateProblem, status);

    /// <summary>Queues something that is not a gate at all — a proxy, a captive portal.</summary>
    /// <param name="status">What it answered.</param>
    /// <param name="body">The HTML it sent instead.</param>
    /// <returns>This, for chaining.</returns>
    public FakeGate Interferes(HttpStatusCode status, string body) {
        answers.Enqueue(_ => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") });

        return this;
    }

    /// <summary>Queues a connection that never lands.</summary>
    /// <returns>This, for chaining.</returns>
    public FakeGate Vanishes() {
        answers.Enqueue(_ => throw new HttpRequestException("no route to host"));

        return this;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        Seen.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));

        return answers.Count == 0
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented))
            : Task.FromResult(answers.Dequeue()(request));
    }
}
