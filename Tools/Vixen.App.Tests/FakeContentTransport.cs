// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;

namespace Vixen.App.Tests;

/// <summary>A content server that serves what it was told to and counts what was asked of it.</summary>
/// <remarks>
///     ⚠ <b>Deliberately much simpler than <c>Vixen.Assets.Tests</c>' fake of the same name</b>, which
///     can drop connections, ignore byte ranges and answer from the wrong offset. Every one of those
///     is a property of <c>BundleCache</c> and is tested where the cache is. What is under test here
///     is one thing the cache cannot answer: whether the <i>host</i> put a transport behind the
///     catalog at all.
/// </remarks>
sealed class FakeContentTransport : IContentTransport, IDisposable {
    readonly Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);

    /// <summary>Every URL that was asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>Whether the transport was disposed, which is how ownership is asserted.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Publishes a resource.</summary>
    public void Serve(string url, byte[] contents) => resources[url] = contents;

    /// <inheritdoc />
    public ValueTask<ContentDownload> GetAsync(
        string url,
        long offset = 0,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Requested.Add(url);

        if (!resources.TryGetValue(url, out var contents)) {
            throw new ContentTransportException(url, "the server answered 404 NotFound");
        }

        return ValueTask.FromResult(
            new ContentDownload(new MemoryStream(contents, (int)offset, contents.Length - (int)offset), offset, contents.Length)
        );
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
