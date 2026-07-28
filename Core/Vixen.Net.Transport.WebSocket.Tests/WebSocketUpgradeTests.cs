// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Net.Transport.WebSocket.Tests;

/// <summary>The opening handshake, which is the only part of this transport we parse.</summary>
public sealed class WebSocketUpgradeTests {
    const string Browser = "GET / HTTP/1.1\r\n"
        + "Host: localhost\r\n"
        + "Upgrade: websocket\r\n"
        + "Connection: Upgrade\r\n"
        + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
        + "Sec-WebSocket-Version: 13\r\n\r\n";

    /// <summary>What a browser sends, answered the way RFC 6455 § 1.3 says it must be.</summary>
    /// <remarks>
    ///     The key and its answer are the worked example from the RFC itself, so this is a test
    ///     against the specification rather than against the implementation's own output.
    /// </remarks>
    [Fact]
    public void TheWorkedExampleFromTheSpecification() {
        var request = Encoding.ASCII.GetBytes(Browser);

        Assert.True(WebSocketUpgrade.IsComplete(request, 0, out var length));
        Assert.Equal(request.Length, length);
        Assert.True(WebSocketUpgrade.TryReadKey(request.AsSpan(0, length), out var key));
        Assert.Equal("dGhlIHNhbXBsZSBub25jZQ==", key);

        var response = Encoding.ASCII.GetString(WebSocketUpgrade.Accept(key));

        Assert.StartsWith("HTTP/1.1 101 Switching Protocols\r\n", response, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", response, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", response, StringComparison.Ordinal);
    }

    /// <summary>Header names are case-insensitive, because RFC 7230 says so.</summary>
    /// <remarks>
    ///     A browser sends <c>Sec-WebSocket-Key</c> and a proxy is entitled to rewrite it. Comparing
    ///     exactly would work in every test anybody wrote and fail behind somebody's load balancer.
    /// </remarks>
    [Theory]
    [InlineData("Sec-WebSocket-Key: abc\r\n\r\n")]
    [InlineData("sec-websocket-key: abc\r\n\r\n")]
    [InlineData("SEC-WEBSOCKET-KEY: abc\r\n\r\n")]
    [InlineData("Sec-WebSocket-Key:   abc\t \r\n\r\n")]
    public void TheHeaderNameIsCaseInsensitiveAndTheValueIsTrimmed(string request) {
        Assert.True(WebSocketUpgrade.TryReadKey(Encoding.ASCII.GetBytes(request), out var key));
        Assert.Equal("abc", key);
    }

    /// <summary>A request with no key, or an empty one, is refused rather than answered.</summary>
    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    [InlineData("Sec-WebSocket-Key:\r\n\r\n")]
    [InlineData("Sec-WebSocket-Key:    \r\n\r\n")]
    [InlineData("Sec-WebSocket-Keyx: abc\r\n\r\n")]
    [InlineData("")]
    public void ARequestWithNoUsableKeyIsRefused(string request) =>
        Assert.False(WebSocketUpgrade.TryReadKey(Encoding.ASCII.GetBytes(request), out _));

    /// <summary>Reading a request in pieces finds the same thing as reading it whole.</summary>
    /// <remarks>
    ///     <para>
    ///         The scan starts three bytes before where the last read finished, so a terminator torn
    ///         across a boundary is still found. <b>Three is exactly right and two is not</b> —
    ///         <c>\r\n\r</c> then <c>\n</c> is the split that catches it — and this walks every split
    ///         of the request rather than a chosen one, because the split a TCP stack picks is not a
    ///         thing anybody controls.
    ///     </para>
    ///     <para>
    ///         Getting it wrong would make a request parse differently depending on network timing:
    ///         no test reproduces it, and no user can report it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ReadingInPiecesFindsTheSameThingAsReadingItWhole() {
        var request = Encoding.ASCII.GetBytes(Browser);

        Assert.True(WebSocketUpgrade.IsComplete(request, 0, out var whole));

        for (var chunk = 1; chunk <= request.Length; chunk++) {
            var read = 0;
            var found = false;
            var length = 0;

            while (read < request.Length) {
                var from = Math.Max(0, read - 3);
                read = Math.Min(request.Length, read + chunk);

                if (WebSocketUpgrade.IsComplete(request.AsSpan(0, read), from, out length)) {
                    found = true;

                    break;
                }
            }

            Assert.True(found, $"A chunk size of {chunk} never found the end of the headers.");
            Assert.Equal(whole, length);
        }
    }

    /// <summary>Headers that have not finished are not treated as if they had.</summary>
    [Theory]
    [InlineData("Sec-WebSocket-Key: abc\r\n")]
    [InlineData("Sec-WebSocket-Key: abc\r\n\r")]
    [InlineData("\r\n\r")]
    [InlineData("")]
    public void UnfinishedHeadersAreNotComplete(string request) {
        Assert.False(WebSocketUpgrade.IsComplete(Encoding.ASCII.GetBytes(request), 0, out var length));
        Assert.Equal(0, length);
    }

    /// <summary>Only the headers are handed on, not whatever followed them.</summary>
    /// <remarks>
    ///     What follows the blank line is the first WebSocket frame, which belongs to
    ///     <c>WebSocket.CreateFromStream</c> and not to a header scan. A length that included it would
    ///     put frame bytes through a line splitter.
    /// </remarks>
    [Fact]
    public void OnlyTheHeadersAreHandedOn() {
        var request = Encoding.ASCII.GetBytes("Sec-WebSocket-Key: abc\r\n\r\n\x81\x05hello");

        Assert.True(WebSocketUpgrade.IsComplete(request, 0, out var length));
        Assert.Equal(26, length);
        Assert.True(WebSocketUpgrade.TryReadKey(request.AsSpan(0, length), out var key));
        Assert.Equal("abc", key);
    }

    /// <summary>A scan starting past the end says no rather than throwing.</summary>
    /// <remarks>
    ///     The caller derives the offset by arithmetic on a length, and arithmetic on a length is
    ///     where off-by-one lives. This is a decoder for bytes from a stranger; the one thing it may
    ///     not do is throw.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void AnOffsetOutsideTheRequestIsRefusedRatherThanThrowing(int from) =>
        Assert.False(WebSocketUpgrade.IsComplete(Encoding.ASCII.GetBytes(Browser), from, out _));
}
