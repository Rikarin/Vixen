// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;

namespace Vixen.Assets;

/// <summary>Fetches a URL's bytes, optionally starting part way in.</summary>
/// <remarks>
///     <para>
///         The one place the content system touches the network, and an interface so that the cache
///         above it can be tested without one. Every interesting failure a download has — a connection
///         that dies half way, a server that ignores a range request, bytes that arrive corrupted — is
///         a behaviour of this interface rather than of HTTP, so a test can produce it deliberately.
///     </para>
///     <para>
///         <b>It fetches; it does not retry, cache or verify.</b> Those are the layer above's job,
///         because they need to know about bundles and this only knows about bytes.
///     </para>
/// </remarks>
public interface IContentTransport {
    /// <summary>Starts reading a URL.</summary>
    /// <param name="url">What to fetch.</param>
    /// <param name="offset">Where to start, for resuming. Zero for the whole thing.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The response, which the caller disposes.</returns>
    /// <exception cref="ContentTransportException">It could not be fetched.</exception>
    ValueTask<ContentDownload> GetAsync(string url, long offset = 0, CancellationToken cancellationToken = default);
}

/// <summary>A response in progress: where it starts, how long it is, and its bytes.</summary>
/// <remarks>
///     <b><see cref="Offset" /> is what the server actually gave, not what was asked for.</b> A server
///     is entitled to ignore a range request and send the whole resource, and one that does must not
///     leave the caller appending a complete file to a partial one. Reporting where the body really
///     starts is what lets the cache notice and start again.
/// </remarks>
/// <param name="body">The bytes, from <paramref name="offset" /> onwards.</param>
/// <param name="offset">Where in the resource the body starts.</param>
/// <param name="totalLength">How long the whole resource is, if the server said.</param>
public sealed class ContentDownload(Stream body, long offset, long? totalLength) : IDisposable {
    /// <summary>The bytes, from <see cref="Offset" /> onwards.</summary>
    public Stream Body { get; } = body;

    /// <summary>Where in the resource <see cref="Body" /> starts.</summary>
    public long Offset { get; } = offset;

    /// <summary>How long the whole resource is, or <see langword="null" /> if the server did not say.</summary>
    public long? TotalLength { get; } = totalLength;

    /// <inheritdoc />
    public void Dispose() => Body.Dispose();
}

/// <summary>A URL that could not be fetched.</summary>
/// <param name="url">Which URL.</param>
/// <param name="reason">Why not.</param>
/// <param name="inner">What went wrong underneath, if anything did.</param>
public sealed class ContentTransportException(string url, string reason, Exception? inner = null)
    : Exception($"'{url}' could not be fetched: {reason}", inner) {
    /// <summary>Which URL.</summary>
    public string Url { get; } = url;
}

/// <summary>The real one: HTTP, with byte ranges.</summary>
/// <remarks>
///     <para>
///         Deliberately thin. Timeouts, retries, proxies and certificate policy are all
///         <see cref="HttpClient" /> configuration, and a game that needs any of them configures the
///         client it hands in rather than asking this class for a knob.
///     </para>
///     <para>
///         The response is read with
///         <see cref="HttpCompletionOption.ResponseHeadersRead" />, so a 400 MB bundle streams into the
///         cache file instead of being buffered whole before the first byte reaches disk.
///     </para>
/// </remarks>
public sealed class HttpContentTransport : IContentTransport, IDisposable {
    readonly HttpClient client;
    readonly bool ownsClient;

    /// <summary>Fetches over HTTP with a client of its own.</summary>
    public HttpContentTransport() : this(new HttpClient(), true) { }

    /// <summary>Fetches over HTTP with a client the caller configured.</summary>
    /// <param name="client">The client.</param>
    /// <param name="ownsClient">Whether disposing this should dispose the client.</param>
    public HttpContentTransport(HttpClient client, bool ownsClient = false) {
        ArgumentNullException.ThrowIfNull(client);

        this.client = client;
        this.ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public async ValueTask<ContentDownload> GetAsync(
        string url,
        long offset = 0,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (offset > 0) {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        HttpResponseMessage response;

        try {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        } catch (HttpRequestException failure) {
            throw new ContentTransportException(url, failure.Message, failure);
        }

        if (!response.IsSuccessStatusCode) {
            var status = response.StatusCode;
            response.Dispose();

            throw new ContentTransportException(
                url,
                $"the server answered {(int)status} {status}"
            );
        }

        // 206 means the range was honoured and the body starts where Content-Range says. 200 means it
        // was not, and the body is the whole resource however much was asked to be skipped.
        var start = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.From ?? offset
            : 0L;

        var total = response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is { } length ? start + length : null);

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return new(new ResponseStream(body, response), start, total);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (ownsClient) {
            client.Dispose();
        }
    }

    /// <summary>Ties the response's lifetime to the body's, so one <c>using</c> closes both.</summary>
    sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
