// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Web;

/// <summary>A remote file, read in ranges as it is seeked and read.</summary>
/// <remarks>
///     <para>
///         <b>What makes streaming possible at all on the web.</b> A bundle whose header says where
///         its entries are should cost one request for the header and one for the entry, not one
///         download of eighty megabytes before the first frame — and HTTP has had the mechanism
///         since 1999. <c>Range: bytes=a-b</c>, a chunk cache, and a stream that is genuinely
///         seekable.
///     </para>
///     <para>
///         <b>The chunk is the unit, not the read.</b> A caller that reads four bytes at a time —
///         a binary reader walking a header — would otherwise make one request per field. Reads are
///         served from a resident chunk and a chunk is fetched whole, so a header walk costs one
///         request and the rest is memory.
///     </para>
///     <para>
///         <b><see cref="Read(Span{byte})" /> works only where the bytes are already resident.</b>
///         There is no way to make it work otherwise: it would have to block, and blocking on the
///         browser's one thread stops the fetch it is waiting for from ever completing — the tab
///         deadlocks rather than being slow. So the synchronous read serves what the current chunk
///         holds and throws with a message naming <see cref="ReadAsync(Memory{byte}, CancellationToken)" />
///         when it cannot, which is the behaviour that leads a caller to the fix rather than to a
///         hung page. <see cref="PrefetchAsync" /> is how code that must read synchronously arranges
///         to be able to.
///     </para>
///     <para>
///         A server that ignores <c>Range</c> answers <c>200</c> with the whole body instead of
///         <c>206</c> with the slice, which is legal. The JavaScript takes the slice when that
///         happens, so this class always gets what it asked for and pays only in bandwidth — see
///         <c>fetchRange</c> in <c>vixen-platform.js</c>.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class FetchStream : Stream {
    /// <summary>How much is fetched at a time.</summary>
    /// <remarks>
    ///     A megabyte. Large enough that a sequential read of a bundle makes few requests, small
    ///     enough that seeking to one structure in the middle of a large file does not pull in the
    ///     rest of it.
    /// </remarks>
    public const int ChunkSize = 1024 * 1024;

    readonly string url;
    readonly long length;

    byte[] chunk = [];
    long chunkOffset = -1;
    long position;
    bool disposed;

    internal FetchStream(string url, long length) {
        this.url = url;
        this.length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => !disposed;

    /// <inheritdoc />
    public override bool CanSeek => !disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length {
        get {
            ObjectDisposedException.ThrowIf(disposed, this);
            return length;
        }
    }

    /// <inheritdoc />
    public override long Position {
        get {
            ObjectDisposedException.ThrowIf(disposed, this);
            return position;
        }
        set {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            position = value;
        }
    }

    /// <summary>How many range requests have been made.</summary>
    /// <remarks>
    ///     For a diagnostic overlay and for a test that asserts a bundle read did not turn into a
    ///     download. A sequential pass over a file should show <c>ceil(Length / ChunkSize)</c>.
    /// </remarks>
    public int RequestCount { get; private set; }

    /// <summary>Whether <paramref name="count" /> bytes from <paramref name="offset" /> can be read
    /// synchronously.</summary>
    public bool IsResident(long offset, int count) =>
        chunkOffset >= 0 && offset >= chunkOffset && offset + count <= chunkOffset + chunk.Length;

    /// <summary>Fetches a range so that a synchronous read of it will succeed.</summary>
    /// <param name="offset">Where to start.</param>
    /// <param name="count">How much to make resident. Rounded up to a chunk.</param>
    /// <param name="cancellationToken">Abandons the fetch.</param>
    /// <remarks>
    ///     What code that genuinely has to read synchronously — a parser that is not a state
    ///     machine, and should not become one to read a header — does first. It cannot make more
    ///     than <see cref="ChunkSize" /> resident at once, because one chunk is what is held.
    /// </remarks>
    public async ValueTask PrefetchAsync(long offset, int count, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, ChunkSize);

        if (!IsResident(offset, count) && offset < length) {
            await FetchChunkAsync(offset, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    ///     The bytes are not resident. Use <see cref="ReadAsync(Memory{byte}, CancellationToken)" />,
    ///     or <see cref="PrefetchAsync" /> first.
    /// </exception>
    public override int Read(Span<byte> buffer) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var wanted = Available(buffer.Length);

        if (wanted == 0) {
            return 0;
        }

        if (!IsResident(position, 1)) {
            throw new InvalidOperationException(
                $"'{url}' is a remote file and byte {position} has not been fetched. A synchronous "
                + "read cannot fetch it: blocking this thread stops the fetch from ever completing, "
                + "because the browser runs both on it. Use ReadAsync, or PrefetchAsync first."
            );
        }

        var start = (int)(position - chunkOffset);
        var taken = Math.Min(wanted, chunk.Length - start);

        chunk.AsSpan(start, taken).CopyTo(buffer);
        position += taken;
        return taken;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var wanted = Available(buffer.Length);

        if (wanted == 0) {
            return 0;
        }

        if (!IsResident(position, 1)) {
            await FetchChunkAsync(position, cancellationToken).ConfigureAwait(false);
        }

        var start = (int)(position - chunkOffset);
        var taken = Math.Min(wanted, chunk.Length - start);

        chunk.AsSpan(start, taken).CopyTo(buffer.Span);
        position += taken;
        return taken;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Free, and does not fetch anything. Seeking to a byte in another chunk costs a request the
    ///     next time something is read, which is what makes "seek to the entry, read the entry" the
    ///     cheap operation it should be.
    /// </remarks>
    public override long Seek(long offset, SeekOrigin origin) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var target = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));

        position = target;
        return position;
    }

    /// <inheritdoc />
    /// <remarks>Nothing to flush; a read-only stream has no pending writes.</remarks>
    public override void Flush() { }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Content served over HTTP is read-only.</exception>
    public override void SetLength(long value) =>
        throw new NotSupportedException("A fetched file is read-only.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Content served over HTTP is read-only.</exception>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A fetched file is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (!disposed) {
            disposed = true;
            chunk = [];
            chunkOffset = -1;
        }

        base.Dispose(disposing);
    }

    /// <summary>How much of a requested read is actually within the file.</summary>
    int Available(int wanted) =>
        position >= length ? 0 : (int)Math.Min(wanted, length - position);

    async ValueTask FetchChunkAsync(long offset, CancellationToken cancellationToken) {
        // Aligned to a chunk boundary rather than starting at the read. Two reads either side of a
        // boundary would otherwise fetch two overlapping chunks and keep neither useful.
        var start = offset / ChunkSize * ChunkSize;
        var size = (int)Math.Min(ChunkSize, length - start);

        var handle = await WebInterop
            .FetchRange(url, start, size)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        chunk = WebBuffer.Take(handle);
        chunkOffset = start;
        RequestCount++;

        if (chunk.Length < size) {
            // A short answer means the manifest and the server disagree about the file's length,
            // which is a stale deployment rather than a transient failure. Said plainly, because
            // the symptom otherwise is a truncated asset far from here.
            throw new IOException(
                $"'{url}' returned {chunk.Length} bytes for a {size}-byte range at {start}. The "
                + "content manifest says the file is longer than the server is serving."
            );
        }
    }
}
