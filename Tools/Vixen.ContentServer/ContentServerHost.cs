// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;

namespace Vixen.ContentServer;

/// <summary>Puts a <see cref="ContentServer" /> on a socket.</summary>
/// <remarks>
///     <para>
///         Deliberately the thinnest thing that can be: accept, hand the path and the range header to
///         <see cref="ContentServer.Serve" />, write back what it says. Every decision — what exists,
///         what a range means, what is inside the root — is made by the class above, which is testable
///         without binding a port. Doc 12 § testing rules out real network in tests, and the way to
///         obey that without leaving the interesting half unchecked is to keep the interesting half
///         out of here.
///     </para>
///     <para>
///         <see cref="HttpListener" /> rather than Kestrel: this is a development tool that should
///         not pull ASP.NET into the build graph, and everything it needs — ranges, statuses,
///         keep-alive — the BCL listener already does.
///     </para>
/// </remarks>
public sealed class ContentServerHost : IDisposable {
    readonly HttpListener listener = new();
    readonly ContentServer server;

    /// <summary>Where it is listening.</summary>
    public string Prefix { get; }

    /// <summary>Told about each request as it is answered, for a developer watching a console.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Sets up a host.</summary>
    /// <param name="server">What answers the requests.</param>
    /// <param name="port">The port.</param>
    /// <param name="host">What to bind — <c>localhost</c> by default, <c>+</c> for every interface.</param>
    public ContentServerHost(ContentServer server, int port, string host = "localhost") {
        ArgumentNullException.ThrowIfNull(server);

        this.server = server;
        Prefix = string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/");
        listener.Prefixes.Add(Prefix);
    }

    /// <summary>Listens until cancelled.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>Nothing.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default) {
        listener.Start();

        using (cancellationToken.Register(listener.Stop)) {
            while (!cancellationToken.IsCancellationRequested) {
                HttpListenerContext context;

                try {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                } catch (HttpListenerException) {
                    // Stopping the listener is how cancellation reaches a blocked accept.
                    break;
                } catch (ObjectDisposedException) {
                    break;
                }

                // Not awaited: one slow bundle download must not stop the next request being
                // accepted, and a developer downloading a pack on a device while poking the catalog
                // from a browser is the ordinary case.
                _ = AnswerAsync(context, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => ((IDisposable)listener).Dispose();

    async Task AnswerAsync(HttpListenerContext context, CancellationToken cancellationToken) {
        try {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            using var reply = server.Serve(path, context.Request.Headers["Range"]);

            context.Response.StatusCode = (int)reply.Status;
            context.Response.ContentType = reply.ContentType;
            context.Response.ContentLength64 = reply.Length;

            // Advertised on every reply, because a client decides whether to bother resuming by
            // looking for it before it has ever sent a range.
            context.Response.AddHeader("Accept-Ranges", "bytes");

            if (reply.ContentRange() is { } contentRange) {
                context.Response.AddHeader("Content-Range", contentRange);
            }

            await reply.WriteBodyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
            Log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{(int)reply.Status} {path} ({reply.Length} bytes)"));
        } catch (Exception failure) when (failure is IOException or HttpListenerException or OperationCanceledException) {
            // A client that hangs up mid-download is the normal way a cancelled install ends, not an
            // event worth taking the server down for.
            Log?.Invoke($"dropped: {failure.Message}");
        } finally {
            try {
                context.Response.Close();
            } catch (Exception failure) when (failure is IOException or HttpListenerException) {
                // Already gone.
            }
        }
    }
}
