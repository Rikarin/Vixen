// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;

namespace Vixen.Samples.AddressablesRemote;

/// <summary>Every request, and how many bytes came back.</summary>
/// <param name="Url">What was fetched, without the host.</param>
/// <param name="Bytes">How many bytes of body arrived.</param>
readonly record struct Request(string Url, long Bytes);

/// <summary>
///     An <see cref="IContentTransport" /> that records what went over the wire, and passes
///     everything through.
/// </summary>
/// <remarks>
///     <para>
///         The whole claim this sample makes is "only the changed bundle is downloaded", and a claim
///         about bytes has to be measured in bytes. Counting at the transport is the only place that
///         is true: above it a cache hit and a download look the same, and below it the byte count is
///         the server's opinion rather than the client's.
///     </para>
///     <para>
///         It also demonstrates that <see cref="IContentTransport" /> is a real seam rather than a
///         nod at one — this is a third implementation beside the HTTP one and the fault-injecting
///         one the tests use.
///     </para>
/// </remarks>
sealed class CountingTransport(IContentTransport inner) : IContentTransport, IDisposable {
    readonly List<Request> requests = [];

    /// <summary>What has been requested since the last <see cref="Reset" />.</summary>
    public IReadOnlyList<Request> Requests => requests;

    /// <summary>Total bytes of body received.</summary>
    public long Bytes => requests.Sum(request => request.Bytes);

    /// <summary>Forgets everything, so the next run measures only itself.</summary>
    public void Reset() => requests.Clear();

    /// <inheritdoc />
    public async ValueTask<ContentDownload> GetAsync(
        string url,
        long offset = 0,
        CancellationToken cancellationToken = default
    ) {
        var download = await inner.GetAsync(url, offset, cancellationToken);

        // Counted as it is read rather than from Content-Length, because a body that is abandoned
        // half way — a cancelled download, a resumed one — did not cost what the header claimed.
        return new(new Counting(download.Body, bytes => Record(url, bytes)), download.Offset, download.TotalLength);
    }

    /// <inheritdoc />
    public void Dispose() => (inner as IDisposable)?.Dispose();

    void Record(string url, long bytes) {
        var name = url[(url.LastIndexOf('/') + 1)..];
        requests.Add(new(name, bytes));
    }

    /// <summary>A stream that tallies what is read out of it.</summary>
    sealed class Counting(Stream inner, Action<long> report) : Stream {
        long total;
        bool reported;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => total;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var read = inner.Read(buffer);
            total += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            total += read;
            return read;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            // Reported on close, once. A request that is disposed twice is not two requests, and the
            // caches below do dispose more than once on some paths.
            if (disposing && !reported) {
                reported = true;
                report(total);
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
