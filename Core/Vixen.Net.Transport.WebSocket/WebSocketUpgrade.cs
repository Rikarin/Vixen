// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Vixen.Net.Transport.WebSocket;

/// <summary>The RFC 6455 handshake, as a function of bytes.</summary>
/// <remarks>
///     <para>
///         <b>The one part of this transport we parse ourselves, and it runs before anything has
///         authenticated.</b> The framing above it is <c>WebSocket.CreateFromStream</c> — the runtime's,
///         and deliberately not ours. The upgrade is thirty lines of header scanning over bytes from a
///         stranger, which makes it the most exposed code in the package and the reason it is a static
///         function over a span rather than a loop inside an <c>async</c> method: something shaped like
///         this can be fuzzed, and something reading from a <c>NetworkStream</c> cannot.
///     </para>
///     <para>
///         <b>Bytes rather than a string, which is a bug fix as much as a shape.</b> The previous
///         version decoded and split the whole accumulated buffer <i>on every read</i>, so a client
///         dribbling its request one byte at a time made the server build four thousand strings of up
///         to four kilobytes — about eight megabytes of garbage for four kilobytes sent, at no cost to
///         the sender, times however many sockets they care to open. Scanning the bytes costs nothing
///         and allocates one string, for the key.
///     </para>
/// </remarks>
public static class WebSocketUpgrade {
    /// <summary>The GUID RFC 6455 says to append to the key before hashing it.</summary>
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The most of a request that will be read before it is refused.</summary>
    /// <remarks>
    ///     Four kilobytes is a generous browser request and a small fraction of what a proxy would
    ///     send. The bound is the point: without one, "read until the headers end" is an instruction
    ///     to buffer whatever a stranger feels like sending.
    /// </remarks>
    public const int MaxRequestBytes = 4096;

    static ReadOnlySpan<byte> HeadersEnd => "\r\n\r\n"u8;

    static ReadOnlySpan<byte> KeyHeader => "sec-websocket-key:"u8;

    /// <summary>Whether the headers have finished, and where they finish.</summary>
    /// <param name="request">What has arrived so far.</param>
    /// <param name="from">Where to start looking, so a growing buffer is not rescanned from the front.</param>
    /// <param name="length">How long the request is, including the blank line.</param>
    /// <returns>Whether the blank line has arrived.</returns>
    /// <remarks>
    ///     <paramref name="from" /> is what makes reading a request O(its length) rather than
    ///     O(length × reads). A caller passes the end of what it had before this read, less three
    ///     bytes, because the terminator may straddle the boundary.
    /// </remarks>
    public static bool IsComplete(ReadOnlySpan<byte> request, int from, out int length) {
        length = 0;

        if (from < 0 || from > request.Length) {
            return false;
        }

        var at = request[from..].IndexOf(HeadersEnd);

        if (at < 0) {
            return false;
        }

        length = from + at + HeadersEnd.Length;

        return true;
    }

    /// <summary>Finds the key a client offered.</summary>
    /// <param name="request">The request, headers and all.</param>
    /// <param name="key">The key, trimmed, if there was one.</param>
    /// <returns>Whether there was.</returns>
    /// <remarks>
    ///     <para>
    ///         Header names are case-insensitive per RFC 7230, which is why this compares against a
    ///         lowered copy rather than an exact one — a browser sends <c>Sec-WebSocket-Key</c> and a
    ///         proxy is entitled to send <c>sec-websocket-key</c>.
    ///     </para>
    ///     <para>
    ///         <b>It does not validate the key</b>, and that is correct rather than lax: RFC 6455 says
    ///         the value is echoed through SHA-1 and the client checks the result, so a nonsense key
    ///         produces a nonsense accept and the client refuses it. Rejecting here would be this
    ///         server deciding a question the protocol gives to the other end.
    ///     </para>
    /// </remarks>
    public static bool TryReadKey(ReadOnlySpan<byte> request, out string key) {
        key = string.Empty;

        while (!request.IsEmpty) {
            var end = request.IndexOf("\r\n"u8);
            var line = end < 0 ? request : request[..end];

            if (StartsWithHeader(line)) {
                var value = line[KeyHeader.Length..];

                // ASCII, because a header value that is not is one no RFC 6455 client sent — and
                // decoding as UTF-8 would let a malformed sequence decide how long the key is.
                key = Encoding.ASCII.GetString(value).Trim();

                return key.Length > 0;
            }

            if (end < 0) {
                return false;
            }

            request = request[(end + 2)..];
        }

        return false;
    }

    /// <summary>The response a client's key earns.</summary>
    /// <param name="key">What they offered.</param>
    /// <returns>The whole HTTP response, ready to write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is null.</exception>
    public static byte[] Accept(string key) {
        ArgumentNullException.ThrowIfNull(key);

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));

        return Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"
        );
    }

    static bool StartsWithHeader(ReadOnlySpan<byte> line) {
        if (line.Length < KeyHeader.Length) {
            return false;
        }

        for (var index = 0; index < KeyHeader.Length; index++) {
            var character = line[index];

            // Lowered in place rather than through a string, because this runs on every line of every
            // request from every stranger and the whole reason this file exists is to not allocate
            // per byte they send.
            if ((character is >= (byte)'A' and <= (byte)'Z' ? (byte)(character + 32) : character)
                != KeyHeader[index]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rents a buffer for one request.</summary>
    /// <returns>It. The caller returns it.</returns>
    internal static byte[] RentRequestBuffer() => ArrayPool<byte>.Shared.Rent(MaxRequestBytes);

    /// <summary>Gives one back.</summary>
    /// <param name="buffer">It.</param>
    internal static void ReturnRequestBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
}
