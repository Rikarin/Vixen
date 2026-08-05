// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;

namespace Vixen.Engine.Renderer;

/// <summary>A <see cref="ITextureStreamSource" /> over content: byte ranges out of shipped bundles.</summary>
/// <remarks>
///     <para>
///         The join <see cref="AssetTextureSource" /> is the one-shot half of. That one reads a whole
///         <c>Texture</c> artefact and decodes it; this one reads a range of the same artefact and
///         decodes nothing, because a page is bytes the device wants as they are.
///     </para>
///     <para>
///         ⚠ <b>A stream per read, not a stream per texture.</b> Pages of one texture are read
///         concurrently on the thread pool, and a shared stream has a position two of them would
///         fight over — the resulting bug is a page holding another page's bytes, which looks like a
///         corrupt texture rather than like a race. Opening is a catalog lookup and a slice of an
///         already-mapped bundle, so the cost of doing it per page is not the cost it looks like.
///     </para>
/// </remarks>
public sealed class AssetTextureStreamSource : ITextureStreamSource {
    readonly AssetManager assets;
    readonly Dictionary<int, string> addresses = [];

    /// <summary>Creates a source over a content manager.</summary>
    /// <param name="assets">Where the bytes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetTextureStreamSource(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        this.assets = assets;
    }

    /// <summary>Says which address a texture number reads from.</summary>
    /// <param name="texture">The number a caller identifies it by.</param>
    /// <param name="address">The address of its <c>Texture</c> artefact.</param>
    /// <exception cref="ArgumentNullException"><paramref name="address" /> is null.</exception>
    public void Register(int texture, string address) {
        ArgumentNullException.ThrowIfNull(address);
        addresses[texture] = address;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(
        int texture,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellation
    ) {
        if (!addresses.TryGetValue(texture, out var address)) {
            return 0;
        }

        await using var stream = await assets.OpenAsync(address, cancellation).ConfigureAwait(false);

        if (!stream.CanSeek) {
            throw new InvalidOperationException(
                $"'{address}' opened a stream that cannot seek, so a page of it cannot be read. Streamed "
                + "textures have to be built into an uncompressed chunk — a compressed one has no slice of "
                + "the mapped bundle that is the payload. See Vixen.Assets on WriteRaw."
            );
        }

        stream.Seek(offset, SeekOrigin.Begin);

        var read = 0;

        while (read < destination.Length) {
            var got = await stream.ReadAsync(destination[read..], cancellation).ConfigureAwait(false);

            if (got == 0) {
                break;
            }

            read += got;
        }

        return read;
    }
}
