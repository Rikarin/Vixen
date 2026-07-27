// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;

namespace Vixen.ContentServer;

/// <summary>The subset of HTTP statuses a static content server has occasion to send.</summary>
public enum ContentStatus {
    /// <summary>Here is the whole thing.</summary>
    Ok = 200,

    /// <summary>Here is the part that was asked for.</summary>
    PartialContent = 206,

    /// <summary>Nothing is at that path.</summary>
    NotFound = 404,

    /// <summary>The range asked for starts past the end of the resource.</summary>
    RangeNotSatisfiable = 416
}

/// <summary>What to send back, and the bytes to send.</summary>
/// <param name="Status">The status.</param>
/// <param name="Offset">Where in the resource the body starts.</param>
/// <param name="Length">How many bytes are in the body.</param>
/// <param name="Total">How many bytes are in the whole resource.</param>
/// <param name="ContentType">What the body is.</param>
public sealed record ContentReply(
    ContentStatus Status,
    long Offset,
    long Length,
    long Total,
    string ContentType
) : IDisposable {
    /// <summary>The bytes, already positioned at <see cref="Offset" />. Null for a status with no body.</summary>
    public Stream? Body { get; init; }

    /// <summary>The <c>Content-Range</c> header this reply needs, if it needs one.</summary>
    /// <returns>The header value, or <see langword="null" />.</returns>
    /// <remarks>
    ///     Built here rather than in the host, because getting it wrong is how a resumed download
    ///     silently assembles a file out of the wrong bytes and the client has no way to tell.
    /// </remarks>
    public string? ContentRange() =>
        Status switch {
            ContentStatus.PartialContent => string.Create(
                CultureInfo.InvariantCulture,
                $"bytes {Offset}-{Offset + Length - 1}/{Total}"
            ),
            ContentStatus.RangeNotSatisfiable => string.Create(CultureInfo.InvariantCulture, $"bytes */{Total}"),
            _ => null
        };

    /// <summary>Writes exactly the bytes this reply promised.</summary>
    /// <param name="destination">Where to write them.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>Nothing.</returns>
    /// <remarks>
    ///     <b>Exactly</b>, rather than "until the stream ends". A ranged reply is a window onto a file
    ///     the stream can read past the end of, and a server that copied to the end would answer a
    ///     request for the middle of a bundle with the middle <i>and everything after it</i> — under a
    ///     <c>Content-Length</c> saying otherwise.
    /// </remarks>
    public async Task WriteBodyToAsync(Stream destination, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(destination);

        if (Body is null) {
            return;
        }

        var buffer = new byte[81920];
        var left = Length;

        while (left > 0) {
            var wanted = (int)Math.Min(buffer.Length, left);
            var read = await Body.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

            if (read == 0) {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            left -= read;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Body?.Dispose();
}

/// <summary>Serves a content build directory over HTTP, for a developer to point a device at.</summary>
/// <remarks>
///     <para>
///         The other end of <c>Vixen.Assets</c>' remote content: a content build produces a directory
///         of bundles and a catalog, and this hands them out so a phone can be pointed at a laptop
///         without a CDN in between. It is a development tool and says so — no TLS, no authentication,
///         no caching policy, and it should never be the thing in front of real players.
///     </para>
///     <para>
///         <b>Byte ranges are the feature.</b> Everything else here is a file copy; ranges are what
///         makes the client's resume work, and a server without them turns every dropped connection on
///         a device into starting the download again. All three forms are answered — <c>bytes=N-</c>,
///         <c>bytes=N-M</c> and the suffix form <c>bytes=-N</c> — because clients in the wild send all
///         three.
///     </para>
///     <para>
///         <b>A hash file is synthesised when it is not on disk.</b> The update client reads
///         <c>catalog.bin.hash</c> before it reads <c>catalog.bin</c>, and a content build directory
///         copied as-is does not contain one; without this, pointing a device at a build gives a
///         rejected update and no clue why. Computed from the file it names, so it cannot disagree
///         with it.
///     </para>
///     <para>
///         <b>Nothing outside the root is reachable.</b> The path is percent-decoded first and then
///         parsed as a <see cref="VirtualPath" />, which resolves <c>.</c> and <c>..</c> and refuses
///         anything left climbing above its own root; the result is rebuilt onto the served directory
///         one segment at a time. It is a single gate rather than a check bolted on afterwards, and it
///         is stated that way because a redundant second check reads as defence in depth while
///         actually being unreachable — which is worse than none, since it invites the next reader to
///         believe the first gate is optional.
///     </para>
/// </remarks>
public sealed class ContentServer {
    readonly VirtualFileSystem files;

    /// <summary>The directory being served.</summary>
    public VirtualPath Root { get; }

    /// <summary>How many requests have been answered.</summary>
    public int Served { get; private set; }

    /// <summary>Serves a directory.</summary>
    /// <param name="files">Where the directory lives.</param>
    /// <param name="root">The directory.</param>
    public ContentServer(VirtualFileSystem files, VirtualPath root) {
        ArgumentNullException.ThrowIfNull(files);

        this.files = files;
        Root = root;
    }

    /// <summary>Answers one request.</summary>
    /// <param name="path">The request path, as it arrived — percent-encoding and all.</param>
    /// <param name="range">The <c>Range</c> header, if there was one.</param>
    /// <returns>What to send. The caller disposes it.</returns>
    public ContentReply Serve(string path, string? range = null) {
        ArgumentNullException.ThrowIfNull(path);
        Served++;

        if (!TryResolve(path, out var resolved)) {
            return NotFound();
        }

        if (files.TryGetEntry(resolved, out var entry) && !entry.IsDirectory) {
            return Open(resolved, entry.Length, range);
        }

        // A hash file that is not on disk is computed from the file it names, so a content build
        // directory can be served exactly as the build wrote it.
        return resolved.Value.EndsWith(".hash", StringComparison.Ordinal)
            ? Synthesise(new(resolved.Value[..^".hash".Length]), range)
            : NotFound();
    }

    /// <summary>Whether a request path names something inside the served directory.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="resolved">Where it points.</param>
    /// <returns><see langword="false" /> if it is malformed or reaches outside the root.</returns>
    public bool TryResolve(string path, out VirtualPath resolved) {
        resolved = default;

        // Decoded first: a traversal written as %2e%2e%2f is the same traversal, and a check done
        // before decoding is a check on the wrong string.
        var decoded = Uri.UnescapeDataString(path);

        if (decoded.Contains('\0', StringComparison.Ordinal)) {
            return false;
        }

        if (!VirtualPath.TryCreate(decoded.StartsWith('/') ? decoded : "/" + decoded, out var relative)) {
            return false;
        }

        // Built segment by segment from a path that has already been normalised, which is what makes
        // this safe rather than a check that hopes to catch something. A normalised virtual path
        // holds no "." or ".." — they were resolved, and anything that would have escaped its own
        // root made TryCreate fail above — so combining its segments onto Root cannot leave Root.
        //
        // That makes VirtualPath's "it escapes above the root" rule load-bearing for a security
        // property, which it was not before this tool existed. NothingOutsideTheRootIsReachable
        // asserts it end to end here rather than trusting the layer below to keep its promise.
        var candidate = Root;

        foreach (var segment in relative.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            candidate /= segment;
        }

        resolved = candidate;

        return true;
    }

    static ContentReply NotFound() => new(ContentStatus.NotFound, 0, 0, 0, "text/plain");

    ContentReply Open(VirtualPath path, long total, string? range) {
        var type = ContentTypeOf(path);

        if (!TryParseRange(range, total, out var offset, out var length)) {
            return length < 0
                ? new(ContentStatus.RangeNotSatisfiable, 0, 0, total, type)
                : new(ContentStatus.Ok, 0, total, total, type) { Body = files.OpenRead(path) };
        }

        var body = files.OpenRead(path);
        body.Seek(offset, SeekOrigin.Begin);

        return new(ContentStatus.PartialContent, offset, length, total, type) { Body = body };
    }

    ContentReply Synthesise(VirtualPath named, string? range) {
        if (!files.Exists(named)) {
            return NotFound();
        }

        using var reading = files.OpenRead(named);
        using var buffer = new MemoryStream();
        reading.CopyTo(buffer);

        var text = Encoding.UTF8.GetBytes(ContentHash.Compute(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)).ToString());

        if (!TryParseRange(range, text.Length, out var offset, out var length)) {
            return length < 0
                ? new(ContentStatus.RangeNotSatisfiable, 0, 0, text.Length, "text/plain")
                : new(ContentStatus.Ok, 0, text.Length, text.Length, "text/plain") { Body = new MemoryStream(text) };
        }

        return new(ContentStatus.PartialContent, offset, length, text.Length, "text/plain") {
            Body = new MemoryStream(text, (int)offset, (int)length)
        };
    }

    /// <summary>Reads a <c>Range</c> header.</summary>
    /// <param name="header">The header, or <see langword="null" />.</param>
    /// <param name="total">How long the resource is.</param>
    /// <param name="offset">Where the range starts.</param>
    /// <param name="length">How long it is, or <c>-1</c> when the range cannot be satisfied.</param>
    /// <returns><see langword="false" /> when the whole resource should be sent, or nothing can be.</returns>
    /// <remarks>
    ///     A header that cannot be understood is ignored and the whole resource sent, which is what
    ///     RFC 9110 § 14.2 says to do — refusing would break a client over a header that is optional
    ///     by construction. A range that <i>is</i> understood and starts past the end is a different
    ///     matter and gets a 416, because sending the whole file to a client that asked for byte
    ///     900 000 of an 800 000-byte file would have it write those bytes at the wrong offset.
    /// </remarks>
    public static bool TryParseRange(string? header, long total, out long offset, out long length) {
        offset = 0;
        length = 0;

        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // One range only. Multipart ranges need a multipart body, and no content client sends them.
        var spec = header["bytes=".Length..].Trim();

        if (spec.Contains(',', StringComparison.Ordinal)) {
            return false;
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);

        if (dash < 0) {
            return false;
        }

        var from = spec[..dash].Trim();
        var to = spec[(dash + 1)..].Trim();

        if (from.Length == 0) {
            // bytes=-N: the last N bytes. A suffix longer than the resource is the whole resource,
            // which RFC 9110 says explicitly rather than being an error.
            if (!long.TryParse(to, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) {
                return false;
            }

            offset = Math.Max(0, total - suffix);
            length = total - offset;

            return length > 0;
        }

        if (!long.TryParse(from, CultureInfo.InvariantCulture, out offset) || offset < 0) {
            offset = 0;
            return false;
        }

        if (offset >= total) {
            length = -1;
            return false;
        }

        if (to.Length == 0) {
            length = total - offset;
            return true;
        }

        if (!long.TryParse(to, CultureInfo.InvariantCulture, out var last) || last < offset) {
            offset = 0;
            return false;
        }

        // A last-byte position past the end is clamped rather than refused, per RFC 9110 § 14.1.2.
        length = Math.Min(last, total - 1) - offset + 1;

        return true;
    }

    /// <summary>What a file is, by extension.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its content type.</returns>
    /// <remarks>
    ///     Short on purpose. A content server hands out three things — bundles, catalogs and the hash
    ///     files beside them — and a general-purpose MIME table would be a hundred lines answering
    ///     questions nobody asks it.
    /// </remarks>
    public static string ContentTypeOf(VirtualPath path) =>
        path.Value.EndsWith(".hash", StringComparison.Ordinal) ? "text/plain" : "application/octet-stream";
}
