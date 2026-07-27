// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>
///     The HTTP transport, against a handler rather than a socket. What is worth testing here is the
///     part that reads a response and decides where in the resource its body starts — a
///     <c>206</c> with a <c>Content-Range</c>, a <c>200</c> that ignored the range, a length that is
///     the remainder rather than the whole — and none of that needs a port to be bound.
/// </summary>
public sealed class HttpContentTransportTests {
    /// <summary>A whole fetch sends no range and reports the resource's length.</summary>
    [Fact]
    public async Task AWholeFetchSendsNoRangeAndReportsTheLength() {
        var server = new Handler((_, _) => Ok(HttpStatusCode.OK, new byte[4096]));
        using var transport = new HttpContentTransport(new HttpClient(server), true);

        using var download = await transport.GetAsync(
            "https://content.example/pack.bundle",
            0,
            TestContext.Current.CancellationToken
        );

        Assert.Null(server.LastRange);
        Assert.Equal(0, download.Offset);
        Assert.Equal(4096, download.TotalLength);
    }

    /// <summary>
    ///     A resume sends <c>Range: bytes=N-</c> and, when the server honours it, reports the body as
    ///     starting at N and the resource as its whole length — not the length of what is left, which
    ///     is what <c>Content-Length</c> alone would say.
    /// </summary>
    [Fact]
    public async Task AResumeSendsARangeAndReportsWhereTheBodyStarts() {
        var server = new Handler((_, range) => {
            var from = range!.Ranges.Single().From!.Value;
            var response = Ok(HttpStatusCode.PartialContent, new byte[4096 - from]);
            response.Content.Headers.ContentRange = new(from, 4095, 4096);

            return response;
        });

        using var transport = new HttpContentTransport(new HttpClient(server), true);

        using var download = await transport.GetAsync(
            "https://content.example/pack.bundle",
            1000,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1000, server.LastRange!.Ranges.Single().From);
        Assert.Null(server.LastRange.Ranges.Single().To);
        Assert.Equal(1000, download.Offset);
        Assert.Equal(4096, download.TotalLength);
    }

    /// <summary>
    ///     The case the whole <see cref="ContentDownload.Offset" /> field exists for. A server is
    ///     entitled to ignore a range and answer <c>200</c> with the whole resource; reporting that the
    ///     body starts at zero is what lets the cache above notice and start again rather than append
    ///     a complete file to a partial one.
    /// </summary>
    [Fact]
    public async Task AServerThatIgnoresTheRangeIsReportedAsStartingAtZero() {
        var server = new Handler((_, _) => Ok(HttpStatusCode.OK, new byte[4096]));
        using var transport = new HttpContentTransport(new HttpClient(server), true);

        using var download = await transport.GetAsync(
            "https://content.example/pack.bundle",
            1000,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, download.Offset);
        Assert.Equal(4096, download.TotalLength);
    }

    /// <summary>
    ///     A partial response with no <c>Content-Range</c> is taken at its word about the offset, and
    ///     its length is the remainder — so the total is that plus where it started.
    /// </summary>
    [Fact]
    public async Task APartialResponseWithNoContentRangeStillAddsUp() {
        var server = new Handler((_, _) => Ok(HttpStatusCode.PartialContent, new byte[3096]));
        using var transport = new HttpContentTransport(new HttpClient(server), true);

        using var download = await transport.GetAsync(
            "https://content.example/pack.bundle",
            1000,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1000, download.Offset);
        Assert.Equal(4096, download.TotalLength);
    }

    /// <summary>A server that says no gives back its code, which is the first thing anyone looks at.</summary>
    [Fact]
    public async Task AFailureStatusNamesTheCode() {
        var server = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var transport = new HttpContentTransport(new HttpClient(server), true);

        var failure = await Assert.ThrowsAsync<ContentTransportException>(
            async () => await transport.GetAsync(
                "https://content.example/gone.bundle",
                0,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("404", failure.Message, StringComparison.Ordinal);
        Assert.Equal("https://content.example/gone.bundle", failure.Url);
    }

    /// <summary>And a connection that never opens is the same kind of answer, with the cause kept.</summary>
    [Fact]
    public async Task AConnectionThatFailsBecomesATransportException() {
        var server = new Handler((_, _) => throw new HttpRequestException("no such host is known"));
        using var transport = new HttpContentTransport(new HttpClient(server), true);

        var failure = await Assert.ThrowsAsync<ContentTransportException>(
            async () => await transport.GetAsync(
                "https://content.invalid/pack.bundle",
                0,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("no such host", failure.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    /// <summary>
    ///     Disposing the download disposes the <b>response</b> with it, not merely the stream it
    ///     handed out. The body outlives the method that produced it, so a download that did not carry
    ///     the response along would leak a connection per fetch — and would still look correct from
    ///     the outside, because the stream gets closed either way.
    /// </summary>
    [Fact]
    public async Task DisposingTheDownloadClosesTheResponseAndNotJustTheStream() {
        var content = new TrackedContent(new byte[16]);
        var server = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        using var transport = new HttpContentTransport(new HttpClient(server), true);
        var download = await transport.GetAsync(
            "https://content.example/pack.bundle",
            0,
            TestContext.Current.CancellationToken
        );

        Assert.False(content.IsDisposed);
        download.Dispose();
        Assert.True(content.IsDisposed);
    }

    /// <summary>A negative offset is a caller's bug, not a server's.</summary>
    [Fact]
    public async Task ANegativeOffsetIsRefusedBeforeAnythingIsSent() {
        var server = new Handler((_, _) => Ok(HttpStatusCode.OK, []));
        using var transport = new HttpContentTransport(new HttpClient(server), true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await transport.GetAsync(
                "https://content.example/pack.bundle",
                -1,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(0, server.Requests);
    }

    static HttpResponseMessage Ok(HttpStatusCode status, byte[] body) {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = body.Length;

        return response;
    }

    /// <summary>Answers requests from a lambda, and remembers what was asked.</summary>
    sealed class Handler(Func<Uri, RangeHeaderValue?, HttpResponseMessage> answer) : HttpMessageHandler {
        public RangeHeaderValue? LastRange { get; private set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            Requests++;
            LastRange = request.Headers.Range;

            return Task.FromResult(answer(request.RequestUri!, request.Headers.Range));
        }
    }

    /// <summary>
    ///     A response body that says whether the <i>response</i> was closed. It hands out a fresh
    ///     stream per read, so closing that stream does not close this — which is exactly the
    ///     difference the test above is looking for.
    /// </summary>
    sealed class TrackedContent(byte[] body) : HttpContent {
        public bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length) {
            length = body.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(body, false));

        protected override void Dispose(bool disposing) {
            IsDisposed |= disposing;
            base.Dispose(disposing);
        }
    }
}
